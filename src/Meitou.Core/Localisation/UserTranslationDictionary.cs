using System.Text.Json;

namespace Meitou.Localisation;

/// <summary>The shared exact-string JSON dictionary used as the title menu's fallback after the game's gettext catalog.</summary>
public static class UserTranslationDictionary
{
    const string ResourceName = "Meitou.Core.launcher-translations.json";
    sealed record LanguageCatalog(string Code, string Name, string? BaseLanguage, Dictionary<string, string> Messages);
    static readonly Dictionary<string, LanguageCatalog> catalogs = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<(string Language, string Source), string> cache = [];

    /// <summary>Reloads the launcher dictionary from the shared Meitou data folder, or the source file in a Debug checkout.</summary>
    public static void Load()
    {
        catalogs.Clear();
        cache.Clear();
        var path = DictionaryPath();
        string defaults = "{}";
        using (var source = typeof(UserTranslationDictionary).Assembly.GetManifestResourceStream(ResourceName))
            if (source is not null)
                using (var reader = new StreamReader(source)) defaults = reader.ReadToEnd();

        LoadJson(defaults);
        if (!File.Exists(path))
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, defaults);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
        }
        else
            try { LoadJson(File.ReadAllText(path)); }
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
        string? baseLanguage = value.TryGetProperty("base", out var baseElement) && baseElement.ValueKind == JsonValueKind.String
            ? baseElement.GetString() : null;

        if (value.TryGetProperty("code", out var codeElement) && codeElement.ValueKind == JsonValueKind.String
            && codeElement.GetString() is { Length: > 0 } explicitCode)
        {
            string displayName = value.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                ? nameElement.GetString() ?? name : name;
            JsonElement explicitTranslations = value.TryGetProperty("translations", out var translationsElement)
                && translationsElement.ValueKind == JsonValueKind.Object ? translationsElement : default;
            AddCatalog(explicitCode, displayName, baseLanguage, explicitTranslations);
            return;
        }

        if (LooksLikeLanguageCode(name))
        {
            AddCatalog(name, name.Equals("pt-BR", StringComparison.OrdinalIgnoreCase) ? "Português (Brasil)" : name, baseLanguage, value);
            return;
        }

        bool foundNestedLanguage = false;
        foreach (var property in value.EnumerateObject())
        {
            if (!LooksLikeLanguageCode(property.Name) || property.Value.ValueKind != JsonValueKind.Object) continue;
            JsonElement translations = property.Value.TryGetProperty("translations", out var nestedTranslations)
                && nestedTranslations.ValueKind == JsonValueKind.Object ? nestedTranslations : property.Value;
            AddCatalog(property.Name, name, baseLanguage, translations);
            foundNestedLanguage = true;
        }
        if (foundNestedLanguage)
            foreach (var property in value.EnumerateObject())
                if (!LooksLikeLanguageCode(property.Name)) ReadCatalog(property.Name, property.Value);
    }

    static void AddCatalog(string code, string name, string? baseLanguage, JsonElement translations)
    {
        var messages = new Dictionary<string, string>(StringComparer.Ordinal);
        if (catalogs.TryGetValue(Normalize(code), out var existing))
            foreach (var message in existing.Messages) messages[message.Key] = message.Value;
        if (translations.ValueKind == JsonValueKind.Object)
            foreach (var item in translations.EnumerateObject())
                if (!item.Name.Equals("base", StringComparison.OrdinalIgnoreCase)
                    && item.Value.ValueKind == JsonValueKind.String && item.Value.GetString() is { } value)
                    messages[item.Name] = value;
        catalogs[Normalize(code)] = new(code, name, baseLanguage ?? existing?.BaseLanguage, messages);
    }

    /// <summary>Whether the JSON dictionary defines this locale, including Meitou-only custom locale codes.</summary>
    public static bool HasLanguage(string language)
    {
        if (catalogs.Count == 0) Load();
        return catalogs.ContainsKey(Normalize(language));
    }

    /// <summary>Returns the exact JSON translation for <paramref name="english"/>, or the input when no entry exists.</summary>
    public static string Translate(string english, string language)
    {
        if (catalogs.Count == 0) Load();
        string code = Normalize(language);
        if (!catalogs.TryGetValue(code, out var catalog)) return english;
        if (cache.TryGetValue((code, english), out var cached)) return cached;
        string result = FindTranslation(catalog, english, []) ?? english;
        cache[(code, english)] = result;
        return result;
    }

    static string? FindTranslation(LanguageCatalog catalog, string english, HashSet<string> visited)
    {
        string code = Normalize(catalog.Code);
        if (!visited.Add(code)) return null;
        if (TranslationMatcher.Find(catalog.Messages, english) is { } translated) return translated;
        if (catalog.BaseLanguage is not { Length: > 0 } baseLanguage) return null;
        var baseCatalog = catalogs.Values.FirstOrDefault(candidate =>
            Normalize(candidate.Code).Equals(Normalize(baseLanguage), StringComparison.OrdinalIgnoreCase)
            || candidate.Name.Equals(baseLanguage, StringComparison.OrdinalIgnoreCase));
        return baseCatalog is null ? null : FindTranslation(baseCatalog, english, visited);
    }

    static string DictionaryPath()
    {
#if DEBUG
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var besideProject = Path.Combine(directory.FullName, "translations.json");
            if (File.Exists(Path.Combine(directory.FullName, "Meitou.Launcher.csproj")) && File.Exists(besideProject)) return besideProject;
            var launcherSource = Path.Combine(directory.FullName, "launcher", "Meitou.Launcher", "translations.json");
            if (File.Exists(launcherSource)) return launcherSource;
        }
#endif
        var data = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create);
        return Path.Combine(data, "Meitou", "launcher.translations.json");
    }

    static bool LooksLikeLanguageCode(string value)
    {
        var parts = value.Replace('_', '-').Split('-');
        return parts[0].Length is 2 or 3 && parts[0].All(char.IsAsciiLetter)
            && parts.Skip(1).All(part => part.Length is >= 2 and <= 8 && part.All(char.IsAsciiLetterOrDigit));
    }

    static string Normalize(string code) => code.Replace('_', '-');
}
