# D5 Multiplayer UI: implementation spec

Scope: `src/Meitou.Game` (PlayerInterface partials, GameHost glue, UserConfig), `src/Meitou.Ui` (one method), `src/Meitou.Engine/Input` (two actions).
In single-player (`Net == null` or `Net.Role == None`) every code path below is inert, and behaviour stays as it is today except for one added Esc menu row (MULTIPLAYER, WP-D, step D3).
No simulation file is touched, so no hash can move.

## 0. Decisions

- **Title entry: reuse `ImportGameButton` in both modes**, captioned `MULTIPLAYER` and enabled. Why:
  - **Verified** by reading the installed `Kenshi_MainMenu.layout`: the column is Continue, New, Load, Import, Options, Credits, Exit, spaced evenly (pitch 0.111). There is no free slot.
  - A decorated control would need a hard-coded position that a GUI mod moving the menu cannot follow. The real widget moves with the layout and keeps its skin, font, hover, keyboard ring and (Meitou) plate art.
  - Import has no backend (parity ledger: "Import/reset squad positions … needs a separate backend"). Today it is greyed in Faithful mode and hidden in Meitou mode, so nothing working is lost.
  - Faithful deviation: one caption. Record it under the engine choices in `docs/faithful-ui-parity.md`. If Import is ever implemented, Multiplayer moves to a decorated control cloned from `ImportGameButton`'s skin and font, and this note is revisited.
- **All new windows are MyGUI layouts.** They are XML strings parsed with `MyGuiReader.ParseLayout` and registered on `faithfulScreens`. They use only installed skins and fonts, each checked to exist in `kenshi_skins.xml`, `kenshi_templates.xml` or `kenshi_fonts.xml`:
  - skins: `Kenshi_FloatingPanelSkin`, `Kenshi_TextboxPaintedText`, `Kenshi_GenericTextBox`, `Kenshi_GenericTextBoxFlat`, `Kenshi_TextboxStandardText`, `Kenshi_Button1`, `Kenshi_Button2`, `Kenshi_EditBox`, `Kenshi_MultiListBox`, `Kenshi_TickBoxSkin`, `Kenshi_SpeedArrowLeftSkin` / `RightSkin`, `PanelEmpty`;
  - fonts: `Kenshi_PaintedTextFont_Large`, `Kenshi_StandardFont_Medium`, `Kenshi_StandardFont_MediumSmall`.

  Both UI modes show the same windows, as in-game windows do today (docs/meitou-ui.md).
- **Code-built overlays use the existing patterns.** Chat history, the player list, name tags, banners and the net indicator are `Pickable:false` decorated `UiControl`s built in `ArrangeUi`, in the style of `AddSpeech` and `AddNameTags`.
  - A decorated button uses skin `Kenshi_Button1Skin` with `FontName=Kenshi_PaintedTextFont_Medium`, `TextAlign=Center`, as `ContextMenu` does. Templates such as `Kenshi_Button1` are expanded only inside layouts.
- **Keys:** `OpenChat` = Enter, `PlayerList` = P (held).
  - Tab is `change_squad` and Grave is `select_all`. P is unbound in the original's table (docs/game/ui-input.md section 6) and is a mnemonic.
  - The sandbox reserves P only under `--sandbox`, which is not a multiplayer mode.
  - KeypadEnter stays the cinema's key and is not mapped to chat.
- **Modal windows don't pause in multiplayer:** `RequestedPause = !Multiplayer && (settingsOpen || HasModal)`.
- **Character transfer between players is out of scope.** Single-player has no character-to-character item move either (parity ledger: "character transfers remain"). Players share items through faction storage and the ground.

## 1. Contract types: new `src/Meitou.Game/Multiplayer/NetUiContract.cs` (WP-E)

```csharp
namespace Meitou.Game;
public enum NetRole { None, Host, Client }
/// <summary>A client's lifecycle; a host is Playing from the moment it hosts.</summary>
public enum NetPhase { Idle, Connecting, Joining, Downloading, Loading, Playing, Ended }
public enum NetEnd { None, Left, HostLeft, HostStopped, Kicked, Banned, TimedOut, WrongPassword, Full, VersionMismatch, ContentMismatch, NameInUse, Unreachable, Refused }
public sealed record NetPlayer(int Number, string Name, int Colour, bool IsHost, bool Joining, bool Afk, int PingMs, int Squads);
public enum NetEventKind { Joined, Left, Paused, Resumed, Speed, Chat }
/// <summary>Name is the actor's name at the time (a Left player is gone from Players); Text the chat line; Reason why a player left.</summary>
public readonly record struct NetEvent(NetEventKind Kind, int Player, string Name, string Text = "", double Speed = 0, NetEnd Reason = NetEnd.None);
public readonly record struct NetQuality(int PingMs, double SnapshotAgeSeconds, float Loss);
public readonly record struct NetRules(int Port, int MaxPlayers, bool ClientsControlTime, bool AllowCheats, bool HasPassword);
public interface IMultiplayerView
{
    NetRole Role { get; }
    NetPhase Phase { get; }
    int LocalPlayer { get; }
    /// <summary>Connected and joining players, by Number (host 0 first).</summary>
    IReadOnlyList<NetPlayer> Players { get; }
    NetRules Rules { get; }
    NetQuality Quality { get; }
    /// <summary>Downloading / Loading progress 0..1; NaN when unknown.</summary>
    float Progress { get; }
    NetEnd EndReason { get; }
    string EndDetail { get; }
    /// <summary>Who paused the shared clock; null when running or unknown.</summary>
    int? PausedBy { get; }
    bool CanControlTime(int player);
    /// <summary>Events since the last call; the UI drains it once per frame.</summary>
    IReadOnlyList<NetEvent> TakeEvents();
    void SendChat(string text);
    void RequestPause(bool paused);
    void RequestSpeed(double speed);
    /// <summary>Host only (no-op on a client).</summary>
    void Kick(int player, string reason);
    void Ban(int player);
}
public sealed record HostSettings(int Port, int MaxPlayers, string Password, bool ClientsControlTime, bool AllowCheats);
public abstract record MultiplayerRequest;
/// <summary>Both null: open the running game.</summary>
public sealed record HostGame(HostSettings Settings, string? NewGameStart, string? SaveFolder) : MultiplayerRequest;
public sealed record JoinGame(string Host, int Port, string Password) : MultiplayerRequest;
public sealed record CancelJoin() : MultiplayerRequest;
public sealed record StopHosting() : MultiplayerRequest;
/// <summary>Quit: exit the application after leaving, else back to the title.</summary>
public sealed record LeaveGame(bool Quit) : MultiplayerRequest;
public static class MultiplayerDefaults { public const int Port = 27420, MaxPlayers = 4, MaxPlayersLimit = 8, NameLength = 24, ChatLength = 200, PasswordLength = 32; }
```

