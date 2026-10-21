using System.Globalization;
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
/// The listen-server side of a co-op session (docs/net.md, "Host"): accepts and authenticates clients, gives each a player number, gates their
/// commands into the host's world, owns the clock, and streams each client its view (snapshots, views, events). Headless: it needs only the
/// <see cref="WorldSession"/>, a transport and <see cref="IHostGame"/>. Everything runs on the caller's frame thread: <see cref="BeginFrame"/> right
/// before <c>session.Advance</c> (the transport's events arrive only inside its poll there), <see cref="EndFrame"/> after it. Owns the transport.
/// </summary>
public sealed class NetHost : IClockAuthority, IDisposable
{
    /// <summary>The message a remote player gets when the host restricts the clock.</summary>
    public const string HostOnlyClockText = "Only the host can change the game speed.";
    /// <summary>The message a remote player gets (at most once a second) when it sent more commands than the rate limit lets through.</summary>
    public const string CommandFloodText = "Too many commands at once; some were ignored.";
    const string ProtocolErrorText = "The host could not read what your game sent.";
    const string TimeoutText = "Lost connection to the host.";
    const string LoadTimeoutText = "Took too long to load.";
    const string NoSlotText = "No free player slot.";
    const string DuplicateText = "Signed in from another place.";
    const string HostIdText = "Your player id is already in this game.";
    const string DefaultCloseText = "The host ended the game.";

    const long ClockEveryUs = 2_000_000, RosterEveryUs = 2_000_000, StatsEveryUs = 1_000_000, WeatherPollUs = 1_000_000, WeatherEveryUs = 60_000_000;
    /// <summary>Most targeted events waiting per client (the oldest go first) and most clock changes announced per frame.</summary>
    const int MaxOutbox = 4096, MaxClockChanges = 8;

    readonly WorldSession session;
    readonly INetServerTransport transport;
    readonly NetHostOptions options;
    readonly NetIdentity self;
    readonly IHostGame game;
    readonly ReplicaCatalog catalog;
    readonly INetClock clock;
    readonly int maxPlayers, sendRate, pausedSendRate;
    readonly ulong animationHash;
    readonly LookRegistry looks;
    readonly SnapshotEncoderOptions encoder;
    readonly HostEventTap tap = new();
    readonly Listener listener;
    readonly Dictionary<NetConnectionId, HostPeer> peers = [];
    // Remote players by ascending number, away ones included (they keep number, name and colour).
    readonly List<HostPlayer> players = [];
    readonly HashSet<Guid> bans = [];
    readonly NetWriter writer = new(1024);
    readonly List<NetEvent> spatial = [], batch = [];
    readonly List<ClockChange> clockChanges = [];
    readonly List<HostPeer> scratch = [];

    bool started, stopped, disposed, lastBusy, rosterDirty = true;
    ushort epoch = 1;
    World? anchorWorld;
    long anchorTick;
    double anchorHours;
    long lastClockUs, lastRosterUs, lastStatsUs, lastWeatherPollUs, lastWeatherUs = long.MinValue;
    int? lastWeatherRevision;
    RulesUpdate? lastRules;
    IReadOnlyList<RosterEntry>? roster;

    /// <summary>
    /// A host for <paramref name="session"/>'s world on <paramref name="transport"/> (owned and disposed with the host). <paramref name="catalog"/>'s
    /// animation library is the one clients must share (its <see cref="Handshake.AnimationHash"/> is checked at <c>Hello</c>); its appearance source should
    /// be the game's <see cref="RecordingAppearances"/> so looks made before hosting have recipes (another source is wrapped, and its looks fall back to
    /// <see cref="AppearanceRecipe.FromLook"/>). <paramref name="clock"/> defaults to a <see cref="StopwatchNetClock"/>.
    /// </summary>
    public NetHost(WorldSession session, INetServerTransport transport, NetHostOptions options, NetIdentity self,
        IHostGame game, ReplicaCatalog catalog, INetClock? clock = null)
    {
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.self = self ?? throw new ArgumentNullException(nameof(self));
        this.game = game ?? throw new ArgumentNullException(nameof(game));
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.clock = clock ?? new StopwatchNetClock();
        maxPlayers = Math.Clamp(options.MaxPlayers, 2, NetProtocol.MaxPlayers);
        sendRate = Math.Clamp(options.SendRate, 1, 60);
        pausedSendRate = Math.Clamp(options.PausedSendRate, 1, sendRate);
        animationHash = Handshake.AnimationHash(catalog.Animations);
        looks = new LookRegistry(catalog.Appearances as RecordingAppearances ?? new RecordingAppearances(catalog.Appearances));
        encoder = new SnapshotEncoderOptions { SendHz = sendRate, BytesPerSecond = Math.Max(1024, options.MaxBytesPerSecond) };
        listener = new Listener(this);
    }

    /// <summary>The world epoch: 1 at start, +1 at every world swap (0 is skipped when it wraps, so a client's 0 means "none yet").</summary>
    public ushort Epoch => epoch;

