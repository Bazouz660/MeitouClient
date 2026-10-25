using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Meitou.Launcher;

/// <summary>
/// The published launcher is one Native AOT executable, but Avalonia's native libraries (Skia, HarfBuzz, ANGLE on Windows,
/// AvaloniaNative on macOS) cannot be
/// linked in statically. The csproj embeds them as <c>native/&lt;file&gt;</c> resources; this writes them once to
/// <c>native/&lt;content hash&gt;/</c> in <see cref="LauncherConfig.DataDirectory"/> and loads them from there (docs/launcher.md).
/// Debug builds embed nothing and load them from next to the executable as usual.
/// </summary>
static class NativeLibraries
{
    const string Prefix = "native/";

    public static void Load()
    {
        var assembly = typeof(NativeLibraries).Assembly;
        var names = assembly.GetManifestResourceNames().Where(n => n.StartsWith(Prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToArray();
        var handles = new Dictionary<string, IntPtr>(StringComparer.OrdinalIgnoreCase);
        // The launcher's own imports (news videos): OpenAL Soft by its file names, libvpx (linked in when published) by the system's.
        NativeLibrary.SetDllImportResolver(assembly, (library, _, search) =>
        {
            string[] candidates = library switch
            {
                "openal" => ["soft_oal", "libopenal", "libopenal.so.1", "/System/Library/Frameworks/OpenAL.framework/OpenAL"],
                "vpx" => ["vpx", "libvpx.so.9", "libvpx.so.8", "libvpx.so.7", "libvpx", "/opt/homebrew/lib/libvpx.dylib", "/usr/local/lib/libvpx.dylib"],
                _ => [],
            };
            foreach (var candidate in candidates)
                if (handles.GetValueOrDefault(candidate) is not 0 and var embedded) return embedded;
                else if (NativeLibrary.TryLoad(candidate, assembly, search, out var loaded)) return loaded;
            return 0;
        });
        if (names.Length == 0) return;

        // Named by content, so a launcher update with other libraries never overwrites ones a running launcher has loaded.
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var name in names)
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(name));
            var buffer = new byte[81920];
            for (int read; (read = stream.Read(buffer)) > 0;) hash.AppendData(buffer, 0, read);
        }
        var directory = Path.Combine(LauncherConfig.DataDirectory, "native", Convert.ToHexStringLower(hash.GetHashAndReset())[..16]);
        Directory.CreateDirectory(directory);

        foreach (var name in names)
        {
            var path = Path.Combine(directory, name[Prefix.Length..]);
            using (var stream = assembly.GetManifestResourceStream(name)!)
                if (!File.Exists(path) || new FileInfo(path).Length != stream.Length)
                {
                    // Via a temporary file, so a crash or a second launcher starting at once never leaves a partial library.
                    var temp = $"{path}.{Environment.ProcessId}.tmp";
                    using (var file = File.Create(temp)) stream.CopyTo(file);
                    File.Move(temp, path, overwrite: true);
                }
            // Loaded by full path now: ANGLE is then found by name (Windows reuses a loaded module), Skia and HarfBuzz through the resolver.
            handles[Path.GetFileNameWithoutExtension(path)] = NativeLibrary.Load(path);
        }

        // Earlier launchers' libraries; one still in use by a running launcher is left for next time.
        foreach (var old in Directory.EnumerateDirectories(Path.GetDirectoryName(directory)!).Where(d => d != directory))
            try { Directory.Delete(old, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

        DllImportResolver resolver = (library, _, _) => handles.GetValueOrDefault(Path.GetFileNameWithoutExtension(library));
        NativeLibrary.SetDllImportResolver(typeof(SkiaSharp.SKObject).Assembly, resolver);
        NativeLibrary.SetDllImportResolver(typeof(HarfBuzzSharp.Blob).Assembly, resolver);
        // macOS's libAvaloniaNative: dyld never matches a bare name to an already loaded library, so it needs the resolver too.
        NativeLibrary.SetDllImportResolver(typeof(Avalonia.AvaloniaNativePlatformOptions).Assembly, resolver);
    }
}
