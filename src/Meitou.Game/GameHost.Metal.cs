#if MEITOU_METAL
// Metal exists on macOS only; the file is compiled only there (the csproj's MEITOU_METAL constant).
#pragma warning disable CA1416
using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Numerics;
using Meitou.Data.Textures;
using Meitou.Engine;
using Meitou.Engine.Time;
using Meitou.Metal;
using Meitou.Rendering;
using Meitou.Rendering.Characters;
using Meitou.Rendering.Display;
using Meitou.Rendering.Gpu;
using Meitou.Rendering.Ui;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using SilkKey = Silk.NET.Input.Key;

namespace Meitou.Game;

sealed partial class GameHost
{
    /// <summary>
    /// The Metal path (macOS): the game boots without the Vulkan renderer and draws the world through the seam's recording model
    /// (native segments around the scene's host pass, guests inside it, the frame committed with the present). The loop is
    /// interactive: the strategy camera and the HUD's selection/orders read the window's input, while the frontend, audio and the
    /// remaining host services wait (docs/metal-backend.md).
    /// </summary>
    int Metal()
    {
        var loaded = LoadScene is not null ? LoadScene() : scene;
        if (loaded is null)
        {
            Console.Error.WriteLine("metal     the world could not be read");
            return 1;
        }
        if (LoadScene is not null) scene = ownedScene = loaded;

        // Same setting as VulkanDisplay: Metal used to always present with display sync, even under --no-vsync.
        bool vsync = g.VSync ?? config.VSync;
        int fpsLimit = g.FpsLimit ?? config.FpsLimit;
        using var display = new MetalDisplay(WindowOptions.Default with
        {
            Size = new Vector2D<int>(o.Width, o.Height),
            Title = "Meitou (metal)",
            FramesPerSecond = 0,
            UpdatesPerSecond = 0,
        }, vsync);
        Console.WriteLine($"metal     vsync {(display.VSync ? "on" : "off")}, frame cap {(fpsLimit > 0 ? $"{fpsLimit} fps" : "off")}");
        using var ctx = new MetalContext(display);
        var window = display.Window;
        ctx.Samplers.MaxAnisotropy = o.Anisotropy;
        // Same scale as the Vulkan path's TAA/upscaler setting: the scene is rendered smaller and TAA reconstructs it.
        // The macOS Vulkan reference runs at 0.50 (36-40 fps on an M1 Pro); 0.83 is the Windows/Meitou default.
        ctx.RenderScale = o.Post.Upscale.Explicit ? o.Post.Upscale.EffectiveScale : 0.5f;

        // The sky as CreateGpu makes it: the world's sky textures (starfield, planets, clouds) and the same switches.
        var sky = new SkyRenderer(ctx, assets)
        {
            CloudCoverage = o.Clouds,
            HazeStrength = o.HazeStrength,
            NightAir = o.NightAir,
            Planetshine = o.Planetshine,
            PlanetshineStrength = o.PlanetshineStrength,
            MeitouStars = o.MeitouStars,
            LitClouds = o.MeitouClouds,
            CloudShadows = o.CloudShadows,
        };
        if (scene.Database is { } skyDb) sky.LoadWorld(install, skyDb);
        // The weather scheduler, as WorldFrame.CreateGpu makes it: its state feeds the sky's fog and colour, and the surface shaders' wetness and dust.
        WorldWeather? weather = null;
        if (scene.Database is { } weatherDb)
        {
            var world = Meitou.Data.World.WeatherWorld.Create(weatherDb, install, 1, Meitou.Data.World.WeatherTime.At(0));
            weather = new WorldWeather(world, weatherDb) { Day = o.Day ?? 0 };
            if (!o.AutoWeather)
            {
                if (!world.ForceWeather(o.Weather))
                    throw new ArgumentException($"no weather named '{o.Weather}'; known: {string.Join(", ", Meitou.Data.World.SkyWeather.Names(weatherDb).Distinct().Take(12))} ...");
                sky.Weather = Meitou.Data.World.SkyWeather.Find(weatherDb, o.Weather) ?? Meitou.Data.World.SkyWeather.Default;
            }
            Console.WriteLine($"metal     weather: {(o.AutoWeather ? $"scheduler, day {o.Day ?? 0}" : $"forced {o.Weather}")}");
        }
        var surfaces = scene.Database is not null ? new WeatherSurfaces(ctx, assets) : null;
        // The placed fog volumes (docs/formats/fogfeatures.md): CPU culling and the frame globals every world shader reads.
        var fogVolumes = new FogVolumes(ctx, o.NoFogFeatures ? [] : Meitou.Data.World.FogFeatures.Load(install));
        // The sun's shadows: the game's CSM (or the Meitou cascades) into the atlas the terrain's receivers sample.
        var shadow = new ShadowPass(ctx, assets)
        {
            Settings = new Meitou.Data.World.ShadowSettings(Meitou.Data.World.KenshiShadows.MapSize(o.ShadowQuality), o.ShadowRangeFor(o.MeitouShadows)),
            Meitou = o.MeitouShadows,
            FilterQuality = o.ShadowFilter,
            Enabled = !o.ShadowsOff,
        };
        shadow.SetTerrain(scene.Coarse, scene.CoarseSize);
        // The final composite through the game's tone map: the scene draws into an HDR target, then this writes the LDR picture.
        var composite = new PostComposite(ctx, flipY: true);
        var taa = new PostTaa(ctx);
        // The text/quad painter, shared by the HUD and the optional frame-stats lines. It is created even for a screenshot:
        // the game's HUD is part of the picture. MEITOU_METAL_OVERLAY=1 adds the stats lines to a still.
        var overlay = DebugOverlay.TryCreate(ctx);
        bool showFrameStats = o.Screenshot is null || Environment.GetEnvironmentVariable("MEITOU_METAL_OVERLAY") == "1";
        display.Activate();
        Splash?.Dispose();

        // The terrain: the whole-world heights, the quadtree's patch grid, and (unless textures are off) the biomes' layers and maps.
        // The textures' CPU bake runs here once, as the Vulkan path's boot does before its loading frames.
        var terrain = new TerrainRenderer(ctx, scene.Coarse, scene.CoarseSize, scene.Window);
        TerrainTextures? terrainTextures = null;
        if (!o.NoTextures && scene.Database is { } textureDb)
        {
            terrainTextures = TerrainTextures.Create(ctx, install, textureDb, assets, source: TerrainTextures.Prepare(install, textureDb));
            terrain.SetTextures(terrainTextures);
            foreach (var m in terrainTextures.Messages.Take(20)) Console.WriteLine($"warning   {m}");
            Console.WriteLine($"metal     terrain: {terrain.LevelCount} LOD levels, {terrainTextures.Capacity} biome slots");
        }
        else Console.WriteLine($"metal     terrain: {terrain.LevelCount} LOD levels, untextured");
        // The fine heights and the biome maps stream round the eye, as the Vulkan path's streamer does (the Metal path has no object streaming yet).
        var streamer = scene.Heightmap is not null
            ? new TerrainStreamer(install, terrain, terrainTextures, scene.Window.Step) { MaterialDistance = o.MaterialDistance }
            : null;
        // The buildings and the world's objects, as WorldFrame.CreateGpu makes them: the same distances and LOD bias. The meshes and
        // textures stream in round the eye on the renderer's own workers.
        WorldObjectRenderer? objects = null;
        if (scene.Objects is { } worldObjects)
        {
            objects = new WorldObjectRenderer(ctx, assets, worldObjects, landmarks: o.LandmarkDistanceFor(o.MeitouReach) > 0)
            {
                ObjectDistance = o.ObjectDistanceFor(o.MeitouReach),
                LandmarkDistance = o.LandmarkDistanceFor(o.MeitouReach),
                DistantRange = o.DistantZones * Meitou.Data.World.WorldLayout.ZoneSize,
                NoDistant = o.DistantZones <= 0,
                LoadBudget = 0,
                SortNearestFirst = o.MeitouReach,
                LodBias = 1 / o.ObjectLod,
            };
            Console.WriteLine("metal     objects ready");
        }
        // The world's foliage (the catalog's meshes, the grass pages and the TERRAIN-mode rocks), as WorldFrame.CreateGpu makes it. The
        // impostor bakes and the GPU culls stay Vulkan-only for now: the CPU cull draws the meshes and the CPU grass path the blades.
        FoliageRenderer? foliage = null;
        if (!o.NoFoliage && scene.Database is { } foliageDb)
        {
            foliage = new FoliageRenderer(ctx, install, foliageDb,
                scene.Objects?.Levels ?? Meitou.Data.World.WorldLevelData.Load(install), assets)
            {
                SwaySeconds = 0,   // a still picture: the grass holds still, so it repeats
                MeitouRange = o.MeitouRange,
                Lod = o.FoliageLod,
                Terrain = terrain,
            };
            if (o.LargeRange is { } largeRange)
            {
                float k = largeRange / foliage.LargeRange;
                (foliage.SmallRange, foliage.MediumRange, foliage.LargeRange) = (foliage.SmallRange * k, foliage.MediumRange * k, largeRange);
            }
            if (o.GrassDensity is { } grassDensity) foliage.GrassDensitySetting = grassDensity;
            Console.WriteLine($"metal     foliage ready ({foliage.LoadMs:0} ms)");
        }
        // The water: the game's faithful quad first (the Meitou grid's ocean FFT and shore field are Vulkan-typed still).
        WaterRenderer? water = null;
        if (!o.NoWater && scene.Database is { } waterDb)
        {
            var messages = new List<string>();
            water = WaterRenderer.Create(ctx, install, waterDb, assets, sky, messages, scene.Coarse, o.WaterGrid, o.WaterOcean,
                meitouWaves: false, shoreField: false);
            water.Refraction = false;
            foreach (var m in messages.Take(20)) Console.WriteLine($"warning   {m}");
            Console.WriteLine("metal     water ready (faithful)");
        }
        terrain.BeginFrame();

        // The world, the simulation's side only: the renderer is not made, so the camera rig and the clock are the game's own.
        var heights = new Meitou.Data.World.GroundHeights(scene.Window, scene.Coarse, scene.CoarseSize, WorldFrame.CoarseStep);
        var (walkability, systems, constants) = BootSimulation(heights, interactive: true);
        (camera, render) = WorldFrame.Setup(scene, o);
        session = new WorldSession(scene.Focus, (scene.X0, scene.Z0, scene.X1, scene.Z1), heights.HeightAt, o.Hour, config.TickRate,
            clock: GameClockFor(constants ??= scene.Database is { } clockDb ? Meitou.Data.Gameplay.GameConstants.FromDatabase(clockDb) : null, o.Hour, o.Day ?? GameClock.DefaultStartDay),
            simulation: worldSettings = new Meitou.Simulation.WorldSettings { Seed = g.Seed, MinPartitionSize = 128, Threads = Math.Max(1, config.SimThreads ?? Math.Clamp(Environment.ProcessorCount / 2, 1, 8)) },
            systems: systems, walkability: walkability);
        session.Camera.Place(new Vector2((float)(o.X ?? scene.Focus.X), (float)(o.Z ?? scene.Focus.Z)), (float)(o.Yaw ?? 30) * MathF.PI / 180,
            (float)(o.Pitch ?? Meitou.Data.World.KenshiCamera.InitialPitchDegrees) * MathF.PI / 180,
            (float)(o.Distance ?? Meitou.Data.World.KenshiCamera.InitialDistance));
        // The game's new-game shortcut: the player's squad in the simulation, which the world draw list then shows.
        if (start is not null && population is not null)
        {
            try
            {
                playerSquad = population.StartPlayer(session.World, start);
                Console.WriteLine($"metal     player squad: {playerSquad.Members.Count} characters");
            }
            catch (InvalidOperationException e) { Console.WriteLine($"metal     player squad skipped: {e.Message}"); }
        }
        // The population's zones: the background towns activate the zones round the player, and a walker's path may cross one
        // that has to be built first. The interactive loop keeps this table current (UpdateNav), as the Vulkan host does.
        if (nav is not null && population is not null)
        {
            population.ZoneGate = zone => nav.Walkability.World.Contains(zone);
            var around = playerSquad is not null ? session.World.Characters.Previous[playerSquad.Leader.Slot].Position : session.Focus;
            var zones = Meitou.Simulation.ZoneActivation.ZonesAround(around, 0);
            var watch = Stopwatch.StartNew();
            Task.WaitAll([.. zones.Select(z => (Task)nav.LoadZone(z))]);
            foreach (var z in zones) navZones[z] = Stopwatch.GetTimestamp();
            Console.WriteLine($"metal     navmesh {zones.Count} zone(s) round the start ready in {watch.ElapsedMilliseconds} ms");
        }
        ApplyCamera(session.Camera.Current);
        SendFocus();
        // Characters: the viewer's generated harness with `--crowd`, else the simulation's snapshot list. The player
        // interface is not booted in this loop, so the draw list is the world-only source (no editor previews/portraits).
        CharacterRenderer? characters = null;
        if (scene.Database is { } characterDb)
        {
            if (o.Crowd > 0)
            {
                characters = CrowdHarness.Create(ctx, install, scene, assets, o, interactive: false);
                Console.WriteLine($"metal     crowd ready ({characters.Assets} appearances)");
            }
            else if (!g.NoPopulation)
            {
                characters = new CharacterRenderer(ctx, install, characterDb, assets, characterRigs) { Source = FillWorldDrawList, SettleFirst = true, LoadBudget = 0 };
                Console.WriteLine("metal     characters ready (population draw list)");
            }
        }
        // The game's HUD and panels. PlayerInterface is the interface's data/layout owner; ArrangeUi/BindUi and the UiPainter
        // work with the session the loop has. Only the panels' pointer/keyboard controls are wired here so far.
        PlayerInterface? hud = null;
        if (overlay is not null)
        {
            hud = new PlayerInterface(session, heights.HeightAt, DrawnPosition) { SpeedUnit = config.SpeedUnit };
            hud.LoadUi(g.UiMode ?? config.UiMode, assets.Find);
            hud.CreateUiPainter(overlay, assets.Find);
            if (popData is { } data)
            {
                hud.FactionName = faction => faction >= 0 && faction < data.Factions.Count ? data.Factions[faction].Name : null;
                hud.MapTowns = () => data.Sites;
                hud.MapFactionOf = id =>
                {
                    if (id is null || data.FactionIndex(id) is not (>= 0 and var town)) return null;
                    var faction = data.Factions[town];
                    if (session.World.Player.Faction is not (>= 0 and var mine))
                        return new MapFaction(faction.Name, 0, MapStanding.Neutral, faction.NotReal);
                    float relation = data.Relations.Get(mine, town);
                    var standing = town == mine ? MapStanding.Player : data.Relations.IsAlly(mine, town) ? MapStanding.Ally
                        : relation < -30 ? MapStanding.Enemy : MapStanding.Neutral;
                    return new MapFaction(faction.Name, relation, standing, faction.NotReal);
                };
                hud.MapItemTowns = record =>
                {
                    if (data.Db.Find(record) is not { Type: Meitou.Data.Fcs.FcsRecordType.MAP_ITEM } map) return null;
                    return [.. Meitou.Simulation.Items.MapItems.Reveals(data, map, session.CurrentSnapshot.Towns, session.World.Seed).Select(i => data.Sites[i].Town.Name)];
                };
            }
            if (characters is not null)
            {
                var (atlas, sampled) = characters.PortraitAtlas;
                hud.SetUiTexture(PlayerInterface.PortraitTexture, atlas, sampled);
                hud.SetItemIcons(ItemIconsOfInstall());
                hud.SetRenderedIcons(name => UiPainter.TryParseRenderedIcon(name, out string record, out string material)
                    && characters.TryGetIcon(new IconKey(record, material), out var icon) ? icon : null);
            }
            // A still or a run can open one of the shared layouts by name for review.
            if (g.UiPanel is { Length: > 0 } selectedPanel)
                hud.ShowUiPanel(selectedPanel is "hud" or "hud-expanded" ? null : selectedPanel, selectedPanel == "hud-expanded");
            // The picks the interface asks the host for: the drawn character meshes, the buildings' floors and their closed shells.
            hud.MeshHit = (id, origin, direction) => characters?.RayHit(DrawKey(id), origin, direction);
            hud.BuildingMoveHit = (origin, direction, distance) => objects?.MoveHit(origin, direction, viewedFloor, distance);
            hud.ClosedBuildingHit = (origin, direction, distance) => objects?.ClosedBuildingHit(origin, direction, distance);
            Console.WriteLine($"metal     HUD ready ({hud.UiMode})");
        }
        // The real input path: the window's keys and pointer go to PlayerInterface first (its panels, builds and the world's
        // selection/order clicks), then to the session's bindings so the strategy camera pans, turns and zooms.
        using var input = Silk.NET.Input.InputWindowExtensions.CreateInput(window);
        bool shiftDown = false, statsVisible = false, frameStatsVisible = showFrameStats, screenshotNow = false;
        Vector2? lastMouse = null;
        Vector2 Pixels(Vector2 p) => p * new Vector2(window.FramebufferSize.X / (float)Math.Max(window.Size.X, 1),
            window.FramebufferSize.Y / (float)Math.Max(window.Size.Y, 1));
        bool Held(SilkKey a, SilkKey b) => input.Keyboards.Any(k => k.IsKeyPressed(a) || k.IsKeyPressed(b));
        bool Ctrl() => Held(SilkKey.ControlLeft, SilkKey.ControlRight);
        bool Alt() => Held(SilkKey.AltLeft, SilkKey.AltRight);
        foreach (var keyboard in input.Keyboards)
        {
            keyboard.KeyDown += (_, key, _) =>
            {
                if (key is SilkKey.ShiftLeft or SilkKey.ShiftRight) { shiftDown = true; if (hud is not null) hud.SlowModifier = true; }
                if (key == SilkKey.Enter && Alt()) { BorderlessFullScreen.Toggle(window); return; }
                // F11 lists the frame; F12 is the small frame-rate line (the profiler chart waits for Metal timestamps).
                if (key == SilkKey.F11) { statsVisible = !statsVisible; return; }
                if (key == SilkKey.F12) { frameStatsVisible = !frameStatsVisible; return; }
                if (key is SilkKey.F8 or SilkKey.PrintScreen) { screenshotNow = true; return; }
                if (key == SilkKey.F7 && hud is not null) { hud.UiVisible = !hud.UiVisible; return; }
                if (KeyMap.Map(key) is not { } mapped) return;
                // The interface's keys come first (I/C, M/T/Y, Esc, B), as in the Vulkan host; what it does not take reaches
                // the session (WASD, Q/E, Space, F2..F4, PageUp/PageDown, the camera bindings).
                if (hud is not null && (hud.UiKey(mapped, Ctrl(), shiftDown) || hud.BuildKey(mapped, shiftDown))) return;
                session.Input.SetKey(mapped, true);
                if (hud is not null) hud.Key(mapped, shiftDown);
            };
            keyboard.KeyUp += (_, key, _) =>
            {
                if (key is SilkKey.ShiftLeft or SilkKey.ShiftRight) { shiftDown = false; if (hud is not null) hud.SlowModifier = false; }
                if (KeyMap.Map(key) is { } mapped) { session.Input.SetKey(mapped, false); hud?.BuildKeyUp(mapped); }
            };
            keyboard.KeyChar += (_, value) => hud?.UiText(value);
        }
        foreach (var mouse in input.Mice)
        {
            mouse.MouseMove += (_, p) =>
            {
                var at = Pixels(p);
                hud?.MouseMove(at);
                // The panels and their drags own the pointer: the camera must not also turn under them.
                if (hud is { UiCapturesPointer: true }) { lastMouse = p; return; }
                if (lastMouse is { } last) session.Input.AddMouseDelta(p.X - last.X, p.Y - last.Y);
                lastMouse = p;
            };
            mouse.MouseDown += (_, button) =>
            {
                if (KeyMap.Map(button) is not { } mapped) return;
                // PlayerInterface sees every click: it takes the HUD's controls, selects, starts a drag box or gives a move order.
                if (hud is not null && hud.MouseDown(mapped, Pixels(mouse.Position), shiftDown, Ctrl(), Alt())) return;
                session.Input.SetMouseButton(mapped, true);
            };
            mouse.MouseUp += (_, button) =>
            {
                if (KeyMap.Map(button) is not { } mapped) return;
                hud?.MouseUp(mapped, Pixels(mouse.Position), shiftDown);
                session.Input.SetMouseButton(mapped, false);
            };
            mouse.Scroll += (_, wheel) =>
            {
                var at = Pixels(mouse.Position);
                // A panel's own scroll wins; over any HUD rectangle the wheel does not zoom the camera, as in the Vulkan host.
                if (hud is not null && (hud.UiScroll(at, wheel.Y) || hud.ContainsUi(at))) return;
                session.Input.AddWheel(wheel.Y);
            };
        }
        window.FocusChanged += focused =>
        {
            if (focused) return;
            session.Input.ReleaseAll();
            if (hud is not null) hud.SlowModifier = false;
            shiftDown = false;
            lastMouse = null;
        };
        // The streamer settles the fine heights and biome maps round the camera before the first frame draws (the still's camera does not move).
        streamer?.Settle(camera.Eye);
        if (streamer is { } settled) Console.WriteLine($"metal     {settled.Describe()}");

        var clock = Stopwatch.StartNew();
        double last = 0, fpsWindow = 0, titleTimer = 0, traceAt = 0, nativeAt = 0, lastWait = 0, lastRecord = 0;
        int frames = 0, fpsFrames = 0;
        string fpsText = "";
        bool trace = Environment.GetEnvironmentVariable("MEITOU_METAL_TRACE") == "1";
        if (trace)
        {
            var watchdog = new Thread(() =>
            {
                while (true)
                {
                    Thread.Sleep(2000);
                    Console.WriteLine($"metal     trace watchdog: main loop at frame {Volatile.Read(ref frames)}");
                }
            }) { IsBackground = true, Name = "metal trace" };
            watchdog.Start();
        }
        var statsLines = new List<string>();
        GpuTexture? sceneColour = null;
        double streamingIdleSince = 0, lastManagedTrim = double.NegativeInfinity;
        // Loading can take long enough for the terminal that started the game to take focus back: bring the game forward again
        // before the interactive loop (its title and FPS are watched there).
        display.Activate();
        while (!window.IsClosing)
        {
            window.DoEvents();
            if (window.IsClosing) break;
            double now = clock.Elapsed.TotalSeconds, frameStart = now, dt = now - last;
            last = now;
            if (trace && now - traceAt >= 1)
            {
                traceAt = now;
                Console.WriteLine($"metal     trace t={now:0.0}s frame {frames}, window {window.Size.X}x{window.Size.Y} points, " +
                    $"framebuffer {window.FramebufferSize.X}x{window.FramebufferSize.Y} pixels, cpu {dt * 1000:0.0} ms " +
                    $"(drawable {lastWait:0.0} ms, record {lastRecord:0.0} ms), gpu {ctx.LastGpuMilliseconds:0.0} ms");
            }
            if (trace && now - nativeAt >= 5)
            {
                nativeAt = now;
                Console.WriteLine($"metal     native {display.NativeWindowState()}");
                Console.WriteLine($"metal     resources {ctx.ResourceSummary()}");
            }
            var size = window.FramebufferSize;
            // The counter is the mean over the last quarter second, so one late frame does not read as a drop.
            fpsWindow += dt;
            fpsFrames++;
            if (fpsWindow >= 0.25)
            {
                fpsText = $"{fpsFrames / fpsWindow:0} fps";
                fpsWindow = 0;
                fpsFrames = 0;
            }
            // Edge scrolling only while the world owns the pointer (the interface is visible and not dragging a panel).
            session.Camera.Strategy.EdgePointer = hud is { UiVisible: false } or { UiCapturesPointer: true } ? null : WindowPointer.InWindow(window);
            session.Advance(dt);
            ApplyCamera(session.CameraAt());
            SendFocus();
            UpdateNav();
            // A live resize can leave the layer without a drawable for a moment: wait for the next one instead of spinning
            // the main thread (the window would stop responding to its own resize).
            if (!ctx.BeginFrame()) { Thread.Sleep(1); continue; }
            double drawableWait = display.LastDrawableWaitMilliseconds;
            long recordStart = Stopwatch.GetTimestamp();
            var sceneSize = new Vector2D<int>(ctx.SceneWidth, ctx.SceneHeight);
            // TAA's mip bias: the scene samples at the render scale, the history at the display size (PostProcess's TAA rule).
            ctx.LodBias = MathF.Log2(sceneSize.X / (float)Math.Max(size.X, 1)) - 0.5f;
            // The still's objects are all resident before the first picture, as the Vulkan screenshot path settles them (its Settle
            // opens its own frame; here the frame is the first one's).
            if (frames == 0) { objects?.Settle(camera.Eye); foliage?.Settle(camera.Eye); }
            streamer?.Update(camera.Eye);
            foliage?.Update(camera.Eye);

            var taaJitter = PostTaa.Offset(frames + 1, sceneSize.X, size.X);
            if (objects is not null) { objects.SetView(sceneSize.X, sceneSize.Y, camera.FieldOfView); objects.Update(camera.Eye); }
            if (characters is not null)
            {
                characters.PortraitRequests.Clear();
                if (hud is not null)
                    foreach (var (member, cell) in hud.SquadBarMembers().Select((member, cell) => (member, cell)))
                        characters.PortraitRequests.Add(new PortraitRequest(DrawKey(member.Id), cell, member.PortraitSerial));
                characters.SetView(sceneSize.X, sceneSize.Y, camera.FieldOfView);
                characters.Update(camera.Eye);
                characters.DrawPortraits();
                characters.DrawIcons();
            }
            // The frame's inputs, as WorldFrame.Draw prepares them: the weather at the camera, the atmosphere's tables for the sun and the
            // eye's height, the fog distance the light ends at, and the view distance that keeps the haze complete before the far plane.
            float hour = (float)session.Clock.HourOfDay;
            var aspect = sceneSize.X / (float)Math.Max(sceneSize.Y, 1);
            var eye = camera.Eye;
            var view = camera.View;
            var rotation = view with { M41 = 0, M42 = 0, M43 = 0 };
            var viewProjection = rotation * camera.Projection(aspect, 1, 1000);
            var sun = scene.Clock.SunDirection(hour);
            const bool held = true;   // a still picture: the weather's timers snap, as the post chain's InstantAdaptation does
            if (weather is { } current)
            {
                current.Update(eye, hour, sun.Y, held);
                current.Apply(sky);
            }
            if (surfaces is { } weatherSurfaces)
            {
                weatherSurfaces.GameTime = hour;
                weatherSurfaces.Apply(weather?.State ?? Meitou.Data.World.WeatherState.Clear);
            }
            var weatherState = weather?.State ?? Meitou.Data.World.WeatherState.Clear;
            water?.Animate(session.Clock.HoursSinceStart, weatherState.WindDirection, weatherState.WindSpeed);
            sky.SetEye(eye, heights.HeightAt, float.NegativeInfinity);
            sky.Eye = eye;
            sky.Clock = scene.Clock;
            sky.Day = weather?.Day ?? 0;
            sky.Hour = hour;
            sky.StarHours = session.Clock.HoursSinceStart;
            sky.StarSeconds = (float)(now % 3600);
            var (colours, light) = sky.Prepare(sun, eye.Y, o.FogDistance + 3 * Math.Max(eye.Y, 0));
            if (weather is { } drift) sky.StepClouds(drift.Times.Game); else sky.StepClouds(held);
            // Far enough that the haze closes before the far plane and the water quad ends (WorldFrame.Draw's rule).
            camera.ViewDistance = Math.Max(camera.MinViewDistance, light.FogDistance / 0.7f);
            fogVolumes.Update(eye, camera.Forward, camera.FieldOfView, aspect, camera.ViewDistance, sun.Y, true, null, sky.FogCullDistance);
            // The casters' cascades, before the scene reads the receiver block (terrain depth only so far: no objects or foliage on Metal).
            var shadowView = new Meitou.Data.World.ShadowView(eye, camera.Forward, Vector3.Normalize(new Vector3(view.M12, view.M22, view.M32)), camera.FieldOfView,
                aspect, camera.Near, camera.Distance);
            shadow.Render(shadowView, light.SunDirection, (cascade, worldToClip, planes, lodEye) =>
            {
                terrain.DrawDepth(worldToClip, lodEye, planes, render);
                if (render.Objects && objects is not null) objects.DrawDepth(worldToClip, lodEye, planes, render, terrain);
                characters?.DrawDepth(worldToClip, lodEye, planes, ShadowLod.ObjectLodFloor(ShadowLod.Applies(cascade.Index) ? (float)cascade.Texel : 0, render.TerrainPixelScale));
                if (foliage is not null)
                {
                    float coarse = shadow.Meitou && ShadowLod.Applies(cascade.Index) ? (float)cascade.Texel : 0;
                    foliage.DrawDepth(worldToClip, lodEye, planes, render, terrain, maxRange: shadow.EffectiveRange * 1.2f,
                        minSize: shadow.MinFoliageCaster(cascade), texel: (float)cascade.Texel, coarse: coarse > 0);
                }
            });


            // The scene's host pass, as SceneHost.Open does: a native segment, a rendering into the backbuffer (cleared to
            // transparent; the sky fills every pixel), the host pass the sky draws as a guest segment in.
            var cmd = (MetalCommandList)ctx.BeginNative("scene");
            var backbuffer = ctx.Backbuffer;
            var depth = ctx.Depth;
            var albedo = ctx.Albedo;
            if (sceneColour is null || sceneColour.Desc.Width != sceneSize.X || sceneColour.Desc.Height != sceneSize.Y)
            {
                sceneColour?.Dispose();
                sceneColour = ctx.CreateTexture(new TextureDesc(InternalFormat.Rgba16f, sceneSize.X, sceneSize.Y,
                    Use: TextureUse.ColourTarget | TextureUse.Sampled, Name: "scene colour"));
            }
            var targets = PassTargets.Of(sceneColour, depth, albedo) with
            {
                Colour = new RenderTarget(sceneColour.Attachment(), LoadOp.Clear, new ClearColour(0, 0, 0, 0)),
                Depth = new RenderTarget(depth.Attachment(), LoadOp.Clear, default),
            };
            cmd.BeginRendering(targets.Rendering);
            ctx.BeginHostPass(cmd, targets, DrawState.Scene(targets.Formats));

            sky.Draw(viewProjection, colours);

            // The terrain: the camera's projection (near 0.5 .. the view distance), the quadtree's LOD in rendered pixels, the sky's light.
            render.TerrainPixelScale = Meitou.Data.World.TerrainLod.ProjectionScale(sceneSize.Y, camera.FieldOfView);
            var worldProjection = view * Jitter.Apply(camera.Projection(aspect, camera.Near, camera.ViewDistance), taaJitter, sceneSize.X, sceneSize.Y);
            var frustum = WorldCamera.FrustumPlanes(worldProjection);
            terrain.BeginFrame();
            terrain.Draw(worldProjection, eye, frustum, render, light);
            if (render.Objects && objects is not null)
                objects.Draw(worldProjection, eye, frustum, render, light.SunDirection, light.FogColour, light.FogDistance, terrain);
            foliage?.Draw(worldProjection, eye, frustum, render, light.SunDirection, light.FogColour, light.FogDistance, terrain);
            // Characters after the opaque geometry: they alone write the scene's alpha (the SSAO character mask in the Vulkan post chain).
            characters?.Draw(worldProjection, eye, frustum, light.SunDirection, light.FogColour, light.FogDistance);
            water?.Draw(worldProjection, eye, light, colours, (float)session.Clock.HoursSinceStart, camera.ViewDistance * 1.5f);

            ctx.EndHostPass(cmd);
            cmd.EndRendering();
            ctx.EndNative(cmd);

            // The temporal resolve: the jittered scene against the history, at the scene's size (the vendor upscalers are not ported yet).
            var taaCmd = (MetalCommandList)ctx.BeginNative("post taa");
            var resolved = taa.Draw(taaCmd, sceneColour, depth, eye, view, camera.FieldOfView, aspect, camera.Near, camera.ViewDistance, taaJitter, size.X, size.Y);
            ctx.EndNative(taaCmd);

            // The composite: the scene's HDR colour through the game's tone map into the backbuffer the present shows.
            var post = (MetalCommandList)ctx.BeginNative("post composite");
            composite.Draw(post, resolved, backbuffer, o.Post, (sky.MinLuminance, sky.MaxLuminance));
            ctx.EndNative(post);
            if (overlay is not null)
            {
                overlay.Target = backbuffer;
                if (hud is not null)
                {
                    hud.Width = size.X;
                    hud.Height = size.Y;
                    hud.Eye = eye;
                    hud.ViewProjection = view * camera.Projection(aspect, camera.Near, camera.ViewDistance);
                    // The move order under a held right button follows the cursor once a frame, as the Vulkan host's Steer does.
                    hud.Steer();
                    if (hud.UiVisible) hud.Draw(overlay, size.X, size.Y);
                }
                bool wrote = false;
                // The framebuffer is 2x the window's points on a Retina display: the fixed-pixel debug text would be half size.
                float textScale = MathF.Max(1, size.X / (float)Math.Max(window.Size.X, 1));
                if (statsVisible)
                {
                    statsLines.Clear();
                    statsLines.Add($"{fpsText}  {dt * 1000:0.00} ms  {size.X}x{size.Y}");
                    statsLines.Add($"sim {session.Simulation.TotalTicks} ticks  {session.CurrentSnapshot.Characters.Count} characters  x{session.TimeScale:0}  {session.Clock.TimeText}");
                    statsLines.Add($"camera {camera.Target.X:0}, {camera.Target.Y:0}, {camera.Target.Z:0}  yaw {camera.Yaw * 180 / MathF.PI:0}  pitch {camera.Pitch * 180 / MathF.PI:0}  boom {camera.Distance:0}");
                    if (characters is not null) statsLines.Add(characters.Statistics());
                    if (foliage is not null) statsLines.Add($"foliage {foliage.DrawnInstances} instances, {foliage.DrawnBlades} blades, {foliage.DrawCalls} calls");
                    overlay.Panel(size.X, size.Y, "Metal frame (F11 hides this)", statsLines, (hud is { UiVisible: true } ? hud.HudBottom : 0) + 8, textScale);
                    wrote = true;
                }
                if (frameStatsVisible)
                {
                    overlay.TextScaled("Meitou - Metal", 16 * textScale, 16 * textScale, DebugOverlay.TextColour, textScale);
                    overlay.TextScaled($"{frames + 1} frames  {fpsText}  {dt * 1000:0.00} ms  {size.X}x{size.Y}", 16 * textScale,
                        (16 + overlay.LineHeight) * textScale, DebugOverlay.TextColour, textScale);
                    wrote = true;
                }
                if (wrote) overlay.Flush(size.X, size.Y);
            }
            ctx.EndFrame();
            lastWait = drawableWait;
            lastRecord = Stopwatch.GetElapsedTime(recordStart).TotalMilliseconds;
            frames++;
            // Once the terrain, object and foliage streamers have been quiet for two seconds, the large decode/upload
            // buffers are garbage. Compact at that idle point (at most once a minute) so the LOH does not keep them
            // for the rest of the session. Active streaming is never interrupted.
            bool streamingIdle = (terrainTextures?.Idle ?? true) && (objects?.Pending ?? 0) == 0 && (foliage?.Pending ?? 0) == 0;
            if (!streamingIdle) streamingIdleSince = 0;
            else if (streamingIdleSince == 0) streamingIdleSince = now;
            if (streamingIdle && now - streamingIdleSince >= 2 && now - lastManagedTrim >= 60)
            {
                lastManagedTrim = now;
                var before = GC.GetGCMemoryInfo();
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                var after = GC.GetGCMemoryInfo();
                long loh = after.GenerationInfo.Length > 3 ? after.GenerationInfo[3].SizeAfterBytes : 0;
                if (trace)
                    Console.WriteLine($"metal     idle trim: heap {before.HeapSizeBytes / 1048576} -> {after.HeapSizeBytes / 1048576} MB, " +
                        $"LOH {loh / 1048576} MB");
            }
            if (screenshotNow)
            {
                string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
                if (pictures.Length == 0) pictures = Directory.GetCurrentDirectory();
                string file = Path.Combine(Directory.CreateDirectory(pictures).FullName, $"meitou-metal-{DateTime.Now:yyyyMMdd-HHmmss}.png");
                Capture(ctx, backbuffer, file);
                screenshotNow = false;
            }
            // A panel's item icons are drawn after the frame that asks for them, and their textures stream in: let a UI still
            // wait for them (up to a bound), while an ordinary world still keeps its 40 frames.
            int pendingIcons = (characters?.PendingIcons ?? 0) + (hud?.PendingIconFiles ?? 0);
            if (o.Screenshot is { } shot && frames >= 40 && (pendingIcons == 0 || frames >= 400))
            {
                if (foliage is not null) Console.WriteLine($"metal     foliage: {foliage.DrawnInstances} instances, {foliage.DrawnBlades} blades, {foliage.DrawCalls} calls");
                Capture(ctx, backbuffer, shot);
                break;   // one picture, as the Vulkan flow's does
            }
            titleTimer += dt;
            if (titleTimer >= 0.5)
            {
                var s = session.Camera.Current;
                window.Title = $"Meitou (metal) | {fpsText} | {(session.Camera.IsFree ? "free camera" : $"boom {s.Distance:0}")} | " +
                    $"{session.Clock.TimeText} {session.Clock.DayText} | {s.Target.X:0}, {s.Target.Z:0}";
                titleTimer = 0;
            }
            // The Vulkan loop's cap: only without vsync (vsync already paces the present); the same 30 fps cap in
            // both comparison runs isolates tearing from the much higher uncapped frame rate.
            if (!display.VSync && fpsLimit > 0) Limit(clock, frameStart, 1.0 / fpsLimit);
            if (g.QuitAfter is { } quitAfter && now >= quitAfter) break;
        }
        // Let the queue finish before the renderers release views, buffers and bindless entries; a quit during a
        // streaming upload otherwise freed resources the GPU was still reading and could fault on the way out.
        ctx.Finish();
        hud?.DisposeUi();
        overlay?.Dispose();
        characters?.Dispose();
        foliage?.Dispose();
        water?.Dispose();
        objects?.Dispose();
        terrain.Dispose();   // owns the textures it was given
        streamer?.Dispose();
        sceneColour?.Dispose();
        shadow.Dispose();
        composite.Dispose();
        taa.Dispose();
        surfaces?.Dispose();
        sky.Dispose();
        double elapsed = clock.Elapsed.TotalSeconds;
        Console.WriteLine($"metal     {frames} frames in {elapsed:0.0} s: {frames / Math.Max(elapsed, 1e-3):0} fps, " +
            $"camera {camera.Target.X:0}, {camera.Target.Z:0}, yaw {camera.Yaw * 180 / MathF.PI:0}, pitch {camera.Pitch * 180 / MathF.PI:0}, boom {camera.Distance:0}");
        return 0;
    }

    /// <summary>The backbuffer's picture as a PNG (the readback's rows are the picture's own: Metal draws right side up).</summary>
    static void Capture(MetalContext ctx, GpuTexture backbuffer, string path)
    {
        int w = backbuffer.Desc.Width, h = backbuffer.Desc.Height;
        var pixels = ctx.Readback(backbuffer);
        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir is not null) Directory.CreateDirectory(dir);
        PngWriter.Write(path, w, h, pixels);
        Console.WriteLine($"shot      {path} ({w}x{h})");
    }
}
#endif
