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
| MyGUI resources | Layout/resource lists, skin atlas pieces including tiled slots, text colours and text client insets, ResourceLayout expansion, ImageTexture and first-frame image sets; font resources through FreeType (source, size, resolution, native hinting, `Codes` ranges, substitute glyph, tab/offset, full Unicode text; [fonts](formats/mygui.md)) | Animated image sets, pointer resources, localisation (locale overlay of font files), inline `#RRGGBB` text colours, exact resize rounding |
| Window/input | Original frames, caption, close, pin, drag, modality, tab selection, editable caret/selection, edit-box clipboard (system clipboard) and undo, Tab between edit fields and buttons (Meitou-only traversal; Enter/Space press), Enter default action, dynamic lists with scrolling and clipping; generic ScrollBar (arrows with repeat, page clicks, thumb drag, wheel), sliders, ListBox rows by `HeightLine`, ScrollView canvas and bars, ComboBox popup, MultiListBox columns with header sort (save / load dialogs), list keys (`ListKey`), window Snap / Movable / MinSize / MaxSize, viewport-change alignment, generic resize grips for mods (**Observed**: Kenshi's windows have none; [widget runtime](formats/mygui.md#widget-runtime-2026-10-20)) | Pointer caret placing and selection, tooltips, the interface sounds without a trigger (docs/audio.md); EditBox scroll bars, ComboBox `ModeDrop` / `SmoothShow`, column resizing, MyGUI's own top-level re-alignment on resize (**Unknown**) |
| HUD | Clock, money, speed/pause, selection, squad bar portraits (still picture, name, selection frame; click selects, double click centres), a squad tab per squad with members (click or Tab switches the bar), medic/rescue with the jobs list (rows with X to remove, dragged to reorder; Shift + right click on a machine or storage adds a job), order toggles (Block, Hold, Chase, Passive, Ranged, Taunt, Sneak; checked state) and move speed arrows, inventory/stats/map/tech/squad shortcuts; name and faction, State / Goal / Encumbrance, the stats lines, the health word, medical rows in the original order with their numbers, layers and arrows; the information panel collapsed with its ^ toggle; Floor 0 and the build button; the Prospect button (nearest selected character; the results window below) and the fading biome banner with its sound | The behaviour slider, a move speed caption, more squads and portrait scrolling, extended stats lines, the rarer health words and states (sleeping, caged, eaten, crippled, bleeding...), splints and pending healing on the bars, floor switching and building, town information, the banner's text-size line layout, Shift-assigned jobs other than machines and storages, tooltips; the context menu has only the verbs Meitou can give (see ui-input.md section 7) |
| Inventory | Original equipment/pocket, bag and limb windows in both UI modes; square cells, equipment footprints, pale empty cells and translucent black item backings, name captions and weight rows; bags sized to their contents; item icons (static files, the game's `data/icons` cache, else drawn from the item's mesh; the name box while loading); pocket/bag/equipment dragging with an icon preview, paused moves and Arrange commands | Building icons, the instance colour of an icon, disabled slot conditions, full tooltip details, stacking/splitting, equipment swaps/race constraints and prosthetics; exact original resizing; disguise/slavery data bindings |
| Stats | Original frame/panels sized to the supplied reference; category headings, ordered rows, separate values, painted attributes; installed descriptions, affected-stat and training text; fractional-level progress and racial XP bonus | Numeric affected-stat readouts, derived stats where applicable, exact row skins and help spacing |
| Overview | All original tabs; live messages; the Squads tab's squad boxes (name box, size, DELETE, draggable portraits); the AI tab's Ditch items, Sleep when injured and Sit when idle ticks with tooltips; installed map image, pan/zoom/centre, player/camera markers and right-click move orders | Original map projection/zoom equivalence, settlement discovery/markers, faction data, squad editing, crafting data bindings, the AI tab's other options and formation |
| Escape menu | Original layout, pause/resume, Options via Tab settings, exit confirmation, new game, original named save/load dialogs | Original options window content; menu and dialog keyboard shortcuts beyond defaults |
| Frontend | Original title image/buttons and credits; start selection and Begin using existing spawn backend; save metadata/slot selection/name editing and overwrite confirmation; load confirmation | Splash, advanced option bindings, character creation, loading/progress, continue-slot persistence (currently newest listed slot, disabled if unreadable) |
| Trade | Both modes: `Kenshi_InventoryTraderWindow` (**Verified** as the trader layout) sized by the original's growth rule, character and trader windows side by side from the top-left margin, the scroll view's frame and template scrollbar (arrows, page clicks, thumb drag, wheel), money line, 18 x 18 stock, ARRANGE for the stock; drag stock onto the pocket or bag grid to buy, own items onto stock to sell; right click buys or sells one unit (Shift the stack); tooltips with Value, Sell value and weight; the fencing prompt while dragging a stolen item over the stock ([ui-screens](game/ui-screens.md#6-inventory-trade-dialogue-build)) | Full original tooltip lines (quality, damage, armour, markup %), stack splitting (input **Unknown**), disabled (red) slot states, a Meitou-styled variant (in-game windows use the original layouts in both modes) |
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
| Save/load info panel | the five original labels in order: File version, Faction name, Funds, Members size, Location; the extra "Day" line is gone | FUN_14047e390 / FUN_14047e780 (section 3) |
| Continue | hidden, not greyed, when no readable save exists | section 3 |
| Esc on a message box | presses its last button (Return already pressed the first) | ui-input.md section 5 |
| T / Y | open the overview on Research / Craft (M on Map); faction has no default key | events 3..6, ui-input.md sections 4 and 6 |

Engine choices that remain; the original is **Unknown** for each:

- Message box captions (YES / CANCEL), window titles ("INVENTORY - name"; "Prospecting Results" is the exe's text, shown upper-cased like the other titles), confirmation texts other than "Are you sure you want to exit?".
- Overview faction name shows "Player"; the original shows the player faction's name.
- The hover label is a plain text box; the original uses the HUD's `TooltipPanel`, whose children are not mapped.
- Medical rows: the parts are ordered head, torso, arms, legs (left first) by their names, matching the original for people; an animal's parts keep that grouping.
- Save list rows carry a `[Meitou]` / `[Kenshi]` source tag; the title's version text reads "Meitou - Faithful UI" (the original's is "Kenshi 1.0.68 - x64 (Newland)"), on purpose.
- The sandbox keeps Z X C Y P G; with `--sandbox` the faithful C (stats) and Y (craft) shortcuts give way to it.

Deliberate multiplayer deviations ([meitou-ui](meitou-ui.md#multiplayer); the original has no multiplayer):

- The title's `ImportGameButton` reads MULTIPLAYER and opens the Multiplayer window (both modes; Import has no backend). If Import is implemented, Multiplayer moves to a control cloned from its skin and font.
- The Esc menu gains multiplayer rows: MULTIPLAYER in a single-player game; PLAYERS, STOP HOSTING or DISCONNECT in a session.
- Modal windows do not pause the game while a multiplayer session runs.

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
| Squad editing/faction identity | Squads tab: add, rename, delete, reorder, move/swap members, dismiss ([Squads tab](game/ui-screens.md#squads-tab)); dismissed squads are not saved yet; faction naming needs implementation |
| Orders/AI policy | Medic/rescue/stop, Block, Passive, Hold, Sneak, Ranged, Taunt and the move speed act ([Order toggles](simulation.md#order-toggles)); Chase is stored only; editable jobs need commands |
| Inventory mutations | Validated pocket/bag/equipment moves and arrangement, paused interface commands, worn appearance/combat refresh; stack/split, swaps, race constraints, prosthetics and character transfers remain |
| Advanced new-game options | Start records/spawning exist; per-session option overrides need integration |
| Character creation | `Kenshi_CharacterEditor` after a new game (all start characters, `force race`) and at plastic surgeons (context menu stand-in for the dialogue line); validated `EditCharacter` commands ([characters](characters.md#character-editor)); the idle stance slider and the hit-points stats line remain |
| Import/reset squad positions | Save loading exists; import/reset rules need a separate backend |

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
frontend requests go to the host, and inventory drags only mutate at a simulation
tick. `InventoryCommandTests` checks stale drags, occupied cells, stack limits,
incompatible-equipment rejection and failed-arrangement rollback. `SaveCatalogTests` checks
source identity, header metadata, and named-slot validation. `UiStateTests` covers
editing, map zoom/pan and runtime visibility overrides; `MyGuiColourTests` covers
skin state text colours and dark parchment list text.

The targeted UI/catalog/inventory tests pass.
