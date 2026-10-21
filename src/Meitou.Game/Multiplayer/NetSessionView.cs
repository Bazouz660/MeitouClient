using Meitou.Net;
using Meitou.Net.Events;
using Meitou.Net.Session;

namespace Meitou.Game;

/// <summary>
/// The session as the interface reads it (<see cref="IMultiplayerView"/>, docs/meitou-ui.md "Multiplayer"), over the game's <see cref="NetHost"/> or
/// <see cref="NetClient"/>: roster, rules, link quality, who paused, and the joins, leaves, clock changes and chat lines as <see cref="MultiplayerEvent"/>s.
/// Frame thread only.
/// </summary>
sealed class NetSessionView : IMultiplayerView
{
    readonly NetHost? host;
    readonly NetClient? client;
    readonly List<MultiplayerEvent> events = [];
    NetDisconnect? ended;

    public NetSessionView(NetHost host)
    {
        this.host = host;
        host.Notice += OnNotice;
        host.Chat += OnChat;
        host.ClockChanged += OnClock;
    }

    public NetSessionView(NetClient client)
    {
        this.client = client;
        client.Notice += OnNotice;
        client.Chat += OnChat;
        client.ClockChanged += OnClock;
        client.Disconnected += end => ended = end;
    }

    public NetRole Role => host is not null ? NetRole.Host : NetRole.Client;

    public NetPhase Phase
    {
        get
        {
            if (Ended is not null) return NetPhase.Ended;
            if (host is not null) return NetPhase.Playing;
            return client!.State switch
            {
                NetClientState.Idle => NetPhase.Idle,
                NetClientState.Connecting => NetPhase.Connecting,
                NetClientState.Authenticating => NetPhase.Joining,
                NetClientState.Joined => NetPhase.Loading,
                NetClientState.Playing => Loaded ? NetPhase.Playing : NetPhase.Loading,
                _ => NetPhase.Ended,
            };
        }
    }

    /// <summary>The client's world is on show (the game sets it when its join's last loading stage is done).</summary>
    public bool Loaded { get; set; }

    public int LocalPlayer => host is not null ? 0 : Math.Max(0, client!.Player);

    public IReadOnlyList<RosterEntry> Players => host?.Roster ?? client!.Roster;

    public NetRules Rules => host?.Rules ?? client!.Rules ?? new NetRules(20, ClockPolicy.AnyPlayer, MultiplayerDefaults.MaxPlayers, false, false, NetProtocol.DefaultPort);

    public NetQuality Quality
    {
        get
        {
            if (client is null) return default;
            var s = client.Stats;
            return new NetQuality(s.RoundTripMs, Math.Max(0, s.SnapshotAgeSeconds), s.LossPercent / 100f);
        }
    }

    /// <summary>Why the session ended: the client's disconnect, or what the game set when it stopped hosting.</summary>
    public NetDisconnect? Ended
    {
        get => ended ?? client?.Closed;
        set => ended = value;
    }

    public int? PausedBy { get; private set; }

    public bool CanControlTime(int player) => Rules.ClockPolicy == ClockPolicy.AnyPlayer || player == 0;

    public IReadOnlyList<MultiplayerEvent> TakeEvents()
    {
        if (events.Count == 0) return [];
        MultiplayerEvent[] all = [.. events];
        events.Clear();
        return all;
    }

    public void SendChat(string text)
    {
        if (host is not null) host.SendChat(text);
        else client!.SendChat(text);
    }

    public void Kick(int player, string reason) => host?.Kick(player, reason);

    public void Ban(int player) => host?.Ban(player, "");

    void OnNotice(NoticeNet n)
    {
        var kind = n.Kind switch
        {
            NoticeKind.Joined => MultiplayerEventKind.Joined,
            NoticeKind.TimedOut => MultiplayerEventKind.TimedOut,
            NoticeKind.Kicked => MultiplayerEventKind.Kicked,
            NoticeKind.Banned => MultiplayerEventKind.Banned,
            _ => MultiplayerEventKind.Left,
        };
        events.Add(new MultiplayerEvent(kind, n.Player, n.Name, n.Detail));
    }

    void OnChat(ChatNet c) => events.Add(new MultiplayerEvent(MultiplayerEventKind.Chat, c.From, c.Name, c.Text));

    void OnClock(ClockChange change, string name)
    {
        switch (change.Kind)
        {
            case ClockChangeKind.Paused:
                PausedBy = change.Player;
                events.Add(new MultiplayerEvent(MultiplayerEventKind.Paused, change.Player, name));
                break;
            case ClockChangeKind.Resumed:
                PausedBy = null;
                events.Add(new MultiplayerEvent(MultiplayerEventKind.Resumed, change.Player, name, Speed: change.Speed));
                break;
            case ClockChangeKind.Speed:
                events.Add(new MultiplayerEvent(MultiplayerEventKind.Speed, change.Player, name, Speed: change.Speed));
                break;
        }
    }
}
