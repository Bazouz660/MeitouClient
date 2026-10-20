using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Meitou.Data;

namespace Meitou.Net.Session;

/// <summary>One data file of the load order: its name, the first 8 bytes of the SHA-256 of its content (little-endian) and its length.</summary>
public sealed record DataSetEntry(string Name, ulong Hash, long Length);

/// <summary>
/// What a process loaded: the files of its <see cref="LoadOrder"/> in order, by content hash (docs/net.md, "Protocol and handshake"). Host and client
/// must hold equal identities, else their worlds differ; hashes (not names or timestamps) catch mod versions.
/// </summary>
public sealed record DataSetIdentity(IReadOnlyList<DataSetEntry> Files)
{
    /// <summary>Most names listed per group in <see cref="Difference"/>; the rest is counted.</summary>
    const int NamesShown = 5;

    /// <summary>First 8 bytes of SHA-256 over (name, hash, length) of every file in order: a one-number summary for logs and quick compares.</summary>
    public ulong Combined { get; } = Combine(Files);

    static ulong Combine(IReadOnlyList<DataSetEntry> files)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> number = stackalloc byte[8];
        foreach (var file in files)
        {
            sha.AppendData(Encoding.UTF8.GetBytes(file.Name));
            sha.AppendData([0]);
            BinaryPrimitives.WriteUInt64LittleEndian(number, file.Hash);
            sha.AppendData(number);
            BinaryPrimitives.WriteInt64LittleEndian(number, file.Length);
            sha.AppendData(number);
        }
        return BinaryPrimitives.ReadUInt64LittleEndian(sha.GetHashAndReset());
    }

    /// <summary>
    /// Hashes every file of <paramref name="order"/> (what the game loads: <see cref="LoadOrder.FromInstall"/>). Reads whole files, so call it once at
    /// start-up or when hosting, on the caller's thread. A file that cannot be read is listed with hash 0 and length -1, so it differs from any real one.
    /// </summary>
    public static DataSetIdentity Compute(LoadOrder order)
    {
        var files = new List<DataSetEntry>(order.Entries.Count);
        foreach (var entry in order.Entries)
        {
            try
            {
                using var stream = new FileStream(entry.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
                long length = stream.Length;
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[1 << 16];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0) sha.AppendData(buffer, 0, read);
                files.Add(new DataSetEntry(entry.Name, BinaryPrimitives.ReadUInt64LittleEndian(sha.GetHashAndReset()), length));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                files.Add(new DataSetEntry(entry.Name, 0, -1));
            }
        }
        return new DataSetIdentity(files);
    }

    /// <summary>
    /// Null when the two sets are equal (same files, hashes and order); otherwise a sentence for the player, such as
    /// <c>Missing: A.mod, B.mod; extra: C.mod; different version: gamedata.base (and 3 more).</c> (at most 5 names per group), or
    /// <c>Same mods in a different load order.</c> when only the order differs. "Missing" is what the host has and the client lacks.
    /// </summary>
    public static string? Difference(DataSetIdentity host, DataSetIdentity client)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(client);
        if (host.Files.SequenceEqual(client.Files)) return null;

        var theirs = new Dictionary<string, DataSetEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in client.Files) theirs.TryAdd(file.Name, file);
        var ours = new HashSet<string>(host.Files.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);

        var missing = new List<string>();
        var different = new List<string>();
        foreach (var file in host.Files)
        {
            if (!theirs.TryGetValue(file.Name, out var other)) missing.Add(file.Name);
            else if (other.Hash != file.Hash || other.Length != file.Length) different.Add(file.Name);
        }
        var extra = client.Files.Where(f => !ours.Contains(f.Name)).Select(f => f.Name).ToList();

        if (missing.Count == 0 && extra.Count == 0 && different.Count == 0) return "Same mods in a different load order.";

        var groups = new List<string>(3);
        if (missing.Count > 0) groups.Add("missing: " + Names(missing));
        if (extra.Count > 0) groups.Add("extra: " + Names(extra));
        if (different.Count > 0) groups.Add("different version: " + Names(different));
        string text = string.Join("; ", groups) + ".";
        return char.ToUpperInvariant(text[0]) + text[1..];
    }

    static string Names(List<string> names)
    {
        string shown = string.Join(", ", names.Take(NamesShown));
        return names.Count > NamesShown ? $"{shown} (and {names.Count - NamesShown} more)" : shown;
    }
}

/// <summary>Who this process is when it hosts or joins: a stable player id, the display name, the build and the loaded data set.</summary>
public sealed record NetIdentity(Guid PlayerId, string Name, string Build, DataSetIdentity Data);
