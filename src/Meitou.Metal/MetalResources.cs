using Meitou.Rendering.Gpu;
using SharpMetal.Metal;

namespace Meitou.Metal;

/// <summary>
/// The backend's own objects behind the engine's handles (docs/resource-layer.md). Every table starts at
/// index 0 reserved for the null handle; entries are only removed by their release (the handle's index is
/// never reused). Render thread only.
/// </summary>
internal sealed class MetalTexture
{
    public required MTLTexture Image { get; init; }
    public required TextureDesc Desc { get; init; }
    /// <summary>The views cut on this texture (freed with it).</summary>
    public readonly List<MetalTextureView> Views = [];

    public void Release() => Image.Dispose();
}

/// <summary>A view of a texture's level and layer range (Metal: a texture view; the backend cuts it at <c>IGpuContext.View</c>).</summary>
internal sealed class MetalTextureView
{
    public required MTLTexture Image { get; init; }
    public required MetalTexture Of { get; init; }
}

internal sealed class MetalBuffer
{
    public required MTLBuffer Buffer { get; init; }
    public required string Name { get; init; }

    public void Release() => Buffer.Dispose();
}

/// <summary>
/// A persistent set (<see cref="IGpuContext.CreateSet"/>): the layout it was made for and the bindings it was
/// created from. On Metal a set is bound straight onto the encoder (no argument buffer yet), so the command
/// list reads the bindings back out at <c>BindSets</c>.
/// </summary>
internal sealed class MetalSet
{
    public required GpuSetLayout Layout { get; init; }
    public required GpuBinding[] Bindings { get; init; }
}
