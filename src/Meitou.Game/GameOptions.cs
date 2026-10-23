using System.Globalization;

namespace Meitou.Game;

/// <summary>Game-only options; everything else is the world options shared with the viewer (<see cref="WorldOptions"/>).</summary>
sealed class GameOptions
{
    public bool TitlePreview;
    public int? FpsLimit;
    public ulong Seed;
    /// <summary><c>--seed</c> was given: a new game from the menu keeps it instead of rolling a fresh one.</summary>
    public bool SeedGiven;
    public bool? VSync;
    public Meitou.Data.Ui.UiMode? UiMode;
    public bool UiShowcase;
    /// <summary>With <c>--screenshot</c>: control ticks (and their real time of simulation, at speed 1) run before the picture, with no input.</summary>
    public int Ticks;
    public bool NoPopulation, NewGame, ListStarts;
    /// <summary>The animation sandbox (<c>--sandbox [n]</c>): 0 off, else the number of characters (1 or 2) on a flat wireframe floor.</summary>
    public int Sandbox;
    /// <summary>The CHARACTER record (name or id) the sandbox spawns (null: the default new-game start's first squad member).</summary>
    public string? SandboxCharacter;
    /// <summary>The sandbox hero's start state, comma-separated: walk, run or free; a fixed speed in units/s; patrol (a 400-unit line), for pictures.</summary>
    public string? SandboxMode;
    /// <summary><c>--sandbox-races</c>: one character of every person race (a CHARACTER record listing only that race) in rings round the pair, whatever n is.</summary>
    public bool SandboxRaces;
    /// <summary>The looping duel for trailers (<c>--duel [x,z]</c>, docs/simulation.md "Duel"): two Samurai Heavy fight on rails at sunset, the same fight every lap.</summary>
    public bool Duel;
    /// <summary>Where the duel is fought (null: the built-in dune crest).</summary>
    public (float X, float Z)? DuelAt;
    /// <summary>The start to play (null = the default); with <c>--move-to</c> the picture shows a selected squad walking.</summary>
    public string? NewGameName;
    public (float X, float Z)? MoveTo;
    /// <summary>Interactive run that closes itself after this many seconds and prints the frame rate (an unattended smoke test).</summary>
    public double? QuitAfter;
    /// <summary>A save folder (Meitou's or the original game's) started in instead of a new world, loaded as the game menu's Load does (<c>--load</c>).</summary>
    public string? Load;
    /// <summary>With <c>--screenshot</c>: a staged scene to check behaviour offscreen (<c>--scene</c>: fight, turret, bed, acid, menu; GameHost.Scenes.cs).</summary>
    public string? Scene;
    /// <summary>The scene's frames and the control ticks between them (<c>--scene-frames</c>, <c>--scene-every</c>).</summary>
    public int SceneFrames = 8, SceneEvery = 30;
    /// <summary>The CHARACTER record (name or id) the fight and turret scenes spawn as enemies (<c>--scene-npc</c>).</summary>
    public string SceneNpc = "Bandit";
    /// <summary>The town (name, part of it) the turret scene moves the squad to first (<c>--scene-town</c>; null: the nearest turret to where the save is).</summary>
    public string? SceneTown;

