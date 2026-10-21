using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Net.Session;
using Meitou.Ui;

namespace Meitou.Game;

/// <summary>The Esc menu's multiplayer rows and the players window (docs/meitou-ui.md "Multiplayer", "Status, Esc menu, players window, alerts").</summary>
sealed partial class PlayerInterface
{
    // ---- Esc menu

    /// <summary>
    /// The popup's geometry in fractions of the original panel's height, from <c>Kenshi_MainMenuPopupPanel.layout</c> (<b>Verified</b>): the six buttons sit at y 0.0436046 + i x 0.145349 with
    /// height 0.130814; Resume is 0.203488 below Exit and the panel ends 0.040698 below Resume.
    /// </summary>
    const float EscFirst = 0.0436046f, EscPitch = 0.145349f, EscHeight = 0.130814f, EscResumeGap = 0.203488f, EscBottom = 0.040698f;
    const string EscPanel = "MainMenuPopupPanel";
    static readonly string[] EscButtons = ["NewGameButton", "SaveGameButton", "LoadGameButton", "OptionsButton", "ExitGameButton", "ResumeButton"];
    /// <summary>The decorated rows (their widget names are not in the layout) and their captions.</summary>
    static readonly Dictionary<string, string> NetMenuCaptions = new()
    {
        ["MpPlayersButton"] = "PLAYERS", ["MpOpenButton"] = "MULTIPLAYER", ["MpStopButton"] = "STOP HOSTING", ["MpLeaveButton"] = "DISCONNECT",
    };
    /// <summary>Where <see cref="BindNetMenu"/> put the decorated rows this frame, for <see cref="AddNetMenuContent"/>.</summary>
    readonly Dictionary<string, UiRect> netMenuRects = [];

    /// <summary>The Esc menu's rows, top to bottom with Resume last, for the role.</summary>
    string[] NetMenuRows() => Net?.Role switch
    {
        NetRole.Host => ["MpPlayersButton", "SaveGameButton", "OptionsButton", "MpStopButton", "ExitGameButton", "ResumeButton"],
        NetRole.Client => ["MpPlayersButton", "OptionsButton", "MpLeaveButton", "ExitGameButton", "ResumeButton"],
        _ => ["NewGameButton", "SaveGameButton", "LoadGameButton", "OptionsButton", "MpOpenButton", "ExitGameButton", "ResumeButton"],
    };

    void BindNetMenu(UiBindings b)
    {
        var rows = NetMenuRows();
        netMenuRects.Clear();
        var layout = faithfulScreens.Layout("menu");
        var panel = layout.Find(EscPanel);
        // A modded layout missing a reference widget keeps its own rows: hide and add only, stacking the decorated rows under Exit.
        bool known = panel is { RealPosition: true } && EscButtons.All(n => layout.Find(n) is not null);
        foreach (string name in EscButtons) if (!rows.Contains(name)) b.Hidden.Add(name);
        if (!known)
        {
            var below = layout.Find("ExitGameButton") is { } exit ? exit.Position.RelativeTo(new(0, 0, Width, Height), exit.RealPosition) : new UiRect(Width * 0.45f, Height * 0.34f, Width * 0.1f, Height * 0.04f);
            int stacked = 0;
            foreach (string name in rows.Where(NetMenuCaptions.ContainsKey))
                netMenuRects[name] = below with { Y = below.Y + below.Height * 1.1f * ++stacked };
            return;
        }
        var original = panel!.Position.RelativeTo(new(0, 0, Width, Height), true);
        float height = original.Height * (EscFirst + (rows.Length - 2) * EscPitch + EscResumeGap + EscHeight + EscBottom);
        var shown = new UiRect(original.X, original.Y + (original.Height - height) / 2, original.Width, height);
        b.Positions[EscPanel] = shown;
        for (int i = 0; i < rows.Length; i++)
        {
            float top = EscFirst + (i == rows.Length - 1 ? (rows.Length - 2) * EscPitch + EscResumeGap : i * EscPitch);
            var at = new UiRect(shown.X + original.Width * 0.125f, shown.Y + original.Height * top, original.Width * 0.75f, original.Height * EscHeight);
            if (NetMenuCaptions.ContainsKey(rows[i])) netMenuRects[rows[i]] = at; else b.Positions[rows[i]] = at;
        }
    }

