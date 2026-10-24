using Meitou.Content;

namespace Meitou.Data.Physics;

/// <summary>Finds the collision (and other asset) files that records name by install-relative paths.</summary>
public static class CollisionPaths
{
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Dictionary<string, string>> modFolders = new();

    /// <summary>Each mod's name to its folder (local mods over Workshop ones, as <see cref="ModList"/> finds them), once per install.</summary>
    static Dictionary<string, string> ModFolders(GameInstall install) =>
        modFolders.GetOrAdd(install.Root, _ => ModList.FindMods(install).ToDictionary(m => m.Key, m => Path.GetDirectoryName(m.Value)!, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// The file a record's path field names (<c>.\data\buildings\x.xml</c>, backslashes, optionally without the leading <c>.\</c>) under the install,
    /// or null when the field is empty or the file is not there.
    /// </summary>
    public static string? Resolve(GameInstall install, string recordPath)
    {
        if (string.IsNullOrWhiteSpace(recordPath)) return null;
        var rel = recordPath.Replace('\\', '/').TrimStart('.', '/');
        var full = GameFiles.Resolve(Path.Combine(install.Root, rel));
        if (File.Exists(full)) return full;
        // A mod's own files: <c>.\mods\Mod Name\x</c> is the folder of that mod, which for a Workshop mod is its Workshop folder, not a folder under the install.
        if (rel.StartsWith("mods/", StringComparison.OrdinalIgnoreCase) && rel.IndexOf('/', 5) is var slash and > 5
            && ModFolders(install).TryGetValue(rel[5..slash], out var folder))
        {
            full = GameFiles.Resolve(Path.Combine(folder, rel[(slash + 1)..]));
            if (File.Exists(full)) return full;
        }
        // Records may name the file relative to data/.
        full = GameFiles.Resolve(Path.Combine(install.DataDirectory, rel.StartsWith("data/", StringComparison.OrdinalIgnoreCase) ? rel[5..] : rel));
        return File.Exists(full) ? full : null;
    }
}
