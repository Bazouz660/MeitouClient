// The game's sky on Metal: the engine's GLSL through the engine's compiler (shaderc) to SPIR-V, through spirv-cross to
// MSL in process, the engine's uniform block in a Metal buffer, the fullscreen triangle drawn by Metal and presented.
using System.Globalization;
using System.Numerics;
using Meitou.Data.World;
using Meitou.Metal;
using Meitou.Rendering;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using SharpMetal.Metal;
using SharpMetal.ObjectiveCCore;

var hour = 12f;
var frames = 300;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--hour" && i + 1 < args.Length) hour = float.Parse(args[++i], CultureInfo.InvariantCulture);
    else if (args[i] == "--hold") frames = 1800;
}

ObjectiveC.LinkMetal();

var options = WindowOptions.Default with { Size = new Vector2D<int>(960, 600), Title = "meitou metal sky" };
using var display = new MetalDisplay(options);
var sky = new MetalSky(display.Device, SkyRenderer.SimpleShader.Vertex, SkyRenderer.SimpleShader.SimpleFragment);
display.Activate();

var camera = new WorldCamera { Target = Vector3.Zero, Yaw = 0.9f, Pitch = 0.15f, Distance = 20 };
var sun = SkyClock.Fallback.SunDirection(hour);
var colours = SkyColours.For(sun);
Console.WriteLine($"device    {display.Device.Name}");
Console.WriteLine($"sun       {sun} at {hour}h");
Console.WriteLine($"colours   zenith {colours.Zenith} horizon {colours.Horizon} sun {colours.SunColour} twilight {colours.Twilight}");

int drawn = 0;
display.Window.Render += _ =>
{
    if (!display.TryBeginFrame(out var command, out var drawable)) return;
    var size = display.Window.FramebufferSize;
    var aspect = size.X / (float)Math.Max(size.Y, 1);
    // The sky is at infinity: the view's translation is dropped, as the Vulkan path does (WorldFrame.DrawLoadingSky).
    var view = camera.View;
    var rotation = view with { M41 = 0, M42 = 0, M43 = 0 };
    var viewProjection = rotation * camera.Projection(aspect, 1, 1000);
    Matrix4x4.Invert(viewProjection, out var inverse);
    var encoder = display.BeginPass(command, drawable, new MTLClearColor { red = 0, green = 0, blue = 0, alpha = 1 });
    sky.Render(encoder, inverse, colours);
    encoder.EndEncoding();
    display.Present(command, drawable);
    if (++drawn >= frames) display.Window.Close();
};

display.Window.Run();
Console.WriteLine($"ok: {drawn} frames");
