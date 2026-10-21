using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Rendering;
using Meitou.Rendering.Ui;

namespace Meitou.Game;

/// <summary>
/// The loading screen drawn while the game starts and between scenes (docs/engine.md "Loading"): the original's <c>Kenshi_LoadingScreen</c> (one of its
/// <c>loading_screen*.dds</c> pictures full screen, its panel made small at the bottom right with a tip from <c>data/tips.txt</c>), and Meitou's panel at the bottom left: LOADING..., a spinner and an
/// approximate progress bar under it (the original has neither: engine choice). The bar eases towards the end of the running step by the time the step usually
/// takes, so it moves while a step runs; it never goes back.
/// </summary>
sealed class LoadingScreen : IDisposable
{
    readonly DebugOverlay overlay;
    DebugOverlay? stamp;   // the original's LOADING... face (Kenshi_UI_Messages: MailartRubberstamp-Regular.ttf)
    readonly UiPainter? painter;
    readonly MyGuiResources? resources;
    readonly MyGuiLayout? layout;
    readonly string? picture;
    readonly string tip;
    readonly Stopwatch clock = Stopwatch.StartNew();
    string label = "";
    double from, to, expected = 1, stepStart;
    float shown;
    const double ExitSeconds = 0.7;
    double? exitStart;
    bool drawn;

    float ExitProgress => exitStart is { } start ? (float)Math.Clamp((clock.Elapsed.TotalSeconds - start) / ExitSeconds, 0, 1) : 0;
    public bool Exiting => exitStart is not null && ExitProgress < 1;

    /// <summary>Reveal the ready scene while the panels slide out. A fast sky load that never showed panels has nothing to dismiss.</summary>
    public void Finish()
    {
        if (drawn) exitStart ??= clock.Elapsed.TotalSeconds;
    }

    LoadingScreen(DebugOverlay overlay, UiPainter? painter, MyGuiResources? resources, MyGuiLayout? layout, string? picture, string tip)
    {
        (this.overlay, this.painter, this.resources, this.layout, this.picture, this.tip) = (overlay, painter, resources, layout, picture, tip);
    }

    /// <summary>The screen with the original's layout, picture and tips when they are found (else the spinner and bar on black).</summary>
    public static LoadingScreen Create(DebugOverlay overlay, Func<string, string?> resolve, string? dataDirectory)
    {
        var random = new Random();
        string tip = "";
        // tips.txt through the resource lookup: the active language's own copy (locale/<code>/tips.txt) replaces data/tips.txt; a tip that is also a
        // message of the catalog (main.pot lists the tips) is translated.
        string? tipsFile = resolve("tips.txt") ?? (dataDirectory is null ? null : Path.Combine(dataDirectory, "tips.txt"));
        if (tipsFile is not null && File.Exists(tipsFile))
        {
            var tips = File.ReadAllLines(tipsFile).Where(l => l.Trim().Length > 0).ToArray();
            if (tips.Length > 0) tip = Meitou.Localisation.Loc.Tr(tips[random.Next(tips.Length)].Trim());
        }
        try
        {
            var resources = MyGuiResources.Load("kenshi_init.xml", resolve);
            var layout = resolve("Kenshi_LoadingScreen.layout") is { } file ? MyGuiTemplates.Expand(MyGuiReader.ReadLayout(file), resources) : null;
            // The pictures the original ships (loading_screen0..10, some numbers missing).
            var pictures = Enumerable.Range(0, 16).Select(i => $"loading_screen{i}.dds").Where(n => resolve(n) is not null).ToArray();
            var painter = new UiPainter(overlay, resources, resolve);
            var stamp = resolve("MailartRubberstamp-Regular.ttf") is { } stampFile ? DebugOverlay.FromFont(overlay.Gpu, stampFile, 64) : null;
            return new(overlay, painter, resources, layout, pictures.Length > 0 ? pictures[random.Next(pictures.Length)] : null, tip) { stamp = stamp };
        }
        catch (Exception e) when (e is IOException or InvalidDataException or System.Xml.XmlException)
        {
            Console.Error.WriteLine($"loading   the original loading screen did not load ({e.Message}); spinner only");
            return new(overlay, null, null, null, null, tip);
        }
    }

    /// <summary>A step starts: its name, the bar's share it covers (<paramref name="start"/> to <paramref name="end"/>, 0..1) and about how long it takes.</summary>
    public void Step(string name, double start, double end, double seconds)
    {
        label = name;
        from = Math.Max(start, shown);
        to = Math.Max(end, from);
        expected = Math.Max(seconds, 0.05);
        stepStart = clock.Elapsed.TotalSeconds;
    }

    /// <summary>The bar's position now: the step's start, eased towards 95 % of its share by the time it has run against the time it usually takes.</summary>
    public float Progress
    {
        get
        {
            double t = (clock.Elapsed.TotalSeconds - stepStart) / expected;
            double eased = from + (to - from) * 0.95 * (1 - Math.Exp(-2.5 * t));
            return shown = Math.Max(shown, (float)Math.Clamp(eased, 0, 1));
        }
    }

    /// <summary>Starts the bar again (a new load between scenes).</summary>
    public void Restart() { shown = 0; from = to = 0; exitStart = null; drawn = false; }

