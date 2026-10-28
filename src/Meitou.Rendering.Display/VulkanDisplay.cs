using Meitou.Rendering.Gpu;
using Meitou.Rendering.Gpu.Core;
using Meitou.Rendering.Upscalers;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Windowing;

namespace Meitou.Rendering.Display;

/// <summary>
/// The window and the GPU behind the renderers: a Vulkan device, the native <see cref="GpuContext"/> they draw with and,
/// with a window, <see cref="VulkanPresenter"/> showing its backbuffer. Without a window (offscreen pictures, benchmarks) Vulkan runs
/// headless. <c>MEITOU_VK_VALIDATION=1</c> turns on the validation layer; its error count is reported on dispose.
/// </summary>
public sealed unsafe class VulkanDisplay : IDisposable
{
    /// <summary>The window; null when headless.</summary>
    public IWindow? Window { get; }
    public VulkanDevice Device { get; }
    /// <summary>The native GPU API the renderers draw with; its frames are what <see cref="BeginFrame"/> / <see cref="Present"/> begin and submit.</summary>
    public GpuContext Context { get; }
    readonly VulkanPresenter? presenter;

    /// <summary>With a window: what the frame is drawn into (the window's framebuffer size), valid after <see cref="BeginFrame"/>; null when headless.</summary>
    public GpuTexture? Backbuffer => presenter?.Backbuffer;
    /// <summary>Stopwatch ticks spent acquiring swapchain images and presenting (0 when headless).</summary>
    public long AcquireTicks => presenter?.AcquireTicks ?? 0;
    public long PresentTicks => presenter?.PresentTicks ?? 0;
    /// <summary>The window's vsync (<see cref="VulkanPresenter.VSync"/>; the swapchain is remade at the next frame). Always off without a window.</summary>
    public bool VSync { get => presenter?.VSync ?? false; set { if (presenter is not null) presenter.VSync = value; } }

    /// <summary>Streamline (DLSS), when <c>dlss</c> asked for it and it loaded; shut down before the device.</summary>
    public Streamline? Streamline { get; }
    /// <summary>NGX (DLSS on Linux), when <c>dlss</c> asked for it and its client library and the DLSS model loaded; shut down before the device.</summary>
    public Ngx? Ngx { get; }

    /// <summary>Set before creating the display: turns on the device features of the bench's triangle measurements (<see cref="VulkanDeviceOptions.TriangleMeasurements"/>; <c>--bench-tris</c>).</summary>
    public static bool TriangleMeasurements { get; set; }

    /// <summary>Set before creating the display: ray queries for the global illumination (<see cref="VulkanDeviceOptions.RayTracing"/>; <c>--gi-debug</c>).</summary>
    public static bool RayTracing { get; set; }

