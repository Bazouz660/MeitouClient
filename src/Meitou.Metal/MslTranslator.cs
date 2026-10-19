using System.Runtime.InteropServices;
using Silk.NET.SPIRV;
using Silk.NET.SPIRV.Cross;

namespace Meitou.Metal;

/// <summary>
/// SPIR-V to Metal Shading Language, in process: the step between the engine's shader compiler (shaderc, which makes SPIR-V)
/// and Metal (which wants MSL). The same translator MoltenVK embeds, without the subprocess and without the CLI tool.
/// </summary>
public static unsafe class MslTranslator
{
    /// <summary>The MSL version the shaders are written for (Metal 3).</summary>
    public const uint MslVersion = 30000;

    /// <summary>
    /// The MSL for one SPIR-V module, with its entry point renamed (the MSL entry point names have to be unique per stage:
    /// spirv-cross names the stages' entry points the same).
    /// </summary>
    public static string Translate(byte[] spirv, string entryPoint, ExecutionModel stage, uint mslVersion = MslVersion)
    {
        ArgumentNullException.ThrowIfNull(spirv);
        var cross = Cross.GetApi();

        Context* context = null;
        cross.ContextCreate(&context);

        var words = new uint[spirv.Length / 4];
        Buffer.BlockCopy(spirv, 0, words, 0, spirv.Length);
        fixed (uint* module = words)
        {
            ParsedIr* parsed = null;
            if (cross.ContextParseSpirv(context, module, (nuint)words.Length, &parsed) != Result.Success)
                throw new InvalidOperationException($"spirv-cross could not parse the module: {Error(cross, context)}");

            Compiler* compiler = null;
            if (cross.ContextCreateCompiler(context, Backend.Msl, parsed, CaptureMode.Copy, &compiler) != Result.Success)
                throw new InvalidOperationException($"spirv-cross could not make a compiler: {Error(cross, context)}");

            CompilerOptions* options = null;
            cross.CompilerCreateCompilerOptions(compiler, &options);
            // The MSL resource indices then equal the SPIR-V binding numbers, so a Metal backend can mirror the engine's
            // binding scheme instead of carrying a per-shader mapping table.
            cross.CompilerOptionsSetUint(options, CompilerOption.MslVersion, mslVersion);
            cross.CompilerOptionsSetUint(options, CompilerOption.MslPlatform, (uint)MslPlatform.Macos);
            cross.CompilerOptionsSetBool(options, CompilerOption.MslEnableDecorationBinding, 1);
            cross.CompilerInstallCompilerOptions(compiler, options);
            cross.CompilerRenameEntryPoint(compiler, "main", entryPoint, stage);

            byte* source = null;
            if (cross.CompilerCompile(compiler, &source) != Result.Success)
                throw new InvalidOperationException($"spirv-cross could not compile the module: {Error(cross, context)}");
            return Marshal.PtrToStringUTF8((nint)source) ?? "";
        }
    }

    static string Error(Cross cross, Context* context)
    {
        byte* message = cross.ContextGetLastErrorString(context);
        return message is null ? "no message" : Marshal.PtrToStringUTF8((nint)message) ?? "no message";
    }
}
