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
var computeFunction = libraries[2].Library.NewFunction((NSString)"compute_main");

// A compute pipeline over a storage image: the engine's ocean FFT, Hi-Z pyramid and fog passes
// are all of this shape. The target is rgba16f, written by the compute pass and sampled back.
var computeDescriptor = new MTLComputePipelineDescriptor(
    new ObjectiveCClass(ObjectiveC.objc_getClass("MTLComputePipelineDescriptor")).AllocInit());
computeDescriptor.ComputeFunction = computeFunction;
NSError computeError = default;
var computePipeline = device.NewComputePipelineState(computeDescriptor, MTLPipelineOption.None, IntPtr.Zero, ref computeError);
if (computeError.NativePtr != IntPtr.Zero)
    throw new InvalidOperationException("compute pipeline failed");

var computedDescriptor = new MTLTextureDescriptor(
    new ObjectiveCClass(ObjectiveC.objc_getClass("MTLTextureDescriptor")).AllocInit());
computedDescriptor.TextureType = MTLTextureType.Type2D;
computedDescriptor.PixelFormat = MTLPixelFormat.RGBA16Float;
computedDescriptor.Width = 64;
computedDescriptor.Height = 64;
computedDescriptor.StorageMode = MTLStorageMode.Private;
computedDescriptor.Usage = MTLTextureUsage.ShaderRead | MTLTextureUsage.ShaderWrite;
var computed = device.NewTexture(computedDescriptor);

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

var tableColors = new[]
{
    new byte[] { 255, 255, 255, 255 },
    new byte[] { 255, 120, 0, 255 },
    new byte[] { 0, 255, 255, 255 },
    new byte[] { 255, 0, 255, 255 },
};

// The bindless table: separate textures and separate samplers, both indexed at runtime - the
// engine's set 1. On Metal 3 (Apple silicon) this is the model MoltenVK uses too: a buffer of
// resource handles, and an argument buffer that holds the address of that buffer.
var tableTextures = new MTLTexture[tableColors.Length];
var tableSamplers = new MTLSamplerState[tableColors.Length];
for (int i = 0; i < tableColors.Length; i++)
{
    var tableTextureDescriptor = new MTLTextureDescriptor(
        new ObjectiveCClass(ObjectiveC.objc_getClass("MTLTextureDescriptor")).AllocInit());
    tableTextureDescriptor.TextureType = MTLTextureType.Type2D;
    tableTextureDescriptor.PixelFormat = MTLPixelFormat.RGBA8Unorm;
    tableTextureDescriptor.Width = 4;
    tableTextureDescriptor.Height = 4;
    tableTextureDescriptor.StorageMode = MTLStorageMode.Shared;
    tableTextureDescriptor.Usage = MTLTextureUsage.ShaderRead;
    var tableTexture = device.NewTexture(tableTextureDescriptor);

    var bytes = new byte[4 * 4 * 4];
    for (int p = 0; p < 4 * 4; p++)
        Array.Copy(tableColors[i], 0, bytes, p * 4, 4);
    var tablePixelBytes = Marshal.AllocHGlobal(bytes.Length);
    Marshal.Copy(bytes, 0, tablePixelBytes, bytes.Length);
    tableTexture.ReplaceRegion(
        new MTLRegion { origin = new MTLOrigin { x = 0, y = 0, z = 0 }, size = new MTLSize { width = 4, height = 4, depth = 1 } },
        0,
        tablePixelBytes,
        4 * 4);
    Marshal.FreeHGlobal(tablePixelBytes);

    var tableSamplerDescriptor = new MTLSamplerDescriptor(
        new ObjectiveCClass(ObjectiveC.objc_getClass("MTLSamplerDescriptor")).AllocInit());
    tableSamplerDescriptor.MinFilter = MTLSamplerMinMagTextureMinFilter.Linear;
    tableSamplerDescriptor.MagFilter = MTLSamplerMinMagTextureMinFilter.Linear;

    tableTextures[i] = tableTexture;
    tableSamplers[i] = device.NewSamplerState(tableSamplerDescriptor);
}