    public void Draw(int width, int height, bool background = true)
    {
        float t = ExitProgress;
        if (t >= 1) return;
        drawn = true;
        float departure = t * t * (3 - 2 * t), opacity = 1 - departure;
        if (background)
        {
            overlay.Rect(0, 0, width, height, new Vector4(0.02f, 0.02f, 0.018f, opacity));
            overlay.Flush(width, height);
        }
        float progress = exitStart is null ? Progress : 1;
        // Two panels along the bottom (engine choice): the loading panel on the left (LOADING..., the spinner, the bar, the step), the layout's own panel on the
        // right, made small and holding only the tip. Sizes at 1080 rows, scaled with the height.
        float s = height / 1080f, margin = 40 * s, panelHeight = 120 * s, top = height - margin - panelHeight;
        float half = Math.Max(width / 2f - 1.5f * margin, 1);
        var box = new UiRect(margin, top, Math.Min(480 * s, half), panelHeight);
        var tipBox = new UiRect(width - margin - Math.Min(480 * s, half), top, Math.Min(480 * s, half), panelHeight);
        float slide = (box.Width + margin + 8 * s) * departure;
        box = box with { X = box.X - slide };
        tipBox = tipBox with { X = tipBox.X + slide };
        if (painter is not null && layout is not null && resources is not null)
        {
            var b = new UiBindings();
            if (!background) b.Hidden.Add("BackgroundImage");
            var image = new Dictionary<string, string> { ["Alpha"] = opacity.ToString(CultureInfo.InvariantCulture) };
            if (picture is not null) image["ImageTexture"] = picture;
            b.Properties["BackgroundImage"] = image;
            b.Positions["LoadingPanel"] = tipBox;
            b.Positions["MessageText"] = new UiRect(tipBox.X + 8 * s, tipBox.Y + 8 * s, tipBox.Width - 16 * s, tipBox.Height - 16 * s);
            b.Hidden.Add("LoadingText");
            b.Text["MessageText"] = tip;
            b.Properties["MessageText"] = new Dictionary<string, string> { ["Static"] = "true", ["WordWrap"] = "true", ["TextAlign"] = "Center",
                ["FontName"] = "Kenshi_StandardFont_Medium", ["FontPixels"] = Math.Max(8, (int)MathF.Round(16 * s)).ToString(CultureInfo.InvariantCulture) };
            var controls = MyGuiPresentation.Arrange(layout, width, height, b, resources.Layers).ToList();
            // The loading panel in the tip panel's frame (Kenshi_FloatingPanelSkin, the layout's LoadingPanel skin).
            controls.Add(new UiControl("LoadingBox", "Widget", box, "", "", "Kenshi_FloatingPanelSkin", true, new Dictionary<string, string>(), Vector4.Zero));
            painter.Draw(controls, width, height, new Vector2(-1, -1), null);
        }
        else overlay.Rect(box.X, box.Y, box.X + box.Width, box.Y + box.Height, new Vector4(0.08f, 0.07f, 0.06f, 0.85f));

        // The bar along the panel's bottom; above it LOADING... and the running step on the left, the spinner on the right.
        float pad = 16 * s, barHeight = 10 * s;
        float x0 = box.X + pad, x1 = box.X + box.Width - pad, barTop = box.Y + box.Height - pad - barHeight;
        float rowTop = box.Y + pad * 0.75f, rowHeight = barTop - pad * 0.5f - rowTop, cy = rowTop + rowHeight / 2;
        float textEnd = x0;
        if (stamp is not null)
        {
            float scale = rowHeight * 0.52f / stamp.LineHeight, textTop = cy - stamp.LineHeight * scale / 2;
            stamp.Target = overlay.Target;
            stamp.TextScaled("LOADING...", x0 + 2, textTop + 2, new Vector4(0, 0, 0, 0.8f), scale);
            textEnd = x0 + stamp.TextScaled("LOADING...", x0, textTop, new Vector4(0.87f, 0.85f, 0.81f, 1), scale);
            stamp.Flush(width, height);
        }
        else textEnd = x0 + overlay.Text("LOADING...", x0, cy - overlay.LineHeight / 2, DebugOverlay.TextColour);

        // The spinner at the right end: twelve spokes, the bright one going round once a second.
        float outer = Math.Min(rowHeight * 0.45f, 22 * s), inner = outer * 0.45f, cx = x1 - outer;
        double turn = clock.Elapsed.TotalSeconds % 1;
        const int Spokes = 12;
        for (int i = 0; i < Spokes; i++)
        {
            float a = i * MathF.Tau / Spokes;
            float behind = (float)(((turn * Spokes - i) % Spokes + Spokes) % Spokes) / Spokes;   // 0 at the head, towards 1 behind it
            var colour = new Vector4(0.9f, 0.84f, 0.7f, 0.15f + 0.85f * (1 - behind));
            overlay.Line(cx + MathF.Sin(a) * inner, cy - MathF.Cos(a) * inner, cx + MathF.Sin(a) * outer, cy - MathF.Cos(a) * outer, Math.Max(2, 3 * s), colour);
        }
        if (label.Length > 0 && textEnd + 12 * s + label.Length * overlay.CharWidth <= cx - outer - 12 * s)
            overlay.Text(label, textEnd + 12 * s, cy - overlay.LineHeight / 2, new Vector4(0.62f, 0.6f, 0.52f, 1));
        overlay.Rect(x0 - 2, barTop - 2, x1 + 2, barTop + barHeight + 2, new Vector4(0.35f, 0.31f, 0.24f, 1));
        overlay.Rect(x0, barTop, x1, barTop + barHeight, new Vector4(0.08f, 0.07f, 0.06f, 1));
        overlay.Rect(x0, barTop, x0 + (x1 - x0) * progress, barTop + barHeight, new Vector4(0.78f, 0.66f, 0.42f, 1));
        overlay.Flush(width, height);
    }

    public void Dispose() { painter?.Dispose(); stamp?.Dispose(); }
}
