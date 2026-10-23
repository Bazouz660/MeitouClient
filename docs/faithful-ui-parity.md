# Faithful UI parity

Scope: reproduce original screens and interactions using available game systems.
Missing gameplay systems are tracked independently. Loading a layout alone is
not feature parity. This ledger describes the implementation, not verified claims
about undocumented original behaviour. Original-file facts are in
[MyGUI](formats/mygui.md) and [UI screens](game/ui-screens.md).

Both UI modes share this in-game HUD, windows, Escape menu and input. Meitou keeps
its custom title, transitions and settings ([native-ui](native-ui.md)); sharing
the implementation does not resolve the fidelity gaps below.

## UI implementation and gaps

| Area | Implemented | Remaining UI work |
| --- | --- | --- |
| MyGUI resources | Layout/resource lists, skin atlas pieces including tiled slots, text colours and text client insets, ResourceLayout expansion, ImageTexture and first-frame image sets; localisation ([locale overlay](formats/localisation.md): language folder registered between base and mods, so its fonts and layouts win); font resources through FreeType (source, size, resolution, native hinting, `Codes` ranges, substitute glyph, tab/offset, full Unicode text; [fonts](formats/mygui.md)) | Animated image sets, pointer resources, inline `#RRGGBB` text colours, exact resize rounding |
| Window/input | Original frames, caption, close, pin, drag, modality, tooltips (generic runtime: 0.5 s delay, original metrics and skins; [Tooltips](game/ui-screens.md#tooltips)), tab selection, editable caret/selection, edit-box clipboard (system clipboard) and undo, Tab between edit fields and buttons (Meitou-only traversal; Enter/Space press), Enter default action, dynamic lists with scrolling and clipping; generic ScrollBar (arrows with repeat, page clicks, thumb drag, wheel), sliders, ListBox rows by `HeightLine`, ScrollView canvas and bars, ComboBox popup, MultiListBox columns with header sort (save / load dialogs), list keys (`ListKey`), window Snap / Movable / MinSize / MaxSize, viewport-change alignment, generic resize grips for mods (**Observed**: Kenshi's windows have none; [widget runtime](formats/mygui.md#widget-runtime-2026-10-20)) | Pointer caret placing and selection, the interface sounds without a trigger (docs/audio.md); EditBox scroll bars, ComboBox `ModeDrop` / `SmoothShow`, column resizing, MyGUI's own top-level re-alignment on resize (**Unknown**) |
| HUD | Clock, money, speed/pause, selection, squad bar portraits (still picture, name, selection frame; click selects, double click centres), a squad tab per squad with members (click or Tab switches the bar), medic/rescue with the jobs list (rows with X to remove, dragged to reorder; Shift + right click on a machine or storage adds a job), order toggles (Block, Hold, Jobs, Passive, Ranged, Taunt, Sneak; checked state), Prospect, move speed arrows, inventory/stats/map/tech/squad shortcuts; the game view's hotkeys through the rebinding table (orders on NUM0..NUM8, `[` `]`, the window keys; [ui-input](game/ui-input.md#4-action-table)); name and faction, State / Goal / Encumbrance, the stats lines, the extended lines, the health word, medical rows in the original order with their numbers, layers and arrows; the information panel collapsed with its ^ toggle; the floor text and arrows (PageUp / PageDown) and the build button; tooltips of the orders, shortcut, build and floor widgets, the medical rows, the health word and the KO point line, door and building status words; a selected building's lines, condition rows and the player's Dismantle / pause / door buttons | The behaviour slider, a move speed caption, portrait scrolling, the rarer health words and states (sleeping, caged, eaten, crippled, bleeding...), splints and crushed parts on the bars, the town panel, the Tutorials window (F1, HLP), Shift-assigned jobs other than machines and storages, the stats and extended lines' breakdown tooltips (labels in [Tooltips](game/ui-screens.md#tooltips)), a building's For sale line and public toggle; the context menu has only the verbs Meitou can give (see ui-input.md section 7) |
| Inventory | Original equipment/pocket, bag and limb windows in both UI modes; square cells, equipment footprints, pale empty cells and translucent black item backings, name captions and weight rows; bags sized to their contents; item icons (static files, the game's `data/icons` cache, else drawn from the item's mesh; the name box while loading); pocket/bag/equipment dragging with an icon preview, paused moves and Arrange commands | Building icons, the instance colour of an icon, disabled slot conditions, the item tooltip rows not listed under Tooltips (armour resistances and grade, nutrition, Damage vs), stacking/splitting, equipment swaps/race constraints and prosthetics; exact original resizing; disguise/slavery data bindings |
| Stats | Original frame/panels sized to the supplied reference; category headings, ordered rows, separate values, painted attributes; installed descriptions, affected-stat and training text; fractional-level progress and racial XP bonus | Numeric affected-stat readouts, derived stats where applicable, exact row skins and help spacing |
| Overview | All original tabs; live messages; the Faction tab's name and relation lines ([Faction tab](game/ui-screens.md#faction-tab)); the Squads tab's squad boxes (name box, size, DELETE, draggable portraits); the AI tab's Ditch items, Sleep when injured and Sit when idle ticks with tooltips; installed map image, pan/zoom/centre, player/camera markers, discovered towns and camps, right-click move orders | Faction renaming, contracts and upcoming events, player outposts on the map, crafting queue bindings, the AI tab's other options and formation |
| Escape menu | Original layout, pause/resume, Options via Tab settings, exit confirmation, new game, original named save/load dialogs | Original options window content; menu and dialog keyboard shortcuts beyond defaults |
| Frontend | Original title image/buttons and credits; the original loading screen ([Loading screen](game/ui-screens.md#loading-screen)) and the background loading bar ([Background loading bar](game/ui-screens.md#background-loading-bar)); start selection and Begin using existing spawn backend; seven numeric advanced options (below); save metadata/slot selection/name editing and overwrite confirmation; load confirmation | Splash, character creation, the progress panel (`Kenshi_ProgressBarPanel`), continue-slot persistence (currently newest listed slot, disabled if unreadable) |
| Trade | Both modes: `Kenshi_InventoryTraderWindow` (**Verified** as the trader layout) sized by the original's growth rule, character and trader windows side by side from the top-left margin, the scroll view's frame and template scrollbar (arrows, page clicks, thumb drag, wheel), money line, 18 x 18 stock, ARRANGE for the stock; hold stock onto the pocket or bag grid to buy, own items onto stock to sell; right click buys or sells one unit (Shift the stack); tooltips with Value, Sell value (mark-up %), weight, quality and the weapon rows; the fencing prompt while holding a stolen item over the stock ([ui-screens](game/ui-screens.md#6-inventory-trade-dialogue-build)) | Armour tooltip rows (resistances, grade), outside-trade prices, disabled (red) slot states, a Meitou-styled variant (in-game windows use the original layouts in both modes) |
| Build | `Kenshi_BuildWindow` (Faithful only) opened by B or the HUD build button: categories and groups from the unlocked buildings, name, variant arrows, cost and hours, Commands text, Confirm / Undo, floor arrows, the failure message in the message panel; the covered HUD panels hide while it is open; ghost and site orders ([simulation.md](simulation.md#buildings)) | Building pictures (`BuildingImage` hidden), descriptions without a record text, the original's list order and Commands text (**Unknown**), a Meitou-styled menu |
| Other screens | XML is readable; confirmation message boxes used by menu flows; `Kenshi_ProspectingWindow` (zone, altitude, skill, area, wind, environment, the resource rows; both modes) | Dialogue, the prospecting map overlay, tutorials, import, file dialogs |

## Fidelity audit (2026-10-14)

Faithful mode was checked against the facts in [UI screens](game/ui-screens.md) and
[UI input](game/ui-input.md); no install was at hand, so nothing here is checked
against the running original.

Brought in line with a documented fact:

| What | Now | Source |
| --- | --- | --- |
| HUD money | `c.1,234`, no space after the point | format string `c.{1,num}` (ui-screens.md section 1) |
| Save/load info panel | empty until a save is selected, then the seven lines of the original's data panel: Date, File version, Faction name, Funds, Members size, Location, Day ([Title screen and its windows](game/ui-screens.md#title-screen-and-its-windows)) | FUN_14047e390 / FUN_14047e780 |
| Continue | hidden, not greyed, when no readable save exists | section 3 |
| Esc on a message box | presses its last button (Return already pressed the first) | ui-input.md section 5 |
| T / Y | open the overview on Research / Craft (M on Map); faction has no default key | events 3..6, ui-input.md sections 4 and 6 |

Engine choices that remain; the original is **Unknown** for each:

- Message box captions other than the Exit dialog's ([Title screen and its windows](game/ui-screens.md#title-screen-and-its-windows)), window titles ("INVENTORY - name"; "Prospecting Results" is the exe's text, shown upper-cased like the other titles), confirmation texts other than "Are you sure you want to exit?".
- The faction tab lists every faction but the player's, the `not real` ones and the generation template (the original's filter is **Unknown**).
- Tooltips: the HUD's texts show in a Fixed tooltip on the `TooltipPanel` rectangle; the Floating and Inventory ones use the metrics of [Tooltips](game/ui-screens.md#tooltips).
- Medical rows: the parts in the original's part array order ([part order](game/character-stats.md#part-order)).
- The sandbox keeps Z X C Y P G; with `--sandbox` the faithful C (stats) and Y (craft) shortcuts give way to it.

Deliberate differences from the original:

- The title's version text reads "Meitou - Faithful UI" (the original's is "Kenshi 1.0.68 - x64 (Newland)").
- The message box (Exit and the other confirmations) centres its buttons and draws its text in the light Main colour, for readability (the original:
  buttons where the layout puts them, left of centre, and dark text on the dark window).

`FaithfulInteractionTests.EveryWidgetTheFaithfulModeFillsExistsInTheOriginalLayout` [Slow] binds the HUD and every faithful screen against the installed layouts. It
fails on any text, action, list or progress bound to a widget the layout lacks. It also prints the absent widgets that are only hidden or styled, and the named widgets
that keep their editor caption, which is the list of original text the mode does not fill yet. First run against the install on 2026-10-14: it
found the front end, inventory, bag and limb windows sharing one set of bindings (each now binds only its own widgets) and the trader in the generic window
(now `Kenshi_InventoryTraderWindow`); it passes, with about 50 named widgets still showing their editor caption (mostly button labels such as "ARRANGE").

## Gameplay backends tracked separately

| Backend | State / dependency |
| --- | --- |
| Trading and stealing | Shops, prices, buying, selling and fencing exist ([Trade](simulation.md#trade)); building shops exist; stealing from a conscious owner or a shop container and smuggling need implementation |
| Dialogue | No complete conversation/choice/effect backend |
| Research and crafting | Research queue, benches, wells and farms work ([Production](simulation.md#production)); no crafting queue |
| Construction/prospecting | Construction exists ([Buildings](simulation.md#buildings)); prospecting: the task and results ([Prospecting](simulation.md#prospecting)), without the map |
| Squad editing/faction identity | Squads tab: add, rename, delete, reorder, move/swap members, dismiss ([Squads tab](game/ui-screens.md#squads-tab)); dismissed squads are not saved yet; faction renaming (the tab's name box, its rule in [Faction tab](game/ui-screens.md#faction-tab)) needs a command and save support |
| Orders/AI policy | Medic/rescue/stop, Block, Passive, Hold, Sneak, Ranged, Taunt and the move speed act ([Order toggles](simulation.md#order-toggles)); Chase is stored only; editable jobs need commands |
| Inventory mutations | The original's held item ([holding an item](game/ui-screens.md#6-inventory-trade-dialogue-build)): lift one unit or the stack, place, merge, swap, give, drop, put back, in every item window; ARRANGE and right-click moves as the original; paused interface commands; worn appearance/combat refresh; race constraints and blueprints remain |
| Advanced new-game options | Seven numeric options wired to new worlds: hunger, death, damage, production, research, building and nests. Expand below the raised start panel; toggle/Esc collapses it. The bottom panel explains the hovered row (label, slider, arrows or value). Bandit looting and easy prospecting switches remain unsupported. [Persistence](simulation.md#new-game-difficulty) |
| Character creation | `Kenshi_CharacterEditor` after a new game (all start characters, `force race`) and at plastic surgeons (context menu stand-in for the dialogue line); validated `EditCharacter` commands; rows from the original templates, catalog captions, stats panel, platform and stage lighting ([characters](characters.md#character-editor)) |
| Import/reset squad positions | The import window is the original's (list, ticks, advanced options); importing into a new world is not implemented (its rules: [Title screen and its windows](game/ui-screens.md#title-screen-and-its-windows)); the reset tick does nothing yet |

## Validation

`MyGuiScreenTests` exercises template client placement, resize alignment,
immutability, drag capture, editing, close, modal interception, lists, clipping,
tab visibility and cycle rejection. The installed-layout test expands and arranges
all layouts using the configured install; it skips when no install is available.
Render captures are smoke checks. No side-by-side or complete interaction parity
gate has passed yet.
`UiRenderingTests` reads back GPU pixels to check top-left clipping against the
renderer's lower-left targets, with synchronization validation enabled.
`FaithfulInteractionTests` clicks installed title/confirmation/inventory controls:
frontend requests go to the host, and the held item only moves at a simulation
tick. `InventoryCommandTests` checks stale drags, occupied cells, stack limits,
incompatible-equipment rejection and failed-arrangement rollback. `SaveCatalogTests` checks
source identity, header metadata, and named-slot validation. `UiStateTests` covers
editing, map zoom/pan and runtime visibility overrides; `MyGuiColourTests` covers
skin state text colours and dark parchment list text.

The targeted UI/catalog/inventory tests pass.
