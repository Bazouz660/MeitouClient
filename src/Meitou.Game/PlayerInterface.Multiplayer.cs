using System.Diagnostics;
using System.Globalization;
using Meitou.Data.Ui;
using Meitou.Net.Session;
using Meitou.Net.Transport.Steam;
using Meitou.Ui;

namespace Meitou.Game;

/// <summary>
/// The Multiplayer window (docs/meitou-ui.md "Title entry and Multiplayer window"): a MyGUI layout parsed from <see cref="MultiplayerLayout"/> with installed skins and
/// fonts only, shown in both UI modes. The Host tab starts a new game, loads a save or (from the Esc menu) opens the running game; the Join tab connects to
/// <see cref="JoinAddress"/> or a Steam friend hosting (<see cref="Steam"/>). Both raise <see cref="MultiplayerRequested"/> and persist the player's choices in <see cref="Config"/>.
/// </summary>
sealed partial class PlayerInterface
{
    /// <summary>Positions are fractions of the parent. Skins: Kenshi_FloatingPanelSkin, Kenshi_Button1/2, Kenshi_EditBox, Kenshi_MultiListBox, Kenshi_TickBoxSkin, Kenshi_SpeedArrow*Skin.</summary>
    const string MultiplayerLayout = """
        <MyGUI type="Layout" version="3.2.0">
         <Widget type="Widget" skin="Kenshi_FloatingPanelSkin" position_real="0 0 0.40625 0.555556" layer="Window" name="Root">
          <Widget type="TextBox" skin="Kenshi_TextboxPaintedText" position_real="0.01 0.012 0.98 0.075" align="HStretch Top" name="lbTitle">
           <Property key="Caption" value="MULTIPLAYER"/><Property key="TextAlign" value="Center"/><Property key="FontName" value="Kenshi_PaintedTextFont_Large"/></Widget>
          <Widget type="Button" skin="Kenshi_Button2" position_real="0.02 0.1 0.18 0.06" name="HostTabButton"><Property key="Caption" value="HOST"/></Widget>
          <Widget type="Button" skin="Kenshi_Button2" position_real="0.21 0.1 0.18 0.06" name="JoinTabButton"><Property key="Caption" value="JOIN"/></Widget>
          <Widget type="TextBox" skin="Kenshi_GenericTextBoxFlat" position_real="0.44 0.1 0.13 0.06" name="lbName"><Property key="Caption" value="Name"/><Property key="TextAlign" value="Right VCenter"/></Widget>
          <Widget type="EditBox" skin="Kenshi_EditBox" position_real="0.58 0.1 0.4 0.06" name="NameEditBox"><Property key="MaxTextLength" value="24"/></Widget>
          <Widget type="Widget" skin="PanelEmpty" position_real="0.02 0.18 0.96 0.66" name="HostPanel">
           <Widget type="MultiListBox" skin="Kenshi_MultiListBox" position_real="0 0 0.6 1" name="GamesList"/>
           <Widget type="TextBox" skin="Kenshi_GenericTextBoxFlat" position_real="0.62 0 0.18 0.1" name="lbPort"><Property key="Caption" value="Port"/></Widget>
           <Widget type="EditBox" skin="Kenshi_EditBox" position_real="0.81 0 0.19 0.1" name="PortEditBox"><Property key="MaxTextLength" value="5"/></Widget>
           <Widget type="TextBox" skin="Kenshi_GenericTextBoxFlat" position_real="0.62 0.12 0.18 0.1" name="lbMaxPlayers"><Property key="Caption" value="Players"/></Widget>
           <Widget type="Button" skin="Kenshi_SpeedArrowLeftSkin" position_real="0.81 0.135 0.04 0.07" name="MaxPlayersLeft"/>
           <Widget type="TextBox" skin="Kenshi_GenericTextBoxFlat" position_real="0.85 0.12 0.11 0.1" name="MaxPlayersText"><Property key="TextAlign" value="Center"/></Widget>
           <Widget type="Button" skin="Kenshi_SpeedArrowRightSkin" position_real="0.96 0.135 0.04 0.07" name="MaxPlayersRight"/>
           <Widget type="TextBox" skin="Kenshi_GenericTextBoxFlat" position_real="0.62 0.24 0.18 0.1" name="lbHostPassword"><Property key="Caption" value="Password"/></Widget>
           <Widget type="EditBox" skin="Kenshi_EditBox" position_real="0.81 0.24 0.19 0.1" name="HostPasswordEditBox"><Property key="Password" value="true"/><Property key="MaxTextLength" value="32"/></Widget>
           <Widget type="TextBox" skin="Kenshi_GenericTextBoxFlat" position_real="0.62 0.36 0.32 0.1" name="lbClientsTime"><Property key="Caption" value="Clients control time"/></Widget>
           <Widget type="Button" skin="Kenshi_TickBoxSkin" position_real="0.95 0.375 0.045 0.07" name="ClientsTimeTickBox"/>
           <Widget type="TextBox" skin="Kenshi_GenericTextBoxFlat" position_real="0.62 0.48 0.32 0.1" name="lbCheats"><Property key="Caption" value="Allow cheats"/></Widget>
           <Widget type="Button" skin="Kenshi_TickBoxSkin" position_real="0.95 0.495 0.045 0.07" name="CheatsTickBox"/>
           <Widget type="Widget" skin="Kenshi_GenericTextBox" position_real="0.62 0.61 0.38 0.39"><Widget type="Widget" skin="PanelEmpty" position_real="0.027 0.036 0.946 0.934" align="Stretch" name="InfoPanel"/></Widget>
          </Widget>
          <Widget type="Widget" skin="PanelEmpty" position_real="0.02 0.18 0.96 0.66" name="JoinPanel">
           <Widget type="TextBox" skin="Kenshi_GenericTextBoxFlat" position_real="0 0 0.2 0.1" name="lbAddress"><Property key="Caption" value="Address"/></Widget>
           <Widget type="EditBox" skin="Kenshi_EditBox" position_real="0.21 0 0.55 0.1" name="AddressEditBox"><Property key="MaxTextLength" value="255"/></Widget>
           <Widget type="TextBox" skin="Kenshi_GenericTextBoxFlat" position_real="0 0.12 0.2 0.1" name="lbJoinPassword"><Property key="Caption" value="Password"/></Widget>
           <Widget type="EditBox" skin="Kenshi_EditBox" position_real="0.21 0.12 0.55 0.1" name="JoinPasswordEditBox"><Property key="Password" value="true"/><Property key="MaxTextLength" value="32"/></Widget>
           <Widget type="TextBox" skin="Kenshi_GenericTextBoxFlat" position_real="0 0.25 0.4 0.08" name="lbRecent"><Property key="Caption" value="Recent games"/></Widget>
           <Widget type="MultiListBox" skin="Kenshi_MultiListBox" position_real="0 0.34 1 0.3" name="RecentList"/>
           <Widget type="TextBox" skin="Kenshi_GenericTextBoxFlat" position_real="0 0.66 1 0.08" name="lbSteamFriends"/>
           <Widget type="MultiListBox" skin="Kenshi_MultiListBox" position_real="0 0.75 1 0.25" name="SteamFriendsList"/>
          </Widget>
          <Widget type="TextBox" skin="Kenshi_TextboxStandardText" position_real="0.02 0.86 0.6 0.12" name="StatusText"><Property key="WordWrap" value="true"/><Property key="TextAlign" value="Left VCenter"/></Widget>
          <Widget type="Button" skin="Kenshi_Button1" position_real="0.64 0.88 0.17 0.09" name="ConnectButton"/>
          <Widget type="Button" skin="Kenshi_Button1" position_real="0.82 0.88 0.17 0.09" name="CancelButton"><Property key="Caption" value="CANCEL"/></Widget>
         </Widget>
        </MyGUI>
        """;

