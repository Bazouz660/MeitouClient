using Meitou.Data;
using Meitou.Net;
using Meitou.Net.Session;

namespace Meitou.Game;

/// <summary>
/// The game's net plumbing (docs/engine.md "Multiplayer"): the role, the host or client object and the session view the interface reads, the two
/// pump points of the frame loop and the dispatch of the interface's multiplayer requests to the hosting (G2) and client (G3) parts. In single-player
/// <see cref="role"/> is <see cref="NetRole.None"/>, no net object exists and every pump is a null check.
/// </summary>
sealed partial class GameHost
{
    NetRole role;
    NetHost? netHost;
    NetClient? netClient;
    NetSessionView? netView;
    /// <summary>The address a client joined, for the window title.</summary>
    string? netAddress;
    Task<DataSetIdentity>? dataIdentity;

    /// <summary><c>--host</c>: host with these settings as soon as the game is ready (G4).</summary>
    public HostSettings? HostOnStart { get; init; }
    /// <summary><c>--join</c>: join this game from the title (G4).</summary>
    public JoinGame? JoinOnStart { get; init; }
    /// <summary><c>--name</c>: the player name for this run, over the config's.</summary>
    public string? NameOverride { get; init; }

    /// <summary>Starts hashing the data set on a worker (once); <see cref="NetIdentity"/> waits for it.</summary>
    void PrepareNetIdentity() => dataIdentity ??= Task.Run(() => DataSetIdentity.Compute(LoadOrder.FromInstall(install)));

    /// <summary>Who this game is to the other side: the config's player id (made and saved on first use), the sanitised name, the build and the data set.</summary>
    NetIdentity NetIdentity()
    {
        bool fresh = config.PlayerId == Guid.Empty;
        var id = config.EnsurePlayerId();
        if (fresh) config.Save();
        PrepareNetIdentity();
        string name = Handshake.SanitizeName(NameOverride ?? config.PlayerName ?? Environment.UserName, 0);
        return new NetIdentity(id, name, $"{BuildVersion.Tag} ({BuildVersion.Commit})", dataIdentity!.GetAwaiter().GetResult());
    }

    /// <summary>Immediately before <c>session.Advance</c>: the transport's events (also while covered), the client's presentation and clock.</summary>
    void NetBeginFrame(double dt)
    {
        netHost?.BeginFrame();
        netClient?.BeginFrame(dt);
    }

    /// <summary>After <c>session.Advance</c> and <see cref="WorldNews"/>, before a save or load can swap the world: the host's sends, the client's uplink.</summary>
    void NetEndFrame()
    {
        netHost?.EndFrame(Covered);
        netClient?.EndFrame();
        HostFrameCore();
        ClientFrameCore();
    }

    /// <summary>
    /// Handles what the interface asks (<see cref="PlayerInterface.MultiplayerRequested"/>); true when the application should quit.
    /// </summary>
    bool OnMultiplayerRequest(MultiplayerRequest request)
    {
        PrepareNetIdentity();
        switch (request)
        {
            case HostGame h:
                StartHostingCore(h.Settings, h.NewGameStart, h.SaveFolder);
                return false;
            case JoinGame j:
                BeginJoinCore(j);
                return false;
            case CancelJoin:
                LeaveClientCore(new NetDisconnect(RejectCode.Left, NetClient.LeftText), false);
                return false;
            case StopHosting:
                StopHostingCore("The host stopped hosting.");
                return false;
            case LeaveGame leave:
                if (role == NetRole.Client) LeaveClientCore(new NetDisconnect(RejectCode.Left, NetClient.LeftText), leave.Quit);
                else if (role == NetRole.Host) StopHostingCore(NetHost.ClosedText);
                return leave.Quit;
            default:
                return false;
        }
    }

    /// <summary>The game now hosts through <paramref name="host"/> (started): the role, the view the interface reads, the modal pause recomputed.</summary>
    void EnterNet(NetHost host)
    {
        netHost = host;
        EnterNet(NetRole.Host, new NetSessionView(host));
    }

    /// <summary>The game now is a client of <paramref name="address"/> through <paramref name="client"/> (connecting).</summary>
    void EnterNet(NetClient client, string address)
    {
        netClient = client;
        netAddress = address;
        EnterNet(NetRole.Client, new NetSessionView(client));
    }

    void EnterNet(NetRole role, NetSessionView view)
    {
        this.role = role;
        netView = view;
        player.Net = view;
        player.RefreshMenuPause();
    }

    /// <summary>The application is closing: every client is told the host closed, or the host that this client left.</summary>
    void ShutdownNet()
    {
        if (role == NetRole.Host) StopHostingCore("The host closed the game.");
        else if (role == NetRole.Client) LeaveClientCore(new NetDisconnect(RejectCode.Left, NetClient.LeftText), quit: true);
    }

    /// <summary>Back to single-player: the net objects are forgotten (the caller stopped or disposed them), the interface's view cleared.</summary>
    void ExitNet()
    {
        role = NetRole.None;
        netHost = null;
        netClient = null;
        netView = null;
        netAddress = null;
        player.Net = null;
        player.RefreshMenuPause();
    }

    /// <summary>The F11 statistics line of the session; null in single-player.</summary>
    string? NetStatsLine()
    {
        if (netHost is { } host) return host.StatsLine();
        if (netClient is { } client)
        {
            var s = client.Stats;
            return string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"client of {netAddress} as player {client.Player} ({client.State}): rtt {s.RoundTripMs} ms, loss {s.LossPercent:0.0}%, in {s.BytesInPerSecond / 1024.0:0.0} KB/s, out {s.BytesOutPerSecond / 1024.0:0.0} KB/s, delay {s.DelayMs} ms, starved {s.Starved}, snapshot age {s.SnapshotAgeSeconds:0.00} s");
        }
        return null;
    }

    /// <summary>The window title's session part: <c> | host, N players</c> or <c> | client of address</c>; empty in single-player.</summary>
    string NetTitle() => role switch
    {
        NetRole.Host when netHost is { } host => $" | host, {host.Roster.Count(e => e.State != NetPlayerState.Away)} players",
        NetRole.Client => $" | client of {netAddress}",
        _ => "",
    };

    /// <summary>G2: host the running game, a new game or a save; a port that cannot be bound leaves the game single-player with an error notice.</summary>
    partial void StartHostingCore(HostSettings settings, string? newGameStart, string? saveFolder);
    /// <summary>G2: start the hosting asked for once the game is ready; tell the clients about a finished save.</summary>
    partial void HostFrameCore();
    /// <summary>G2: drop every client with <paramref name="reason"/>; the game goes on single-player.</summary>
    partial void StopHostingCore(string reason);
    /// <summary>G3: connect, wait for the host's world, build the replica.</summary>
    partial void BeginJoinCore(JoinGame target);
    /// <summary>G3: leave the host's game (or cancel a join) and go back to the title, or quit.</summary>
    partial void LeaveClientCore(NetDisconnect reason, bool quit);
    /// <summary>G3: a client's per-frame follow-up (relations row, character creation, the camera view sent to the host).</summary>
    partial void ClientFrameCore();
}