    /// <summary>The lobby rules every player sees (the bound port after <see cref="Start"/>).</summary>
    public NetRules Rules => new(sendRate, options.ClockPolicy, maxPlayers, game.Gate.Cheats, !string.IsNullOrEmpty(options.Password), transport.Port);

    /// <summary>The player list: the host first, then remote players by number; away players stay listed.</summary>
    public IReadOnlyList<RosterEntry> Roster => roster ??= BuildRoster(session.World);

    /// <summary>Every player with link figures: the host first.</summary>
    public IReadOnlyList<NetPlayerInfo> Players
    {
        get
        {
            var list = new List<NetPlayerInfo>(players.Count + 1) { new(0, self.PlayerId, self.Name, NetPlayerState.Host, 0, default) };
            foreach (var p in players)
            {
                var stats = default(NetPeerStats);
                if (p.Peer is { } peer)
                {
                    var link = transport.Stats(peer.Id);
                    stats = new NetPeerStats(link.RoundTripMs, link.LossPercent, peer.BytesInPerSecond, peer.BytesOutPerSecond, peer.LastSnapshotBytes);
                }
                list.Add(new NetPlayerInfo(p.Number, p.Id, p.Name, p.State, p.Colour, stats));
            }
            return list;
        }
    }

    /// <summary>Joined, left, timed out, kicked and banned players (the same notices go to every client).</summary>
    public event Action<NoticeNet>? Notice;

    /// <summary>Every chat line, the host's own included, in the order clients get them.</summary>
    public event Action<ChatNet>? Chat;

    /// <summary>Every pause or speed change, the host's own included, with the name of the player who made it.</summary>
    public event Action<ClockChange, string>? ClockChanged;

    /// <summary>Diagnostics: refused or unencodable messages, joins and drops. Null: none.</summary>
    public Action<string>? Log { get; set; }

    bool Running => started && !stopped;
    long NowUs => clock.NowMicroseconds;
    Vector3 CameraTarget => session.Camera.Current.Target;

