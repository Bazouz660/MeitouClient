using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Data.Textures;
using Meitou.Data.Ui;
using Meitou.Rendering.Gpu;
using Silk.NET.Vulkan;

namespace Meitou.Rendering.Ui;

/// <summary>Native alpha-composited MyGUI atlas slices and styled native control frames.</summary>
public sealed class UiPainter : IDisposable
{
    readonly DebugOverlay overlay;
    readonly MyGuiResources resources;
    readonly Func<string, string?> resolve;
    DebugOverlay? standardFont, paintedFont, titleFont;
    Dictionary<int, DebugOverlay>? pixelFonts;
    readonly Dictionary<(string Source, int Pixels, bool Snapped), DebugOverlay> resourceFonts = [];
    readonly Dictionary<(string Path, float Size, float Resolution), float> fontSizes = [];

    /// <summary>The point size the original draws font resource <paramref name="path"/> at (<see cref="KenshiFontSize"/>).</summary>
    float EffectiveFontSize(string path, float size, float resolution)
    {
        if (!fontSizes.TryGetValue((path, size, resolution), out float effective))
            fontSizes[(path, size, resolution)] = effective = KenshiFontSize.Effective(File.ReadAllBytes(path), size, resolution);
        return effective;
    }
    readonly Dictionary<string, string?> fontPaths = new(StringComparer.Ordinal);

    string? FontPath(string name)
    {
        if (!fontPaths.TryGetValue(name, out var path)) fontPaths[name] = path = resolve(name);
        return path;
    }
    /// <summary>The standard and painted faces baked at <see cref="LargePixels"/>: lettering scaled well past the 16 px atlases is drawn from these, so it stays sharp.</summary>
    DebugOverlay? standardLarge, paintedLarge;
    const float LargePixels = 48;
    readonly Dictionary<string, (Texture Texture, SampledTexture Sampled)> textures = new(StringComparer.Ordinal);
    /// <summary>Textures the host draws at run time (the squad portraits), shown by an ImageBox's <c>ImageTexture</c> under their name; not owned.</summary>
    readonly Dictionary<string, (Texture Texture, SampledTexture Sampled)> external = new(StringComparer.Ordinal);
    /// <summary>Which texels of each uploaded texture are fully opaque, by name (<see cref="OpaqueCoverage"/>); textures given on the GPU have none and never count.</summary>
    readonly Dictionary<string, OpacityMask> masks = new(StringComparer.Ordinal);
    readonly List<UiRect> opaqueRects = new();
    readonly List<UiRect> previousOpaqueRects = new();
    double opaqueArea;
    DebugOverlay.OverlayPass? overlayPass;

    /// <summary>
    /// The share of the viewport (0..1) under fully opaque pieces of the last <see cref="Draw"/>: solid backgrounds and atlas slices whose
    /// texels are all alpha 255, drawn with alpha 1; text and translucent pieces do not count. Overlaps count once. The measure for how
    /// much scene shading a UI depth stamp could skip.
    /// </summary>
    public float OpaqueCoverage { get; private set; }

    /// <summary>Makes <paramref name="texture"/> the image an ImageBox shows for <c>ImageTexture</c> <paramref name="name"/> (the caller keeps and frees it).</summary>
    public void SetTexture(string name, Texture texture, SampledTexture sampled) => external[name] = (texture, sampled);

    /// <summary>
    /// Uploads <paramref name="image"/> (made by the host) as <c>ImageTexture</c> <paramref name="name"/>, between frames; the painter owns the copy. <paramref name="srgb"/>
    /// false stores the bytes as they are, as the game's atlas textures are (art baked from them then draws in their colours).
    /// </summary>
    public void SetImage(string name, RgbaImage image, bool srgb = true) => Upload(name, image, srgb);

    /// <summary>Whether <c>ImageTexture</c> <paramref name="name"/> is on the GPU.</summary>
    public bool HasTexture(string name) => textures.ContainsKey(name) || external.ContainsKey(name);

    public UiPainter(DebugOverlay overlay, MyGuiResources resources, Func<string, string?> resolve)
    {
        this.overlay = overlay;
        this.resources = resources;
        this.resolve = resolve;
        // Upload before rendering starts. Missing atlas files remain an explicit diagnostic.
        try
        {
            if (resolve("Exo2-SemiBold.ttf") is { } standard)
            {
                standardFont = DebugOverlay.FromFont(overlay.Gpu, standard);
                standardLarge = DebugOverlay.FromFont(overlay.Gpu, standard, LargePixels);
            }
            if (resolve("Sentencia_1.ttf") is { } painted)
            {
                paintedFont = DebugOverlay.FromFont(overlay.Gpu, painted);
                paintedLarge = DebugOverlay.FromFont(overlay.Gpu, painted, LargePixels);
                titleFont = DebugOverlay.FromFont(overlay.Gpu, painted, 144);
            }
            foreach (string name in resources.Skins.Values.Select(s => s.Texture).Where(s => s.Length > 0).Distinct())
            {
                LoadTexture(name);
            }
        }
        catch { Dispose(); throw; }
    }