// The handles: the argument buffer holds the table's arrays and the pointers to them, the way
// MoltenVK does it (the arrays live in the same buffer, at an offset the struct points at).
var handleCount = tableTextures.Length;
var tableArrayOffset = 16;
var samplerArrayOffset = tableArrayOffset + handleCount * 8;
var argumentBuffer = device.NewBuffer((ulong)(samplerArrayOffset + handleCount * 8), MTLResourceOptions.ResourceStorageModeShared);
for (int i = 0; i < handleCount; i++)
{
    Marshal.WriteInt64(argumentBuffer.Contents + tableArrayOffset + i * 8, (long)tableTextures[i].GpuResourceID._impl);
    Marshal.WriteInt64(argumentBuffer.Contents + samplerArrayOffset + i * 8, (long)tableSamplers[i].GpuResourceID._impl);
}
// The struct's two pointers: the addresses of the two arrays inside this buffer.
Marshal.WriteInt64(argumentBuffer.Contents, (long)(argumentBuffer.GpuAddress + (ulong)tableArrayOffset));
Marshal.WriteInt64(argumentBuffer.Contents + 8, (long)(argumentBuffer.GpuAddress + (ulong)samplerArrayOffset));
argumentBuffer.DidModifyRange(new NSRange { location = 0, length = (ulong)(samplerArrayOffset + handleCount * 8) });
Console.WriteLine($"addresses  arg={argumentBuffer.GpuAddress:x} table=+{tableArrayOffset} samplers=+{samplerArrayOffset} tex0={tableTextures[0].GpuResourceID._impl:x} samp0={tableSamplers[0].GpuResourceID._impl:x}");

var samplerDescriptor = new MTLSamplerDescriptor(
    new ObjectiveCClass(ObjectiveC.objc_getClass("MTLSamplerDescriptor")).AllocInit());
samplerDescriptor.MinFilter = MTLSamplerMinMagTextureMinFilter.Linear;
samplerDescriptor.MagFilter = MTLSamplerMinMagTextureMinFilter.Linear;
var sampler = device.NewSamplerState(samplerDescriptor);

// A texture table: four layers in one texture, the layer picked at runtime by the shader. This is
// the capability the engine's runtime-sized sampler arrays need; the table is a texture array
// here, and on Metal the general form of it is a tier-2 argument buffer (also in SharpMetal).
var layersDescriptor = new MTLTextureDescriptor(
    new ObjectiveCClass(ObjectiveC.objc_getClass("MTLTextureDescriptor")).AllocInit());
layersDescriptor.TextureType = MTLTextureType.Type2DArray;
layersDescriptor.PixelFormat = MTLPixelFormat.RGBA8Unorm;
layersDescriptor.Width = 4;
layersDescriptor.Height = 4;
layersDescriptor.ArrayLength = 4;
layersDescriptor.StorageMode = MTLStorageMode.Shared;
layersDescriptor.Usage = MTLTextureUsage.ShaderRead;
var layers = device.NewTexture(layersDescriptor);

var layerColors = new[]
{
    new byte[] { 255, 60, 40, 255 },
    new byte[] { 40, 255, 90, 255 },
    new byte[] { 60, 120, 255, 255 },
    new byte[] { 255, 220, 40, 255 },
};
for (uint slice = 0; slice < layerColors.Length; slice++)
{
    var bytes = new byte[4 * 4 * 4];
    for (int i = 0; i < 4 * 4; i++)
        Array.Copy(layerColors[slice], 0, bytes, i * 4, 4);
    var sliceBytes = Marshal.AllocHGlobal(bytes.Length);
    Marshal.Copy(bytes, 0, sliceBytes, bytes.Length);
    layers.ReplaceRegion(
        new MTLRegion { origin = new MTLOrigin { x = 0, y = 0, z = 0 }, size = new MTLSize { width = 4, height = 4, depth = 1 } },
        0,
        slice,
        sliceBytes,
        4 * 4,
        4 * 4);
    Marshal.FreeHGlobal(sliceBytes);
}

