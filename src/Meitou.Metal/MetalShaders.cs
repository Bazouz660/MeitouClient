using Meitou.Rendering.Gpu;
using Meitou.Rendering.Gpu.Shaders;
using SharpMetal.Foundation;
using SharpMetal.Metal;
using Silk.NET.SPIRV;

namespace Meitou.Metal;

/// <summary>
/// The backend's own artifacts of a program (the twin of <c>VulkanProgram</c>): the MSL libraries and
/// functions. The engine's compile result (<see cref="ProgramInfo"/>) lives on the handle.
/// </summary>
internal sealed class MetalProgram : IDisposable
{
    public MetalProgram(ProgramInfo info, MTLLibrary vertexLibrary, MTLFunction vertexFunction, string vertexMsl,
        MTLLibrary fragmentLibrary, MTLFunction fragmentFunction, string fragmentMsl)
    {
        Info = info;
        (VertexLibrary, VertexFunction, VertexMsl) = (vertexLibrary, vertexFunction, vertexMsl);
        (FragmentLibrary, FragmentFunction, FragmentMsl) = (fragmentLibrary, fragmentFunction, fragmentMsl);
    }

    public ProgramInfo Info { get; }
    public MTLLibrary VertexLibrary { get; }
    public MTLFunction VertexFunction { get; }
    /// <summary>The vertex stage's MSL (diagnostics: what SPIR-V became).</summary>
    public string VertexMsl { get; }
    public MTLLibrary FragmentLibrary { get; }
    public MTLFunction FragmentFunction { get; }
    public string FragmentMsl { get; }

    public void Dispose()
    {
        VertexFunction.Dispose();
        VertexLibrary.Dispose();
        FragmentFunction.Dispose();
        FragmentLibrary.Dispose();
    }
}

/// <summary>
/// The programs the engine's shaders compile to, on Metal: the engine's own compiler (shaderc) makes the
/// SPIR-V (the fragment stage's bindings at 0 — Metal's stages have their own binding tables, so the
/// Vulkan scheme's 32 offset is not needed), spirv-cross translates it to MSL in process, and Metal
/// compiles the source.
/// </summary>
public sealed class MetalShaders : IGpuShaders
{
    /// <summary>
    /// Metal's stages have one binding table per kind (<c>[[buffer(n)]]</c>, <c>[[texture(n)]]</c>), and its mapping is by
    /// the binding decoration alone — set included, since sets are not namespaced (docs/metal-backend.md). Every resource's
    /// binding flattens to base + set × stride + binding, both in the SPIR-V before the translation and in the runtime
    /// binds (<see cref="MetalCommandList"/>), so the two agree. The vertex stage's base leaves the indices below it to the
    /// vertex buffers (the pipeline's slots, Vulkan's own numbers: <see cref="MetalPipelines.VertexDescriptor"/>); its top
    /// index belongs to the push constants (<see cref="MslTranslator.PushConstantBinding"/>).
    /// </summary>
    internal const uint SetStride = 8;
    internal const uint VertexBlockBase = 15;

    /// <summary>A slot's index in its stage's MSL table. The bases are per kind (Metal has one table per kind and only the
    /// buffer tables carry the vertex buffers): a vertex module's buffers shift by <see cref="VertexBlockBase"/>, its images
    /// and samplers — and the fragment stage's everything — flatten from 0.</summary>
    internal static uint Flatten(GpuSlotKind kind, GpuStage stages, uint set, uint binding) =>
        (stages.HasFlag(GpuStage.Vertex) && kind is GpuSlotKind.UniformBuffer or GpuSlotKind.StorageBuffer ? VertexBlockBase : 0)
        + set * SetStride + binding;

    readonly MTLDevice device;
    readonly List<MetalProgram> programs = [];

    public MetalShaders(MTLDevice device) => this.device = device;

    /// <summary>The compiler the programs go through: Metal's own options (per-stage bindings at 0, no cache shared with Vulkan's).</summary>
    public GlslProgramCompiler Compiler { get; } = new(new ShaderCompileOptions { FragmentBindingBase = 0, UseDiskCache = false });

    /// <summary>The backend's identity of a program handle (the internals resolve through this).</summary>
    internal MetalProgram Backend(GpuProgram program) => programs[program.Index];

