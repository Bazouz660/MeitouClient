// Meitou Metal spike — stage 1: the shader chain the engine would need.
// GLSL -> SPIR-V (glslang) -> MSL (spirv-cross) -> runtime compile -> textured triangle.
// Local experiment (branch metal-spike), not part of the solution or the upstream PR.

using System.Diagnostics;
using System.Runtime.InteropServices;
using Silk.NET.GLFW;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using SharpMetal.Foundation;
using SharpMetal.Metal;
using SharpMetal.ObjectiveCCore;
using SharpMetal.QuartzCore;

ObjectiveC.LinkMetal();

// How many frames to present before exiting. The capture run uses a big number so the window
// stays up while a screenshot is taken; the default run is a quick self-test.
var framesToRun = 300;
if (args.Contains("--hold")) framesToRun = 1800;

var opts = WindowOptions.Default;
opts.Size = new Vector2D<int>(640, 400);
opts.Title = "meitou metal spike (stage 1: glsl -> spirv -> msl)";
var window = Window.Create(opts);
window.Initialize();

var device = MTLDevice.CreateSystemDefaultDevice();
Console.WriteLine($"device    {device.Name}");
var queue = device.NewCommandQueue();

// The shader chain: the engine compiles GLSL to SPIR-V with shaderc; Metal needs MSL, so the
// SPIR-V goes through spirv-cross (the same translator MoltenVK embeds). Here both run as tools.
// Each stage becomes its own library: spirv-cross names both stages' entry point "main0" and their
// interface structs "main0_out", so one combined source would collide. Metal takes the vertex
// function from one library and the fragment function from the other, so the collision never shows.
var shaderDir = Path.Combine(AppContext.BaseDirectory, "Shaders");
var stages = TranslateShaders(shaderDir);
Console.WriteLine($"msl       {string.Join(" + ", stages.Select(s => s.Name + ":" + s.Msl.Length))} chars");

var libraries = new List<(string Name, MTLLibrary Library)>();
foreach (var stage in stages)
{
    NSError compileError = default;
    var library = device.NewLibrary((NSString)stage.Msl, new MTLCompileOptions(), ref compileError);
    if (compileError.NativePtr != IntPtr.Zero)
        throw new InvalidOperationException($"msl compile failed ({stage.Name}): {compileError.LocalizedDescription}");
    libraries.Add((stage.Name, library));
}
var vertexFunction = libraries[0].Library.NewFunction((NSString)"vertex_main");
var fragmentFunction = libraries[1].Library.NewFunction((NSString)"fragment_main");

var pipelineDescriptor = new MTLRenderPipelineDescriptor(
    new ObjectiveCClass(ObjectiveC.objc_getClass("MTLRenderPipelineDescriptor")).AllocInit());
pipelineDescriptor.VertexFunction = vertexFunction;
pipelineDescriptor.FragmentFunction = fragmentFunction;
var attachmentArray = pipelineDescriptor.ColorAttachments;
var attachment = attachmentArray.Object(0);
attachment.PixelFormat = MTLPixelFormat.BGRA8Unorm;
attachmentArray.SetObject(attachment, 0);
NSError pipelineError = default;
var pipeline = device.NewRenderPipelineState(pipelineDescriptor, ref pipelineError);
if (pipelineError.NativePtr != IntPtr.Zero)
    throw new InvalidOperationException("pipeline failed");

// A 4x4 texture, so the sampled result is visible.
var textureDescriptor = new MTLTextureDescriptor(
    new ObjectiveCClass(ObjectiveC.objc_getClass("MTLTextureDescriptor")).AllocInit());
textureDescriptor.TextureType = MTLTextureType.Type2D;
textureDescriptor.PixelFormat = MTLPixelFormat.RGBA8Unorm;
textureDescriptor.Width = 4;
textureDescriptor.Height = 4;
textureDescriptor.StorageMode = MTLStorageMode.Shared;
textureDescriptor.Usage = MTLTextureUsage.ShaderRead;
var texture = device.NewTexture(textureDescriptor);

var pixels = new byte[4 * 4 * 4];
for (int y = 0; y < 4; y++)
for (int x = 0; x < 4; x++)
{
    int i = (y * 4 + x) * 4;
    bool checker = ((x ^ y) & 1) == 0;
    pixels[i + 0] = checker ? (byte)240 : (byte)40;
    pixels[i + 1] = checker ? (byte)90 : (byte)200;
    pixels[i + 2] = checker ? (byte)60 : (byte)230;
    pixels[i + 3] = 255;
}
var pixelBytes = Marshal.AllocHGlobal(pixels.Length);
Marshal.Copy(pixels, 0, pixelBytes, pixels.Length);
texture.ReplaceRegion(
    new MTLRegion { origin = new MTLOrigin { x = 0, y = 0, z = 0 }, size = new MTLSize { width = 4, height = 4, depth = 1 } },
    0,
    pixelBytes,
    4 * 4);
Marshal.FreeHGlobal(pixelBytes);

var samplerDescriptor = new MTLSamplerDescriptor(
    new ObjectiveCClass(ObjectiveC.objc_getClass("MTLSamplerDescriptor")).AllocInit());
