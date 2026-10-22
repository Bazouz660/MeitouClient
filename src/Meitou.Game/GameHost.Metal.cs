#if MEITOU_METAL
// Metal exists on macOS only; the file is compiled only there (the csproj's MEITOU_METAL constant).
#pragma warning disable CA1416
using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Numerics;
using Meitou.Data.Particles;
using Meitou.Data.Save;
using Meitou.Data.Textures;
using Meitou.Data.World;
using Meitou.Engine;
using Meitou.Engine.Input;
using Meitou.Engine.Time;
using Meitou.Metal;
using Meitou.Rendering;
using Meitou.Rendering.Characters;
using Meitou.Rendering.Display;
using Meitou.Rendering.Gpu;
using Meitou.Rendering.Ui;
using Meitou.Simulation.Editing;
using Meitou.Simulation.Saving;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using EngineKey = Meitou.Engine.Input.Key;
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
        // The banks and the output device load on a worker while the window, the world and the renderers are made.
        BeginAudio();
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
        // The Metal path has no single GPU allocator with a global budget: every cache has its own high-water mark.
        // Travelling through the world can fill the object, foliage and character caches at the same time, so keep the
        // whole device allocation under a conservative share of what Metal recommends. The guard pauses/evicts caches
        // and shortens the ranges as the ceiling approaches, exactly as it does on Vulkan.
        ulong memoryBudget = ctx.VideoMemoryBudget > 0 ? Math.Min(ctx.VideoMemoryBudget, 5UL << 30) : 5UL << 30;
        if (ulong.TryParse(Environment.GetEnvironmentVariable("MEITOU_METAL_GPU_BUDGET_MB"), NumberStyles.None,
                CultureInfo.InvariantCulture, out ulong budgetMb) && budgetMb > 0)
            memoryBudget = budgetMb << 20;
        VramGuard? memoryGuard = null;
        if (Environment.GetEnvironmentVariable("MEITOU_VRAM_GUARD") != "0")
        {
            memoryGuard = new VramGuard(() => ((ulong)ctx.Device.CurrentAllocatedSize, memoryBudget));
            Console.WriteLine($"metal     memory guard: {memoryBudget / 1048576} MB of GPU allocation");
        }
        var window = display.Window;
        // The shared window setting: borderless full screen as the Vulkan path starts; --windowed keeps the window for one run.
        bool fullScreen = config.FullScreen && !o.Windowed;
        void ToggleFullScreen()
        {
            BorderlessFullScreen.Toggle(window);
            display.SetFullScreenPresentation(BorderlessFullScreen.IsOn(window));
        }
        if (fullScreen) ToggleFullScreen();
        else display.SetFullScreenPresentation(false);
        ctx.Samplers.MaxAnisotropy = o.Anisotropy;
        // Same scale as the Vulkan path's TAA/upscaler setting: the scene is rendered smaller and an upscaler reconstructs it.
        // The Metal default is MetalFX Balanced at 0.50; Native at 1.00 is the fallback when MetalFX is unavailable.
        // MEITOU_METAL_NO_TAA is a diagnostic view: native size, no jitter and no temporal resolve.
        bool noTaaRequested = Environment.GetEnvironmentVariable("MEITOU_METAL_NO_TAA") is "1" or "true";
        // `--upscaler metalfx` is the command-line form; MEITOU_METALFX stays usable for launch scripts.
        bool metalFxRequested = !noTaaRequested && (o.Post.Upscale.Kind == UpscalerKind.MetalFx ||
            Environment.GetEnvironmentVariable("MEITOU_METALFX") is "1" or "true");
        bool noTaa = false;
        bool spatial = false;

        // Safe Metal defaults while the foliage mesh path and the cloud-shadow path are being fixed: both are opt-in.
        bool metalFoliage = Environment.GetEnvironmentVariable("MEITOU_METAL_FOLIAGE") == "1";
        bool metalCloudShadows = Environment.GetEnvironmentVariable("MEITOU_METAL_CLOUD_SHADOWS") == "1";
        bool metalWater = Environment.GetEnvironmentVariable("MEITOU_METAL_WATER") == "1";
        bool metalShadows = Environment.GetEnvironmentVariable("MEITOU_METAL_SHADOWS") == "1";
        if (!metalFoliage) Console.WriteLine("metal     foliage disabled (safe mode; MEITOU_METAL_FOLIAGE=1 to test)");
        if (!metalCloudShadows) Console.WriteLine("metal     cloud shadows disabled (safe mode; F10 or MEITOU_METAL_CLOUD_SHADOWS=1)");
        if (!metalWater) Console.WriteLine("metal     water disabled (safe mode; MEITOU_METAL_WATER=1 to test)");
        if (!metalShadows) Console.WriteLine("metal     shadows disabled (safe mode; F10 or MEITOU_METAL_SHADOWS=1)");
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
            CloudShadows = metalCloudShadows,
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
        hostWeather = weather;
        var surfaces = scene.Database is not null ? new WeatherSurfaces(ctx, assets) : null;
        // The placed fog volumes (docs/formats/fogfeatures.md): CPU culling and the frame globals every world shader reads.
        var fogVolumes = new FogVolumes(ctx, o.NoFogFeatures ? [] : Meitou.Data.World.FogFeatures.Load(install));
        // The sun's shadows: the game's CSM (or the Meitou cascades) into the atlas the terrain's receivers sample.
        var shadow = new ShadowPass(ctx, assets)
        {
            Settings = new Meitou.Data.World.ShadowSettings(Meitou.Data.World.KenshiShadows.MapSize(o.ShadowQuality), o.ShadowRangeFor(o.MeitouShadows)),
            Meitou = o.MeitouShadows,
            FilterQuality = o.ShadowFilter,
            Enabled = metalShadows && !o.ShadowsOff,
            Guard = memoryGuard,
        };
        shadow.SetTerrain(scene.Coarse, scene.CoarseSize);
        // The final composite through the game's tone map: the scene draws into an HDR target, then this writes the LDR picture.
        var composite = new PostComposite(ctx, flipY: true);
        composite.LoadHeatHaze(assets);
        var taa = new PostTaa(ctx);
        // MetalFX temporal is the default; spatial covers the performance tier, where temporal at 2.5x/3x darkens.
        var metalFx = MetalFxUpscaler.TryCreate(ctx);
        var metalFxSpatial = MetalFxSpatialUpscaler.TryCreate(ctx);
        MetalUpscalerMode mode = config.MetalUpscaler;
        float scale = Math.Clamp(config.MetalRenderScale, 0.33f, 1f);
        if (noTaaRequested)
        {
            mode = MetalUpscalerMode.Native;
            scale = 1f;
        }
        else if (Environment.GetEnvironmentVariable("MEITOU_METALFX_SPATIAL") is "1" or "true")
        {
            mode = MetalUpscalerMode.Spatial;
            scale = 0.4f;
        }
        else if (metalFxRequested)
        {
            mode = MetalUpscalerMode.Temporal;
            // --upscaler metalfx without a scale means the balanced default; an explicit --render-scale wins.
            scale = o.Post.Upscale.Scale is null ? 0.5f : o.Post.Upscale.EffectiveScale;
        }
        else if (o.Post.Upscale.Explicit)
            scale = o.Post.Upscale.EffectiveScale;

        if (mode == MetalUpscalerMode.Temporal && metalFx is null)
        {
            Console.WriteLine("metal     MetalFX temporal unavailable; using Native");
            mode = MetalUpscalerMode.Native;
            scale = 1f;
        }
        if (mode == MetalUpscalerMode.Spatial && metalFxSpatial is null)
        {
            Console.WriteLine("metal     MetalFX spatial unavailable; using " + (metalFx is null ? "Native" : "MetalFX temporal"));
            mode = metalFx is null ? MetalUpscalerMode.Native : MetalUpscalerMode.Temporal;
            scale = mode == MetalUpscalerMode.Native ? 1f : Math.Clamp(scale, 0.5f, 1f);
        }
        if (mode == MetalUpscalerMode.Temporal && scale < 0.5f && metalFxSpatial is not null)
            mode = MetalUpscalerMode.Spatial;   // the temporal scaler's reliable range is 0.50 and up
        if (mode == MetalUpscalerMode.Spatial && scale >= 0.5f && metalFx is not null)
            mode = MetalUpscalerMode.Temporal;
        scale = Math.Clamp(scale, 0.33f, 1f);
        ctx.RenderScale = scale;

        void ApplyMode(MetalUpscalerMode next)
        {
            mode = next;
            noTaa = mode == MetalUpscalerMode.Native;
            spatial = mode == MetalUpscalerMode.Spatial;
            taa.External = mode switch
            {
                MetalUpscalerMode.Temporal => metalFx,
                MetalUpscalerMode.Spatial => metalFxSpatial,
                _ => null,
            };
            config.MetalUpscaler = mode;
        }
        ApplyMode(mode);
        if (noTaa) Console.WriteLine("metal     no TAA/upscaler: native scene, no jitter");

        int CurrentPresetIndex()
        {
            if (mode == MetalUpscalerMode.Native) return 0;
            if (mode == MetalUpscalerMode.Taa) return 4;
            if (mode == MetalUpscalerMode.Temporal && MathF.Abs(ctx.RenderScale - 0.67f) < 0.005f) return 1;
            if (mode == MetalUpscalerMode.Temporal && MathF.Abs(ctx.RenderScale - 0.50f) < 0.005f) return 2;
            if (mode == MetalUpscalerMode.Spatial && MathF.Abs(ctx.RenderScale - 0.40f) < 0.005f) return 3;
            return 5;   // a custom scale/upscaler combination
        }
        void SetRenderScale(float value)
        {
            value = Math.Clamp(value, 0.33f, 1f);
            ctx.RenderScale = value;
            config.MetalRenderScale = value;
            // Native means full size; moving the scale engages the MetalFX tier that supports it. TAA keeps its own scale.
            if (mode == MetalUpscalerMode.Native && value < 1f)
            {
                if (value >= 0.5f && metalFx is not null) ApplyMode(MetalUpscalerMode.Temporal);
                else if (metalFxSpatial is not null) ApplyMode(MetalUpscalerMode.Spatial);
            }
            else if (mode == MetalUpscalerMode.Temporal && value < 0.5f && metalFxSpatial is not null)
                ApplyMode(MetalUpscalerMode.Spatial);
            else if (mode == MetalUpscalerMode.Spatial && value >= 0.5f && metalFx is not null)
                ApplyMode(MetalUpscalerMode.Temporal);
        }
        void SetPreset(int index)
        {
            MetalUpscalerMode next = index switch
            {
                0 => MetalUpscalerMode.Native,
                1 or 2 => MetalUpscalerMode.Temporal,
                3 => MetalUpscalerMode.Spatial,
                4 => MetalUpscalerMode.Taa,
                _ => mode,
            };
            float nextScale = index switch { 0 => 1f, 1 => 0.67f, 2 => 0.50f, 3 => 0.40f, 4 => 0.50f, _ => ctx.RenderScale };
            SetRenderScaleForPreset(next, nextScale);
        }
        void SetRenderScaleForPreset(MetalUpscalerMode next, float nextScale)
        {
            if (next == MetalUpscalerMode.Temporal && metalFx is null)
            {
                Console.WriteLine("metal     MetalFX temporal is unavailable on this device");
                next = MetalUpscalerMode.Native;
                nextScale = 1f;
            }
            if (next == MetalUpscalerMode.Spatial && metalFxSpatial is null)
            {
                Console.WriteLine("metal     MetalFX spatial is unavailable on this device");
                next = metalFx is null ? MetalUpscalerMode.Native : MetalUpscalerMode.Temporal;
                nextScale = next == MetalUpscalerMode.Native ? 1f : Math.Clamp(nextScale, 0.5f, 1f);
            }
            ApplyMode(next);
            ctx.RenderScale = Math.Clamp(nextScale, 0.33f, 1f);
            config.MetalRenderScale = ctx.RenderScale;
        }
        string UpscalerStatusText() => mode switch
        {
            MetalUpscalerMode.Native => "Native",
            MetalUpscalerMode.Temporal => $"MetalFX temporal {ctx.RenderScale:0.00}",
            MetalUpscalerMode.Spatial => $"MetalFX spatial {ctx.RenderScale:0.00}",
            _ => $"TAA {ctx.RenderScale:0.00} (experimental)",
        };
        bool openSettingsForStill = Environment.GetEnvironmentVariable("MEITOU_METAL_SETTINGS") is "1" or "true";
        Console.WriteLine($"metal     upscaler {mode.ToString().ToLowerInvariant()} at {ctx.RenderScale:0.00}");
        // The text/quad painter, shared by the HUD and the optional frame-stats lines. It is created even for a screenshot:
        // the game's HUD is part of the picture. MEITOU_METAL_OVERLAY=1 adds the stats lines to a still.
        var overlay = DebugOverlay.TryCreate(ctx);
        bool showFrameStats = o.Screenshot is null || Environment.GetEnvironmentVariable("MEITOU_METAL_OVERLAY") == "1";
        display.Activate();
        if (BorderlessFullScreen.IsOn(window)) display.SetFullScreenPresentation(true);
        Splash?.Dispose();

        // The terrain: the whole-world heights, the quadtree's patch grid, and (unless textures are off) the biomes' layers and maps.
        // The textures' CPU bake runs here once, as the Vulkan path's boot does before its loading frames.
        // The world's lamps (including interior room lights), read on a worker; their grid publishes the light globals the world shaders sample.
        WorldLamps? lamps = null;
        if (scene.Database is { } lampDb && scene.Heightmap is { } lampMap)
        {
            lamps = new WorldLamps(ctx, () =>
            {
                var levels = WorldLevelData.Load(install, includeInteriors: true);
                return WorldLights.ForWorld(lampDb, levels, lampMap.HeightAt, new BuildingLayouts(lampDb, levels.Interiors));
            });
            hostLamps = lamps;
        }
        var terrain = new TerrainRenderer(ctx, scene.Coarse, scene.CoarseSize, scene.Window);
        // The weather's and map placers' particles: created here, updated each frame, and drawn after the scene effects.
        ParticleRenderer? particles = null;
        WeatherAreas? weatherAreas = null;
        if (!o.NoParticles && Environment.GetEnvironmentVariable("MEITOU_METAL_NO_PARTICLES") != "1" && scene.Database is { } effectsDb)
        {
            var particleWatch = Stopwatch.StartNew();
            particles = ParticleRenderer.Create(ctx, install);
            particles.PrewarmSeconds = o.ParticlePrewarm;
            particles.World.GroundHeight = terrain.HeightAt;
            particles.World.FreezeGroundHeight = () => terrain.Snapshot().HeightAt;
            weatherAreas = WeatherAreas.Load(install);
            particles.World.Area = RegionArea.At(weatherAreas, scene.Focus.X, scene.Focus.Z);
            var placers = MapEffectPlacers.Find(effectsDb, MapFeatureFile.Open(install));
            particles.SetPlacers(placers);
            Console.WriteLine($"metal     particles ready: {particles.Library.Systems.Count} systems, {placers.Count} placers ({particleWatch.ElapsedMilliseconds} ms)");
        }
        TerrainTextures? terrainTextures = null;
        bool terrainTexturesOff = Environment.GetEnvironmentVariable("MEITOU_METAL_NO_TERRAIN_TEXTURES") == "1";
        if (!o.NoTextures && !terrainTexturesOff && scene.Database is { } textureDb)
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
                Guard = memoryGuard,
            };
            hostObjects = objects;
            Console.WriteLine("metal     objects ready");
        }
        // The buildings' interior masks (docs/render-interior-cut.md): the depth range the terrain, foliage and map features are cut by
        // while a viewed building is open. The renderer is backend-neutral; Metal draws it into its own target before the scene.
        InteriorMaskRenderer? interiorMask = objects is null ? null : new InteriorMaskRenderer(ctx, objects);
        // The world's foliage (the catalog's meshes, the grass pages and the TERRAIN-mode rocks), as WorldFrame.CreateGpu makes it. The
        // impostor bakes and the GPU culls stay Vulkan-only for now: the CPU cull draws the meshes and the CPU grass path the blades.
        FoliageRenderer? foliage = null;
        if (!o.NoFoliage && metalFoliage && scene.Database is { } foliageDb)
        {
            foliage = new FoliageRenderer(ctx, install, foliageDb,
                scene.Objects?.Levels ?? Meitou.Data.World.WorldLevelData.Load(install), assets)
            {
                SwaySeconds = 0,   // a still picture: the grass holds still, so it repeats
                MeitouRange = o.MeitouRange,
                Lod = o.FoliageLod,
                Terrain = terrain,
                Guard = memoryGuard,
                // The Metal impostor atlas still comes out corrupt (red/black bands, and a gray
                // full-screen card under MetalFX); keep the meshes and grass until that path is fixed.
                Impostors = false,
                // Diagnostic/workaround: keep the tree, bush and rock meshes but skip the grass page draws.
                DrawGrass = Environment.GetEnvironmentVariable("MEITOU_METAL_NO_GRASS") != "1",
                // Diagnostic isolation for the far-travel GPU hang: trees/bushes off, the TERRAIN-mode rocks still draw.
                DrawMeshes = Environment.GetEnvironmentVariable("MEITOU_METAL_NO_FOLIAGE_MESHES") != "1",
            };
            if (o.LargeRange is { } largeRange)
            {
                float k = largeRange / foliage.LargeRange;
                (foliage.SmallRange, foliage.MediumRange, foliage.LargeRange) = (foliage.SmallRange * k, foliage.MediumRange * k, largeRange);
            }
            if (o.GrassDensity is { } grassDensity) foliage.GrassDensitySetting = grassDensity;
            hostFoliage = foliage;
            Console.WriteLine($"metal     foliage ready ({foliage.LoadMs:0} ms)");
        }
        // The water: the game's faithful quad first (the Meitou grid's ocean FFT and shore field are Vulkan-typed still).
        WaterRenderer? water = null;
        if (metalWater && !o.NoWater && scene.Database is { } waterDb)
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
        hostGround = heights.HeightAt;
        var (walkability, systems, constants) = BootSimulation(heights, interactive: true);
        (camera, render) = WorldFrame.Setup(scene, o);
        session = new WorldSession(scene.Focus, (scene.X0, scene.Z0, scene.X1, scene.Z1), heights.HeightAt, o.Hour, config.TickRate,
            clock: GameClockFor(constants ??= scene.Database is { } clockDb ? Meitou.Data.Gameplay.GameConstants.FromDatabase(clockDb) : null, o.Hour, o.Day ?? GameClock.DefaultStartDay),
            simulation: worldSettings = new Meitou.Simulation.WorldSettings { Seed = g.Seed, MinPartitionSize = 128, Threads = Math.Max(1, config.SimThreads ?? Math.Clamp(Environment.ProcessorCount / 2, 1, 8)) },
            systems: systems, walkability: walkability);
        session.Camera.Place(new Vector2((float)(o.X ?? scene.Focus.X), (float)(o.Z ?? scene.Focus.Z)), (float)(o.Yaw ?? 30) * MathF.PI / 180,
            (float)(o.Pitch ?? Meitou.Data.World.KenshiCamera.InitialPitchDegrees) * MathF.PI / 180,
            (float)(o.Distance ?? Meitou.Data.World.KenshiCamera.InitialDistance));
        // The saved key bindings feed the same settings dialog and camera/order input as Vulkan.
        foreach (var problem in session.Bindings.Apply(config.Bindings)) Console.Error.WriteLine($"config    binding skipped: {problem}");
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
            hostCharacters = characters;
            if (characters is not null)
            {
                characters.Guard = memoryGuard;
                characters.InteriorObjects = objects;
            }
        }
        // The game's HUD and panels. PlayerInterface is the interface's data/layout owner; ArrangeUi/BindUi and the UiPainter
        // work with the session the loop has. Only the panels' pointer/keyboard controls are wired here so far.
        PlayerInterface? hud = null;
        HoverOutlineRenderer? hoverOutline = null;
        MoveMarkerRenderer? moveMarker = null;
        NavmeshOverlayRenderer? navOverlay = null;
        if (overlay is not null)
        {
            hud = new PlayerInterface(session, heights.HeightAt, DrawnPosition) { SpeedUnit = config.SpeedUnit, Floaters = config.Floaters };
            player = hud;
            hud.LoadUi(g.UiMode ?? config.UiMode, assets.Find);
            hud.CreateUiPainter(overlay, assets.Find);
            ConfigurePlayerInterface(hud);
            if (characters is not null)
            {
                var (atlas, sampled) = characters.PortraitAtlas;
                hud.SetUiTexture(PlayerInterface.PortraitTexture, atlas, sampled);
            }
            ConnectItemIcons(hud);
            hud.ConfigureFrontend(scene.Database,
                () => SaveCatalog.Scan(QuickSave.DefaultRoot, install.SaveDirectory,
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "kenshi", "save")),
                assets.Find("credits.txt") is { } creditsFile ? File.ReadAllText(creditsFile) : "");
            // A still or a run can open one of the shared layouts by name for review.
            if (g.UiPanel is { Length: > 0 } selectedPanel)
                hud.ShowUiPanel(selectedPanel is "hud" or "hud-expanded" ? null : selectedPanel, selectedPanel == "hud-expanded");
            // The picks the interface asks the host for: the drawn character meshes, the buildings' floors and their closed shells.
            hud.MeshHit = (id, origin, direction) => characters?.RayHit(DrawKey(id), origin, direction);
            hud.Floor = (() => viewedFloor, StepFloor);
            hud.BuildingMoveHit = (origin, direction, distance) => objects?.MoveHit(origin, direction, viewedFloor, distance);
            hud.SurfaceMoveHit = SurfaceMoveHit;
            hud.ClosedBuildingHit = (origin, direction, distance) => objects?.ClosedBuildingHit(origin, direction, distance);
            hoverOutline = new HoverOutlineRenderer(ctx);
            moveMarker = MoveMarkerRenderer.Create(ctx, assets.Find);
            hud.MoveMarker = moveMarker;
            navOverlay = new NavmeshOverlayRenderer(ctx);
            navOverlayRenderer = navOverlay;
            Console.WriteLine($"metal     HUD ready ({hud.UiMode})");
        }
        // The shared settings layout: Metal supplies the settings it can change live (window, upscaler, distances,
        // shadows, sky and the composite); the Interface/Controls/Advanced/Audio pages are the game's own.
        SettingsPanel? settingsValues = null;
        SettingsMenu? settings = null;
        if (overlay is not null && hud is not null)
        {
            settingsValues = CreateMetalSettings();
            settings = new SettingsMenu(hud, settingsValues, session.Bindings, SaveSettings,
                new Slider("Frame limit", 0, 360, () => fpsLimit, v => fpsLimit = (int)MathF.Round(v / 5) * 5, "0",
                    Text: v => v <= 0 ? "Unlimited" : $"{v:0} fps"), config)
            {
                UpscalerStatus = UpscalerStatusText,
                AttackSlotsChanged = slots => { foreach (var combat in session.World.Systems.OfType<Meitou.Simulation.Combat.CombatSystem>()) combat.AttackSlots = slots; },
                LateDefencesChanged = on => { foreach (var combat in session.World.Systems.OfType<Meitou.Simulation.Combat.CombatSystem>()) combat.LateDefences = on; },
                DoorsCloseChanged = on => { foreach (var locks in session.World.Systems.OfType<Meitou.Simulation.Locks.LockSystem>()) locks.DoorsCloseBehind = on; },
                OffscreenChanged = () =>
                {
                    foreach (var population in session.World.Systems.OfType<Meitou.Simulation.PopulationSystem>())
                        (population.KeepRoamers, population.OffscreenFights, population.OffscreenHealing, population.CampaignAncestorsOnce, population.KeepWild) = (config.KeepRoamers, config.OffscreenFights, config.OffscreenHealing, config.CampaignAncestorsOnce, config.KeepWild);
                },
                AudioChanged = AudioSettingsChanged,
            };
            // The saved graphics values are the same keys the Vulkan panel uses. The Metal display choices have their own
            // config fields, and a command-line upscaler/scale stays explicit for this run.
            var metalDisplayKeys = new HashSet<string>(StringComparer.Ordinal)
            {
                "VSync", "Full screen", SettingsMenu.MetalPresetSlider, WorldFrame.UpscalerSliders[1],
            };
            foreach (var slider in settingsValues.Sliders)
                if (!SessionSliders.Contains(slider.Label) && !metalDisplayKeys.Contains(slider.Label) &&
                    config.Graphics.TryGetValue(slider.Label, out float value))
                    slider.Set(Math.Clamp(value, slider.Min, slider.Max));
            foreach (var toggle in settingsValues.Toggles)
                if (config.Graphics.TryGetValue("Meitou: " + toggle.Label, out float value) && (value >= 0.5f) != toggle.Get())
                    toggle.Set(value >= 0.5f);
            // The impostor atlas path is still corrupt on Metal: a saved "Far impostors" setting must not turn it back on.
            if (foliage is not null) foliage.DisableImpostors();
            // The saved Cloud shadows and Shadow distance settings can re-enable the safe-mode suspects after the sky and the
            // shadow pass were made; keep both off until the black-terrain path is fixed, unless the diagnostic env asks.
            if (!metalCloudShadows) sky.CloudShadows = false;
            if (!metalShadows) shadow.Enabled = false;
            if (openSettingsForStill) settings.Visible = true;
        }
        // The loading screen is backend-neutral: the same Kenshi layout, picture and tips the Vulkan host draws.
        if (overlay is not null)
        {
            try
            {
                loading = LoadingScreen.Create(overlay, assets.Find, install.DataDirectory,
                    (g.UiMode ?? config.UiMode) == Meitou.Data.Ui.UiMode.Faithful);
            }
            catch (Exception e) { Console.Error.WriteLine($"loading   the loading screen did not load: {e.Message}"); }
        }
        bool quit = false;
        // The title opens unless a start, a save or a picture chose the world; --title-preview opens it for a still.
        bool showTitle = hud is not null && (g.TitlePreview || o.Screenshot is null && start is null && StartSave is null && !IsSandbox && !IsDuel && !g.NoPopulation);
        if (hud is not null)
        {
            hud.ScreenAction += e =>
            {
                if (e.Action == "save") saveRequestedFolder = SaveCatalog.Folder(QuickSave.DefaultRoot, e.Text);
                else if (e.Action == "load") loadRequestedFolder = e.Text;
                else if (e.Action == "new-game") newGameRequested = e.Text;
                else if (e.Action == "main-menu") mainMenuRequested = true;
                else if (e.Widget == "OptionsButton" && settings is not null) settings.Visible = true;
                else if (e.Widget == "ExitGameButton" || e.Action == "exit") quit = true;
            };
            if (showTitle) hud.ShowTitle();
        }
        // --load starts in the save: the same swap the menu's load uses, with the world already read around its camera.
        if (hud is not null && StartSave is { } startSave)
        {
            readingFolder = g.Load ?? "";
            try
            {
                var navmesh = SwapInSave(Task.FromResult(startSave), interactive: true, out string summary);
                navmesh.Wait();
                SettleNewWorld();
                FinishLoadNotice(summary);
                ApplyCamera(session.Camera.Current);
            }
            catch (SceneLoadFailed e)
            {
                Console.Error.WriteLine($"load      {e.Message}");
                hud.Notice(e.Message, true);
            }
        }
        // The audio device is up before the first frame; the title theme starts here when the menu is open.
        AttachAudio();
        SettingsPanel CreateMetalSettings()
        {
            var sliders = new List<Slider>();
            var toggles = new List<Toggle>();
            // Display: the same labels the shared settings menu knows (VSync, Full screen, render scale and filtering), plus
            // the Metal preset. The frame limit is added by SettingsMenu itself.
            sliders.Add(new Slider("VSync", 0, 1, () => display.VSync ? 1 : 0, v => display.VSync = v >= 0.5f,
                Text: v => v >= 0.5f ? "on" : "off (uncapped)"));
            sliders.Add(new Slider("Full screen", 0, 1, () => BorderlessFullScreen.IsOn(window) ? 1 : 0,
                v => { if (v >= 0.5f != BorderlessFullScreen.IsOn(window)) ToggleFullScreen(); },
                Text: v => v >= 0.5f ? "borderless" : "window"));
            sliders.Add(new Slider(SettingsMenu.MetalPresetSlider, 0, 5, () => CurrentPresetIndex(), v =>
            {
                int index = (int)MathF.Round(v);
                if (index is >= 0 and <= 4) SetPreset(index);
            }, "0"));
            sliders.Add(new Slider(WorldFrame.UpscalerSliders[1], 0.33f, 1, () => ctx.RenderScale, SetRenderScale, "0.00",
                Text: v => v.ToString("0.00", CultureInfo.InvariantCulture)));
            sliders.Add(new Slider(WorldFrame.FilteringSlider, 0, 4, () => MathF.Log2(ctx.Samplers.MaxAnisotropy),
                v => ctx.Samplers.MaxAnisotropy = 1 << (int)MathF.Round(v), "0",
                Text: v => (1 << (int)MathF.Round(v)) + "x"));
            sliders.Add(new Slider(WorldFrame.TextureQualitySlider, 0, TextureQuality.Maximum, () => TextureQuality.Level,
                v => TextureQuality.Level = (int)MathF.Round(v), "0", Text: v => TextureQuality.Labels[Math.Clamp((int)MathF.Round(v), 0, TextureQuality.Maximum)]));

            // World: the distances the Metal renderers actually read every frame.
            if (objects is not null)
            {
                sliders.Add(new Slider("Object draw distance", 1000, 400000, () => objects.ObjectDistance, v => objects.ObjectDistance = v, "0", Logarithmic: true,
                    Faithful: Enhancements.FaithfulObjectDistance, Meitou: Enhancements.MeitouObjectDistance));
                if (objects.HasLandmarks)
                    sliders.Add(new Slider("Landmark distance (Meitou)", 1000, 400000, () => objects.LandmarkDistance,
                        v => objects.LandmarkDistance = MathF.Round(v / 100) * 100, "0", Logarithmic: true));
                sliders.Add(new Slider("Distant towns (zones)", 0, ObjectRanges.MaxDistantTownRangeZones * 10,
                    () => objects.DistantRange / Meitou.Data.World.WorldLayout.ZoneSize,
                    v => objects.DistantRange = MathF.Round(v) * Meitou.Data.World.WorldLayout.ZoneSize, "0"));
                sliders.Add(new Slider("Object LOD distance x", 0.25f, 4, () => 1 / objects.LodBias,
                    v => objects.LodBias = 1 / v, "0.00", Logarithmic: true));
            }
            if (foliage is not null)
            {
                sliders.Add(new Slider("Foliage draw distance x", 0.25f, 80, () => foliage.RangeSetting,
                    v => foliage.RangeSetting = v, "0.00", Logarithmic: true));
                sliders.Add(new Slider("Grass draw distance x", 0.25f, 80, () => foliage.GrassRangeSetting,
                    v => foliage.GrassRangeSetting = v, "0.00", Logarithmic: true));
                sliders.Add(new Slider("Grass density x", 0.1f, 2, () => foliage.GrassDensitySetting,
                    v => foliage.GrassDensitySetting = v, "0.00"));
                sliders.Add(new Slider("Foliage LOD distance x (lod Meitou)", 0.25f, 8,
                    () => FoliageRenderer.DefaultLodTolerance / foliage.LodTolerance,
                    v => foliage.LodTolerance = FoliageRenderer.DefaultLodTolerance / v, "0.00", Logarithmic: true));
                toggles.Add(new Toggle("Foliage ranges", () => foliage.MeitouRange, v => foliage.MeitouRange = v));
            }
            sliders.Add(new Slider("Terrain detail: error px (less = finer)", 1, 32, () => render.TerrainPixelError,
                v => render.TerrainPixelError = v, "0.0", Logarithmic: true,
                Faithful: WorldRenderOptions.DefaultTerrainPixelError, Meitou: Enhancements.MeitouTerrainPixelError));

            // Lighting: the same shadow controls the Vulkan settings expose, and the sky switches the Metal sky reads.
            const float shadowsOff = Meitou.Data.World.KenshiShadows.MinRange * 0.9f;
            sliders.Add(new Slider("Shadow distance (game 1k-9k, 0 off)", shadowsOff, 200000,
                () => shadow.Enabled ? shadow.Settings.Range : shadowsOff, v =>
                {
                    shadow.Enabled = v >= Meitou.Data.World.KenshiShadows.MinRange;
                    if (shadow.Enabled) shadow.Settings = shadow.Settings with { Range = MathF.Round(v / 100) * 100 };
                }, "0", Logarithmic: true, Text: v => shadow.Enabled ? v.ToString("0", CultureInfo.InvariantCulture) : "0 (off)",
                Faithful: Meitou.Data.World.KenshiShadows.DefaultRange, Meitou: Enhancements.MeitouShadowRange));
            sliders.Add(new Slider("Shadow map size (game quality 0-2)", 0, 2,
                () => Array.IndexOf(Meitou.Data.World.KenshiShadows.QualitySizes, shadow.Settings.MapSize),
                v => shadow.Settings = shadow.Settings with { MapSize = Meitou.Data.World.KenshiShadows.MapSize((int)MathF.Round(v)) }, "0",
                Text: v => Meitou.Data.World.KenshiShadows.MapSize((int)MathF.Round(v)).ToString(CultureInfo.InvariantCulture) + "²"));
            if (shadow.Meitou)
            {
                sliders.Add(new Slider("Shadow filter 0-2 (Meitou; 0 low-end)", 0, 2, () => shadow.FilterQuality,
                    v => shadow.FilterQuality = (int)MathF.Round(v), "0"));
                toggles.Add(new Toggle("Shadow cascade 5 (Meitou)", () => shadow.MeitouCascades >= 5,
                    v => shadow.MeitouCascades = v ? 5 : 4));
            }
            sliders.Add(new Slider("Haze strength (1 = game)", 0, 3, () => sky.HazeStrength,
                v => sky.HazeStrength = v, "0.00", Faithful: 1, Meitou: Enhancements.MeitouHazeStrength));
            sliders.Add(new Slider("Planetshine strength (x physical)", 0, 200, () => sky.PlanetshineStrength,
                v => sky.PlanetshineStrength = MathF.Round(v), "0"));
            toggles.Add(new Toggle("Cloud shadows", () => sky.CloudShadows, v => sky.CloudShadows = v));
            toggles.Add(new Toggle("Night air", () => sky.NightAir, v => sky.NightAir = v));
            toggles.Add(new Toggle("Planetshine", () => sky.Planetshine, v => sky.Planetshine = v));
            toggles.Add(new Toggle("Night sky", () => sky.MeitouStars, v => sky.MeitouStars = v));
            toggles.Add(new Toggle("Clouds", () => sky.LitClouds, v => sky.LitClouds = v));

            // Water: the faithful water's clarity controls (the Metal path has no reflection pass yet).
            if (water is not null)
            {
                sliders.Add(new Slider("Water clarity x (Meitou)", 0.25f, 8, () => water.Clarity,
                    v => water.Clarity = v, "0.00", Logarithmic: true));
                sliders.Add(new Slider("Far water clarity (Meitou)", 0, 1, () => water.FarClarity,
                    v => water.FarClarity = v, "0.00", Text: v => v <= 0 ? "0 (true slant)" : v.ToString("0.00", CultureInfo.InvariantCulture)));
                sliders.Add(new Slider("Water see-through distance (Meitou, game 4000)", 1000, 100000,
                    () => water.ClearDistance, v => water.ClearDistance = MathF.Round(v / 100) * 100, "0", Logarithmic: true));
            }

            // Effects: the composite already supports the game's exposure/tone-map/grading path, even before the rest of
            // the Vulkan post chain is ported.
            sliders.Add(new Slider("Tone map (0 = game)", 0, 3, () => (int)o.Post.ToneMap,
                v => o.Post.ToneMap = (ToneMapOperator)(int)MathF.Round(v), "0",
                Text: v => (ToneMapOperator)(int)MathF.Round(v) switch
                {
                    ToneMapOperator.Shoulder => "shoulder",
                    ToneMapOperator.Aces => "ACES",
                    ToneMapOperator.Hybrid => "hybrid (clamp + ACES)",
                    _ => "clamp (as the game)",
                }));
            sliders.Add(new Slider("Hybrid tone map: ACES share", 0, 1, () => o.Post.ToneMix,
                v => o.Post.ToneMix = MathF.Round(v * 20) / 20, "0.00"));
            sliders.Add(new Slider("Grading (0 = game)", 0, 1, () => o.Post.Grade ? 1 : 0,
                v => o.Post.Grade = v >= 0.5f,
                Text: v => v >= 0.5f ? $"saturation {PostOptions.Saturation:0.00}, contrast {PostOptions.Contrast:0.00}" : "off (as the game)"));
            toggles.Add(new Toggle("Dithering", () => o.Post.Dither, v => o.Post.Dither = v));
            return new SettingsPanel(overlay!, "Settings   (Tab hides this)", sliders, toggles, "Metal switches", [], null, null);
        }
        // The real input path: the window's keys and pointer go to PlayerInterface first (its panels, builds and the world's
        // selection/order clicks), then to the session's bindings so the strategy camera pans, turns and zooms.
        using var input = Silk.NET.Input.InputWindowExtensions.CreateInput(window);
        if (hud is not null)
        {
            hud.ClipboardRead = () => input.Keyboards.Count > 0 ? input.Keyboards[0].ClipboardText : null;
            hud.ClipboardWrite = text => { if (input.Keyboards.Count > 0) input.Keyboards[0].ClipboardText = text; };
        }
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
                if (key == SilkKey.Enter && Alt()) { ToggleFullScreen(); return; }
                if (Covered)
                {
                    if (key is SilkKey.Escape or SilkKey.Space) skyArrival?.Skip();
                    return;
                }
                if (key == SilkKey.F10 && settings is not null) { settings.Visible = !settings.Visible; return; }
                // The full settings dialog (Metal's F10 used to be a preset overlay; the preset now lives in Display).
                if (settings?.Visible == true)
                {
                    if ((key == SilkKey.KeypadEnter ? EngineKey.Enter : KeyMap.Map(key)) is { } settingsKey)
                    {
                        if (settingsKey is EngineKey.F8 or EngineKey.PrintScreen) screenshotNow = true;
                        else settings.KeyDown(settingsKey, shiftDown, Ctrl());
                    }
                    return;
                }
                if (settings is not null && KeyMap.Map(key) is { } settingsToggle &&
                    session.Bindings.Get(InputAction.ToggleSettings).Contains(Binding.Of(settingsToggle)))
                {
                    settings.Visible = !settings.Visible;
                    return;
                }
                // F11 lists the frame; F12 is the small frame-rate line (the profiler chart waits for Metal timestamps).
                if (key == SilkKey.F11 && shiftDown) { ToggleNavOverlay(); return; }
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
            keyboard.KeyChar += (_, value) =>
            {
                if (Covered) return;
                if (settings?.Visible == true) settings.TextInput(value);
                else hud?.UiText(value);
            };
        }
        foreach (var mouse in input.Mice)
        {
            mouse.MouseMove += (_, p) =>
            {
                if (Covered) { lastMouse = p; return; }
                var at = Pixels(p);
                if (settings?.Visible == true) { settings.MouseMove(at); lastMouse = p; return; }
                hud?.MouseMove(at);
                // The panels and their drags own the pointer: the camera must not also turn under them.
                if (hud is { UiCapturesPointer: true }) { lastMouse = p; return; }
                if (lastMouse is { } last) session.Input.AddMouseDelta(p.X - last.X, p.Y - last.Y);
                lastMouse = p;
            };
            mouse.MouseDown += (_, button) =>
            {
                if (Covered) return;
                if (KeyMap.Map(button) is not { } mapped) return;
                var at = Pixels(mouse.Position);
                if (settings?.Visible == true) { settings.MouseDown(at, mapped); return; }
                // PlayerInterface sees every click: it takes the HUD's controls, selects, starts a drag box or gives a move order.
                if (hud is not null && hud.MouseDown(mapped, at, shiftDown, Ctrl(), Alt())) return;
                session.Input.SetMouseButton(mapped, true);
            };
            mouse.MouseUp += (_, button) =>
            {
                if (Covered) return;
                if (settings?.Visible == true) { settings.MouseUp(); return; }
                if (KeyMap.Map(button) is not { } mapped) return;
                hud?.MouseUp(mapped, Pixels(mouse.Position), shiftDown);
                session.Input.SetMouseButton(mapped, false);
            };
            mouse.Scroll += (_, wheel) =>
            {
                if (Covered) return;
                if (settings?.Visible == true) { settings.Scroll(wheel.Y); return; }
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
        // The game's quick save/load keys are control actions, as in the Vulkan host (F5/F9 by default).
        session.Ticked += actions =>
        {
            if (actions.Pressed(InputAction.QuickSave)) quickSaveRequested = true;
            if (actions.Pressed(InputAction.QuickLoad)) quickLoadRequested = true;
            if (actions.Pressed(InputAction.FloorUp)) StepFloor(1);
            if (actions.Pressed(InputAction.FloorDown)) StepFloor(-1);
        };
        // Put the title's camera and staging up before the streamers settle, so the menu's first frame is already the staged view.
        if (hud is not null) ApplyTitleCamera();
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
        if (BorderlessFullScreen.IsOn(window)) display.SetFullScreenPresentation(true);
        while (!window.IsClosing && !quit)
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
                Console.WriteLine($"metal     streaming terrain {streamer?.Pending ?? 0} ({(streamer is null || streamer.Idle ? "idle" : "busy")}), " +
                    $"objects {objects?.Pending ?? 0}, foliage {foliage?.Pending ?? 0}, cull groups {foliage?.LastCullGroups ?? 0}");
                if (streamer is not null) Console.WriteLine($"metal     terrain stream: {streamer.Describe()}");
                if (memoryGuard is not null) Console.WriteLine($"metal     memory guard {memoryGuard.Status}");
                if (objects is not null) Console.WriteLine($"metal     objects resident: {objects.ResidentDescription}");
                if (foliage is not null) Console.WriteLine($"metal     foliage resident: {foliage.ResidentDescription}");
                if (characters is not null) Console.WriteLine($"metal     characters: {characters.Describe()}");
            }
            var size = window.FramebufferSize;
            // The original's background-loading bar follows the interface's mode, as on Vulkan; the Metal host keeps the same shared bar.
            bool faithfulUi = hud?.UiMode == Meitou.Data.Ui.UiMode.Faithful;
            loadBar.Update(faithfulUi ? PendingLoads() : 0);
            if (hud is not null) hud.LoadBar = loadBar;
            if (loading is not null) (loading.LoadBar, loading.Faithful) = (loadBar, faithfulUi);
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
            session.Camera.Strategy.EdgePointer = Covered || hud is not null && TitleVisible || settings?.Visible == true ||
                hud is { UiVisible: false } or { UiCapturesPointer: true } ? null : WindowPointer.InWindow(window);
            if (!Covered) session.Advance(dt);
            if (hud is not null)
            {
                HandleSaves();
                HandleNewGame();
                HandleMainMenu();
            }
            StepSceneLoad();
            AdvanceSkyArrival(dt);
            // The merged character editor is shared with Vulkan: a new game opens it once the squad is in the snapshot, and the world resumes when it closes.
            OpenPendingEditor();
            ResumeAfterCreation();
            if (hud is not { EditorOpen: true }) ClearEditorStage();
            ApplyCamera(session.CameraAt());
            if (hud is not null) ApplyTitleCamera();
            // The pointer's Ctrl/Alt modifiers and the Highlight key (Alt): labels on the ground items near the focus, signs and the hover outlines agree.
            bool ctrlHeld = Ctrl(), altHeld = Alt();
            bool highlighting = altHeld && !Covered && !TitleVisible && hud is { UiVisible: true } &&
                settings?.Visible != true && !session.Camera.IsFree;
            if (hud is not null)
            {
                hud.CtrlHeld = ctrlHeld;
                hud.AltHeld = altHeld;
                hud.HighlightFocus = highlighting ? camera.Target : null;
            }
            if (objects is not null) objects.HighlightSigns = highlighting;
            if (!Covered && hud is not null && TitleVisible) titleBattle?.Advance(dt);
            if (skyArrival is not null)
            {
                ApplyCamera(session.Camera.Current);
                if (hud is not null) ApplyTitleCamera();
                ApplySkyArrival();
            }
            if (!Covered) UpdateAudio(dt);
            SendFocus();
            UpdateNav();
            UpdateNavOverlay();
            // Diagnostic isolation for the far-travel freeze: skip the runtime building/ground-item/lamps diff.
            if (Environment.GetEnvironmentVariable("MEITOU_METAL_NO_BUILDINGS") != "1") UpdateBuildings();
            // The interior view (docs/formats/zones.md): the player's building opens, its upper floors and their characters are hidden.
            // It must run before the character draw list (which carries aboveFloor) and before the shadow casters.
            UpdateInteriorView();
            memoryGuard?.Tick();
            // A live resize can leave the layer without a drawable for a moment: wait for the next one instead of spinning
            // the main thread (the window would stop responding to its own resize).
            if (!ctx.BeginFrame()) { Thread.Sleep(1); continue; }
            double drawableWait = display.LastDrawableWaitMilliseconds;
            long recordStart = Stopwatch.GetTimestamp();
            var sceneSize = new Vector2D<int>(ctx.SceneWidth, ctx.SceneHeight);
            // The editor's own scene replaces the world picture: the shared host draws the character and its platform into the stage texture the interface shows.
            if (hud is { EditorOpen: true }) DrawEditorStage(size.X, size.Y);
            // TAA's mip bias: the scene samples at the render scale, the history at the display size (PostProcess's TAA rule).
            ctx.LodBias = noTaa || spatial ? 0 : MathF.Log2(sceneSize.X / (float)Math.Max(size.X, 1)) - 0.5f;
            // The still's objects are all resident before the first picture, as the Vulkan screenshot path settles them (its Settle
            // opens its own frame; here the frame is the first one's).
            if (frames == 0) { objects?.Settle(camera.Eye); foliage?.Settle(camera.Eye); }
            streamer?.Update(camera.Eye);
            foliage?.Update(camera.Eye);

            var taaJitter = noTaa || spatial ? Vector2.Zero : PostTaa.Offset(frames + 1, sceneSize.X, size.X);
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
                var hoverKeys = new List<(long Key, Vector4 Colour)>();
                bool outlining = config.HoverOutline && !session.Camera.IsFree && hud is { UiVisible: true } &&
                    !Covered && settings?.Visible != true;
                if (outlining)
                {
                    if (highlighting)
                        hoverKeys.AddRange(hud!.HighlightTargets(camera.Target, PlayerInterface.HighlightRadius)
                            .Select(t => (DrawKey(t.Id), t.Colour)));
                    else if (hud!.HoverTarget is { } hover)
                        hoverKeys.Add((DrawKey(hover.Id), hover.Colour));
                }
                characters.DrawHoverMasks(hoverOutline is null ? [] : hoverKeys, sceneSize.X, sceneSize.Y,
                    camera.View, camera.FieldOfView, camera.Eye);
                characters.DrawIcons();
            }
            // The frame's inputs, as WorldFrame.Draw prepares them: the weather at the camera, the atmosphere's tables for the sun and the
            // eye's height, the fog distance the light ends at, and the view distance that keeps the haze complete before the far plane.
            bool titleLook = hud is not null && TitleVisible;
            float hour = presentationHour ?? (titleLook ? TitleHour : (float)session.Clock.HourOfDay);
            var aspect = sceneSize.X / (float)Math.Max(sceneSize.Y, 1);
            var eye = camera.Eye;
            var view = camera.View;
            var rotation = view with { M41 = 0, M42 = 0, M43 = 0 };
            var viewProjection = rotation * camera.Projection(aspect, 1, 1000);
            // The title stages the world as the poster: clear morning light, the sun right of the view, the moons full and the ruin
            // beside the logo. Save the gameplay values and put them back after the world pass.
            var savedClock = scene.Clock;
            var savedSkyWeather = sky.Weather;
            float? savedCloudCoverage = sky.CloudCoverage;
            var savedCloudWind = sky.CloudWind;
            var savedPlanetTurn = sky.PlanetTurn;
            float savedPlanetFill = sky.PlanetFill;
            var savedFill = characters?.Fill ?? default;
            bool savedHidePlaced = fogVolumes.HidePlaced;
            if (titleLook)
            {
                float fill = Math.Clamp(scene.Clock.SunDirection(hour).Y / MathF.Max(scene.Clock.SunDirection(TitleHour).Y, 1e-3f), 0, 1);
                fill = fill * fill * (3 - 2 * fill);
                sky.Weather = Meitou.Data.World.SkyWeather.Default;
                sky.CloudCoverage = 0.38f;
                sky.CloudWind = TitleComposition.CloudWind(camera);
                sky.PlanetTurn = TitleComposition.MoonTurn(camera);
                sky.PlanetFill = 0.8f * fill;
                scene.Clock = scene.Clock with { SunTurn = TitleComposition.SunTurn(scene.Clock, hour, camera) };
                if (characters is not null) characters.Fill = new Vector3(0.064f, 0.052f, 0.04f) * fill;
                fogVolumes.HidePlaced = true;
            }
            var sun = scene.Clock.SunDirection(hour);
            const bool held = true;   // a still picture: the weather's timers snap, as the post chain's InstantAdaptation does
            if (!titleLook && weather is { } current)
            {
                current.Update(eye, hour, sun.Y, held);
                current.Apply(sky);
            }
            if (!titleLook && surfaces is { } weatherSurfaces)
            {
                weatherSurfaces.GameTime = hour;
                weatherSurfaces.Apply(weather?.State ?? Meitou.Data.World.WeatherState.Clear);
            }
            var weatherState = titleLook ? Meitou.Data.World.WeatherState.Clear : weather?.State ?? Meitou.Data.World.WeatherState.Clear;
            water?.Animate(session.Clock.HoursSinceStart, weatherState.WindDirection, weatherState.WindSpeed);
            // The weather's particles and map placers step on the game clock; the draw follows the scene effects.
            if (particles is not null && !titleLook)
            {
                if (weather is not null)
                {
                    if (weatherAreas is { } areas)
                    {
                        var (cx, cz) = WeatherAreas.CellOf(eye.X, eye.Z);
                        if (WeatherAreas.InMap(cx, cz)) particles.World.Area = RegionArea.At(areas, eye.X, eye.Z);
                    }
                    particles.SetWeather(weather.EffectInput());
                }
                particles.World.Density = o.ParticleDensity;
                particles.Update(session.Clock.HoursSinceStart * 3600, camera);
            }
            sky.SetEye(eye, heights.HeightAt, float.NegativeInfinity);
            sky.Eye = eye;
            sky.Clock = scene.Clock;
            sky.Day = weather?.Day ?? 0;
            sky.Hour = hour;
            sky.StarHours = session.Clock.HoursSinceStart;
            sky.StarSeconds = (float)(now % 3600);
            var (colours, light) = sky.Prepare(sun, eye.Y, o.FogDistance + 3 * Math.Max(eye.Y, 0));
            if (titleLook) sky.StepClouds((float)dt);
            else if (weather is { } drift) sky.StepClouds(drift.Times.Game); else sky.StepClouds(held);
            // Far enough that the haze closes before the far plane and the water quad ends (WorldFrame.Draw's rule).
            camera.ViewDistance = Math.Max(camera.MinViewDistance, light.FogDistance / 0.7f);
            fogVolumes.Update(eye, camera.Forward, camera.FieldOfView, aspect, camera.ViewDistance, sun.Y, true, null, sky.FogCullDistance);
            // The scene's projection, its frustum and the interior masks all use the same jittered view: the mask is read with the
            // scene's pixel coordinates (texelFetch), and the casters' LOD needs the rendered pixel scale.
            render.TerrainPixelScale = Meitou.Data.World.TerrainLod.ProjectionScale(sceneSize.Y, camera.FieldOfView);
            var worldProjection = view * Jitter.Apply(camera.Projection(aspect, camera.Near, camera.ViewDistance), taaJitter, sceneSize.X, sceneSize.Y);
            var frustum = WorldCamera.FrustumPlanes(worldProjection);
            // The near-view interior masks, before the scene's passes: the terrain, foliage and map features over a mask discard their
            // fragments between its front and back faces (the game's InteriorMask compositor node).
            interiorMask?.Record(worldProjection, eye, sceneSize.X, sceneSize.Y);
            // The world's lamps, and the lanterns the characters carry, in this frame's grid.
            if (lamps is not null)
            {
                lamps.Moving.Clear();
                characters?.CollectLamps(lamps.Moving);
                lamps.Update(eye, objects, WorldLights.DaylightFactor(hour, scene.Clock.Sunrise, scene.Clock.Sunset));
            }
            // The casters' cascades, before the scene reads the receiver block.
            shadow.RangeCap = fogVolumes.AtmosphereDistance;
            shadow.SetFogHide(fogVolumes.ShadowHideDistance, fogVolumes.ShadowHidePending);
            shadow.Temporal = mode is MetalUpscalerMode.Temporal or MetalUpscalerMode.Taa;
            var shadowView = new Meitou.Data.World.ShadowView(eye, camera.Forward, Vector3.Normalize(new Vector3(view.M12, view.M22, view.M32)), camera.FieldOfView,
                aspect, camera.Near, camera.Distance);
            ShadowPass.LandmarkCasters? landmarkCasters = null;
            if (render.Objects && objects is { HasLandmarks: true } landmarkObjects)
                landmarkCasters = new ShadowPass.LandmarkCasters(
                    spheres => landmarkObjects.LandmarkCasters(camera.Eye, spheres),
                    (_, worldToClip, planes, lodEye) => landmarkObjects.DrawLandmarksDepth(worldToClip, lodEye, planes, render, terrain));
            shadow.Render(shadowView, light.SunDirection, (cascade, worldToClip, planes, lodEye) =>
            {
                float coarse = shadow.Meitou && ShadowLod.Applies(cascade.Index) ? (float)cascade.Texel : 0;
                terrain.DrawDepth(worldToClip, lodEye, planes, render, coarse);
                if (render.Objects && objects is not null) objects.DrawDepth(worldToClip, lodEye, planes, render, terrain, coarse);
                characters?.DrawDepth(worldToClip, lodEye, planes, ShadowLod.ObjectLodFloor(coarse, render.TerrainPixelScale));
                if (foliage is not null)
                {
                    foliage.DrawDepth(worldToClip, lodEye, planes, render, terrain, maxRange: shadow.EffectiveRange * 1.2f,
                        minSize: shadow.MinFoliageCaster(cascade), texel: (float)cascade.Texel, coarse: coarse > 0);
                }
            }, sunHeight: sky.ShadowSunHeight, landmarks: landmarkCasters);

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
            terrain.BeginFrame();
            terrain.InteriorCut = interiorMask;
            terrain.Draw(worldProjection, eye, frustum, render, light);
            terrain.InteriorCut = null;
            if (render.Objects && objects is not null)
            {
                objects.InteriorCut = interiorMask;
                objects.Draw(worldProjection, eye, frustum, render, light.SunDirection, light.FogColour, light.FogDistance, terrain);
                objects.InteriorCut = null;
            }
            foliage?.InteriorCut = interiorMask;
            foliage?.Draw(worldProjection, eye, frustum, render, light.SunDirection, light.FogColour, light.FogDistance, terrain);
            if (foliage is not null) foliage.InteriorCut = null;
            // Characters after the opaque geometry: they alone write the scene's alpha (the SSAO character mask in the Vulkan post chain).
            characters?.Draw(worldProjection, eye, frustum, light.SunDirection, light.FogColour, light.FogDistance);
            water?.Draw(worldProjection, eye, light, colours, (float)session.Clock.HoursSinceStart, camera.ViewDistance * 1.5f);

            ctx.EndHostPass(cmd);
            cmd.EndRendering();
            ctx.EndNative(cmd);

            var postInputs = new PostComposite.SceneInputs(eye, view, camera.FieldOfView, aspect, camera.Near, camera.ViewDistance,
                characters is not null && characters.DrawnCharacters > 0, water is not null ? Meitou.Data.World.WorldWater.Height : null);
            composite.HeatHazeAmount = titleLook ? 0 : weatherState.HeatHaze;
            composite.HeatHazeHours = session.Clock.HoursSinceStart;
            // Fog volumes, light shafts and SSAO are scene effects: they run before the temporal resolve, as the Vulkan chain does them.
            var sceneEffects = (MetalCommandList)ctx.BeginNative("post scene effects");
            if (shadow.Enabled)
            {
                float darkening = WorldFrame.ShaftDarkening(o.Post, light, sun.Y) *
                    WorldFrame.ShaftCloudFade(WorldFrame.Overcast(sky.CloudDensity, surfaces?.Rain ?? 0));
                float shaftFar = Math.Min(camera.ViewDistance, sky.HazeCompleteDistance ?? sky.HazeDistance);
                float shaftFloor = terrain.HeightAt(eye.X, eye.Z);
                if (water is not null) shaftFloor = Math.Max(shaftFloor, Meitou.Data.World.WorldWater.Height);
                composite.RunLightShafts(sceneEffects, sceneColour, depth, postInputs, o.Post, darkening, shaftFar, shaftFloor,
                    WorldFrame.ShaftAirByTime(o.Post, scene.Clock, hour), !noTaa);
            }
            composite.ApplyScene(sceneEffects, sceneColour, depth, postInputs, fogVolumes.UsedData > 0, o.Post.Ssao);
            ctx.EndNative(sceneEffects);

            // The weather's and map placers' particles, over the scene after SSAO and before the temporal resolve. Metal draws
            // them full size for now (the low-resolution particle targets are Vulkan-only): the game's queue 84.
            if (particles is not null && !titleLook && Environment.GetEnvironmentVariable("MEITOU_METAL_NO_PARTICLE_DRAW") != "1")
            {
                float particleNear = camera.Near;
                var particleNearProjection = view * Jitter.Apply(
                    camera.Projection(aspect, Math.Min(0.5f, particleNear * 0.5f), particleNear), taaJitter, sceneSize.X, sceneSize.Y);
                particles.Prepare(worldProjection, particleNearProjection, particleNear, view, eye, light.SunDirection,
                    0, sceneSize.X * sceneSize.Y, forcedDivisor: 1);
                if (particles.FullCount > 0)
                {
                    var particleCmd = (MetalCommandList)ctx.BeginNative("particles");
                    var particleTargets = PassTargets.Of(sceneColour, depth);
                    particleCmd.BeginRendering(particleTargets.Rendering);
                    ctx.BeginHostPass(particleCmd, particleTargets, DrawState.Scene(particleTargets.Formats));
                    particles.Record(1);
                    ctx.EndHostPass(particleCmd);
                    particleCmd.EndRendering();
                    ctx.EndNative(particleCmd);
                }
            }

            // The temporal resolve: the jittered scene against the history, at the scene's size (the vendor upscalers are not ported yet).
            GpuTexture resolved;
            if (noTaa) resolved = sceneColour;
            else
            {
                var taaCmd = (MetalCommandList)ctx.BeginNative("post taa");
                resolved = taa.Draw(taaCmd, sceneColour, depth, eye, view, camera.FieldOfView, aspect, camera.Near, camera.ViewDistance, taaJitter, size.X, size.Y, (float)dt);
                ctx.EndNative(taaCmd);
            }

            // The composite: the scene's HDR colour through the game's tone map into the backbuffer the present shows.
            var post = (MetalCommandList)ctx.BeginNative("post composite");
            composite.Draw(post, resolved, depth, backbuffer, o.Post, (sky.MinLuminance, sky.MaxLuminance), postInputs, temporal: !noTaa);
            ctx.EndNative(post);
            if (titleLook)
            {
                scene.Clock = savedClock;
                sky.Weather = savedSkyWeather;
                sky.CloudCoverage = savedCloudCoverage;
                sky.CloudWind = savedCloudWind;
                sky.PlanetTurn = savedPlanetTurn;
                sky.PlanetFill = savedPlanetFill;
                if (characters is not null) characters.Fill = savedFill;
                fogVolumes.HidePlaced = savedHidePlaced;
            }
            // The overlays that sit on the finished picture, before the interface: the move marker, the navmesh debug view and the hover outline.
            var (overlayNear, overlayFar) = camera.Slices().Last();
            var overlayProjection = view * camera.Projection(aspect, overlayNear, overlayFar);
            if (moveMarker is { Strength: > 0 })
                moveMarker.Record(backbuffer, size.X, size.Y, depth, sceneSize.X, sceneSize.Y, overlayProjection, eye, sun);
            if (navOverlay is { Visible: true })
                navOverlay.Record(backbuffer, size.X, size.Y, depth, sceneSize.X, sceneSize.Y, overlayProjection, eye);
            if (hoverOutline is not null && characters is { HoverMasks.Count: > 0 } hovered)
                hoverOutline.Record(backbuffer, size.X, size.Y, hovered.HoverMasks, depth, sceneSize.X, sceneSize.Y,
                    overlayNear, overlayFar, overlayProjection);
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
                if (settings?.Visible == true)
                {
                    settings.Draw(size.X, size.Y);
                    wrote = true;
                }
                if (wrote) overlay.Flush(size.X, size.Y);
                // The scene changes use the same Kenshi loading screen as Vulkan; it covers the world until the sky arrival is done.
                if (loading is not null && (sceneLoad is not null || loading.Exiting))
                {
                    overlay.Target = backbuffer;
                    loading.Draw(size.X, size.Y);
                }
            }
            ctx.EndFrame();
            // Mark the covered frame: the next one may run the next stage (which can hold the thread).
            if (sceneLoad is { } pendingLoad) pendingLoad.Shown = true;
            lastWait = drawableWait;
            lastRecord = Stopwatch.GetElapsedTime(recordStart).TotalMilliseconds;
            frames++;
            // Once the terrain, object and foliage streamers have been quiet for two seconds, the large decode/upload
            // buffers are garbage. Compact at that idle point (at most once a minute) so the LOH does not keep them
            // for the rest of the session. Active streaming is never interrupted.
            bool streamingIdle = (terrainTextures?.Idle ?? true) && (objects?.Pending ?? 0) == 0 && (foliage?.Pending ?? 0) == 0;
            if (!streamingIdle) streamingIdleSince = 0;
            else if (streamingIdleSince == 0) streamingIdleSince = now;
            var managedBefore = GC.GetGCMemoryInfo();
            bool managedHighWater = managedBefore.HeapSizeBytes > 4L << 30;
            if ((streamingIdle && now - streamingIdleSince >= 2 || managedHighWater) && now - lastManagedTrim >= 60)
            {
                lastManagedTrim = now;
                var before = managedBefore;
                // Compacting the LOH is a stop-the-world move of every large object: on a 4+ GB heap it stalled
                // this loop for 1.6 s. Do it where a loading screen or a menu covers the pause, or when the heap
                // has grown past 4 GB; otherwise a background collection is enough and does not move live arrays.
                bool safePoint = TitleVisible || settings?.Visible == true || sceneLoad is not null || loading?.Exiting == true;
                bool compact = safePoint || managedHighWater;
                if (compact)
                {
                    GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                }
                else GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: false, compacting: false);
                var after = GC.GetGCMemoryInfo();
                long loh = after.GenerationInfo.Length > 3 ? after.GenerationInfo[3].SizeAfterBytes : 0;
                if (trace)
                    Console.WriteLine($"metal     idle trim ({(compact ? "compacting" : "background")}): " +
                        $"heap {before.HeapSizeBytes / 1048576} -> {after.HeapSizeBytes / 1048576} MB, LOH {loh / 1048576} MB");
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
        void SaveSettings()
        {
            config.VSync = display.VSync;
            config.FpsLimit = fpsLimit;
            config.FullScreen = BorderlessFullScreen.IsOn(window);
            config.MetalUpscaler = mode;
            config.MetalRenderScale = ctx.RenderScale;
            if (hud is not null) config.UiMode = hud.UiMode;
            config.Bindings = session.Bindings.ToDictionary();
            if (settingsValues is not null)
            {
                foreach (var slider in settingsValues.Sliders)
                {
                    // These have their own config fields; the rest use the same graphics keys as the Vulkan settings.
                    if (SessionSliders.Contains(slider.Label) || slider.Label is SettingsMenu.MetalPresetSlider ||
                        slider.Label == WorldFrame.UpscalerSliders[1]) continue;
                    config.Graphics[slider.Label] = slider.Get();
                }
                foreach (var toggle in settingsValues.Toggles)
                    config.Graphics["Meitou: " + toggle.Label] = toggle.Get() ? 1 : 0;
            }
            config.Save();
        }
        // Let the queue finish before the renderers release views, buffers and bindless entries; a quit during a
        // streaming upload otherwise freed resources the GPU was still reading and could fault on the way out.
        SaveSettings();
        display.SetFullScreenPresentation(false);
        loading?.Dispose();
        loading = null;
        ctx.Finish();
        hud?.DisposeUi();
        overlay?.Dispose();
        characters?.Dispose();
        foliage?.Dispose();
        water?.Dispose();
        interiorMask?.Dispose();
        objects?.Dispose();
        lamps?.Dispose();
        particles?.Dispose();
        terrain.Dispose();   // owns the textures it was given
        streamer?.Dispose();
        sceneColour?.Dispose();
        shadow.Dispose();
        composite.Dispose();
        taa.Dispose();
        metalFx?.Dispose();           // PostTaa owns it while selected; safe to dispose again after a preset switch
        metalFxSpatial?.Dispose();
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
