using Meitou.Rendering.Gpu.Shaders;
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

    /// <summary>The frame being recorded (its number, its resource states, its command list).</summary>
    IGpuFrame Frame { get; }

    /// <summary>The pipelines by description (the engine's own).</summary>
    IGpuPipelines Pipelines { get; }

    /// <summary>The programs the engine's shaders compile to.</summary>
    IGpuShaders Shaders { get; }

    /// <summary>The samplers by description (the backend's objects; the renderers hold the handles).</summary>
    IGpuSamplers Samplers { get; }

    /// <summary>The uploads (the staging copies into the textures and the buffers).</summary>
    IGpuUploads Uploads { get; }

    /// <summary>The stand-in a sampler reads when nothing is bound (GL's default texture, by the sampler's declaration).</summary>
    SampledTexture Dummy(SamplerInfo sampler);

    /// <summary>The generic attribute value a disabled vertex input reads (GL's constant (0, 0, 0, 1), bound with stride 0).</summary>
    GpuBuffer DummyVertex { get; }

    /// <summary>Frame-global textures, blocks and uniform values by GLSL name (docs/renderer-native.md 4.3).</summary>
    FrameGlobals Globals { get; }

    /// <summary>The bindless table (the arrays of textures the shaders index by number).</summary>
    IGpuBindlessTable Bindless { get; }

    /// <summary>The device limit a uniform buffer slice's offset must align to (the default blocks' slices).</summary>
    ulong UniformBufferAlign { get; }

    /// <summary>
    /// A set that lives as long as the context, its slots written once from the bindings (the layout says what each slot holds and
    /// whether its offset comes at bind time — then the binding's offset is ignored and the bind's dynamic offset is used).
    /// </summary>
    GpuSet CreateSet(GpuSetLayout layout, ReadOnlySpan<GpuBinding> bindings);

    // ---- the host's recording model ----

    /// <summary>
    /// A guest's segment in the open host pass: draws only (no rendering, barriers, copies or dispatches), with the host's targets and
    /// state (<see cref="CurrentTargets"/>, <see cref="CurrentState"/>). Inside a rendering with secondaries it is a secondary of its own,
    /// executed in its place. Without a host pass it throws. Closed by <see cref="EndGuest"/>.
    /// </summary>
    IGpuCommandList BeginGuest(string label);

    /// <summary>Closes a guest segment (<see cref="BeginGuest"/>).</summary>
    void EndGuest(IGpuCommandList cmd);

    /// <summary>
    /// A native segment of its own outside any pass (a full barrier before and after; nothing is assumed bound): copies, dispatches and
    /// the renderers' own renderings. Closed by <see cref="EndNative"/>.
    /// </summary>
    IGpuCommandList BeginNative(string label);

    /// <summary>Closes a native segment (<see cref="BeginNative"/>).</summary>
    void EndNative(IGpuCommandList cmd);

    /// <summary>Whether a host pass or a native segment is open (the guests check it before they draw).</summary>
    bool PassOpen { get; }

    /// <summary>What the open pass draws into (its host's targets, viewport and scissor); throws when no pass is open.</summary>
    PassTargets CurrentTargets();

    /// <summary>The state the open pass's host hands its guests; throws when no pass is open.</summary>
    DrawState CurrentState();

    /// <summary>Records something that does not disturb a pass (a timestamp, a label) where the frame's commands go now.</summary>
    void Interleave(Action<IGpuCommandList> record);

    /// <summary>Whether the device is an integrated GPU (its budgets and star layers differ).</summary>
    bool IsIntegrated { get; }

    /// <summary>The upscaler's texture LOD bias: added to the mip level of every mipmapped fetch.</summary>
    float LodBias { get; set; }
}
