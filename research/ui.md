## 1. Title menu: how it's built and where to add Multiplayer

**Both modes use the original MyGUI screen.** `LoadUi` registers the game's own layouts as screens (`PlayerInterface.Ui.cs:49-51`, list `FaithfulScreenFiles` at `:58-69`, e.g. `("title","Kenshi_MainMenu",true)`). `ShowTitle()` = `faithfulScreens.Open("title")` (`PlayerInterface.Frontend.cs:30`).
- **Meitou mode only reskins it.** `AddFaithfulContent` (`PlayerInterface.Faithful.cs:181`) calls `MeitouTitleMenu(controls)` (`PlayerInterface.TitleMenu.cs:66-123`). That method adds ImageBox/TextBox art and turns each real button into an invisible hit target with `Hollow(c)` (`:126`). Positions come from `MenuLayout` (`:52-60`) and the `MenuButtons` table (`:23-27`: `("NewGameButton","NEW GAME",icon)…`). The bindings apply them through `b.Positions` (`Frontend.cs:155`).
- **Button actions.** A click goes `MyGuiScreenHost.PointerDown` → `Activated(new(id, c.Name, c.Action))` (`MyGuiScreenHost.cs:141`) → `FaithfulAction` → `HandleFrontendAction` (`Frontend.cs:70-136`, title switch at `:74-89`). That raises `ScreenAction`, which the host handles at `GameHost.Interactive.cs:334-342` (`"new-game"`, `"load"`, `"main-menu"`, exit).
- **All buttons start disabled.** `BindFaithfulScreen` disables every layout button (`Faithful.cs:101`), and `BindFrontend` re-enables some (`Frontend.cs:137-163`).

**Two ways to add a "MULTIPLAYER" entry:**
- **(a) Cheapest: reuse `ImportGameButton`.** It is a real widget in `Kenshi_MainMenu` that sits between Load and Options (the original order is listed in `docs/game/ui-screens.md` §2). It is hidden in Meitou mode (`Frontend.cs:154`) and Import is not implemented.
  - Remove that `Hidden.Add`, add `("ImportGameButton","MULTIPLAYER",icon)` to `MenuButtons`, `Enable("ImportGameButton")`, and add a `case` in `HandleFrontendAction`.
  - It then gets the plate art, hover, layout and keyboard focus ring for free.
  - Cost: Faithful mode stops matching the original title.
- **(b) A new decorated control.** In `MeitouTitleMenu` (or a Faithful branch of `AddFaithfulContent`), append a `UiControl` with `Type "Button"` and `Name "MultiplayerButton"`. Decorated controls are part of the frame (`MyGuiScreenHost.cs:97`), so hit testing and `Activated` work unchanged. You have to give it a position yourself, since `MenuLayout` only places named layout widgets.

**Lobby, Host and Join screens: use the MyGUI screen host.**
- Write a Meitou layout as an XML string and load it with `MyGuiReader.ParseLayout(xml)` (`Meitou.Data/Ui/MyGui.cs:111`; the tests do this at `MyGuiScreenTests.cs:28`).
- Register it with `faithfulScreens.Register("lobby", layout, modal: true, centreRoot: true)` next to the loop at `Ui.cs:49`. It must reference only the installed skins and fonts, never shipped game files.
- What you get for free:
  - `EditBox` with caret, selection, clipboard and 128-step undo (`UiTextBuffer`). `MaxTextLength` and `Password` come from layout properties (`MyGuiScreenHost.Keyboard.cs:37-45`).
  - Tab/Shift+Tab traversal, the Meitou focus ring (`:25`, `:104-115`), and Enter firing the `DefaultAction` button (`MyGuiScreenHost.cs:191-198`).
  - `MultiListBox` with columns, sorting and selection.
- **Model to copy: the save/load panels.** Binding in `Frontend.cs:174-188`: `b.Columns["GamesList"]=[new("Name",170,Fill:true),new("Date",180)]`, `b.Lists[...]`, `b.SelectedItems`, `DefaultAction`. The save name box uses `SetText`/`GetText("save","SaveEditBox")` (`:68`, `:127`). Validation errors go to `Notice` (`:129`), confirmations to `Confirm(...)` (`:40-43`).
- Lobby mapping:
  - `PlayersList` columns: Name / Ready / Ping.
  - Edit boxes: `NameEditBox`, `AddressEditBox`, `PortEditBox`.
  - Buttons: Host / Join / Ready / Start / Cancel.
