using System.Diagnostics;
using System.Numerics;
using Meitou.Engine;
using Meitou.Net.Replication;
using Meitou.Net.Session;
using Meitou.Net.Transport;
using Meitou.Simulation;

namespace Meitou.Game;

/// <summary>
/// Client mode (docs/engine.md "Multiplayer", Client): this game shows a host's world in a replica session (no systems, never ticked), sends the
/// player's commands and clock input to the host and follows its clock, relations and character creation. Connecting and the handshake run behind the
/// Multiplayer window; each <c>WorldReset</c> (the join, then every host world swap) builds the replica under the loading screen.
/// </summary>
sealed partial class GameHost
{
    /// <summary>How long the host may take from the connection to the first <c>WorldReset</c>.</summary>
    const double HostAnswerSeconds = 60;

    /// <summary>The newest world the host announced, and the epoch of the replica built from it (-1 none).</summary>
    WorldReset? clientReset;
    int builtEpoch = -1;
    /// <summary>The ending the client reported, handled at the frame's end (not from inside the transport's poll).</summary>
    NetDisconnect? clientEnded;
    long joinStarted;
    TaskCompletionSource? presented;
    int creationAsked = -1;
    (int Epoch, long Revision) relationsShown = (-1, -1);
    /// <summary>The client data's relations of every faction towards the player faction, as they were before the host's row replaced them.</summary>
    (int Faction, float[] Column)? relationsSaved;
    /// <summary>A failed join's view, kept for the Multiplayer window to read its reason, then cleared.</summary>
    int endedViewFrames;

