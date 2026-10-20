# Meitou UI kit

Design reference for the Meitou UI mode ([native-ui](native-ui.md)): the game's materials (rust,
worn paper, charcoal button plates, Exo 2 SemiBold, `kenshi_colours.xml`) as floating plates with
soft corners, used by the Meitou title and settings. In-game HUD and windows currently use
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

Pending (package U2).

### Chat

Pending (package U3).

### Player list, name tags, map markers

Pending (package U4).

### Status, Esc menu, players window, alerts

Pending (package U5).
