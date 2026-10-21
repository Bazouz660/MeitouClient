using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Meitou.Engine;
using Meitou.Engine.Time;
using Meitou.Net.Commands;
using Meitou.Net.Events;
using Meitou.Net.Replication;
using Meitou.Net.Transport;
using Meitou.Simulation;

namespace Meitou.Net.Session;

/// <summary>
/// The host of a co-op session (docs/net.md, "Host"): it runs the world, admits clients, gates their commands, owns the clock, and sends each client its
/// own snapshots, views and events. Everything happens on the caller's frame thread: <see cref="BeginFrame"/> immediately before <c>session.Advance</c>
/// (the transport's events arrive only there), <see cref="EndFrame"/> after it. No game types: the game is reached through <see cref="IHostGame"/>.
/// </summary>
public sealed class NetHost : IClockAuthority, IDisposable
{
    /// <summary>Commands per second a client may send, and the burst it may save up (excess is dropped with at most one notice a second).</summary>
    public const double CommandRate = 200, CommandBurst = 400;
    /// <summary>Chat lines a client may send per <see cref="ChatWindowSeconds"/>.</summary>
    public const double ChatLines = 5, ChatWindowSeconds = 5;
    /// <summary>Refusals (<c>CommandResult</c>) sent per second and peer at most.</summary>
    public const int RefusalsPerSecond = 2;
    /// <summary>Seconds between unchanged <c>ClockState</c> and roster broadcasts, and between weather resends.</summary>
    public const double ClockEverySeconds = 2, RosterEverySeconds = 2, WeatherEverySeconds = 60;
    /// <summary>Most events in one <c>EventBatch</c> message the host writes (a batch whose bytes exceed the reliable limit is split further).</summary>
    public const int EventsPerMessage = 256;
    /// <summary>The highest player number (numbers are bytes on the UI side).</summary>
    public const int MaxNumber = 255;

    public const string HostOnlyClock = "Only the host can change the game speed.", ThrottleText = "Too many commands: some were dropped.",
        BadData = "Bad data from the client.", HelloLate = "Took too long to join.", LoadLate = "Took too long to load.", SignedInElsewhere = "Signed in from another place.",
        NoSlotText = "No free player slot.", BannedText = "You are banned from this game.", ClosedText = "The host ended the game.";

    sealed class Listener(NetHost host) : INetListener
    {
        public byte[]? Request(string address, ReadOnlySpan<byte> connectData) => host.OnRequest(address, connectData);
        public void Connected(NetConnectionId peer) => host.OnConnected(peer);
        public void Received(NetConnectionId peer, NetChannel channel, ReadOnlySpan<byte> data) => host.OnReceived(peer, channel, data);
        public void Dropped(NetConnectionId peer, NetDropReason reason, ReadOnlySpan<byte> data) => host.OnDropped(peer, reason);
    }

    /// <summary>A roster line the host keeps: connected (<see cref="Peer"/> set) or away.</summary>
    sealed class Member(int number, Guid guid, string name, int colour)
    {
        public int Number = number;
        public readonly Guid Guid = guid;
        public string Name = name;
        public int Colour = colour;
        public HostPeer? Peer;
    }

    readonly WorldSession session;
    readonly INetServerTransport transport;
    readonly NetHostOptions options;
    readonly NetIdentity self;
    readonly IHostGame game;
    readonly ReplicaCatalog catalog;
    readonly INetClock clock;
    readonly Listener listener;
    readonly ulong animationHash;
    readonly LookRegistry looks;
    readonly SnapshotEncoderOptions encoder;
    readonly HostEventTap tap = new();
    readonly Dictionary<NetConnectionId, HostPeer> peers = [];
    readonly SortedDictionary<int, Member> members = [];
    readonly HashSet<Guid> bans = [];
    readonly Dictionary<NetConnectionId, (NoticeKind Kind, string Detail)> ending = [];
    readonly NetWriter writer = new(1024);
    readonly List<NetEvent> scratch = [];
    readonly List<HostPeer> scratchPeers = [];
    bool started, stopped;
    ushort epoch = 1;
    NetRules rules;
    SessionRules? sentSessionRules;
    // The clock: the anchor of the last ClockState, whether a change waits, the busy flag last sent.
    long anchorTick;
    double anchorHours;
    bool clockDirty, lastBusy;
    ClockChange pendingChange = new(-1, ClockChangeKind.None, 0);
    long lastClockUs = long.MinValue / 2, lastRosterUs = long.MinValue / 2, lastWeatherUs = long.MinValue / 2, lastStatsUs;
    int? weatherRevision;
    bool rosterDirty;
    IReadOnlyList<RosterEntry> roster = [];

