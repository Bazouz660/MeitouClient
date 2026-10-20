namespace Meitou.Rendering.Gpu;

/// <summary>What a view covers (the engine's words; the backend maps it to its own).</summary>
public enum GpuViewType { Texture2D, Texture2DArray, Cube }

/// <summary>Which component a texel reads (the swizzle of a sampled view). The default is the identity.</summary>
public enum GpuSwizzleComponent { Identity, R, G, B, A, One, Zero }

/// <summary>The swizzle of a sampled view (the engine's words; the backend maps it). The default is the identity.</summary>
public readonly record struct GpuSwizzle(GpuSwizzleComponent R = GpuSwizzleComponent.Identity, GpuSwizzleComponent G = GpuSwizzleComponent.Identity,
    GpuSwizzleComponent B = GpuSwizzleComponent.Identity, GpuSwizzleComponent A = GpuSwizzleComponent.Identity)
{
    /// <summary>The identity swizzle.</summary>
    public static GpuSwizzle Identity => default;

    /// <summary>The red channel in all three, alpha one (a single-channel format).</summary>
    public static GpuSwizzle RrrOne => new(GpuSwizzleComponent.R, GpuSwizzleComponent.R, GpuSwizzleComponent.R, GpuSwizzleComponent.One);
}

/// <summary>
/// A sampler the renderers hold: the backend's identity and the description it was made from
/// (docs/resource-layer.md). The Vulkan sampler itself is the backend's; index 0 is the null one.
/// </summary>
public readonly record struct GpuSampler(int Index, SamplerDesc Desc)
{
    /// <summary>The null sampler (a backend's empty sampler).</summary>
    public static GpuSampler Null => new(0, default);

    public bool IsNull => Index == 0;
}

/// <summary>
/// A view of a texture the renderers hold: the backend's identity (the index into the backend's
/// views) (docs/resource-layer.md). The level and layer range it covers is the backend's.
/// </summary>
public readonly record struct GpuView(int Index)
{
    /// <summary>The null view (a backend's empty view).</summary>
    public static GpuView Null => new(0);

    public bool IsNull => Index == 0;
}

/// <summary>
/// A graphics pipeline the renderers hold: the description it was made from (docs/resource-layer.md)
/// — it is the pipeline's identity (the backend caches by it). The pipeline state is the backend's
/// (Vulkan: a VkPipeline; Metal: an MTLRenderPipelineState).
/// </summary>
public readonly record struct GpuPipeline(GraphicsPipelineDesc Desc)
{
    /// <summary>The null pipeline (no program).</summary>
    public static GpuPipeline Null => default;

    public bool IsNull => Desc.Program is null;
}

/// <summary>
/// A compute pipeline the renderers hold: the description it was made from — the pipeline's identity.
/// </summary>
public readonly record struct GpuComputePipeline(ComputePipelineDesc Desc)
{
    /// <summary>The null pipeline (no program).</summary>
    public static GpuComputePipeline Null => default;

    public bool IsNull => Desc.Program is null;
}

/// <summary>The part of a buffer a copy covers (the source's offset, the destination's, and the size).</summary>
public readonly record struct GpuBufferCopy(ulong SourceOffset, ulong DestinationOffset, ulong Size);

/// <summary>Which pipeline a command belongs to (the stages it runs in).</summary>
public enum GpuBindPoint { Graphics, Compute }

/// <summary>
/// A set of resources the renderers bind to a program: the backend's identity (the index into the
/// backend's sets) (docs/resource-layer.md). The set itself is the backend's (Vulkan: a descriptor
/// set; Metal: the program's argument table).
/// </summary>
public readonly record struct GpuSet(int Index)
{
    /// <summary>The null set.</summary>
    public static GpuSet Null => new(0);

    public bool IsNull => Index == 0;
}

/// <summary>
/// A buffer the renderers hold: the backend's identity (the index into the backend's buffers) and
/// its size (docs/resource-layer.md). The buffer itself is the backend's.
/// </summary>
public readonly record struct GpuBuffer(int Index, ulong Size)
{
    /// <summary>The null buffer (a backend's empty buffer).</summary>
    public static GpuBuffer Null => new(0, 0);

    public bool IsNull => Index == 0;
}
