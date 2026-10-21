using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Net.Session;

namespace Meitou.Game;

/// <summary>
/// The session's notices, banners, indicators and alerts (docs/meitou-ui.md "Multiplayer", "Status, Esc menu, players window, alerts"): the connection indicator, the
/// "who paused" line under the paused panel, the banner for another player's clock change, the starving plate, the disabled time buttons and the alert for an ended session.
/// </summary>
sealed partial class PlayerInterface
{
    /// <summary>The seconds a clock banner holds, of which the last <see cref="BannerFade"/> fade out.</summary>
    const double BannerHold = 3, BannerFade = 0.5;
    /// <summary>A client whose newest snapshot on show is older than this is starving (the game host ends the session at its own, longer timeout).</summary>
    const double StarveSeconds = 0.5;
    /// <summary>The round trips (ms) up to which the indicator lights three and two bars; above, one (engine choice).</summary>
    const int PingGood = 100, PingFair = 200;

    string? banner;
    long bannerUntil;
    (string Title, string Text)? pendingAlert;

    /// <summary>Once a frame: <paramref name="from"/> is the role of the last frame, <paramref name="events"/> the session's new events.</summary>
    void StatusFrame(NetRole from, IReadOnlyList<MultiplayerEvent> events)
    {
        foreach (var e in events)
        {
            // A pause needs no banner: the line under the paused panel names who paused for as long as it lasts.
            if (e.Player == LocalPlayer || e.Kind is not (MultiplayerEventKind.Resumed or MultiplayerEventKind.Speed)) continue;
            banner = e.Kind switch
            {
                MultiplayerEventKind.Resumed => $"{e.Name} resumed the game",
                _ => string.Create(CultureInfo.InvariantCulture, $"{e.Name} set the speed to {e.Speed:0.##}x"),
            };
            bannerUntil = Stopwatch.GetTimestamp() + (long)(BannerHold * Stopwatch.Frequency);
        }
        if (Net is not { Role: not NetRole.None })
        {
            banner = null;
            if (from != NetRole.None) faithfulScreens.Close("players");   // the session ended under an open players window
        }
    }

    /// <summary>The host's report of an ended session (title DISCONNECTED once it reached play, else CONNECTION FAILED; <see cref="EndText"/>), shown over the title.</summary>
    public void ShowDisconnected(NetDisconnect reason, bool reachedPlay)
    {
        pendingAlert = EndText(reason, reachedPlay);
        if (TitleOpen) OpenPendingAlert();
    }

    /// <summary>Opens the alert of an ended session, once the title is up.</summary>
    void OpenPendingAlert()
    {
        if (pendingAlert is not { } alert) return;
        pendingAlert = null;
        Alert(alert.Title, alert.Text);
    }

    /// <summary>The HUD's time buttons are off for a player the host's rules leave off the clock.</summary>
    void BindNetHud(UiBindings b)
    {
        if (Net is null || Net.CanControlTime(Net.LocalPlayer)) return;
        for (int i = 0; i < ButtonSpeeds.Length; i++) b.Disabled.Add($"TimeSpeedButton{i + 1}");
    }

    /// <summary>
    /// The width <paramref name="text"/> is drawn at in font resource <paramref name="font"/> (the painter's own measuring, as the tooltips use): sized by the standard face instead, a
    /// box came out narrower than its Kenshi font and the text was cut with an ellipsis.
    /// </summary>
    float MeasureNet(string font, string text) =>
        uiPainter?.MeasureText(font, text, (int)Height, 0, TitleOpen ? 0 : StandardTextPixels * Height / 1080f).Width ?? text.Length * StandardTextPixels * Height / 1080f * 0.6f;

    /// <summary>A text box of the session's UI; its lettering scales with the window as its bounds do (no fixed <c>TextScale</c>).</summary>
    static UiControl NetText(string name, UiRect bounds, string text, string font, string align, string colour, string skin = "", float alpha = 1)
    {
        var properties = new Dictionary<string, string> { ["FontName"] = font, ["TextAlign"] = align, ["TextShadow"] = "true", ["TextColour"] = colour };
        if (alpha < 1) properties["Alpha"] = alpha.ToString("R", CultureInfo.InvariantCulture);
        return new(name, "TextBox", bounds, text, "", skin, true, properties, Vector4.Zero, Pickable: false);
    }

