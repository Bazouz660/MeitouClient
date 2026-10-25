using System.Text;
using Meitou.Content;
using Meitou.Localisation;

namespace Meitou.Launcher;

/// <summary>Updates only the game's language choice in its settings.cfg.</summary>
internal static class KenshiLanguageSettings
{
    static readonly Dictionary<string, string> KenshiNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["English"] = "en_GB",
        ["German"] = "de_DE",
        ["French"] = "fr_FR",
        ["Russian"] = "ru_RU",
        ["Italian"] = "it_IT",
        ["Portuguese"] = "pt_BR",
        ["Portuguese (Brazil)"] = "pt_BR",
        ["Português (Brasil)"] = "pt_BR",
        ["Spanish"] = "es_ES",
        ["Chinese"] = "zh_CN",
        ["Japanese"] = "ja_JP",
        ["Korean"] = "ko_KR",
    };

    public static string? ResolveCode(GameInstall install, string launcherCode, string? languageName = null)
    {
        if (languageName is not null && KenshiNames.TryGetValue(languageName.Trim(), out string? namedCode))
            return namedCode;

        if (launcherCode.Equals("en", StringComparison.OrdinalIgnoreCase)
            || launcherCode.Replace('-', '_').Equals(GameLanguage.DefaultCode, StringComparison.OrdinalIgnoreCase))
            return GameLanguage.DefaultCode;

        string normalized = launcherCode.Replace('-', '_');
        string? tableCode = GameLanguage.SteamLanguages
            .Select(language => language.Code)
            .FirstOrDefault(code => code.Equals(normalized, StringComparison.OrdinalIgnoreCase));
        if (tableCode is not null) return tableCode;

        try
        {
            string localeRoot = Path.Combine(install.Root, "locale");
            string? installedCode = Directory.Exists(localeRoot)
                ? Directory.EnumerateDirectories(localeRoot)
                    .Select(Path.GetFileName)
                    .FirstOrDefault(code => code is not null && !code.StartsWith('_')
                        && code.Equals(normalized, StringComparison.OrdinalIgnoreCase))
                : null;
            return installedCode ?? (LauncherText.HasLanguage(launcherCode) ? launcherCode : null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void SetLanguage(GameInstall install, string code)
    {
        string path = Path.Combine(install.Root, "settings.cfg");
        byte[] original = File.Exists(path) ? File.ReadAllBytes(path) : [];
        (Encoding encoding, int preambleLength) = DetectEncoding(original);
        string text = encoding.GetString(original, preambleLength, original.Length - preambleLength);
        string updated = ReplaceLanguage(text, code);
        byte[] body = encoding.GetBytes(updated);
        byte[] result = new byte[preambleLength + body.Length];
        original.AsSpan(0, preambleLength).CopyTo(result);
        body.CopyTo(result, preambleLength);
        File.WriteAllBytes(path, result);
    }

    static string ReplaceLanguage(string text, string code)
    {
        string newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n"
            : text.Contains('\n') ? "\n" : text.Contains('\r') ? "\r" : Environment.NewLine;

        for (int lineStart = 0; lineStart < text.Length;)
        {
            int contentEnd = lineStart;
            while (contentEnd < text.Length && text[contentEnd] is not ('\r' or '\n')) contentEnd++;
            int equals = text.IndexOf('=', lineStart, contentEnd - lineStart);
            if (equals > lineStart && text[lineStart..equals].Trim().Equals("language", StringComparison.Ordinal))
            {
                int valueStart = equals + 1;
                while (valueStart < contentEnd && char.IsWhiteSpace(text[valueStart])) valueStart++;
                return string.Concat(text.AsSpan(0, valueStart), code, text.AsSpan(contentEnd));
            }

            if (contentEnd == text.Length) break;
            lineStart = contentEnd + (text[contentEnd] == '\r' && contentEnd + 1 < text.Length && text[contentEnd + 1] == '\n' ? 2 : 1);
        }

        string separator = text.Length == 0 || text.EndsWith('\n') || text.EndsWith('\r') ? "" : newline;
        return string.Concat(text, separator, "language=", code, newline);
    }

    static (Encoding Encoding, int PreambleLength) DetectEncoding(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(new byte[] { 0x00, 0x00, 0xFE, 0xFF })) return (new UTF32Encoding(true, false), 4);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE, 0x00, 0x00 })) return (new UTF32Encoding(false, false), 4);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) return (new UTF8Encoding(false), 3);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })) return (Encoding.BigEndianUnicode, 2);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })) return (Encoding.Unicode, 2);
        return (new UTF8Encoding(false), 0);
    }
}
