namespace Meitou.Rendering.Gpu;

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
