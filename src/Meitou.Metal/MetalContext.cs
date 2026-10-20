using System.Runtime.InteropServices;
using Meitou.Rendering.Gpu;
using Meitou.Rendering.Gpu.Shaders;
using SharpMetal.Foundation;
using SharpMetal.Metal;
using SharpMetal.ObjectiveCCore;
using SharpMetal.QuartzCore;

namespace Meitou.Metal;

/// <summary>
/// The Metal device as the renderers see it (docs/resource-layer.md): the second implementation of
/// <see cref="IGpuContext"/>, sized to the sky pass — every operation a later pass needs first throws. The
/// frame loop is the context's own (<see cref="BeginFrame"/>, <see cref="EndFrame"/>), as the Vulkan
/// context's is; the host's pass model (<see cref="BeginHostPass"/>) mirrors <c>GpuContext</c>'s. The
/// renderers draw into an offscreen <see cref="Backbuffer"/> (the engine's Rgba8, as the Vulkan path's
/// <c>VulkanPresenter</c> has); the present is a flipped copy into the drawable — Metal's blit can do
/// neither the flip nor the format, so a five-line MSL pass does. Render thread only.
/// </summary>
public sealed unsafe class MetalContext : IGpuContext, IDisposable
{
    /// <summary>The frames in flight: the drawable ring's depth (three, as the layer's own).</summary>
    public const int FrameRingDepth = 3;

    const string PresentMsl = """
        #include <metal_stdlib>
        using namespace metal;

        vertex float4 present_vertex(uint id [[vertex_id]])
        {
            float2 xy = float2((id << 1) & 2, id & 2);
            return float4(xy * 2.0 - 1.0, 0.0, 1.0);
        }

        fragment float4 present_fragment(float4 position [[position]], constant float2& targetSize [[buffer(1)]],
            texture2d<float> source, sampler source_sampler)
        {
            // No flip: the engine's GL-style shaders land right side up in the backbuffer on Metal (the NDC points up as GL's
            // does), so row for row is the picture (the Vulkan path's blit flips instead - its backbuffer is upside down).
            // Divide by the drawable, not the source: on a scaled display the drawable is larger than the framebuffer the
            // scene and HUD are drawn into, and dividing by the source there showed only its top-left part.
            float2 uv = position.xy / targetSize;
            return source.sample(source_sampler, uv);
        }
        """;

    /// <summary>A scaled texture copy (the impostor bake's halving): a fullscreen sampling pass, as Metal's blit cannot scale.</summary>
    const string BlitMsl = """
        #include <metal_stdlib>
        using namespace metal;

        struct blit_out
        {
            float4 position [[position]];
        };

        vertex blit_out blit_vertex(uint id [[vertex_id]])
        {
            float2 xy = float2((id << 1) & 2, id & 2);
            blit_out o;
            o.position = float4(xy * 2.0 - 1.0, 0.0, 1.0);
            return o;
        }

        fragment float4 blit_fragment(blit_out in [[stage_in]], constant float2& targetSize [[buffer(1)]],
            texture2d<float> source [[texture(0)]], sampler sourceSampler [[sampler(0)]])
        {
            return source.sample(sourceSampler, in.position.xy / targetSize);
        }
        """;

    /// <summary>The attachments' partial clear (Vulkan's <c>vkCmdClearAttachments</c>): a fullscreen triangle under the clear rect's scissor.</summary>
    const string ClearMsl = """
        #include <metal_stdlib>
        using namespace metal;

        struct clear_out { float4 position [[position]]; };
        struct clear_depth_out { float depth [[depth(any)]]; };
        struct clear_both_out { float4 colour [[color(0)]]; float depth [[depth(any)]]; };

        vertex clear_out clear_vertex(uint id [[vertex_id]])
        {
            float2 xy = float2((id << 1) & 2, id & 2);
            clear_out o;
            o.position = float4(xy * 2.0 - 1.0, 0.0, 1.0);
            return o;
        }

        fragment float4 clear_colour_fragment(clear_out in [[stage_in]], constant float4& value [[buffer(0)]])
        {
            return value;
        }

        fragment clear_depth_out clear_depth_fragment(clear_out in [[stage_in]], constant float& value [[buffer(1)]])
        {
            clear_depth_out o;
            o.depth = value;
            return o;
        }

        fragment clear_both_out clear_both_fragment(clear_out in [[stage_in]], constant float4& colour [[buffer(0)]],
            constant float& value [[buffer(1)]])
        {
            clear_both_out o;
            o.colour = colour;
            o.depth = value;
            return o;
        }
        """;

    readonly MetalDisplay display;
    readonly MetalFrame[] frames;
    readonly MetalTimestamps timestamps = new();
    readonly MetalBindlessTable bindless;
    readonly List<MetalTexture?> textures = [null];
    readonly List<MetalTextureView?> views = [null];
    readonly List<MetalBuffer?> buffers = [null];
    readonly List<MetalSet?> sets = [null];
    readonly Dictionary<(int Slot, ScalarKind Kind, bool Depth), SampledTexture> dummies = [];
    readonly List<GpuTexture> dummyTextures = [];
    readonly Dictionary<(bool Test, bool Write, DepthFunction Op), MTLDepthStencilState> depthStates = [];
    GpuBuffer dummyVertex;
    GpuTexture? backbuffer, depth, albedo;
    MTLRenderPipelineState presentPipeline;
    long frameNumber;
    int slot = -1;
    // The host pass open now (the guests' targets and state), and the segments of the recording model.
    PassTargets? passTargets;
    DrawState? passState;
    MetalCommandList? passList, segment, guest;