    public GpuProgram Legacy(string vertexGlsl, string fragmentGlsl, string name)
    {
        var c = Compiler.Compile(vertexGlsl, fragmentGlsl);
        // The default blocks move to set 1 (vertex binding 0, fragment binding 1), as the Vulkan path's do (SpirvPatch, VkGl's step);
        // then both stages' bindings flatten onto Metal's one table per stage (the vertex stage's blocks above the vertex buffers).
        byte[] v = SpirvPatch.RemapBindings(c.Vertex.DefaultBlock is null ? c.VertexSpirv : SpirvPatch.MoveDefaultBlock(c.VertexSpirv, 0), VertexBlockBase, 0, SetStride);
        byte[] f = SpirvPatch.RemapBindings(c.Fragment.DefaultBlock is null ? c.FragmentSpirv : SpirvPatch.MoveDefaultBlock(c.FragmentSpirv, 1), 0, 0, SetStride);

        if (Environment.GetEnvironmentVariable("MEITOU_METAL_DUMP") is { } dir)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, $"{name}.vert.spv"), v);
            File.WriteAllBytes(Path.Combine(dir, $"{name}.frag.spv"), f);
        }

        // The program's sets in the engine's words, as ShaderLibrary.Legacy builds them for Vulkan — off Metal's own reflection.
        // The slots carry the engine's numbers (the guests bind by them); MetalCommandList flattens at the binds.
        var slots = new List<GpuSlot>();
        void AddSlots(ShaderReflection r, GpuStage stage)
        {
            foreach (var b in r.Blocks)
                if (b.Kind == BlockKind.Uniform && !b.IsDefault) slots.Add(new GpuSlot((uint)b.Binding, GpuSlotKind.UniformBuffer, stage));
            foreach (var s in r.Samplers) slots.Add(new GpuSlot((uint)s.Binding, GpuSlotKind.CombinedSampler, stage, (uint)Math.Max(1, s.ArrayLength)));
        }
        AddSlots(c.Vertex, GpuStage.Vertex);
        AddSlots(c.Fragment, GpuStage.Fragment);
        var defaultSlots = new List<GpuSlot>();
        if (c.Vertex.DefaultBlock is not null) defaultSlots.Add(new GpuSlot(0, GpuSlotKind.UniformBuffer, GpuStage.Vertex, 1, OffsetAtBind: true));
        if (c.Fragment.DefaultBlock is not null) defaultSlots.Add(new GpuSlot(1, GpuSlotKind.UniformBuffer, GpuStage.Fragment, 1, OffsetAtBind: true));
        var info = new ProgramInfo
        {
            Index = programs.Count, Name = name, Model = ShaderModel.Legacy, VertexCode = v, FragmentCode = f, Compiled = c,
            VertexReflection = c.Vertex, FragmentReflection = c.Fragment,
            NeutralSets = [GpuSetLayout.Written([.. slots]), GpuSetLayout.Fixed([.. defaultSlots])],
        };

        // One library per stage: spirv-cross names both entry points the same, so two stages in one source collide (docs/metal-backend.md).
        var vertexMsl = MslTranslator.Translate(v, "vertex_main", ExecutionModel.Vertex);
        var fragmentMsl = MslTranslator.Translate(f, "fragment_main", ExecutionModel.Fragment);
        if (Environment.GetEnvironmentVariable("MEITOU_METAL_DUMP") is { } dump)
        {
            Directory.CreateDirectory(dump);
            File.WriteAllText(Path.Combine(dump, $"{name}.vert.msl"), vertexMsl);
            File.WriteAllText(Path.Combine(dump, $"{name}.frag.msl"), fragmentMsl);
        }
        var vertexLibrary = Library(vertexMsl, name);
        var fragmentLibrary = Library(fragmentMsl, name);
        programs.Add(new MetalProgram(info, vertexLibrary, Function(vertexLibrary, "vertex_main", name), vertexMsl,
            fragmentLibrary, Function(fragmentLibrary, "fragment_main", name), fragmentMsl));
        return new GpuProgram(info.Index, info);
    }

    /// <summary>Only the legacy model is translated so far; the native model's and compute programs come with their passes.</summary>
    public GpuProgram Compute(string glsl, string name) => throw new NotSupportedException("the Metal backend has no compute programs yet (the sky pass needs none)");

    /// <summary>
    /// A native-model program: strict GLSL 450, the layout given by the caller (<paramref name="setLayouts"/>, not owned) and a
    /// push-constant block of <paramref name="pushConstantBytes"/> bytes, as ShaderLibrary.Native builds it for Vulkan. The
    /// stages' bindings flatten onto Metal's one table per stage before the translation, so the runtime binds at the same
    /// indices (<see cref="Flatten"/>, <see cref="MetalCommandList.BindSets"/>).
    /// </summary>
    public GpuProgram Native(string vertexGlsl, string fragmentGlsl, string name, GpuSetLayout[] setLayouts, uint pushConstantBytes)
    {
        if (pushConstantBytes > 128) throw new ArgumentOutOfRangeException(nameof(pushConstantBytes), "push constants are limited to 128 bytes (the guaranteed minimum)");
        var (vs, fs) = InterfaceLocations.Apply(vertexGlsl, fragmentGlsl);
        var v = SpirvPatch.RemapBindings(Compiler.CompileNative(vs, fragment: false), VertexBlockBase, 0, SetStride);
        var f = SpirvPatch.RemapBindings(Compiler.CompileNative(fs, fragment: true), 0, 0, SetStride);
        var rv = SpirvReflection.Parse(v);
        var rf = SpirvReflection.Parse(f);
        foreach (var block in rv.Blocks.Concat(rf.Blocks))
            if (block.Kind == BlockKind.PushConstant && block.Size > pushConstantBytes)
                throw new InvalidOperationException($"{name}: push-constant block '{block.Name}' is {block.Size} bytes, the layout's range {pushConstantBytes}");

        if (Environment.GetEnvironmentVariable("MEITOU_METAL_DUMP") is { } dir)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, $"{name}.vert.spv"), v);
            File.WriteAllBytes(Path.Combine(dir, $"{name}.frag.spv"), f);
        }

        var info = new ProgramInfo
        {
            Index = programs.Count, Name = name, Model = ShaderModel.Native, VertexCode = v, FragmentCode = f,
            VertexReflection = rv, FragmentReflection = rf, PushConstantBytes = pushConstantBytes, NeutralSets = setLayouts,
        };
        // The bindless set's runtime arrays are Metal 3 tier-2 argument buffers (docs/metal-backend.md); only a program
        // whose layout includes the table is translated with them.
        bool bindless = Array.Exists(setLayouts, l => ReferenceEquals(l, GpuSetLayout.Bindless));
        uint? tableSet = bindless ? Rendering.NativeShaders.BindlessSet : null;
        var vertexMsl = MslTranslator.Translate(v, "vertex_main", ExecutionModel.Vertex, argumentBufferSet: tableSet);
        var fragmentMsl = MslTranslator.Translate(f, "fragment_main", ExecutionModel.Fragment, argumentBufferSet: tableSet);
        if (Environment.GetEnvironmentVariable("MEITOU_METAL_DUMP") is { } dump)
        {
            Directory.CreateDirectory(dump);
            File.WriteAllText(Path.Combine(dump, $"{name}.vert.msl"), vertexMsl);
            File.WriteAllText(Path.Combine(dump, $"{name}.frag.msl"), fragmentMsl);
        }
        var vertexLibrary = Library(vertexMsl, name);
        var fragmentLibrary = Library(fragmentMsl, name);
        programs.Add(new MetalProgram(info, vertexLibrary, Function(vertexLibrary, "vertex_main", name), vertexMsl,
            fragmentLibrary, Function(fragmentLibrary, "fragment_main", name), fragmentMsl));
        return new GpuProgram(info.Index, info);
    }

    public void Dispose(GpuProgram program) => Backend(program).Dispose();

    MTLLibrary Library(string msl, string name)
    {
        NSError error = default;
        var library = device.NewLibrary((NSString)msl, new MTLCompileOptions(), ref error);
        if (error.NativePtr != IntPtr.Zero)
            throw new InvalidOperationException($"{name}: the MSL did not compile: {error.LocalizedDescription}");
        return library;
    }

    static MTLFunction Function(MTLLibrary library, string entry, string name)
    {
        var function = library.NewFunction((NSString)entry);
        if (function.NativePtr == IntPtr.Zero) throw new InvalidOperationException($"{name}: no function {entry} in the MSL");
        return function;
    }
}
