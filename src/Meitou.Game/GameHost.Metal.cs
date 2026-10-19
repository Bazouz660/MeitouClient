#if MEITOU_METAL
// Metal exists on macOS only; the file is compiled only there (the csproj's MEITOU_METAL constant).
#pragma warning disable CA1416
using System.Diagnostics;
using System.Numerics;
using Meitou.Engine;
using Meitou.Engine.Time;
using Meitou.Metal;
using Meitou.Rendering;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using SharpMetal.Metal;

namespace Meitou.Game;

sealed partial class GameHost
{
    /// <summary>
    /// The Metal path (macOS): the game boots without the Vulkan renderer and draws the sky with Metal. The engine's own
    /// GLSL goes through the engine's compiler to SPIR-V and through spirv-cross to MSL in process; the uniform block is
    /// the engine's own. The world is not drawn yet: the sky is the first pass of the port (docs/metal-backend.md).
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
        var window = display.Window;
        var sky = new MetalSky(display.Device, SkyRenderer.SimpleShader.Vertex, SkyRenderer.SimpleShader.SimpleFragment);
        display.Activate();
        Splash?.Dispose();

        // The world, the simulation's side only: the renderer is not made, so the camera rig and the clock are the game's own.
        var heights = new Meitou.Data.World.GroundHeights(scene.Window, scene.Coarse, scene.CoarseSize, WorldFrame.CoarseStep);
        var (walkability, systems, constants) = BootSimulation(heights, interactive: false);
        (camera, render) = WorldFrame.Setup(scene, o);
        session = new WorldSession(scene.Focus, (scene.X0, scene.Z0, scene.X1, scene.Z1), heights.HeightAt, o.Hour, config.TickRate,
            clock: GameClockFor(constants ??= scene.Database is { } clockDb ? Meitou.Data.Gameplay.GameConstants.FromDatabase(clockDb) : null, o.Hour, o.Day ?? GameClock.DefaultStartDay),
            simulation: worldSettings = new Meitou.Simulation.WorldSettings { Seed = g.Seed, MinPartitionSize = 128, Threads = Math.Max(1, config.SimThreads ?? Math.Clamp(Environment.ProcessorCount / 2, 1, 8)) },
            systems: systems, walkability: walkability);
        session.Camera.Place(new Vector2((float)(o.X ?? 0), (float)(o.Z ?? 0)), (float)(o.Yaw ?? 30) * MathF.PI / 180,
            (float)(o.Pitch ?? Meitou.Data.World.KenshiCamera.InitialPitchDegrees) * MathF.PI / 180,
            (float)(o.Distance ?? Meitou.Data.World.KenshiCamera.InitialDistance));
        ApplyCamera(session.Camera.Current);

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
            if (!display.TryBeginFrame(out var command, out var drawable)) continue;

            // The sky: the engine's own shader and colours, the camera's own view (the translation dropped, as the Vulkan path does).
            var size = window.FramebufferSize;
            var aspect = size.X / (float)Math.Max(size.Y, 1);
            var view = camera.View;
            var rotation = view with { M41 = 0, M42 = 0, M43 = 0 };
            var viewProjection = rotation * camera.Projection(aspect, 1, 1000);
            Matrix4x4.Invert(viewProjection, out var inverse);
            var sun = scene.Clock.SunDirection((float)session.Clock.HourOfDay);
            var colours = SkyColours.For(sun);
            var encoder = display.BeginPass(command, drawable, new MTLClearColor { red = 0, green = 0, blue = 0, alpha = 1 });
            sky.Render(encoder, inverse, colours);
            encoder.EndEncoding();
            display.Present(command, drawable);
            frames++;
            if (g.QuitAfter is { } quitAfter && now >= quitAfter) break;
        }
        Console.WriteLine($"metal     {frames} frames");
        return 0;
    }
}
#endif