    const string NewGameRow = "<new>";   // not a valid folder name, so never a save
    /// <summary>The status line's error ink (a light red: the window's panel is dark).</summary>
    const string MultiplayerErrorInk = "1 0.45 0.38";

    enum MpTab { Host, Join }
    MpTab mpTab;
    /// <summary>Opened from the Esc menu: the tab hosts the running game.</summary>
    bool mpRunning;
    /// <summary>The selected games-list row: <see cref="NewGameRow"/> or a save folder.</summary>
    string mpGame = NewGameRow;
    int mpMaxPlayers = MultiplayerDefaults.MaxPlayers;
    bool mpClientsTime = true, mpCheats;
    (string Text, bool Error) mpStatus;
    /// <summary>The join in progress (the window shows its status and only Cancel works).</summary>
    JoinGame? mpJoining;
    long mpJoinStarted;
    /// <summary>The settings chosen for a new game still to be started from the new-game panel (<see cref="BeginHostedNewGame"/>).</summary>
    HostSettings? hostIntent;
    /// <summary>When the Steam friends list was last read (0: not since the window opened, so the next read asks Steam for their rich presence).</summary>
    long mpSteamPolled;

    /// <summary>Steam for the Join tab's friends list; set by the game host (null: a build without it, the list says Steam is off).</summary>
    public ISteamDirectory? Steam { get; set; }

