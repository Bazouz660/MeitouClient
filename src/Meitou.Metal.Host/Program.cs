// The sky through the Metal backend's seam: the engine's own SkyRenderer (IGpuContext) on a MetalContext —
// the same recording model the Vulkan path drives (a host pass, a guest segment inside it, the present).
using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Data.World;
using Meitou.Metal;
using Meitou.Rendering;
using Meitou.Rendering.Gpu;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using SharpMetal.Metal;
using SharpMetal.ObjectiveCCore;

var hour = 12f;
var frames = 300;
string? shot = null;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--hour" && i + 1 < args.Length) hour = float.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
    else if (args[i] == "--hold") frames = 1800;
    else if (args[i] == "--shot" && i + 1 < args.Length) { shot = args[++i]; frames = 10; }
}

ObjectiveC.LinkMetal();

var options = WindowOptions.Default with { Size = new Vector2D<int>(960, 600), Title = "meitou metal sky (the seam)" };
using var display = new MetalDisplay(options);
using var ctx = new MetalContext(display);
var sky = new SkyRenderer(ctx);
display.Activate();

var camera = new WorldCamera { Target = Vector3.Zero, Yaw = 0.9f, Pitch = 0.15f, Distance = 20 };
var sun = SkyClock.Fallback.SunDirection(hour);
var coloursNow = SkyColours.For(sun);
Console.WriteLine($"colours   zenith {coloursNow.Zenith} horizon {coloursNow.Horizon} sun {coloursNow.SunColour} twilight {coloursNow.Twilight}");
Console.WriteLine($"device    {display.Device.Name}");
Console.WriteLine($"sun       {sun} at {hour}h");

int drawn = 0;
display.Window.Render += _ =>
{
    if (!ctx.BeginFrame()) return;
    var backbuffer = ctx.Backbuffer;
    var targets = PassTargets.Of(backbuffer, null) with { Colour = new RenderTarget(backbuffer.Attachment(), LoadOp.Clear, new ClearColour(0, 0, 0, 1)) };
    var cmd = ctx.Frame.Commands;
    cmd.BeginRendering(targets.Rendering);
    ctx.BeginHostPass(cmd, targets, DrawState.For(targets.Formats, deviceDepthClamp: false));

    // The sky at infinity: the view's translation is dropped, as the Vulkan path does (WorldFrame.DrawLoadingSky).
    var size = display.Window.FramebufferSize;
    var aspect = size.X / (float)Math.Max(size.Y, 1);
    var view = camera.View;
    var rotation = view with { M41 = 0, M42 = 0, M43 = 0 };
    var viewProjection = rotation * camera.Projection(aspect, 1, 1000);
    var (colours, _) = sky.Prepare(sun, 0, 100000);
    sky.Draw(viewProjection, colours);

    ctx.EndHostPass(cmd);
    cmd.EndRendering();
    ctx.EndFrame();
    if (shot is { } path && drawn == 4) SaveBmp(ctx, path);
    if (++drawn >= frames) display.Window.Close();
};

display.Window.Run();
Console.WriteLine($"ok: {drawn} frames");

/// <summary>The backbuffer's picture as a BMP (rows bottom-up, BGRX).</summary>
static void SaveBmp(MetalContext ctx, string path)
{
    var backbuffer = ctx.Backbuffer;
    int w = backbuffer.Desc.Width, h = backbuffer.Desc.Height;
    var pixels = ctx.Readback(backbuffer);
    int stride = w * 4;
    var rows = new byte[54 + stride * h];
    rows[0] = (byte)'B'; rows[1] = (byte)'M';
    WriteInt(rows, 2, 54 + stride * h); WriteInt(rows, 10, 54); WriteInt(rows, 14, 40);
    WriteInt(rows, 18, w); WriteInt(rows, 22, h); rows[26] = 1; rows[28] = 32;
    for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int src = (h - 1 - y) * stride + x * 4, dst = 54 + y * stride + x * 4;
            rows[dst] = pixels[src + 2]; rows[dst + 1] = pixels[src + 1]; rows[dst + 2] = pixels[src]; rows[dst + 3] = 255;
        }
    File.WriteAllBytes(path, rows);
    Console.WriteLine($"shot      {path} ({w}x{h})");
}

static void WriteInt(byte[] b, int at, int value)
{
    b[at] = (byte)value; b[at + 1] = (byte)(value >> 8); b[at + 2] = (byte)(value >> 16); b[at + 3] = (byte)(value >> 24);
}