    public MetalContext(MetalDisplay display)
    {
        this.display = display;
        frames = new MetalFrame[FrameRingDepth];
        for (int i = 0; i < FrameRingDepth; i++) frames[i] = new MetalFrame(this, i);
        Shaders = new MetalShaders(Device);
        Pipelines = new MetalPipelines(Device, Shaders);
        Samplers = new MetalSamplers(Device);
        Uploads = new MetalUploads(this);
        bindless = new MetalBindlessTable(this);
        var shared = NewSharedBuffer(16, "dummy vertex");
        new Span<float>(shared.Pointer, 4)[3] = 1;   // GL's disabled-attribute constant (0, 0, 0, 1)
        dummyVertex = RegisterBuffer(shared.Buffer, "dummy vertex");
        presentPipeline = MakePresentPipeline();
    }

    public MTLDevice Device => display.Device;

    /// <summary>The queue the frames are committed to (an upload batch's command buffer comes from it too: the queue executes its command buffers in commit order).</summary>
    public MTLCommandQueue Queue => display.Queue;

    // ---- the seam's services ----

    public MetalShaders Shaders { get; }
    public MetalPipelines Pipelines { get; }
    public MetalSamplers Samplers { get; }
    public MetalUploads Uploads { get; }
    public MetalFrame Frame => frames[slot];
    public FrameGlobals Globals { get; } = new();
    /// <summary>The frame as the seam's interface (<see cref="IGpuContext.Frame"/>).</summary>
    IGpuFrame IGpuContext.Frame => Frame;

    IGpuShaders IGpuContext.Shaders => Shaders;
    IGpuPipelines IGpuContext.Pipelines => Pipelines;
    IGpuSamplers IGpuContext.Samplers => Samplers;
    IGpuUploads IGpuContext.Uploads => Uploads;

    /// <summary>The bindless table (docs/metal-backend.md, "The bindless table"): the bindless set as a Metal 3 tier-2
    /// argument buffer, one per frame slot.</summary>
    public IGpuBindlessTable Bindless => bindless;

    /// <summary>The frame's number (the bindless table releases freed entries once the frames that could read them are done).</summary>
    internal long FrameNumber => frameNumber;

    /// <summary>The device limit a uniform buffer slice's offset must align to (Metal's constant buffer alignment).</summary>
    public ulong UniformBufferAlign => 256;

    /// <summary>The device is an integrated GPU: Apple silicon's memory is unified (its budgets and star layers differ).</summary>
    public bool IsIntegrated => true;

    /// <summary>The frame's timestamps (<see cref="MetalTimestamps"/>; nothing is read yet).</summary>
    internal MetalTimestamps Timestamps => timestamps;

    /// <summary>The upscaler's texture LOD bias: added to the mip level of every mipmapped fetch (set by the post-processing chain).</summary>
    public float LodBias { get; set; }

    /// <summary>Upload bytes into the frame's statistics; a batch before the first frame (the sky's textures at boot) counts into the next one.</summary>
    internal void CountUpload(int bytes)
    {
        if (slot >= 0) Frame.Stats.UploadBytes += bytes;
        else pendingUploadBytes += bytes;
    }
    long pendingUploadBytes;

    /// <summary>Whether the open rendering has a depth attachment (<see cref="MetalCommandList.SetDepth"/>; Metal's validation objects to setting the state without one).</summary>
    internal bool HasDepthAttachment { get; private set; }

    /// <summary>The backend's depth state for a test/write/compare (made once, cached by value).</summary>
    internal MTLDepthStencilState DepthState(bool test, bool write, DepthFunction op)
    {
        var key = (test, write, op);
        if (depthStates.TryGetValue(key, out var made)) return made;
        var d = new MTLDepthStencilDescriptor();
        d.DepthCompareFunction = test ? MetalConventions.Compare(op) : MTLCompareFunction.Always;
        d.IsDepthWriteEnabled = test && write;
        return depthStates[key] = Device.NewDepthStencilState(d);
    }

    // ---- the resources ----

    public GpuTexture CreateTexture(TextureDesc desc)
    {
        var d = new MTLTextureDescriptor();
        int samples = Math.Max(1, desc.Samples);
        d.TextureType = desc.Kind switch
        {
            TextureKind.Texture2D => samples > 1 ? MTLTextureType.Type2DMultisample : MTLTextureType.Type2D,
            TextureKind.Texture2DArray => samples > 1 ? MTLTextureType.Type2DMultisampleArray : MTLTextureType.Type2DArray,
            TextureKind.Cube => MTLTextureType.Cube,
            _ => throw new NotSupportedException($"no Metal texture type for {desc.Kind}"),
        };
        if (samples > 1 && desc.Kind == TextureKind.Cube) throw new NotSupportedException("Metal has no multisampled cube textures");
        d.PixelFormat = MetalConventions.Pixel(desc.Format);
        d.Width = (ulong)desc.Width;
        d.Height = (ulong)desc.Height;
        d.MipmapLevelCount = (ulong)(samples > 1 ? 1 : desc.Levels);
        // A cube's six faces are the type's own (arrayLength 1); only a cube *array* counts layers.
        d.ArrayLength = (ulong)(desc.Kind == TextureKind.Cube ? 1 : desc.Layers);
        d.SampleCount = (ulong)samples;
        d.StorageMode = MTLStorageMode.Shared;   // unified memory: the device's own, and CPU-writable (docs/metal-backend.md)
        MTLTextureUsage usage = 0;
        if (desc.Use.HasFlag(TextureUse.Sampled)) usage |= MTLTextureUsage.ShaderRead;
        if (desc.Use.HasFlag(TextureUse.Storage)) usage |= MTLTextureUsage.ShaderWrite;
        if (desc.Use.HasFlag(TextureUse.ColourTarget) || desc.Use.HasFlag(TextureUse.DepthTarget)) usage |= MTLTextureUsage.RenderTarget;
        if (desc.Use.HasFlag(TextureUse.TransferDst)) usage |= MTLTextureUsage.ShaderWrite;
        usage |= MTLTextureUsage.PixelFormatView;   // the engine's views (levels, layers, swizzles) are Metal texture views
        d.Usage = usage;
        d.ResourceOptions = MTLResourceOptions.ResourceStorageModeShared;
        var texture = new MetalTexture { Image = Device.NewTexture(d), Desc = desc };
        textures.Add(texture);
        return new GpuTexture(this, desc, textures.Count - 1);
    }
    public GpuView View(GpuTexture texture, int baseLevel, int levels, int baseLayer, int layers, GpuViewType type, GpuSwizzle swizzle)
    {
        var of = textures[texture.Index]!;
        var format = MetalConventions.Pixel(of.Desc.Format);
        var viewType = type switch
        {
            GpuViewType.Texture2D => MTLTextureType.Type2D,
            GpuViewType.Texture2DArray => MTLTextureType.Type2DArray,
            GpuViewType.Cube => MTLTextureType.Cube,
            _ => throw new NotSupportedException($"no Metal view type for {type}"),
        };
        var levelRange = new NSRange { location = (ulong)baseLevel, length = (ulong)levels };
        var layerRange = new NSRange { location = (ulong)baseLayer, length = (ulong)layers };
        var view = new MetalTextureView
        {
            // A swizzled view (the BC4 masks' red in all three, alpha one) is Metal's own texture swizzle: the same format, the
            // sample reads the channels the engine's GpuSwizzle names.
            Image = swizzle.Equals(GpuSwizzle.Identity)
                ? of.Image.NewTextureView(format, viewType, levelRange, layerRange)
                : of.Image.NewTextureView(format, viewType, levelRange, layerRange, Swizzle(swizzle)),
            Of = of,
        };
        of.Views.Add(view);
        views.Add(view);
        return new GpuView(views.Count - 1);
    }

