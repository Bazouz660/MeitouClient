using System.Buffers.Binary;
using Meitou.Engine;
using Meitou.Engine.Time;
using Meitou.Net.Commands;
using Meitou.Net.Events;
using Meitou.Net.Replication;
using Meitou.Net.Transport;
using Meitou.Simulation;
using WorldResetMessage = Meitou.Net.Session.WorldReset;
using ReadyMessage = Meitou.Net.Session.Ready;

namespace Meitou.Net.Session;

/// <summary>Where a <see cref="NetClient"/> is: connecting, answering the challenge, joined (a world epoch is known, the replica not ready), playing, or closed.</summary>
public enum NetClientState : byte { Idle, Connecting, Authenticating, Joined, Playing, Closed }

/// <summary>A client's link figures for the stats line and the connection indicator: the transport's, the jitter buffer's, and the age of the newest snapshot.</summary>
public readonly record struct NetClientStats(int RoundTripMs, float LossPercent, int BytesInPerSecond, int BytesOutPerSecond,
    int DelayMs, int Starved, double SnapshotAgeSeconds);

/// <summary>
/// A net client (docs/net.md, "Client and jitter buffer"): it joins a host, builds the host's snapshots with a <see cref="ClientMirror"/>, shows them in the
/// attached replica <see cref="WorldSession"/> through a <see cref="JitterBuffer"/>, follows the host's clock, releases events, and sends the replica's
/// commands, its acks and view, chat and clock requests. Headless: it needs no game, only a transport, and runs on the frame thread
/// (<see cref="BeginFrame"/> before the session advances, <see cref="EndFrame"/> after). It owns the transport and disposes it.
/// </summary>
public sealed class NetClient : IClockAuthority, IDisposable
{
    /// <summary>Longest wait between two <see cref="ClientFrame"/>s while no snapshot part arrives, in microseconds.</summary>
    public const long FrameIntervalUs = 100_000;

    const string BadData = "Bad data from the host.";

    readonly INetClientTransport transport;
    readonly NetIdentity self;
    readonly INetClock clock;
    readonly ulong animationHash;
    readonly ClientMirror mirror;
    readonly JitterBuffer jitter = new();
    readonly ClientEvents events = new();
    readonly StringTableWriter controlStrings = new(true);
    readonly NetWriter writer = new(1024);
    readonly Listener listener;
    NetConnectionId? peer;
    string host = "";
    string? password;
    WorldSession? session, authority;
    ClockState? clockState;
    ClockChange lastChange = new(-1, ClockChangeKind.None, 0);
    bool worldKnown, polling, disposeAfterPoll, disposed;
    uint nextSeq = 1;
    long nowUs, lastFrameUs, lastSnapshotUs = -1, statsStartUs = -1, statsIn, statsOut;
    int bytesIn, bytesOut;
    ViewState view;
    Action<string>? log;

    public NetClient(INetClientTransport transport, NetIdentity self, ReplicaCatalog catalog, INetClock? clock = null, ILookResolver? looks = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(self);
        ArgumentNullException.ThrowIfNull(catalog);
        this.transport = transport;
        this.self = self;
        this.clock = clock ?? new StopwatchNetClock();
        animationHash = Handshake.AnimationHash(catalog.Animations);
        mirror = new ClientMirror(catalog.Animations, looks ?? new LookCache(catalog.Appearances, catalog.Database));
        listener = new Listener(this);
    }

    public NetClientState State { get; private set; }

    /// <summary>This client's player number (-1 before the <c>Welcome</c>; a world swap may change it).</summary>
    public int Player { get; private set; } = -1;

    /// <summary>The world epoch of the last <c>WorldReset</c>.</summary>
    public ushort Epoch { get; private set; }

    public NetRules? Rules { get; private set; }
    public SessionRules? SessionRules { get; private set; }

    /// <summary>Why the client closed; null while open.</summary>
    public NetDisconnect? Closed { get; private set; }

    public IReadOnlyList<RosterEntry> Roster { get; private set; } = [];

    /// <summary>A snapshot of this epoch is on show in the attached replica.</summary>
    public bool HasPresented { get; private set; }

    /// <summary>The newest snapshot the mirror assembled in this epoch (null: none yet), before the jitter buffer delays it.</summary>
    public WorldSnapshot? Newest { get; private set; }

    /// <summary>The presentation point in host ticks (null: nothing presented in this epoch).</summary>
    public double? PresentedTick => jitter.Position;

