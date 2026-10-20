using Meitou.Data.Ui;
using Meitou.Net.Session;
using Meitou.Simulation;
using Meitou.Ui;

namespace Meitou.Game;

/// <summary>
/// The multiplayer plumbing of the interface (docs/meitou-ui.md "Multiplayer"): the session view the game host sets, the requests it handles, the
/// own-character rule and the per-frame and per-screen dispatch into the multiplayer partials (<c>PlayerInterface.{Multiplayer,Chat,Players,NetStatus,NetMenu}.cs</c>).
/// With <see cref="Net"/> null (single-player) every hook is inert and <see cref="Mine"/> is <see cref="CharacterSnapshot.IsPlayer"/>, so nothing changes.
/// </summary>
sealed partial class PlayerInterface
{
    /// <summary>The session the game hosts or joined (the game host sets it); null in single-player.</summary>
    public IMultiplayerView? Net { get; set; }
    /// <summary>Raised for what the interface asks of the game host: host, join, cancel, stop hosting, leave.</summary>
    public event Action<MultiplayerRequest>? MultiplayerRequested;
    /// <summary>The player's settings (name, id, host settings, recent games); the game host sets it.</summary>
    internal UserConfig? Config { get; set; }
    /// <summary>The PlayerList action is held (the game host's control tick sets it).</summary>
    public bool PlayerListHeld { get; set; }
    /// <summary>Recomputes the modal pause after the clock authority changed (hosting started or stopped with a window open).</summary>
    public void RefreshMenuPause() => UpdateMenuPause();

    bool Multiplayer => Net is { Role: not NetRole.None };
    int LocalPlayer => Net?.LocalPlayer ?? 0;
    /// <summary>A character this viewer commands (<see cref="CharacterSnapshot.Owner"/> is the local player); in single-player every player character, as before.</summary>
    bool Mine(CharacterSnapshot c) => c.IsPlayer && (!Multiplayer || c.Owner == LocalPlayer);
    /// <summary>A teammate's character: of the shared player faction, commanded by another player.</summary>
    bool OtherPlayers(CharacterSnapshot c) => c.IsPlayer && !Mine(c);
    bool OtherPlayers(CharacterId id) => Multiplayer && session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == id) is { } c && OtherPlayers(c);
    /// <summary>The roster entry of player <paramref name="number"/>; null when unknown (shown as "Player n" in <see cref="PlayerColours.Absent"/>).</summary>
    RosterEntry? PlayerInfo(int number) => Net?.Players.FirstOrDefault(p => p.Player == number);

    /// <summary>Hands <paramref name="request"/> to the game host. False when nobody handles it: the caller shows "Multiplayer is not available in this build.".</summary>
    bool Request(MultiplayerRequest request)
    {
        if (MultiplayerRequested is null) return false;
        MultiplayerRequested(request);
        return true;
    }

    /// <summary>A line for the Overview's Messages tab (the last 200 kept).</summary>
    void LogMessage(string text)
    {
        uiMessages.Add(text);
        if (uiMessages.Count > 200) uiMessages.RemoveAt(0);
    }

    static void EnableButtons(UiBindings b, params string[] names)
    {
        foreach (var name in names) { b.Disabled.Remove(name); b.Actions[name] = name; }
    }

    /// <summary>The alert for an ended session: DISCONNECTED once it reached play, else CONNECTION FAILED; the text is the host's or the transport's reason (docs/net.md).</summary>
    static (string Title, string Text) EndText(NetDisconnect end, bool reachedPlay) =>
        (reachedPlay ? "DISCONNECTED" : "CONNECTION FAILED",
         end.Reason.Length > 0 ? end.Reason : end.Code == RejectCode.Left ? "You left the game." : "The connection to the host ended.");

    NetRole lastRole;

    /// <summary>Once a frame from <see cref="Draw"/>: drains the session's events and runs the multiplayer partials' frame steps with the role of the last frame.</summary>
    void PollNet()
    {
        var role = Net?.Role ?? NetRole.None;
        var from = lastRole;
        lastRole = role;
        var events = Net?.TakeEvents() ?? [];
        MultiplayerScreenFrame();
        ChatFrame(from, events);
        StatusFrame(from, events);
    }

    /// <summary>Registers the Meitou multiplayer layouts (<c>multiplayer</c>, <c>chat</c>, <c>players</c>) after the original ones.</summary>
    void RegisterNetScreens()
    {
        RegisterMultiplayerScreen();
        RegisterChatScreen();
        RegisterPlayersScreen();
    }

    void BindNetScreen(string id, UiBindings b)
    {
        if (id == "multiplayer") BindMultiplayerScreen(b);
        else if (id == "chat") BindChatScreen(b);
        else BindPlayersScreen(b);
    }

    void AddNetContent(string id, List<UiControl> controls)
    {
        if (id == "menu") AddNetMenuContent(controls);
        else if (id == "players") AddPlayersContent(controls);
    }

    void NetScreenAction(MyGuiEvent e)
    {
        if (e.Screen == "multiplayer") MultiplayerAction(e);
        else if (e.Screen == "chat") ChatAction(e);
        else PlayersAction(e);
    }
}
