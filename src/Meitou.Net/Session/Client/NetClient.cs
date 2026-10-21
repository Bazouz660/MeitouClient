using System.Buffers.Binary;
using Meitou.Engine;
using Meitou.Engine.Time;
using Meitou.Net.Commands;
using Meitou.Net.Events;
using Meitou.Net.Replication;
using Meitou.Net.Transport;
using Meitou.Simulation;

namespace Meitou.Net.Session;

/// <summary>Where a client is: not started, waiting for the host, proving who it is, admitted (a world is being built), playing, or ended.</summary>
public enum NetClientState : byte { Idle, Connecting, Authenticating, Joined, Playing, Closed }

/// <summary>A client's link figures: round trip, loss, payload rates, the playback delay, starvations and the age of the newest snapshot.</summary>
public readonly record struct NetClientStats(int RoundTripMs, float LossPercent, int BytesInPerSecond, int BytesOutPerSecond,
    int DelayMs, int Starved, double SnapshotAgeSeconds);

/// <summary>
/// A co-op client (docs/net.md, "Client and jitter buffer"): it joins a host, shows the host's snapshots in a replica session through a jitter buffer,
/// sends the commands the UI queues, follows the host's clock and raises the host's chat, notices and messages. Everything runs on the caller's frame
/// thread: <see cref="BeginFrame"/> (transport events, presentation, clock) before the frame's simulation step, <see cref="EndFrame"/> (uplink) after.
/// No game types.
/// </summary>
public sealed class NetClient : IClockAuthority, IDisposable
{
    /// <summary>How often a <c>ClientFrame</c> goes out when no part arrived (seconds).</summary>
    public const double FrameEverySeconds = 0.1;

    public const string LostText = "Lost connection to the host.", UnreachableText = "Could not reach the host.", BadData = "Bad data from the host.",
        NeedsPasswordText = "This game needs a password.", LeftText = "You left the game.";

    sealed class Listener(NetClient client) : INetListener
    {
        public byte[]? Request(string address, ReadOnlySpan<byte> connectData) => [];
        public void Connected(NetConnectionId peer) => client.connected |= peer == client.peer;
        public void Received(NetConnectionId peer, NetChannel channel, ReadOnlySpan<byte> data) => client.OnReceived(peer, channel, data);
        public void Dropped(NetConnectionId peer, NetDropReason reason, ReadOnlySpan<byte> data) => client.OnDropped(peer, reason, data);
    }

    readonly INetClientTransport transport;
    readonly NetIdentity self;
    readonly ReplicaCatalog catalog;
    readonly INetClock clock;
    readonly Listener listener;
    readonly ClientMirror mirror;
    readonly JitterBuffer jitter = new();
    readonly ClientEvents events = new();
    readonly StringTableWriter commandStrings = new(confirmOnSend: true);
    readonly NetWriter writer = new(1024);
    readonly List<SimCommand> outgoing = [];
    NetConnectionId peer;
    bool connected, attached;
    string? password;
    WorldSession? session;
    ClockState? clockState;
    ViewState view;
    uint nextCommandSeq = 1;
    long lastFrameUs = long.MinValue / 2, lastSnapshotUs = -1, statsAtUs, bytesSentAt, bytesReceivedAt;
    int inPerSecond, outPerSecond;
    double presentedTick = double.NaN;
    (WorldSnapshot Older, WorldSnapshot Newer, WorldSnapshot Shown)? teleportCache;

    public NetClient(INetClientTransport transport, NetIdentity self, ReplicaCatalog catalog, INetClock? clock = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(self);
        ArgumentNullException.ThrowIfNull(catalog);
        this.transport = transport;
        this.self = self;
        this.catalog = catalog;
        this.clock = clock ?? new StopwatchNetClock();
        listener = new Listener(this);
        mirror = new ClientMirror(catalog.Animations, new LookCache(catalog.Appearances, catalog.Database));
    }

    public NetClientState State { get; private set; }
    /// <summary>This client's player number (it can change with a host world swap); -1 before the welcome.</summary>
    public int Player { get; private set; } = -1;
    /// <summary>The epoch of the host world being shown or built.</summary>
    public ushort Epoch { get; private set; }
    public NetRules? Rules { get; private set; }
    /// <summary>The host's simulation rules (the client's settings show these, locked).</summary>
    public SessionRules? SessionRules { get; private set; }
    /// <summary>How the connection ended; null while it lasts.</summary>
    public NetDisconnect? Closed { get; private set; }
    public IReadOnlyList<RosterEntry> Roster { get; private set; } = [];
    /// <summary>A snapshot of this epoch is on show.</summary>
    public bool HasPresented { get; private set; }
    /// <summary>The host's clock as last received.</summary>
    public ClockState? Clock => clockState;
    /// <summary>The newest snapshot the mirror assembled (before the jitter buffer); null before the first.</summary>
    internal WorldSnapshot? LastAssembled { get; private set; }
    /// <summary>Where diagnostics go; null: nowhere.</summary>
    public Action<string>? Log { get; set; }