    /// <summary>The width in pixels of <paramref name="text"/> in the standard face at <paramref name="emPixels"/> (to fit a panel to its text before it is drawn).</summary>
    public float MeasureStandard(string text, float emPixels) => (standardFont ?? overlay).MeasureText(text, emPixels / 16f);

    public void Draw(IReadOnlyList<UiControl> controls, int width, int height, Vector2 mouse, string? active, float minimumTextPixels = 0)
    {
        for (int uploads = 0; uploads < IconUploadsPerFrame && decodedIcons.TryDequeue(out var icon); uploads++) Upload(icon.Name, icon.Image);
        using var pass = (overlayPass ??= new DebugOverlay.OverlayPass(overlay.Gpu, width, height)).Reset(width, height);
        foreach (var c in controls)
        {
            var b = c.Bounds;
            string widgetState = c.Properties.GetValueOrDefault("State") ?? (!c.Enabled ? "disabled" : active == c.Action && active is not null ? "pushed" : b.Contains(mouse.X, mouse.Y) && c.Action.Length > 0 ? "highlighted" : "normal");
            var clip = (c.Clip ?? new(0, 0, width, height)).Intersect(new(0, 0, width, height));
            if (b.Intersect(clip).Width <= 0 || b.Intersect(clip).Height <= 0) continue;
            if (b.Width <= 0 || b.Height <= 0) continue;
            if (c.Background.W > 0)
            {
                var colour = c.Type == "Button" && c.Action == active ? c.ActiveColour ?? new Vector4(0.25f, 0.5f, 0.4f, c.Background.W)
                    : c.Type == "Button" && b.Contains(mouse.X, mouse.Y) ? c.HoverColour ?? c.Background : c.Background;
                if (!c.Enabled) colour = c.DisabledColour ?? colour * new Vector4(0.6f, 0.6f, 0.6f, 1);
                colour.W *= Alpha(c.Properties);
                overlay.Rect(b.X, b.Y, b.X + b.Width, b.Y + b.Height, colour);
                if (colour.W >= 1) opaqueRects.Add(b.Intersect(clip));
            }
            overlay.Flush(width, height, clip, pass);
            if (c.Type == "ImageBox" && MyGuiImages.Resolve(resources, c.Properties) is { } image && (!image.Texture.StartsWith(IconPrefix, StringComparison.Ordinal) || textures.ContainsKey(image.Texture)) && (!image.Texture.StartsWith(RenderedIconPrefix, StringComparison.Ordinal) || RenderedIconReady(image.Texture)))
            {
                var loaded = LoadTexture(image.Texture);
                var source = image.Source.Width > 0 && image.Source.Height > 0 ? image.Source : new UiRect(0, 0, loaded.Texture.Desc.Width, loaded.Texture.Desc.Height);
                // ImageFlipV: an image drawn by the renderer (bottom row first, as its targets keep it) shown the right way up.
                bool flip = c.Properties.GetValueOrDefault("ImageFlipV") == "true";
                float v0 = source.Y / loaded.Texture.Desc.Height, v1 = (source.Y + source.Height) / loaded.Texture.Desc.Height;
                if (flip) (v0, v1) = (v1, v0);
                var tint = Colour(c.Properties.GetValueOrDefault("Colour"), Vector4.One) * new Vector4(1, 1, 1, Alpha(c.Properties));
                float border = 0;
                bool bordered = !flip && float.TryParse(c.Properties.GetValueOrDefault("ImageBorder"), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out border) && border > 0;
                // ImageAngle: radians clockwise about the box's centre (MyGUI's RotatingSkin, the map's camera marker).
                if (float.TryParse(c.Properties.GetValueOrDefault("ImageAngle"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float angle) && angle != 0)
                {
                    overlay.ImageRotated(loaded.Sampled, new(b.X, b.Y, b.X + b.Width, b.Y + b.Height),
                        new(source.X / loaded.Texture.Desc.Width, v0, (source.X + source.Width) / loaded.Texture.Desc.Width, v1), tint, angle);
                }
                else if (bordered)
                {
                    float borderScale = float.TryParse(c.Properties.GetValueOrDefault("ImageBorderScale"), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float parsed) ? Math.Max(0, parsed) : 1;
                    float bx = Math.Min(border, source.Width / 2), by = Math.Min(border, source.Height / 2);
                    float dx = Math.Min(bx * borderScale, b.Width / 2), dy = Math.Min(by * borderScale, b.Height / 2);
                    Span<float> xs = [b.X, b.X + dx, b.X + b.Width - dx, b.X + b.Width];
                    Span<float> ys = [b.Y, b.Y + dy, b.Y + b.Height - dy, b.Y + b.Height];
                    Span<float> us = [source.X, source.X + bx, source.X + source.Width - bx, source.X + source.Width];
                    Span<float> vs = [source.Y, source.Y + by, source.Y + source.Height - by, source.Y + source.Height];
                    for (int row = 0; row < 3; row++)
                        for (int column = 0; column < 3; column++)
                            overlay.Image(loaded.Sampled, new(xs[column], ys[row], xs[column + 1], ys[row + 1]),
                                new(us[column] / loaded.Texture.Desc.Width, vs[row] / loaded.Texture.Desc.Height,
                                    us[column + 1] / loaded.Texture.Desc.Width, vs[row + 1] / loaded.Texture.Desc.Height), tint);
                }
                else
                    overlay.Image(loaded.Sampled, new(b.X, b.Y, b.X + b.Width, b.Y + b.Height),
                        new(source.X / loaded.Texture.Desc.Width, v0, (source.X + source.Width) / loaded.Texture.Desc.Width, v1), tint);
                if (angle == 0 && tint.W >= 1 && masks.TryGetValue(image.Texture, out var imageMask)) AddOpaque(imageMask, source, b, clip, allowCore: !flip && !bordered);
                overlay.Flush(width, height, clip, pass);
            }
            if (resources.Skins.TryGetValue(c.Skin, out var skin) && textures.TryGetValue(skin.Texture, out var atlas))
            {
                var atlasMask = masks.GetValueOrDefault(skin.Texture);
                // A toggled button (Checked) uses MyGUI's *_checked states, else looks pushed.
                bool on = c.Properties.GetValueOrDefault("Checked") == "true";
                string state = on ? widgetState + "_checked" : widgetState;
                foreach (var part in skin.Parts.Where(p => p.Type is "SubSkin" or "MainSkin" or "TileRect"))
                {
                    if (!part.States.TryGetValue(state, out var source) && !(on && part.States.TryGetValue("pushed", out source)) && !part.States.TryGetValue("normal", out source)) continue;
                    var dest = part.Offset.AlignTo(b, skin.Width, skin.Height, part.Align);
                    var tint = Colour(c.Properties.GetValueOrDefault("Colour"), Vector4.One) * new Vector4(1, 1, 1, Alpha(c.Properties));
                    if (part.Type == "TileRect")
                    {
                        var properties = part.StateProperties?.GetValueOrDefault(state) ?? part.StateProperties?.GetValueOrDefault("normal");
                        string? tileSize = c.Properties.GetValueOrDefault("TileSize") ?? properties?.GetValueOrDefault("TileSize");
                        var size = tileSize?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        float tileW = size?.Length == 2 ? float.Parse(size[0], System.Globalization.CultureInfo.InvariantCulture) : source.Width;
                        float tileH = size?.Length == 2 ? float.Parse(size[1], System.Globalization.CultureInfo.InvariantCulture) : source.Height;
                        if (properties?.GetValueOrDefault("TileH") != "true") tileW = dest.Width;
                        if (properties?.GetValueOrDefault("TileV") != "true") tileH = dest.Height;
                        if (tileW > 0 && tileH > 0 && tint.W >= 1 && atlasMask?.Covers(source) == true) opaqueRects.Add(dest.Intersect(clip));
                        if (tileW > 0 && tileH > 0)
                            for (float tileY = dest.Y; tileY < dest.Y + dest.Height; tileY += tileH)
                                for (float tileX = dest.X; tileX < dest.X + dest.Width; tileX += tileW)
                                {
                                    float pieceW = Math.Min(tileW, dest.X + dest.Width - tileX), pieceH = Math.Min(tileH, dest.Y + dest.Height - tileY);
                                    overlay.Image(atlas.Sampled, new(tileX, tileY, tileX + pieceW, tileY + pieceH),
                                        new(source.X / atlas.Texture.Desc.Width, source.Y / atlas.Texture.Desc.Height,
                                            (source.X + source.Width * pieceW / tileW) / atlas.Texture.Desc.Width, (source.Y + source.Height * pieceH / tileH) / atlas.Texture.Desc.Height), tint);
                                }
                        continue;
                    }
                    // Crop the image and UVs to the viewport together.
                    float x0 = Math.Max(0, dest.X), y0 = Math.Max(0, dest.Y), x1 = Math.Min(width, dest.X + dest.Width), y1 = Math.Min(height, dest.Y + dest.Height);
                    if (x1 <= x0 || y1 <= y0 || dest.Width <= 0 || dest.Height <= 0) continue;
                    float tw = atlas.Texture.Desc.Width, th = atlas.Texture.Desc.Height;
                    overlay.Image(atlas.Sampled, new(x0, y0, x1, y1), new(
                        (source.X + (x0 - dest.X) / dest.Width * source.Width) / tw,
                        (source.Y + (y0 - dest.Y) / dest.Height * source.Height) / th,
                        (source.X + (x1 - dest.X) / dest.Width * source.Width) / tw,
                        (source.Y + (y1 - dest.Y) / dest.Height * source.Height) / th), tint);
                    if (tint.W >= 1 && atlasMask is not null) AddOpaque(atlasMask, source, dest, clip, allowCore: true);
                }
                overlay.Flush(width, height, clip, pass);
            }
            if (c.Type == "Button" && c.Enabled && c.Action == active && c.ActiveColour is { } accent)
                overlay.Rect(b.X + 4, b.Y + b.Height - 4, b.X + b.Width - 4, b.Y + b.Height - 2, accent);
            overlay.Flush(width, height, clip, pass);
            bool focused = c.Properties.GetValueOrDefault("Focused") == "true";
            if (c.Caption.Length == 0 && !focused) continue;
            // MyGUI shows a button caption only through a text part of its skin: a button skin of image parts alone (the map's centre button, "O") shows none.
            if (c.Type == "Button" && resources.Skins.TryGetValue(c.Skin, out var captionSkin) && !resources.Templates.ContainsKey(c.Skin) && !captionSkin.Parts.Any(p => p.Type is "SimpleText" or "EditText")) continue;
            var textArea = UiTextLayout.ContentRect(resources, c);
            if (textArea.Width <= 0 || textArea.Height <= 0) continue;
            var textClip = clip.Intersect(textArea);
            string fontName = c.Properties.GetValueOrDefault("FontName")
                ?? (resources.Templates.TryGetValue(c.Skin, out var template)
                    ? template.Descendants("Property").FirstOrDefault(p => (string?)p.Attribute("key") == "FontName")?.Attribute("value")?.Value : null)
                ?? "Kenshi_StandardFont_Medium";
            var font = fontName == "Meitou_TitleFont" ? titleFont ?? paintedFont ?? overlay :
                fontName.Contains("Painted", StringComparison.Ordinal) || fontName.Contains("Banner", StringComparison.Ordinal) ? paintedFont ?? standardFont ?? overlay : standardFont ?? overlay;
            var textColour = MyGuiColours.Text(resources, c, widgetState, c.Enabled ? DebugOverlay.TextColour : new(0.45f, 0.43f, 0.38f, 1));
            textColour.W *= Alpha(c.Properties);
            font.Target = overlay.Target;
            float scale = c.TextScale ?? (c.Skin.Length > 0 ? Math.Clamp(height / 1080f, 0.5f, 1.5f) : 1);
            // Settings request a font baked at its physical pixel size rather than scaling the 16px atlas.
            if (int.TryParse(c.Properties.GetValueOrDefault("FontPixels"), out int pixels) && FontPath("Exo2-SemiBold.ttf") is { } fontPath)
            {
                pixels = Math.Clamp(Math.Max(pixels, (int)MathF.Round(minimumTextPixels)), 8, 96);
                var fonts = pixelFonts ??= [];
                int key = pixels + 1000; // Keep old cached atlases distinct during hot reload.
                if (!fonts.TryGetValue(key, out var exact)) fonts[key] = exact = DebugOverlay.FromUiFont(overlay.Gpu, fontPath, pixels);
                font = exact;
                font.Target = overlay.Target;
                scale = 1;
            }
            else if (resources.Resources.TryGetValue(fontName, out var fontResource)
                && (string?)fontResource.Attribute("type") == "ResourceTrueTypeFont")
            {
                string? Property(string key) => fontResource.Elements("Property").FirstOrDefault(p => (string?)p.Attribute("key") == key)?.Attribute("value")?.Value;
                float Number(string key, float fallback) => float.TryParse(Property(key), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float number) && float.IsFinite(number) && number > 0 ? number : fallback;
                if (Property("Source") is { } source && FontPath(source) is { } sourcePath)
                {
                    // TrueType resource sizes are points at the resource's DPI, resized as the original does on load. Bake at the resulting em pixel size.
                    float resolution = Number("Resolution", 50);
                    int resourcePixels = Math.Clamp((int)MathF.Round(Math.Max(minimumTextPixels,
                        EffectiveFontSize(sourcePath, Number("Size", 20), resolution) * resolution / 72 * (c.TextScale ?? height / 1080f))), 6, 192);
                    var key = (sourcePath, resourcePixels, Property("Antialias") == "false");
                    if (!resourceFonts.TryGetValue(key, out var exact)) resourceFonts[key] = exact = DebugOverlay.FromUiFont(overlay.Gpu, sourcePath, resourcePixels, key.Item3);
                    font = exact;
                    font.Target = overlay.Target;
                    scale = 1;
                }
            }
            if (minimumTextPixels > 0)
            {
                // Short captions keep the main UI's readable size. Centre the line box over
                // its inset client and clip to the widget instead of shrinking the lettering.
                float extra = Math.Max(0, font.LineHeight * scale - textArea.Height);
                textArea = textArea with { Y = textArea.Y - extra / 2, Height = textArea.Height + extra };
                textClip = clip.Intersect(new(textArea.X, b.Y, textArea.Width, b.Height));
            }
            else scale = Math.Min(scale, textArea.Height / font.LineHeight);
            if (scale > 1.25f && (font == standardFont ? standardLarge : font == paintedFont ? paintedLarge : null) is { } large)
            {
                scale *= font.LineHeight / large.LineHeight;
                font = large;
                font.Target = overlay.Target;
            }
            string text = c.Caption.Replace("\\n", "\n", StringComparison.Ordinal);
            var lines = c.Properties.GetValueOrDefault("WordWrap") == "true" ? font.WrapText(text, textArea.Width, scale) : text.ReplaceLineEndings("\n").Split('\n').Select(line => font.FitText(line, textArea.Width, scale)).ToArray();
            if (focused && c.Properties.GetValueOrDefault("Numeric") == "true") lines = [text];
            string align = c.Properties.GetValueOrDefault("TextAlign") ?? "Left VCenter";
            float y = textArea.Y + (align.Contains("Bottom", StringComparison.Ordinal) ? Math.Max(0, textArea.Height - font.LineHeight * scale * lines.Count) : align.Split(' ').Any(a => a is "Center" or "VCenter") ? Math.Max(0, (textArea.Height - font.LineHeight * scale * lines.Count) / 2) : 0);
            if (focused)
            {
                int Index(string key) => int.TryParse(c.Properties.GetValueOrDefault(key), out int at) ? Math.Clamp(at, 0, text.Length) : 0;
                int selection = Index("SelectionStart"), length = Index("SelectionLength"), caret = Index("CaretIndex");
                float textWidth = font.MeasureText(text, scale);
                float origin = textArea.X + (align.Contains("Right", StringComparison.Ordinal) ? textArea.Width - textWidth : align.Split(' ').Any(a => a is "Center" or "HCenter") ? (textArea.Width - textWidth) / 2 : 0);
                if (length > 0)
                {
                    float start = origin + font.MeasureText(text[..selection], scale);
                    float end = origin + font.MeasureText(text[..Math.Min(selection + length, text.Length)], scale);
                    overlay.Rect(start, y, end, y + font.LineHeight * scale, new(0.25f, 0.4f, 0.45f, 0.8f));
                }
                if ((System.Diagnostics.Stopwatch.GetTimestamp() / (System.Diagnostics.Stopwatch.Frequency / 2)) % 2 == 0)
                {
                    float caretX = origin + font.MeasureText(text[..caret], scale);
                    overlay.Rect(caretX, y, caretX + 1, y + font.LineHeight * scale, textColour);
                }
                overlay.Flush(width, height, textClip, pass);
            }
            foreach (string line in lines)
            {
                float textWidth = font.MeasureText(line, scale);
                float x = textArea.X + (align.Contains("Right", StringComparison.Ordinal) ? textArea.Width - textWidth : align.Split(' ').Any(a => a is "Center" or "HCenter") ? (textArea.Width - textWidth) / 2 : 0);
                if (new Meitou.Data.Ui.UiRect(x, y, textWidth, font.LineHeight * scale).Intersect(textClip).Width > 0)
                {
                    if (c.Properties.GetValueOrDefault("TextShadow") == "true") font.TextScaled(line, x + 1, y + 1, new(0, 0, 0, textColour.W), scale);
                    font.TextScaled(line, x, y, textColour, scale);
                }
                y += font.LineHeight * scale;
            }
            font.Flush(width, height, textClip, pass);
        }
        overlay.Flush(width, height, null, pass);
        if (!CollectionsMarshal.AsSpan(opaqueRects).SequenceEqual(CollectionsMarshal.AsSpan(previousOpaqueRects)))
        {
            previousOpaqueRects.Clear();
            previousOpaqueRects.AddRange(opaqueRects);
            opaqueArea = UnionArea(opaqueRects);
        }
        OpaqueCoverage = width > 0 && height > 0 ? (float)(opaqueArea / ((double)width * height)) : 0;
        opaqueRects.Clear();
    }

    /// <summary>Records the part of <paramref name="dest"/> that is opaque: all of it when every texel of <paramref name="source"/> is, else (when <paramref name="allowCore"/>) the stretched image of the slice's largest opaque core, so ragged panel edges do not disqualify the solid middle.</summary>
    void AddOpaque(OpacityMask mask, UiRect source, UiRect dest, UiRect clip, bool allowCore)
    {
        if (mask.Covers(source)) { opaqueRects.Add(dest.Intersect(clip)); return; }
        if (!allowCore || source.Width <= 0 || source.Height <= 0 || mask.Core(source) is not { } core) return;
        float fx = dest.Width / source.Width, fy = dest.Height / source.Height;
        opaqueRects.Add(new UiRect(dest.X + (core.X - source.X) * fx, dest.Y + (core.Y - source.Y) * fy, core.Width * fx, core.Height * fy).Intersect(clip));
    }

    /// <summary>The area of the union of <paramref name="rects"/> (overlaps once): the cells of the grid their edges make, each in or out.</summary>
    internal static double UnionArea(List<UiRect> rects)
    {
        rects.RemoveAll(r => r.Width <= 0 || r.Height <= 0);
        if (rects.Count == 0) return 0;
        var xs = rects.SelectMany(r => new[] { r.X, r.X + r.Width }).Distinct().Order().ToArray();
        var ys = rects.SelectMany(r => new[] { r.Y, r.Y + r.Height }).Distinct().Order().ToArray();
        var covered = new bool[(xs.Length - 1) * (ys.Length - 1)];
        foreach (var r in rects)
        {
            int x0 = Array.BinarySearch(xs, r.X), x1 = Array.BinarySearch(xs, r.X + r.Width), y0 = Array.BinarySearch(ys, r.Y), y1 = Array.BinarySearch(ys, r.Y + r.Height);
            for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) covered[y * (xs.Length - 1) + x] = true;
        }
        double area = 0;
        for (int y = 0; y + 1 < ys.Length; y++) for (int x = 0; x + 1 < xs.Length; x++)
            if (covered[y * (xs.Length - 1) + x]) area += (double)(xs[x + 1] - xs[x]) * (ys[y + 1] - ys[y]);
        return area;
    }

