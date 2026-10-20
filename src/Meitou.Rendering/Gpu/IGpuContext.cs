namespace Meitou.Rendering.Gpu;

/// <summary>
/// The seam between the renderers and a graphics API (docs/resource-layer.md): what the renderers
/// may ask of a backend. The Vulkan context implements it as it is; the Metal one grows a pass at
/// a time, and an operation it does not have yet throws.
/// </summary>
public interface IGpuContext
{
    // ---- the resources ----

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

    // ---- the services ----

    /// <summary>The frame being recorded (its number, its constants, its command list).</summary>
    GpuFrame Frame { get; }

    /// <summary>The pipelines by description (the engine's own).</summary>
    PipelineLibrary Pipelines { get; }

    /// <summary>The programs the engine's shaders compile to.</summary>
    ShaderLibrary Shaders { get; }

    /// <summary>The samplers by description (the backend's objects; the renderers hold the handles).</summary>
    SamplerCache Samplers { get; }

    /// <summary>The uploads (the staging copies into the textures and the buffers).</summary>
    Uploader Uploads { get; }

    /// <summary>The stand-ins the samplers read when nothing is bound.</summary>
    GpuDefaults Defaults { get; }

    /// <summary>Frame-global textures, blocks and uniform values by GLSL name (docs/renderer-native.md 4.3).</summary>
    FrameGlobals Globals { get; }

    /// <summary>The bindless table (the arrays of textures the shaders index by number).</summary>
    IGpuBindlessTable Bindless { get; }

    /// <summary>The upscaler's texture LOD bias: added to the mip level of every mipmapped fetch.</summary>
    float LodBias { get; set; }
}