    public const string Usage = """
        meitou [where] [options]     boots into the world with the Kenshi camera (default --town "The Hub")
          --fps-limit <n>            frame limit when vsync is off (default 150 from meitou.user.json; 0 = unlimited)
          --vsync / --no-vsync       vsync (default off)
          --seed <n>                 the world seed (default 0)
          --ui <faithful|meitou>      UI mode, independent of graphics; also available in the settings (default faithful)
          --ui-showcase              with --screenshot: capture HUD, inventory, stats, map, menus, a trade with the nearest trader and a conversation in either UI mode
          --title-preview            with --screenshot: capture the staged Meitou title scene
          --no-population            no town residents or movement (an empty world)
          --new-game [start]         a new game as the NEW_GAME_STARTOFF start (default Wanderer): the player squad at its town, camera on it
          --list-starts              print the available starts and exit
          --sandbox [n]              animation sandbox: no world, a flat 1000x1000 wireframe floor (lines every 10, bold every 100) with 1 character (n = 2: two facing each other 30 units apart; more: the rest idle in rings round them, up to 1000); Z/X/C walk/run/free, [ ] fixed speed, P patrol, G fight, Y follow; works with --screenshot --ticks
          --sandbox-character <name> the CHARACTER or ANIMAL_CHARACTER record the sandbox spawns (default: the first member of the default new-game start)
          --sandbox-mode <list>      the sandbox hero's start, comma-separated: walk, run or free, a fixed speed in u/s, patrol (the 400-unit patrol), fight (the G key, with --sandbox 2), e.g. run,patrol (with --move-to or --screenshot)
          --sandbox-races            the sandbox adds one character of every person race (the first CHARACTER record listing only that race) in rings round the pair, whatever n is; the log lists them
          --duel [x,z]               trailer scene: two helmeted Samurai duel at sunset in the desert dunes (or at x,z), on rails: every lap is the same fight, the clock, weather and clouds start again with it,
                                     and it restarts when a cinema flight is played or recorded (Backspace restarts it); the time of day is held (22:00, or --time) and the weather clear (or --weather)
          --move-to <x> <z>          with --new-game: select the squad and order it to walk (for screenshots; the picture is taken where the leader got to)
          --ticks <n>                with --screenshot: run n control ticks (and the same real time of the simulation, at speed 1) before the picture
          --quit-after <s>           close after s seconds and print the frame rate (smoke test)
          --scene <name>             with --screenshot (and --load): stage a scene and save a frame every --scene-every ticks (default 30), --scene-frames of them (default 8):
                                     fight (enemies of --scene-npc spawned beside the leader and attacked), turret (the nearest town turret, enemies spawned in its range),
                                     bed (the leader sleeps in the nearest bed), acid (acid rain forced on the characters), menu (the context menus of the nearest character,
                                     door, container and cage, one frame each); the log prints the leader's state each frame; --scene-town <name> moves the squad to that town first (turret)
          --load <save folder>       start in a save, Meitou's or the original game's (%LOCALAPPDATA%\kenshi\save\<name>), loaded as the game menu's Load does; works with --screenshot
          --yaw/--pitch/--distance   start view: heading, pitch above the horizon and boom (Kenshi: 30 degrees, boom 150; clamped to 10..2000)
          world options as meitou-viewer --world: --at, --zone, --town, --radius, --time, --screenshot, --size, --no-foliage, ...
        Keys: W/A/S/D or arrows move (or the pointer at a screen edge), Q/E rotate, Comma/Period tilt, wheel or Home/End zoom, PageUp/PageDown floor up/down (lens in the free camera), middle drag or Ctrl orbit, left click or drag selects, right click moves (shift queues), 1..9, 0 / ` select, [ / ] previous / next squad member, R stops,
          numpad 0..8 Block, Hold, Passive, Ranged, Sneak, Taunt, run speed, Medic (also H), Rescue (also J), L rest, K put down, I/C/M/T/Y windows, Tab next squad (all rebindable),
          ; free camera (R/F up/down), Space pause, F2/F3/F4 speed 1x/2x/5x, F8 or PrintScreen screenshot,
          F5 quicksave, F9 quickload (Meitou's own slot under %LOCALAPPDATA%\Meitou\save), Ctrl+F9 fps counter, F10 debug menu (teleport, spawn, items, money, heal, max skills, damage), F11 statistics, F12 profiler (gpu, cpu, off), Esc game menu (save, load, exit). Shift+F1 Meitou on / off, Alt+Enter full screen,
          F6 cinematic camera editor (numpad keys as in the viewer; game-shots.json; Home the lens also in the free camera), F7 hide the interface,
          Backspace restarts the --duel lap, PageUp/PageDown narrow/widen its lens, Home resets it.
        Settings (frame limit, vsync, full screen (--windowed for one run), tick rate (tickRate), simulation threads (simThreads), the settings sliders, key bindings) are kept in meitou.user.json.
        """;

    /// <summary>Takes the game's own options out of <paramref name="args"/> and returns the rest.</summary>
    public static (GameOptions, string[]) Split(string[] args)
    {
        var g = new GameOptions();
        var rest = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a} needs a value");
            switch (a)
            {
                case "--ui-showcase": g.UiShowcase = true; break;
                case "--fps-limit": g.FpsLimit = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--seed": g.Seed = ulong.Parse(Next(), CultureInfo.InvariantCulture); g.SeedGiven = true; break;
                case "--ui":
                    g.UiMode = Next().ToLowerInvariant() switch { "faithful" => Meitou.Data.Ui.UiMode.Faithful, "meitou" => Meitou.Data.Ui.UiMode.Meitou, _ => throw new ArgumentException("--ui must be faithful or meitou") };
                    break;
                case "--vsync": g.VSync = true; break;
                case "--no-vsync": g.VSync = false; break;
                case "--ticks": g.Ticks = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--no-population": g.NoPopulation = true; break;
                case "--title-preview": g.TitlePreview = true; g.UiMode = Meitou.Data.Ui.UiMode.Meitou; break;
                case "--new-game":
                    g.NewGame = true;
                    if (i + 1 < args.Length && !args[i + 1].StartsWith('-')) g.NewGameName = args[++i];
                    break;
                case "--list-starts": g.ListStarts = true; break;
                case "--sandbox":
                    g.Sandbox = i + 1 < args.Length && int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out int count) ? Math.Clamp(count, 1, 1000) : 1;
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out _)) i++;
                    break;
                case "--sandbox-character": g.SandboxCharacter = Next(); break;
                case "--sandbox-mode": g.SandboxMode = Next(); break;
                case "--sandbox-races": g.SandboxRaces = true; break;
                case "--duel":
                    g.Duel = true;
                    if (i + 1 < args.Length && args[i + 1].Split(',') is [var dx, var dz] && float.TryParse(dx, NumberStyles.Float, CultureInfo.InvariantCulture, out float x) && float.TryParse(dz, NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
                    {
                        g.DuelAt = (x, z);
                        i++;
                    }
                    break;
                case "--move-to": g.MoveTo = (float.Parse(Next(), CultureInfo.InvariantCulture), float.Parse(Next(), CultureInfo.InvariantCulture)); break;
                case "--quit-after": g.QuitAfter = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--load": g.Load = Next(); break;
                case "--scene": g.Scene = Next().ToLowerInvariant(); break;
                case "--scene-frames": g.SceneFrames = Math.Clamp(int.Parse(Next(), CultureInfo.InvariantCulture), 1, 200); break;
                case "--scene-every": g.SceneEvery = Math.Max(int.Parse(Next(), CultureInfo.InvariantCulture), 1); break;
                case "--scene-npc": g.SceneNpc = Next(); break;
                case "--scene-town": g.SceneTown = Next(); break;
                default: rest.Add(a); break;
            }
        }
        return (g, rest.ToArray());
    }
}
