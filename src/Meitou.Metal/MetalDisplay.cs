using System.Runtime.InteropServices;
using Silk.NET.GLFW;
using Silk.NET.Windowing;
using SharpMetal.Foundation;
using SharpMetal.Metal;
using SharpMetal.ObjectiveCCore;
using SharpMetal.QuartzCore;

namespace Meitou.Metal;

/// <summary>
/// The window and the Metal swapchain: a GLFW window with a CAMetalLayer on its content view, presented by Metal. The
/// mirror of the Vulkan display (Meitou.Rendering.Display.VulkanDisplay) for the Metal path.
/// </summary>
public sealed unsafe class MetalDisplay : IDisposable
{
    /// <summary>The window the game draws into.</summary>
    public IWindow Window { get; }

    /// <summary>The Metal device.</summary>
    public MTLDevice Device { get; }

    /// <summary>The queue the frames are committed to.</summary>
    public MTLCommandQueue Queue { get; }

    /// <summary>The layer the drawables come from.</summary>
    public CAMetalLayer Layer { get; }

    public MetalDisplay(WindowOptions options)
    {
        ObjectiveC.LinkMetal();
        Window = Silk.NET.Windowing.Window.Create(options);
        Window.Initialize();
        Device = MTLDevice.CreateSystemDefaultDevice();
        Queue = Device.NewCommandQueue();

        // NSWindow* from GLFW: Silk.NET.GLFW has no binding for it, so the export is taken from the same brew dylib the
        // engine's runtime already loads (meitou-run.sh points DYLD at it).
        var glfw = NativeLibrary.Load("libglfw.3.dylib");
        var getCocoaWindow = (delegate* unmanaged<WindowHandle*, IntPtr>)NativeLibrary.GetExport(glfw, "glfwGetCocoaWindow");
        var nsWindow = getCocoaWindow((WindowHandle*)Window.Native!.Glfw!.Value);

        // The layer has to fill the view (NSViewWidthSizable | NSViewHeightSizable) or the drawable covers only part of
        // the window; its drawable size then follows the view's bounds times the scale factor, retina included.
        var contentView = ObjectiveC.IntPtr_objc_msgSend(nsWindow, "contentView");
        ObjectiveC.objc_msgSend(contentView, "setWantsLayer:", true);
        var layerPtr = new ObjectiveCClass(ObjectiveC.objc_getClass("CAMetalLayer")).AllocInit();
        ObjectiveC.objc_msgSend(layerPtr, "setAutoresizingMask:", (byte)18);
        ObjectiveC.objc_msgSend(contentView, "setLayer:", layerPtr);
        var layer = new CAMetalLayer(layerPtr);
        layer.Device = Device;
        layer.PixelFormat = MTLPixelFormat.BGRA8Unorm;
        layer.FramebufferOnly = true;
        Layer = layer;
    }

    /// <summary>Brings the app to the front, so a screenshot of the screen shows the window.</summary>
    public void Activate()
    {
        var app = ObjectiveC.IntPtr_objc_msgSend(ObjectiveC.objc_getClass("NSApplication"), "sharedApplication");
        ObjectiveC.objc_msgSend(app, "activateIgnoringOtherApps:", true);
    }

    /// <summary>Begins a frame: the drawable to draw into and the command buffer to record it in.</summary>
    public bool TryBeginFrame(out MTLCommandBuffer command, out CAMetalDrawable drawable)
    {
        command = default;
        drawable = default;
        var next = Layer.NextDrawable;
        if (next.DrawableID == 0) return false;
        drawable = next;
        command = Queue.CommandBuffer();
        return true;
    }

    /// <summary>Opens the render pass for a frame: the drawable cleared to <paramref name="clear"/>.</summary>
    public MTLRenderCommandEncoder BeginPass(MTLCommandBuffer command, CAMetalDrawable drawable, MTLClearColor clear)
    {
        var descriptor = new MTLRenderPassDescriptor(
            ObjectiveC.IntPtr_objc_msgSend(ObjectiveC.objc_getClass("MTLRenderPassDescriptor"), "renderPassDescriptor"));
        // The attachments come back as a struct copy, so a value-type property (the clear colour) has to be written back
        // into the array, or the pass clears to whatever the descriptor's memory held.
        var attachments = descriptor.ColorAttachments;
        var attachment = attachments.Object(0);
        attachment.Texture = drawable.Texture;
        attachment.LoadAction = MTLLoadAction.Clear;
        attachment.StoreAction = MTLStoreAction.Store;
        attachment.ClearColor = clear;
        attachments.SetObject(attachment, 0);
        return command.RenderCommandEncoder(descriptor);
    }

    /// <summary>Presents the frame and commits the command buffer.</summary>
    public void Present(MTLCommandBuffer command, CAMetalDrawable drawable)
    {
        command.PresentDrawable(drawable);
        command.Commit();
    }

    public void Dispose() => Window.Dispose();
}