    partial void BeginJoinCore(JoinGame target)
    {
        if (role != NetRole.None) { player.Notice("Already in a multiplayer game.", true); return; }
        if (sceneLoad is not null || scene.Database is not { } db || animationLibrary is not { } animations || popData?.Appearances is not { } looks)
        {
            player.Notice("Cannot join now.", true);
            return;
        }
        var client = new NetClient(new LiteNetTransport(), NetIdentity(), new ReplicaCatalog(db, animations, looks)) { Log = line => Console.WriteLine($"net       {line}") };
        client.WorldReset += reset => clientReset = reset;
        client.Disconnected += end => clientEnded = end;
        client.Message += (text, error) =>
        {
            Console.WriteLine($"net       {text}");
            player.Notice(text, error);
        };
        client.Knockout += k =>
        {
            string name = session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == k.Entry.Attacker)?.Name ?? "?";
            player.Notice(KnockoutText(name, k.Entry), !k.Entry.Success);
        };
        client.Weather += ApplyHostWeather;
        string address = target.Host.Contains(':') ? $"[{target.Host}]:{target.Port}" : $"{target.Host}:{target.Port}";
        (clientReset, builtEpoch, clientEnded, presented, creationAsked, relationsShown, endedViewFrames) = (null, -1, null, null, -1, (-1, -1), 0);
        joinStarted = Stopwatch.GetTimestamp();
        EnterNet(client, address);
        Console.WriteLine($"net       joining {address}");
        client.Connect(target.Host, target.Port, target.Password.Length == 0 ? null : target.Password);
    }

    /// <summary>Every frame of a client: an ending, a new host world, the presented snapshot, character creation, relations and the camera view.</summary>
    partial void ClientFrameCore()
    {
        if (endedViewFrames > 0 && --endedViewFrames == 0 && role == NetRole.None && player.Net is NetSessionView { Phase: NetPhase.Ended }) player.Net = null;
        if (netClient is not { } client) return;
        if (clientEnded is { } end)
        {
            clientEnded = null;
            Console.WriteLine($"net       disconnected: {end.Code} {end.Reason}");
            LeaveClientCore(end, quit: false);
            return;
        }
        if (clientReset is null && Stopwatch.GetElapsedTime(joinStarted).TotalSeconds > HostAnswerSeconds)
        {
            client.Leave();
            LeaveClientCore(new NetDisconnect(RejectCode.Timeout, "The host did not send its world."), quit: false);
            return;
        }
        if (clientReset is { } reset && reset.Epoch != builtEpoch && sceneLoad is null) LoadHostWorld();
        if (client.HasPresented) presented?.TrySetResult();
        if (session.Mode != SessionMode.Replica || builtEpoch < 0) return;

        var snapshot = session.CurrentSnapshot;
        FollowHostRelations(snapshot);
        // A new player's start squad: the editor opens on it (its edits and the finish travel as commands).
        if (snapshot.InCreation && !player.EditorOpen && pendingCreation is null && creationAsked != worldEpoch && sceneLoad is null && !warming
            && snapshot.PlayerSquads.FirstOrDefault(s => s.Id == snapshot.ActiveSquad) is { Members.Count: > 0 } squad
            && squad.Members.All(id => snapshot.Characters.Any(c => c.Id == id)))
        {
            pendingCreation = [.. squad.Members];
            creationAsked = worldEpoch;
        }
        client.SetView(new ViewState(camera.Eye, camera.Target, gpu.Characters?.DrawDistance ?? 6000));
    }

    /// <summary>The host's world under the loading screen: the replica built, the first snapshot shown, then the menus closed and the camera on the squad.</summary>
    void LoadHostWorld()
    {
        presented = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool first = builtEpoch < 0;
        StartSceneLoad(player.TitleOpen,
            Stage("world", () => { BuildReplica(clientReset!); return null; }),
            // The first snapshot; after a minute the load goes on and the world shows when one comes.
            Stage("people", () => Task.WhenAny(presented.Task, Task.Delay(TimeSpan.FromSeconds(HostAnswerSeconds)))),
            Stage("finishing", () =>
            {
                SettleNewWorld();
                player.FollowSelected();
                if (first) FinishJoin();
                return null;
            }));
    }

    /// <summary>A replica of the host's world <paramref name="reset"/> in place of this game's: no population, the clock, camera, weather and blood of the new world.</summary>
    void BuildReplica(WorldReset reset)
    {
        var client = netClient!;
        session.ReplaceWorld([], world => world.MakeReplica(reset.Seed), SessionMode.Replica);
        population = null;
        aggro = null;
        playerSquad = null;
        loadedSave = null;
        preparedBase = null;
        pendingCreation = null;
        creationWarmOwed = false;
        worldEpoch++;
        builtEpoch = reset.Epoch;
        if (nav is not null)
        {
            foreach (var z in navZones.Keys) nav.UnloadZone(z);
            navZones.Clear();
        }
        double hours = reset.Clock.AnchorHours;
        session.Clock.SetTime((long)Math.Floor(hours / 24), hours - Math.Floor(hours / 24) * 24);
        var view = session.Camera.Current;
        session.Camera.Place(new Vector2(reset.Focus.X, reset.Focus.Z), view.Yaw, view.Pitch, view.Distance);
        if (reset.Weather is { } weather) ApplyHostWeather(weather);
        BloodForNewWorld(null);
        relationsShown = (-1, -1);
        client.Attach(session);
        client.Ready();
        Console.WriteLine($"net       host world {reset.Epoch}: player {reset.Player}, seed {reset.Seed}");
    }

    void FinishJoin()
    {
        if (netView is { } view) view.Loaded = true;
        string host = netClient?.Roster.FirstOrDefault(e => e.State == NetPlayerState.Host)?.Name ?? "the host";
        if (netAddress is { } address)
        {
            config.RememberGame(address, host, DateTime.UtcNow);
            config.Save();
        }
        player.Notice($"Joined {host}'s game");
    }

    /// <summary>The host's weather: its regions continued from the day and hour it sent, and its forced weather.</summary>
    void ApplyHostWeather(WeatherSync weather)
    {
        if (gpu.Weather is not { } w) return;
        if (weather.Regions.Count > 0) w.Restore(weather.Regions, weather.Day, weather.Hour);
        if (weather.Forced != w.World.ForcedWeather?.Name) w.World.ForceWeather(weather.Forced);
    }

    /// <summary>
    /// The host's relations of the player faction towards each faction into the client data's table (the other factions' entries towards the player, which
    /// the player's row is read from), so colours, standings and turret targets agree with the host's. NPC-to-NPC relations stay the data's.
    /// </summary>
    void FollowHostRelations(WorldSnapshot snapshot)
    {
        if ((worldEpoch, snapshot.RelationsRevision) == relationsShown || popData?.Relations is not { } table) return;
        relationsShown = (worldEpoch, snapshot.RelationsRevision);
        int p = snapshot.PlayerFaction;
        var row = snapshot.Relations;
        if (!table.Contains(p) || row.Count != table.Count || table.PlayerFaction != p)
        {
            Console.Error.WriteLine($"net       host relations not shown: player faction {p} (here {table.PlayerFaction}), {row.Count} of {table.Count} factions");
            return;
        }
        if (relationsSaved is null)
        {
            var column = new float[table.Count];
            for (int f = 0; f < table.Count; f++) column[f] = table.Get(p, f);
            relationsSaved = (p, column);
        }
        for (int f = 0; f < table.Count; f++)
            if (f != p) table.Set(f, p, row[f]);
    }

    partial void LeaveClientCore(NetDisconnect reason, bool quit)
    {
        if (netClient is not { } client) return;
        bool reachedPlay = netView?.Loaded == true;
        bool built = session.Mode == SessionMode.Replica;
        client.Dispose();   // tells the host when still connected
        (sceneLoad, skyArrival, presentationHour, warming, pendingCreation, creationWarmOwed) = (null, null, null, false, null, false);
        (clientReset, clientEnded, presented) = (null, null, null);
        builtEpoch = -1;
        if (relationsSaved is { } saved && popData?.Relations is { } table)
            for (int f = 0; f < table.Count; f++)
                if (f != saved.Faction) table.Set(f, saved.Faction, saved.Column[f]);
        relationsSaved = null;
        session.ClockAuthority = null;
        session.Simulation.RequestedPause = false;
        session.Simulation.SetSpeed(1);
        var view = netView;
        ExitNet();
        if (quit) return;
        if (!built)
        {
            // Still on the title: the Multiplayer window reads the reason from the ended view for a frame or two.
            if (reason.Code != RejectCode.Left && view is not null)
            {
                player.Notice(reason.Reason, true);
                view.Ended = reason;
                player.Net = view;
                endedViewFrames = 3;
            }
            return;
        }
        StartSceneLoad(mainMenuTransition: true, Stage("build", () =>
        {
            var systems = BuildSystems(interactive: true);
            session.ReplaceWorld(systems.Systems);
            population = systems.Population; aggro = systems.Aggro; playerSquad = null;
            if (nav is not null) population!.ZoneGate = zone => nav.Walkability.World.Contains(zone);
            loadedSave = null; preparedBase = null; worldEpoch++;
            SettleNewWorld();
            player.ShowMainMenu();
            AudioMainMenu();
            if (reason.Code != RejectCode.Left) player.ShowDisconnected(reason, reachedPlay);
            return null;
        }));
    }
}
