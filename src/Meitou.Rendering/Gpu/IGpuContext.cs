namespace Meitou.Rendering.Gpu;

/// <summary>
/// The seam between the renderers and a graphics API (docs/resource-layer.md): what the renderers
/// may ask of a backend. The Vulkan context implements it as it is; the Metal one grows a pass at
/// a time, and an operation it does not have yet throws.
/// </summary>
public interface IGpuContext
{
    /// <summary>A new texture: the backend makes its own image and its own layout rules.</summary>
    GpuTexture CreateTexture(TextureDesc desc);

    /// <summary>
    /// A view of a texture's level and layer range: the backend cuts it (or its own equivalent) and
    /// keeps it.
    /// </summary>
    GpuView View(GpuTexture texture, int baseLevel, int levels, int baseLayer, int layers, GpuViewType type, GpuSwizzle swizzle);

    /// <summary>Releases a texture the backend made (and the views cut on it).</summary>
    void ReleaseTexture(GpuTexture texture);

    /// <summary>A new device-local buffer of <paramref name="size"/> bytes.</summary>
    GpuBuffer CreateBuffer(ulong size, BufferUse use, string name);

    /// <summary>Releases a device-local buffer the backend made.</summary>
    void ReleaseBuffer(GpuBuffer buffer);
}