`src/Meitou.Game/Multiplayer/PlayerColours.cs` (WP-E):

```csharp
/// <summary>The players' colours (docs/multiplayer.md "UI"): light enough for the dark HUD plates, shadowed over terrain; the host is 0, the single-player marker colour.</summary>
static class PlayerColours
{
    public const int Count = 8;
    // sky (MarkerPlayer of kenshi_colours.xml), amber, orchid, mint, rose, lime, peach, periwinkle: hues ~45° apart, no red (enemy) or dark green (ally)
    static readonly int[] Rgb = [0x8DCDFF, 0xF2C14E, 0xD69CF2, 0x7DDCB0, 0xF58FC0, 0xB9E36B, 0xFFAB7A, 0xA4AAFF];
    public static readonly Vector4 Absent = new(0.62f, 0.62f, 0.62f, 1);
    public static Vector4 Of(int colour);       // Absent when out of range
    public static string MyGui(int colour);     // "r g b", invariant culture, for TextColour / Colour properties
    public static Vector4 Selected(int colour); // 0: the map's SelectedColour (0.652, 0.903, 1); else Vector4.Lerp(Of(colour), Vector4.One, 0.35f) with W = 1
    /// <summary>The colour of a joining player: <paramref name="previous"/> when free and in range, else the lowest free one; -1 when all are taken. The host takes 0.</summary>
    public static int Assign(IReadOnlyCollection<int> taken, int previous = -1);
}
```

The order makes the first four the most distinct. Names are always shown, so colour is never the only cue (colour-blind players).

## WP-E: plumbing, shared-file hooks, ownership. Do this first, serially

WP-E is the only package that edits existing shared files. Each new package file below starts as a stub, so the build passes. A–D then replace only their own files.

**E1 `src/Meitou.Ui/MyGuiScreenHost.Keyboard.cs`**
- Add `static bool Is(IReadOnlyDictionary<string,string> p, string key)`.
- Change `NewBuffer(UiControl c, string text)` into `NewBuffer(IReadOnlyDictionary<string,string> properties, string text)`, and pass `c.Properties` from `FocusEdit`.
- Add the method below. It is named `FocusBox` because `Focus` is already the focus property.
  - It does not look at runtime `UiBindings` properties.
  - It must be called after `Open`, because `Open` clears the focus.

```csharp
/// <summary>Puts the typing focus on the editable EditBox <paramref name="widget"/> of the open screen <paramref name="id"/>, caret at the end, as a press would. Its mode comes from the layout's properties. False: not open, or not an editable box.</summary>
public bool FocusBox(string id, string widget)
{
    if (!open.Contains(id) || screens[id].Layout.Find(widget) is not { Type: "EditBox" } w || Is(w.Properties, "Static") || Is(w.Properties, "ReadOnly")) return false;
    var s = screens[id];
    s.Text.TryAdd(widget, w.Properties.GetValueOrDefault("Caption") ?? "");
    keyButton = null; focus = (id, widget); edit = NewBuffer(w.Properties, s.Text[widget]); edit.Key(UiEditKey.DocumentEnd);
    return true;
}
```

**E2 Bindings**
- `InputBindings.cs`: add `OpenChat` and `PlayerList` after `Highlight`, with the summaries "Multiplayer: opens the chat line (Meitou's; the game has no chat)." and "Multiplayer, held: the player list."
- In `ResetToDefaults`, add `Set(InputAction.OpenChat, Key.Enter); Set(InputAction.PlayerList, Key.P);`.
- `SettingsMenu.Descriptions.cs` `BindingHint`:
  - `OpenChat => "Open the chat line in a multiplayer game."`
  - `PlayerList => "Hold to show the players in a multiplayer game."`
- The Controls page lists the new actions on its own.

**E3 `UserConfig.cs`.** These are new keys only, so there is no version bump.

```csharp
public string? PlayerName { get; set; }
public Guid PlayerId { get; set; }                     // Empty until EnsurePlayerId
public int HostPort { get; set; } = MultiplayerDefaults.Port;
public int HostMaxPlayers { get; set; } = MultiplayerDefaults.MaxPlayers;
public bool HostClientsControlTime { get; set; } = true;
public bool HostAllowCheats { get; set; }
public List<RecentGame> RecentGames { get; set; } = [];   // newest first, at most 8, unique by Address (OrdinalIgnoreCase)
public Guid EnsurePlayerId() { if (PlayerId == Guid.Empty) PlayerId = Guid.NewGuid(); return PlayerId; }
public sealed class RecentGame { public string Address { get; set; } = ""; public string Host { get; set; } = ""; public DateTime LastJoinedUtc { get; set; } }
```

- Passwords are never stored.
- Two instances in one working directory share `PlayerId`. Run them from separate directories (the user config is read from the working directory first).

**E4 new `src/Meitou.Game/PlayerInterface.Net.cs`** (WP-E owns it)