- **Alternative: a native Meitou-kit screen like `SettingsMenu`** (`SettingsMenu.cs`, `.Drawing.cs`, `SettingsMenuArt.cs`). It looks better but is all bespoke:
  - Separate input wiring in `GameHost.Interactive.cs:201-207` and `:274-279`.
  - Its text editing is numeric only (`TextInput` accepts digits, `SettingsMenu.cs:365-370`).
  - Not recommended for text entry.
- **`UiHost`/`UiNode`** (`Meitou.Ui/UiHost.cs`, `UiNode.cs`) only has Panel, Label and Button (`UiNode.cs:28`), with no edit box. It renders only while the HUD shows (`Ui.cs:194`). Not suitable for a lobby.

**Caveats:**
- **Modal screens pause the game.** `UpdateMenuPause()` sets `session.Simulation.RequestedPause = settingsOpen || faithfulScreens.HasModal` (`Faithful.cs:17`). Any modal (Esc menu, settings, message box, lobby) pauses the world. That has to be bypassed in a networked game.
- **Faithful mode has no live title world.** `TitleVisible` requires Meitou mode (`GameHost.Title.cs:26`).
- **Join flow template.** `BeginQuickLoad` (`GameHost.Saving.cs:170-184`) runs under the loading screen:
  - `StartSceneLoad(player.TitleOpen, Stage("read", () => Task.Run(() => SaveGame.Load(folder))), Stage("build", () => SwapInSave(read!, ...)), Stage("finishing", ...))`
  - A network "read" stage that returns a `Task<SaveGame>` fits `SwapInSave(Task<SaveGame>, …)` (`:191`) unchanged.
  - `SaveGame` only reads and writes folders (`Load(string folder)` at `SaveGame.cs:52`, `Write(string folder)` at `:263`), so the transfer would be a temp folder plus an archive.
  - Add step labels to `SceneSteps` (`GameHost.Loading.cs:211-214`).
  - A `SceneLoadFailed` becomes a `player.Notice` and the old world stays (`GameHost.Transition.cs:81-88`).
  - Disconnecting back to the title reuses `HandleMainMenu` (`GameHost.NewGame.cs:16-33`).

## 2. In-game text overlays and which to reuse

| Mechanism | Where | What it is | Use for |
|---|---|---|---|
| `Notice(text, error)` | `PlayerInterface.Hud.cs:25-32`; drawn in `DrawNotice` `:50-58` | One slot, centred at the top, in the debug overlay's own font (not a Kenshi font). Lasts 2.5 s, or 6 s for errors; a new notice replaces the old one. Also appended to `uiMessages` (max 200), which is shown in the Overview "Messages" tab as `MessagesTextBox` (`Faithful.cs:144`, `:207`) | "X paused the game", joined/left notices, connection errors. The `uiMessages` log can double as chat history |
| Name tags `AddNameTags` | `PlayerInterface.Stealth.cs:55-83`, inserted under every panel (`Ui.cs:215-217`) | TextBox controls with `Kenshi_StandardFont_Large`, centred, `TextShadow`, projected over the head. Drawn only for `c.IsPlayer` within `NameTagRangeSquared` | Player name tags: add a branch for other players' characters with an owner label or colour. Needs an owner or faction id on `CharacterSnapshot`; today it only has `IsPlayer` (`CharacterTable.cs:74`) |
| Speech bubbles `AddSpeech` | `PlayerInterface.Speech.cs:16-44` | Reads `session.CurrentSnapshot.Speech`. Draws a `Kenshi_SpeechPanelSkin` panel plus wrapped text (`Wrap`), with distance fade | Chat bubbles over the sender's lead character: refactor it to take a list of (speaker, text, large) that also holds host-side chat entries |
| Damage floaters | `PlayerInterface.Floaters.cs:35-74`, `FloaterText.cs` | Text via `DebugOverlay.TextScaled` at projected world points; rises and fades over 1.4 s; fed by the `World.Floaters` queue | Brief world-anchored pings. Not chat |
| HUD text panels | `PlayerInterface.HudText.cs` | Label/value rows inside the HUD's info panels | Not relevant |
| `PausedPanel` | `Ui.cs:153` (hidden unless speed is 0) | The original HUD's paused panel | A lasting "Paused by X". Which child widget holds the text is **Unknown** (needs reading `Kenshi_MainPanel.layout`) |
| `UiHost` contributions | `UiSlots.Overlay` / `Toolbar` (`Ui.cs:187-194`, `UiHost.cs:98-115`, `UiRegistration.Contribute` `:184`) | Code-built Label nodes that read `ctx.Text(key)` from `BindUi` text | Ping / connection indicator: set `b.Text["net.ping"]` in `BindUi` (`Ui.cs:98`). Only drawn while the HUD shows |
| Debug overlay panels | `GameHost.Interactive.cs:513-528` (fps counter, F11 stats, `DrawDebugMenu`) | Debug-font panels below `player.HudBottom` | A network-stats panel for development |

