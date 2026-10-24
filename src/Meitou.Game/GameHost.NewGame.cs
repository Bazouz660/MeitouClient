using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Simulation;

namespace Meitou.Game;

sealed partial class GameHost
{
    string? newGameRequested;
    bool mainMenuRequested;

    /// <summary>
    /// Leaves the game for the main menu: a new world with the town population and no player squad (what a run without a start has behind the title), the camera where it is;
    /// the faithful mode then shows the title screen, the Meitou mode its game menu. Under the loading screen (GameHost.Transition.cs).
    /// </summary>
    void HandleMainMenu()
    {
        if (!mainMenuRequested) return;
        mainMenuRequested = false;
        if (!SavesAvailable(out string why) || Busy()) { player.Notice(why.Length > 0 ? why : "Save or load still in progress", true); return; }
        StartSceneLoad(mainMenuTransition: true, Stage("build", () =>
        {
            var built = BuildSystems(interactive: true);
            session.ReplaceWorld(built.Systems);
            population = built.Population; aggro = built.Aggro; playerSquad = null;
            if (nav is not null) population!.ZoneGate = zone => nav.Walkability.World.Contains(zone);
            loadedSave = null; preparedBase = null; worldEpoch++;
            SettleNewWorld();
            DisposeTitleScene(); // re-read the latest Continue save after the campaign or its character appearances changed
            player.ShowMainMenu();
            AudioMainMenu();
            return null;
        }));
    }

    /// <summary>A new game of the chosen start, under the loading screen: the systems and the navmesh round the start first, then the world with the player's squad.</summary>
    void HandleNewGame()
    {
        if (newGameRequested is not { } id) return;
        newGameRequested = null;
        if (!SavesAvailable(out string why) || Busy()) { player.Notice(why.Length > 0 ? why : "Save or load still in progress", true); return; }
        if (scene.Database is not { } database || NewGameStart.Find(NewGameStart.LoadAll(database), id) is not { } chosen) return;
        StandardSystemSet? built = null;
        var nextDifficulty = player.NewGameOptions;
        var nextData = nextDifficulty.Apply(popData!);
        // A fresh seed per game, as the original rolls its names and squads anew each time (docs/characters.md "Names"); --seed keeps one.
        ulong seed = g.SeedGiven ? g.Seed : (ulong)Random.Shared.NextInt64(1, long.MaxValue);
        AudioGameStarts();
        StartSceneLoad(player.TitleOpen,
            Stage("build", () =>
            {
                built = BuildSystems(interactive: true, nextData, nextDifficulty);
                // The navmesh round the place the squad will stand, drawn with the same seed as StartPlayer.
                var at = PopulationSystem.StartPlace(nextData, seed, chosen).Centre;
                return NavmeshAround(new(at.X, 0, at.Y));
            }, duringAscent: true),
            Stage("people", () =>
            {
                Squad? spawned = null;
                try { session.ReplaceWorld(built!.Systems, world => { world.RestoreSeed(seed); spawned = built.Population!.StartPlayer(world, chosen); }); }
                catch (Exception e) { Console.Error.WriteLine($"new game  {e}"); throw new SceneLoadFailed($"New game failed: {e.Message}"); }
                popData = nextData; difficulty = nextDifficulty;
                population = built.Population; aggro = built.Aggro; playerSquad = spawned;
                if (nav is not null) population!.ZoneGate = zone => nav.Walkability.World.Contains(zone);
                loadedSave = null; preparedBase = null; worldEpoch++;
                session.Clock.SetTime(1, 13);
                var lead = session.World.Characters.Previous[spawned!.Leader.Slot].Position;
                var view = session.Camera.Current;
                session.Camera.Place(new Vector2(lead.X, lead.Z), view.Yaw, view.Pitch, view.Distance);
                SettleNewWorld();
                player.Notice($"Started {chosen.Name}");
                pendingCreation = [.. spawned.Members];   // the start's characters are made in the editor first (docs/characters.md "Character editor")
                return null;
            }));
    }
}