    void RegisterMultiplayerScreen() => faithfulScreens.Register("multiplayer", MyGuiReader.ParseLayout(MultiplayerLayout), modal: true, centreRoot: true);

    /// <summary>The user name filtered to what a player name allows (letters, digits, space, '-' and '_'), cut to the limit; "Drifter" when nothing is left.</summary>
    static string DefaultName()
    {
        string name = new string(Environment.UserName.Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_').ToArray()).Trim();
        if (name.Length > MultiplayerDefaults.NameLength) name = name[..MultiplayerDefaults.NameLength].TrimEnd();
        return name.Length > 0 ? name : "Drifter";
    }

    /// <summary>Opens the Multiplayer window: from the title, or with <paramref name="runningGame"/> from the Esc menu to host the game running.</summary>
    public void OpenMultiplayer(bool runningGame)
    {
        RefreshSlots();
        var config = Config;
        mpRunning = runningGame;
        mpGame = NewGameRow;
        mpMaxPlayers = Math.Clamp(config?.HostMaxPlayers ?? MultiplayerDefaults.MaxPlayers, 2, MultiplayerDefaults.MaxPlayersLimit);
        mpClientsTime = config?.HostClientsControlTime ?? true;
        mpCheats = config?.HostAllowCheats ?? false;
        mpStatus = ("", false);
        mpJoining = null;
        mpSteamPolled = 0;
        var recent = config?.RecentGames ?? [];
        mpTab = runningGame || recent.Count == 0 ? MpTab.Host : MpTab.Join;
        faithfulScreens.Open("multiplayer");
        faithfulScreens.SetText("multiplayer", "NameEditBox", config?.PlayerName is { Length: > 0 } saved ? saved : DefaultName());
        faithfulScreens.SetText("multiplayer", "PortEditBox", (config?.HostPort ?? MultiplayerDefaults.Port).ToString(CultureInfo.InvariantCulture));
        faithfulScreens.SetText("multiplayer", "AddressEditBox", recent.Count > 0 ? recent[0].Address : "");
        faithfulScreens.SetText("multiplayer", "HostPasswordEditBox", "");
        faithfulScreens.SetText("multiplayer", "JoinPasswordEditBox", "");
        faithfulScreens.SetSelected("multiplayer", "GamesList", runningGame ? -1 : 0);
        faithfulScreens.SetSelected("multiplayer", "RecentList", -1);
        faithfulScreens.SetSelected("multiplayer", "SteamFriendsList", -1);
        if (faithfulScreens.GetText("multiplayer", "NameEditBox").Length == 0) faithfulScreens.FocusBox("multiplayer", "NameEditBox");
        UpdateMenuPause();
    }

    void BindMultiplayerScreen(UiBindings b)
    {
        bool joining = mpJoining is not null, host = mpTab == MpTab.Host;
        if (joining)
        {
            EnableButtons(b, "CancelButton");
            foreach (var w in faithfulScreens.Layout("multiplayer").Widgets.Where(w => w.Type is "EditBox" or "MultiListBox")) b.Disabled.Add(w.Name);
        }
        else EnableButtons(b, "HostTabButton", "ConnectButton", "CancelButton", "MaxPlayersLeft", "MaxPlayersRight", "ClientsTimeTickBox", "CheatsTickBox");
        if (!joining && !mpRunning) EnableButtons(b, "JoinTabButton");
        if (mpRunning) b.Hidden.Add("JoinTabButton");
        b.Hidden.Add(host ? "JoinPanel" : "HostPanel");
        if (mpRunning) b.Hidden.Add("GamesList");
        static Dictionary<string, string> Checked(bool on, string? ink = null) => new(ink is null ? [] : [new("TextColour", ink)]) { ["Checked"] = on ? "true" : "false", ["StateSelected"] = on ? "true" : "false" };
        // The active tab reads in white (the plate alone barely differs from the inactive one).
        b.Properties["HostTabButton"] = Checked(host, host ? "1 1 1" : null);
        b.Properties["JoinTabButton"] = Checked(!host, host ? null : "1 1 1");
        b.Properties["ClientsTimeTickBox"] = Checked(mpClientsTime);
        b.Properties["CheatsTickBox"] = Checked(mpCheats);
        b.Text["ConnectButton"] = mpRunning ? "OPEN" : host ? "HOST" : "JOIN";
        b.Properties["ConnectButton"] = new Dictionary<string, string> { ["DefaultAction"] = "true" };
        b.Text["MaxPlayersText"] = mpMaxPlayers.ToString(CultureInfo.InvariantCulture);

        // The games list: a new game, then the load list's saves.
        b.Columns["GamesList"] = [new("Game", 170, Fill: true), new("Date", 150)];
        var rows = new List<UiListItem> { new(NewGameRow, "New game", ["New game", ""]) };
        rows.AddRange(slots.Select(s => new UiListItem(s.Folder, s.Name, [$"{s.Name}   [{(s.Writable ? "Meitou" : "Kenshi")}]", s.ModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)])));
        b.Lists["GamesList"] = rows;
        b.SelectedItems["GamesList"] = rows.FindIndex(r => r.Id == mpGame);
        string info = mpRunning ? "Your game goes on while the others join."
            : mpGame == NewGameRow ? "Start a new game; you choose the start next."
            : DescribeSlot(slots.FirstOrDefault(s => s.Folder == mpGame)).Text;
        b.Text["InfoPanel"] = info;
        b.Properties["InfoPanel"] = new Dictionary<string, string> { ["WordWrap"] = "true", ["TextAlign"] = "Left Top", ["TextColour"] = MyGuiColours.InkText, ["TextPadding"] = "8 8 8 8" };

        b.Columns["RecentList"] = [new("Address", 170, Fill: true), new("Host", 150), new("Last joined", 140)];
        b.Lists["RecentList"] = (Config?.RecentGames ?? []).Select(g => new UiListItem(g.Address, g.Address,
            [g.Address, g.Host, g.LastJoinedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)])).ToArray();

