using InternalFormat = Meitou.Rendering.Gpu.InternalFormat;
using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using StbTrueTypeSharp;

using Meitou.Rendering.Gpu;

namespace Meitou.Rendering;

/// <summary>
/// Text panels drawn over the frame (the viewer's frame statistics and key list). Text is rasterised from a monospace
/// system font with stb_truetype; without one the overlay stays off. Draws into <see cref="Target"/> after
/// everything else, so screenshots taken before it don't include it. Native (docs/renderer-native.md 7.5, 8.9): one <see cref="LegacyProgram"/>
/// draw in a segment and rendering of its own, the vertices in the frame's constants, the font a native R8 texture.
/// </summary>
public sealed unsafe class DebugOverlay : IDisposable
{
    /// <summary>The baked characters: printable ASCII and Latin-1 up to the middle dot (U+00B7), which the Meitou UI joins details with.</summary>
    const int FirstChar = 32, CharCount = 0xB8 - FirstChar;
    // The size the glyphs are baked at (16 px; a display face is baked larger so it stays sharp drawn big) and the atlas side that holds them.
    readonly float PixelHeight = 16;
    readonly int AtlasSize = 512;

    static readonly string[] FontCandidates =
    [
        @"C:\Windows\Fonts\consola.ttf", @"C:\Windows\Fonts\CascadiaMono.ttf", @"C:\Windows\Fonts\cour.ttf",
        "/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf", "/usr/share/fonts/TTF/DejaVuSansMono.ttf",
        // stb_truetype cannot bake from .ttc collections without an index, so macOS needs a plain .ttf (Menlo is a .ttc here).
        "/System/Library/Fonts/Supplemental/Courier New.ttf", "/System/Library/Fonts/SFNS.ttf",
        "/System/Library/Fonts/Menlo.ttc",
    ];

    // Vertex: position (pixels), uv (u < 0: solid), colour.
    const string Vertex = """
        #version 330 core
        layout(location = 0) in vec2 aPos;
        layout(location = 1) in vec2 aUv;
        layout(location = 2) in vec4 aColour;
        uniform vec2 uScreen;
        out vec2 vUv;
        out vec4 vColour;
        void main()
        {
            vUv = aUv;
            vColour = aColour;
            gl_Position = vec4(aPos / uScreen * vec2(2.0, -2.0) + vec2(-1.0, 1.0), 0.0, 1.0);
        }
        """;

    const string Fragment = """
        #version 330 core
        in vec2 vUv;
        in vec4 vColour;
        out vec4 fragColour;
        uniform sampler2D uAtlas;
        uniform int uImage;
        void main()
        {
            vec4 texel = vUv.x < 0.0 ? vec4(1.0) : texture(uAtlas, vUv);
            fragColour = uImage != 0 ? vColour * texel : vec4(vColour.rgb, vColour.a * texel.r);
        }
        """;

    /// <summary>The native GPU API (docs/renderer-native.md 7.1 step 8).</summary>
    public IGpuContext Gpu { get; }
    readonly GpuTexture atlas;
    readonly SampledTexture atlasSampled;
    readonly LegacyProgram program;
    readonly UniformHandle uScreen;
    readonly UniformHandle uImage;
    SampledTexture? imageBatch;
    readonly SamplerSlot samplerAtlas;
    readonly StbTrueType.stbtt_bakedchar[] glyphs = new StbTrueType.stbtt_bakedchar[CharCount];
    readonly StbTrueType.stbtt_packedchar[]? packedGlyphs;
    readonly float[]? kerning;
    readonly float baselineOffset;
    readonly List<float> batch = [];
    const int Stride = 8;
    readonly Dictionary<(string Text, float X, float Top, float Scale, Vector4 Colour), (float[] Vertices, float Width)> textGeometry = [];
    int cachedTextFloats;

    public float LineHeight { get; }
    public float CharWidth { get; }

    public bool Visible { get; set; }