    /// <summary>
    /// A host for <paramref name="session"/> (a local session with a running game) over <paramref name="transport"/>, which it owns and disposes.
    /// <paramref name="catalog"/>'s appearances should be a <see cref="RecordingAppearances"/> so looks made before hosting have recipes.
    /// </summary>
    public NetHost(WorldSession session, INetServerTransport transport, NetHostOptions options, NetIdentity self, IHostGame game, ReplicaCatalog catalog, INetClock? clock = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(self);
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(catalog);
        this.session = session;
        this.transport = transport;
        this.options = options with
        {
            MaxPlayers = Math.Clamp(options.MaxPlayers, 2, NetProtocol.MaxPlayers),
            SendRate = Math.Clamp(options.SendRate, 1, 60),
            PausedSendRate = Math.Clamp(options.PausedSendRate, 1, 60),
            MaxBytesPerSecond = Math.Max(options.MaxBytesPerSecond, 4096),
            Password = string.IsNullOrEmpty(options.Password) ? null : options.Password,
        };
        this.self = self;
        this.game = game;
        this.catalog = catalog;
        this.clock = clock ?? new StopwatchNetClock();
        listener = new Listener(this);
        animationHash = Handshake.AnimationHash(catalog.Animations);
        looks = new LookRegistry(catalog.Appearances as RecordingAppearances ?? new RecordingAppearances(catalog.Appearances));
        encoder = new SnapshotEncoderOptions { SendHz = this.options.SendRate, BytesPerSecond = this.options.MaxBytesPerSecond };
        rules = MakeRules();
    }

    /// <summary>The lobby rules clients see (the bound port once started).</summary>
    public NetRules Rules => rules;

    /// <summary>The epoch of the current world: it grows by one with every world swap.</summary>
    public ushort Epoch => epoch;

    /// <summary>The player list as last built: the host first, then every remote player by number; players who left stay as <see cref="NetPlayerState.Away"/>.</summary>
    public IReadOnlyList<RosterEntry> Roster => roster;

    /// <summary>The players with their link figures, the host first.</summary>
    public IReadOnlyList<NetPlayerInfo> Players
    {
        get
        {
            var list = new List<NetPlayerInfo>(members.Count + 1) { new(0, self.PlayerId, self.Name, NetPlayerState.Host, 0, default) };
            foreach (var m in members.Values) list.Add(new NetPlayerInfo(m.Number, m.Guid, m.Name, StateOf(m), m.Colour, m.Peer is { } p ? StatsOf(p) : default));
            return list;
        }
    }

    /// <summary>Where diagnostics go (unencodable data, dropped peers); null: nowhere.</summary>
    public Action<string>? Log { get; set; }

    /// <summary>A player joined, left, timed out, was kicked or banned (also sent to every client).</summary>
    public event Action<NoticeNet>? Notice;
    /// <summary>A chat line, the host's own included, as every client gets it.</summary>
    public event Action<ChatNet>? Chat;
    /// <summary>Every clock change, the host's own included, with the name of the player who made it.</summary>
    public event Action<ClockChange, string>? ClockChanged;

    /// <summary>
    /// Starts listening (a port that cannot be bound throws <see cref="System.Net.Sockets.SocketException"/> and nothing changes), names the host's session
    /// after <see cref="NetIdentity"/>, and takes the session's clock: <see cref="WorldSession.ClockAuthority"/> = this, no modal pause.
    /// </summary>
    public void Start()
    {
        if (started) throw new InvalidOperationException("the host is already started");
        if (session.Mode != SessionMode.Local) throw new InvalidOperationException("only a local session can be hosted");
        transport.Listen(options.Port);
        started = true;
        Claim(session.World);
        session.ClockAuthority = this;
        session.Simulation.RequestedPause = false;
        session.WorldReplaced += OnWorldReplaced;
        rules = MakeRules();
        sentSessionRules = game.Rules;
        Anchor();
        rosterDirty = true;
    }

    /// <summary>Immediately before <c>session.Advance</c>: the transport's events (joins, commands, clock and chat requests), timeouts, then the event tap hooks the world.</summary>
    public void BeginFrame()
    {
        if (!started || stopped) return;
        transport.Poll(listener);
        long now = clock.NowMicroseconds;
        foreach (var peer in Snapshot(peers.Values))
        {
            if (peer.State == HostPeerState.Challenged && now - peer.SinceUs > options.HelloTimeout.Ticks / TimeSpan.TicksPerMicrosecond)
                DropPeer(peer, RejectCode.Timeout, HelloLate, NoticeKind.TimedOut);
            else if (peer.State == HostPeerState.Loading && !peer.ResetOwed && now - peer.SinceUs > options.LoadTimeout.Ticks / TimeSpan.TicksPerMicrosecond)
                DropPeer(peer, RejectCode.LoadTimeout, LoadLate, NoticeKind.TimedOut);
        }
        tap.BeginFrame(session.World);
    }

