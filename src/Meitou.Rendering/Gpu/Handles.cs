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