**`GameConsole` is not an in-game console.** It attaches to the parent terminal and tees output to log.txt, and `ForPrompt` opens a console window for the install-path question (`GameConsole.cs:16-55`). There is no in-game console to reuse.
- The F10 debug menu is the only in-game typed field (`GameHost.DebugMenu.cs:29-45`; raw `+= c`, no caret).
- It enqueues `Debug*` commands straight into `world.Commands` (`:57`), so in multiplayer it must be host-only or routed through the network.

**Recommendation for chat:**
- History: fading lines at the bottom left, built as Pickable:false TextBox controls in `ArrangeUi`, in the same style as speech and name tags.
- Entry: a small non-modal registered screen holding one `EditBox` plus a `DefaultAction` Send button. Non-modal means no pause.

## 3. Keyboard path to edit boxes, and key bindings

**Event order** in `GameHost.Interactive.cs`, `kb.KeyDown`:
1. Alt+Enter.
2. `Covered` (loading) swallows keys.
3. F7.
4. Settings panel.
5. F10 check against bindings (`:210`).
6. Debug menu.
7. Free-camera Ctrl+C/V, guarded by `!player.UiEditing`.
8. `player.UiKey(...)` (`:239`). Returns before `session.Input.SetKey(key,true)` (`:263`) when it consumes the key.

**Inside `UiKey`** (`Faithful.cs:18-60`):
- Escape closes the top screen or opens the menu (`:23-31`).
- `if (faithfulScreens.HasKeyboardFocus && EditFocusKey(...)) return true;` (`:33`). `EditFocusKey` (`PlayerInterface.Keyboard.cs:32-40`) swallows every key while a writable box has focus, so WASD and the number keys don't leak into the game.
- Enter in a single-line box → `EditEnter` → `Submit()` → the `DefaultAction` button.

**Characters:** `kb.KeyChar` (`:274-279`) → `player.UiText` → `faithfulScreens.TextInput` (`Faithful.cs:61`, `MyGuiScreenHost.cs:169-174`; control characters are ignored).

**Gap: no public way to focus a box.** `FocusEdit` is private (`MyGuiScreenHost.Keyboard.cs:48`), and `Open()` clears focus (`MyGuiScreenHost.cs:45`). `FocusNext()` only works once the screen has been arranged, because it uses `frame`. A chat box that opens already focused therefore needs a small public `Focus(screen, widget)` added to `MyGuiScreenHost`.

**Adding bindings:**
- Add the actions to `enum InputAction` (`Meitou.Engine/Input/InputBindings.cs:4-31`) and give defaults in `ResetToDefaults` (`:79-115`).
- They then appear automatically on the Settings Controls page (`SettingsMenu.cs:162`), with conflict detection (`:415`).
- They are saved through `config.Bindings = session.Bindings.ToDictionary()` (`Interactive.cs:624`) and applied at boot (`GameHost.cs:186`).
- **Opening chat:** check the binding in the `KeyDown` handler, the way `ToggleDebugMenu` does (`:210`). Don't wait for a control tick, or the key leaks into `session.Input` first.
- **Hold-to-show player list:** read `actions.Held(...)` in `session.Ticked`, like `highlightHeld` (`:353`).
- **Free keys:** Tab is already `change_squad` (`Faithful.cs:46`), and `ToggleSettings` was moved off Tab for that reason (`GameHost.cs:193-194`). Enter is unused in play: only screen Submit and list Accept use it. Enter is the natural chat key; the player list needs a different key.
- **KeyMap:** `KeyMap.Map` (`KeyMap.cs:21-60`) converts Silk keys to engine keys. It has no `KeypadEnter` mapping; that key is special-cased only for the settings panel and the debug menu.

## 4. Command line