    public NetClientStats Stats
    {
        get
        {
            var link = connected ? transport.Stats(peer) : default;
            double age = lastSnapshotUs < 0 ? -1 : (clock.NowMicroseconds - lastSnapshotUs) / 1e6;
            return new NetClientStats(link.RoundTripMs, link.LossPercent, inPerSecond, outPerSecond, (int)Math.Round(jitter.DelaySeconds * 1000), jitter.Starved, age);
        }
    }

    /// <summary>The first <c>WorldReset</c> is the join; later ones are the host's world swaps. The handler builds a replica, then calls <see cref="Attach"/> and <see cref="Ready"/>.</summary>
    public event Action<WorldReset>? WorldReset;
    public event Action<NetDisconnect>? Disconnected;
    public event Action<ChatNet>? Chat;
    public event Action<NoticeNet>? Notice;
    public event Action<KnockoutNet>? Knockout;
    /// <summary>A host message (false) or the refusal of one of this client's commands (true: show it as an error).</summary>
    public event Action<string, bool>? Message;
    /// <summary>A clock change a player made (the host's or a client's, this one's included), with the player's name.</summary>
    public event Action<ClockChange, string>? ClockChanged;
    public event Action<WeatherSync>? Weather;
    public event Action<RulesUpdate>? RulesChanged;
    public event Action? RosterChanged;