    /// <summary>A window created from <paramref name="options"/> (its Vulkan surface; shown), or headless when <paramref name="options"/> is null.</summary>
    public VulkanDisplay(WindowOptions? options, bool vsync, bool dlss = false)
    {
        var (validation, sync, gpu) = VulkanDeviceOptions.FromEnvironment();
        validationRequested = validation == true;
        var deviceOptions = new VulkanDeviceOptions
        {
            Validation = validation == true, SyncValidation = sync, GpuValidation = gpu,
            PipelineCachePath = PipelineCacheFile(),
            TriangleMeasurements = TriangleMeasurements,
            RayTracing = RayTracing,
        };
        if (dlss && OperatingSystem.IsWindows())
        {
            // Streamline goes in before the instance and adds what it needs to the device.
            Streamline = Streamline.TryInit(out var why);
            if (Streamline is null) Console.WriteLine($"upscaler  {why}");
            else Streamline.Apply(deviceOptions);
        }
        else if (dlss && OperatingSystem.IsLinux())
        {
            // Linux: NGX through NVIDIA's client library (libmeitou_ngx.so), likewise before the instance.
            Ngx = Ngx.TryInit(out var why);
            if (Ngx is null) Console.WriteLine($"upscaler  {why}");
            else Ngx.Apply(deviceOptions);
        }
        if (options is not null)
        {
            Window = Silk.NET.Windowing.Window.Create(options.Value with { API = GraphicsAPI.DefaultVulkan, VSync = false });
            Window.Initialize();
            SetWindowIcon(Window);
            var surface = Window.VkSurface ?? throw new InvalidOperationException("the window has no Vulkan surface support");
            var names = surface.GetRequiredExtensions(out uint count);
            var extensions = new string[count];
            for (int i = 0; i < count; i++) extensions[i] = SilkMarshal.PtrToString((nint)names[i])!;
            deviceOptions.InstanceExtensions = [.. extensions, .. deviceOptions.InstanceExtensions ?? []];
            deviceOptions.CreateSurface = instance => surface.Create<AllocationCallbacks>(instance.ToHandle(), null).ToSurface();
        }
        Device = VulkanDevice.Create(deviceOptions);
        Console.WriteLine($"vulkan    {Device.DeviceName}");
        Context = new GpuContext(Device);
        if (options is not null) presenter = new VulkanPresenter(Context, vsync) { PresentFunction = Streamline is { DlssSupported: true } s ? s.PresentProxy : null };
    }

    /// <summary>The two moons as the window's icon (the embedded 256 x 256 RGBA); a window system that refuses it keeps the default.</summary>
    static void SetWindowIcon(IWindow window)
    {
        try
        {
            using var stream = typeof(VulkanDisplay).Assembly.GetManifestResourceStream("meitou-256.rgba");
            if (stream is null) return;
            var pixels = new byte[256 * 256 * 4];
            stream.ReadExactly(pixels);
            var icon = new Silk.NET.Core.RawImage(256, 256, pixels);
            window.SetWindowIcon(ref icon);
        }
        catch (Exception e) { Console.WriteLine($"window icon  {e.Message}"); }
    }

    /// <summary>Starts a frame of the given size; false when nothing can be shown (a minimised window): skip drawing.</summary>
    public bool BeginFrame(int width, int height) => presenter?.BeginFrame(width, height) ?? width > 0 && height > 0;

    /// <summary>Shows the frame (headless: just submits it). The backbuffer stays intact until the next frame, so a screenshot of it is read after this.
    /// With <paramref name="topDown"/> the window shows that finished picture (row 0 the top) instead of the flipped backbuffer.</summary>
    public void Present(Meitou.Rendering.Gpu.GpuTexture? topDown = null)
    {
        if (presenter is not null) presenter.Present(topDown);
        else Context.EndFrame();
    }

    /// <summary>Headless: ends the frame (submits it).</summary>
    public void EndFrame() => Context.EndFrame();

    public void Dispose()
    {
        presenter?.Dispose();
        Context.Dispose();
        Device.Frames.WaitAll();   // what was released after the frames in flight
        Streamline?.Dispose();
        Ngx?.Dispose();
        if (Device.ValidationErrors > 0) Console.WriteLine($"vulkan validation: {Device.ValidationErrors} errors\n{string.Join("\n", Device.ValidationLog.Take(20))}");
        else if (validationRequested) Console.WriteLine("vulkan validation: 0 errors");
        Device.Dispose();
        Window?.Dispose();
    }

    readonly bool validationRequested;

    /// <summary>
    /// <c>%LOCALAPPDATA%\Meitou\pipeline-cache.bin</c> (the driver checks the header and ignores a cache from another device or driver);
    /// <c>--no-load-cache</c> turns it off. Null when there is no place for it.
    /// </summary>
    static string? PipelineCacheFile()
    {
        if (Meitou.Data.World.LoadCaches.Disabled) return null;
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(root)) return null;
        try
        {
            var dir = Path.Combine(root, "Meitou");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "pipeline-cache.bin");
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
