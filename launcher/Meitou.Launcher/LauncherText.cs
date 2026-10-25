using System.Text.Json;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Meitou.Localisation;

namespace Meitou.Launcher;

/// <summary>Exact-string translations for the launcher's own interface, loaded from the editable JSON dictionary.</summary>
static class LauncherText
{
    const string ResourceName = "Meitou.Launcher.translations.json";
    sealed record LanguageCatalog(string Code, string Name, Dictionary<string, string> Translations);
    static readonly Dictionary<string, LanguageCatalog> catalogs = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<(string Language, string Source), string> cache = [];

    public static string DictionaryPath
    {
        get
        {
#if DEBUG
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                var source = Path.Combine(directory.FullName, "translations.json");
                if (File.Exists(Path.Combine(directory.FullName, "Meitou.Launcher.csproj")) && File.Exists(source)) return source;
            }
#endif
            return Path.Combine(LauncherConfig.DataDirectory, "launcher.translations.json");
        }
    }
    public static IReadOnlyList<(string Code, string Name)> Languages =>
        [("en", "English"), .. catalogs.Values.Where(language => !language.Code.Equals("en", StringComparison.OrdinalIgnoreCase))
            .OrderBy(language => language.Name, StringComparer.CurrentCultureIgnoreCase).Select(language => (language.Code, language.Name))];
    public static bool HasLanguage(string language) => language.Equals("en", StringComparison.OrdinalIgnoreCase) || catalogs.ContainsKey(Normalize(language));

    public static void Load()
    {
        catalogs.Clear();
        cache.Clear();
        string defaults = "{}";
        using (var source = typeof(LauncherText).Assembly.GetManifestResourceStream(ResourceName))
            if (source is not null)
                using (var reader = new StreamReader(source)) defaults = reader.ReadToEnd();

        LoadJson(defaults);
        if (!File.Exists(DictionaryPath))
        {
            Directory.CreateDirectory(LauncherConfig.DataDirectory);
            File.WriteAllText(DictionaryPath, defaults);
        }
        else
            try { LoadJson(File.ReadAllText(DictionaryPath)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    static void LoadJson(string text)
    {
        try
        {
            using var json = JsonDocument.Parse(text);
            foreach (var entry in json.RootElement.EnumerateObject())
                ReadCatalog(entry.Name, entry.Value);
        }
        catch (JsonException) { }
    }

    static void ReadCatalog(string name, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return;

        if (value.TryGetProperty("code", out var codeElement) && codeElement.ValueKind == JsonValueKind.String
            && codeElement.GetString() is { Length: > 0 } explicitCode)
        {
            string displayName = value.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                ? nameElement.GetString() ?? name : name;
            JsonElement translations = value.TryGetProperty("translations", out var translationElement)
                && translationElement.ValueKind == JsonValueKind.Object ? translationElement : default;
            AddCatalog(explicitCode, displayName, translations);
            return;
        }

        if (LooksLikeLanguageCode(name))
        {
            // Backward-compatible flat form: "pt-BR": { "Settings": "Configurações" }.
            AddCatalog(name, name.Equals("pt-BR", StringComparison.OrdinalIgnoreCase) ? "Português (Brasil)" : name, value);
            return;
        }

        bool foundNestedLanguage = false;
        foreach (var property in value.EnumerateObject())
        {
            if (LooksLikeLanguageCode(property.Name) && property.Value.ValueKind == JsonValueKind.Object)
            {
                // Display-name form: "Português-Brasil": { "pt-BR": { "Settings": "Configurações" } }.
                JsonElement translations = property.Value.TryGetProperty("translations", out var translationElement)
                    && translationElement.ValueKind == JsonValueKind.Object ? translationElement : property.Value;
                AddCatalog(property.Name, name, translations);
                foundNestedLanguage = true;
            }
        }

        // Tolerate a language entry nested under another display-name object, as can happen when editing the JSON by hand.
        if (foundNestedLanguage)
        {
            foreach (var property in value.EnumerateObject())
                if (!LooksLikeLanguageCode(property.Name)) ReadCatalog(property.Name, property.Value);
        }
    }

    static void AddCatalog(string code, string name, JsonElement translations)
    {
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        if (catalogs.TryGetValue(code, out var existing))
            foreach (var entry in existing.Translations) entries[entry.Key] = entry.Value;
        if (translations.ValueKind == JsonValueKind.Object)
            foreach (var pair in translations.EnumerateObject())
                if (!pair.Name.Equals("base", StringComparison.OrdinalIgnoreCase)
                    && pair.Value.ValueKind == JsonValueKind.String && pair.Value.GetString() is { } translated)
                    entries[pair.Name] = translated;
        catalogs[code] = new(code, name, entries);
    }

    static bool LooksLikeLanguageCode(string value)
    {
        var parts = value.Replace('_', '-').Split('-');
        return parts.Length > 0 && parts[0].Length is 2 or 3 && parts[0].All(char.IsAsciiLetter)
            && parts.Skip(1).All(part => part.Length is >= 2 and <= 8 && part.All(char.IsAsciiLetterOrDigit));
    }

    public static string Translate(string source, string language)
    {
        // Canonicalize translated labels too, so controls materialized after the first tree pass can switch back to English.
        var english = catalogs.Values.SelectMany(catalog => catalog.Translations)
            .FirstOrDefault(pair => pair.Value == source).Key ?? source;
        string code = Normalize(language);
        if (!catalogs.TryGetValue(code, out var catalog)) return english;
        if (cache.TryGetValue((code, english), out var cached)) return cached;
        string translated = TranslationMatcher.Find(catalog.Translations, english) ?? english;
        cache[(code, english)] = translated;
        return translated;
    }

    static string Normalize(string language) => language.Replace('_', '-');

    public static void TranslateTree(Control root, string language, IDictionary<Control, string> originals)
    {
        void Visit(Control control)
        {
            // ComboBox templates contain the selected item's presenter. Translating it can pin a cached value across selection changes.
            // These other subtrees contain game, mod, release or news text supplied as data, not launcher labels.
            if (control is ComboBox || control is ListBox || control is ContentControl { Name: "ReleaseNotes" } || control is StackPanel { Name: "NewsPanel" }) return;
            if (control is TextBlock text && text.Text is { } value)
            {
                if (!originals.ContainsKey(control)) originals[control] = value;
                text.Text = Translate(originals[control], language);
            }
            else if (control is Button button && button.Content is string content)
            {
                if (!originals.ContainsKey(control)) originals[control] = content;
                button.Content = Translate(originals[control], language);
            }
            foreach (var child in control.GetVisualChildren())
                if (child is Control nested) Visit(nested);
        }
        Visit(root);
    }
}
