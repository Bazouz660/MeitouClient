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
/// A buffer the renderers hold: the backend's identity (the index into the backend's buffers) and
/// its size (docs/resource-layer.md). The buffer itself is the backend's.
/// </summary>
public readonly record struct GpuBuffer(int Index, ulong Size)
{
    /// <summary>The null buffer (a backend's empty buffer).</summary>
    public static GpuBuffer Null => new(0, 0);

    public bool IsNull => Index == 0;
}