samplerDescriptor.MinFilter = MTLSamplerMinMagFilter.Linear;
samplerDescriptor.MagFilter = MTLSamplerMinMagFilter.Linear;
var sampler = device.NewSamplerState(samplerDescriptor);

var frames = 0;

unsafe
{
    // NSWindow* from GLFW. Silk.NET.GLFW has no native-access binding for it, so take the export
    // from the same brew dylib the engine's runtime already loads (meitou-run.sh points DYLD at it).
    var glfw = NativeLibrary.Load("libglfw.3.dylib");
    var getCocoaWindow = (delegate* unmanaged<WindowHandle*, IntPtr>)NativeLibrary.GetExport(glfw, "glfwGetCocoaWindow");
    var nsWindow = getCocoaWindow((WindowHandle*)window.Native!.Glfw!.Value);
    Console.WriteLine($"nswindow  0x{nsWindow:x}");

    // Attach a CAMetalLayer to the window's content view. The layer has to fill the view
    // (NSViewWidthSizable | NSViewHeightSizable) or the drawable covers only part of the window;
    // its drawable size then follows the view's bounds times the scale factor, retina included.
    var contentView = ObjectiveC.IntPtr_objc_msgSend(nsWindow, "contentView");
    ObjectiveC.objc_msgSend(contentView, "setWantsLayer:", true);
    var layerPtr = new ObjectiveCClass(ObjectiveC.objc_getClass("CAMetalLayer")).AllocInit();
    ObjectiveC.objc_msgSend(layerPtr, "setAutoresizingMask:", (byte)18);
    ObjectiveC.objc_msgSend(contentView, "setLayer:", layerPtr);
    var layer = new CAMetalLayer(layerPtr);
    layer.Device = device;
    layer.PixelFormat = MTLPixelFormat.BGRA8Unorm;
    layer.FramebufferOnly = true;

    var clock = Stopwatch.StartNew();

    window.Render += deltaTime =>
    {
        var drawable = layer.NextDrawable;
        if (drawable.DrawableID == 0) return;

        var cmd = queue.CommandBuffer();
        var rpdPtr = ObjectiveC.IntPtr_objc_msgSend(ObjectiveC.objc_getClass("MTLRenderPassDescriptor"), "renderPassDescriptor");
        var rpd = new MTLRenderPassDescriptor(rpdPtr);
        var colorAttachment = rpd.ColorAttachments.Object(0);
        colorAttachment.Texture = drawable.Texture;
        colorAttachment.LoadAction = MTLLoadAction.Clear;
        colorAttachment.StoreAction = MTLStoreAction.Store;
        colorAttachment.ClearColor = new MTLClearColor
        {
            red = 0.08 + 0.04 * Math.Sin(clock.Elapsed.TotalSeconds),
            green = 0.08,
            blue = 0.12,
            alpha = 1.0,
        };

        var encoder = cmd.RenderCommandEncoder(rpd);
        encoder.SetRenderPipelineState(pipeline);
        encoder.SetFragmentTexture(texture, 0);
        encoder.SetFragmentSamplerState(sampler, 0);
        encoder.DrawPrimitives(MTLPrimitiveType.Triangle, 0, 3);
        encoder.EndEncoding();
        cmd.PresentDrawable(drawable);
        cmd.Commit();

        if (++frames % 100 == 0) Console.WriteLine($"frame     {frames}");
        if (frames >= framesToRun) window.Close();
    };
}

window.Run();
Console.WriteLine($"spike ok: {frames} frames presented (glsl -> spirv -> msl, sampled texture)");

// GLSL -> SPIR-V -> MSL. The engine does the first step with shaderc; the second step is what a
// Metal backend needs, and it is what MoltenVK does internally.
static List<(string Name, string Msl)> TranslateShaders(string shaderDir)
{
    var stages = new List<(string Name, string Msl)>();
    foreach (var stage in new[] { "vert", "frag" })
    {
        var source = Path.Combine(shaderDir, $"spike.{stage}");
        var spirv = Path.Combine(Path.GetTempPath(), $"spike.{stage}.spv");
        var metal = Path.Combine(Path.GetTempPath(), $"spike.{stage}.metal");

        Run("glslangValidator", $"-V --target-env vulkan1.1 \"{source}\" -o \"{spirv}\"");
        var entry = stage == "vert" ? "vertex_main" : "fragment_main";
        Run("spirv-cross", $"--msl --msl-version 20100 --rename-entry-point main {entry} {stage} \"{spirv}\" --output \"{metal}\"");

        stages.Add((stage, File.ReadAllText(metal)));
    }
    return stages;
}

static void Run(string file, string arguments)
{
    var info = new ProcessStartInfo(file, arguments) { RedirectStandardError = true, RedirectStandardOutput = true };
    using var process = Process.Start(info) ?? throw new InvalidOperationException($"{file} did not start");
    var stdout = process.StandardOutput.ReadToEnd();
    var stderr = process.StandardError.ReadToEnd();
    process.WaitForExit();
    if (process.ExitCode != 0)
        throw new InvalidOperationException($"{file} failed: {stdout}{stderr}");
}