    /// <summary>Diagnostics (bad parts, commands not sent, the reason of a protocol error); null: none.</summary>
    public Action<string>? Log
    {
        get => log;
        set => mirror.Log = log = value;
    }

    public NetClientStats Stats
    {
        get
        {
            var link = peer is { } p && !disposed ? transport.Stats(p) : default;
            double age = lastSnapshotUs < 0 ? 0 : Math.Max(0, clock.NowMicroseconds - lastSnapshotUs) / 1e6;
            return new NetClientStats(link.RoundTripMs, link.LossPercent, bytesIn, bytesOut, jitter.DelayMs, jitter.Starved, age);
        }
    }

    /// <summary>The first one is the join; later ones are host world swaps: build a replica (<see cref="SessionMode.Replica"/>), <see cref="Attach"/> it, then <see cref="Ready"/>.</summary>
    public event Action<WorldResetMessage>? WorldReset;

    /// <summary>The connection ended from the host's side or the network (not raised for <see cref="Leave"/>).</summary>
    public event Action<NetDisconnect>? Disconnected;

    public event Action<ChatNet>? Chat;
    public event Action<NoticeNet>? Notice;
    public event Action<KnockoutNet>? Knockout;

    /// <summary>A message for the log (<c>MessageNet</c>, false) or a refusal of one of this client's commands or requests (true: an error).</summary>
    public event Action<string, bool>? Message;

    /// <summary>A pause, resume, speed or time change made by a player (the name from the roster), this client's own included.</summary>
    public event Action<ClockChange, string>? ClockChanged;

    public event Action<WeatherSync>? Weather;
    public event Action<RulesUpdate>? RulesChanged;
    public event Action? RosterChanged;