    void AddNetMenuContent(List<UiControl> controls)
    {
        foreach (var (name, at) in netMenuRects)
            controls.Add(new(name, "Button", at, NetMenuCaptions[name], name, "Kenshi_Button1Skin", true,
                new Dictionary<string, string> { ["FontName"] = "Kenshi_PaintedTextFont_Medium", ["TextAlign"] = "Center" }, default, Pickable: true));
    }

    /// <summary>Hands <paramref name="request"/> to the game host, or tells the player nobody handles it.</summary>
    void Send(MultiplayerRequest request)
    {
        if (!Request(request)) Notice("Multiplayer is not available in this build.", true);
    }

    /// <summary>An Esc menu press: true when a multiplayer row (or the multiplayer meaning of Exit) took it.</summary>
    bool NetMenuAction(MyGuiEvent e)
    {
        switch (e.Widget)
        {
            case "MpOpenButton": OpenMultiplayer(runningGame: true); return true;
            case "MpPlayersButton":
                faithfulScreens.Close("menu");
                playersSelected = -1;
                faithfulScreens.Open("players");
                return true;
            case "MpStopButton":
                Confirm("STOP HOSTING", "Stop hosting? The other players are disconnected; your game goes on.", () => Send(new StopHosting()), "STOP");
                return true;
            case "MpLeaveButton":
                Confirm("DISCONNECT", "Leave the game and return to the main menu?", () => Send(new LeaveGame(false)), "DISCONNECT");
                return true;
            case "ExitGameButton" when Net?.Role == NetRole.Client:
                Confirm("EXIT", "Leave the game and quit?", () => Send(new LeaveGame(true)), "QUIT");
                return true;
            case "ExitGameButton" when Net?.Role == NetRole.Host:
                Confirm("EXIT", (session.World.Player.Exists ? "Return to the main menu or quit the game? Unsaved progress will be lost." : "Return to the main menu or quit the game?") + " The other players are disconnected.",
                    () => ScreenAction?.Invoke(new("menu", "MainMenuButton", "main-menu")), "MAIN MENU", ("QUIT", () => ScreenAction?.Invoke(new("menu", "ExitGameButton", "exit"))));
                return true;
            default: return false;
        }
    }

    // ---- players window

    /// <summary>The players window (<c>players</c>, modal, centred): the roster with a Kick and a Ban button for the host (fractions of the parent).</summary>
    const string PlayersLayout = """
        <MyGUI type="Layout" version="3.2.0">
         <Widget type="Widget" skin="Kenshi_FloatingPanelSkin" position_real="0 0 0.3125 0.444" layer="Window" name="Root">
          <Widget type="TextBox" skin="Kenshi_TextboxPaintedText" position_real="0.01 0.012 0.98 0.09" align="HStretch Top" name="lbTitle">
           <Property key="Caption" value="PLAYERS"/><Property key="TextAlign" value="Center"/><Property key="FontName" value="Kenshi_PaintedTextFont_Large"/></Widget>
          <Widget type="MultiListBox" skin="Kenshi_MultiListBox" position_real="0.02 0.11 0.96 0.7" name="PlayersList"/>
          <Widget type="Button" skin="Kenshi_Button1" position_real="0.02 0.86 0.2 0.1" name="KickButton"><Property key="Caption" value="KICK"/></Widget>
          <Widget type="Button" skin="Kenshi_Button1" position_real="0.23 0.86 0.2 0.1" name="BanButton"><Property key="Caption" value="BAN"/></Widget>
          <Widget type="Button" skin="Kenshi_Button1" position_real="0.78 0.86 0.2 0.1" name="CloseButton"><Property key="Caption" value="CLOSE"/></Widget>
         </Widget>
        </MyGUI>
        """;