    /// <summary>One bit per texel of an uploaded texture: alpha 255. Answers whether a source rectangle (texels) is opaque throughout, remembering each rectangle asked.</summary>
    sealed class OpacityMask
    {
        readonly ulong[] bits;
        readonly int width, height;
        readonly Dictionary<(int X0, int Y0, int X1, int Y1), bool> answers = new();
        readonly Dictionary<(int X0, int Y0, int X1, int Y1), UiRect?> cores = new();

        public OpacityMask(RgbaImage image)
        {
            (width, height) = (image.Width, image.Height);
            bits = new ulong[(width * height + 63) / 64];
            var pixels = image.Pixels;
            for (int i = 0, n = width * height; i < n; i++)
                if (pixels[i * 4 + 3] == 255) bits[i >> 6] |= 1UL << (i & 63);
        }

        public bool Covers(UiRect source)
        {
            (int X0, int Y0, int X1, int Y1) key = (Math.Max(0, (int)MathF.Floor(source.X)), Math.Max(0, (int)MathF.Floor(source.Y)), Math.Min(width, (int)MathF.Ceiling(source.X + source.Width)), Math.Min(height, (int)MathF.Ceiling(source.Y + source.Height)));
            if (answers.TryGetValue(key, out bool known)) return known;
            bool opaque = key.X1 > key.X0 && key.Y1 > key.Y0;
            for (int y = key.Y0; opaque && y < key.Y1; y++)
                for (int x = key.X0; x < key.X1; x++)
                {
                    int i = y * width + x;
                    if ((bits[i >> 6] & 1UL << (i & 63)) == 0) { opaque = false; break; }
                }
            return answers[key] = opaque;
        }