// The uniform block (std140) and the storage table (std430), both CPU-writable, bound at the
// indices the SPIR-V bindings asked for: buffer(1) for the uniform block, buffer(2) for the table.
var globals = device.NewBuffer(32, MTLResourceOptions.ResourceStorageModeShared);
var table = device.NewBuffer(4 * 16, MTLResourceOptions.ResourceStorageModeShared);
var tableItems = new[] { 1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f, 0f, 1f, 1f, 0f, 0f };
Marshal.Copy(ToBytes(tableItems), 0, table.Contents, tableItems.Length * 4);
table.DidModifyRange(new NSRange { location = 0, length = (ulong)(tableItems.Length * 4) });

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

    // The layer's drawable size follows its bounds, which follow the view's - but the view has
    // no bounds yet while the window is being created, so the size has to be set from the
    // window's own size (points) times the scale factor. Without it the drawable is 1x1.
    Console.WriteLine($"window    {window.Position.X},{window.Position.Y} {opts.Size.X}x{opts.Size.Y}");

    // Bring the app to the front, so a screenshot of the screen shows the window.
    var app = ObjectiveC.IntPtr_objc_msgSend(ObjectiveC.objc_getClass("NSApplication"), "sharedApplication");
    ObjectiveC.objc_msgSend(app, "activateIgnoringOtherApps:", true);

    var clock = Stopwatch.StartNew();

    window.Render += deltaTime =>
    {
        var drawable = layer.NextDrawable;
        if (drawable.DrawableID == 0) return;
        if (frames == 0) Console.WriteLine($"drawable  {drawable.Texture.Width}x{drawable.Texture.Height}");

        var cmd = queue.CommandBuffer();

        // The compute pass first, into the storage image the fragment stage samples.
        var computeEncoder = cmd.ComputeCommandEncoder();
        computeEncoder.SetComputePipelineState(computePipeline);
        computeEncoder.SetTexture(computed, 0);
        computeEncoder.SetBuffer(table, 0, 1);
        computeEncoder.DispatchThreadgroups(
            new MTLSize { width = 8, height = 8, depth = 1 },
            new MTLSize { width = 8, height = 8, depth = 1 });
        computeEncoder.EndEncoding();

        var rpdPtr = ObjectiveC.IntPtr_objc_msgSend(ObjectiveC.objc_getClass("MTLRenderPassDescriptor"), "renderPassDescriptor");
        var rpd = new MTLRenderPassDescriptor(rpdPtr);
        // The attachments come back as a struct copy, so a value-type property (the clear colour)
        // has to be written back into the array - otherwise the pass clears to whatever the
        // descriptor's memory happened to hold.
        var attachments = rpd.ColorAttachments;
        var colorAttachment = attachments.Object(0);
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
        attachments.SetObject(colorAttachment, 0);

        var encoder = cmd.RenderCommandEncoder(rpd);
        encoder.SetRenderPipelineState(pipeline);
        encoder.SetFragmentTexture(texture, 0);
        encoder.SetFragmentSamplerState(sampler, 0);
        encoder.SetFragmentBuffer(globals, 0, 1);
        encoder.SetFragmentBuffer(table, 0, 2);
        encoder.SetFragmentTexture(layers, 3);
        encoder.SetFragmentSamplerState(sampler, 3);
        encoder.SetFragmentTexture(computed, 4);
        encoder.SetFragmentSamplerState(sampler, 4);
        // The bindless table's argument buffer, at the buffer index the shader declares it.
        encoder.SetFragmentBuffer(argumentBuffer, 0, 6);

        // The uniform block, rewritten per frame: std140 puts the vec4 tint first, the float after.
        var time = (float)clock.Elapsed.TotalSeconds;
        var uniform = new[] { 1.00f, 0.95f, 0.90f, 1.00f, time, 0f, 0f, 0f };
        Marshal.Copy(ToBytes(uniform), 0, globals.Contents, uniform.Length * 4);
        globals.DidModifyRange(new NSRange { location = 0, length = (ulong)(uniform.Length * 4) });

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
    foreach (var stage in new[] { "vert", "frag", "comp" })
    {
        var source = Path.Combine(shaderDir, $"spike.{stage}");
        var spirv = Path.Combine(Path.GetTempPath(), $"spike.{stage}.spv");
        var metal = Path.Combine(Path.GetTempPath(), $"spike.{stage}.metal");

        Run("glslangValidator", $"-V --target-env vulkan1.1 \"{source}\" -o \"{spirv}\"");
        var entry = stage switch
        {
            "vert" => "vertex_main",
            "frag" => "fragment_main",
            _ => "compute_main",
        };
        // --msl-decoration-binding keeps the MSL resource indices equal to the SPIR-V binding
        // numbers, so a Metal backend can mirror the Vulkan binding scheme instead of carrying a
        // per-shader mapping table (without it spirv-cross renumbers by its own traversal order).
        //
        // The bindless table (set 6) goes into a tier-2 argument buffer in the device address
        // space; set 0 stays a set of ordinary bindings (--msl-discrete-descriptor-set 0). The
        // argument buffer takes the buffer index of its descriptor set, so the table's set must
        // not collide with the binding numbers of set 0 (0 to 4 here).
        var argumentBuffers = stage == "frag"
            ? " --msl-argument-buffers --msl-argument-buffer-tier 1 --msl-device-argument-buffer 6 --msl-discrete-descriptor-set 0"
            : "";
        Run("spirv-cross", $"--msl --msl-version 30000 --msl-decoration-binding{argumentBuffers} --rename-entry-point main {entry} {stage} \"{spirv}\" --output \"{metal}\"");

        stages.Add((stage, File.ReadAllText(metal)));
    }
    return stages;
}

static byte[] ToBytes(float[] values)
{
    var bytes = new byte[values.Length * 4];
    for (int i = 0; i < values.Length; i++)
        BitConverter.GetBytes(values[i]).CopyTo(bytes, i * 4);
    return bytes;
}

static void Run(string file, string arguments){
    var info = new ProcessStartInfo(file, arguments) { RedirectStandardError = true, RedirectStandardOutput = true };
    using var process = Process.Start(info) ?? throw new InvalidOperationException($"{file} did not start");
    var stdout = process.StandardOutput.ReadToEnd();
    var stderr = process.StandardError.ReadToEnd();
    process.WaitForExit();
    if (process.ExitCode != 0)
        throw new InvalidOperationException($"{file} failed: {stdout}{stderr}");
}