    /// <summary>The player number selected in the list, -1 for none.</summary>
    int playersSelected = -1;

    void RegisterPlayersScreen() => faithfulScreens.Register("players", MyGuiReader.ParseLayout(PlayersLayout), modal: true, centreRoot: true);

    /// <summary>The rows of the list: everyone not away, in roster order.</summary>
    IEnumerable<RosterEntry> PlayersShown() => Net?.Players.Where(p => p.State != NetPlayerState.Away) ?? [];

    void BindPlayersScreen(UiBindings b)
    {
        var rows = PlayersShown().ToArray();
        b.Columns["PlayersList"] = [new("Player", 100, Fill: true), new("Squads", 80), new("Ping", 80), new("Status", 110)];
        b.Lists["PlayersList"] = rows.Select(p => new UiListItem(p.Player.ToString(CultureInfo.InvariantCulture), p.Name, [
            p.Name + (p.Player == LocalPlayer ? " (you)" : "") + (p.State == NetPlayerState.Host ? " · HOST" : ""),
            p.Squads.ToString(CultureInfo.InvariantCulture),
            p.State == NetPlayerState.Host || p.Player == LocalPlayer ? "—" : $"{p.RoundTripMs} ms",
            p.State == NetPlayerState.Loading ? "Joining" : ""])).ToArray();
        b.SelectedItems["PlayersList"] = Array.FindIndex(rows, p => p.Player == playersSelected);
        EnableButtons(b, "CloseButton");
        b.Properties["CloseButton"] = new Dictionary<string, string> { ["DefaultAction"] = "true" };
        if (Net?.Role != NetRole.Host) { b.Hidden.Add("KickButton"); b.Hidden.Add("BanButton"); return; }
        if (rows.FirstOrDefault(p => p.Player == playersSelected) is { State: not NetPlayerState.Host }) EnableButtons(b, "KickButton", "BanButton");
        else { b.Disabled.Add("KickButton"); b.Disabled.Add("BanButton"); }
    }

    /// <summary>Each player's row text in the player's colour (the roster's colour index).</summary>
    void AddPlayersContent(List<UiControl> controls)
    {
        for (int i = 0; i < controls.Count; i++)
        {
            var c = controls[i];
            if (c.Properties.GetValueOrDefault("ListOwner") != "PlayersList" || !c.Name.Contains("@column0", StringComparison.Ordinal)
                || !int.TryParse(c.Argument, NumberStyles.Integer, CultureInfo.InvariantCulture, out int player)) continue;
            var properties = new Dictionary<string, string>(c.Properties, StringComparer.Ordinal) { ["TextColour"] = PlayerColours.MyGui(PlayerInfo(player)?.Colour ?? -1) };
            controls[i] = c with { Properties = properties };
        }
    }

    void PlayersAction(MyGuiEvent e)
    {
        if (e.Widget == "PlayersList" && e.Action == "select")
        {
            playersSelected = int.TryParse(e.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int chosen) ? chosen : -1;
            return;
        }
        if (e.Widget == "CloseButton") { faithfulScreens.Close("players"); return; }
        if (Net is not { Role: NetRole.Host } net || PlayersShown().FirstOrDefault(p => p.Player == playersSelected) is not { State: not NetPlayerState.Host } who) return;
        int number = who.Player;
        if (e.Widget == "KickButton") Confirm("KICK PLAYER", $"Kick {who.Name} from the game?", () => net.Kick(number, ""));
        else if (e.Widget == "BanButton") Confirm("BAN PLAYER", $"Ban {who.Name}? They cannot rejoin while you host.", () => net.Ban(number));
    }
}