        /// <summary>The largest rectangle of opaque texels inside <paramref name="source"/>, inset one texel so bilinear taps never reach a translucent neighbour; null when none is left.</summary>
        public UiRect? Core(UiRect source)
        {
            (int X0, int Y0, int X1, int Y1) key = (Math.Max(0, (int)MathF.Floor(source.X)), Math.Max(0, (int)MathF.Floor(source.Y)), Math.Min(width, (int)MathF.Ceiling(source.X + source.Width)), Math.Min(height, (int)MathF.Ceiling(source.Y + source.Height)));
            if (cores.TryGetValue(key, out var known)) return known;
            int w = key.X1 - key.X0, h = key.Y1 - key.Y0;
            UiRect? best = null;
            if (w > 2 && h > 2)
            {
                var heights = new int[w];
                var stack = new Stack<int>();
                long bestArea = 0;
                for (int row = 0; row < h; row++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int i = (key.Y0 + row) * width + key.X0 + x;
                        heights[x] = (bits[i >> 6] & 1UL << (i & 63)) != 0 ? heights[x] + 1 : 0;
                    }
                    stack.Clear();
                    for (int x = 0; x <= w; x++)
                    {
                        int at = x == w ? 0 : heights[x];
                        while (stack.Count > 0 && heights[stack.Peek()] >= at)
                        {
                            int top = heights[stack.Pop()], left = stack.Count > 0 ? stack.Peek() + 1 : 0;
                            long area = (long)top * (x - left);
                            if (area > bestArea) { bestArea = area; best = new UiRect(key.X0 + left, key.Y0 + row - top + 1, x - left, top); }
                        }
                        stack.Push(x);
                    }
                }
                if (best is { } found) best = found.Width > 2 && found.Height > 2 ? new UiRect(found.X + 1, found.Y + 1, found.Width - 2, found.Height - 2) : null;
            }
            return cores[key] = best;
        }
    }

    static float Alpha(IReadOnlyDictionary<string, string> properties) => float.TryParse(properties.GetValueOrDefault("Alpha"), System.Globalization.NumberStyles.Float,
        System.Globalization.CultureInfo.InvariantCulture, out float value) ? Math.Clamp(value, 0, 1) : 1;
    static Vector4 Colour(string? value, Vector4 fallback)
    {
        if (value is null) return fallback;
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is not (3 or 4)) return fallback;
        var numbers = new float[parts.Length];
        for (int i = 0; i < parts.Length; i++) if (!float.TryParse(parts[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out numbers[i])) return fallback;
        return new(numbers[0], numbers[1], numbers[2], parts.Length == 4 ? numbers[3] : 1);
    }

    /// <summary>
    /// Whether the image file <paramref name="path"/> (an item icon) is on the GPU and can be shown as an ImageBox's <c>ImageTexture</c> of <see cref="IconTexture"/>.
    /// The first ask starts decoding it on a worker, <see cref="Draw"/> uploads it when done; a file that cannot be read stays false.
    /// </summary>
    public bool IconReady(string path)
    {
        string name = IconTexture(path);
        if (textures.ContainsKey(name)) return true;
        if (requestedIcons.Add(name))
            Task.Run(() =>
            {
                try { decodedIcons.Enqueue((name, TextureLoader.LoadFile(path, allMips: false).Levels[0])); }
                catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException) { failedIcons.TryAdd(name, true); }
            });
        return false;
    }

    /// <summary>Icon files asked for (<see cref="IconReady"/>) that are still decoding or waiting for their upload.</summary>
    public int PendingIconFiles => requestedIcons.Count(n => !textures.ContainsKey(n) && !failedIcons.ContainsKey(n));
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> failedIcons = new(StringComparer.Ordinal);

    /// <summary>The <c>ImageTexture</c> name of the icon the renderer draws from an item's mesh (<see cref="RenderedIcons"/>), shown once it exists; flip it (<c>ImageFlipV</c>), as the renderer's targets are upside down.</summary>
    public static string RenderedIconTexture(string record, string material) => RenderedIconPrefix + record + "|" + material;
    public const string RenderedIconPrefix = "meitou:icon:";

    /// <summary>The record and material of a <see cref="RenderedIconTexture"/> name.</summary>
    public static bool TryParseRenderedIcon(string name, out string record, out string material)
    {
        (record, material) = ("", "");
        if (!name.StartsWith(RenderedIconPrefix, StringComparison.Ordinal) || name.IndexOf('|') is not (>= 0 and var bar)) return false;
        (record, material) = (name[RenderedIconPrefix.Length..bar], name[(bar + 1)..]);
        return record.Length > 0;
    }

    /// <summary>Looks up the texture of a <see cref="RenderedIconTexture"/> name, asking for it to be made when it is not (the host sets this; null: no icons are drawn).</summary>
    public Func<string, (Texture Texture, SampledTexture Sampled)?>? RenderedIcons { get; set; }

    /// <summary>Whether the rendered icon <paramref name="name"/> can be drawn now (this asks for it too).</summary>
    public bool RenderedIconReady(string name) => RenderedIcons?.Invoke(name) is not null;

    /// <summary>The <c>ImageTexture</c> name under which the image file <paramref name="path"/> is shown once <see cref="IconReady"/>.</summary>
    public static string IconTexture(string path) => IconPrefix + path;
    const string IconPrefix = "file:";
    const int IconUploadsPerFrame = 4;
    readonly HashSet<string> requestedIcons = new(StringComparer.Ordinal);
    readonly System.Collections.Concurrent.ConcurrentQueue<(string Name, RgbaImage Image)> decodedIcons = new();

    readonly Dictionary<string, MyGuiPointer?> pointers = new(StringComparer.Ordinal);

    /// <summary>
    /// Draws the mouse pointer <paramref name="name"/> (kenshi_pointers.xml) with its hot spot on <paramref name="mouse"/>, in screen pixels as MyGUI does; nothing for an unknown name.
    /// Called last in a frame: the original's <c>Pointer</c> layer is above every other.
    /// </summary>
    public void DrawPointer(string name, Vector2 mouse, int width, int height)
    {
        if (!pointers.TryGetValue(name, out var pointer)) pointers[name] = pointer = MyGuiPointers.Resolve(resources, name);
        if (pointer is null) return;
        overlay.Flush(width, height);   // whatever was queued before (another texture, text) goes first
        var loaded = LoadTexture(pointer.Image.Texture);
        var at = pointer.At(MathF.Round(mouse.X), MathF.Round(mouse.Y));
        var source = pointer.Image.Source;
        float w = loaded.Texture.Desc.Width, h = loaded.Texture.Desc.Height;
        overlay.Image(loaded.Sampled, new(at.X, at.Y, at.X + at.Width, at.Y + at.Height),
            new(source.X / w, source.Y / h, (source.X + source.Width) / w, (source.Y + source.Height) / h), Vector4.One);
        overlay.Flush(width, height);
    }

    (Texture Texture, SampledTexture Sampled) LoadTexture(string name)
    {
        if (external.TryGetValue(name, out var given) || textures.TryGetValue(name, out given)) return given;
        if (name.StartsWith(RenderedIconPrefix, StringComparison.Ordinal) && RenderedIcons?.Invoke(name) is { } rendered) return rendered;
        string path = resolve(name) ?? throw new FileNotFoundException($"MyGUI texture not found: {name}");
        return Upload(name, TextureLoader.LoadFile(path, allMips: false).Levels[0]);
    }

    (Texture Texture, SampledTexture Sampled) Upload(string name, RgbaImage image, bool srgb = false)
    {
        var gpu = overlay.Gpu;
        using var upload = gpu.Uploads.Begin();
        var texture = upload.Create(new TextureDesc(srgb ? Format.R8G8B8A8Srgb : Format.R8G8B8A8Unorm, image.Width, image.Height, Name: $"UI {name}"));
        var sampled = new SampledTexture(gpu.Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.Linear, TextureMagFilter.Linear,
            TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge, false, DepthFunction.Lequal, false, 1, false, 0)), texture.View(), texture.Image);
        upload.Write(texture, 0, 0, new PixelRect(0, 0, (uint)image.Width, (uint)image.Height), image.Pixels);
        masks[name] = new OpacityMask(image);
        return textures[name] = (texture, sampled);
    }

    public void Dispose()
    {
        foreach (var t in textures.Values) t.Texture.Dispose();
        textures.Clear();
        masks.Clear();
        if (pixelFonts is { } fonts) { foreach (var font in fonts.Values) font.Dispose(); fonts.Clear(); }
        foreach (var font in resourceFonts.Values) font.Dispose();
        resourceFonts.Clear();
        standardFont?.Dispose(); paintedFont?.Dispose(); titleFont?.Dispose(); standardLarge?.Dispose(); paintedLarge?.Dispose();
        standardFont = null; paintedFont = null; titleFont = null; standardLarge = null; paintedLarge = null;
    }
}