    /// <summary>
    /// After <c>session.Advance</c> and the game's news: the tap's events, owed world resets, the clock, rules and weather, then (unless
    /// <paramref name="hostBusy"/>, the host's loading screen, when only Control traffic goes out) events and snapshots, and the roster.
    /// </summary>
    public void EndFrame(bool hostBusy)
    {
        if (!started || stopped) return;
        var world = session.World;
        tap.EndFrame(world);
        long now = clock.NowMicroseconds;

        foreach (var peer in peers.Values)
            if (peer.ResetOwed && !ending.ContainsKey(peer.Id)) SendWorldReset(peer, world, now, hostBusy);

        SendClock(world, hostBusy, now);
        SendRules();
        SendWeather(now);

        if (!hostBusy)
        {
            foreach (var peer in peers.Values)
                if (peer.InGame) SendEvents(peer, world);
            foreach (var peer in peers.Values)
                if (peer.State == HostPeerState.Playing && now >= peer.NextSendUs) SendSnapshot(peer, world, now);
        }
        tap.ClearBatch();

        if (now - lastStatsUs >= 1_000_000) UpdateStats(now);
        if (rosterDirty || now - lastRosterUs >= RosterEverySeconds * 1e6) SendRoster(world, now);
    }

    /// <summary>Sends a non-spatial event to player <paramref name="player"/>, or to every connected remote player with -1 (the host's own news stays local).</summary>
    public void Send(int player, NetEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (EventFilter.IsSpatial(e)) throw new ArgumentException("spatial events reach clients through the world's queues", nameof(e));
        foreach (var peer in peers.Values)
            if (peer.InGame && (player < 0 || peer.Player == player)) peer.Pending.Add(e);
    }

    /// <summary>The host's own chat line, as player 0.</summary>
    public void SendChat(string text) => PostChat(0, self.Name, text);

    /// <summary>Drops player <paramref name="player"/> with <c>Kicked by the host: {reason}</c>; they may come back.</summary>
    public void Kick(int player, string reason)
    {
        if (PeerOf(player) is { } peer) DropPeer(peer, RejectCode.Kicked, $"Kicked by the host: {reason}", NoticeKind.Kicked, reason);
    }

    /// <summary>Bans player <paramref name="player"/>'s id for this host's lifetime and drops them when connected.</summary>
    public void Ban(int player, string reason)
    {
        if (!members.TryGetValue(player, out var m)) return;
        bans.Add(m.Guid);
        if (m.Peer is { } peer) DropPeer(peer, RejectCode.Banned, BannedText, NoticeKind.Banned, reason);
        else Announce(new NoticeNet(NoticeKind.Banned, m.Number, m.Name, reason ?? ""));
    }

    /// <summary>The host's own clock input, as player 0.</summary>
    void IClockAuthority.Request(ClockRequest request) => ApplyClock(0, request);