    /// <summary>Starts joining <paramref name="host"/>:<paramref name="port"/>; failures end in <see cref="Disconnected"/>.</summary>
    public void Connect(string host, int port, string? password)
    {
        if (State != NetClientState.Idle) throw new InvalidOperationException("a client connects once");
        this.password = string.IsNullOrEmpty(password) ? null : password;
        var data = new byte[NetProtocol.Magic.Length + 2];
        NetProtocol.Magic.CopyTo(data);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(NetProtocol.Magic.Length), NetProtocol.Version);
        State = NetClientState.Connecting;
        peer = transport.Connect(host, port, data);
    }

    /// <summary>Shows this epoch in <paramref name="replica"/> (its <see cref="WorldSession.Mode"/> must be <see cref="SessionMode.Replica"/>) and makes this client its clock authority.</summary>
    public void Attach(WorldSession replica)
    {
        ArgumentNullException.ThrowIfNull(replica);
        if (replica.Mode != SessionMode.Replica) throw new ArgumentException("a client shows the host's world in a replica session", nameof(replica));
        session = replica;
        attached = true;
        replica.ClockAuthority = this;
    }

    /// <summary>The replica of this epoch is built: snapshots may start (<see cref="NetClientState.Playing"/>).</summary>
    public void Ready()
    {
        if (State != NetClientState.Joined) return;
        writer.Reset();
        ProtocolCodec.Write(writer, new Ready(Epoch));
        transport.Send(peer, NetChannel.Control, writer.Written);
        State = NetClientState.Playing;
    }

    /// <summary>The camera the host's interest and event filter use (sent in every <c>ClientFrame</c>).</summary>
    public void SetView(ViewState view) => this.view = view;

    /// <summary>Before the frame's simulation step: transport events, then (attached and playing) the snapshot on show, due events and the host's clock.</summary>
    public void BeginFrame(double realSeconds)
    {
        if (State is NetClientState.Idle or NetClientState.Closed) return;
        transport.Poll(listener);
        if (State == NetClientState.Closed) return;
        long now = clock.NowMicroseconds;
        if (mirror.TryAssemble(out var assembled) && assembled.Tick >= 0)
        {
            jitter.Add(assembled.Tick, now, assembled);
            lastSnapshotUs = now;
            LastAssembled = assembled;
        }
        if (attached && session is { } replica && State == NetClientState.Playing)
        {
            FollowRate();
            if (jitter.Advance(now, realSeconds) is { } p)
            {
                replica.Present(Unteleport(p.Older, p.Newer), p.Newer, p.Alpha, p.MotionAlpha);
                presentedTick = p.Tick;
                HasPresented = true;
            }
            events.Release(replica.World, double.IsNaN(presentedTick) ? double.MinValue : presentedTick, now);
            FollowClock(replica);
        }
        if (now - statsAtUs >= 1_000_000) UpdateStats(now);
    }

    /// <summary>After the frame: the commands the UI queued (sent while playing, else dropped) and a <c>ClientFrame</c> when parts arrived or every 100 ms.</summary>
    public void EndFrame()
    {
        if (State is NetClientState.Idle or NetClientState.Closed) return;
        if (attached && session is { } replica)
        {
            var due = replica.World.Commands.TakeDue(long.MaxValue);
            if (State == NetClientState.Playing && due.Count > 0) SendCommands(due);
        }
        long now = clock.NowMicroseconds;
        if (State == NetClientState.Playing && (mirror.HasNewParts || now - lastFrameUs >= FrameEverySeconds * 1e6))
        {
            writer.Reset();
            mirror.WriteFrame(writer, view);
            transport.Send(peer, NetChannel.Snapshots, writer.Written);
            lastFrameUs = now;
        }
    }

    /// <summary>Asks the host to post <paramref name="text"/> to everyone (it comes back as <see cref="Chat"/>).</summary>
    public void SendChat(string text)
    {
        if (State is not (NetClientState.Joined or NetClientState.Playing)) return;
        string line = NetHost.CleanChat(text);
        if (line.Length == 0) return;
        writer.Reset();
        ProtocolCodec.Write(writer, new ChatRequest(line));
        transport.Send(peer, NetChannel.Control, writer.Written);
    }

    /// <summary>Leaves on purpose: the host is told, <see cref="Closed"/> says <see cref="RejectCode.Left"/>; <see cref="Disconnected"/> is not raised.</summary>
    public void Leave()
    {
        if (State is NetClientState.Idle or NetClientState.Closed) return;
        writer.Reset();
        ProtocolCodec.Write(writer, new Reject(RejectCode.Left, LeftText));
        transport.Drop(peer, writer.Written);
        Close(new NetDisconnect(RejectCode.Left, LeftText), raise: false);
    }

    /// <summary>The local player's pause and speed input goes to the host, which decides.</summary>
    void IClockAuthority.Request(ClockRequest request)
    {
        if (State is not (NetClientState.Joined or NetClientState.Playing)) return;
        writer.Reset();
        ProtocolCodec.Write(writer, request);
        transport.Send(peer, NetChannel.Control, writer.Written);
    }

    /// <summary><see cref="Leave"/>, then the transport is disposed.</summary>
    public void Dispose()
    {
        Leave();
        transport.Dispose();
    }

    // ---- Receiving ----

    void OnReceived(NetConnectionId from, NetChannel channel, ReadOnlySpan<byte> data)
    {
        if (from != peer || State == NetClientState.Closed) return;
        try
        {
            switch (channel)
            {
                case NetChannel.Control:
                    Control(data);
                    break;
                case NetChannel.Snapshots:
                    if (State is NetClientState.Joined or NetClientState.Playing) mirror.Receive(NetChannel.Snapshots, data);
                    break;
                case NetChannel.Views:
                    if (State is not (NetClientState.Joined or NetClientState.Playing)) throw new NetFormatException("a view before the welcome");
                    mirror.Receive(NetChannel.Views, data);
                    break;
                case NetChannel.Events:
                    OnEvents(data);
                    break;
            }
        }
        catch (NetFormatException e)
        {
            Log?.Invoke($"net: bad data from the host: {e.Message}");
            writer.Reset();
            ProtocolCodec.Write(writer, new Reject(RejectCode.ProtocolError, BadData));
            transport.Drop(peer, writer.Written);
            Close(new NetDisconnect(RejectCode.ProtocolError, BadData), raise: true);
        }
    }

    void Control(ReadOnlySpan<byte> data)
    {
        var r = new NetReader(data);
        var type = ProtocolCodec.ReadType(ref r);
        if (State is NetClientState.Connecting or NetClientState.Authenticating && type is not (MessageType.Challenge or MessageType.Welcome))
            throw new NetFormatException($"{type} before the welcome");
        switch (type)
        {
            case MessageType.Challenge:
                OnChallenge(ProtocolCodec.ReadChallenge(ref r));
                break;
            case MessageType.Welcome:
                if (State != NetClientState.Authenticating) throw new NetFormatException("an unexpected welcome");
                var welcome = ProtocolCodec.ReadWelcome(ref r);
                Player = welcome.Player;
                Rules = welcome.Rules;
                Roster = welcome.Roster;
                State = NetClientState.Joined;
                RosterChanged?.Invoke();
                break;
            case MessageType.WorldReset:
                OnWorldReset(ProtocolCodec.ReadWorldReset(ref r));
                break;
            case MessageType.Roster:
                Roster = ProtocolCodec.ReadRoster(ref r);
                RosterChanged?.Invoke();
                break;
            case MessageType.ClockState:
                OnClock(ProtocolCodec.ReadClockState(ref r));
                break;
            case MessageType.Rules:
                var rules = ProtocolCodec.ReadRulesUpdate(ref r);
                Rules = rules.Net;
                SessionRules = rules.Session;
                RulesChanged?.Invoke(rules);
                break;
            case MessageType.Weather:
                Weather?.Invoke(ProtocolCodec.ReadWeather(ref r));
                break;
            case MessageType.CommandResult:
                var result = CommandBatchCodec.ReadResult(ref r);
                r.ExpectEnd();
                Message?.Invoke(result.Reason, true);
                break;
            case MessageType.Reject:
                var reject = ProtocolCodec.ReadReject(ref r);
                Close(new NetDisconnect(reject.Code, reject.Reason), raise: true);
                break;
            default:
                throw new NetFormatException($"the host does not send {type}");
        }
    }

    void OnChallenge(Challenge challenge)
    {
        if (State != NetClientState.Connecting) throw new NetFormatException("an unexpected challenge");
        if (challenge.NeedsPassword && password is null)
        {
            writer.Reset();
            ProtocolCodec.Write(writer, new Reject(RejectCode.Password, NeedsPasswordText));
            transport.Drop(peer, writer.Written);
            Close(new NetDisconnect(RejectCode.Password, NeedsPasswordText), raise: true);
            return;
        }
        writer.Reset();
        ProtocolCodec.Write(writer, new Hello(NetProtocol.Version, self.Build, self.PlayerId, self.Name, Handshake.Proof(password, challenge.Nonce, self.PlayerId),
            self.Data, Handshake.AnimationHash(catalog.Animations)));
        transport.Send(peer, NetChannel.Control, writer.Written);
        State = NetClientState.Authenticating;
    }

    void OnWorldReset(WorldReset reset)
    {
        if (State is not (NetClientState.Joined or NetClientState.Playing)) throw new NetFormatException("a world reset before the welcome");
        Epoch = reset.Epoch;
        Player = reset.Player;
        SessionRules = reset.Rules;
        clockState = reset.Clock;
        mirror.Reset(reset.Epoch);
        jitter.Clear();
        events.Clear();
        HasPresented = false;
        presentedTick = double.NaN;
        LastAssembled = null;
        teleportCache = null;
        attached = false;
        State = NetClientState.Joined;
        WorldReset?.Invoke(reset);
    }

    void OnClock(ClockState state)
    {
        if (state.Epoch != Epoch) return;
        clockState = state;
        if (state.Change.Player >= 0 && state.Change.Kind is ClockChangeKind.Paused or ClockChangeKind.Resumed or ClockChangeKind.Speed)
            ClockChanged?.Invoke(state.Change, NameOf(state.Change.Player));
    }

    void OnEvents(ReadOnlySpan<byte> data)
    {
        var r = new NetReader(data);
        var batch = EventCodec.Read(ref r);
        r.ExpectEnd();
        // Chat, notices, messages and knock-outs do not belong to a world: they are raised whatever the epoch.
        foreach (var e in batch.Events)
        {
            switch (e)
            {
                case ChatNet chat: Chat?.Invoke(chat); break;
                case NoticeNet notice: Notice?.Invoke(notice); break;
                case MessageNet message: Message?.Invoke(message.Text, false); break;
                case KnockoutNet knockout: Knockout?.Invoke(knockout); break;
            }
        }
        if (batch.Epoch == Epoch && State is NetClientState.Joined or NetClientState.Playing) events.Add(batch.Tick, clock.NowMicroseconds, batch.Events);
    }

    void OnDropped(NetConnectionId from, NetDropReason reason, ReadOnlySpan<byte> data)
    {
        if (from != peer || State == NetClientState.Closed) return;
        NetDisconnect end;
        if (data.Length > 0 && TryReadReject(data) is { } reject) end = new NetDisconnect(reject.Code, reject.Reason);
        else end = reason switch
        {
            NetDropReason.Error => new NetDisconnect(RejectCode.Unreachable, UnreachableText),
            NetDropReason.Timeout when !connected => new NetDisconnect(RejectCode.Unreachable, UnreachableText),
            _ => new NetDisconnect(RejectCode.Timeout, LostText),
        };
        Close(end, raise: true);
    }

    static Reject? TryReadReject(ReadOnlySpan<byte> data)
    {
        try
        {
            var r = new NetReader(data);
            return ProtocolCodec.ReadType(ref r) == MessageType.Reject ? ProtocolCodec.ReadReject(ref r) : null;
        }
        catch (NetFormatException)
        {
            return null;
        }
    }

    void Close(NetDisconnect end, bool raise)
    {
        State = NetClientState.Closed;
        Closed = end;
        attached = false;
        if (session is { } s && ReferenceEquals(s.ClockAuthority, this))
        {
            s.ClockAuthority = null;
            s.Simulation.RequestedPause = false;
        }
        if (raise) Disconnected?.Invoke(end);
    }

    // ---- Presentation and clock ----

    /// <summary>The pair's older snapshot with teleported characters replaced (<see cref="JitterBuffer.Unteleport"/>), cached per pair.</summary>
    WorldSnapshot Unteleport(WorldSnapshot older, WorldSnapshot newer)
    {
        if (teleportCache is { } c && ReferenceEquals(c.Older, older) && ReferenceEquals(c.Newer, newer)) return c.Shown;
        var shown = JitterBuffer.Unteleport(older, newer);
        teleportCache = (older, newer, shown);
        return shown;
    }

    void FollowRate()
    {
        if (clockState is not { } c) return;
        double rate = c.Speed == 0 || c.HostBusy ? 0 : JitterBuffer.TicksPerSecond * (c.AchievedSpeed > 0 ? c.AchievedSpeed : c.Speed);
        jitter.SetRate(rate, Rules?.SendRate ?? 20);
    }

    /// <summary>The replica's clocks follow the host: speed and pause for the HUD, and the game time carried from the anchor to the presented tick.</summary>
    void FollowClock(WorldSession replica)
    {
        if (clockState is not { } c) return;
        var sim = replica.Simulation;
        sim.SetSpeed(Array.IndexOf(SimulationClock.Speeds, c.LastSpeed) >= 0 ? c.LastSpeed : 1);
        if (c.Speed == 0) sim.SetSpeed(0);
        sim.RequestedPause = c.HostBusy;
        if (double.IsNaN(presentedTick)) return;
        var game = replica.Clock;
        double hours = c.AnchorHours + (presentedTick - c.AnchorTick) / JitterBuffer.TicksPerSecond / GameClock.SecondsPerGameHour;
        double seconds = (hours - game.TotalHours) * GameClock.SecondsPerGameHour;
        if (seconds is >= 0 and < 3600) game.Advance(seconds);
        else if (!(seconds is > -1 and < 0)) game.SetTime((long)Math.Floor(hours / 24), hours - Math.Floor(hours / 24) * 24);
        replica.World.GameHours = game.TotalHours;
    }

    // ---- Uplink ----

    void SendCommands(IReadOnlyList<SimCommand> due)
    {
        outgoing.Clear();
        bool cheats = Rules?.Cheats == true;
        foreach (var command in due)
        {
            ushort tag;
            try { tag = CommandTags.TagOf(command.GetType()); }
            catch (ArgumentException) { continue; }   // a local-only command
            var origin = CommandTags.Of(tag).Origin;
            if (origin == CommandOrigin.Client || (origin == CommandOrigin.Debug && cheats)) outgoing.Add(command);
        }
        for (int start = 0; start < outgoing.Count;)
        {
            int count = Math.Min(NetLimits.MaxCommandBatch, outgoing.Count - start);
            while (true)
            {
                writer.Reset();
                writer.WriteByte((byte)MessageType.CommandBatch);
                try
                {
                    CommandBatchCodec.Write(writer, commandStrings, new CommandBatch(Epoch, nextCommandSeq, outgoing.GetRange(start, count)));
                    transport.Send(peer, NetChannel.Control, writer.Written);
                    nextCommandSeq += (uint)count;
                    break;
                }
                catch (ArgumentException e) when (count > 1)
                {
                    count /= 2;
                    Log?.Invoke($"net: a command batch was split ({e.Message})");
                }
                catch (ArgumentException e)
                {
                    Log?.Invoke($"net: {outgoing[start].GetType().Name} was not sent: {e.Message}");
                    break;
                }
            }
            start += count;
        }
    }

    void UpdateStats(long now)
    {
        if (connected)
        {
            var link = transport.Stats(peer);
            double seconds = Math.Max(1e-3, (now - statsAtUs) / 1e6);
            outPerSecond = (int)((link.BytesSent - bytesSentAt) / seconds);
            inPerSecond = (int)((link.BytesReceived - bytesReceivedAt) / seconds);
            bytesSentAt = link.BytesSent;
            bytesReceivedAt = link.BytesReceived;
        }
        statsAtUs = now;
    }

    string NameOf(int player)
    {
        foreach (var e in Roster)
            if (e.Player == player) return e.Name;
        return $"Player {player}";
    }
}
