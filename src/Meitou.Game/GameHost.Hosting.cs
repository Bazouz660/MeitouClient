using System.Net.Sockets;
using Meitou.Net.Events;
using Meitou.Net.Replication;
using Meitou.Net.Session;
using Meitou.Net.Transport;
using Meitou.Simulation;

namespace Meitou.Game;

/// <summary>
/// Host mode (docs/engine.md "Multiplayer", Host): the running game, a new game or a save opened to other players. A request waits until the game is
/// ready to host (<see cref="HostReady"/>), then a <see cref="NetHost"/> on UDP takes the session's clock; stopping (or the main menu, or quitting)
/// drops every client and the game goes on single-player.
/// </summary>
sealed partial class GameHost
{
    /// <summary>Hosting asked for, waiting for the game (a new game or a load first).</summary>
    HostSettings? hostPending;
    /// <summary>Set by the save worker: the next frame tells the clients the host saved.</summary>
    volatile bool savedForClients;

    /// <summary>What the net host reads of the game: the gate's options, the simulation rules and the weather.</summary>
    sealed class HostGameLink(GameHost game, HostSettings settings) : IHostGame
    {
        WeatherSync? weather;
        int weatherRevision = -1;
        long weatherAt;

        public GateOptions Gate { get; } = new() { Cheats = settings.AllowCheats, Placeable = game.player.PlanPlaceable };

        public SessionRules Rules => game.CurrentRules();

        /// <summary>The weather state, rebuilt when its revision moves or once a second (the host asks every frame).</summary>
        public WeatherSync? Weather()
        {
            if (game.gpu.Weather is not { } w) return null;
            long now = Environment.TickCount64;
            if (weather is null || w.Revision != weatherRevision || now - weatherAt >= 1000)
            {
                var clock = game.session.Clock;
                weather = new WeatherSync(w.Revision, w.World.ForcedWeather?.Name, (int)clock.Day, clock.HourOfDay, w.World.Snapshot());
                weatherRevision = w.Revision;
                weatherAt = now;
            }
            return weather;
        }
    }

    /// <summary>The simulation rules of this game's settings, which a host's clients use in place of their own.</summary>
    SessionRules CurrentRules() => new(Math.Clamp(config.AttackSlots, 1, 5), config.LateDefences, config.DoorsCloseBehind, config.KeepRoamers, config.OffscreenFights,
        config.OffscreenHealing, config.CampaignAncestorsOnce, config.KeepWild, config.LuckyrenMode, config.ResidentSchedule, config.GroundItemLimit);

    /// <summary>Whether a game runs that may be hosted: shown, the player's own squad made (its editor closed), not the duel or sandbox.</summary>
    bool HostReady => !Covered && sceneLoad is null && session.World.Player.Exists && population is not null && !player.EditorOpen && pendingCreation is null
        && !creationWarmOwed && !IsDuel && !IsSandbox && session.Mode == Meitou.Engine.SessionMode.Local;

    partial void StartHostingCore(HostSettings settings, string? newGameStart, string? saveFolder)
    {
        if (role != NetRole.None) { player.Notice("Already in a multiplayer game.", true); return; }
        hostPending = settings;
        if (newGameStart is not null) newGameRequested = newGameStart;
        else if (saveFolder is not null) loadRequestedFolder = saveFolder;
        else if (!HostReady && !Covered) { hostPending = null; player.Notice("Start or load a game first, then open it to multiplayer.", true); }
    }

    /// <summary>Every frame: starts the hosting asked for once the game is ready; tells the clients about a finished save.</summary>
    partial void HostFrameCore()
    {
        if (savedForClients)
        {
            savedForClients = false;
            netHost?.Send(-1, new MessageNet("The host saved the game."));
        }
        if (hostPending is { } settings && role == NetRole.None && HostReady)
        {
            hostPending = null;
            StartHosting(settings);
        }
    }

    void StartHosting(HostSettings settings)
    {
        if (scene.Database is not { } db || animationLibrary is not { } animations || popData?.Appearances is not { } looks) { player.Notice("This game cannot be hosted.", true); return; }
        var identity = NetIdentity();
        var options = new NetHostOptions
        {
            Port = settings.Port, MaxPlayers = settings.MaxPlayers, Password = settings.Password.Length == 0 ? null : settings.Password, HostName = identity.Name,
            ClockPolicy = settings.ClientsControlTime ? ClockPolicy.AnyPlayer : ClockPolicy.HostOnly,
        };
        var host = new NetHost(session, new LiteNetTransport(), options, identity, new HostGameLink(this, settings), new ReplicaCatalog(db, animations, looks));
        host.Log = line => Console.WriteLine($"net       {line}");
        try
        {
            host.Start();
        }
        catch (SocketException e)
        {
            host.Dispose();
            Console.Error.WriteLine($"net       cannot host on port {settings.Port}: {e.Message}");
            player.Notice($"Cannot host on port {settings.Port}: {e.Message}", true);
            return;
        }
        host.Notice += n => Console.WriteLine($"net       {n.Kind} player {n.Player} {n.Name} {n.Detail}");
        EnterNet(host);
        (config.HostPort, config.HostMaxPlayers, config.HostClientsControlTime, config.HostAllowCheats) = (settings.Port, settings.MaxPlayers, settings.ClientsControlTime, settings.AllowCheats);
        config.Save();
        Console.WriteLine($"net       hosting on UDP port {host.Rules.Port}: up to {host.Rules.MaxPlayers - 1} more players");
        player.Notice($"Hosting on UDP port {host.Rules.Port}: up to {host.Rules.MaxPlayers - 1} more players");
    }

    partial void StopHostingCore(string reason)
    {
        hostPending = null;
        if (netHost is not { } host) return;
        host.Stop(reason);
        host.Dispose();
        if (netView is { } view) view.Ended = new NetDisconnect(RejectCode.HostClosed, reason);
        Console.WriteLine($"net       stopped hosting: {reason}");
        ExitNet();
    }
}