    /// <summary>Starts connecting to <paramref name="host"/>:<paramref name="port"/> (<see cref="NetClientState.Connecting"/>); only once, from <see cref="NetClientState.Idle"/>.</summary>
    public void Connect(string host, int port, string? password)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (State != NetClientState.Idle) throw new InvalidOperationException("a net client connects once");
        this.host = host;
        this.password = password;
        Span<byte> request = stackalloc byte[NetProtocol.Magic.Length + 2];
        NetProtocol.Magic.CopyTo(request);
        BinaryPrimitives.WriteUInt16LittleEndian(request[NetProtocol.Magic.Length..], NetProtocol.Version);
        State = NetClientState.Connecting;
        peer = transport.Connect(host, port, request);
    }

    /// <summary>
    /// Shows this epoch's snapshots in <paramref name="replica"/> (its <see cref="WorldSession.Mode"/> must be <see cref="SessionMode.Replica"/>) and makes this client its
    /// <see cref="WorldSession.ClockAuthority"/>. Ignored once closed.
    /// </summary>
    public void Attach(WorldSession replica)
    {
        ArgumentNullException.ThrowIfNull(replica);
        if (replica.Mode != SessionMode.Replica) throw new ArgumentException("a net client presents into a replica session", nameof(replica));
        if (State == NetClientState.Closed) return;
        session = authority = replica;
        replica.ClockAuthority = this;
        HasPresented = false;
    }

    /// <summary>Tells the host the replica of <see cref="Epoch"/> is ready (<c>Ready</c>); snapshots start and the state becomes <see cref="NetClientState.Playing"/>. Only while <see cref="NetClientState.Joined"/>.</summary>
    public void Ready()
    {
        if (State != NetClientState.Joined || !worldKnown) return;
        Send(NetChannel.Control, w => ProtocolCodec.Write(w, new ReadyMessage(Epoch)));
        State = NetClientState.Playing;
        lastFrameUs = clock.NowMicroseconds - FrameIntervalUs;
    }

    /// <summary>The camera the host's interest and event filter use; sent with the next <see cref="ClientFrame"/>.</summary>
    public void SetView(ViewState view) => this.view = view;

    /// <summary>Before the session advances: polls the transport, assembles and presents the host's snapshots, follows the host's clock and releases due events.</summary>
    public void BeginFrame(double realSeconds)
    {
        if (State is NetClientState.Idle or NetClientState.Closed) return;
        nowUs = clock.NowMicroseconds;
        polling = true;
        try { transport.Poll(listener); }
        finally { polling = false; }
        if (disposeAfterPoll) Dispose();
        if (State is not (NetClientState.Joined or NetClientState.Playing)) return;
        if (mirror.TryAssemble(out var snapshot))
        {
            jitter.Add(snapshot.Tick, nowUs, snapshot);
            Newest = snapshot;
            lastSnapshotUs = nowUs;
        }
        var replica = session is { Mode: SessionMode.Replica } s ? s : null;
        FollowSpeed(replica);
        var shown = jitter.Advance(nowUs, realSeconds);
        if (shown is { } p && replica is not null)
        {
            replica.Present(p.Older, p.Newer, p.Alpha, p.MotionAlpha);
            HasPresented = true;
            FollowTime(replica, p.Tick);
        }
        events.Release(replica?.World, shown is { } q ? (long)Math.Floor(q.Tick) : null, nowUs);
        CountBytes();
    }

    /// <summary>After the session advanced: sends the replica's queued commands (only while playing; else they are dropped) and, when due, a <see cref="ClientFrame"/>.</summary>
    public void EndFrame()
    {
        if (State is not (NetClientState.Joined or NetClientState.Playing)) return;
        if (session is { Mode: SessionMode.Replica } s)
        {
            var commands = s.World.Commands.TakeDue(long.MaxValue);
            if (commands.Count > 0 && State == NetClientState.Playing) SendCommands(commands);
        }
        if (State != NetClientState.Playing || peer is not { } p) return;
        long now = clock.NowMicroseconds;
        if (!mirror.HasNewParts && now - lastFrameUs < FrameIntervalUs) return;
        writer.Reset();
        mirror.WriteFrame(writer, view);
        if (writer.Length <= transport.MaxUnreliablePayload(p)) transport.Send(p, NetChannel.Snapshots, writer.Written);
        lastFrameUs = now;
    }

    /// <summary>Sends a chat line (cut to <see cref="NetProtocol.MaxChatLength"/> characters); the host echoes it back as a <see cref="Chat"/> event.</summary>
    public void SendChat(string text)
    {
        if (State is not (NetClientState.Joined or NetClientState.Playing) || string.IsNullOrWhiteSpace(text)) return;
        if (text.Length > NetProtocol.MaxChatLength)
            text = text[..(char.IsHighSurrogate(text[NetProtocol.MaxChatLength - 1]) ? NetProtocol.MaxChatLength - 1 : NetProtocol.MaxChatLength)];
        Send(NetChannel.Control, w => ProtocolCodec.Write(w, new ChatRequest(text)));
    }

    /// <summary>Leaves the game: the host gets <c>Reject(Left)</c>, the state becomes <see cref="NetClientState.Closed"/>; <see cref="Disconnected"/> is not raised.</summary>
    public void Leave() => Close(new NetDisconnect(RejectCode.Left, "You left the game."), Encode(new Reject(RejectCode.Left, "Left the game.")), raise: false);

    /// <summary>The local player's pause or speed input, sent to the host as a <c>ClockRequest</c>; the host's <c>ClockState</c> brings the result.</summary>
    void IClockAuthority.Request(ClockRequest request)
    {
        if (State is not (NetClientState.Joined or NetClientState.Playing) || !Enum.IsDefined(request.Kind)) return;
        if (!double.IsFinite(request.Speed)) request = request with { Speed = 0 };
        Send(NetChannel.Control, w => ProtocolCodec.Write(w, request));
    }

    /// <summary><see cref="Leave"/>, then the transport is disposed (after the poll when called from an event handler).</summary>
    public void Dispose()
    {
        Leave();
        if (disposed) return;
        if (polling)
        {
            disposeAfterPoll = true;
            return;
        }
        disposed = true;
        transport.Dispose();
        writer.Dispose();
    }

    // ---- Receiving ----

    sealed class Listener(NetClient client) : INetListener
    {
        public byte[]? Request(string address, ReadOnlySpan<byte> connectData) => null;

        public void Connected(NetConnectionId peer) { }

        public void Received(NetConnectionId peer, NetChannel channel, ReadOnlySpan<byte> data) => client.Received(peer, channel, data);

        public void Dropped(NetConnectionId peer, NetDropReason reason, ReadOnlySpan<byte> data) => client.Dropped(peer, reason, data);
    }

    void Received(NetConnectionId from, NetChannel channel, ReadOnlySpan<byte> data)
    {
        if (from != peer || State == NetClientState.Closed) return;
        try
        {
            switch (channel)
            {
                case NetChannel.Snapshots:
                    if (State is NetClientState.Joined or NetClientState.Playing) mirror.Receive(channel, data);
                    break;
                case NetChannel.Views:
                    if (State is NetClientState.Joined or NetClientState.Playing) mirror.Receive(channel, data);
                    break;
                case NetChannel.Control:
                    OnControl(data);
                    break;
                case NetChannel.Events:
                    OnEvents(data);
                    break;
            }
        }
        catch (NetFormatException e)
        {
            log?.Invoke($"protocol error on {channel}: {e.Message}");
            Close(new NetDisconnect(RejectCode.ProtocolError, BadData), Encode(new Reject(RejectCode.ProtocolError, BadData)), raise: true);
        }
    }

    void OnControl(ReadOnlySpan<byte> data)
    {
        var r = new NetReader(data);
        var type = ProtocolCodec.ReadType(ref r);
        bool joined = State is NetClientState.Joined or NetClientState.Playing;
        switch (type)
        {
            case MessageType.Challenge:
                var challenge = ProtocolCodec.ReadChallenge(ref r);
                if (State != NetClientState.Connecting) throw new NetFormatException("A Challenge after the handshake started.");
                OnChallenge(challenge);
                break;
            case MessageType.Welcome:
                var welcome = ProtocolCodec.ReadWelcome(ref r);
                if (State != NetClientState.Authenticating) throw new NetFormatException("A Welcome without a Hello.");
                Player = welcome.Player;
                Rules = welcome.Rules;
                Roster = welcome.Roster;
                State = NetClientState.Joined;
                RosterChanged?.Invoke();
                break;
            case MessageType.Reject:
                var reject = ProtocolCodec.ReadReject(ref r);
                Close(new NetDisconnect(reject.Code, reject.Reason), [], raise: true);
                break;
            case MessageType.WorldReset:
                var reset = ProtocolCodec.ReadWorldReset(ref r);
                if (!joined) throw new NetFormatException("A WorldReset before the Welcome.");
                OnWorldReset(reset);
                break;
            case MessageType.Roster:
                var roster = ProtocolCodec.ReadRoster(ref r);
                if (!joined) break;
                Roster = roster;
                RosterChanged?.Invoke();
                break;
            case MessageType.ClockState:
                var state = ProtocolCodec.ReadClockState(ref r);
                if (joined && worldKnown && state.Epoch == Epoch) OnClock(state);
                break;
            case MessageType.Rules:
                var rules = ProtocolCodec.ReadRulesUpdate(ref r);
                if (!joined) break;
                Rules = rules.Net;
                SessionRules = rules.Session;
                RulesChanged?.Invoke(rules);
                break;
            case MessageType.Weather:
                var weather = ProtocolCodec.ReadWeather(ref r);
                if (joined) Weather?.Invoke(weather);
                break;
            case MessageType.CommandResult:
                var result = CommandBatchCodec.ReadResult(ref r);
                r.ExpectEnd();
                if (joined) Message?.Invoke(result.Reason, true);
                break;
            default:
                throw new NetFormatException($"{type} is not a host message on the Control channel.");
        }
    }

    void OnChallenge(Challenge challenge)
    {
        if (challenge.NeedsPassword && string.IsNullOrEmpty(password))
        {
            const string text = "This game needs a password.";
            Close(new NetDisconnect(RejectCode.Password, text), Encode(new Reject(RejectCode.Password, text)), raise: true);
            return;
        }
        var proof = Handshake.Proof(challenge.NeedsPassword ? password : null, challenge.Nonce, self.PlayerId);
        var hello = new Hello(NetProtocol.Version, self.Build, self.PlayerId, self.Name, proof, self.Data, animationHash);
        try
        {
            Send(NetChannel.Control, w => ProtocolCodec.Write(w, hello));
        }
        catch (ArgumentException e)
        {
            // Only a data set the codec cannot carry (more than ProtocolCodec.MaxDataFiles files) gets here.
            string text = $"This game data cannot be sent to the host: {e.Message}";
            Close(new NetDisconnect(RejectCode.Data, text), Encode(new Reject(RejectCode.Data, text)), raise: true);
            return;
        }
        State = NetClientState.Authenticating;
    }

    void OnWorldReset(WorldResetMessage reset)
    {
        Epoch = reset.Epoch;
        worldKnown = true;
        Player = reset.Player;
        SessionRules = reset.Rules;
        mirror.Reset(reset.Epoch);
        jitter.Clear();
        events.Clear();
        Newest = null;
        lastSnapshotUs = -1;
        HasPresented = false;
        session = null;   // the game builds the new replica and attaches it; the old session stays the clock authority's until then
        State = NetClientState.Joined;
        clockState = reset.Clock;
        WorldReset?.Invoke(reset);
        if (State != NetClientState.Closed) OnClock(reset.Clock);
    }

    void OnClock(ClockState state)
    {
        clockState = state;
        var change = state.Change;
        if (change.Player < 0 || change.Kind == ClockChangeKind.None || change == lastChange) return;
        lastChange = change;
        ClockChanged?.Invoke(change, NameOf(change.Player));
    }

    void OnEvents(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0 || data[0] != (byte)MessageType.EventBatch) throw new NetFormatException("The Events channel carries event batches only.");
        var r = new NetReader(data);
        var batch = EventCodec.Read(ref r);
        r.ExpectEnd();
        // Spatial events and knock-outs name this world's characters: another epoch's are dropped. Chat, notices and messages are not tied to a world.
        bool current = worldKnown && batch.Epoch == Epoch;
        foreach (var e in batch.Events)
        {
            if (State == NetClientState.Closed) return;
            switch (e)
            {
                case SoundNet or BloodNet or FloaterNet:
                    if (current) events.Add(batch.Tick, nowUs, e);
                    break;
                case ChatNet chat: Chat?.Invoke(chat); break;
                case NoticeNet notice: Notice?.Invoke(notice); break;
                case MessageNet message: Message?.Invoke(message.Text, false); break;
                case KnockoutNet knockout:
                    if (current) Knockout?.Invoke(knockout);
                    break;
            }
        }
    }

    void Dropped(NetConnectionId from, NetDropReason reason, ReadOnlySpan<byte> data)
    {
        if (from != peer || State == NetClientState.Closed) return;
        Reject? reject = null;
        if (data.Length > 0)
        {
            try
            {
                var r = new NetReader(data);
                if (ProtocolCodec.ReadType(ref r) == MessageType.Reject) reject = ProtocolCodec.ReadReject(ref r);
            }
            catch (NetFormatException) { }
        }
        var end = reject is not null ? new NetDisconnect(reject.Code, reject.Reason) : reason switch
        {
            NetDropReason.Timeout when State == NetClientState.Connecting => new NetDisconnect(RejectCode.Unreachable, $"Could not reach {host}."),
            NetDropReason.Error => new NetDisconnect(RejectCode.Unreachable, $"Could not reach {host}."),
            NetDropReason.Rejected => new NetDisconnect(RejectCode.Protocol, "The host refused the connection."),
            _ => new NetDisconnect(RejectCode.Timeout, "Lost connection to the host."),
        };
        Close(end, null, raise: true);
    }

    void Close(NetDisconnect end, byte[]? payload, bool raise)
    {
        if (State == NetClientState.Closed) return;
        if (payload is not null && peer is { } p) transport.Drop(p, payload);
        State = NetClientState.Closed;
        Closed = end;
        if (authority is { } a && ReferenceEquals(a.ClockAuthority, this)) a.ClockAuthority = null;
        session = authority = null;
        jitter.Clear();
        events.Clear();
        if (raise) Disconnected?.Invoke(end);
    }

    // ---- Presenting ----

    /// <summary>The replica's speed and pause show the host's; the presentation runs at 30 x the host's achieved speed, 0 while paused or busy.</summary>
    void FollowSpeed(WorldSession? replica)
    {
        if (clockState is not { } c) return;
        double speed = c.AchievedSpeed > 0 ? Math.Min(c.AchievedSpeed, c.Speed) : c.Speed;
        jitter.SetRate(c.Speed == 0 || c.HostBusy ? 0 : JitterBuffer.TicksPerSecond * speed, Rules?.SendRate ?? 20);
        if (replica is null) return;
        var sim = replica.Simulation;
        double last = c.LastSpeed is 1 or 2 or 5 ? c.LastSpeed : 1;
        sim.SetSpeed(last);
        if (c.Speed == 0) sim.SetSpeed(0);
        else if (c.Speed is 1 or 2 or 5) sim.SetSpeed(c.Speed);
        sim.RequestedPause = c.HostBusy;
    }

    /// <summary>
    /// The replica's game clock shows the host's time at the presented tick: <c>AnchorHours + (tick - AnchorTick) / 30 / SecondsPerGameHour</c>; it moves
    /// forward by the difference when that is under an hour of game seconds, holds when it is less than a second behind, else jumps.
    /// </summary>
    void FollowTime(WorldSession replica, double tick)
    {
        if (clockState is not { } c) return;
        double hours = c.AnchorHours + (tick - c.AnchorTick) / JitterBuffer.TicksPerSecond / GameClock.SecondsPerGameHour;
        double seconds = (hours - replica.Clock.TotalHours) * GameClock.SecondsPerGameHour;
        if (seconds is >= 0 and < 3600) replica.Clock.Advance(seconds);
        else if (!(seconds is > -1 and < 0) && double.IsFinite(hours))
        {
            double day = Math.Floor(hours / 24);
            replica.Clock.SetTime((long)day, hours - day * 24);
        }
        replica.World.GameHours = replica.Clock.TotalHours;
    }

    // ---- Sending ----

    void SendCommands(IReadOnlyList<SimCommand> commands)
    {
        bool cheats = Rules?.Cheats ?? false;
        var sendable = new List<SimCommand>(commands.Count);
        bool refused = false;
        foreach (var command in commands)
        {
            // The host's reader drops a peer that sends a host-only command, or a debug one with cheats off: never write those.
            CommandOrigin origin;
            try { origin = CommandTags.Of(CommandTags.TagOf(command.GetType())).Origin; }
            catch (ArgumentException) { log?.Invoke($"{command.GetType().Name} has no wire tag; not sent"); continue; }
            if (origin == CommandOrigin.HostOnly || origin == CommandOrigin.Debug && !cheats)
            {
                log?.Invoke($"{command.GetType().Name} is {(origin == CommandOrigin.HostOnly ? "host-only" : "a cheat and cheats are off")}; not sent");
                refused |= origin == CommandOrigin.Debug;
                continue;
            }
            sendable.Add(command);
        }
        if (refused) Message?.Invoke("Cheats are off in this game.", true);
        for (int i = 0; i < sendable.Count; i += NetLimits.MaxCommandBatch)
            SendBatch(sendable.GetRange(i, Math.Min(NetLimits.MaxCommandBatch, sendable.Count - i)));
    }

    /// <summary>Sends one batch; one the codec refuses (too large, an unencodable command) is split until the command at fault is found and dropped.</summary>
    void SendBatch(List<SimCommand> batch)
    {
        if (peer is not { } p) return;
        writer.Reset();
        writer.WriteByte((byte)MessageType.CommandBatch);
        try
        {
            CommandBatchCodec.Write(writer, controlStrings, new CommandBatch(Epoch, nextSeq, batch));
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            if (batch.Count == 1)
            {
                log?.Invoke($"{batch[0].GetType().Name} not sent: {e.Message}");
                return;
            }
            int half = batch.Count / 2;
            SendBatch(batch.GetRange(0, half));
            SendBatch(batch.GetRange(half, batch.Count - half));
            return;
        }
        transport.Send(p, NetChannel.Control, writer.Written);
        nextSeq += (uint)batch.Count;
    }

    void Send(NetChannel channel, Action<NetWriter> write)
    {
        if (peer is not { } p) return;
        writer.Reset();
        write(writer);
        transport.Send(p, channel, writer.Written);
    }

    static byte[] Encode(Reject reject)
    {
        using var w = new NetWriter(64);
        ProtocolCodec.Write(w, reject);
        return w.Written.ToArray();
    }

    string NameOf(int player)
    {
        foreach (var e in Roster)
            if (e.Player == player)
                return e.Name;
        return player == Player ? self.Name : $"Player {player}";
    }

    void CountBytes()
    {
        if (peer is not { } p) return;
        var link = transport.Stats(p);
        if (statsStartUs < 0)
        {
            (statsStartUs, statsIn, statsOut) = (nowUs, link.BytesReceived, link.BytesSent);
            return;
        }
        long span = nowUs - statsStartUs;
        if (span < 1_000_000) return;
        bytesIn = (int)Math.Min(int.MaxValue, (link.BytesReceived - statsIn) * 1_000_000 / span);
        bytesOut = (int)Math.Min(int.MaxValue, (link.BytesSent - statsOut) * 1_000_000 / span);
        (statsStartUs, statsIn, statsOut) = (nowUs, link.BytesReceived, link.BytesSent);
    }
}