        // Steam friends hosting: name and players; selecting one puts steam:<id> in the address box.
        string? steamOff = Steam is null ? "not in this build" : Steam.Off;
        b.Text["lbSteamFriends"] = steamOff is null ? "Steam friends hosting" : $"Steam friends hosting: Steam is off ({steamOff})";
        b.Columns["SteamFriendsList"] = [new("Friend", 200, Fill: true), new("Players", 160)];
        b.Lists["SteamFriendsList"] = (Steam?.Hosts ?? []).Select(h => new UiListItem(SteamPresence.Address(h.Id), h.Name,
            [h.Name, h.Compatible ? $"{h.Present}/{h.Max}" : $"{h.Present}/{h.Max} (other version)"])).ToArray();

        b.Text["StatusText"] = mpStatus.Text;
        b.Properties["StatusText"] = new Dictionary<string, string> { ["TextColour"] = mpStatus.Error ? MultiplayerErrorInk : "0.93 0.9 0.8" };
    }

    void MultiplayerAction(MyGuiEvent e)
    {
        if (e.Action == "text") return;
        if (mpJoining is not null)
        {
            if (e.Widget == "CancelButton") CancelMultiplayerJoin();
            return;
        }
        switch (e.Widget)
        {
            case "HostTabButton": mpTab = MpTab.Host; break;
            case "JoinTabButton" when !mpRunning: mpTab = MpTab.Join; break;
            case "MaxPlayersLeft": mpMaxPlayers = Math.Max(2, mpMaxPlayers - 1); break;
            case "MaxPlayersRight": mpMaxPlayers = Math.Min(MultiplayerDefaults.MaxPlayersLimit, mpMaxPlayers + 1); break;
            case "ClientsTimeTickBox": mpClientsTime = !mpClientsTime; break;
            case "CheatsTickBox": mpCheats = !mpCheats; break;
            case "GamesList" when e.Action == "select": mpGame = e.Text; break;
            case "RecentList" when e.Action == "select": faithfulScreens.SetText("multiplayer", "AddressEditBox", e.Text); break;
            case "SteamFriendsList" when e.Action == "select": faithfulScreens.SetText("multiplayer", "AddressEditBox", e.Text); break;
            case "CancelButton":
                if (ValidName(faithfulScreens.GetText("multiplayer", "NameEditBox")) is { } name && Config is { } config) { config.PlayerName = name; config.Save(); }
                faithfulScreens.Close("multiplayer");
                break;
            case "ConnectButton": Connect(); break;
        }
    }

    /// <summary>The trimmed name when it is 1 to 24 characters without control characters and does not start with '/' or '#'; else null.</summary>
    static string? ValidName(string text)
    {
        string name = text.Trim();
        return name.Length is >= 1 and <= MultiplayerDefaults.NameLength && !name.Any(char.IsControl) && name[0] is not ('/' or '#') ? name : null;
    }

    void Fail(string text) => mpStatus = (text, true);

    void Connect()
    {
        if (ValidName(faithfulScreens.GetText("multiplayer", "NameEditBox")) is not { } name) { Fail("Enter a name of 1 to 24 characters."); return; }
        bool host = mpTab == MpTab.Host;
        HostSettings? settings = null;
        string folder = "";
        string address = "", joinPassword = "";
        int port = 0;
        if (host)
        {
            if (!int.TryParse(faithfulScreens.GetText("multiplayer", "PortEditBox").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is < 1024 or > 65535)
            { Fail("The port must be a number from 1024 to 65535."); return; }
            if (!mpRunning && mpGame != NewGameRow)
            {
                if (slots.All(s => s.Folder != mpGame)) { Fail("Choose a game to host."); return; }
                folder = mpGame;
            }
            settings = new(port, mpMaxPlayers, faithfulScreens.GetText("multiplayer", "HostPasswordEditBox"), mpClientsTime, mpCheats);
        }
        else if (SteamPresence.TryParseAddress(faithfulScreens.GetText("multiplayer", "AddressEditBox"), out ulong steamHost))
        {
            if ((Steam is null ? "not in this build" : Steam.Off) is { } off) { Fail($"Steam is off ({off}): join by address."); return; }
            address = SteamPresence.Address(steamHost);
            joinPassword = faithfulScreens.GetText("multiplayer", "JoinPasswordEditBox");
        }
        else
        {
            if (!JoinAddress.TryParse(faithfulScreens.GetText("multiplayer", "AddressEditBox"), MultiplayerDefaults.Port, out address, out port))
            { Fail($"Enter an address such as 192.168.1.20:{MultiplayerDefaults.Port}, or choose a Steam friend."); return; }
            joinPassword = faithfulScreens.GetText("multiplayer", "JoinPasswordEditBox");
        }
        if (Config is { } config)
        {
            config.PlayerName = name;
            if (settings is not null) { config.HostPort = port; config.HostMaxPlayers = mpMaxPlayers; config.HostClientsControlTime = mpClientsTime; config.HostAllowCheats = mpCheats; }
            config.EnsurePlayerId();
            config.Save();
        }
        if (!host)
        {
            mpJoining = new(address, port, joinPassword);
            mpJoinStarted = Stopwatch.GetTimestamp();
            mpStatus = ($"Connecting to {JoinName(mpJoining)}…", false);
            if (!Request(mpJoining)) { mpJoining = null; Fail("Multiplayer is not available in this build."); }
            return;
        }
        if (mpRunning)
        {
            if (Request(new HostGame(settings!, null, null))) faithfulScreens.Close("multiplayer"); else Fail("Multiplayer is not available in this build.");
        }
        else if (folder.Length == 0)
        {
            if (MultiplayerRequested is null) { Fail("Multiplayer is not available in this build."); return; }
            hostIntent = settings;
            faithfulScreens.Close("multiplayer");
            faithfulScreens.Open("new-game");
        }
        else
        {
            if (MultiplayerRequested is null) { Fail("Multiplayer is not available in this build."); return; }
            var request = new HostGame(settings!, null, folder);
            faithfulScreens.Close("multiplayer");
            if (session.World.Player.Exists) Confirm("HOST GAME", "Load this save and host it? Unsaved progress will be lost.", () => Request(request));
            else Request(request);
        }
    }

    /// <summary>"host:port" for a status line or a recent-games entry (an IPv6 host in brackets); a Steam host as <c>steam:&lt;id&gt;</c>.</summary>
    static string JoinText(JoinGame join) => SteamPresence.TryParseAddress(join.Host, out _) ? join.Host
        : join.Host.Contains(':') ? $"[{join.Host}]:{join.Port}" : $"{join.Host}:{join.Port}";

    /// <summary>The status line's name of a join: the Steam friend's name when the list knows it, else <see cref="JoinText"/>.</summary>
    string JoinName(JoinGame join) => SteamPresence.TryParseAddress(join.Host, out ulong id) && Steam?.Hosts.FirstOrDefault(h => h.Id == id) is { Name.Length: > 0 } friend
        ? friend.Name : JoinText(join);

    void CancelMultiplayerJoin()
    {
        Request(new CancelJoin());
        mpJoining = null;
        mpStatus = ("Cancelled.", false);
    }

    /// <summary>The new-game Begin button: true when the start was taken to host it (the Multiplayer window chose New game).</summary>
    bool BeginHostedNewGame(string startId)
    {
        if (hostIntent is not { } settings) return false;
        hostIntent = null;
        Request(new HostGame(settings, startId, null));
        return true;
    }

    /// <summary>Every frame: forgets an abandoned new-game intent and follows the join in progress through the session's phases.</summary>
    void MultiplayerScreenFrame()
    {
        if (hostIntent is not null && !faithfulScreens.IsOpen("new-game")) hostIntent = null;
        // The friends list follows their rich presence every 2 s while the Join tab shows.
        if (Steam is { Off: null } steam && mpTab == MpTab.Join && faithfulScreens.IsOpen("multiplayer")
            && (mpSteamPolled == 0 || Stopwatch.GetElapsedTime(mpSteamPolled).TotalSeconds >= 2))
        {
            steam.Refresh(request: mpSteamPolled == 0);
            mpSteamPolled = Stopwatch.GetTimestamp();
        }
        if (mpJoining is not { } join) return;
        var phase = Net?.Phase ?? NetPhase.Idle;
        if (phase == NetPhase.Playing)
        {
            string hostName = Net?.Players.FirstOrDefault(p => p.State == NetPlayerState.Host)?.Name ?? "";
            if (Config is { } config) { config.RememberGame(JoinText(join), hostName, DateTime.UtcNow); config.Save(); }
            mpJoining = null;
            return;
        }
        if (!faithfulScreens.IsOpen("multiplayer"))
        {
            // Closed by Esc while connecting: a cancel. A world load covers the screen and closes the frontend itself: the join goes on.
            if (phase != NetPhase.Loading) Request(new CancelJoin());
            mpJoining = null;
            return;
        }
        int seconds = (int)Stopwatch.GetElapsedTime(mpJoinStarted).TotalSeconds;
        switch (phase)
        {
            case NetPhase.Joining: mpStatus = ("Joining…", false); break;
            case NetPhase.Loading: mpStatus = ("Loading the world…", false); break;
            case NetPhase.Ended:
                mpStatus = (Net?.Ended is { } end ? EndText(end, reachedPlay: false).Text : "The connection failed.", true);
                mpJoining = null;
                break;
            default: mpStatus = ($"Connecting to {JoinName(join)}… {seconds} s", false); break;
        }
    }
}