    static MTLTextureSwizzleChannels Swizzle(GpuSwizzle swizzle) => new()
    {
        red = Swizzle(swizzle.R, MTLTextureSwizzle.Red),
        green = Swizzle(swizzle.G, MTLTextureSwizzle.Green),
        blue = Swizzle(swizzle.B, MTLTextureSwizzle.Blue),
        alpha = Swizzle(swizzle.A, MTLTextureSwizzle.Alpha),
    };

    static MTLTextureSwizzle Swizzle(GpuSwizzleComponent component, MTLTextureSwizzle identity) => component switch
    {
        GpuSwizzleComponent.Identity => identity,
        GpuSwizzleComponent.R => MTLTextureSwizzle.Red,
        GpuSwizzleComponent.G => MTLTextureSwizzle.Green,
        GpuSwizzleComponent.B => MTLTextureSwizzle.Blue,
        GpuSwizzleComponent.A => MTLTextureSwizzle.Alpha,
        GpuSwizzleComponent.One => MTLTextureSwizzle.One,
        GpuSwizzleComponent.Zero => MTLTextureSwizzle.Zero,
        _ => throw new NotSupportedException($"no Metal swizzle for {component}"),
    };

    public void ReleaseTexture(GpuTexture texture)
    {
        var of = textures[texture.Index]!;
        foreach (var view in of.Views) views[views.IndexOf(view)] = null;
        of.Views.Clear();
        of.Release();
        textures[texture.Index] = null;
    }

    public GpuBuffer CreateBuffer(ulong size, BufferUse use, string name)
    {
        // Device-local on Apple silicon is the unified memory itself; shared mode makes the CPU's writes (the dummy vertex,
        // the frame's constants ring) the same object the GPU reads.
        var shared = NewSharedBuffer(size, name);
        return RegisterBuffer(shared.Buffer, name);
    }

    public void ReleaseBuffer(GpuBuffer buffer)
    {
        buffers[buffer.Index]?.Release();
        buffers[buffer.Index] = null;
    }

    public GpuSet CreateSet(GpuSetLayout layout, ReadOnlySpan<GpuBinding> bindings)
    {
        sets.Add(new MetalSet { Layout = layout, Bindings = [.. bindings] });
        return new GpuSet(sets.Count - 1);
    }

    /// <summary>The constants block (the seam's <see cref="IGpuContext.AllocateConstantSet"/>): on Metal the set binds straight onto the
    /// encoder and reads the offset at the bind, so it is an ordinary set holding the buffer.</summary>
    public GpuSet AllocateConstantSet(GpuSetLayout layout, GpuBuffer buffer, ulong size) =>
        CreateSet(layout, [new GpuBinding(0, GpuBindingKind.UniformBuffer, Buffer: new GpuBufferBinding(buffer, 0, size))]);

    /// <summary>Records a prepared guest segment at once, as a guest of the host's open pass (Metal records in order on the calling thread).</summary>
    public void Record(string label, RecordJob job)
    {
        var cmd = BeginGuest(label);
        try { job.Record(cmd); }
        finally { EndGuest(cmd); job.Release(); }
    }

    // ---- the host's recording model ----

    public IGpuCommandList BeginGuest(string label)
    {
        if (guest is not null) throw new InvalidOperationException("BeginGuest inside a guest segment");
        if (passList is null) throw new InvalidOperationException("BeginGuest without a host pass");
        passList.Invalidate();
        passList.BeginLabel(label);
        Frame.Stats.NativeSegments++;
        return guest = passList;
    }

    public void EndGuest(IGpuCommandList cmd)
    {
        if (guest is null || !ReferenceEquals(cmd, guest)) throw new InvalidOperationException("EndGuest without a matching BeginGuest");
        guest = null;
        cmd.EndLabel();
    }

    public IGpuCommandList BeginNative(string label)
    {
        if (segment is not null) throw new InvalidOperationException("BeginNative inside a native segment");
        EnsureFrame();
        var list = Frame.Commands;
        list.Invalidate();
        Frame.States.AssumeFullBarrier();
        Frame.Stats.NativeSegments++;
        list.BeginLabel(label);
        return segment = list;
    }

