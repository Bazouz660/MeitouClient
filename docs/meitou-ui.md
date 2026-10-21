# Meitou UI kit

Design reference for the Meitou UI mode ([native-ui](native-ui.md)): the game's materials (rust,
worn paper, charcoal button plates, Exo 2 SemiBold, `kenshi_colours.xml`) as floating plates with
soft corners, used by the Meitou title and settings and, in the Meitou UI mode, by the in-game tooltips (one nine-sliced `rust` plate, radius 10; [ui-screens](game/ui-screens.md#tooltips)). In-game HUD and windows currently use
the original layouts in both modes. Values: [tokens.json](meitou-ui/tokens.json). Live
components: [reference/](meitou-ui/reference/) (web, React 18): run
`tools/scripts/meitou-ui-textures.ps1` once (crops the textures from the install into git-ignored
`reference/textures/`), then open `reference/index.html`. Online copy (private):
https://claude.ai/artifact/U49EczXEMYKM29AyPSGJPm.
The project site ([site/](../site/), Astro + nginx container) uses a texture-free web variant in
`site/src/styles/meitou.css`: same tokens, plates/paper/buttons drawn in CSS, no sprites.

## Surfaces

| Surface | Build | Text |
| --- | --- | --- |
| Plate (windows, tooltips, dialogue, clock) | `rust` under `plate-tint`, 1px `plate-rim` inset, `radius-card`, `plate-drop` | `main` |
| Paper card (cards, primary action, save card) | `paper-inner` stretched whole, `paper-border` inset, `radius-card` | `secondary` |
| Button plate (buttons, selected tab) | Button1Skin 9-slice (6) at 70% brightness, clipped to `radius-btn`; raised skin at 85% on hover, pressed skin for selection/press | `main`, `paper-light` on hover |
| Message banner | MessageSkin (caps 82 / 53), square | `secondary` |

- Never stretch the uncropped GenericTextBoxSkin: its 5px alpha rim (insets L5 R5 T5 B4) reads as a
  halo. Use `paper-inner` and draw the edge (2px `paper-edge`, 1px faint line, 10px `paper-wear`).
- One paper (primary) action per screen; everything else is a plate.
- No window frames. Plates float over the world; behind a stack that must read over the scene, a
  vignette: `radial-gradient(ellipse 620px 470px at 4% 100%, rgba(8,6,5,.93) 25%, rgba(8,6,5,.6) 55%, transparent 80%)`.

## Layout and states

- Radii: buttons/tabs 7, cards/windows/tooltips 10, portraits/badges 4, bars/items 3.
- Main menu (1600×900): stack 320 wide, 56 from left, 36 from bottom; save card, 8 gap, plate
  buttons 38 tall 6 apart, Exit 4 further. Icon column 24, 12 to the label.
- Type: Exo 2 600. 24 window title, 22 primary label (upper, 0.08em), 20 headings, 16 body,
  14 menu items (upper, 0.1em) and stat rows, 12 captions, 10 key badges (upper).
- Focus 2px `brass-light` ring offset 2. Meitou-only keyboard focus (Tab, Shift+Tab over a window's buttons) draws it round the button in the Meitou UI mode, also on the original layouts' windows ([ui-input](game/ui-input.md#edit-boxes-and-keyboard-focus)); the Faithful mode shows none. Disabled `greyed-bright`, plate at 50%. Paper brightens 10%
  on hover and drops 1px on press. Settings/title hover brightening eases in/out over 160ms.

## Copy

Nouns for labels, verbs for buttons, title case in source (menu styles uppercase it). Numbers in
full (`64 / 80`), money `c.4,250`, true minus `−14`, details joined with ` · `, no percentages,
emoji or exclamation marks.

## Textures

Atlas `data/gui/gfx/Kenshi_UI.png`; rectangles from `data/gui/skins/kenshi_skins.xml`
(**Verified**: read from the XML). The script lists every crop; notable:
GenericTextBoxSkin 341,446,186,102; Button1Skin 612,766 (pushed 741,766) 118×45; MessageSkin
761,251,170,70; rust 1342,120,492,546; menu icons from the order sprite 0,0,256,170.
**Observed** (visual inspection of the cropped `reference/textures/rust.png`): rust includes a
T-shaped metal joint. Settings use its uninterrupted right sheet (atlas 1620,136,200,510),
baked as a continuous window surface rather than stretching the joint across the dialog.
**Observed** (read installed `kenshi_skins.xml`): `Kenshi_CloseButtonSkin` is 31×32;
normal/highlighted/pushed atlas crops start at (697/733/769,542). Settings reuse this skin.
**Observed** (installed `kenshi_skins.xml`): `Kenshi_Button1Skin` normal/highlighted use
the same raised atlas pieces; pushed selects the separate sunken pieces at x741.
**Observed** (installed `kenshi_images.xml`): the order sprite's named pointer images include
arrows, talk, light, lock/lockpick, door/escape, house, tools, mining, lifting, pickup, medical,
hand, search, use/gears, steal, knockout, scavenge and invalid. They are action cursors, not
dedicated save/load icons. `Kenshi_SaveGamePanel.layout` uses a plain captioned Save button;
no dedicated save image was found in these resources or by inspecting the atlas.

## Multiplayer

Co-op windows, keys and palette; all engine choices (the game has no multiplayer), in both UI modes. Protocol and
sessions: [net](net.md), [simulation](simulation.md#multiplayer-host-authoritative-co-op).

### Plumbing and ownership

- Contract (`Meitou.Game/Multiplayer/NetUiContract.cs`): the game host sets `PlayerInterface.Net`
  (`IMultiplayerView`: role, phase, local player, roster, rules, quality, end, paused-by, events) and handles
  `MultiplayerRequested` (`HostGame`, `JoinGame`, `CancelJoin`, `StopHosting`, `LeaveGame`). `Net` null or role
  `None` is single-player: every hook is inert.
- Own characters: `Mine(c)` = `IsPlayer` and, in a session, `CharacterSnapshot.Owner == LocalPlayer`. Click and box
  selection, the squad bar, portraits, the Squads tab, the roster text and the jobs list use it; a click on a teammate's
  character inspects it; Alt-loot skips a teammate's downed character. Treating and carrying (`PickHurtAlly`,
  `PickDowned`), the context menu and the `max faction size` count keep every player character (shared faction).
- `Draw` → `PollNet` once a frame: drains `TakeEvents`, then the window, chat and status frame steps. The windows
  (`multiplayer`, `chat`, `players`) are MyGUI layouts registered after the original ones, using installed skins and
  fonts only; the chat history, player list and indicators are decorated controls drawn by `ArrangeUi`.
- Title: `ImportGameButton` (the layout's column is Continue, New, Load, Import, Options, Credits, Exit: **Verified**,
  installed `Kenshi_MainMenu.layout`) is captioned MULTIPLAYER, enabled, and opens the Multiplayer window; Import has no
  backend. The Meitou title stack shows it only once its menu places it.
- Keys: Enter opens the chat line, P held shows the player list ([ui-input](game/ui-input.md#multiplayer-keys)).
- Time: modal windows never pause while a clock authority is set (`WorldSession.ModalsPause`). A HUD speed button
  pressed without the right (`CanControlTime`: `ClockPolicy.AnyPlayer` or the host) shows "Only the host can change
  the game speed."; the keys reach the host, which refuses with the same text.
- F10 opens the debug menu in a session only when `Rules.Cheats`, else notice "Cheats are off in this game.".
- Ended session: a one-button alert (OK), titled DISCONNECTED once the session reached play, else CONNECTION FAILED;
  the text is `NetDisconnect.Reason`.
- Palette (`PlayerColours`, index = `RosterEntry.Colour`): host 0 sky `#8DCDFF` (`MarkerPlayer`), amber `#F2C14E`,
  orchid `#D69CF2`, mint `#7DDCB0`, rose `#F58FC0`, lime `#B9E36B`, peach `#FFAB7A`, periwinkle `#A4AAFF`; hues ~45°
  apart, no red (enemy) or dark green (ally). Unknown or gone: grey 0.62. Selected: the host the map's
  (0.652, 0.903, 1), others lightened 35 %. A joiner gets its preferred colour when free, else the lowest free; a name
  always goes with the colour.
- Settings (`meitou.user.json`, new keys, no version bump): `playerName`, `playerId` (made on first use; a second
  instance on one machine uses `--config <file>`), `hostPort` 40120, `hostMaxPlayers` 4 (2-8),
  `hostClientsControlTime` true, `hostAllowCheats` false, `recentGames` (address, host, last joined; newest first,
  at most 8, unique by address ignoring case). Passwords are never stored.
- Join address (`JoinAddress`): `host`, `host:port`, `[v6]`, `[v6]:port`, or a bare IPv6 address (host only); port
  1-65535 in digits, default 40120.

### Title entry and Multiplayer window

- Title: MULTIPLAYER sits between LOAD GAME and OPTIONS in the Meitou stack (`MenuButtons`, icon `Kenshi_CharacterSpeedSet`
  group at 1250,765 50x50 in `Kenshi_UI.png`: **Verified**, `kenshi_images.xml`; it is dark, so it reads dim on the plate).
  The stack grows by 44 kit units, so the vignette behind it is 520 tall (the kit's 470 cut the card).
- Window `multiplayer` (modal, centred): a MyGUI layout string (`PlayerInterface.Multiplayer.cs`, `MyGuiReader.ParseLayout`)
  using installed skins and fonts only, identical in both UI modes. Name box (shared by both tabs; `PlayerName`, default the
  user name filtered to letters, digits, space, `-`, `_`, else "Drifter"); HOST and JOIN tabs.
  - Host: games list ("New game" then the load list's saves), port (1024-65535), players 2-8, password, clients control
    time, allow cheats, info panel. The button reads HOST. New game closes the window and opens the new-game panel; its
    Begin raises `HostGame(settings, startId, null)` (`BeginHostedNewGame`). A save raises `HostGame(settings, null,
    folder)`, behind a confirm when a game is running.
  - Join: address (`JoinAddress`, default port 40120), password, recent games (selecting one fills the address). The button
    reads JOIN and raises `JoinGame`; the window stays open with the phase as status ("Connecting to host:port... N s",
    "Joining...", "Loading the world...") and only Cancel works (`CancelJoin`). Esc while connecting is a Cancel. An ended
    session shows `EndText(...).Text` in red and re-enables the controls; once Playing the game goes first into
    `RecentGames`. A world load covers the screen and closes the window itself.
  - `OpenMultiplayer(true)` (Esc menu): Host tab only, no games list, button OPEN, `HostGame(settings, null, null)`.
  - Validation errors show in the status line: name 1-24 characters without control characters and not starting with `/` or
    `#`; port; address. Name, host port, max players and the two ticks are saved to `meitou.user.json`; passwords never.
    `MultiplayerRequested` unhandled: "Multiplayer is not available in this build.".

### Chat

Engine choice (the game has no chat); only in a session (`Net` role not `None`).
- Entry: OpenChat (Enter, outside windows) opens the non-modal `chat` layout (never pauses; `Kenshi_EditBox` 200
  chars + SEND, at `y = 0.47·H`, clear of the HUD's InformationPanel at 0.5176·H: **Verified**,
  `Kenshi_MainPanel.layout`) with the box focused (`MyGuiScreenHost.FocusBox`). Enter or SEND sends and closes; Esc or
  a click away keeps the text as the draft for the next open; Up/Down step through the last 20 sent lines; Tab is swallowed.
- Sending: text goes to `IMultiplayerView.SendChat`; own lines are never added locally, they return as `Chat` events so
  every screen shows the host's order. Chat and session-event lines also go to the Overview Messages tab.
- History (`AddChatHistory`, Kenshi_StandardFont_Medium, bottom left above the entry): last 100 lines kept. Closed: lines
  younger than 12 s, fading over the last 2 s, at most 8 rows; open: the last 14 rows. A player's line starts with the
  name in the player's colour (`PlayerColours`, grey if gone); system and error lines have no name.
- System lines from events: joined, left, lost the connection, kicked, banned, paused, resumed, set the speed to n×;
  "You" for the local player's own change. Starting to host adds "Hosting on port n...".
- Commands (`ChatCommands`, pure): `/help`, `/players` (`#n Name · HOST` or `· ping ms`), host only `/kick <player>
  [reason]` and `/ban <player>`. A player is `#n`, an exact name, or a unique name prefix; a quoted name may hold
  spaces. A client gets "Only the host can kick players."; the host cannot kick or ban itself.

### Player list, name tags, map markers

All three are inert in single-player (`Multiplayer` false): tags and map are exactly the original's.

- **Player list** (`PlayerInterface.Players.cs`): while `PlayerList` (P) is held, a `Kenshi_FloatingPanelSkin` panel
  (0.34 W wide, centred, top 0.14 H) drawn over the windows, all controls unpickable. Title `PLAYERS n / max`, then one
  row per roster entry whose state is not `Away` (those stay in the roster only so tags can still name them): colour chip,
  name in its colour (+ " (you)"), a `HOST` plate for the host, "joining…" while `Loading`, squads, ping coloured green
  up to 80 ms, amber up to 180 ms, red above ("—" for the host). No AFK state: the roster carries none.
- **Name tags** (`PlayerInterface.Stealth.cs`): a teammate's character (`OtherPlayers`) shows its bare name in the
  owner's colour and the owner's name under it (MediumSmall, alpha 0.85); an owner missing from the roster is "Player n" in
  `PlayerColours.Absent`. Own tags keep the original look plus a 2-pixel bar in one's colour. Range and stealth-icon rules
  are unchanged.
- **Map** (`PlayerInterface.Map.cs`): own markers use one's colours (single-player keeps the map's); nests show near any
  player's character. Zoomed in, each teammate's character is a `Character` marker in its owner's colour (tooltip name and
  player); at the least zoom, one marker per (owner, squad) at the mean position, 1.3x size, tooltip owner and squad size.

### Status, Esc menu, players window, alerts

Code: `PlayerInterface.NetStatus.cs`, `PlayerInterface.NetMenu.cs`. All engine choices; inert in single-player except the
Esc row.

- Indicator (`0.12W x 0.03H` at `0.872W, 0.008H`): a client shows three bars (lit 3 / 2 / 1 for a round trip up to
  100 ms / 200 ms / above; green, amber, red) and "{ping} ms"; a host shows "HOSTING · {present}/{max}".
- Starving (client, `Quality.SnapshotAgeSeconds` above 0.5 s): the bars turn red and a plate at `0.16H` reads "Waiting
  for the host... {age} s". The session's own timeout belongs to the game host.
- Paused: under the original `PausedPanel`, "{name} paused the game" ("You ..." for oneself) in the pauser's colour, from
  `IMultiplayerView.PausedBy`. Another player's pause, resume or speed event shows a 3 s banner
  (`Kenshi_FloatinglPanelSmallSkin`, `0.115H`, lower while paused, fading over its last 0.5 s).
- Time buttons: `TimeSpeedButton1..` are disabled while `!CanControlTime(LocalPlayer)`.
- Esc menu (`Kenshi_MainMenuPopupPanel`; rows re-laid on the layout's pitch and the panel resized about its centre;
  geometry **Verified** from the installed layout): single-player NEW GAME, SAVE GAME, LOAD GAME, OPTIONS, MULTIPLAYER,
  EXIT GAME, RESUME; host PLAYERS, SAVE GAME, OPTIONS, STOP HOSTING, EXIT GAME, RESUME; client PLAYERS, OPTIONS,
  DISCONNECT, EXIT GAME, RESUME. The added rows are decorated buttons (`MpOpenButton`, `MpPlayersButton`,
  `MpStopButton`, `MpLeaveButton`). A client's Exit offers leaving and quitting; a host's adds that the others are
  disconnected.
- Players window (`players`, modal, no pause): Player (in its colour, " (you)", " · HOST") / Squads / Ping / Status
  ("Joining" while loading). The host gets confirmed KICK and BAN for a selected non-host row. It closes when the
  session ends.
- Ended session: `ShowDisconnected(NetDisconnect, reachedPlay)` keeps the alert until the title is up, then opens it.