```csharp
/// <summary>The multiplayer session the host runs or joined (D4 sets it); null in single-player.</summary>
public IMultiplayerView? Net { get; set; }
public event Action<MultiplayerRequest>? MultiplayerRequested;
internal UserConfig? Config { get; set; }
/// <summary>Held PlayerList action (the host's control tick).</summary>
public bool PlayerListHeld { get; set; }
bool Multiplayer => Net is { Role: not NetRole.None };
int LocalPlayer => Net?.LocalPlayer ?? 0;
/// <summary>A character this viewer commands. In single-player every IsPlayer one, as before.</summary>
bool Mine(CharacterSnapshot c) => c.IsPlayer && (!Multiplayer || c.Owner == LocalPlayer);
bool OtherPlayers(CharacterSnapshot c) => c.IsPlayer && !Mine(c);
NetPlayer? PlayerInfo(int number) => Net?.Players.FirstOrDefault(p => p.Number == number);
/// <summary>False when nobody handles it: the caller shows "Multiplayer is not available in this build.".</summary>
bool Request(MultiplayerRequest r) { if (MultiplayerRequested is null) return false; MultiplayerRequested(r); return true; }
void LogMessage(string text) { uiMessages.Add(text); if (uiMessages.Count > 200) uiMessages.RemoveAt(0); }
static void EnableButtons(UiBindings b, params string[] names) { foreach (var n in names) { b.Disabled.Remove(n); b.Actions[n] = n; } }
/// <summary>Readable text for an ended session (the status line and the alert).</summary>
static (string Title, string Text) EndText(NetEnd reason, string detail);
NetRole lastRole;
/// <summary>Once per frame from Draw: drains the events and runs the packages' frame steps.</summary>
void PollNet()
{
    var role = Net?.Role ?? NetRole.None; var from = lastRole; lastRole = role;
    var events = Net?.TakeEvents() ?? [];
    MultiplayerScreenFrame(); ChatFrame(from, events); StatusFrame(from, events);
}
void RegisterNetScreens() { RegisterMultiplayerScreen(); RegisterChatScreen(); RegisterPlayersScreen(); }
void BindNetScreen(string id, UiBindings b) { if (id == "multiplayer") BindMultiplayerScreen(b); else if (id == "chat") BindChatScreen(b); else BindPlayersScreen(b); }
void AddNetContent(string id, List<UiControl> c) { if (id == "menu") AddNetMenuContent(c); else if (id == "players") AddPlayersContent(c); }
void NetScreenAction(MyGuiEvent e) { if (e.Screen == "multiplayer") MultiplayerAction(e); else if (e.Screen == "chat") ChatAction(e); else PlayersAction(e); }
```

`EndText` returns:

| Reason | Title | Text |
|---|---|---|
| HostLeft | DISCONNECTED | The host left the game. |
| HostStopped | DISCONNECTED | The host stopped hosting. |
| Kicked | DISCONNECTED | You were kicked from the game.{" Reason: " + detail when detail is set} |
| Banned | DISCONNECTED | You are banned from this game. |
| TimedOut | DISCONNECTED | Lost the connection to the host. |
| WrongPassword | CONNECTION FAILED | Wrong password. |
| Full | CONNECTION FAILED | The game is full. |
| VersionMismatch | CONNECTION FAILED | The host runs another version of Meitou ({detail}). |
| ContentMismatch | CONNECTION FAILED | The host's game data or mods differ from yours: {detail} |
| NameInUse | CONNECTION FAILED | Another player in the game already uses that name. |
| Unreachable | CONNECTION FAILED | Could not reach {detail}. |
| Refused | CONNECTION FAILED | The host refused the connection. |
| Left, None | DISCONNECTED | You left the game. |

**E5 stub files.** Create them with exactly these members and empty or `false` bodies. Each package then fills in its own file.

- `PlayerInterface.Multiplayer.cs` (A): `RegisterMultiplayerScreen()`, `BindMultiplayerScreen(UiBindings)`, `MultiplayerAction(MyGuiEvent)`, `MultiplayerScreenFrame()`, `public void OpenMultiplayer(bool runningGame)`, `bool BeginHostedNewGame(string startId)`.
- `PlayerInterface.Chat.cs` (B): `RegisterChatScreen()`, `BindChatScreen(UiBindings)`, `ChatAction(MyGuiEvent)`, `ChatFrame(NetRole, IReadOnlyList<NetEvent>)`, `bool OpenChatKey(Key)`, `bool ChatEntryKey(Key, bool shift)`, `AddChatHistory(List<UiControl>)`.
- `PlayerInterface.Players.cs` (C): `AddPlayerList(List<UiControl>)`.
- `PlayerInterface.NetStatus.cs` (D): `StatusFrame(NetRole, IReadOnlyList<NetEvent>)`, `AddNetStatus(List<UiControl>)`, `BindNetHud(UiBindings)`, `bool NetTimeButton(int index)`, `public void ShowDisconnected(NetEnd reason, string detail)`, `OpenPendingAlert()`.
- `PlayerInterface.NetMenu.cs` (D): `BindNetMenu(UiBindings)`, `AddNetMenuContent(List<UiControl>)`, `bool NetMenuAction(MyGuiEvent)`, `RegisterPlayersScreen()`, `BindPlayersScreen(UiBindings)`, `AddPlayersContent(List<UiControl>)`, `PlayersAction(MyGuiEvent)`.

**E6 hooks in existing files**

