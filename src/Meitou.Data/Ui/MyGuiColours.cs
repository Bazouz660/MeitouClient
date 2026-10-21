using System.Globalization;
using System.Numerics;

namespace Meitou.Data.Ui;

public static class MyGuiColours
{
    static IReadOnlyDictionary<string, string> palette = new Dictionary<string, string>();

    /// <summary>
    /// The named colours of <c>gui/colours/kenshi_colours.xml</c> (Main, Secondary, Title, Bad, Good, Alert, ...) as "r g b" strings. A mod's file (Dark UI) replaces the game's,
    /// so a text colour the game takes from here must not be a constant. Set once when the UI loads; a name the file lacks gets the game's own value.
    /// </summary>
    public static void LoadPalette(string? path)
    {
        var read = new Dictionary<string, string>();
        if (path is not null && File.Exists(path))
            foreach (var property in MyGuiReader.ReadFile(path).Elements("Property"))
                if (property.Attribute("key")?.Value is { } key && property.Attribute("value")?.Value is { } value && Hex(value) is { } rgb) read[key] = rgb;
        palette = read;
    }

    /// <summary>The colour named <paramref name="key"/> in the palette, else <paramref name="gameHex"/> (the game's own value, as "rrggbb").</summary>
    public static string Named(string key, string gameHex) => palette.GetValueOrDefault(key) ?? Hex(gameHex)!;

    static string? Hex(string value)
    {
        value = value.TrimStart('#');
        if (value.Length != 6) return null;
        var n = new float[3];
        for (int i = 0; i < 3; i++)
        {
            if (!int.TryParse(value.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int b)) return null;
            n[i] = b / 255f;
        }
        return string.Join(' ', n.Select(f => f.ToString("0.###", CultureInfo.InvariantCulture)));
    }

    /// <summary>The palette's Secondary: the text default (dark ink on the game's parchment, grey in Dark UI).</summary>
    public static string InkText => Named("Secondary", "140806");
    public static Vector4 Ink => Parse(InkText, default);
    public static Vector4 Parse(string? value, Vector4 fallback)
    {
        if (value is null) return fallback;
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is not (3 or 4)) return fallback;
        var n = new float[parts.Length];
        for (int i = 0; i < parts.Length; i++) if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out n[i]) || !float.IsFinite(n[i])) return fallback;
        return new(n[0], n[1], n[2], parts.Length == 4 ? n[3] : 1);
    }
    public static Vector4 Text(MyGuiResources resources, UiControl control, string state, Vector4 fallback)
    {
        // A text-only button (no background to tint) shows its hover colour on the text: dialogue choices, research and crafting lists.
        if (state == "highlighted" && control.HoverColour is { } hover && control.Background.W <= 0) return hover;
        var colour = control.TextColour ?? fallback;
        if (control.TextColour is null && resources.Skins.TryGetValue(control.Skin, out var skin))
            foreach (var part in skin.Parts.Where(p => p.Type is "SimpleText" or "EditText"))
            {
                var properties = part.StateProperties?.GetValueOrDefault(state) ?? part.StateProperties?.GetValueOrDefault("normal");
                colour = Parse(properties?.GetValueOrDefault("colour"), colour);
            }
        return Parse(control.Properties.GetValueOrDefault("TextColour"), colour);
    }
}
