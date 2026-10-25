namespace Meitou.Localisation;

/// <summary>Finds exact, normalized or safely close source strings in a user translation map.</summary>
public static class TranslationMatcher
{
    public static string? Find(IReadOnlyDictionary<string, string> translations, string source)
    {
        if (translations.TryGetValue(source, out var exact)) return exact;

        string normalized = Normalize(source);
        foreach (var (key, value) in translations)
            if (Normalize(key) == normalized) return value;

        foreach (var (key, value) in translations)
            if (HasPreservableSuffix(source, key, out var suffix)) return value + suffix;

        int best = int.MaxValue;
        string? result = null;
        bool ambiguous = false;
        foreach (var (key, value) in translations)
        {
            string candidate = Normalize(key);
            int longest = Math.Max(normalized.Length, candidate.Length);
            int limit = Math.Min(4, Math.Max(1, (int)Math.Ceiling(longest * 0.08)));
            if (longest < 5 || Math.Abs(normalized.Length - candidate.Length) > limit
                || Digits(normalized) != Digits(candidate)) continue;
            int distance = Distance(normalized, candidate, limit);
            if (distance > limit) continue;
            if (distance < best) { best = distance; result = value; ambiguous = false; }
            else if (distance == best && result != value) ambiguous = true;
        }
        return ambiguous ? null : result;
    }

    static string Normalize(string text)
    {
        var result = new System.Text.StringBuilder(text.Length);
        bool space = true;
        foreach (char c in text.Trim())
        {
            if (char.IsWhiteSpace(c))
            {
                if (!space) result.Append(' ');
                space = true;
            }
            else
            {
                result.Append(char.ToLowerInvariant(c));
                space = false;
            }
        }
        return result.ToString().TrimEnd('.', ',', ';', ':', '!', '?');
    }

    static string Digits(string text) => string.Concat(text.Where(char.IsDigit));

    static bool HasPreservableSuffix(string source, string key, out string suffix)
    {
        suffix = "";
        if (!source.StartsWith(key, StringComparison.OrdinalIgnoreCase) || source.Length == key.Length) return false;
        string tail = source[key.Length..];
        if (tail.Length == 0 || !char.IsWhiteSpace(tail[0])) return false;
        string token = tail.Trim();
        bool marker = token is "v" or "V" or "▼" or "▾" or "…" or "..."
            || token.Length > 1 && (token[0] is 'v' or 'V') && char.IsDigit(token[1]) && token[2..].All(c => char.IsDigit(c) || c is '.' or '-' or '_')
            || token.Length > 0 && char.IsDigit(token[0]) && token.All(c => char.IsDigit(c) || c is '.' or '-' or '_');
        if (!marker) return false;
        suffix = tail;
        return true;
    }

    static int Distance(string a, string b, int limit)
    {
        if (a.Length > b.Length) (a, b) = (b, a);
        var previous = new int[a.Length + 1];
        var current = new int[a.Length + 1];
        for (int j = 0; j <= a.Length; j++) previous[j] = j;
        for (int i = 1; i <= b.Length; i++)
        {
            current[0] = i;
            int rowMin = current[0];
            for (int j = 1; j <= a.Length; j++)
            {
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[j - 1] == b[i - 1] ? 0 : 1));
                rowMin = Math.Min(rowMin, current[j]);
            }
            if (rowMin > limit) return limit + 1;
            (previous, current) = (current, previous);
        }
        return previous[a.Length];
    }
}