| File | Change |
|---|---|
| `PlayerInterface.Ui.cs` | `LoadUi`: `RegisterNetScreens();` after the `FaithfulScreenFiles` loop. The audit test iterates `FaithfulScreenFiles` only, so it is unaffected. |
| | `BindUi`: after the `PausedPanel` line, `if (Multiplayer) BindNetHud(b);`. The roster line uses `Mine`. |
| | `ArrangeUi`: after `AddJobRemoveButtons(controls);` add `if (Multiplayer && !TitleOpen && !EditorOpen) { AddChatHistory(controls); AddNetStatus(controls); }`. After `AddInventoryDrag(controls);` add `if (Multiplayer && PlayerListHeld && !TitleOpen) AddPlayerList(controls);`. |
| | `ShowUiPanel` accepts `"multiplayer"`, which calls `OpenMultiplayer(false)`, for screenshot review. |
| `PlayerInterface.Faithful.cs` | `UpdateMenuPause` as in §0. |
| | `UiKey`: after the `CloseContextMenu` line, `if (ChatEntryKey(key, shift)) return true;`. After `if (faithfulScreens.HasModal) return true;`, `if (OpenChatKey(key)) return true;`. |
| | `FaithfulAction`: before `HandleFrontendAction`, `else if (e.Screen is "multiplayer" or "chat" or "players") { NetScreenAction(e); UpdateMenuPause(); }`. |
| | `BindFaithfulScreen`: after `BindFrontend`, `if (id is "multiplayer" or "chat" or "players") { BindNetScreen(id, b); return b; }`. |
| | `AddFaithfulContent`: first line `if (id is "menu" or "players") { AddNetContent(id, controls); return; }`. `AddMapContent` returns early for any id other than `"overview"`, so nothing is skipped. |
| | `JobDragEnd` and `RemoveJob`: `is { IsPlayer: true } lead` becomes `is { } lead && Mine(lead)`. |
| `PlayerInterface.Frontend.cs` | `Confirmation` gains `bool Alert = false`. |
| | New `void Alert(string title, string text) { confirmation = new(title, text, () => { }, "OK", null, Alert: true); faithfulScreens.Open("message"); }`. |
| | `CancelButton => confirmation?.Alert == true ? "ButtonA" : …`. The message binding for an alert enables only `ButtonA` and hides `ButtonB` and `ButtonC`. |
| | `ShowTitle` calls `OpenPendingAlert()` before `UpdateMenuPause()`. `CloseFrontend` also closes `"multiplayer"`, `"players"` and `"chat"`. |
| | `HandleFrontendAction`: the first line of the title/menu branch is `if (e.Screen == "menu" && NetMenuAction(e)) return true;`. Add `case "ImportGameButton": OpenMultiplayer(runningGame: false); break;`. |
| | new-game `BeginButton`: `if (BeginHostedNewGame(starts[startIndex].Id)) return true;` before the existing start. |
| | `BindFrontend`: `menu` calls `BindNetMenu(b)` before its `return true`. `title` does `Enable("ImportGameButton"); b.Text["ImportGameButton"] = "MULTIPLAYER";` and the Meitou-mode `Hidden.Add("ImportGameButton")` is deleted. |
| | Extract `static (string Text, bool Readable) DescribeSlot(SaveSlot? slot)` from `ReadSlotDetails`, which then calls it. |
| `PlayerInterface.Hud.cs` | `Draw`: `PollNet();` after `PollUiSounds();`. `PressButton`: first line `if (NetTimeButton(index)) return;`. |
| `PlayerInterface.cs` | `PlayerCharacters` uses `Mine`. `MouseUp`: `other = … PickWhere(p, c => !Mine(c))`, so a click on a teammate's character inspects it. `MouseDown` Alt-loot adds `&& !OtherPlayers(target)`, so other humans' downed characters are never looted from the UI. |
| `PlayerInterface.Picking.cs` | `Pick` and `InBox` use `Mine`. `PickHurtAlly` and `PickDowned` keep `IsPlayer`: treating and carrying teammates is co-op. |
| `PlayerInterface.Portraits.cs` (both `byId`), `PlayerInterface.Squads.cs:82` | Use `Mine`. `LivingPlayerCharacters` keeps `IsPlayer`, because `max faction size` counts the whole faction. Add a comment. |
| `GameHost.Interactive.cs` | Before `player.ScreenAction +=`: `player.Config = config;`. |
| | `session.Ticked`: `player.PlayerListHeld = actions.Held(InputAction.PlayerList);`. |
| | F10 branch: `if (player.Net is { Role: not NetRole.None } n && !n.Rules.AllowCheats) { player.Notice("Cheats are off in this game.", true); return; }` before toggling. |

`ContextMenu.Verbs` needs no change. Teammates are `IsPlayer`, so Talk, Trade, Surgery, Attack and Stealth KO are already left out, and First Aid, Splint, Put Down, Pick Lock (cage) and Pay bail remain.

**E7 docs**
- Add a `## UI` section to `docs/multiplayer.md`, or create that file with only this section and link it from `docs/README.md`. It covers the keys, windows, Esc menu rows, palette, chat commands and end texts, all marked as engine choices.
- `docs/game/ui-input.md`: Meitou-only multiplayer keys, Enter (chat) and P (held, player list).
- `docs/faithful-ui-parity.md` engine choices: Import → MULTIPLAYER, the added Esc rows, and modal windows not pausing in multiplayer.
- `docs/game/ui-screens.md` section 4: `PausedPanel` holds TextBox `lbPaused` (caption `PAUSED`, font `Kenshi_UI_Messages`). **Verified** by reading the installed `Kenshi_MainPanel.layout`.

## WP-A: Title entry and Multiplayer window

Files: `PlayerInterface.TitleMenu.cs` and `PlayerInterface.Multiplayer.cs`.

**A1 Title.** Insert `("ImportGameButton", "MULTIPLAYER", new(1250, 765, 50, 50))` into `MenuButtons` between LOAD GAME and OPTIONS.
- The icon is `Kenshi_CharacterSpeedSet` / `Group`, the group-of-people icon. **Verified**: Kenshi_UI.png at 1250,765, 50×50, read from `kenshi_images.xml`.
- `MenuLayout` already stacks every entry. The stack grows by 44 kit units.
- Check with `ShowUiPanel("title")` screenshots that the card stays inside the vignette. If it does not, raise the vignette's 470 to 520 and update docs/meitou-ui.md.

**A2 Layout** (`const string MultiplayerLayout`). Register it as `("multiplayer", modal: true, centreRoot: true)`. Positions are fractions of the parent.

```xml
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
   <Widget type="TextBox" skin="Kenshi_GenericTextBoxFlat" position_real="0.62 0.12 0.18 0.1" name="lbMaxPlayers"><Property key="Caption" value="Max players"/></Widget>
   <Widget type="Button" skin="Kenshi_SpeedArrowLeftSkin" position_real="0.81 0.135 0.04 0.07" name="MaxPlayersLeft"/>
   <Widget type="TextBox" skin="Kenshi_TextboxStandardText" position_real="0.85 0.12 0.11 0.1" name="MaxPlayersText"><Property key="TextAlign" value="Center"/></Widget>
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
   <Widget type="MultiListBox" skin="Kenshi_MultiListBox" position_real="0 0.34 1 0.66" name="RecentList"/>
  </Widget>
  <Widget type="TextBox" skin="Kenshi_TextboxStandardText" position_real="0.02 0.86 0.6 0.12" name="StatusText"><Property key="WordWrap" value="true"/><Property key="TextAlign" value="Left VCenter"/></Widget>
  <Widget type="Button" skin="Kenshi_Button1" position_real="0.64 0.88 0.17 0.09" name="ConnectButton"/>
  <Widget type="Button" skin="Kenshi_Button1" position_real="0.82 0.88 0.17 0.09" name="CancelButton"><Property key="Caption" value="CANCEL"/></Widget>
 </Widget>
</MyGUI>
```