    public void EndNative(IGpuCommandList cmd)
    {
        if (segment is null || !ReferenceEquals(cmd, segment)) throw new InvalidOperationException("EndNative without a matching BeginNative");
        if (PassOpen) throw new InvalidOperationException("EndNative with a host pass open");
        cmd.EndLabel();
        segment = null;
    }

    public bool PassOpen => passList is not null;

    public PassTargets CurrentTargets() => passTargets ?? throw new InvalidOperationException("no pass is open");
    public DrawState CurrentState() => passState ?? throw new InvalidOperationException("no pass is open");

    /// <summary>Records something that does not disturb a pass (a timestamp, a label) where the frame's commands go now.</summary>
    public void Interleave(Action<IGpuCommandList> record)
    {
        if (passList is { } host && guest is null) { record(host); return; }
        EnsureFrame();
        record(Frame.Commands);
    }

    // ---- the host's pass (the twin of GpuContext's BeginHostPass; the Metal host drives it directly) ----

    /// <summary>
    /// The caller has begun a rendering instance on <paramref name="cmd"/> (inside its own native segment) into
    /// <paramref name="targets"/>: until <see cref="EndHostPass"/> guests draw into it with <paramref name="state"/>.
    /// </summary>
    public void BeginHostPass(MetalCommandList cmd, PassTargets targets, DrawState state)
    {
        if (passList is not null) throw new InvalidOperationException("a host pass is already open");
        (passList, passTargets, passState) = (cmd, targets, state);
        HasDepthAttachment = !targets.Depth.View.IsNull;
    }

    void IGpuContext.BeginHostPass(IGpuCommandList cmd, PassTargets targets, DrawState state) => BeginHostPass((MetalCommandList)cmd, targets, state);

    /// <summary>The viewport and scissor (null: the whole viewport) the guests draw with from now on (a cascade's tile).</summary>
    public void SetPassViewport(PixelViewport viewport, PixelRect? scissor = null)
    {
        if (passTargets is null) throw new InvalidOperationException("no host pass is open");
        passTargets = passTargets.At(viewport, scissor);
    }

    /// <inheritdoc/>
    public bool DepthClamp => true;

    /// <inheritdoc/>
    public bool RecordsSecondaries => false;

    public void EndHostPass(IGpuCommandList cmd)
    {
        if (passList is null || !ReferenceEquals(cmd, passList)) throw new InvalidOperationException("EndHostPass without a matching BeginHostPass");
        (passList, passTargets, passState) = (null, null, null);
        HasDepthAttachment = false;
    }

    // ---- the frame loop ----

    /// <summary>
    /// Begins a frame: the layer's next drawable and its command buffer, and the frame's backbuffer (the
    /// offscreen target the renderers draw into, re-made when the window's size changed). False when no
    /// drawable is ready: the frame is skipped, as the display's own loop does.
    /// </summary>
    public bool BeginFrame()
    {
        if (!display.TryBeginFrame(out var command, out var drawable)) return false;
        var size = display.Window.FramebufferSize;
        int width = size.X, height = size.Y;
        if (backbuffer is not { } target || target.Desc.Width != width || target.Desc.Height != height)
        {
            backbuffer?.Dispose();
            backbuffer = CreateTexture(new TextureDesc(InternalFormat.Rgba8, width, height,
                Use: TextureUse.ColourTarget | TextureUse.Sampled, Name: "backbuffer"));
            depth?.Dispose();
            depth = CreateTexture(new TextureDesc(InternalFormat.DepthComponent24, width, height,
                Use: TextureUse.DepthTarget | TextureUse.Sampled, Name: "depth"));
            albedo?.Dispose();
            albedo = CreateTexture(new TextureDesc(InternalFormat.Rgba8ui, width, height, Use: TextureUse.ColourTarget, Name: "gi albedo"));
        }
        frameNumber++;
        slot = (slot + 1) % FrameRingDepth;
        frames[slot].Begin(frameNumber, command, drawable);
        bindless.BeginFrame(slot);
        if (pendingUploadBytes > 0) { frames[slot].Stats.UploadBytes = pendingUploadBytes; pendingUploadBytes = 0; }
        timestamps.Begin(slot, frameNumber);
        return true;
    }

    /// <summary>The offscreen target the renderers draw into (the engine's Rgba8, as the Vulkan path's backbuffer is).</summary>
    public GpuTexture Backbuffer => backbuffer ?? throw new InvalidOperationException("no frame is open");

    /// <summary>The scene's depth attachment (the backbuffer's size, re-made with it): the host pass tests and writes it.</summary>
    public GpuTexture Depth => depth ?? throw new InvalidOperationException("no frame is open");

    /// <summary>The scene's second colour target (the lit fragments' GI albedo; the GI resolve is not on Metal yet, but the lit
    /// programs write it, so the pass has the attachment they expect).</summary>
    public GpuTexture Albedo => albedo ?? throw new InvalidOperationException("no frame is open");

    /// <summary>Ends the frame: the backbuffer is copied into the drawable (flipped) and the frame's commands are committed.</summary>
    public void EndFrame()
    {
        var frame = frames[slot];
        bindless.EndFrame();
        frame.End();
        timestamps.End();
        Present(frame);
    }