    /// <summary>One line of link figures for the debug overlay and the log.</summary>
    public string StatsLine()
    {
        int players = 0, rtt = 0, outBytes = 0, inBytes = 0;
        foreach (var peer in peers.Values)
        {
            if (!peer.InGame) continue;
            players++;
            rtt = Math.Max(rtt, transport.Stats(peer.Id).RoundTripMs);
            outBytes += peer.OutPerSecond;
            inBytes += peer.InPerSecond;
        }
        return string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"net host :{transport.Port} epoch {epoch}: {players + 1}/{options.MaxPlayers} players, out {outBytes / 1024.0:0.0} KB/s, in {inBytes / 1024.0:0.0} KB/s, worst rtt {rtt} ms");
    }

    /// <summary>Drops every client with <see cref="RejectCode.HostClosed"/> and <paramref name="reason"/> and gives the clock back to the session (single-player).</summary>
    public void Stop(string reason)
    {
        if (stopped) return;
        stopped = true;
        foreach (var peer in Snapshot(peers.Values)) DropPeer(peer, RejectCode.HostClosed, reason, NoticeKind.Left);
        if (started)
        {
            transport.Poll(listener);
            session.WorldReplaced -= OnWorldReplaced;
        }
        if (tap.Hooked) tap.EndFrame(session.World);
        if (ReferenceEquals(session.ClockAuthority, this)) session.ClockAuthority = null;
    }

    /// <summary><see cref="Stop"/> with <c>The host ended the game.</c>, then the transport is disposed.</summary>
    public void Dispose()
    {
        Stop(ClosedText);
        transport.Dispose();
    }

    // ---- Transport events ----

    byte[]? OnRequest(string address, ReadOnlySpan<byte> connectData)
    {
        int connected = 0;
        foreach (var peer in peers.Values)
            if (!ending.ContainsKey(peer.Id)) connected++;
        var reject = Handshake.CheckRequest(connectData, connected, options.MaxPlayers, addressBanned: false);
        return reject is null ? null : Encode(reject);
    }

    void OnConnected(NetConnectionId id)
    {
        if (stopped) return;
        var peer = new HostPeer(id, transport.Address(id), clock.NowMicroseconds) { Nonce = RandomNumberGenerator.GetBytes(Handshake.NonceLength) };
        peers[id] = peer;
        writer.Reset();
        ProtocolCodec.Write(writer, new Challenge(peer.Nonce, options.HostName, options.Password is not null));
        transport.Send(id, NetChannel.Control, writer.Written);
    }

    void OnReceived(NetConnectionId id, NetChannel channel, ReadOnlySpan<byte> data)
    {
        if (!peers.TryGetValue(id, out var peer) || ending.ContainsKey(id)) return;
        try
        {
            var r = new NetReader(data);
            switch (channel)
            {
                case NetChannel.Control:
                    Control(peer, ref r);
                    break;
                case NetChannel.Snapshots:
                    if (peer.Replicator is null) throw new NetFormatException("a client frame before the welcome");
                    peer.Replicator.OnClientFrame(ClientFrameCodec.Read(ref r));
                    break;
                default:
                    throw new NetFormatException($"clients do not send on {channel}");
            }
        }
        catch (NetFormatException e)
        {
            Log?.Invoke($"net: player {peer.Player} ({peer.Address}) sent bad data: {e.Message}");
            DropPeer(peer, RejectCode.ProtocolError, BadData, NoticeKind.Left);
        }
    }

    void Control(HostPeer peer, ref NetReader r)
    {
        var type = ProtocolCodec.ReadType(ref r);
        if (peer.State == HostPeerState.Challenged && type != MessageType.Hello) throw new NetFormatException($"{type} before the hello");
        switch (type)
        {
            case MessageType.Hello:
                if (peer.State != HostPeerState.Challenged) throw new NetFormatException("a second hello");
                OnHello(peer, ProtocolCodec.ReadHello(ref r));
                break;
            case MessageType.Ready:
                OnReady(peer, ProtocolCodec.ReadReady(ref r));
                break;
            case MessageType.CommandBatch:
                OnCommands(peer, CommandBatchCodec.Read(ref r, peer.Strings, session.World.Tick, peer.Player, game.Gate.Cheats));
                break;
            case MessageType.ClockRequest:
                ApplyClock(peer.Player, ProtocolCodec.ReadClockRequest(ref r));
                break;
            case MessageType.ChatRequest:
                OnChat(peer, ProtocolCodec.ReadChatRequest(ref r).Text);
                break;
            default:
                throw new NetFormatException($"clients do not send {type}");
        }
    }

    void OnDropped(NetConnectionId id, NetDropReason reason)
    {
        if (!peers.Remove(id, out var peer)) return;
        (NoticeKind Kind, string Detail) kind = ending.Remove(id, out var end) ? end : (reason == NetDropReason.Timeout ? NoticeKind.TimedOut : NoticeKind.Left, "");
        if (!peer.InGame || !members.TryGetValue(peer.Player, out var m) || m.Peer != peer) return;
        m.Peer = null;
        rosterDirty = true;
        HostWorldLink.PlayerLeft(session.World, m.Number);
        if (!stopped) Announce(new NoticeNet(kind.Kind, m.Number, m.Name, kind.Detail));
    }

    // ---- Handshake ----

    void OnHello(HostPeer peer, Hello hello)
    {
        var reject = Handshake.CheckHello(hello, self, animationHash, options.Password, peer.Nonce, options.AllowBuildMismatch, bans.Contains(hello.Player));
        if (reject is not null)
        {
            Refuse(peer, reject);
            return;
        }
        int others = 0;
        foreach (var p in peers.Values)
            if (p != peer && p.InGame && !ending.ContainsKey(p.Id)) others++;
        if (others + 1 >= options.MaxPlayers)
        {
            Refuse(peer, new Reject(RejectCode.Full, $"The game is full ({options.MaxPlayers} players)."));
            return;
        }
        var world = session.World;
        int number = NumberFor(world, hello.Player);
        if (number is <= 0 or > MaxNumber)
        {
            Refuse(peer, number == 0 ? new Reject(RejectCode.Duplicate, "Your player id is already in this game.") : new Reject(RejectCode.NoSlot, NoSlotText));
            return;
        }
        if (members.TryGetValue(number, out var known) && known.Peer is { } older) DropPeer(older, RejectCode.Duplicate, SignedInElsewhere, NoticeKind.Left);

        string name = UniqueName(Handshake.SanitizeName(hello.Name, number), hello.Player);
        world.Player.Join(number, hello.Player, name);
        var taken = new HashSet<int> { 0 };
        foreach (var m in members.Values)
            if (m.Guid != hello.Player && m.Colour >= 0) taken.Add(m.Colour);
        int colour = known is not null && known.Guid == hello.Player && known.Colour >= 0 && !taken.Contains(known.Colour)
            ? known.Colour : PlayerColourRule.Assign(taken, number);
        var member = new Member(number, hello.Player, name, colour) { Peer = peer };
        members[number] = member;

        peer.Player = number;
        peer.Guid = hello.Player;
        peer.Name = name;
        peer.Colour = colour;
        peer.Sink = new PeerSink(transport, peer.Id);
        peer.Replicator = new ClientReplicator(number, encoder, looks, catalog.Animations) { Log = Log };
        peer.Replicator.Reset(epoch);
        peer.State = HostPeerState.Loading;
        rosterDirty = true;

        long now = clock.NowMicroseconds;
        writer.Reset();
        ProtocolCodec.Write(writer, new Welcome(number, rules, BuildRoster(world)));
        transport.Send(peer.Id, NetChannel.Control, writer.Written);
        SendWorldReset(peer, world, now, lastBusy);
        Announce(new NoticeNet(NoticeKind.Joined, number, name, ""));
    }

    void OnReady(HostPeer peer, Ready ready)
    {
        if (peer.State != HostPeerState.Loading || peer.ResetOwed || ready.Epoch != epoch) return;
        var world = session.World;
        peer.State = HostPeerState.Playing;
        peer.Replicator!.Reset(epoch, peer.Player);
        peer.NextSendUs = clock.NowMicroseconds;
        HostWorldLink.EnqueueJoin(world, peer.Player, HostWorldLink.JoinPoint(world, session.Camera.Current.Target));
        rosterDirty = true;
    }

    void Refuse(HostPeer peer, Reject reject)
    {
        ending[peer.Id] = (NoticeKind.Left, "");
        transport.Drop(peer.Id, Encode(reject));
    }

    /// <summary>Ends a connection with a reason the client shows; an in-game player becomes Away with <paramref name="kind"/> announced when the drop arrives.</summary>
    void DropPeer(HostPeer peer, RejectCode code, string reason, NoticeKind kind, string detail = "")
    {
        if (ending.ContainsKey(peer.Id)) return;
        ending[peer.Id] = (kind, detail ?? "");
        transport.Drop(peer.Id, Encode(new Reject(code, reason)));
    }

    static byte[] Encode(Reject reject)
    {
        var w = new NetWriter(64);
        ProtocolCodec.Write(w, reject);
        return w.Written.ToArray();
    }

    int NumberFor(World world, Guid guid)
    {
        if (world.Player.FindByGuid(guid) is { } known) return known.Number;
        int next = world.Player.NextNumber();
        // An away player keeps their number in the roster even when the world has no session for it yet.
        foreach (var m in members.Values)
            if (m.Guid == guid) return world.Player.Find(m.Number) is null ? m.Number : next;
        while (members.ContainsKey(next)) next++;
        return next;
    }

    /// <summary><paramref name="name"/>, or with " (2)", " (3)"... when the host or another player already has it.</summary>
    string UniqueName(string name, Guid guid)
    {
        bool Taken(string candidate)
        {
            if (string.Equals(candidate, self.Name, StringComparison.OrdinalIgnoreCase)) return true;
            foreach (var m in members.Values)
                if (m.Guid != guid && string.Equals(m.Name, candidate, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
        if (!Taken(name)) return name;
        for (int i = 2; ; i++)
        {
            string suffix = $" ({i})";
            string stem = name.Length + suffix.Length > NetProtocol.MaxNameLength ? name[..Math.Max(0, NetProtocol.MaxNameLength - suffix.Length)].TrimEnd() : name;
            if (stem.Length > 0 && char.IsHighSurrogate(stem[^1])) stem = stem[..^1];
            if (!Taken(stem + suffix)) return stem + suffix;
        }
    }

    // ---- Commands, chat, clock ----

    void OnCommands(HostPeer peer, CommandBatch batch)
    {
        // The batch is always read (it may define strings later batches use); only a playing peer's batch of this epoch is applied.
        if (peer.State != HostPeerState.Playing || batch.Epoch != epoch) return;
        var world = session.World;
        long now = clock.NowMicroseconds;
        bool throttled = false;
        for (int i = 0; i < batch.Commands.Count; i++)
        {
            if (!HostPeer.Take(ref peer.CommandTokens, ref peer.CommandRefillUs, now, CommandRate, CommandBurst))
            {
                throttled = true;
                continue;
            }
            var result = CommandGate.Admit(world, peer.Player, batch.Commands[i], game.Gate);
            if (result.Command is { } admitted) world.Commands.Enqueue(admitted with { Tick = world.Tick, Player = peer.Player });
            else Refusal(peer, batch.FirstSeq + (uint)i, result.Refusal ?? CommandGate.NotAllowed, now);
        }
        if (throttled && now - peer.LastThrottleNoticeUs >= 1_000_000)
        {
            peer.LastThrottleNoticeUs = now;
            peer.Pending.Add(new MessageNet(ThrottleText));
        }
    }

    void Refusal(HostPeer peer, uint seq, string reason, long now)
    {
        if (now - peer.LastRefusalUs >= 1_000_000)
        {
            peer.LastRefusalUs = now;
            peer.RefusalsThisSecond = 0;
        }
        if (peer.RefusalsThisSecond >= RefusalsPerSecond) return;
        peer.RefusalsThisSecond++;
        writer.Reset();
        writer.WriteByte((byte)MessageType.CommandResult);
        CommandBatchCodec.Write(writer, new CommandResult(seq, reason));
        transport.Send(peer.Id, NetChannel.Control, writer.Written);
    }

    void OnChat(HostPeer peer, string text)
    {
        if (!peer.InGame) return;
        if (!HostPeer.Take(ref peer.ChatTokens, ref peer.ChatRefillUs, clock.NowMicroseconds, ChatLines / ChatWindowSeconds, ChatLines)) return;
        PostChat(peer.Player, peer.Name, text);
    }

    void PostChat(int from, string name, string text)
    {
        string line = CleanChat(text);
        if (line.Length == 0) return;
        var chat = new ChatNet(from, name, line);
        foreach (var peer in peers.Values)
            if (peer.InGame) peer.Pending.Add(chat);
        Chat?.Invoke(chat);
    }

    /// <summary>Control characters removed, trimmed, at most <see cref="NetProtocol.MaxChatLength"/> characters (never splitting a surrogate pair).</summary>
    internal static string CleanChat(string? text)
    {
        var sb = new StringBuilder(Math.Min(text?.Length ?? 0, NetProtocol.MaxChatLength + 1));
        foreach (char c in text ?? "")
            if (!char.IsControl(c)) sb.Append(c);
        string line = sb.ToString().Trim();
        if (line.Length > NetProtocol.MaxChatLength)
        {
            int cut = NetProtocol.MaxChatLength;
            if (char.IsHighSurrogate(line[cut - 1])) cut--;
            line = line[..cut].TrimEnd();
        }
        return line;
    }

    void ApplyClock(int player, ClockRequest request)
    {
        if (stopped) return;
        if (options.ClockPolicy == ClockPolicy.HostOnly && player != 0)
        {
            if (PeerOf(player) is { } peer) peer.Pending.Add(new MessageNet(HostOnlyClock));
            return;
        }
        var sim = session.Simulation;
        double speed = sim.Speed, last = sim.LastNonZeroSpeed;
        if (request.Kind == ClockRequestKind.SetSpeed && !(request.Speed == 0 || Array.IndexOf(SimulationClock.Speeds, request.Speed) >= 0)) return;
        request.ApplyTo(sim);
        if (sim.Speed == speed && sim.LastNonZeroSpeed == last) return;
        var kind = sim.Speed == 0 ? ClockChangeKind.Paused : speed == 0 ? ClockChangeKind.Resumed : ClockChangeKind.Speed;
        var change = new ClockChange(player, kind, sim.Speed == 0 ? sim.LastNonZeroSpeed : sim.Speed);
        pendingChange = change;
        clockDirty = true;
        ClockChanged?.Invoke(change, NameOf(player));
    }

    // ---- World swap ----

    void OnWorldReplaced(World next)
    {
        if (stopped) return;
        epoch++;
        Claim(next);
        long now = clock.NowMicroseconds;
        // Connected players first take their numbers in the new world (its roster, else the next free one); away players keep theirs only where it is free.
        var away = members.Values.Where(m => m.Peer is null).ToList();
        var connected = members.Values.Where(m => m.Peer is not null).ToList();
        members.Clear();
        foreach (var m in connected)
        {
            int number = next.Player.FindByGuid(m.Guid) is { Number: > 0 and <= MaxNumber } s ? s.Number : next.Player.NextNumber();
            if (members.ContainsKey(number) || number > MaxNumber)
            {
                DropPeer(m.Peer!, RejectCode.NoSlot, NoSlotText, NoticeKind.Left);
                continue;
            }
            next.Player.Join(number, m.Guid, m.Name);
            m.Number = number;
            members[number] = m;
            var peer = m.Peer!;
            peer.Player = number;
            peer.State = HostPeerState.Loading;
            peer.ResetOwed = true;
            peer.SinceUs = now;
            peer.Replicator!.Reset(epoch, number);
        }
        foreach (var m in away)
        {
            int number = next.Player.FindByGuid(m.Guid)?.Number ?? m.Number;
            if (number <= 0 || members.ContainsKey(number) || (next.Player.Find(number) is { } s && s.Guid != m.Guid && s.Guid != Guid.Empty)) continue;
            m.Number = number;
            members[number] = m;
        }
        // Commands of the old world's peers are stale; the clock is anchored again once the game has set it (the next EndFrame).
        pendingChange = new ClockChange(-1, ClockChangeKind.None, 0);
        anchorTick = next.Tick;
        anchorHours = session.Clock.TotalHours;
        clockDirty = true;
        rosterDirty = true;
    }

    /// <summary>The host's own roster entry on <paramref name="world"/> (it is player 0 there).</summary>
    void Claim(World world)
    {
        world.Player.Local.Guid = self.PlayerId;
        world.Player.Local.Name = self.Name;
    }

    void SendWorldReset(HostPeer peer, World world, long now, bool busy)
    {
        peer.ResetOwed = false;
        peer.SinceUs = now;
        var focus = HostWorldLink.FocusOf(world, peer.Player, session.Camera.Current.Target);
        writer.Reset();
        ProtocolCodec.Write(writer, new WorldReset(epoch, peer.Player, world.Seed, focus, MakeClock(world, busy, pendingChange), game.Rules, game.Weather()));
        transport.Send(peer.Id, NetChannel.Control, writer.Written);
    }

    // ---- Per-frame sends ----

    ClockState MakeClock(World world, bool busy, ClockChange change)
    {
        var sim = session.Simulation;
        return new ClockState(epoch, sim.Speed, sim.LastNonZeroSpeed, busy, sim.Speed == 0 ? 0 : sim.AchievedSpeed, world.Tick, session.Clock.TotalHours, change);
    }

    void Anchor()
    {
        anchorTick = session.World.Tick;
        anchorHours = session.Clock.TotalHours;
    }

    void SendClock(World world, bool busy, long now)
    {
        double expected = anchorHours + (world.Tick - anchorTick) * session.Simulation.TickSeconds / GameClock.SecondsPerGameHour;
        bool timeSet = Math.Abs(session.Clock.TotalHours - expected) > 1e-6;
        if (timeSet && pendingChange.Kind == ClockChangeKind.None) pendingChange = new ClockChange(-1, ClockChangeKind.TimeSet, session.Simulation.Speed);
        if (!clockDirty && !timeSet && busy == lastBusy && now - lastClockUs < ClockEverySeconds * 1e6) return;
        var state = MakeClock(world, busy, pendingChange);
        writer.Reset();
        ProtocolCodec.Write(writer, state);
        foreach (var peer in peers.Values)
            if (peer.InGame && !peer.ResetOwed && !ending.ContainsKey(peer.Id)) transport.Send(peer.Id, NetChannel.Control, writer.Written);
        lastBusy = busy;
        lastClockUs = now;
        clockDirty = false;
        pendingChange = new ClockChange(-1, ClockChangeKind.None, 0);
        Anchor();
    }

    void SendRules()
    {
        var next = MakeRules();
        var sessionRules = game.Rules;
        if (next == rules && sessionRules == sentSessionRules) return;
        rules = next;
        sentSessionRules = sessionRules;
        writer.Reset();
        ProtocolCodec.Write(writer, new RulesUpdate(rules, sessionRules));
        BroadcastControl();
    }

    void SendWeather(long now)
    {
        if (!HasInGamePeer()) return;
        bool due = now - lastWeatherUs >= WeatherEverySeconds * 1e6;
        var weather = game.Weather();
        if (weather is null || (!due && weather.Revision == weatherRevision)) return;
        weatherRevision = weather.Revision;
        lastWeatherUs = now;
        writer.Reset();
        ProtocolCodec.Write(writer, weather);
        BroadcastControl();
    }

    void BroadcastControl()
    {
        foreach (var peer in peers.Values)
            if (peer.InGame && !peer.ResetOwed && !ending.ContainsKey(peer.Id)) transport.Send(peer.Id, NetChannel.Control, writer.Written);
    }

    bool HasInGamePeer()
    {
        foreach (var peer in peers.Values)
            if (peer.InGame) return true;
        return false;
    }

    NetRules MakeRules() => new(options.SendRate, options.ClockPolicy, options.MaxPlayers, game.Gate.Cheats, options.Password is not null,
        started ? transport.Port : options.Port);

    void SendEvents(HostPeer peer, World world)
    {
        scratch.Clear();
        if (peer.State == HostPeerState.Playing && !peer.ResetOwed && peer.Replicator is { } replicator)
            foreach (var e in tap.Batch)
                if (EventFilter.Wants(e, replicator.View, replicator.Interest, options.EventRadius)) scratch.Add(e);
        scratch.AddRange(peer.Pending);
        peer.Pending.Clear();
        if (scratch.Count == 0 || ending.ContainsKey(peer.Id)) return;
        for (int start = 0; start < scratch.Count;)
        {
            int count = Math.Min(EventsPerMessage, scratch.Count - start);
            while (true)
            {
                writer.Reset();
                EventCodec.Write(writer, new EventBatch(epoch, world.Tick, scratch.GetRange(start, count)));
                if (writer.Length <= NetLimits.MaxReliableMessage || count == 1) break;
                count = Math.Max(1, count / 2);
            }
            if (writer.Length <= NetLimits.MaxReliableMessage) transport.Send(peer.Id, NetChannel.Events, writer.Written);
            else Log?.Invoke($"net: an event of {writer.Length} bytes was not sent");
            start += count;
        }
    }

    void SendSnapshot(HostPeer peer, World world, long now)
    {
        if (peer.ResetOwed || ending.ContainsKey(peer.Id)) return;
        var view = world.PublishFor(peer.Player);
        peer.Replicator!.Send(view, TimeSpan.FromMicroseconds(now), peer.Sink!);
        long interval = (long)(1e6 / (session.Simulation.IsPaused ? options.PausedSendRate : options.SendRate));
        peer.NextSendUs += interval;
        if (peer.NextSendUs <= now) peer.NextSendUs = now + interval;
    }

    void SendRoster(World world, long now)
    {
        roster = BuildRoster(world);
        rosterDirty = false;
        lastRosterUs = now;
        writer.Reset();
        ProtocolCodec.Write(writer, roster);
        foreach (var peer in peers.Values)
            if (peer.InGame && !ending.ContainsKey(peer.Id)) transport.Send(peer.Id, NetChannel.Control, writer.Written);
    }

    List<RosterEntry> BuildRoster(World world)
    {
        var list = new List<RosterEntry>(members.Count + 1) { new(0, self.Name, NetPlayerState.Host, 0, 0, HostWorldLink.SquadsOf(world, 0)) };
        foreach (var m in members.Values)
            list.Add(new RosterEntry(m.Number, m.Name, StateOf(m), m.Peer is { } p ? transport.Stats(p.Id).RoundTripMs : 0, Math.Max(0, m.Colour), HostWorldLink.SquadsOf(world, m.Number)));
        return list;
    }

    void UpdateStats(long now)
    {
        foreach (var peer in peers.Values)
        {
            var stats = transport.Stats(peer.Id);
            double seconds = Math.Max(1e-3, (now - peer.StatsAtUs) / 1e6);
            peer.OutPerSecond = (int)((stats.BytesSent - peer.BytesSentAt) / seconds);
            peer.InPerSecond = (int)((stats.BytesReceived - peer.BytesReceivedAt) / seconds);
            peer.BytesSentAt = stats.BytesSent;
            peer.BytesReceivedAt = stats.BytesReceived;
            peer.StatsAtUs = now;
        }
        lastStatsUs = now;
    }

    NetPeerStats StatsOf(HostPeer peer)
    {
        var link = transport.Stats(peer.Id);
        return new NetPeerStats(link.RoundTripMs, link.LossPercent, peer.InPerSecond, peer.OutPerSecond, peer.Replicator?.LastSend.SnapshotBytes ?? 0);
    }

    static NetPlayerState StateOf(Member m) => m.Peer?.State switch
    {
        HostPeerState.Playing => NetPlayerState.Playing,
        HostPeerState.Loading => NetPlayerState.Loading,
        _ => NetPlayerState.Away,
    };

    void Announce(NoticeNet notice)
    {
        foreach (var peer in peers.Values)
            if (peer.InGame && !ending.ContainsKey(peer.Id)) peer.Pending.Add(notice);
        Notice?.Invoke(notice);
    }

    HostPeer? PeerOf(int player) => player > 0 && members.TryGetValue(player, out var m) ? m.Peer : null;

    string NameOf(int player) => player == 0 ? self.Name : members.TryGetValue(player, out var m) ? m.Name : $"Player {player}";

    List<HostPeer> Snapshot(IEnumerable<HostPeer> source)
    {
        scratchPeers.Clear();
        scratchPeers.AddRange(source);
        return scratchPeers;
    }
}