**A3 State and binding**
- Fields: `enum MpTab { Host, Join }`, `MpTab mpTab`, `bool mpRunning` (opened from the Esc menu: host the running game), `string mpGame = "new"` (the selected `GamesList` id: `"new"` or a save folder), `int mpMaxPlayers`, `bool mpClientsTime, mpCheats`, `(string Text, bool Error) mpStatus`, `JoinGame? mpJoining`, `long mpJoinStarted`, `HostSettings? hostIntent`, `string mpAddressShown`.
- `OpenMultiplayer(bool runningGame)`:
  - `RefreshSlots()`, then load the fields from `Config`.
  - Open the screen, then `SetText` the boxes:
    - Name: `Config.PlayerName ?? DefaultName()`, where `DefaultName()` is `Environment.UserName` filtered to letters, digits, space, `-` and `_`, cut to 24, else `"Drifter"`.
    - Port: `HostPort`.
    - Address: `RecentGames[0].Address` or `""`.
    - Passwords: `""`.
  - Then `FocusBox("multiplayer", "NameEditBox")` when the name is empty.
  - `mpTab = runningGame ? Host : Join` when there are recent games, else `Host`.
- `BindMultiplayerScreen(b)`:
  - `EnableButtons` for the tab buttons, Connect, Cancel, the arrows and the ticks.
  - Tab buttons: `Checked` / `StateSelected` on the active one. Hide the inactive panel. With `mpRunning`, hide `JoinTabButton` and `GamesList`.
  - `ConnectButton` caption is `HOST`, `JOIN`, or `OPEN` (`mpRunning`). It has `DefaultAction=true`.
  - `GamesList` columns `[new("Game", 170, Fill: true), new("Date", 150)]`. Rows: `("new", "New game", ["New game", ""])`, then the same slot rows as the load list. `SelectedItems` comes from `mpGame`.
  - `InfoPanel`: `DescribeSlot(slot).Text`. For `"new"`: "Start a new game; you choose the start next." With `mpRunning`: "Your game goes on while the others join."
  - `RecentList` columns `[Address (fill), Host 150, Last joined 140 (local "yyyy-MM-dd HH:mm")]`.
  - `MaxPlayersText` = n. The tick boxes get `Checked` / `StateSelected`.
  - `StatusText`: the text, with `TextColour = BadColour` when it is an error.
  - While `mpJoining` is set, disable every control except `CancelButton`.

**A4 Actions**
- Tabs switch `mpTab`. The arrows clamp n to 2..8. The ticks toggle.
- `GamesList` select sets `mpGame`. `RecentList` select puts the row's address into `AddressEditBox` (`SetText`).
- `CancelButton`:
  - while `mpJoining`: `Request(new CancelJoin())`, then `mpJoining = null` and status "Cancelled.";
  - otherwise: save the name if it is valid and close.
- `ConnectButton`: validate, setting an error status and returning on failure:
  - Name: trimmed, 1–24 characters, no control characters, not starting with `/` or `#`. "Enter a name of 1 to 24 characters."
  - Host port: integer 1024–65535. "The port must be a number from 1024 to 65535."
  - Join address: `TryParseAddress(text, MultiplayerDefaults.Port, out host, out port)`. It accepts `host`, `host:port`, `[v6]` / `[v6]:port`, and a bare IPv6 address (several colons) as the host alone; the port must be 1–65535. On failure: "Enter an address such as 192.168.1.20:27420."
- Then write `PlayerName`, `HostPort`, `HostMaxPlayers` and the two ticks to `Config`, and call `Config.EnsurePlayerId(); Config.Save()`. After that:
  - **Join:** `mpJoining = new(host, port, pw)`, status "Connecting to {host}:{port}…", then `Request`.
  - **Open running:** `Request(new HostGame(s, null, null))`, then close.
  - **New:** `hostIntent = s`, close this window, then `faithfulScreens.Open("new-game")`.
  - **Save:** `Request(new HostGame(s, null, folder))`, behind `Confirm("HOST GAME", "Load this save and host it? Unsaved progress will be lost.")` when `session.World.Player.Exists`.
  - If `Request` returns false: status "Multiplayer is not available in this build." with error colour, and `mpJoining = null`.
- `BeginHostedNewGame(startId)`: if `hostIntent` is set, `Request(new HostGame(hostIntent, startId, null))`, clear it and return true. Otherwise return false.

**A5 `MultiplayerScreenFrame()`** runs every frame.
- `hostIntent` is cleared when `new-game` is not open.
- While `mpJoining`:
  - The window was closed by Esc while `Phase is Connecting or Joining`: send `CancelJoin` and drop `mpJoining`.
  - Phase `Connecting`: "Connecting to {addr}… {s} s".
  - Phase `Joining`: "Joining…".
  - Phase `Downloading` / `Loading`: "Downloading the world… {p:P0}". D4 then covers the screen and calls `CloseFrontend`.
  - Phase `Playing`: insert `RecentGame(addr, host name from Players.First(IsHost), now)` at the front, keeping 8 entries unique by address, `Config.Save()`, then `mpJoining = null`.
  - Phase `Ended` while the window is open: status `EndText(...).Text` as an error, controls enabled again, `mpJoining = null`.

## WP-B: Chat

Files: `PlayerInterface.Chat.cs` and `Multiplayer/ChatCommands.cs`.

**B1 Entry layout** (`ChatLayout`). Register it as `("chat", modal: false, centreRoot: false)`, so it never pauses anything.

```xml
<MyGUI type="Layout" version="3.2.0"><Widget type="Widget" skin="PanelEmpty" position_real="0.008 0.47 0.3 0.036" layer="Window" name="Root">
 <Widget type="EditBox" skin="Kenshi_EditBox" position_real="0 0 0.8 1" align="Stretch" name="ChatEditBox"><Property key="MaxTextLength" value="200"/></Widget>
 <Widget type="Button" skin="Kenshi_Button2" position_real="0.81 0 0.19 1" align="Right VStretch" name="SendButton"><Property key="Caption" value="SEND"/></Widget>
</Widget></MyGUI>
```

The history sits above `y = 0.47·H`. The HUD's InformationPanel starts at 0.5176·H (**Verified**, `Kenshi_MainPanel.layout`), so the bottom-left HUD stays clear.

**B2 State.** `readonly record struct ChatLine(long At, int Player, string Name, string Text, ChatTone Tone)` with `enum ChatTone { Player, System, Error }`. Fields: `List<ChatLine> chatLines` (at most 100), `List<string> chatSent` (at most 20), `int chatRecall = -1`, `string chatDraft = ""`.
- `OpenChatKey(key)`:
  - It acts when `Multiplayer && !TitleOpen && !EditorOpen` and `session.Bindings.Get(InputAction.OpenChat)` contains `Binding.Of(key)`.
  - It runs: `Open("chat")`, `SetText("chat", "ChatEditBox", chatDraft)`, `FocusBox("chat", "ChatEditBox")`, `chatRecall = -1`, and returns true.
  - It is reached only when no modal window is open and Enter found no default button.