- **Parsing.** `GameOptions.Split` (`GameOptions.cs:66-112`) takes the game's own flags; everything else goes to `WorldOptions.Parse`, which throws `unknown option` (`WorldFrame.cs:383`). So `--host [port]`, `--join <ip[:port]>` and `--name <n>` must become `case`s before `default: rest.Add(a)` (`GameOptions.cs:109`), with fields beside `Load` (`:33`), plus a line in `Usage` (`:35-63`).
- **Startup branching.** `Program.Main` decides `toTitle` from the flags (`Program.cs:45`) and validates combinations (`:79-143`, e.g. `--load` against `--new-game`). For `--join`, the client can boot to the title world and then run the join stages. For `--host` with `--new-game` or `--load`, it can reuse the existing paths.
- **Headless server.** Today it needs a GPU:
  - `Run()` → `Screenshot()` → `new VulkanDisplay(null, …)` (`GameHost.Screenshot.cs:95`).
  - `Boot` always builds the renderer (`GameHost.cs:96`), and the session takes `gpu.Terrain.HeightAt` (`:104`).
  - `BootSimulation` makes no GPU resources (`:111-116`), and tests build `WorldSession` without a GPU (`FaithfulInteractionTests.cs:33`). So a dedicated server would need a new host path: `WorldFrame.Load`, then `BootSimulation`, then `WorldSession(heights.HeightAt)`.
  - `GameConsole.AttachToParent` already gives terminal output.
- **Two instances on one machine (Observed from code):**
  - The second instance gets no log.txt: the file opens with `FileShare.Read`, the `IOException` is swallowed and it returns null (`GameLog.cs:29-35`).
  - `meitou.user.json` is read from the working directory first (`UserConfig.cs:96-104`) and rewritten whole on exit (`Interactive.cs:599`). Running each instance from its own working directory keeps names and configs apart.

## 5. Settings persistence

- `UserConfig` (`UserConfig.cs`) is serialized as camelCase JSON (`:94`).
- Adding `public string? PlayerName { get; set; }`, `LastJoinAddress`, `HostPort` needs no version bump. A bump to `CurrentVersion` is only needed for a rename or retype (`:20-25`).
- Unknown keys are kept through `[JsonExtensionData] Unknown` (`:30-31`), and a bad value falls back to its default (`:118-153`).
- Write it with `config.Save()` (`:222-227`), or let `SaveSettings` on exit do it (`Interactive.cs:609-626`).
- The launcher edits the same file (`:14`). `LauncherSchema` lists only the keys the launcher shows, so new keys can stay out of it.

## 6. Host-loop findings that affect choosing L or S

- **Not every command goes through `Say`.** `PlayerInterface.Say` (`PlayerInterface.cs:56-60`) is the single place UI commands enter `session.World.Commands`. But `Meitou.Game` enqueues directly at 29 other sites, including:
  - `SendFocus`: a `FocusCommand` from the local camera every 50 units (`GameHost.Characters.cs:27-35`). The world's active area follows one camera.
  - `UpdateNav`: a `RepathCommand` sent whenever a navmesh zone finishes on a background thread (`GameHost.cs:322-326`, `volatile navChanged`). Its timing depends on the machine.
- **Pause and speed are not commands.** `PressButton` (`Hud.cs:62-66`) and `WorldSession.Tick` (`WorldSession.cs:138-143`) change `Simulation` directly. Menus set `RequestedPause` (`Faithful.cs:17`). All three need network routing; that is also where "X paused" comes from.
- **The interactive game is not deterministic by design.** `BuildSystems` sets `Background = interactive` for population building on a worker, and `SynchronousPaths = !interactive || IsDuel` (`GameHost.Saving.cs:60`, `:64`). The comment says "the duel replays exactly: its paths are answered inside the tick, never on a thread whose timing differs". Per their doc comments, `PopulationSettings.Background` is false only in tests (`PopulationData.cs:199-200`) and `SynchronousPaths` is false in the interactive game (`StandardSystems.cs:51-52`). Lockstep (L) would have to force both into synchronous mode.
- **Gameplay rules come from each machine's config.** Attack slots, late defences, the off-screen switches, doors closing and campaign ancestors are set at `GameHost.Saving.cs:58-67`, and live from the settings callbacks (`Interactive.cs:113-121`). Tick rate and threads come from `config` (`GameHost.cs:104-106`). Either the host decides these or the Advanced/Fixes settings pages are locked for clients.
- **The UI assumes one player.** `session.World.Player` (`PlayerState.cs`) holds one faction, money and selection. `IsPlayer` drives name tags, the squad bar and `PlayerCharacters()` (`PlayerInterface.cs:39-40`).
- **Network pump placement.** Poll the network after `window.DoEvents()` and before `session.Advance(frameDt)` (`Interactive.cs:422`), next to `HandleSaves`/`HandleNewGame`/`HandleMainMenu`/`StepSceneLoad` (`:426-429`). That is a tick boundary with no world tick running.