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
/// compiles the source. Only the legacy model is translated so far (the sky pass's).
/// </summary>
public sealed class MetalShaders : IGpuShaders
{
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
        // The default blocks move to set 1 (vertex binding 0, fragment binding 1), as the Vulkan path's do (SpirvPatch, VkGl's step).
        byte[] v = c.Vertex.DefaultBlock is null ? c.VertexSpirv : SpirvPatch.MoveDefaultBlock(c.VertexSpirv, 0);
        byte[] f = c.Fragment.DefaultBlock is null ? c.FragmentSpirv : SpirvPatch.MoveDefaultBlock(c.FragmentSpirv, 1);

        if (Environment.GetEnvironmentVariable("MEITOU_METAL_DUMP") is { } dir)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, $"{name}.vert.spv"), v);
            File.WriteAllBytes(Path.Combine(dir, $"{name}.frag.spv"), f);
        }

        // The program's sets in the engine's words, as ShaderLibrary.Legacy builds them for Vulkan — off Metal's own reflection.
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
