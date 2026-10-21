using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Net.Session;

namespace Meitou.Game;

/// <summary>
/// The held player list (docs/meitou-ui.md "Multiplayer", "Player list, name tags, map markers"): a panel over the windows with every player still in the game, in roster
/// order: colour chip, name in its colour, HOST tag, joining state, squads and ping. Drawn only while <see cref="PlayerListHeld"/>; all controls are unpickable.
/// </summary>
sealed partial class PlayerInterface
{
    // The ping thresholds and colours of the connection indicator (green up to 80 ms, amber up to 180 ms, red above).
    static readonly Vector4 ListPingGood = Rgb(0x79B44B), ListPingFair = Rgb(0xF2C14E), ListPingBad = Rgb(0xE65139), ListGrey = new(0.65f, 0.65f, 0.65f, 1);

    /// <summary>The colour of a round trip of <paramref name="ms"/>.</summary>
    static Vector4 ListPingColour(int ms) => ms <= 80 ? ListPingGood : ms <= 180 ? ListPingFair : ListPingBad;

    void AddPlayerList(List<UiControl> controls)
    {
        if (Net is not { } net) return;
        // Entries that left (Away) stay in the roster so tags can still name them, but not in the list.
        var rows = net.Players.Where(p => p.State != NetPlayerState.Away).ToList();
        float s = Height / 1080f, rowHeight = Height * 0.034f, panelWidth = Width * 0.34f, pad = panelWidth * 0.03f;
        float titleHeight = Height * 0.05f, headerHeight = Height * 0.03f;
        float panelHeight = titleHeight + headerHeight + rows.Count * rowHeight + pad;
        float left = (Width - panelWidth) / 2, top = Height * 0.14f;
        var none = new Dictionary<string, string>();
        const string small = "Kenshi_StandardFont_MediumSmall";
        UiControl Text(string name, string caption, float x, float width, float y, float height, string font, Vector4 colour, string align = "Left VCenter") =>
            new(name, "TextBox", new(x, y, width, height), caption, "", "", true,
                new Dictionary<string, string> { ["FontName"] = font, ["TextAlign"] = align, ["TextShadow"] = "true" }, Vector4.Zero, colour, Pickable: false);
        float X(float fraction) => left + panelWidth * fraction;
        float W(float fraction) => panelWidth * fraction;

        controls.Add(new("players-list", "Widget", new(left, top, panelWidth, panelHeight), "", "", "Kenshi_FloatingPanelSkin", true, none, Vector4.Zero, Pickable: false));
        controls.Add(Text("players-list-title", $"PLAYERS  {rows.Count} / {net.Rules.MaxPlayers}", left + pad, panelWidth - 2 * pad, top + 4 * s, titleHeight,
            "Kenshi_PaintedTextFont_Medium", Vector4.One));
        float y = top + titleHeight;
        controls.Add(Text("players-list-h-name", "Player", X(0.08f), W(0.3f), y, headerHeight, small, ListGrey));
        controls.Add(Text("players-list-h-squads", "Squads", X(0.7f), W(0.14f), y, headerHeight, small, ListGrey, "Center VCenter"));
        controls.Add(Text("players-list-h-ping", "Ping", X(0.84f), W(0.14f), y, headerHeight, small, ListGrey, "Right VCenter"));
        y += headerHeight;
        float chip = Width * 0.012f;
        for (int i = 0; i < rows.Count; i++, y += rowHeight)
        {
            var p = rows[i];
            var colour = PlayerColours.Of(p.Colour);
            bool host = p.State == NetPlayerState.Host;
            controls.Add(new($"players-list-chip-{i}", "Widget", new(X(0.04f), y + (rowHeight - chip) / 2, chip, chip), "", "", "", true, none, colour, Pickable: false));
            controls.Add(Text($"players-list-name-{i}", p.Player == net.LocalPlayer ? p.Name + " (you)" : p.Name, X(0.08f), W(0.34f), y, rowHeight,
                "Kenshi_StandardFont_Medium", colour));
            if (host)
            {
                float tagHeight = rowHeight * 0.7f;
                controls.Add(new($"players-list-host-{i}", "Button", new(X(0.43f), y + (rowHeight - tagHeight) / 2, W(0.1f), tagHeight), "HOST", "", "Kenshi_Button1Skin", true,
                    new Dictionary<string, string> { ["FontName"] = small, ["TextAlign"] = "Center" }, Vector4.Zero, Pickable: false));
            }
            if (p.State == NetPlayerState.Loading) controls.Add(Text($"players-list-state-{i}", "joining…", X(0.54f), W(0.16f), y, rowHeight, small, ListGrey));
            controls.Add(Text($"players-list-squads-{i}", p.Squads.ToString(CultureInfo.InvariantCulture), X(0.7f), W(0.14f), y, rowHeight, small, Vector4.One, "Center VCenter"));
            controls.Add(host
                ? Text($"players-list-ping-{i}", "—", X(0.84f), W(0.14f), y, rowHeight, small, ListGrey, "Right VCenter")
                : Text($"players-list-ping-{i}", $"{p.RoundTripMs} ms", X(0.84f), W(0.14f), y, rowHeight, small, ListPingColour(p.RoundTripMs), "Right VCenter"));
        }
    }
}