- `ChatEntryKey(key, shift)` acts only while `faithfulScreens.Focus is { Screen: "chat" }`:
  - Escape: keep the text as `chatDraft`, close, return true.
  - Up / Down: step through `chatSent` (`SetText` puts the caret at the end), return true.
  - Tab: return true. It is swallowed, so the focus never leaves the box without a click.
  - Any other key: return false, and the usual `EditFocusKey` path types it.
- `BindChatScreen`: `EnableButtons(b, "SendButton")` and `SendButton` `DefaultAction=true`. Enter in the box reaches it through `EditEnter`, then `Submit`.
- `ChatAction(SendButton)`:
  - `text = GetText.Trim()`. Close the window and clear `chatDraft`. If the text is empty, return.
  - Push the text to `chatSent`.
  - If it starts with `/`, `RunChatCommand`. Otherwise `Net.SendChat(text)`.
  - Own lines are never added locally. They come back as `Chat` events, so the order is the host's.
- `ChatFrame(from, events)`:
  - `from == None && role != None`: clear the lines. If the role is Host, add the system line "Hosting on port {Rules.Port}. Others join with your address and this port."
  - The chat is open but no longer focused (clicked away): store the draft and close.
  - Map events to lines:

| Event | Line (`You` when Player == LocalPlayer) |
|---|---|
| Joined | {Name} joined the game. |
| Left | Left: {Name} left the game. · TimedOut: {Name} lost the connection. · Kicked: {Name} was kicked. · Banned: {Name} was banned. |
| Paused / Resumed | {Name} paused / resumed the game. |
| Speed | {Name} set the speed to {Speed:0.#}×. |
| Chat | Name: Text (ChatTone.Player) |

Every line also goes to `LogMessage("{Name}: {Text}")` (the Overview Messages tab).

**B3 `AddChatHistory`** (needs `uiPainter`, else return).
- Sizes: `s = Height/1080`; `em = 20·50/72·s` (Kenshi_StandardFont_Medium, as `AddSpeech`); `line = 1.25·em`; left edge `0.008·W + 4s`; bottom `0.47·H − 6s`; width `0.30·W`.
- Wrap each line with the existing `Wrap` and `painter.MeasureStandard`. The first visual line carries the name prefix.
- Closed: lines younger than 12 s, `alpha = clamp((12 − age)/2, 0, 1)`, at most 8 visual lines. Open: the last 14 visual lines at alpha 1.
- Each visual line is built from:
  - a backing `Widget` (`Background = (0,0,0,0.4·alpha)`, width = measured + 8s);
  - a name `TextBox` (`TextColour = PlayerColours.MyGui(colour)`; the colour comes from `PlayerInfo`, else `Absent`);
  - a text `TextBox`: `0.92 0.89 0.83` for Player, `0.85 0.81 0.72` for System, `BadColour` for Error;
  - all with `FontName=Kenshi_StandardFont_Medium`, `TextShadow=true`, `Alpha`, `Pickable:false`, `TextScale:1`.

**B4 `ChatCommands`** (pure static, no I/O)

```csharp
enum ChatVerb { Help, Players, Kick, Ban, Unknown }
readonly record struct ChatCommand(ChatVerb Verb, string Target, string Rest);
static class ChatCommands
{
    /// <summary>"/kick Bob being rude" → (Kick, "Bob", "being rude"); a quoted target may hold spaces; the verb ignores case.</summary>
    public static ChatCommand Parse(string line);
    /// <summary>"#3" by number; else the exact name (OrdinalIgnoreCase); else a unique prefix. Null with <paramref name="error"/>: "No player called X." / "X matches several players: A, B.".</summary>
    public static NetPlayer? Resolve(IReadOnlyList<NetPlayer> players, string token, out string error);
}
```

`RunChatCommand` replies with System or Error lines:
- `/help`: lists the commands; the host also sees kick and ban.
- `/players`: one line per player: "#n Name · HOST · 34 ms".
- `/kick <player> [reason]`:
  - a client gets "Only the host can kick players.";
  - the target is self or the host: "You cannot kick yourself.";
  - otherwise `Net.Kick(n, reason)`.
- `/ban <player>`: the same checks, then `Net.Ban`.
- Anything else: "Unknown command. Type /help."

## WP-C: Player list, name tags, map markers

Files: `PlayerInterface.Players.cs`, `PlayerInterface.Stealth.cs` and `PlayerInterface.Map.cs`.

**C1 `AddPlayerList`** (held P). It is drawn over windows.
- Panel: `Kenshi_FloatingPanelSkin`, width `0.34·W`, centred, top `0.14·H`.
- Title: `PLAYERS  {count} / {Rules.MaxPlayers}` in `Kenshi_PaintedTextFont_Medium`.
- Header row in `Kenshi_StandardFont_MediumSmall`, grey `0.65`: Player · Squads · Ping.
- Rows are `0.034·H` tall, in `Players` order. Each row has:
  - a colour chip: a Widget with `Background = Of(colour)`, `0.012·W` square;
  - the name in its colour, with " (you)" on the local row;
  - a `HOST` tag (Kenshi_Button1Skin plate, MediumSmall font) for the host;
  - status in grey: "joining…" or "AFK";
  - squads count;
  - ping "{n} ms", coloured green `0x79B44B` up to 80 ms, amber `0xF2C14E` up to 180 ms, red `0xE65139` above, and "—" on the host row.
- All controls are `Pickable:false`.

**C2 `AddNameTags`.** Single-player output stays identical.
- The loop keeps `c.IsPlayer`.
- For `OtherPlayers(c)`:
  - The caption is `c.Name`, never bracketed, with `TextColour = PlayerColours.MyGui(owner colour)`.
  - A second `TextBox` of height `0.7·height`, just below, shows the owner's name in `Kenshi_StandardFont_MediumSmall`, same colour, `Alpha 0.85`, shadow.
  - An owner missing from `Players` is drawn as "Player {n}" in `Absent`.
- For `Mine(c)` while `Multiplayer`: the existing tag plus a `Widget` bar (`Background = own colour`, `0.4·width` wide, `2s` tall, centred, `1s` under the text).
- Range and stealth icon rules are unchanged.

**C3 Map** (`AddMapContent`).
- Split `players` into `mine` and `others`. `PlayerNear` (nests) uses both, because teammates reveal nests too.
- Own markers: single-player keeps `PlayerColour` / `SelectedColour` exactly. In multiplayer they use `PlayerColours.Of(own)` / `Selected(own)`.
- `level > 0`: each other character gets a `Character` marker in its owner's colour, `tooltip = "{Name}\nPlayer: {owner}"`.
- `level == 0`: one `Character` marker per `(Owner, SquadId)` group at the mean position, `1.3×` size, tooltip `"{owner}\nSquad of {n}"`.
- Markers are drawn after the towns and before the movement marker, using the existing `Marker()`.

## WP-D: Notices, indicators, dialogs, Esc menu, players window

Files: `PlayerInterface.NetStatus.cs` and `PlayerInterface.NetMenu.cs`.

**D1 Time**
- `NetTimeButton(index)` returns false when `!Multiplayer`. Otherwise:
  - without `CanControlTime(LocalPlayer)`: `Notice("Only the host can change the game speed.", true)`;
  - index 0: `RequestPause(!session.Simulation.IsPaused)`;
  - else `RequestSpeed(ButtonSpeeds[index])`;
  - returns true.
- `BindNetHud(b)` disables `TimeSpeedButton1..4` without the right.

**D2 `AddNetStatus`** (after the HUD and before the windows). `s = H/1080`.
- **Paused by**: when `PausedPanel` is among `controls` and `PausedBy` is set, a TextBox `0.026·H` tall, `2s` under the panel, reading "by {name}" (or "by you") in the pauser's colour, `Kenshi_StandardFont_Medium`, shadow. `lbPaused` keeps the original's `PAUSED`.
- **Banner**: `StatusFrame` turns Paused, Resumed and Speed events from other players into `banner = (text, until = now + 3 s)`, with the same texts as the chat lines.
  - It is drawn as a TextBox with skin `Kenshi_FloatinglPanelSmallSkin` (the installed spelling) at centre x, `y = 0.115·H`, width = measured + 24s, height `0.04·H`.
  - It uses `Kenshi_StandardFont_Medium` and fades over its last 0.5 s.
  - While paused it moves `0.03·H` lower, below the "by" line.
- **Indicator**, top right (`W − 0.128·W`, `0.008·H`, `0.12·W × 0.03·H`):
  - Client: three bars (Widgets, `3s` wide, heights 1/3, 2/3 and 1 of the box) lit 3, 2 or 1 by the ping thresholds of C1, in their colour. Then "{ping} ms" in `Kenshi_StandardFont_MediumSmall`.
  - Host: "HOSTING · {n}/{max}".
- **Starving** (client, `Quality.SnapshotAgeSeconds > 0.5`): the bars turn red, and a `Kenshi_FloatingPanelSkin` plate at centre `y = 0.16·H` reads "Waiting for the host… {age:0} s". The timeout itself is D4's, which then ends with `TimedOut`.

**D3 Esc menu.** `BindNetMenu` places rows into the original slots.

The slot geometry is derived from the layout. **Verified** (`Kenshi_MainMenuPopupPanel.layout`): the six buttons sit at panel fractions y 0.0436 + i·0.14535, height 0.1308. Resume is 0.2035 below Exit, and the bottom margin is 0.0407.
- `PlaceMenuRows(UiBindings b, IReadOnlyList<string> rows)` (Resume last):
  - Panel height in units of the original panel height P: `first + (n−2)·pitch + resumeGap + h + bottom`, keeping the original centre.
  - Every row gets an absolute `b.Positions` rect. Layout buttons not in `rows` go into `b.Hidden`.
  - Rects of decorated rows are stored in `netMenuRects` for `AddNetMenuContent`, which adds a `Button` (skin `Kenshi_Button1Skin`, `FontName=Kenshi_PaintedTextFont_Medium`, `TextAlign=Center`, `Action` = its name).
  - If any reference widget is missing (a modded layout), skip moving rows: only hide and add, stacking decorated rows under `ExitGameButton`.

| State | Rows |
|---|---|
| Single-player, in a game | NEW GAME, SAVE GAME, LOAD GAME, OPTIONS, **MULTIPLAYER**, EXIT GAME, RESUME |
| Host | **PLAYERS**, SAVE GAME, OPTIONS, **STOP HOSTING**, EXIT GAME, RESUME |
| Client | **PLAYERS**, OPTIONS, **DISCONNECT**, EXIT GAME, RESUME |

The bold rows are decorated (`MpPlayersButton`, `MpOpenButton`, `MpStopButton`, `MpLeaveButton`).

`NetMenuAction` handles:
- MULTIPLAYER: `OpenMultiplayer(true)`.
- PLAYERS: close `menu`, open `players`.
- STOP HOSTING: `Confirm("STOP HOSTING", "Stop hosting? The other players are disconnected; your game goes on.", → StopHosting, "STOP")`.
- DISCONNECT: `Confirm("DISCONNECT", "Leave the game and return to the main menu?", → LeaveGame(false), "DISCONNECT")`.
- EXIT GAME:
  - Client: `Confirm("EXIT", "Leave the game and quit?", → LeaveGame(true), "QUIT")`.
  - Host: the existing text plus " The other players are disconnected."
- Anything else: return false.

**D4 Players window** (`PlayersLayout`, `("players", modal: true, centreRoot: true)`).
- Root `Kenshi_FloatingPanelSkin` at `0 0 0.3125 0.444`. Children:
  - `lbTitle` `PLAYERS` (Painted Large) at `0.01 0.012 0.98 0.09`;
  - `PlayersList` Kenshi_MultiListBox at `0.02 0.11 0.96 0.7`, columns Player (fill) / Squads 80 / Ping 80 / Status 110;
  - `KickButton` at `0.02 0.86 0.2 0.1`;
  - `BanButton` at `0.23 0.86 0.2 0.1`;
  - `CloseButton` at `0.78 0.86 0.2 0.1`, `DefaultAction`. All buttons use `Kenshi_Button1`.
- Row ids are player numbers. Cells: name + " (you)" + " · HOST", squads, ping or "—", "Joining" / "AFK" / "".
- `AddPlayersContent` puts a colour chip at the left of each row control whose `ListOwner == "PlayersList"`, keyed by the row's `Argument`. Check that MultiListBox rows carry the item id, as ListBox rows do for JobsPanel; if they don't, skip the chips.
- Kick and Ban are shown only to the host, and enabled for a selected non-host row:
  - Kick: `Confirm("KICK PLAYER", "Kick {name} from the game?", → Net.Kick(n, ""))`.
  - Ban: `Confirm("BAN PLAYER", "Ban {name}? They cannot rejoin while you host.", → Net.Ban(n))`.

**D5 Alert.** `ShowDisconnected(reason, detail)` stores `pendingAlert = EndText(reason, detail)`. If `TitleOpen`, it calls `OpenPendingAlert()` at once. `OpenPendingAlert()` runs `Alert(title, text)` and clears the pending alert. D4 calls it only for sessions that reached Downloading or later. Earlier failures appear as the window's status line (A5).

## Invariants and edge cases

- `Multiplayer == false`: `Mine == IsPlayer`. Every hook returns false or adds nothing, and `RequestedPause` is computed exactly as today. Single-player HUD and selection behaviour are identical except the extra Esc row.
- `PollNet` runs only from `Draw`, once per frame. `ArrangeUi` stays free of side effects apart from what it already does.
- Chat never leaks keys: while the box is focused, `UiKey` returns true from `EditFocusKey`, so WASD, digits and Space never reach `session.Input`, and Ctrl+C/V stay the box's (`UiEditing`).
- A modal window opening while the chat is focused moves the focus away (`Open` clears focus), so `ChatFrame` stores the draft and closes the chat.
- Text sanitising: trim, cut chat to 200 and names to 24, and drop control characters. The host validates again (D4).
- A player who left but still owns characters is shown as "Player {n}" in `Absent`.
- The UI never sends commands that name another player's characters: selection, box select, portraits and jobs all use `Mine`. A stale snapshot can still race, and D3's gate drops those silently.
- Esc on the Multiplayer window while connecting is a Cancel.

## Leave alone / owned elsewhere

- `Say` and command routing: D3/D4's sink. Pause and speed keys (Space, F2–F4 in `WorldSession.Tick`): D4 routes them to `RequestPause` / `RequestSpeed`.
- Client mirrors read by the HUD (`session.Simulation.Speed/IsPaused`, `session.Clock`, `session.World.Player.Money`): D4/D1.
- F5/F9 on clients ("Only the host can save."): D4's `HandleSaves`. Hosting and stopping on main menu or exit: D4. Command line (`--host`, `--join`, `--name`): D4.
- Locking the gameplay-rule settings rows on clients is D4's call. `SettingsMenu.Entry` is the hook.
- `GameHost.Interiors` and `GameHost.Characters` uses of `IsPlayer`, `FloaterText` colours, speech bubbles and chat bubbles: unchanged and out of scope. `ContextMenu` needs no change.

## Cross-module interfaces

**Exposed by D5**
- `IMultiplayerView` and the `Net*` / `HostSettings` / `MultiplayerRequest` / `MultiplayerDefaults` types (§1). D5 owns them and D4 implements them.
- `PlayerInterface`:
  - `IMultiplayerView? Net { get; set; }`
  - `event Action<MultiplayerRequest>? MultiplayerRequested`
  - `void ShowDisconnected(NetEnd reason, string detail)`
  - `void OpenMultiplayer(bool runningGame)`
  - `void CloseFrontend()` (existing; D4 calls it when the join covers the screen)
  - `void Notice(string, bool)` (existing)
  - `bool PlayerListHeld { set; }`
  - `internal UserConfig? Config { set; }`
- `PlayerColours.Assign(IReadOnlyCollection<int> taken, int previous = -1)`, `Of(int)`, `MyGui(int)`. D4 assigns colours with them.
- `UserConfig`: `PlayerName`, `PlayerId` with `EnsurePlayerId()`, `HostPort`, `HostMaxPlayers`, `HostClientsControlTime`, `HostAllowCheats`, `RecentGames`.
- `MyGuiScreenHost.FocusBox(string id, string widget)` (Meitou.Ui), and `InputAction.OpenChat` / `InputAction.PlayerList`.

**Consumed by D5**
- **D1:**
  - `CharacterSnapshot.Owner : int`: the owning human's player number; 0 for host-owned characters, single-player and non-player characters.
  - Per-viewer `WorldSnapshot.PlayerSquads`, `ActiveSquad`, `CharacterSnapshot.Selected / Inspected / Stealth`, through `session.CurrentSnapshot` on host and client alike.
- **D3:** `InspectCommand` is accepted for any character from any issuer. Foreign IDs are dropped silently.
- **D4:**
  - Implements `IMultiplayerView` and sets `player.Net`. It sets `Net = null`, or `Role = None`, once a session is over and the title shows.
  - Handles every `MultiplayerRequest`:
    - `HostGame` with a start or folder runs the existing new-game or load flow, then hosts;
    - `JoinGame` uses `Config.PlayerName` and `EnsurePlayerId()`;
    - `CancelJoin`, `StopHosting` and `LeaveGame` (main menu or quit).
  - Echoes every chat message, the sender's own included, as a `Chat` event, and emits Joined, Left, Paused, Resumed and Speed with names.
  - Fills `NetPlayer.Squads`, `Afk` and `PingMs`, and `Quality.SnapshotAgeSeconds` on clients.
  - Calls `ShowDisconnected` and returns to the title for failures from Downloading on.
  - Respects `Rules.AllowCheats` for client `Debug*` commands.

## Order and parallelism

1. **WP-E** (serial, first): E1 → E2 → E3 → E4/E5 (contract, stubs) → E6 → E7. Build with warnings as errors, then run the quick tests. `FaithfulInteractionTests` must still pass: Esc → Exit → ButtonC still works with the 7-row menu.
2. **In parallel**, each touching only its own files: **WP-A** (`TitleMenu.cs`, `PlayerInterface.Multiplayer.cs`), **WP-B** (`PlayerInterface.Chat.cs`, `Multiplayer/ChatCommands.cs`), **WP-C** (`PlayerInterface.Players.cs`, `PlayerInterface.Stealth.cs`, `PlayerInterface.Map.cs`), **WP-D** (`PlayerInterface.NetStatus.cs`, `PlayerInterface.NetMenu.cs`).
3. **Integration with D4**: a two-instance smoke run from separate working directories. Visual checks by screenshot in both UI modes: title, Multiplayer window, Esc menus, chat, held P, tags, map.

No new tests are part of this module; the approved tests belong to D1–D4. `ChatCommands.Parse` / `Resolve`, `TryParseAddress` and `PlayerColours.Assign` are pure. Unit tests for them would catch parsing regressions, but they need the user's approval first.
