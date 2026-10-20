using System.Text;

namespace Meitou.Net.Replication;

/// <summary>Writer-side limits for snapshot text (docs/net.md, "Snapshot core"): readers refuse an over-limit string, so the host cuts it first.</summary>
internal static class WireText
{
    /// <summary>The text cut at a character boundary so its UTF-8 form is at most <paramref name="maxBytes"/>; null stays null.</summary>
    public static string? LimitOrNull(string? s, int maxBytes)
    {
        if (s is null) return null;
        if (s.Length == 0 || s.Length * 3 <= maxBytes) return s;   // a UTF-16 unit is at most 3 UTF-8 bytes
        int bytes = 0, chars = 0;
        foreach (var rune in s.EnumerateRunes())
        {
            bytes += rune.Utf8SequenceLength;
            if (bytes > maxBytes) return s[..chars];
            chars += rune.Utf16SequenceLength;
        }
        return s;
    }

    public static string Limit(string? s, int maxBytes) => LimitOrNull(s, maxBytes) ?? "";

    /// <summary>The float, or <paramref name="fallback"/> when it is NaN or infinite (a reader refuses those).</summary>
    public static float Finite(float v, float fallback = 0f) => float.IsFinite(v) ? v : fallback;

    /// <summary>A string the layout requires: a null on the wire is a format error.</summary>
    public static string ReadRequired(ref NetReader r, StringTableReader s, int maxBytes) =>
        s.Read(ref r, maxBytes) ?? throw new NetFormatException("A required string is null.");

    /// <summary>The sum of a position and a delta, checked against the position range.</summary>
    public static int AddPosition(int baseline, long delta)
    {
        long v = baseline + delta;
        if (v is < -(1 << 30) or > (1 << 30)) throw new NetFormatException("A position is outside the world range.");
        return (int)v;
    }
}
