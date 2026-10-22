using Meitou.Data.Ui;

namespace Meitou.Rendering.Ui;

/// <summary>Text measuring for layouts that must fit what <see cref="Draw"/> will paint (the tooltips).</summary>
public sealed partial class UiPainter
{
    /// <summary>The size of a measured text block.</summary>
    public readonly record struct TextSize(float Width, float Height);

    /// <summary>
    /// The font a TrueType font resource (<c>Kenshi_StandardFont_Medium</c> ...) is drawn with at <paramref name="scale"/> (the control's <c>TextScale</c>, else the viewport height over 1080),
    /// baked at the em pixel size the original resizes it to; null when <paramref name="fontName"/> is no such resource.
    /// </summary>
    DebugOverlay? ResourceFont(string fontName, float scale, float minimumTextPixels)
    {
        if (FontSpec(fontName) is not { } spec || FontPath(spec.Source) is not { } sourcePath) return null;
        float resolution = spec.Resolution > 0 ? spec.Resolution : 50;
        bool faithful = Mode == UiMode.Faithful;
        // Faithful: the original's size at this scale; Meitou: the 1080-line size scaled (its own look).
        float points = faithful ? ScaledFontSize(sourcePath, spec, scale) : EffectiveFontSize(sourcePath, spec.Size > 0 ? spec.Size : 20, resolution) * scale;
        float em = points * resolution / 72;
        long size64;
        uint dpi;
        if (faithful && em >= Math.Max(6, minimumTextPixels) && em <= 192)
        {
            // MyGUI's own request: fractional points at the resource's dpi (1/16 point steps, so a resized window makes a bounded number of fonts).
            size64 = Math.Max(1, (long)MathF.Round(points * 16)) * 4;
            dpi = (uint)MathF.Round(resolution);
        }
        else
        {
            // Whole pixels: Meitou's look, and the display policy's minimum size in either mode.
            size64 = Math.Clamp((int)MathF.Round(Math.Max(minimumTextPixels, em)), 6, 192) * 64L;
            dpi = 72;
        }
        return UiFontFor(fontName, spec, sourcePath, size64, dpi, faithful, faithful || !spec.Antialias);
    }

    /// <summary>
    /// The size <see cref="Draw"/> gives <paramref name="text"/> in font resource <paramref name="fontName"/> on a viewport <paramref name="viewportHeight"/> high: wrapped to
    /// <paramref name="wrapWidth"/> when that is above zero (a control with <c>WordWrap</c> true), else one line per line break. <paramref name="minimumTextPixels"/> is the value passed to <see cref="Draw"/>.
    /// </summary>
    /// <summary>The height of <paramref name="control"/>'s caption as <see cref="Draw"/> would paint it: its font, wrapped to its text area when it has <c>WordWrap</c>.</summary>
    public float TextHeight(UiControl control, int viewportHeight, float minimumTextPixels)
    {
        string fontName = control.Properties.GetValueOrDefault("FontName")
            ?? (resources.Templates.TryGetValue(control.Skin, out var template)
                ? template.Descendants("Property").FirstOrDefault(p => (string?)p.Attribute("key") == "FontName")?.Attribute("value")?.Value : null)
            ?? "Kenshi_StandardFont_Medium";
        float wrap = control.Properties.GetValueOrDefault("WordWrap") == "true" ? UiTextLayout.ContentRect(resources, control).Width : 0;
        return MeasureText(fontName, control.Caption, viewportHeight, wrap, minimumTextPixels).Height;
    }

    public TextSize MeasureText(string fontName, string text, int viewportHeight, float wrapWidth, float minimumTextPixels)
    {
        var font = ResourceFont(fontName, viewportHeight / 1080f, minimumTextPixels) ?? standardFont ?? overlay;
        float scale = font == standardFont || font == overlay ? Math.Clamp(viewportHeight / 1080f, 0.5f, 1.5f) : 1;
        text = text.Replace("\\n", "\n", StringComparison.Ordinal).ReplaceLineEndings("\n");
        var lines = wrapWidth > 0 ? font.WrapText(text, wrapWidth, scale) : text.Split('\n');
        float width = 0;
        foreach (string line in lines) width = Math.Max(width, font.MeasureText(line, scale));
        return new(width, Math.Max(1, lines.Count) * font.LineHeight * scale);
    }
}