    /// <summary>
    /// Starts listening (a port that cannot be bound throws <see cref="System.Net.Sockets.SocketException"/> and nothing changes), names the local session
    /// after <c>self</c>, takes the clock (<see cref="WorldSession.ClockAuthority"/>; modal screens stop pausing) and clears a requested pause.
    /// </summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (started) throw new InvalidOperationException("The host is already started.");
        transport.Listen(options.Port);
        started = true;
        var world = session.World;
        HostWorldLink.Join(world, 0, self.PlayerId, self.Name);
        session.ClockAuthority = this;
        session.Simulation.RequestedPause = false;
        session.WorldReplaced += OnWorldReplaced;
        Anchor(world);
        long now = NowUs;
        lastClockUs = lastRosterUs = lastStatsUs = now;
        lastRules = new RulesUpdate(Rules, game.Rules);
        Report(string.Create(CultureInfo.InvariantCulture, $"Hosting on port {transport.Port}."));
    }

    /// <summary>Immediately before <c>session.Advance</c>: polls the transport (handshakes, commands, clock and chat requests, drops), applies the timeouts, then hooks the event tap.</summary>
    public void BeginFrame()
    {
        if (!Running) return;
        transport.Poll(listener);
        if (!Running) return;
        CheckTimeouts(NowUs);
        tap.BeginFrame(session.World);
    }

    /// <summary>
    /// After <c>session.Advance</c> and the game's news: the tap's events back to the host's consumers, pending <c>WorldReset</c>s, the clock, rules and
    /// weather; unless <paramref name="hostBusy"/> (the host is under its loading screen) also events and snapshots; then the roster and link figures.
    /// </summary>
    public void EndFrame(bool hostBusy)
    {
        if (!Running) return;
        var world = session.World;
        long now = NowUs;
        spatial.Clear();
        if (!tap.EndFrame(world, AnyPlaying() ? spatial : null)) spatial.Clear();
        SendWorldResets(world, now, hostBusy);
        UpdateClock(world, now, hostBusy);
        UpdateRules();
        UpdateWeather(now);
        if (!hostBusy)
        {
            SendEvents(world);
            SendSnapshots(world, now);
        }
        if (rosterDirty || now - lastRosterUs >= RosterEveryUs) BroadcastRoster(world, now);
        if (now - lastStatsUs >= StatsEveryUs) MeasureStats(now);
        lastBusy = hostBusy;
    }

    /// <summary>Queues a non-spatial event for player <paramref name="player"/> (-1: every connected remote player; the host's own number 0 is ignored); it goes out in that player's next event batch.</summary>
    public void Send(int player, NetEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (EventFilter.IsSpatial(e)) throw new ArgumentException("Spatial events reach clients through the event tap.", nameof(e));
        if (!Running) return;
        if (player < 0) Broadcast(e);
        else if (player > 0 && FindPlayer(player)?.Peer is { Welcomed: true } peer) Post(peer, e);
    }

    /// <summary>A chat line from the host (player 0): cleaned and clamped like a client's, sent to every connected client and raised as <see cref="Chat"/>.</summary>
    public void SendChat(string text)
    {
        if (!Running || CleanChat(text) is not { } line) return;
        PostChat(0, self.Name, line);
    }

    /// <summary>Drops player <paramref name="player"/>'s connection (<see cref="RejectCode.Kicked"/>); the player stays in the roster as away and may rejoin.</summary>
    public void Kick(int player, string reason)
    {
        if (!Running || FindPlayer(player) is not { Peer: { } peer } p) return;
        Disconnect(peer, new Reject(RejectCode.Kicked, WithReason("Kicked by the host", reason)));
        Leave(p, NoticeKind.Kicked, reason ?? "");
    }

    /// <summary>Bans player <paramref name="player"/>'s roster id for this host's lifetime and drops a live connection (<see cref="RejectCode.Banned"/>).</summary>
    public void Ban(int player, string reason)
    {
        if (!Running || FindPlayer(player) is not { } p) return;
        bans.Add(p.Id);
        if (p.Peer is { } peer)
        {
            Disconnect(peer, new Reject(RejectCode.Banned, WithReason("Banned by the host", reason)));
            Leave(p, NoticeKind.Banned, reason ?? "");
        }
        else
        {
            Announce(new NoticeNet(NoticeKind.Banned, p.Number, p.Name, reason ?? ""));
        }
    }

    /// <summary>The host's own pause and speed input, as player 0 (the control tick and HUD buttons reach it through <see cref="WorldSession.RequestClock"/>).</summary>
    void IClockAuthority.Request(ClockRequest request) => ApplyClock(0, request);

    /// <summary>One line for the debug overlay: port, epoch, players, then per connected player its state and link figures.</summary>
    public string StatsLine()
    {
        var sb = new StringBuilder();
        int connected = 0;
        foreach (var p in players)
            if (p.Peer is not null) connected++;
        sb.Append(CultureInfo.InvariantCulture, $"host :{transport.Port} epoch {epoch} {connected + 1}/{maxPlayers} players");
        foreach (var info in Players)
        {
            if (info.Number == 0 || info.State == NetPlayerState.Away) continue;
            var s = info.Stats;
            sb.Append(CultureInfo.InvariantCulture,
                $" | {info.Number} {info.Name} {info.State} {s.RoundTripMs} ms {s.LossPercent:0.#}% in {s.BytesInPerSecond / 1024.0:0.0} out {s.BytesOutPerSecond / 1024.0:0.0} KB/s snap {s.LastSnapshotBytes} B");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Stops hosting: every connection is dropped with <see cref="RejectCode.HostClosed"/> and <paramref name="reason"/>, the event tap gives the world its
    /// queues back and the clock is the session's own again (<see cref="WorldSession.ClockAuthority"/> null). The world keeps the extra sessions. Idempotent.
    /// </summary>
    public void Stop(string reason)
    {
        if (!Running) return;
        stopped = true;
        byte[] payload = Encode(new Reject(RejectCode.HostClosed, string.IsNullOrEmpty(reason) ? DefaultCloseText : reason));
        foreach (var peer in peers.Values) transport.Drop(peer.Id, payload);
        peers.Clear();
        foreach (var p in players) p.Peer = null;
        tap.Detach(session.World);
        session.WorldReplaced -= OnWorldReplaced;
        if (ReferenceEquals(session.ClockAuthority, this)) session.ClockAuthority = null;
        roster = null;
        Report("Hosting stopped.");
    }

    /// <summary><see cref="Stop"/> with "The host ended the game.", then the transport is disposed.</summary>
    public void Dispose()
    {
        if (disposed) return;
        Stop(DefaultCloseText);
        disposed = true;
        transport.Dispose();
        writer.Dispose();
    }

    // ---- Transport events (inside Poll) ----

    sealed class Listener(NetHost host) : INetListener
    {
        public byte[]? Request(string address, ReadOnlySpan<byte> connectData) => host.OnRequest(connectData);
        public void Connected(NetConnectionId peer) => host.OnConnected(peer);
        public void Received(NetConnectionId peer, NetChannel channel, ReadOnlySpan<byte> data) => host.OnReceived(peer, channel, data);
        public void Dropped(NetConnectionId peer, NetDropReason reason, ReadOnlySpan<byte> data) => host.OnDropped(peer, reason);
    }

    byte[]? OnRequest(ReadOnlySpan<byte> connectData)
    {
        if (!Running) return Encode(new Reject(RejectCode.HostClosed, DefaultCloseText));
        // Every live connection counts (challenged ones too), so simultaneous requests cannot overfill the game; Hello checks again.
        return Handshake.CheckRequest(connectData, peers.Count, maxPlayers, addressBanned: false) is { } reject ? Encode(reject) : null;
    }

    void OnConnected(NetConnectionId id)
    {
        if (!Running)
        {
            transport.Drop(id, Encode(new Reject(RejectCode.HostClosed, DefaultCloseText)));
            return;
        }
        var peer = new HostPeer(id, new PeerSink(transport, id), RandomNumberGenerator.GetBytes(Handshake.NonceLength), NowUs);
        peers[id] = peer;
        var challenge = new Challenge(peer.Nonce, options.HostName, !string.IsNullOrEmpty(options.Password));
        if (TryWrite(w => ProtocolCodec.Write(w, challenge), "Challenge")) SendWritten(peer, NetChannel.Control);
    }

    void OnDropped(NetConnectionId id, NetDropReason reason)
    {
        if (!peers.Remove(id, out var peer)) return;
        if (peer.Player is { } p && ReferenceEquals(p.Peer, peer))
        {
            p.Peer = null;
            Leave(p, reason == NetDropReason.Timeout ? NoticeKind.TimedOut : NoticeKind.Left, "");
        }
    }

    void OnReceived(NetConnectionId id, NetChannel channel, ReadOnlySpan<byte> data)
    {
        if (!Running || !peers.TryGetValue(id, out var peer)) return;
        try
        {
            Receive(peer, channel, data);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // A reader refusal (NetFormatException) or anything else a client's bytes caused: the stream state is lost, so the peer goes.
            Report($"Dropped connection {id}: {e.GetType().Name}: {e.Message}");
            if (!peers.ContainsKey(id)) return;
            var p = peer.Player;
            Disconnect(peer, new Reject(RejectCode.ProtocolError, ProtocolErrorText));
            if (p is not null && peer.Welcomed) Leave(p, NoticeKind.Left, ProtocolErrorText);
        }
    }

    void Receive(HostPeer peer, NetChannel channel, ReadOnlySpan<byte> data)
    {
        if (channel == NetChannel.Snapshots)
        {
            // Unreliable: a part that does not parse is ignored, never a reason to drop.
            if (peer.Replicator is not { } replicator || data.IsEmpty || data[0] != (byte)MessageType.ClientFrame) return;
            ClientFrame frame;
            try
            {
                var fr = new NetReader(data);
                frame = ClientFrameCodec.Read(ref fr);
            }
            catch (NetFormatException)
            {
                return;
            }
            replicator.OnClientFrame(frame);
            return;
        }
        if (channel != NetChannel.Control) throw new NetFormatException($"A client sent on the {channel} channel.");
        var r = new NetReader(data);
        var type = ProtocolCodec.ReadType(ref r);
        if (!peer.Welcomed)
        {
            if (type != MessageType.Hello) throw new NetFormatException($"Expected Hello, got {type}.");
            OnHello(peer, ProtocolCodec.ReadHello(ref r));
            return;
        }
        switch (type)
        {
            case MessageType.Ready:
                OnReady(peer, ProtocolCodec.ReadReady(ref r));
                break;
            case MessageType.CommandBatch:
                OnCommands(peer, ref r);
                break;
            case MessageType.ClockRequest:
                var request = ProtocolCodec.ReadClockRequest(ref r);
                if (peer.ClockRequests.Take(NowUs)) ApplyClock(peer.Number, request);
                break;
            case MessageType.ChatRequest:
                var chat = ProtocolCodec.ReadChatRequest(ref r);
                if (peer.Chat.Take(NowUs) && CleanChat(chat.Text) is { } line) PostChat(peer.Number, peer.Player!.Name, line);
                break;
            default:
                throw new NetFormatException($"A client may not send {type}.");
        }
    }

    void OnHello(HostPeer peer, Hello hello)
    {
        var world = session.World;
        if (Handshake.CheckHello(hello, self, animationHash, options.Password, peer.Nonce, options.AllowBuildMismatch, bans.Contains(hello.Player)) is { } reject)
        {
            Disconnect(peer, reject);
            return;
        }
        var known = players.Find(p => p.Id == hello.Player);
        var previous = known?.Peer;
        int welcomed = 0;
        foreach (var other in peers.Values)
            if (other.Welcomed && !ReferenceEquals(other, previous)) welcomed++;
        if (welcomed + 1 >= maxPlayers)
        {
            Disconnect(peer, new Reject(RejectCode.Full, $"The game is full ({maxPlayers} players)."));
            return;
        }
        int number = HostWorldLink.NumberFor(world, hello.Player);
        if (number == 0)
        {
            Disconnect(peer, new Reject(RejectCode.Duplicate, HostIdText));
            return;
        }
        if (number > HostWorldLink.MaxNumber)
        {
            Disconnect(peer, new Reject(RejectCode.NoSlot, NoSlotText));
            return;
        }
        // The same player signed in again: the older connection goes quietly (the player continues on the new one).
        if (previous is not null) Disconnect(previous, new Reject(RejectCode.Duplicate, DuplicateText));

        string name = UniqueName(Handshake.SanitizeName(hello.Name, number), hello.Player);
        HostWorldLink.Join(world, number, hello.Player, name);
        var player = known ?? new HostPlayer(number, hello.Player, name, -1);
        if (known is null) players.Add(player);
        player.Number = number;
        player.Name = name;
        player.Peer = peer;
        player.Colour = PickColour(player);
        SortPlayers();
        peer.Player = player;
        peer.State = HostPeerState.Loading;
        peer.SinceUs = NowUs;
        peer.Replicator = new ClientReplicator(number, encoder, looks, catalog.Animations) { Log = Log };
        peer.Replicator.Reset(epoch);
        MarkRoster();

        var welcome = new Welcome(number, Rules, Roster);
        if (TryWrite(w => ProtocolCodec.Write(w, welcome), "Welcome")) SendWritten(peer, NetChannel.Control);
        SendWorldReset(peer, world, lastBusy);
        Report($"{name} joined as player {number}.");
        Announce(new NoticeNet(NoticeKind.Joined, number, name, ""));
    }

    void OnReady(HostPeer peer, Ready ready)
    {
        if (ready.Epoch != epoch || peer.PendingReset || peer.State != HostPeerState.Loading) return;
        var world = session.World;
        peer.State = HostPeerState.Playing;
        peer.Replicator!.Reset(epoch);
        peer.NextSendUs = NowUs;
        HostWorldLink.EnqueueJoin(world, peer.Number, HostWorldLink.JoinPoint(world, CameraTarget));
        MarkRoster();
    }

    void OnCommands(HostPeer peer, ref NetReader r)
    {
        var world = session.World;
        var gate = game.Gate;
        // Always decoded, so the Control string table stays in step with the client's even when the batch is then ignored.
        var commands = CommandBatchCodec.Read(ref r, peer.ControlStrings, world.Tick, peer.Number, gate.Cheats);
        r.ExpectEnd();
        if (peer.State != HostPeerState.Playing || commands.Epoch != epoch) return;
        long now = NowUs;
        bool flooded = false;
        for (int i = 0; i < commands.Commands.Count; i++)
        {
            if (!peer.Commands.Take(now))
            {
                flooded = true;
                continue;
            }
            if (HostWorldLink.Admit(world, peer.Number, commands.Commands[i], gate) is { } refusal && peer.Refusals.Take(now))
            {
                var result = new CommandResult(unchecked(commands.FirstSeq + (uint)i), refusal);
                writer.Reset();
                writer.WriteByte((byte)MessageType.CommandResult);
                CommandBatchCodec.Write(writer, result);
                SendWritten(peer, NetChannel.Control);
            }
        }
        if (flooded && peer.OverflowNotices.Take(now)) Post(peer, new MessageNet(CommandFloodText));
    }

    // ---- Players ----

    void CheckTimeouts(long now)
    {
        scratch.Clear();
        foreach (var peer in peers.Values)
        {
            if (peer.State == HostPeerState.Challenged ? now - peer.SinceUs > Micro(options.HelloTimeout)
                : peer.State == HostPeerState.Loading && !peer.PendingReset && now - peer.SinceUs > Micro(options.LoadTimeout))
                scratch.Add(peer);
        }
        foreach (var peer in scratch)
        {
            if (!peer.Welcomed)
            {
                Disconnect(peer, new Reject(RejectCode.Timeout, TimeoutText));
                continue;
            }
            var p = peer.Player!;
            Disconnect(peer, new Reject(RejectCode.LoadTimeout, LoadTimeoutText));
            Leave(p, NoticeKind.TimedOut, LoadTimeoutText);
        }
    }

    /// <summary>Drops a live connection with <paramref name="reject"/> as its payload; its player (if any) becomes away. No notice.</summary>
    void Disconnect(HostPeer peer, Reject reject)
    {
        peers.Remove(peer.Id);
        transport.Drop(peer.Id, Encode(reject));
        if (peer.Player is { } p && ReferenceEquals(p.Peer, peer))
        {
            p.Peer = null;
            MarkRoster();
        }
    }

    /// <summary>A joined player is gone: their windows close in the world, everyone is told.</summary>
    void Leave(HostPlayer player, NoticeKind kind, string detail)
    {
        HostWorldLink.PlayerLeft(session.World, player.Number);
        MarkRoster();
        Report($"{player.Name} ({player.Number}): {kind} {detail}");
        Announce(new NoticeNet(kind, player.Number, player.Name, detail));
    }

    void Announce(NoticeNet notice)
    {
        Broadcast(notice);
        Notice?.Invoke(notice);
    }

    HostPlayer? FindPlayer(int number)
    {
        foreach (var p in players)
            if (p.Number == number) return p;
        return null;
    }

    void SortPlayers() => players.Sort(static (a, b) => a.Number.CompareTo(b.Number));

    /// <summary>The sanitized name, with " (2)", " (3)"... when the host or another roster entry already has it (ignoring case).</summary>
    string UniqueName(string name, Guid id)
    {
        bool Taken(string candidate)
        {
            if (string.Equals(candidate, self.Name, StringComparison.OrdinalIgnoreCase)) return true;
            foreach (var p in players)
                if (p.Id != id && string.Equals(p.Name, candidate, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
        if (!Taken(name)) return name;
        for (int k = 2; ; k++)
        {
            string candidate = string.Create(CultureInfo.InvariantCulture, $"{name} ({k})");
            if (!Taken(candidate)) return candidate;
        }
    }

    /// <summary>The player's earlier colour when still free, else <see cref="PlayerColourRule"/> by number; the host and connected players hold theirs.</summary>
    int PickColour(HostPlayer player)
    {
        var taken = new HashSet<int> { 0 };
        foreach (var p in players)
            if (!ReferenceEquals(p, player) && p.Peer is not null && p.Colour >= 0) taken.Add(p.Colour);
        return PlayerColourRule.Assign(taken, player.Colour >= 0 ? player.Colour : player.Number);
    }

    void OnWorldReplaced(World next)
    {
        if (!Running) return;
        epoch = epoch == ushort.MaxValue ? (ushort)1 : (ushort)(epoch + 1);
        HostWorldLink.Join(next, 0, self.PlayerId, self.Name);
        long now = NowUs;
        var kept = new List<HostPlayer>();
        foreach (var p in players)
        {
            if (p.Peer is not { Welcomed: true } peer) continue;
            int n = HostWorldLink.NumberFor(next, p.Id);
            if (n == 0 || n > HostWorldLink.MaxNumber)
            {
                Disconnect(peer, new Reject(RejectCode.NoSlot, NoSlotText));
                Announce(new NoticeNet(NoticeKind.Left, p.Number, p.Name, NoSlotText));
                continue;
            }
            HostWorldLink.Join(next, n, p.Id, p.Name);
            p.Number = n;
            peer.State = HostPeerState.Loading;
            peer.PendingReset = true;
            peer.SinceUs = now;
            peer.Replicator!.Player = n;
            peer.Replicator.Reset(epoch);
            kept.Add(p);
        }
        // Away players are the new world's: its roster sessions not connected now (a save's players keep their numbers, names and our colours).
        foreach (var s in next.Player.Sessions)
        {
            if (s.Number == 0 || s.Guid == Guid.Empty || kept.Exists(p => p.Id == s.Guid)) continue;
            var old = players.Find(p => p.Id == s.Guid);
            string name = string.IsNullOrEmpty(s.Name) ? old?.Name ?? Handshake.SanitizeName("", s.Number) : s.Name;
            kept.Add(new HostPlayer(s.Number, s.Guid, name, old?.Colour ?? -1));
        }
        players.Clear();
        players.AddRange(kept);
        SortPlayers();
        foreach (var p in players)
            if (p.Colour < 0) p.Colour = PlayerColourRule.Assign(new HashSet<int> { 0 }, p.Number);
        Anchor(next);
        MarkRoster();
        Report(string.Create(CultureInfo.InvariantCulture, $"World replaced: epoch {epoch}."));
    }

    void MarkRoster()
    {
        rosterDirty = true;
        roster = null;
    }

    IReadOnlyList<RosterEntry> BuildRoster(World world)
    {
        var list = new List<RosterEntry>(players.Count + 1) { new(0, self.Name, NetPlayerState.Host, 0, 0, HostWorldLink.Squads(world, 0)) };
        foreach (var p in players)
        {
            int rtt = p.Peer is { } peer ? transport.Stats(peer.Id).RoundTripMs : 0;
            list.Add(new RosterEntry(p.Number, p.Name, p.State, rtt, Math.Max(0, p.Colour), HostWorldLink.Squads(world, p.Number)));
        }
        return list;
    }

    // ---- Outgoing ----

    bool AnyPlaying()
    {
        foreach (var peer in peers.Values)
            if (peer.State == HostPeerState.Playing) return true;
        return false;
    }

    void SendWorldResets(World world, long now, bool busy)
    {
        foreach (var peer in peers.Values)
        {
            if (!peer.PendingReset) continue;
            peer.PendingReset = false;
            peer.SinceUs = now;
            SendWorldReset(peer, world, busy);
        }
    }

    void SendWorldReset(HostPeer peer, World world, bool busy)
    {
        var focus = HostWorldLink.FocusOf(world, peer.Number, CameraTarget);
        if (!float.IsFinite(focus.X) || !float.IsFinite(focus.Y) || !float.IsFinite(focus.Z)) focus = Vector3.Zero;   // the reader refuses non-finite vectors
        var reset = new WorldReset(epoch, peer.Number, world.Seed, focus,
            ClockNow(world, busy, new ClockChange(-1, ClockChangeKind.None, 0)), game.Rules, game.Weather());
        if (!TryWrite(w => ProtocolCodec.Write(w, reset), "WorldReset"))
        {
            // Weather the codec refuses (a non-finite number) must not keep the client out: send the reset without it.
            if (reset.Weather is null) return;
            var bare = reset with { Weather = null };
            if (!TryWrite(w => ProtocolCodec.Write(w, bare), "WorldReset")) return;
        }
        SendWritten(peer, NetChannel.Control);
    }

    ClockState ClockNow(World world, bool busy, ClockChange change)
    {
        var sim = session.Simulation;
        return new ClockState(epoch, sim.Speed, sim.LastNonZeroSpeed, busy, sim.AchievedSpeed, world.Tick, session.Clock.TotalHours, change);
    }

    void Anchor(World world)
    {
        anchorWorld = world;
        anchorTick = world.Tick;
        anchorHours = session.Clock.TotalHours;
    }

    void ApplyClock(int player, ClockRequest request)
    {
        if (!Running) return;
        if (options.ClockPolicy == ClockPolicy.HostOnly && player != 0)
        {
            Send(player, new MessageNet(HostOnlyClockText));
            return;
        }
        var sim = session.Simulation;
        double before = sim.Speed;
        try
        {
            request.ApplyTo(sim);
        }
        catch (ArgumentOutOfRangeException)
        {
            return;   // a speed that is not 0, 1, 2 or 5, or an unknown kind: ignored
        }
        if (sim.Speed == before) return;
        var kind = sim.Speed == 0 ? ClockChangeKind.Paused : before == 0 ? ClockChangeKind.Resumed : ClockChangeKind.Speed;
        var change = new ClockChange(player, kind, sim.Speed);
        if (clockChanges.Count >= MaxClockChanges) clockChanges.RemoveAt(0);
        clockChanges.Add(change);
        ClockChanged?.Invoke(change, NameOf(player));
    }

    string NameOf(int player) => player == 0 ? self.Name : FindPlayer(player)?.Name ?? Handshake.SanitizeName("", player);

    void UpdateClock(World world, long now, bool busy)
    {
        // The game clock moved other than by the frame's ticks (a load, the time slider, a new game): clients re-anchor.
        bool timeSet = false;
        if (ReferenceEquals(anchorWorld, world))
        {
            double expected = anchorHours + (world.Tick - anchorTick) * session.Simulation.TickSeconds / GameClock.SecondsPerGameHour;
            timeSet = Math.Abs(session.Clock.TotalHours - expected) > 1e-6;
        }
        Anchor(world);
        if (timeSet) clockChanges.Add(new ClockChange(-1, ClockChangeKind.TimeSet, session.Simulation.Speed));
        if (clockChanges.Count == 0 && busy == lastBusy && now - lastClockUs < ClockEveryUs) return;
        if (clockChanges.Count == 0) clockChanges.Add(new ClockChange(-1, ClockChangeKind.None, 0));
        foreach (var change in clockChanges)
        {
            var state = ClockNow(world, busy, change);
            if (TryWrite(w => ProtocolCodec.Write(w, state), "ClockState")) SendToWelcomed(NetChannel.Control);
        }
        clockChanges.Clear();
        lastClockUs = now;
    }

    void UpdateRules()
    {
        var rules = new RulesUpdate(Rules, game.Rules);
        if (rules == lastRules) return;
        lastRules = rules;
        if (TryWrite(w => ProtocolCodec.Write(w, rules), "Rules")) SendToWelcomed(NetChannel.Control);
    }

    void UpdateWeather(long now)
    {
        if (now - lastWeatherPollUs < WeatherPollUs) return;
        lastWeatherPollUs = now;
        bool any = false;
        foreach (var peer in peers.Values) any |= peer.Welcomed;
        if (!any || game.Weather() is not { } weather) return;
        if (weather.Revision == lastWeatherRevision && lastWeatherUs != long.MinValue && now - lastWeatherUs < WeatherEveryUs) return;
        lastWeatherRevision = weather.Revision;
        lastWeatherUs = now;
        if (TryWrite(w => ProtocolCodec.Write(w, weather), "Weather")) SendToWelcomed(NetChannel.Control);
    }

    void BroadcastRoster(World world, long now)
    {
        roster = BuildRoster(world);
        rosterDirty = false;
        lastRosterUs = now;
        var list = roster;
        if (TryWrite(w => ProtocolCodec.Write(w, list), "Roster")) SendToWelcomed(NetChannel.Control);
    }

    /// <summary>Per client one or more event batches: its targeted events, and for a playing client the spatial ones it wants, split so each fits <see cref="NetLimits.MaxReliableMessage"/>.</summary>
    void SendEvents(World world)
    {
        foreach (var peer in peers.Values)
        {
            if (!peer.Welcomed) continue;
            batch.Clear();
            batch.AddRange(peer.Outbox);
            peer.Outbox.Clear();
            if (peer.State == HostPeerState.Playing && peer.Replicator is { } replicator)
                foreach (var e in spatial)
                    if (EventFilter.Wants(e, replicator.View, replicator.Interest, options.EventRadius)) batch.Add(e);
            if (batch.Count > 0) SendBatch(peer, world.Tick);
        }
    }

    void SendBatch(HostPeer peer, long tick)
    {
        int start = 0;
        while (start < batch.Count)
        {
            int count = Math.Min(EventCodec.MaxEvents, batch.Count - start);
            while (true)
            {
                writer.Reset();
                bool written = true;
                try
                {
                    EventCodec.Write(writer, new EventBatch(epoch, tick, batch.GetRange(start, count)));
                }
                catch (ArgumentException e)
                {
                    written = false;
                    if (count == 1) Report($"Event not sent: {e.Message}");
                }
                if (written && writer.Length <= NetLimits.MaxReliableMessage)
                {
                    SendWritten(peer, NetChannel.Events);
                    break;
                }
                if (count == 1)
                {
                    if (written) Report("Event not sent: larger than a reliable message.");
                    break;
                }
                count = (count + 1) / 2;
            }
            start += count;
        }
    }

    void SendSnapshots(World world, long now)
    {
        foreach (var peer in peers.Values)
        {
            if (peer.State != HostPeerState.Playing || peer.Replicator is not { } replicator || now < peer.NextSendUs) continue;
            replicator.Send(HostWorldLink.Publish(world, peer.Number), TimeSpan.FromTicks(now * 10), peer.Sink);
            peer.LastSnapshotBytes = replicator.LastSend.SnapshotBytes;
            long interval = 1_000_000 / (session.Simulation.IsPaused ? pausedSendRate : sendRate);
            long next = peer.NextSendUs + interval;
            peer.NextSendUs = now - next > interval ? now + interval : next;
        }
    }

    void MeasureStats(long now)
    {
        double seconds = Math.Max(1e-3, (now - lastStatsUs) / 1e6);
        lastStatsUs = now;
        foreach (var peer in peers.Values)
        {
            var link = transport.Stats(peer.Id);
            peer.BytesOutPerSecond = (int)Math.Clamp((link.BytesSent - peer.LastBytesSent) / seconds, 0, int.MaxValue);
            peer.BytesInPerSecond = (int)Math.Clamp((link.BytesReceived - peer.LastBytesReceived) / seconds, 0, int.MaxValue);
            peer.LastBytesSent = link.BytesSent;
            peer.LastBytesReceived = link.BytesReceived;
        }
    }

    void PostChat(int from, string name, string text)
    {
        var line = new ChatNet(from, name, text);
        Broadcast(line);
        Chat?.Invoke(line);
    }

    /// <summary>Queues <paramref name="e"/> for every joined (loading or playing) client.</summary>
    void Broadcast(NetEvent e)
    {
        foreach (var peer in peers.Values)
            if (peer.Welcomed) Post(peer, e);
    }

    static void Post(HostPeer peer, NetEvent e)
    {
        if (peer.Outbox.Count >= MaxOutbox) peer.Outbox.RemoveAt(0);
        peer.Outbox.Add(e);
    }

    /// <summary>Control characters dropped, trimmed, cut to <see cref="NetProtocol.MaxChatLength"/> characters (never inside a surrogate pair); null when nothing is left.</summary>
    static string? CleanChat(string? text)
    {
        if (text is null) return null;
        var sb = new StringBuilder(Math.Min(text.Length, NetProtocol.MaxChatLength + 1));
        foreach (char c in text)
            if (!char.IsControl(c)) sb.Append(c);
        string line = sb.ToString().Trim();
        if (line.Length > NetProtocol.MaxChatLength)
        {
            int cut = NetProtocol.MaxChatLength;
            if (char.IsHighSurrogate(line[cut - 1])) cut--;
            line = line[..cut].TrimEnd();
        }
        return line.Length == 0 ? null : line;
    }

    static string WithReason(string prefix, string? reason) => string.IsNullOrWhiteSpace(reason) ? prefix + "." : $"{prefix}: {reason}";

    static long Micro(TimeSpan span) => span.Ticks / TimeSpan.TicksPerMicrosecond;

    /// <summary>Writes one message into the shared writer; false (reported) when the codec refuses it or it exceeds <see cref="NetLimits.MaxReliableMessage"/>.</summary>
    bool TryWrite(Action<NetWriter> write, string what)
    {
        writer.Reset();
        try
        {
            write(writer);
        }
        catch (ArgumentException e)
        {
            Report($"{what} not sent: {e.Message}");
            return false;
        }
        if (writer.Length <= NetLimits.MaxReliableMessage) return true;
        Report($"{what} not sent: {writer.Length} bytes.");
        return false;
    }

    void SendWritten(HostPeer peer, NetChannel channel) => transport.Send(peer.Id, channel, writer.Written);

    void SendToWelcomed(NetChannel channel)
    {
        foreach (var peer in peers.Values)
            if (peer.Welcomed && !peer.PendingReset) SendWritten(peer, channel);
    }

    /// <summary>A <c>Reject</c> as a drop or refusal payload (the codec keeps it within <see cref="INetTransport.MaxDropPayload"/>).</summary>
    byte[] Encode(Reject reject)
    {
        writer.Reset();
        ProtocolCodec.Write(writer, reject);
        return writer.Written.ToArray();
    }

    void Report(string message) => Log?.Invoke(message);
}