    /// <summary>What <see cref="Flush"/> draws into: the frame's final picture (the window's backbuffer, or an offscreen texture). Set by the host.</summary>
    public GpuTexture? Target { get; set; }

    DebugOverlay(IGpuContext gpu, byte[] font, float pixelHeight = 16) : this(gpu, font, pixelHeight, false) { }

    /// <summary>Glyphs baked without oversampling and placed on whole pixels (<see cref="FromUiFont"/>).</summary>
    readonly bool snapped;

    DebugOverlay(IGpuContext gpu, byte[] font, float pixelHeight, bool uiFont, bool snapped = false)
    {
        Gpu = gpu;
        this.snapped = snapped;
        (PixelHeight, AtlasSize) = (pixelHeight, pixelHeight > 32 ? 2048 : uiFont ? 1024 : 512);
        var pixels = new byte[AtlasSize * AtlasSize];
        if (uiFont)
        {
            packedGlyphs = new StbTrueType.stbtt_packedchar[CharCount];
            kerning = new float[CharCount * CharCount];
            fixed (byte* f = font)
            fixed (byte* p = pixels)
            fixed (StbTrueType.stbtt_packedchar* g = packedGlyphs)
            {
                var pack = new StbTrueType.stbtt_pack_context();
                if (StbTrueType.stbtt_PackBegin(pack, p, AtlasSize, AtlasSize, 0, 1, null) == 0)
                    throw new InvalidOperationException("Could not create the UI font atlas.");
                try
                {
                    StbTrueType.stbtt_PackSetOversampling(pack, snapped ? 1u : 2u, snapped ? 1u : 2u);
                    // Negative sizes specify em pixels, matching UI/CSS font sizes, rather than ascender-to-descender height.
                    if (StbTrueType.stbtt_PackFontRange(pack, f, 0, -PixelHeight, FirstChar, CharCount, g) == 0)
                        throw new InvalidOperationException("The UI font atlas is too small.");
                }
                finally { StbTrueType.stbtt_PackEnd(pack); }
                var info = new StbTrueType.stbtt_fontinfo();
                if (StbTrueType.stbtt_InitFont(info, f, 0) == 0) throw new InvalidDataException("Invalid UI font.");
                float em = StbTrueType.stbtt_ScaleForMappingEmToPixels(info, PixelHeight);
                int ascent, descent, gap;
                StbTrueType.stbtt_GetFontVMetrics(info, &ascent, &descent, &gap);
                baselineOffset = (PixelHeight * 1.25f - (ascent - descent) * em) / 2 + ascent * em;
                for (int left = 0; left < CharCount; left++)
                    for (int right = 0; right < CharCount; right++)
                        kerning[left * CharCount + right] = StbTrueType.stbtt_GetCodepointKernAdvance(info, FirstChar + left, FirstChar + right) * em;
            }
        }
        else
            fixed (byte* f = font)
            fixed (byte* p = pixels)
            fixed (StbTrueType.stbtt_bakedchar* g = glyphs)
                StbTrueType.stbtt_BakeFontBitmap(f, 0, PixelHeight, p, AtlasSize, AtlasSize, FirstChar, CharCount, g);
        LineHeight = PixelHeight * 1.25f;
        CharWidth = packedGlyphs is { } packed ? packed['M' - FirstChar].xadvance : glyphs['M' - FirstChar].xadvance;

        // The glyphs (R8), sampled as the GL texture was: linear, repeating, no mips.
        using (var upload = gpu.Uploads.Begin())
        {
            atlas = upload.Create(new TextureDesc(InternalFormat.R8, AtlasSize, AtlasSize, Use: TextureUse.Sampled | TextureUse.TransferDst, Name: "debug overlay font"));
            upload.Write(atlas, 0, 0, new PixelRect(0, 0, (uint)AtlasSize, (uint)AtlasSize), pixels);
        }
        atlasSampled = new SampledTexture(gpu.Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.Linear, TextureMagFilter.Linear, TextureWrapMode.Repeat,
            TextureWrapMode.Repeat, TextureWrapMode.Repeat, false, DepthFunction.Lequal, false, 1, false, 0)), atlas.View());

        program = LegacyProgram.Create(gpu, Vertex, Fragment, "debug overlay");
        uScreen = program.Uniform("uScreen");
        uImage = program.Uniform("uImage");
        samplerAtlas = program.Sampler("uAtlas");
    }

    /// <summary>The overlay, or null when no monospace font is found.</summary>
    public static DebugOverlay? TryCreate(IGpuContext gpu)
    {
        var path = FontCandidates.FirstOrDefault(File.Exists);
        return path is null ? null : new DebugOverlay(gpu, File.ReadAllBytes(path));
    }

    /// <param name="pixelHeight">The size the glyphs are baked at: about the size the face is drawn at keeps it sharp.</param>
    public static DebugOverlay FromFont(IGpuContext gpu, string path, float pixelHeight = 16) => new(gpu, File.ReadAllBytes(path), pixelHeight);

    /// <summary>A UI face baked at <paramref name="pixelHeight"/> em pixels. <paramref name="snapped"/>: glyphs baked at 1x and placed on whole pixels, so each texel lands on one screen pixel (sharper; for fonts the original draws with <c>Antialias</c> false).</summary>
    public static DebugOverlay FromUiFont(IGpuContext gpu, string path, float pixelHeight, bool snapped = false) => new(gpu, File.ReadAllBytes(path), pixelHeight, true, snapped);

    public float MeasureText(string text, float scale = 1)
    {
        float width = 0;
        int previous = -1;
        foreach (char c in text)
        {
            int index = c - FirstChar;
            if (index < 0 || index >= CharCount) index = '?' - FirstChar;
            if (previous >= 0 && kerning is { } pairs) width += pairs[previous * CharCount + index] * scale;
            width += (packedGlyphs is { } packed ? packed[index].xadvance : glyphs[index].xadvance) * scale;
            previous = index;
        }
        return width;
    }

    public string FitText(string text, float width, float scale = 1)
    {
        if (MeasureText(text, scale) <= width) return text;
        const string suffix = "...";
        float available = width - MeasureText(suffix, scale), used = 0;
        if (available <= 0) return "";
        int count = 0;
        foreach (char c in text)
        {
            used += MeasureText(c.ToString(), scale);
            if (used > available) break;
            count++;
        }
        return text[..count] + suffix;
    }

    public IReadOnlyList<string> WrapText(string text, float width, float scale = 1)
    {
        var result = new List<string>();
        foreach (string paragraph in text.ReplaceLineEndings("\n").Split('\n'))
        {
            string line = "";
            foreach (string word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                string next = line.Length == 0 ? word : line + " " + word;
                if (line.Length > 0 && MeasureText(next, scale) > width) { result.Add(FitText(line, width, scale)); line = word; }
                else line = next;
            }
            result.Add(FitText(line, width, scale));
        }
        return result;
    }

    /// <summary>
    /// The items of a usage text's <c>Keys:</c> section, one per line ("T textures", "Esc quit"...): split at commas and
    /// semicolons, except inside parentheses.
    /// </summary>
    public static List<string> KeyItems(string usage)
    {
        int at = usage.IndexOf("Keys:", StringComparison.Ordinal);
        var items = new List<string>();
        if (at < 0) return items;
        var keys = string.Join(' ', usage[(at + 5)..].Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        int depth = 0, start = 0;
        for (int i = 0; i <= keys.Length; i++)
        {
            char c = i < keys.Length ? keys[i] : ',';
            // "Esc quit. F1 post off": a full stop before the next line's first key ends an item too.
            if (c == '.' && i + 2 < keys.Length && keys[i + 1] == ' ' && char.IsUpper(keys[i + 2])) c = ',';
            if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (depth == 0 && (c == ',' || c == ';'))
            {
                // ", / . time of day": a comma right after a separator is a key, not another separator.
                if (c == ',' && i < keys.Length && keys[start..i].Trim().Length == 0) continue;
                var item = keys[start..i].Trim().TrimEnd('.');
                if (item.Length > 0) items.Add(item);
                start = i + 1;
            }
        }
        return items;
    }

    public static readonly Vector4 PanelColour = new(0.05f, 0.05f, 0.06f, 0.78f), TextColour = new(0.92f, 0.92f, 0.88f, 1f);

    /// <summary>Draws a titled panel listing <paramref name="lines"/> when <see cref="Visible"/>; see <see cref="Panel"/>.</summary>
    public float Draw(int width, int height, string title, IReadOnlyList<string> lines, float top = 0) =>
        Visible ? Panel(width, height, title, lines, top) : top;

    /// <summary>
    /// Draws a titled panel at the left edge, below <paramref name="top"/>, listing <paramref name="lines"/> in as many
    /// columns as the height left needs; returns the panel's bottom (where a next panel can start).
    /// </summary>
    public float Panel(int width, int height, string title, IReadOnlyList<string> lines, float top = 0, float scale = 1)
    {
        if (width <= 0 || height <= 0) return top;
        scale = MathF.Max(scale, 1e-3f);
        float margin = 16 * scale, pad = 12 * scale, gap = 28 * scale;
        float lineHeight = LineHeight * scale, charWidth = CharWidth * scale;
        float y = top + margin;
        int rowsFit = Math.Max((int)((height - y - margin - 2 * pad - lineHeight * 1.5f) / lineHeight), 1);
        // Lines wider than the window wrap (the resident memory lines run long); with more than one column, to the column's share of it.
        float usable = width - 2 * margin - 2 * pad;
        var source = lines;
        lines = Wrap(source, (int)(usable / charWidth));
        int columns = (lines.Count + rowsFit - 1) / rowsFit;
        for (int pass = 0; pass < 2 && columns > 1; pass++)
        {
            lines = Wrap(source, (int)((usable - (columns - 1) * gap) / columns / charWidth));
            columns = (lines.Count + rowsFit - 1) / rowsFit;
        }
        int rows = (lines.Count + columns - 1) / Math.Max(columns, 1);
        float columnWidth = (lines.Count == 0 ? 0 : lines.Max(l => l.Length)) * charWidth;
        float panelW = Math.Max(columns * columnWidth + (columns - 1) * gap, title.Length * charWidth) + 2 * pad;
        float panelH = (rows + 1.5f) * lineHeight + 2 * pad;

        Rect(margin, y, margin + panelW, y + panelH, PanelColour);
        TextScaled(title, margin + pad, y + pad, TextColour, scale);
        for (int i = 0; i < lines.Count; i++)
            TextScaled(lines[i], margin + pad + i / rows * (columnWidth + gap), y + pad + (i % rows + 1.5f) * lineHeight, TextColour, scale);
        Flush(width, height);
        return y + panelH;
    }

    /// <summary>
    /// The lines broken to at most <paramref name="maxChars"/> characters, after a space where there is one in the line's second half (else hard);
    /// a continuation is indented four characters past its line's own indent.
    /// </summary>
    public static IReadOnlyList<string> Wrap(IReadOnlyList<string> lines, int maxChars)
    {
        maxChars = Math.Max(maxChars, 16);
        if (lines.All(l => l.Length <= maxChars)) return lines;
        var result = new List<string>(lines.Count + 4);
        foreach (var line in lines)
        {
            string indent = new(' ', Math.Min(line.Length - line.TrimStart(' ').Length + 4, maxChars / 2));
            string rest = line;
            bool first = true;
            while (true)
            {
                string piece = first ? rest : indent + rest;
                if (piece.Length <= maxChars) { result.Add(piece); break; }
                int cut = piece.LastIndexOf(' ', maxChars);
                if (cut <= maxChars / 2) cut = maxChars;
                result.Add(piece[..cut].TrimEnd());
                rest = piece[cut..].TrimStart();
                first = false;
            }
        }
        return result;
    }

    /// <summary>Queues a solid rectangle (pixels, top-left origin) for the next <see cref="Flush"/>.</summary>
    public void Rect(float x0, float y0, float x1, float y1, Vector4 colour) => Quad(x0, y0, x1, y1, -1, 0, -1, 0, colour);

    /// <summary>Queues a line of text whose top is at <paramref name="top"/>; returns its width in pixels.</summary>
    public float Text(string s, float x, float top, Vector4 colour) => TextScaled(s, x, top, colour, 1);

    public float TextScaled(string s, float x, float top, Vector4 colour, float scale)
    {
        if (imageBatch is not null) throw new InvalidOperationException("Flush UI images before queuing text.");
        var key = (s, x, top, scale, colour);
        // Font atlases are immutable. Bound the cache even for scrolling text and continuously changing counters.
        if (textGeometry.TryGetValue(key, out var cached))
        {
            batch.AddRange(cached.Vertices.AsSpan());
            return cached.Width;
        }
        int first = batch.Count;
        float start = x, baseline = top + (packedGlyphs is null ? PixelHeight : baselineOffset) * scale;
        int previous = -1;
        foreach (char c in s)
        {
            int index = c - FirstChar;
            if (index < 0 || index >= CharCount) index = '?' - FirstChar;
            if (previous >= 0 && kerning is { } pairs) x += pairs[previous * CharCount + index] * scale;
            previous = index;
            if (packedGlyphs is { } packed)
            {
                var pg = packed[index];
                float gx = snapped ? MathF.Round(x) : x, gy = snapped ? MathF.Round(baseline) : baseline;
                Quad(gx + pg.xoff * scale, gy + pg.yoff * scale, gx + pg.xoff2 * scale, gy + pg.yoff2 * scale,
                    pg.x0 / (float)AtlasSize, pg.y0 / (float)AtlasSize, pg.x1 / (float)AtlasSize, pg.y1 / (float)AtlasSize, colour);
                x += pg.xadvance * scale;
                continue;
            }
            var g = glyphs[index];
            float x0 = MathF.Round(x + g.xoff * scale), y0 = MathF.Round(baseline + g.yoff * scale);
            Quad(x0, y0, x0 + (g.x1 - g.x0) * scale, y0 + (g.y1 - g.y0) * scale,
                g.x0 / (float)AtlasSize, g.y0 / (float)AtlasSize, g.x1 / (float)AtlasSize, g.y1 / (float)AtlasSize, colour);
            x += g.xadvance * scale;
        }
        float width = x - start;
        int added = batch.Count - first;
        if (added <= 16384)
        {
            if (textGeometry.Count >= 512 || cachedTextFloats + added > 262144)
            {
                textGeometry.Clear();
                cachedTextFloats = 0;
            }
            textGeometry[key] = (CollectionsMarshal.AsSpan(batch)[first..].ToArray(), width);
            cachedTextFloats += added;
        }
        return width;
    }

    /// <summary>Draws everything queued since the last flush over <see cref="Target"/> (viewport <paramref name="width"/> × <paramref name="height"/>).</summary>
    public void Flush(int width, int height, Meitou.Data.Ui.UiRect? clip = null)
        => Flush(width, height, clip, null);

    /// <summary>Widget and font batches share one rendering; scissors and draw order still change per flush.</summary>
    internal void Flush(int width, int height, Meitou.Data.Ui.UiRect? clip, OverlayPass? pass)
    {
        if (batch.Count == 0 || width <= 0 || height <= 0 || Target is null) { batch.Clear(); imageBatch = null; return; }
        // A native segment of its own. It is begun first: the overlay is also drawn after a frame ended (a screenshot's key list), and the
        // segment opens the frame whose constants hold the vertices.
        var t = PassTargets.Of(Target, null).At(new PixelViewport(0, 0, width, height, 0, 1));
        if (clip is { } rect)
        {
            var clipped = rect.Intersect(new(0, 0, width, height));
            // The target keeps the renderer's lower-left convention; UI rectangles use top-left.
            int left = (int)Math.Floor(clipped.X), right = (int)Math.Ceiling(clipped.X + clipped.Width);
            int top = (int)Math.Floor(clipped.Y), bottom = (int)Math.Ceiling(clipped.Y + clipped.Height);
            t = t with { Scissor = new PixelRect(left, height - bottom, (uint)(right - left), (uint)(bottom - top)) };
        }
        if (pass is not null)
        {
            pass.Add(this, CollectionsMarshal.AsSpan(batch), imageBatch, t.Scissor);
            batch.Clear();
            imageBatch = null;
            return;
        }
        var cmd = Gpu.BeginNative("debug overlay");
        cmd.BeginRendering(t.Rendering);
        program.Set(uScreen, (float)width, height);
        program.Set(uImage, imageBatch is null ? 0 : 1);
        program.Bind(samplerAtlas, imageBatch ?? atlasSampled);
        var floats = CollectionsMarshal.AsSpan(batch);
        var vertices = Gpu.Frame.WriteConstants(new byte[floats.Length * 4], 16);
        MemoryMarshal.AsBytes(floats).CopyTo(vertices.Bytes);
        // As the GL version drew: no depth test or culling, blended over by alpha.
        var state = DrawState.For(t.Formats, Gpu.DepthClamp, blend: new BlendState(true, BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha));
        state.Record(cmd, t);
        Span<LegacyProgram.Attribute?> attributes =
        [
            new LegacyProgram.Attribute(new GpuBufferBinding(vertices.Handle, vertices.Offset), InternalFormat.RG32f, Stride * 4, false),
            new LegacyProgram.Attribute(new GpuBufferBinding(vertices.Handle, vertices.Offset + 8), InternalFormat.RG32f, Stride * 4, false),
            new LegacyProgram.Attribute(new GpuBufferBinding(vertices.Handle, vertices.Offset + 16), InternalFormat.Rgba32f, Stride * 4, false),
        ];
        var pipeline = Gpu.Pipelines.Get(state.Pipeline(program.Program, program.VertexLayout(attributes), PrimitiveType.Triangles, t.Formats, "debug overlay"));
        cmd.BindPipeline(pipeline);
        Span<GpuBufferBinding> buffers = [attributes[0]!.Value.Buffer, attributes[1]!.Value.Buffer, attributes[2]!.Value.Buffer];
        cmd.BindVertexBuffers(0, buffers);
        program.Flush(cmd);
        cmd.Draw((uint)(floats.Length / Stride));
        cmd.EndRendering();
        Gpu.EndNative(cmd);
        batch.Clear();
        imageBatch = null;
    }

    /// <summary>Collects painter-ordered batches, coalescing adjacent compatible draws into one upload and rendering.</summary>
    internal sealed class OverlayPass(IGpuContext gpu, int width, int height) : IDisposable
    {
        readonly List<float> vertices = [];
        readonly List<Run> runs = [];
        DebugOverlay? renderer;
        GpuTexture? target;
        readonly record struct Run(SampledTexture Texture, bool Image, PixelRect Scissor, int First, int Count);

        public OverlayPass Reset(int newWidth, int newHeight)
        {
            width = newWidth; height = newHeight;
            vertices.Clear(); runs.Clear(); renderer = null; target = null;
            return this;
        }

        public void Add(DebugOverlay owner, ReadOnlySpan<float> data, SampledTexture? image, PixelRect clip)
        {
            if (owner.Gpu != gpu || target is not null && target != owner.Target)
                throw new InvalidOperationException("An overlay pass must share its GPU and target.");
            renderer ??= owner;
            target = owner.Target;
            // If no geometry reaches outside this scissor, the full viewport clips it identically.
            // Different widget clips can then share a draw without changing their pixels.
            bool contained = true, solid = true;
            float left = clip.X, right = left + clip.Width;
            float bottom = height - clip.Y, top = bottom - clip.Height;
            for (int i = 0; i < data.Length; i += Stride)
            {
                contained &= data[i] >= left && data[i] <= right && data[i + 1] >= top && data[i + 1] <= bottom;
                solid &= data[i + 2] < 0;
            }
            if (contained) clip = new PixelRect(0, 0, (uint)width, (uint)height);
            var texture = image ?? owner.atlasSampled;
            bool isImage = image.HasValue;
            int first = vertices.Count / Stride, count = data.Length / Stride;
            vertices.AddRange(data);
            if (runs.Count > 0)
            {
                var last = runs[^1];
                if (SameClip(last.Scissor, clip) && (solid || last.Texture == texture && last.Image == isImage))
                {
                    runs[^1] = last with { Count = last.Count + count };
                    return;
                }
            }
            runs.Add(new(texture, isImage, clip, first, count));
        }

        static bool SameClip(PixelRect a, PixelRect b) => a.X == b.X && a.Y == b.Y
            && a.Width == b.Width && a.Height == b.Height;

        public void Dispose()
        {
            if (renderer is not { } painter || target is null || runs.Count == 0) return;
            var cmd = gpu.BeginNative("UI overlay");
            var targets = PassTargets.Of(target, null).At(new PixelViewport(0, 0, width, height, 0, 1));
            cmd.BeginRendering(targets.Rendering);
            try
            {
                var state = DrawState.For(targets.Formats, gpu.DepthClamp, blend: new BlendState(true, BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha));
                state.Record(cmd, targets);
                var data = CollectionsMarshal.AsSpan(vertices);
                var upload = gpu.Frame.WriteConstants(new byte[data.Length * 4], 16);
                MemoryMarshal.AsBytes(data).CopyTo(upload.Bytes);
                Span<LegacyProgram.Attribute?> attributes =
                [
                    new(new GpuBufferBinding(upload.Handle, upload.Offset), InternalFormat.RG32f, Stride * 4, false),
                    new(new GpuBufferBinding(upload.Handle, upload.Offset + 8), InternalFormat.RG32f, Stride * 4, false),
                    new(new GpuBufferBinding(upload.Handle, upload.Offset + 16), InternalFormat.Rgba32f, Stride * 4, false),
                ];
                var program = painter.program;
                var pipeline = gpu.Pipelines.Get(state.Pipeline(program.Program, program.VertexLayout(attributes), PrimitiveType.Triangles, targets.Formats, "UI overlay"));
                cmd.BindPipeline(pipeline);
                Span<GpuBufferBinding> buffers = [attributes[0]!.Value.Buffer, attributes[1]!.Value.Buffer, attributes[2]!.Value.Buffer];
                cmd.BindVertexBuffers(0, buffers);
                program.Set(painter.uScreen, (float)width, height);
                var scissor = targets.Scissor;
                foreach (var run in runs)
                {
                    if (!SameClip(scissor, run.Scissor)) { cmd.SetScissor(run.Scissor); scissor = run.Scissor; }
                    program.Set(painter.uImage, run.Image ? 1 : 0);
                    program.Bind(painter.samplerAtlas, run.Texture);
                    program.Flush(cmd);
                    cmd.Draw((uint)run.Count, firstVertex: (uint)run.First);
                }
            }
            finally
            {
                cmd.EndRendering();
                gpu.EndNative(cmd);
                vertices.Clear(); runs.Clear(); renderer = null; target = null;
            }
        }
    }
    /// <summary>Queues a texture rectangle. Flush before switching between image and font batches.</summary>
    public void Image(SampledTexture texture, Vector4 destination, Vector4 uv, Vector4 colour)
    {
        if (batch.Count > 0 && (imageBatch is null || imageBatch.Value != texture))
            throw new InvalidOperationException("Flush before switching overlay textures.");
        imageBatch = texture;
        Quad(destination.X, destination.Y, destination.Z, destination.W, uv.X, uv.Y, uv.Z, uv.W, colour);
    }

    /// <summary>Queues a texture rectangle turned by <paramref name="angle"/> radians (clockwise on screen) about its centre.</summary>
    public void ImageRotated(SampledTexture texture, Vector4 destination, Vector4 uv, Vector4 colour, float angle)
    {
        if (batch.Count > 0 && (imageBatch is null || imageBatch.Value != texture))
            throw new InvalidOperationException("Flush before switching overlay textures.");
        imageBatch = texture;
        var centre = new Vector2(destination.X + destination.Z, destination.Y + destination.W) / 2;
        var half = new Vector2(destination.Z - destination.X, destination.W - destination.Y) / 2;
        var (sin, cos) = MathF.SinCos(angle);
        Vector2 Corner(float sx, float sy) => centre + new Vector2(sx * half.X * cos - sy * half.Y * sin, sx * half.X * sin + sy * half.Y * cos);
        var (a, b, c, d) = (Corner(-1, -1), Corner(1, -1), Corner(1, 1), Corner(-1, 1));
        Emit(a.X, a.Y, uv.X, uv.Y, colour); Emit(b.X, b.Y, uv.Z, uv.Y, colour); Emit(c.X, c.Y, uv.Z, uv.W, colour);
        Emit(a.X, a.Y, uv.X, uv.Y, colour); Emit(c.X, c.Y, uv.Z, uv.W, colour); Emit(d.X, d.Y, uv.X, uv.W, colour);
    }

    /// <summary>Queues a solid triangle (pixels, top-left origin).</summary>
    public void Triangle(Vector2 a, Vector2 b, Vector2 c, Vector4 colour)
    {
        var vertices = Reserve(3);
        vertices[0] = new(new(a.X, a.Y, -1, 0), colour);
        vertices[1] = new(new(b.X, b.Y, -1, 0), colour);
        vertices[2] = new(new(c.X, c.Y, -1, 0), colour);
    }

    /// <summary>Queues a solid line segment <paramref name="thickness"/> pixels wide.</summary>
    public void Line(float x0, float y0, float x1, float y1, float thickness, Vector4 colour)
    {
        var d = new Vector2(x1 - x0, y1 - y0);
        float length = d.Length();
        if (length < 1e-3f) return;
        var n = new Vector2(-d.Y, d.X) / length * (thickness * 0.5f);
        OverlayVertex a = new(new(x0 + n.X, y0 + n.Y, -1, 0), colour), b = new(new(x1 + n.X, y1 + n.Y, -1, 0), colour);
        OverlayVertex c = new(new(x1 - n.X, y1 - n.Y, -1, 0), colour), e = new(new(x0 - n.X, y0 - n.Y, -1, 0), colour);
        var vertices = Reserve(6);
        vertices[0] = a; vertices[1] = b; vertices[2] = c;
        vertices[3] = a; vertices[4] = c; vertices[5] = e;
    }

    void Quad(float x0, float y0, float x1, float y1, float u0, float v0, float u1, float v1, Vector4 c)
    {
        OverlayVertex a = new(new(x0, y0, u0, v0), c), b = new(new(x1, y0, u1, v0), c);
        OverlayVertex d = new(new(x1, y1, u1, v1), c), e = new(new(x0, y1, u0, v1), c);
        var vertices = Reserve(6);
        vertices[0] = a; vertices[1] = b; vertices[2] = d;
        vertices[3] = a; vertices[4] = d; vertices[5] = e;
    }

    void Emit(float x, float y, float u, float v, Vector4 c)
    {
        Reserve(1)[0] = new(new(x, y, u, v), c);
    }

    [StructLayout(LayoutKind.Sequential)]
    readonly record struct OverlayVertex(Vector4 PositionUv, Vector4 Colour);

    Span<OverlayVertex> Reserve(int count)
    {
        int start = batch.Count;
        int added = count * Stride;
        batch.EnsureCapacity(start + added);
        CollectionsMarshal.SetCount(batch, start + added);
        return MemoryMarshal.Cast<float, OverlayVertex>(CollectionsMarshal.AsSpan(batch).Slice(start, added));
    }

    public void Dispose()
    {
        atlas.Dispose();
        program.Dispose();
    }
}