    /// <summary>Notices and indicators over the HUD (drawn before the windows).</summary>
    void AddNetStatus(List<UiControl> controls)
    {
        if (Net is not { Role: not NetRole.None } net) return;
        float s = Height / 1080f;
        var panel = controls.FirstOrDefault(c => c.Name == "PausedPanel");
        bool paused = panel is not null;
        // Who paused: under the paused panel, in the pauser's colour.
        if (panel is not null && net.PausedBy is { } pauser)
        {
            var who = PlayerInfo(pauser);
            string name = pauser == net.LocalPlayer ? "You" : who?.Name ?? $"Player {pauser}";
            string line = $"{name} paused the game";
            // Centred on the panel at the line's own width: a long name runs past the panel's edges instead of being cut.
            float w = Math.Max(panel.Bounds.Width, MeasureNet("Kenshi_StandardFont_Medium", line) + 16 * s);
            controls.Add(NetText("net-paused-by", new(panel.Bounds.X + (panel.Bounds.Width - w) / 2, panel.Bounds.Y + panel.Bounds.Height + 2 * s, w, 0.026f * Height),
                line, "Kenshi_StandardFont_Medium", "Center", PlayerColours.MyGui(who?.Colour ?? -1)));
        }

        // The banner for another player's clock change.
        long now = Stopwatch.GetTimestamp();
        bool bannerShown = false;
        if (banner is { } text)
        {
            double left = Stopwatch.GetElapsedTime(now, bannerUntil).TotalSeconds;
            if (left <= 0) banner = null;
            else
            {
                bannerShown = true;
                float w = MeasureNet("Kenshi_StandardFont_Medium", text) + 32 * s;
                float y = (0.115f + (paused ? 0.03f : 0)) * Height;
                controls.Add(NetText("net-banner", new((Width - w) / 2, y, w, 0.04f * Height), text, "Kenshi_StandardFont_Medium", "Center", "0.902 0.902 0.902",
                    "Kenshi_FloatinglPanelSmallSkin", (float)Math.Clamp(left / BannerFade, 0, 1)));
            }
        }

        // The indicator, top right.
        var box = new UiRect(Width - 0.128f * Width, 0.008f * Height, 0.12f * Width, 0.03f * Height);
        const string inkText = "0.902 0.902 0.902";
        if (net.Role == NetRole.Host)
        {
            int present = net.Players.Count(p => p.State != NetPlayerState.Away);
            controls.Add(NetText("net-indicator", box, $"HOSTING · {present}/{net.Rules.MaxPlayers}", "Kenshi_StandardFont_MediumSmall", "Right VCenter", inkText));
            return;
        }
        var quality = net.Quality;
        bool starving = quality.SnapshotAgeSeconds > StarveSeconds;
        int lit = quality.PingMs <= PingGood ? 3 : quality.PingMs <= PingFair ? 2 : 1;
        var tone = starving || lit == 1 ? new Vector4(0.95f, 0.35f, 0.3f, 1) : lit == 2 ? new Vector4(0.95f, 0.75f, 0.3f, 1) : new Vector4(0.45f, 0.9f, 0.5f, 1);
        float barW = 3 * s, gap = 2 * s, barsWidth = 3 * barW + 2 * gap;
        for (int i = 0; i < 3; i++)
        {
            float h = box.Height * (i + 1) / 3;
            controls.Add(new($"net-bar-{i}", "Widget", new(box.X + box.Width - barsWidth + i * (barW + gap), box.Y + box.Height - h, barW, h), "", "", "", true,
                new Dictionary<string, string> { ["NeedMouse"] = "false" }, i < lit ? tone : new Vector4(0.35f, 0.35f, 0.35f, 0.6f), Pickable: false));
        }
        controls.Add(NetText("net-ping", new(box.X, box.Y, box.Width - barsWidth - 6 * s, box.Height), $"{quality.PingMs} ms", "Kenshi_StandardFont_MediumSmall", "Right VCenter", inkText));
        if (starving)
        {
            string wait = $"Waiting for the host... {quality.SnapshotAgeSeconds:0} s";
            float w = MeasureNet("Kenshi_StandardFont_Medium", wait) + 32 * s;
            controls.Add(NetText("net-starving", new((Width - w) / 2, (bannerShown ? 0.21f : 0.16f) * Height, w, 0.05f * Height), wait, "Kenshi_StandardFont_Medium", "Center", inkText,
                "Kenshi_FloatingPanelSkin"));
        }
    }
}
