#if MEITOU_METAL
// Metal exists on macOS only; the file is compiled only there (the csproj's MEITOU_METAL constant).
#pragma warning disable CA1416
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Numerics;
using Meitou.Data.Textures;
using Meitou.Engine;
using Meitou.Engine.Time;
using Meitou.Metal;
using Meitou.Rendering;
using Meitou.Rendering.Gpu;
using Silk.NET.Maths;
using Silk.NET.Windowing;

namespace Meitou.Game;

sealed partial class GameHost
{
    /// <summary>
    /// The Metal path (macOS): the game boots without the Vulkan renderer and draws the sky with the real <see cref="SkyRenderer"/>
    /// on the <see cref="MetalContext"/> — the seam's recording model as the Vulkan path drives it (a native segment opening the
    /// scene's host pass, the sky a guest segment in it, the frame committed with the present). The world is not drawn yet: the
    /// sky is the first pass of the port (docs/metal-backend.md).
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

        using var display = new MetalDisplay(WindowOptions.Default with
        {
            Size = new Vector2D<int>(o.Width, o.Height),
            Title = "Meitou (metal)",
            FramesPerSecond = 0,
            UpdatesPerSecond = 0,
        });
        using var ctx = new MetalContext(display);
        var window = display.Window;
        ctx.Samplers.MaxAnisotropy = o.Anisotropy;

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
        terrain.BeginFrame();

        // The world, the simulation's side only: the renderer is not made, so the camera rig and the clock are the game's own.
        var heights = new Meitou.Data.World.GroundHeights(scene.Window, scene.Coarse, scene.CoarseSize, WorldFrame.CoarseStep);
        var (walkability, systems, constants) = BootSimulation(heights, interactive: false);
        (camera, render) = WorldFrame.Setup(scene, o);
        session = new WorldSession(scene.Focus, (scene.X0, scene.Z0, scene.X1, scene.Z1), heights.HeightAt, o.Hour, config.TickRate,
            clock: GameClockFor(constants ??= scene.Database is { } clockDb ? Meitou.Data.Gameplay.GameConstants.FromDatabase(clockDb) : null, o.Hour, o.Day ?? GameClock.DefaultStartDay),
            simulation: worldSettings = new Meitou.Simulation.WorldSettings { Seed = g.Seed, MinPartitionSize = 128, Threads = Math.Max(1, config.SimThreads ?? Math.Clamp(Environment.ProcessorCount / 2, 1, 8)) },
            systems: systems, walkability: walkability);
        session.Camera.Place(new Vector2((float)(o.X ?? scene.Focus.X), (float)(o.Z ?? scene.Focus.Z)), (float)(o.Yaw ?? 30) * MathF.PI / 180,
            (float)(o.Pitch ?? Meitou.Data.World.KenshiCamera.InitialPitchDegrees) * MathF.PI / 180,
            (float)(o.Distance ?? Meitou.Data.World.KenshiCamera.InitialDistance));
        ApplyCamera(session.Camera.Current);
        // The streamer settles the fine heights and biome maps round the camera before the first frame draws (the still's camera does not move).
        streamer?.Settle(camera.Eye);
        if (streamer is { } settled) Console.WriteLine($"metal     {settled.Describe()}");

        var clock = Stopwatch.StartNew();
        double last = 0;
        int frames = 0;
        while (!window.IsClosing)
        {
            window.DoEvents();
            if (window.IsClosing) break;
            double now = clock.Elapsed.TotalSeconds, dt = now - last;
            last = now;
            session.Advance(dt);
            ApplyCamera(session.CameraAt());
            if (!ctx.BeginFrame()) continue;
            streamer?.Update(camera.Eye);

            // The scene's host pass, as SceneHost.Open does: a native segment, a rendering into the backbuffer (cleared to
            // transparent; the sky fills every pixel), the host pass the sky draws as a guest segment in.
            var cmd = (MetalCommandList)ctx.BeginNative("scene");
            var backbuffer = ctx.Backbuffer;
            var depth = ctx.Depth;
            var albedo = ctx.Albedo;
            var size = window.FramebufferSize;
            var targets = PassTargets.Of(backbuffer, depth, albedo) with
            {
                Colour = new RenderTarget(backbuffer.Attachment(), LoadOp.Clear, new ClearColour(0, 0, 0, 0)),
                Depth = new RenderTarget(depth.Attachment(), LoadOp.Clear, default),
            };
            cmd.BeginRendering(targets.Rendering);
            ctx.BeginHostPass(cmd, targets, DrawState.Scene(targets.Formats));

            // The frame's inputs, as WorldFrame.Draw prepares them: the weather at the camera, the atmosphere's tables for the sun and the
            // eye's height, the fog distance the light ends at, and the view distance that keeps the haze complete before the far plane.
            float hour = (float)session.Clock.HourOfDay;
            var aspect = size.X / (float)Math.Max(size.Y, 1);
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
            sky.Draw(viewProjection, colours);

            // The terrain: the camera's projection (near 0.5 .. the view distance), the quadtree's LOD in rendered pixels, the sky's light.
            render.TerrainPixelScale = Meitou.Data.World.TerrainLod.ProjectionScale(size.Y, camera.FieldOfView);
            var worldProjection = view * camera.Projection(aspect, camera.Near, camera.ViewDistance);
            var frustum = WorldCamera.FrustumPlanes(worldProjection);
            terrain.BeginFrame();
            terrain.Draw(worldProjection, eye, frustum, render, light);

            ctx.EndHostPass(cmd);
            cmd.EndRendering();
            ctx.EndNative(cmd);
            ctx.EndFrame();
            frames++;
            if (o.Screenshot is { } shot && frames == 40) { Capture(ctx, backbuffer, shot); break; }   // one picture, as the Vulkan flow's does
            if (g.QuitAfter is { } quitAfter && now >= quitAfter) break;
        }
        terrain.Dispose();   // owns the textures it was given
        streamer?.Dispose();
        surfaces?.Dispose();
        sky.Dispose();
        Console.WriteLine($"metal     {frames} frames");
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