    /// <summary>The present: the backbuffer's picture into the drawable, y flipped (a five-line MSL pass; Metal's blit can do neither the flip nor the format).</summary>
    void Present(MetalFrame frame)
    {
        var command = frame.Command;
        var drawable = frame.Drawable;
        var descriptor = new MTLRenderPassDescriptor(
            ObjectiveC.IntPtr_objc_msgSend(ObjectiveC.objc_getClass("MTLRenderPassDescriptor"), "renderPassDescriptor"));
        var attachments = descriptor.ColorAttachments;
        var attachment = attachments.Object(0);
        attachment.Texture = drawable.Texture;
        attachment.LoadAction = MTLLoadAction.DontCare;
        attachment.StoreAction = MTLStoreAction.Store;
        attachments.SetObject(attachment, 0);
        var encoder = command.RenderCommandEncoder(descriptor);
        encoder.SetRenderPipelineState(presentPipeline);
        encoder.SetFragmentTexture(TextureAt(backbuffer!), 0);
        encoder.SetFragmentSamplerState(Samplers.Backend(Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.Nearest, TextureMagFilter.Nearest,
            TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge, false, DepthFunction.Lequal, false, 1, false, 0))), 0);
        var targetSize = stackalloc float[2] { (float)drawable.Texture.Width, (float)drawable.Texture.Height };
        encoder.SetFragmentBytes((nint)targetSize, 8, 1);
        encoder.DrawPrimitives(MTLPrimitiveType.Triangle, 0, 3);
        encoder.EndEncoding();
        command.PresentDrawable(drawable);
        command.Commit();
        foreach (var buffer in setBuffers) buffer.Dispose();
        setBuffers.Clear();
    }

    public void EnsureFrame()
    {
        if (slot < 0 || !Frame.Open) throw new InvalidOperationException("no frame is open (BeginFrame first)");
    }

    // ---- the backend's own lookups (the command list resolves the handles through these) ----

    internal MTLTexture TextureAt(GpuTexture texture) => textures[texture.Index]!.Image;

    internal MTLTexture ViewTexture(GpuView view) => views[view.Index]!.Image;

    internal MetalSet SetAt(GpuSet set) => sets[set.Index]!;

    internal (MTLBuffer Buffer, ulong Offset) Buffer(GpuBufferBinding binding) => (buffers[binding.Buffer.Index]!.Buffer, binding.Offset);

    /// <summary>The bindless table's own object (its resources are told to the encoder at the bind).</summary>
    internal MetalBindlessTable BindlessTable => bindless;

    internal MTLBuffer BufferAt(GpuBuffer buffer) => buffers[buffer.Index]!.Buffer;

    /// <summary>A small shared buffer for a descriptor set's argument buffer; released once this frame's command buffer has
    /// been committed (the command buffer keeps the resources it encoded).</summary>
    internal MTLBuffer NewSetBuffer(ulong size)
    {
        var buffer = Device.NewBuffer(Math.Max(size, 8ul), MTLResourceOptions.ResourceStorageModeShared);
        setBuffers.Add(buffer);
        return buffer;
    }
    readonly List<MTLBuffer> setBuffers = [];

    internal SharedBuffer NewSharedBuffer(ulong size, string name)
    {
        var buffer = Device.NewBuffer(Math.Max(size, 16ul), MTLResourceOptions.ResourceStorageModeShared);
        return new SharedBuffer(buffer, (byte*)buffer.Contents, buffer.Length);
    }

    /// <summary>A shared-mode buffer, its CPU address and its size (a record would carry the pointer as a generated field, which C# refuses).</summary>
    internal readonly struct SharedBuffer
    {
        public readonly MTLBuffer Buffer;
        public readonly byte* Pointer;
        public readonly ulong Size;

        public SharedBuffer(MTLBuffer buffer, byte* pointer, ulong size)
        {
            Buffer = buffer;
            Pointer = pointer;
            Size = size;
        }
    }

    internal GpuBuffer RegisterBuffer(MTLBuffer buffer, string name)
    {
        buffers.Add(new MetalBuffer { Buffer = buffer, Name = name });
        return new GpuBuffer(buffers.Count - 1, buffer.Length);
    }

    internal void RebindBuffer(GpuBuffer handle, MTLBuffer buffer) => buffers[handle.Index] = new MetalBuffer { Buffer = buffer, Name = "frame constants" };

    /// <summary>The texture and sampler a combined binding reads.</summary>
    internal (MTLTexture Texture, MTLSamplerState Sampler) Sampled(SampledTexture sampled) =>
        (ViewTexture(sampled.View), Samplers.Backend(sampled.Sampler));

    /// <summary>The present pass's pipeline: the drawable's pixel format is the layer's (the only place the surface's format is named).</summary>
    MTLRenderPipelineState MakePresentPipeline()
    {
        NSError error = default;
        var library = Device.NewLibrary((NSString)PresentMsl, new MTLCompileOptions(), ref error);
        if (error.NativePtr != IntPtr.Zero) throw new InvalidOperationException($"the present pass's MSL did not compile: {error.LocalizedDescription}");
        var d = new MTLRenderPipelineDescriptor(new ObjectiveCClass(ObjectiveC.objc_getClass("MTLRenderPipelineDescriptor")).AllocInit());
        d.VertexFunction = library.NewFunction((NSString)"present_vertex");
        d.FragmentFunction = library.NewFunction((NSString)"present_fragment");
        var attachment = d.ColorAttachments.Object(0);
        attachment.PixelFormat = display.Layer.PixelFormat;
        d.ColorAttachments.SetObject(attachment, 0);
        var pipeline = Device.NewRenderPipelineState(d, ref error);
        if (error.NativePtr != IntPtr.Zero) throw new InvalidOperationException($"the present pass's pipeline failed: {error.LocalizedDescription}");
        return pipeline;
    }

    // ---- the stand-ins ----

    uint? standIn2D;

    /// <inheritdoc/>
    public uint StandIn2D => standIn2D ??= Bindless.Register(BindlessKind.Texture2D,
        Dummy(new SamplerInfo("", 0, 0, SamplerDimension.Dim2D, false, false, false, ScalarKind.Float, 0)));

    /// <inheritdoc/>
    public bool HasRayQuery => false;

    /// <inheritdoc/>
    public bool ConstantVertexAttributes => false;
    public bool RenderTargetsBottomUp => false;

    /// <summary>The vertices a disabled attribute may index: the generic value repeated at every 16-byte stride (4 MB an array).</summary>
    const int DummyVertexCapacity = 1 << 18;

    GpuBuffer? dummyFloatVertex, dummyIntVertex;

    /// <inheritdoc/>
    public GpuBuffer DummyVertexArray(ScalarKind kind)
    {
        if (kind == ScalarKind.Float) return dummyFloatVertex ??= MakeDummyVertexArray(0, 0, 0, 1, "dummy float vertices");
        return dummyIntVertex ??= MakeDummyVertexArray(0, 0, 0, 1, "dummy int vertices");
    }

    GpuBuffer MakeDummyVertexArray(int x, int y, int z, int w, string name)
    {
        var shared = NewSharedBuffer((ulong)DummyVertexCapacity * 16, name);
        var values = (int*)shared.Pointer;
        for (int i = 0; i < DummyVertexCapacity; i++)
            (values[i * 4], values[i * 4 + 1], values[i * 4 + 2], values[i * 4 + 3]) = (x, y, z, w);
        return RegisterBuffer(shared.Buffer, name);
    }

    /// <inheritdoc/>
    public int FramesInFlight => FrameRingDepth;

    /// <inheritdoc/>
    public ulong VideoMemoryBudget => Device.RecommendedMaxWorkingSetSize;

    /// <inheritdoc/>
    public void DeferDelete(Action release) => release();

    /// <inheritdoc/>
    public void Finish()
    {
        if (Frame.Open) EndFrame();
        var marker = Queue.CommandBuffer();
        marker.Commit();
        marker.WaitUntilCompleted();
    }

    /// <inheritdoc/>
    public IGpuReadback CreateReadback(ulong size, string name) => new MetalReadback(this, size, name);

    /// <inheritdoc/>
    public bool FrameCompleted(long frame) => frameNumber - frame >= FrameRingDepth;

    /// <summary>A texture copied into another, scaled (the impostor bake's supersampled row halved): a sampling pass into the destination.</summary>
    internal void BlitScaled(MTLCommandBuffer command, GpuTexture src, GpuTexture dst, TextureMinFilter filter) =>
        BlitScaled(command, TextureAt(src), TextureAt(dst), MetalConventions.Pixel(dst.Desc.Format), dst.Desc.Width, dst.Desc.Height, filter);

    /// <summary>
    /// A texture's level over another level of the same texture (the mip chain's step): a blit when the two levels are the same
    /// size, a sampling pass over level views when they are not (Metal's blit cannot scale). All slices: a cube's six faces,
    /// an array's layers, per-slice 2D views for the scaled case.
    /// </summary>
    internal void BlitLevel(MTLCommandBuffer command, GpuTexture texture, int srcLevel, int dstLevel, TextureMinFilter filter)
    {
        var desc = texture.Desc;
        var image = TextureAt(texture);
        int srcWidth = Math.Max(desc.Width >> srcLevel, 1), srcHeight = Math.Max(desc.Height >> srcLevel, 1);
        int dstWidth = Math.Max(desc.Width >> dstLevel, 1), dstHeight = Math.Max(desc.Height >> dstLevel, 1);
        int layers = desc.Kind == TextureKind.Cube ? 6 * Math.Max(desc.Layers, 1) : desc.Layers;
        var format = MetalConventions.Pixel(desc.Format);
        if (srcWidth == dstWidth && srcHeight == dstHeight)
        {
            var blit = command.BlitCommandEncoder();
            for (uint layer = 0; layer < (uint)layers; layer++)
                blit.CopyFromTexture(image, layer, (ulong)srcLevel, new MTLOrigin(),
                    new MTLSize { width = (ulong)srcWidth, height = (ulong)srcHeight, depth = 1 },
                    image, layer, (ulong)dstLevel, new MTLOrigin());
            blit.EndEncoding();
            return;
        }
        for (uint layer = 0; layer < (uint)layers; layer++)
        {
            var source = image.NewTextureView(format, MTLTextureType.Type2D,
                new NSRange { location = (ulong)srcLevel, length = 1 }, new NSRange { location = layer, length = 1 });
            var target = image.NewTextureView(format, MTLTextureType.Type2D,
                new NSRange { location = (ulong)dstLevel, length = 1 }, new NSRange { location = layer, length = 1 });
            BlitScaled(command, source, target, format, dstWidth, dstHeight, filter);
            levelViews.Add(source);
            levelViews.Add(target);
        }
    }

    readonly List<MTLTexture> levelViews = [];

    unsafe void BlitScaled(MTLCommandBuffer command, MTLTexture source, MTLTexture target, MTLPixelFormat format, int width, int height, TextureMinFilter filter)
    {
        if (!blitPipelines.TryGetValue(format, out var pipeline)) blitPipelines[format] = pipeline = MakeBlitPipeline(format);
        var descriptor = new MTLRenderPassDescriptor(
            ObjectiveC.IntPtr_objc_msgSend(ObjectiveC.objc_getClass("MTLRenderPassDescriptor"), "renderPassDescriptor"));
        var attachments = descriptor.ColorAttachments;
        var attachment = attachments.Object(0);
        attachment.Texture = target;
        attachment.LoadAction = MTLLoadAction.DontCare;
        attachment.StoreAction = MTLStoreAction.Store;
        attachments.SetObject(attachment, 0);
        var encoder = command.RenderCommandEncoder(descriptor);
        encoder.SetRenderPipelineState(pipeline);
        encoder.SetFragmentTexture(source, 0);
        var mag = filter == TextureMinFilter.Linear ? TextureMagFilter.Linear : TextureMagFilter.Nearest;
        var sampler = Samplers.Backend(Samplers.Get(SamplerDesc.FromGl(filter, mag, TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge,
            TextureWrapMode.ClampToEdge, false, DepthFunction.Lequal, false, 1, false, 0)));
        encoder.SetFragmentSamplerState(sampler, 0);
        var size = stackalloc float[2] { width, height };
        encoder.SetFragmentBytes((nint)size, 8, 1);
        encoder.DrawPrimitives(MTLPrimitiveType.Triangle, 0, 3);
        encoder.EndEncoding();
    }

    /// <summary>
    /// Clears the open pass's colour and / or depth over <paramref name="rect"/> (Vulkan's <c>vkCmdClearAttachments</c>): a fullscreen
    /// triangle under the rect's scissor, the colour and depth as the fragment's outputs. Metal's render passes clear whole attachments
    /// at their load only, so a partial clear (a shadow tile, a slice's depth) is a draw.
    /// </summary>
    internal unsafe void ClearAttachments(MTLRenderCommandEncoder encoder, in RenderingDesc desc, bool colour, ClearColour colourValue,
        bool depth, float depthValue, PixelRect rect)
    {
        var colourTexture = desc.Colour.IsNull ? default : ViewTexture(desc.Colour.View);
        var depthTexture = desc.Depth.IsNull ? default : ViewTexture(desc.Depth.View);
        colour &= colourTexture.NativePtr != IntPtr.Zero;
        depth &= depthTexture.NativePtr != IntPtr.Zero;
        if (!colour && !depth) return;
        var colourFormat = colourTexture.NativePtr != IntPtr.Zero ? colourTexture.PixelFormat : MTLPixelFormat.Invalid;
        var extraFormat = desc.Extra.IsNull ? MTLPixelFormat.Invalid : ViewTexture(desc.Extra.View).PixelFormat;
        var depthFormat = depthTexture.NativePtr != IntPtr.Zero ? depthTexture.PixelFormat : MTLPixelFormat.Invalid;
        ulong samples = Math.Max(1ul, colourTexture.NativePtr != IntPtr.Zero ? colourTexture.SampleCount : depthTexture.SampleCount);
        var key = (colourFormat, extraFormat, depthFormat, samples, colour, depth);
        if (!clearPipelines.TryGetValue(key, out var pipeline))
            clearPipelines[key] = pipeline = MakeClearPipeline(colourFormat, extraFormat, depthFormat, samples, colour, depth);
        encoder.SetRenderPipelineState(pipeline);
        if (depth) encoder.SetDepthStencilState(DepthState(true, true, DepthFunction.Always));
        else if (depthTexture.NativePtr != IntPtr.Zero) encoder.SetDepthStencilState(DepthState(false, false, DepthFunction.Always));
        encoder.SetViewport(new MTLViewport
        {
            originX = 0, originY = 0,
            width = colourTexture.NativePtr != IntPtr.Zero ? colourTexture.Width : depthTexture.Width,
            height = colourTexture.NativePtr != IntPtr.Zero ? colourTexture.Height : depthTexture.Height,
            znear = 0, zfar = 1,
        });
        encoder.SetScissorRect(new MTLScissorRect { x = (ulong)rect.X, y = (ulong)rect.Y, width = rect.Width, height = rect.Height });
        if (colour)
        {
            var value = stackalloc float[4] { colourValue.R, colourValue.G, colourValue.B, colourValue.A };
            encoder.SetFragmentBytes((nint)value, 16, 0);
        }
        if (depth)
        {
            float value = depthValue;
            encoder.SetFragmentBytes((nint)(&value), 4, 1);
        }
        encoder.DrawPrimitives(MTLPrimitiveType.Triangle, 0, 3);
    }

    MTLRenderPipelineState MakeClearPipeline(MTLPixelFormat colour, MTLPixelFormat extra, MTLPixelFormat depth, ulong samples, bool writesColour, bool writesDepth)
    {
        NSError error = default;
        var library = clearLibrary ??= Device.NewLibrary((NSString)ClearMsl, new MTLCompileOptions(), ref error);
        if (error.NativePtr != IntPtr.Zero) throw new InvalidOperationException($"the clear pass's MSL did not compile: {error.LocalizedDescription}");
        var d = new MTLRenderPipelineDescriptor(new ObjectiveCClass(ObjectiveC.objc_getClass("MTLRenderPipelineDescriptor")).AllocInit());
        d.VertexFunction = library.NewFunction((NSString)"clear_vertex");
        d.FragmentFunction = library.NewFunction((NSString)(writesColour && writesDepth ? "clear_both_fragment"
            : writesDepth ? "clear_depth_fragment" : "clear_colour_fragment"));
        d.RasterSampleCount = samples;
        if (colour != MTLPixelFormat.Invalid)
        {
            // A depth-only clear still names the pass's colour attachment: the mask off, so the pass's picture is untouched.
            var attachment = d.ColorAttachments.Object(0);
            attachment.PixelFormat = colour;
            attachment.WriteMask = writesColour ? MTLColorWriteMask.All : MTLColorWriteMask.None;
            d.ColorAttachments.SetObject(attachment, 0);
        }
        if (extra != MTLPixelFormat.Invalid)
        {
            // The second colour target (the GI mask): the clear never writes it, but the pass's pipeline must name it.
            var attachment = d.ColorAttachments.Object(1);
            attachment.PixelFormat = extra;
            attachment.WriteMask = MTLColorWriteMask.None;
            d.ColorAttachments.SetObject(attachment, 1);
        }
        if (depth != MTLPixelFormat.Invalid)
        {
            d.DepthAttachmentPixelFormat = depth;
            if (depth is MTLPixelFormat.Depth32FloatStencil8) d.StencilAttachmentPixelFormat = depth;
        }
        var pipeline = Device.NewRenderPipelineState(d, ref error);
        if (error.NativePtr != IntPtr.Zero) throw new InvalidOperationException($"the clear pass's pipeline failed: {error.LocalizedDescription}");
        return pipeline;
    }

    readonly Dictionary<MTLPixelFormat, MTLRenderPipelineState> blitPipelines = [];
    readonly Dictionary<(MTLPixelFormat Colour, MTLPixelFormat Extra, MTLPixelFormat Depth, ulong Samples, bool WritesColour, bool WritesDepth), MTLRenderPipelineState> clearPipelines = [];
    MTLLibrary? clearLibrary;

    MTLRenderPipelineState MakeBlitPipeline(MTLPixelFormat format)
    {
        NSError error = default;
        var library = Device.NewLibrary((NSString)BlitMsl, new MTLCompileOptions(), ref error);
        if (error.NativePtr != IntPtr.Zero) throw new InvalidOperationException($"the blit pass's MSL did not compile: {error.LocalizedDescription}");
        var d = new MTLRenderPipelineDescriptor(new ObjectiveCClass(ObjectiveC.objc_getClass("MTLRenderPipelineDescriptor")).AllocInit());
        d.VertexFunction = library.NewFunction((NSString)"blit_vertex");
        d.FragmentFunction = library.NewFunction((NSString)"blit_fragment");
        var attachment = d.ColorAttachments.Object(0);
        attachment.PixelFormat = format;
        d.ColorAttachments.SetObject(attachment, 0);
        var pipeline = Device.NewRenderPipelineState(d, ref error);
        if (error.NativePtr != IntPtr.Zero) throw new InvalidOperationException($"the blit pass's pipeline failed: {error.LocalizedDescription}");
        return pipeline;
    }

    /// <summary>The stand-in a sampler reads when nothing is bound: (0, 0, 0, 1), or depth 1 for a shadow sampler.</summary>
    public SampledTexture Dummy(SamplerInfo sampler)
    {
        int slot = sampler.Dimension == SamplerDimension.Cube ? 2 : sampler.Arrayed ? 1 : 0;
        var key = (slot, sampler.SampledKind, sampler.Depth);
        if (dummies.TryGetValue(key, out var made)) return made;
        var format = sampler.Depth ? InternalFormat.DepthComponent24 : sampler.SampledKind switch
        {
            ScalarKind.UInt => InternalFormat.R32ui,
            ScalarKind.Int => InternalFormat.R32i,
            _ => InternalFormat.Rgba8,
        };
        // A cube's six faces are its type's slices, not array layers: Layers is 1 for a plain cube (CreateTexture sets
        // ArrayLength 1), and GpuTexture.View asks for all six slices.
        int layers = 1;
        var texture = CreateTexture(new TextureDesc(format, 1, 1, Layers: layers, Kind: slot == 2 ? TextureKind.Cube : TextureKind.Texture2D,
            Use: TextureUse.Sampled | TextureUse.TransferDst, Name: "dummy texture"));
        // Shared storage: the CPU writes the texel where it is (a blit would need a command buffer of its own).
        var image = textures[texture.Index]!.Image;
        var region = new MTLRegion { origin = new MTLOrigin(), size = new MTLSize { width = 1, height = 1, depth = 1 } };
        if (sampler.Depth)
        {
            float one = 1f;
            image.ReplaceRegion(region, 0, (nint)(&one), 4);
        }
        else
        {
            // The texel value is the R component (the other channels come from the format): 0, as GL's default texture is (0, 0, 0, 1).
            Span<byte> texel = [0, 0, 0, 255];
            if (sampler.SampledKind is ScalarKind.UInt or ScalarKind.Int) texel = new byte[4];
            fixed (byte* p = texel) image.ReplaceRegion(region, 0, (nint)p, 4);
        }
        var view = texture.View();
        var sampledTexture = new SampledTexture(Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.Nearest, TextureMagFilter.Nearest,
            TextureWrapMode.Repeat, TextureWrapMode.Repeat, TextureWrapMode.Repeat, sampler.Depth, DepthFunction.Lequal, false, 1, false, 0)), view);
        dummyTextures.Add(texture);
        dummies[key] = sampledTexture;
        return sampledTexture;
    }

    /// <summary>The generic attribute value a disabled vertex input reads (GL's constant (0, 0, 0, 1), bound with stride 0).</summary>
    public GpuBuffer DummyVertex => dummyVertex;

    /// <summary>
    /// A texture's level 0 as RGBA bytes, read back through a blit (a texture is not CPU-mappable, even in shared storage): the
    /// display's and tests' picture check. Rows in the texture's own order.
    /// </summary>
    public byte[] Readback(GpuTexture texture)
    {
        var desc = textures[texture.Index]!.Desc;
        int rowBytes = desc.Width * 4;
        var command = Queue.CommandBuffer();
        var blit = command.BlitCommandEncoder();
        var buffer = Device.NewBuffer((ulong)(rowBytes * desc.Height), MTLResourceOptions.ResourceStorageModeShared);
        blit.CopyFromTexture(TextureAt(texture), 0, 0, new MTLOrigin(), new MTLSize { width = (ulong)desc.Width, height = (ulong)desc.Height, depth = 1 },
            buffer, 0, (ulong)rowBytes, (ulong)(rowBytes * desc.Height));
        blit.EndEncoding();
        command.Commit();
        command.WaitUntilCompleted();
        var pixels = new byte[rowBytes * desc.Height];
        Marshal.Copy(buffer.Contents, pixels, 0, pixels.Length);
        buffer.Dispose();
        return pixels;
    }

    public void Dispose()
    {
        foreach (var p in blitPipelines.Values) p.Dispose();
        blitPipelines.Clear();
        foreach (var p in clearPipelines.Values) p.Dispose();
        clearPipelines.Clear();
        clearLibrary?.Dispose();
        foreach (var v in levelViews) v.Dispose();
        levelViews.Clear();
        bindless.Dispose();
        foreach (var t in dummyTextures) t.Dispose();
        dummyTextures.Clear();
        foreach (var b in buffers) b?.Release();
        buffers.Clear();
    }
}
