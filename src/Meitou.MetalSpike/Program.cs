// Meitou Metal spike — stage 0: prove the vertical slice C# -> SharpMetal -> CAMetalLayer on the GLFW window.
// Animated clear + a triangle from runtime-compiled MSL (the seam a SPIRV-Cross step would feed later).
// 300 frames, exit. Local experiment (branch metal-spike), not part of the solution or the upstream PR.

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

const string msl = """
    #include <metal_stdlib>
    using namespace metal;

    struct VOut {
        float4 position [[position]];
        float3 color;
    };

    vertex VOut vertexMain(uint vid [[vertex_id]]) {
        float2 pos[3] = { float2(-0.7, -0.7), float2(0.7, -0.7), float2(0.0, 0.7) };
        float3 col[3] = { float3(1.0, 0.25, 0.2), float3(0.2, 1.0, 0.35), float3(0.25, 0.45, 1.0) };
        VOut o;
        o.position = float4(pos[vid], 0.0, 1.0);
        o.color = col[vid];
        return o;
    }

    fragment float4 fragmentMain(VOut in [[stage_in]]) {
        return float4(in.color, 1.0);
    }
    """;

var opts = WindowOptions.Default;
opts.Size = new Vector2D<int>(640, 400);
opts.Title = "meitou metal spike (stage 0: clear + triangle)";
var window = Window.Create(opts);
window.Initialize();

var device = MTLDevice.CreateSystemDefaultDevice();
Console.WriteLine($"device    {device.Name}");
var queue = device.NewCommandQueue();

// Runtime-compile the MSL — the exact seam a GLSL->SPIR-V->MSL step would feed.
NSError compileError = default;
var library = device.NewLibrary((NSString)msl, new MTLCompileOptions(), ref compileError);
if (compileError.NativePtr != IntPtr.Zero)
    throw new InvalidOperationException("msl compile failed");
var vertexFunction = library.NewFunction((NSString)"vertexMain");
var fragmentFunction = library.NewFunction((NSString)"fragmentMain");

var pipelineDescriptorPtr = new ObjectiveCClass(ObjectiveC.objc_getClass("MTLRenderPipelineDescriptor")).AllocInit();
var pipelineDescriptor = new MTLRenderPipelineDescriptor(pipelineDescriptorPtr);
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

unsafe
{
    // NSWindow* from GLFW. Silk.NET.GLFW has no native-access binding for it, so take the export
    // from the same brew dylib the engine's runtime already loads (meitou-run.sh points DYLD at it).
    var glfw = NativeLibrary.Load("libglfw.3.dylib");
    var getCocoaWindow = (delegate* unmanaged<WindowHandle*, IntPtr>)NativeLibrary.GetExport(glfw, "glfwGetCocoaWindow");
    var nsWindow = getCocoaWindow((WindowHandle*)window.Native!.Glfw!.Value);
    Console.WriteLine($"nswindow  0x{nsWindow:x}");

    // Attach a CAMetalLayer to the window's content view.
    var contentView = ObjectiveC.IntPtr_objc_msgSend(nsWindow, "contentView");
    ObjectiveC.objc_msgSend(contentView, "setWantsLayer:", true);
    var layerPtr = new ObjectiveCClass(ObjectiveC.objc_getClass("CAMetalLayer")).AllocInit();
    ObjectiveC.objc_msgSend(contentView, "setLayer:", layerPtr);
    var layer = new CAMetalLayer(layerPtr);
    layer.Device = device;
    layer.PixelFormat = MTLPixelFormat.BGRA8Unorm;
    layer.FramebufferOnly = true;

    var clock = Stopwatch.StartNew();
    var frames = 0;

    window.Render += deltaTime =>
    {
        var drawable = layer.NextDrawable;
        if (drawable.DrawableID == 0) return;

        var cmd = queue.CommandBuffer();
        var rpdPtr = ObjectiveC.IntPtr_objc_msgSend(ObjectiveC.objc_getClass("MTLRenderPassDescriptor"), "renderPassDescriptor");
        var rpd = new MTLRenderPassDescriptor(rpdPtr);
        var attachment = rpd.ColorAttachments.Object(0);
        attachment.Texture = drawable.Texture;
        attachment.LoadAction = MTLLoadAction.Clear;
        attachment.StoreAction = MTLStoreAction.Store;
        attachment.ClearColor = new MTLClearColor
        {
            red = 0.08 + 0.04 * Math.Sin(clock.Elapsed.TotalSeconds),
            green = 0.08,
            blue = 0.12,
            alpha = 1.0,
        };

        var encoder = cmd.RenderCommandEncoder(rpd);
        encoder.SetRenderPipelineState(pipeline);
        encoder.DrawPrimitives(MTLPrimitiveType.Triangle, 0, 3);
        encoder.EndEncoding();
        cmd.PresentDrawable(drawable);
        cmd.Commit();

        if (++frames % 100 == 0) Console.WriteLine($"frame     {frames}");
        if (frames >= framesToRun) window.Close();
    };
}

window.Run();
Console.WriteLine("spike ok: 300 frames presented (clear + MSL triangle)");
