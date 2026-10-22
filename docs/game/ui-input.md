# Input and controls

Original OIS/GUI/action dispatch research, controls.cfg and exe byte/name-table checks. Source
scopes/action tables below. Meitou `InputState`/`InputBindings` consume presses once per control
tick; host picking/UI issue commands, not direct simulation mutation. [Simulation](../simulation.md#player).

## Binding encoding

**Verified** code=scancode | Shift0x100 | Ctrl0x200 | Alt0x400. Low-byte OIS scancode; left/right
modifiers share bits. Low-byte0 mouse=(buttonIndex+1)*4096 (Mouse1/2/3=0x1000/2000/3000).
Name formatter prefixes Ctrl+Alt+Shift, unnamed Key_n, mouse Mouse_n. Key names are a256-entry
exe table;111 null. Preserve scancode semantics rather than substituting ASCII key codes.
Controls.cfg first line version1, repeated action=KeyName entries allow multiple bindings.
Missing/empty file writes defaults; version<1 treats changed camera/floor/build bindings specially
(exact application partly Observed). Default-unbound and UI-unrebindable actions remain in tables.

## Dispatch and selection

Window/OIS state, modifier composition, binding lookup, action event and GUI/pointer mode are
separate stages. UI consumption/pause/loading/editor state can block commands. Press/release,
held movement, click/drag, double-click, floor/camera and context verbs have separate handlers;
do not trigger game actions solely from a matching key. Selection ownership and inspection differ:
inspecting non-player characters updates panels without giving orders to them. Context menus
choose verbs based on target type/state, faction, carrying and available interaction.

Meitou selection commands retain the current selection when no valid player characters are
targeted (empty ground or an empty drag box). Selecting valid members replaces or adds to it;
inspecting another faction keeps the selection and shows that character in the panels (see [UI screens](ui-screens.md#4-hud-mainbargui)).
While a selected character sneaks, an NPC that is up gets the verb "Stealth KO (N%)" and a quick right click on it gives
that instead of an attack ([combat](combat.md#stealth-knock-out)); the first sneaking selected character goes.

Original strategy camera actions/zoom flags are in [camera](../formats/camera.md). Engine defaults:
Space pause,F2/F3/F4=1/2/5,F5 save,F9 load; Tab is the original's change_squad (the settings open from the Esc menu, ToggleSettings is unbound by default); current complete keys in [viewer](../viewer.md)
and [engine](../engine.md). User bindings and viewer/cinematic bindings have separate lifetimes.
Settings-tab labels/type/ranges/restart semantics: [settings](../formats/settings.md).

### 1. Pipeline — layout/example

```
OIS keyboard/mouse -> MainListener (keyPressed/mouseMoved/mousePressed)
   -> inject into MyGUI (always)
   -> binding table (FUN_140360b30) unless a text field / key capture owns the keyboard
        -> sets held-state flags (polled by camera, selection code)
        -> appends event ids to a per-frame queue
   -> GameLoop_InputActions (FUN_140788100) drains the queue: MainBar hotkeys first, then game actions
   -> frameEnded (FUN_14082b6e0) clears the pressed flags of event actions and the queue (FUN_140360a80);
      held flags are cleared by the release handler (FUN_140360da0), which looks the released code up again
```

### 4. Action table

Binding maps: general map at Controls+0x28, build-mode map at +0x50 (checked first while mode field +0xd4 == 1,
**Observed**). Actions with `build_` prefix use context 1.

| Action | Default | Event id | Kind |
|---|---|---|---|
| mouse_select | Mouse1 | 0 | held |
| mouse_command | Mouse2 | 0 | held |
| mouse_rotate | Left Control or Mouse3 (two alternative bindings, not a chord) | 0 | held |
| camera_forward / back / left / right | Up+W / Down+S / Left+A / Right+D | 0 | held |
| camera_tilt+ / tilt- | `,` / `.` | 0 | held |
| camera_rotate_left / right | Q / E | 0 | held |
| camera_zoom_in / out | Home / End | 0 | held |
| highlight | Left Alt | 0 | held |
| toggle_fps_camera | `;` | 0x28 | event |
| toggle_camera_grid | Shift+`;` | 0x29 | event |
| floor_up / floor_down | PgUp / PgDn | 0x24 / 0x25 | event |
| speed_1 / 2 / 3 | F2 / F3 / F4 | 0x13 / 0x14 / 0x15 | event |
| stop_movement | R | 0x16 | event |
| pause | Space | 0x12 | event |
| toggle_block / hold / passive / ranged / sneak | NUM0 / NUM1 / NUM2 / NUM3 / NUM4 | 0x17 / 0x18 / 0x19 / 0x1b / 0x1c | event |
| toggle_taunt / cycle_run_speed | NUM5 / NUM6 | 0x1d / 0x1e | event |
| medic / rescue | NUM7 / NUM8 | 0x1f / 0x20 | event |
| toggle_jobs / prospect | unbound | 0x1a / 0x21 | event |
| screenshot | SYSRQ, F8 | 0x10 | event |
| toggle_bar | F7 | 8 | event |
| toggle_inventory / stats / map | I / C / M | 1 / 2 / 3 | event |
| toggle_faction / research / crafting | unbound / T / Y | 4 / 5 / 6 | event |
| toggle_help | F1 | 7 | event |
| quicksave / quickload | F5 / F9 | 0xe / 0xf | event |
| toggle_build | B | 10 | event |
| focus_char | F | 0x11 | event (accepts Shift) |
| select_0..8 / select_9 | keys 1..9 / key 0 | 100..109 | event (accepts Shift) |
| select_all | GRAVE | 0x22 | event (accepts Shift) |
| change_squad | Tab | 0x23 | event |
| character_next / prev | `]` / `[` | 0x26 / 0x27 | event |
| build_undo / build_apply | Backspace / Space | 0xb / 0xc | event, build map |
| build_rotate_left / right | `,` / `.` | - | build map |
| build_move_up / down | `=` / `-` (+ NUM+ / NUM- registered) | - | build map |
| build_tilt_increase / decrease | `]` / `[` (+ NUM+ / NUM-) | - | build map |
| editor_toggle | Shift+F12 | 9 | event |
| editor_delete | Delete | - | event |
| reload_biomes | Ctrl+F6 | 900 | event |
| rebuild_navmesh | Ctrl+Shift+F11 | 0x386 | event |
| gizmo_move / rotate / scale | H / J / K | 0x398 / 0x399 / 0x39a | event |

Meitou's build map (`PlayerInterface.Build.cs`) is live only in build mode and takes its keys before anything else: the
documented speeds, Shift as the slow modifier of the up/down move; the numpad aliases are not mapped (the numpad is the
cinema camera's). `[` `]` tilt there, not character_next / prev. Left click queues the ghost.

### 6. Event dispatch

GameLoop_InputActions (FUN_140788100; step 4 of [game-loop.md](game-loop.md#update-threads-and-budget)) drains the queue each frame. **Observed**: it does nothing while the loading
window is visible (FUN_1406e2100 is a visibility test on the LoadingWindow, constructor 14091a280); the Esc menu
being visible (EscMenu, constructor 1409180e0) ignores every event; two further gates suppress most game
events: a byte at PlayerInterface+0x2f0 and a visibility flag of the widget at GUI+0x20 (FUN_1406e1fd0, not
identified; pause 0x12 is only blocked by the second). While the MainBar object exists every event except the
100..120 and 0x22 group first goes to the MainBar hotkeys (FUN_1407262b0):

| Event | Effect (**Observed**) |
|---|---|
| 1 | open the inventory window of the selected character (FUN_140724980 -> FUN_1406e6f10; target window inferred from the action name `toggle_inventory`) |
| 2 | open or, when already open, close the per-character window for the selection (FUN_1406e5f10; stats window inferred from `toggle_stats`) |
| 3 / 4 / 5 / 6 | request Overview tab Map / Faction / Research / Crafting (HUD field +0xd0 = 0/1/2/3) |
| 7 | toggle the visibility of one GUI window (FUN_1406e1f20; taken to be the Tutorials window, **Unknown** which) |
| any other id | forwarded to the orders panel handler FUN_1407226e0, which acts on 0x17..0x21 (orders buttons) and ignores the rest |
| 8 | not in the hotkey function: handled in the action switch by FUN_1406e2a20, which flips the GUI-hidden flag at GUI+0x330 ("toggle whole GUI") |

Squad events (**Observed**): select_0..9 (100..109, `FUN_1407f90b0`) and select_all 0x22 (`FUN_1407f8d00`) act on the active squad's members
([squad tabs](ui-screens.md#4-hud-mainbargui)); change_squad 0x23 (`FUN_1407f6380`) with more than one squad makes the next one in order (wrapping) that has
members active, and leaves the selection alone.

Meitou's first aid and rest bindings (2026-10-13, `PlayerInterface`, engine choices; simulation side in [simulation.md](../simulation.md#bodies)). The original's own: `medic` (NUM7, event 0x1f) and `rescue` (NUM8, 0x20) are
orders-panel toggles forwarded to FUN_1407226e0 (the Medic and Rescue buttons of the panel, [ui-screens.md](ui-screens.md#4-hud-mainbargui)), which dispatch orders 58 and 148: one-shot, or a job with Shift ([perma-jobs](ai-tasks.md#perma-jobs), **Observed**); the targeted order is the context menu
verb 25 "First Aid" (pointer mode 1 `med`, [pointers](#mouse-pointers); Meitou's context menu also provides it). The numpad belongs to the cinema camera (`Cinema`, docs/viewer.md), so:

| Input | Effect |
|---|---|
| right click on one of the player's own characters with something to bandage | the selected characters treat it (`TreatOrder`); a lone selected character on itself treats itself |
| `H` | Medic for the selection: each looks for a patient once; with Shift the medic job (58) is added to their lists |
| `L` | the selection rests (sits down; **no binding in the original**); any move order or `R` ends it |

| Input | Effect |
|---|---|
| Ctrl + right click on a knocked-out or dead character | the nearest selected character picks it up (`PickUpOrder`); on a body a selected character carries, puts it down (`PutDownOrder`) |
| right click on one of the player's own downed characters with nothing to bandage | picks it up (as the original's quick right click; with something to bandage it is first aid, as above) |
| Alt + right click on a knocked-out or dead character | the nearest selected character takes all that fits from its inventory and backpack (`LootOrder`; no window) |
| right click on anyone else's knocked-out or dead character | the nearest selected walks over and its whole inventory opens in a second character window beside the inventory (worn gear in the equipment slots, its bag in a bag window): items are dragged out of it or into it, onto equipment slots too (`OpenLootOrder`, [simulation](../simulation.md#carrying-rescue-and-looting); engine choice, the original's loot window is **Unknown**) |
| an item dragged out of the inventory window and let go over the world | dropped on the ground 3 units ahead of the character (`DropInventoryItem`; worn gear too, not locked-on items or fitted limbs; engine choice, the original's drop rule is **Unknown**) |
| right click on an item in an open trade window | buys one unit of a stock item, or sells one unit of the customer's own item (Shift: the whole stack as far as money and room go); see [ui-screens](ui-screens.md#6-inventory-trade-dialogue-build) |
| right click on an item while the inventory and another item window are open | one unit moves to the other window, where it first fits (stacks of it, then free cells), in both directions; Shift: the whole stack (**Observed**, release handler `FUN_140714a20`: the one-unit move `FUN_140713d20`, repeated while Shift is held until it fails). Meitou: between the lead's inventory or bag and the shown machine (its inputs and output), storage, container, or the open loot (`PutInMachine` / `TakeFromMachine` / `TakeLootItem` / `PutLootItem` with a count, `InventoryCommands.MoveUnits`) |
| `J` | Rescue for the selection (the original's NUM8): each looks for a body once; with Shift the rescue job (148) is added |
| `K` | the selected carriers put their bodies down |

### Movement marker

**Observed** (owner's base-game example): right-clicking a building targets its floor rather than terrain behind it.
Meitou movement picks loaded buildings' floor/stair collision up to the viewed floor before terrain; wall hits drop to the floor below
(up to 4 units across wall thickness). Clicks, queued moves and held-button steering share this destination and marker.
Buildings without a readable interior mask/floor collision still use terrain; the original's pick filters: [building picking](#building-picking-and-selection).
Outside the rooms, the ray's first descent onto a kept navmesh surface more than 5 units above the terrain (a wall top, ramp, deck) wins
over the terrain, and its height is the goal's floor (engine choice). **Observed** (JC2, zone 20.29, scratch probe): a ray onto Defensive
Wall II `16-Newwworld-INGAME` gave Y 1218 (terrain 1103); before, the goal resolved to the blocked ground under the wall.

**Observed** (decompile): a ground move order shows an entity of `position.mesh` (a downward cone over a flat ring, 0.29 × 1.56 units)
scaled 8 on its scene node, at the ray's ground point (the terrain-click handler FUN_140800250, show FUN_1407f2660). Built in FUN_1407f9530:
a clone (`movemarker`) of `debug_marker`, pass 0 set to alpha blending (`setSceneBlending(0)`, SBT_TRANSPARENT_ALPHA). Each frame FUN_1407f92d0 sets the fragment constant
`colour` of pass 0 to (0.612, 0.694, 0.529, t) and of pass 1 to (0.306, 0.347, 0.2645, 0.6 t), then lowers t (1 at the click) by the scaled
frame time `DAT_142133794` ([game-loop.md](game-loop.md#the-three-time-values): 1 s of game time, 0.01 a frame at speed 0) and hides it at
t ≤ 0. Shown only with `Movement marker` ([settings.md](../formats/settings.md); global `DAT_142133511`, `Rotation marker` is `DAT_142133510`).
**Verified** against `data/materials/debug.material` and `materials/forward/basic.program` / `basic.hlsl`: `debug_marker` pass 0 is
`depth_func less_equal`, pass 1 `depth_write off`, `depth_func greater`, `alpha_blend` (the part behind the scene); both `Basic_Lit_VP`
(`NORMALS`: `rgb *= saturate(N·sun) * contrast + 1 - contrast`, contrast 0.5) and `Basic_FP` (`colour × vertex colour`).
Meitou (`MoveMarkerRenderer`) draws both passes after the tone map, so the colours show as given whatever the exposure, sorting fragments by
the near slice's scene depth, multisampled (up to 8×) and resolved over the picture: the mesh's rough band at the cone's widest part
and the notch on the stem are in `position.mesh` itself; on every ground move, following the cursor each frame while the button steers, always on (settings.cfg is not read yet).

### Mouse pointers

**Verified** (read `data/gui/pointer/kenshi_pointers.xml`, `images/kenshi_images.xml`): 25 `ResourceImageSetPointer`s (default `arrow`, layer
`Pointer`, the last in `core_layer.xml`), each naming a `Size`, a hot spot `Point` (pixels from its top left) and an image set whose one frame
(group and index `Pointer`) is a crop of `Kenshi_UI.png`. An entry with an empty name and no values also exists. Some frames differ from the
pointer's `Size` (`lift` 34×30 in 35×35, `hand` 33×35 in 32×32, `pick_item` 32×32 in 35×35). **Verified** (MyGUI `ResourceImageSetPointer::setPosition`, read on GitHub): the image box is `Size` large at mouse − `Point`, so the frame is stretched to `Size`; screen pixels (no UI scale).

**Observed** (FUN_1406e3d60, which calls `PointerManager::setPointer`): pointer mode → name: 0 `arrow` (`invisible` with the free camera
and the Esc menu hidden), 1 `med`, 2 `search`, 3 `lift`, 4 `pick_item`, 5 `attk`, 7 `talk`, 8 and 10 `s_talk`, 9 `use`, 0xb `build`,
0xc `door`, 0xd `door_escape`, 0xe `lock`, 0xf `lockpick`, 0x10 `house`, 0x11 `green`, 0x12 `mine`, 0x13 `spanner`, 0x14 `light`,
0x15 `steal`, 0x16 `hand`, 0x17 `invalidmove`, 0x18 `scavenge`, 0x19 `knockout`; 6 has no case. It is called through thunk `140053acb`.
The modes a building hover sets are in [building picking](#building-picking-and-selection) (`house` 0x10 is returned by none of them); a ground
hit with no walkable point sets `invalidmove` (`FUN_140800250`). Which other hover state sets each mode is **Unknown**.

Meitou (`PlayerInterface.Pointer.cs`, engine choices): the window's own cursor is hidden and the pointer drawn last in the frame (not in
screenshots or cinema takes). It follows the quick right-click order, so it never disagrees with the click: with a selection, over a
non-hostile trader or a non-hostile character with a talk dialogue (`World.CanTalk`) `talk`, a hostile living character `attk` (also without a selection, red outline; any other non-hostile one: no quick order, `arrow`, white outline, a right click moves), an own hurt character `med`, an own downed one `lift`, anyone else's downed one `search` (a right click opens its inventory), else a loose item (`pick_item`, a right click picks it up: [ground items](../simulation.md)); Ctrl over a downed character
`lift`, Alt `search`; otherwise, over the interface, the settings panel or the loading screen, `arrow`. The free camera hides it (as mode 0's
`invisible`). Interface → Hardware mouse (`HardwareMouse` in meitou.user.json, default off as the game's `hardware_mouse`; settings.cfg is
not read) shows the Windows cursor instead, applied at once.

### Character picking

**Observed** (`FUN_140800250`): the original picks characters with the same physics ray as buildings ([building picking](#building-picking-and-selection)): hits in groups 0, 1,
3, 4 (3 and 4 are ragdoll shapes, [ragdolls](ragdolls.md#collision-groups); what 0 and 1 are is **Unknown**). Meitou (engine choice, [phs](../formats/phs.md)): clicks, quick orders, the hover
label, pointer and outline take the nearest character whose posed mesh the ray meets. Each mesh keeps, from its bind-pose vertices, one
box per bone (vertices by their strongest weight; an unskinned part one box), grown 0.4 units; the boxes are posed by the live bone palette,
so people and animals pick over their whole drawn body, a ragdoll where it lies. **Verified** (sandbox probe, 2026-10-16): a person hits from the feet to 18 units
up and misses at 22; a bonedog hits up to 10. A character the renderer does not draw falls back to a capsule 18 units tall.
A building seen from outside (its interior not shown, [interior masks](../formats/zones.md#interior-masks)) hides what is inside or behind it from these picks and from
ground-item picks: the ray stops where it meets the building's `interior mask` collision (engine choice, standing in for the original's building hits in the same ray).

### Building sign highlight

**Observed** (owner's original-game report): holding Alt highlights building signs in yellow.
**Observed** (merged installed game data, reader survey): BUILDING `is sign` is true on `993-gamedata.base`
(Sign Medic) and `3436-otto.mod` (sign3_bar); category `sign` alone is insufficient (`972-gamedata.base`, Weapon Sign, has the flag false).
Meitou's rebindable `Highlight` action defaults to either Alt key. While held in the focused gameplay view,
real parts whose owning BUILDING has `is sign` receive a yellow tint, including layout objects and both LODs
during a transition. Release restores their materials. Loading, menus, hidden UI, free camera and cinema suppress it.
Rendering details: [objects](../render-objects.md#building-sign-highlight).

### Ground item labels

**Observed** (owner's original-game report): holding Alt shows a small name label over each loose item.
**Verified** (`Kenshi_ItemDropLabel.layout`, `kenshi_colours.xml`): root `Kenshi_GenericTextBoxFlatSmallSkin` 0.0521 x 0.0259 of the screen, layer Back,
pointer `pick`; `ItemNameText` (`Kenshi_TextboxStandardText`, Kenshi_StandardFont_Medium) centred. Colours: `DropItemLabelBack/TextDefault` #ffffff,
`...Selected` #66ff66, `...SelectedStealing` #ff6666.
**Observed** (`ItemManager::ItemLabel`, `FUN_1401649e0` / `FUN_140162950`): the root's width is fitted to the text; a quantity above 1 is appended
(exact text **Unknown**); the label widget takes a mouse click; hovered labels turn Selected, or SelectedStealing when the item counts as stolen
(owner-faction test, **Unknown** detail). Weapons, armour and some other item types take their text colour from the item's grade
(`ItemGrade0`–`5` by thresholds 16/36/51/66/81, `ItemArtifact`, else `ItemStandard`). Range and anchor **Unknown**.
Meitou (`PlayerInterface.GroundItems.cs`): with the Highlight gating above, every item within 500 units of the camera's focus not hidden by a
building, name only, white, green under the pointer; a label picks like its item (pointer `pick_item`, right click picks it up). Overlapping
labels are stacked above the nearer one. No quantity, grade or stealing colours yet (ground items have no owner).

### Hover outline

Meitou only (Advanced → Hover outline, `HoverOutline`, default on; the game has none). The character under the cursor is drawn alone into a
mask (colour pass, live pose, picture levels, own depth planes round it) right after the portraits, then after the tone map an outline
(about 2.5 px at 1080p, 60 % strength fading outward) is blended round the silhouette, through walls, and its visible part tinted at 11 %, visible meaning its
linear depth within 1 % + 1 unit of the near slice's scene depth (jittered, render size). Outline and tint take the pointer's order's colour: attack red (only for a faction hostile to the player's; a neutral one is white although a right click attacks it), treat green, talk gold, carry/loot blue, else white (no selection or no order). No name or goal label is shown over a character (**Observed** by the owner in play, not traced in the exe: the original shows none for ordinary NPCs, only the outline; Meitou had a plain name and goal label until 2026-10-17). Holding the Highlight key (Alt, which also lights up signs) outlines the same way every character (the squad's too, people and animals) within 500 units of the camera's focus, each in the colour hovering it would give (own hurt green, downed or carried blue, trader or someone to talk to gold, hostile red, else white; all white with no selection); one mask per colour, overlapping outline rectangles joined. Off in the free camera, with the interface hidden and in
screenshots and cinema takes. Not in the Shift+F1 switch list (a game setting, like Attackers per target).

### Selection rings

Each selected character gets a green ground ring (radius 1.6, kept on the water surface for swimmers), hidden in the character editor.
Advanced → Selection rings (`SelectionRings`, default on) turns them off.

### Building picking and selection

The original's mouse code, kenshi_x64.exe 1.0.68. The panels a selected building fills: [ui-screens](ui-screens.md#selected-building).

**Buttons** (**Verified**: Controls `DAT_142133370`, actions registered in `FUN_1403636c0`): held flags `mouse_select` +0xf3,
`mouse_command` +0xf4, `highlight` (Left Alt) +0xf2. Each frame `FUN_140801630` stores edges on PlayerInterface (**Observed**): +0x2f1 select
released, +0x2f2 select pressed, +0x2f3 command released, +0x2f4 command pressed; the two command edges are cleared while the context menu
is visible or was last frame. Then `FUN_140800f10` (drag box) and, unless a box is being dragged, the pick `FUN_140800250` run every frame, so
hover and clicks share one pick.

**Ray** (**Observed**, `FUN_1409b3910`, `FUN_1409b37a0`): a PhysX raycast through all shapes from the camera through the cursor, group mask
`0xffffde7f` (every [collision group](../formats/collision.md#collision-groups-observed-fun_14055f6e0-and-the-loaders) except 7, 8 and 13, the
interior-mask hulls); the hits are sorted nearest first (**Verified**, comparator `FUN_1409b1820` by disassembly: ascending distance at hit
+0x24). Buildings are therefore picked by their collision shapes; a part without collision cannot be clicked. The walk over the hits
(`FUN_140800250`, **Observed**) stops at the first decisive one:

| Hit groups | Result |
|---|---|
| 2, 6, 14 (terrain, foliage) | ground: ends the walk |
| 0, 1, 3, 4 (characters, ragdolls) | that character (`FUN_1407fb410`); behind an already taken building only when the building hit was a passable part (23..26) |
| 3, 4, 28..31 with an item object | that item (`FUN_1407fc440`); behind a taken building only when inside the building's bounding box |
| 5 (doors) | the door; when a building was taken first, that building |
| 9..12, 15..31 (building parts) | building candidate, rules below |

- A building of another faction must be visible (slot 33: flag +0x19d): furniture in a house whose floor (+0xa4) is above the floor the house
  shows is hidden and cannot be picked (`FUN_14092add0`; the town keeps the shown floor per building, 4 = whole building).
- Parts of a building with an interior (`FUN_140291650`) count only when their floor is not above the floor that building shows; the floor of a
  hit comes from its group (`FUN_1409b1850`: 10/16/20/24/29 → 1, 11/17/21/25/30 → 2, 12/18/22/26/31 → 3, 27 roof → 4, else 0). So with a
  floor cut the ray passes the hidden roof and floors and reaches what is shown. When the viewed floor (PlayerInterface+0x1e4, PgUp/PgDn) is
  below the hit's floor, the walk skips ahead to the next hit on the same building at or below it.
- A turret or lamp mounted on the hit building (its slot 135 handle) met within 12 units behind the hit is taken instead (`FUN_1407f2af0`).

**Building hover and click** (`FUN_1407fc6e0`, **Observed**). Every frame over a building it sets the pointer; on a command release (a quick
right click) it gives the order to the selected characters (the shared order dispatch `FUN_1407fa650` / `FUN_1407fae70` / `FUN_1407f9e20`,
target the building, its parent building (slot 59) and the hit's x, z at the zone or terrain height, `FUN_1409b32f0`, not a building
floor); on a select release without a command release in
the same frame it selects. With an animals-only selection (+0x268: no selected character is a CharacterHuman) only the selection runs. Tests
in order (task numbers are [ai-tasks](ai-tasks.md) taskTypes):

| Building | Pointer | Quick right click |
|---|---|---|
| not complete (+0x160, [construction](buildings-production.md#construction-state)) | `build` | 2 BUILD |
| broken (slot 97: +0x3b6 machines, +0x3ec doors, never for plain buildings) | `spanner` | 95 REPAIR |
| door (+0x1a0) of an incomplete house (+0x368) | `build` | 2 BUILD on the house |
| door | own: `lock` if its lock is locked, else `door`; another faction's: `lockpick` if locked (with a tooltip, text **Unknown**), else `door`; `spanner` below 90 % health | another faction's locked door without Alt: "Hold down the ALT key to pick the lock." and a move to the door; otherwise 72 OPEN_DOOR, or 73 CLOSE_DOOR when open (+0x380 = 1) |
| function 18 (BF_FLUFF), or slot 107 (+0x198, meaning **Unknown**) is 7, 9 or 12, with an inventory (without one, or when `FUN_14004a0cf` refuses another faction's, only the selection runs) | `search` when own (or the owner faction has flag +0x1d0), else `scavenge` | 26 LOOT_TARGET; a shop (function 9) of another faction without Alt: "Hold down the ALT key to steal items" and 29 MOVE |
| otherwise | slot 130 below | slot 131 below, with the exceptions under the table |

| Class | Pointer (slot 130) | Order (slot 131) |
|---|---|---|
| Building, GatewayBuilding, WallBuilding | `arrow` | 29 MOVE_CUS_ORDERED |
| Crafting, Farm, Production, Generator, WindGenerator, RainCollector, Torture | `mine` for function 1 / 27, else `use` | 87 OPERATE_MACHINERY, 152 OPERATE_AUTOMATIC_MACHINERY when +0x3ac is 0 |
| FurnaceBuilding | as Farm | 26 LOOT_TARGET |
| ResearchBuilding | as Storage | 87 |
| TurretBuilding | as Storage | 146 USE_TURRET |
| StorageBuilding | `arrow` for function 16; own `use`; no inventory: cage `lockpick` if locked else `door_escape`, other `use`; inventory: `lockpick` if locked else `scavenge` | 26 |
| LightBuilding | `light` | as UseableStuff |
| UseableStuff | as Storage | another faction's with an inventory: 26, or when locked 76 PICK_LOCK (owner faction flag +0x1d0) else 29; by function: 2, 9, 13 → 26; 7 → 97 USE_TRAINING_DUMMY; 6, 25 → 258 USE_BED_ORDER; 8 → 76 when another faction's and locked, else 108 PUT_IN_CAGE; 11 → 136 FEED_CORPSE_INTO_MACHINE; 16 → 29; else 87 / 152 |

**Verified** (exe bytes): Building and Gateway/Wall slot 130 return 0, Building slot 131 returns 0x1d. The rest is **Observed**. Exceptions:

- Occupied beds and cages whose BUILDING_FUNCTIONALITY has `occupant selection` (+0x3a8, read by `FUN_1402a0900`): the hover and both clicks
  go to the occupant (`FUN_1407fb410`). An occupant of another faction ends it there; for one of the player's, the building logic continues and a
  left click selects the occupant.
- A cage (its quick click is reached only when the context menu does not open at once, last point): `use` while the lead selected character
  (+0xf0) carries someone (+0x348). Another faction's empty cage: 108 PUT_IN_CAGE when carrying, else 107 USE_CAGE; the player's own cage:
  108 (slot 131). In another faction's cage holding one of yours, Alt + right click gives 76 PICK_LOCK when its lock is locked or
  207 GET_OUT_OF_CAGE_ESCAPE when not; without Alt the bar shows "Hold down the ALT key to pick the lock." / "...to escape the cage.".
- Another faction's non-cage building with a locked lock: Alt gives 76; without Alt the pick-lock message and a move to it.
- No order while every selected character is unconscious ("Your selected character is unconscious", `FUN_1407f6f50`).
- Holding the command button 0.2 s on the same target opens the context menu instead (`FUN_1407a6bc0`). It opens on the press, without the
  hold, for a building whose function is 8 (BF_CAGE), or 22 (BF_DOOR) whose (first) door is locked and not the player's faction's, unless
  `DAT_142133449` (Shift held, [ai-tasks.md](ai-tasks.md)) is set.

**Selection** (`FUN_1407f9000`, `FUN_1407f7f20`, **Observed**). A building does not join the squad selection (+0x208): it goes into the one
object slot +0x248 (saved as `selected_gui_object`, [save](../formats/save.md)) that also holds an inspected character of another faction.
The selected characters stay selected and keep taking orders. A building with doors is selected as its first door (slot 124 → door
list +0x1c0), except in the editor. The focus handle `DAT_1421345d0` becomes that object and `FUN_1406e91e0` points the HUD panels at it.
Selecting calls slot 38 (`FUN_1405504a0`): the BUILDING's `select sound` (BuildingSound) as switch `Buildings_Click` with event
`Building_Click`; when the building is complete and the player's faction's, its window opens (`FUN_1406e6640`; window by class in
[ui-screens](ui-screens.md#selected-building)). Clicking the selected building again reopens its inventory window if it was closed. No
highlight or outline call was found for a selected building (**Unknown** whether the renderer marks it).

The object stays selected until something replaces or clears it: a left click on another building or another faction's character replaces it.
Selecting one of the player's characters without Shift (click, drag box, number keys, `[` / `]`) runs `FUN_1407f3b50`, which clears the
squad selection, the object slot and the focus. With Shift the object slot stays but the focus (the HUD) moves to the character. A click on the
ground and Esc do not clear it (**Observed** by absence: `FUN_140800250` has no such path, and `FUN_140917120` does not use the focus handle).

Meitou (`PlayerInterface.BuildingSelect.cs`, engine choices): a left click with no character under the cursor selects the nearest building the ray
meets before the ground, among the player's sites and the level data's buildings (not `is node` / `is foliage`). Picking is a ray against a vertical
prism over the convex hull of the record's collision shapes (`BuildingFootprints`) as the first test, then, for a record with collision files, against its collision
triangles (`PlayerInterface.CollisionPick.cs`, double sided, nearest hit), so ground, characters and neighbours beside a courtyard or concave building are not taken for it. There are
no interior floors (a part without collision cannot be clicked, as in the original). Only hits before the ground count, for a click and for the hammer pointer alike (a building's hull and
collision reach below the ground; without the limit the ray, after meeting the ground, found them and the pointer showed the hammer on bare ground beside a building). The character selection stays. The HUD panels show the building as in
[ui-screens](ui-screens.md#selected-building) (the simulation publishes the selected building's doors, `InspectBuildingCommand`); no outline
is drawn (as the original). Selecting or inspecting a character clears it, Shift
included (the original keeps the slot); a site that disappears clears it. Selecting plays the building's select sound ([audio](../audio.md#interface)). Not built: a house's first-door focus, the
building windows, the Dismantle / pause / door buttons in the panel (the right-click menu has them), the For sale line, the Door and Building
Condition bars, hover pointers over world buildings, and quick right-click orders on them, except doors: a door (picked within 40 px of its leaf
centre where it has slid to, `DoorSpot.Slide` × open amount, `PlayerInterface.Doors.cs`) shows `door` / `lock` / `lockpick` as in the table and a quick right click gives `DoorOrder` (the nearest selected
walks to the door's near side and opens or closes it, "It's locked." at a locked one; another faction's locked door: Alt picks the lock, else the hint
and a walk there). The right button's target, for the pointer and the click alike, is the nearest along the ray of: a character with a quick order or a menu, a ground item, a container, a door (these three picked by a pixel magnet round their middle, their distance to it), a rock and a site that is an object; a plain building takes it only when none is there (`PlayerInterface.PickAt`, as the original walks its hits nearest first; what a building seen from outside hides is left out, Meitou's stand-in for the floor rule). Ctrl or Alt over a downed character comes first. Sites of the player's get the `build` pointer only when unfinished; finished ones the class pointer of the table (`mine` for a machine of function 1 or 27, `use` for other machines, furnaces and turrets, `search` for a storage, none for a plain building, as in the original; a quick right click on a furnace walks there and opens its window, on a turret it mans it, [simulation](../simulation.md#production)); a right click on a finished plain building gives way to a container, a ground item, a door or a character under the cursor, so a chest inside a house can be targeted (the building's menu, with Dismantle, opens on a right click held on the building itself; a quick right click on a finished plain building is a move to the hit point's x, z at the terrain height, as the original's target (the goal then takes the walkable surface nearest that height, its floor inside, `Walkability.GroundHeight`; the original's goal snap is about 30, [pathfinding](pathfinding.md)); a level building with an interior is picked by its floors instead (`FloorSurfaces.MoveHit`: a wall or roof hit drops onto the floor below, upper floors above the viewed one are passed through); on a finished storage of the player's it walks the selection there and, once one of them is within 120, opens the storage window with the character's inventory beside it, with the `search` pointer; a left click on a storage shows its window alone).

### Edit boxes and keyboard focus

**Verified** against the MyGUI 3.2.2 source (tag `MyGUI3.2.2`: `EditBox`, `InputManager`, `Button`, `TextIterator`; MIT). Kenshi's own `MyGUI.log` says "MyGUI version 3.2.3" (**Observed**; upstream has no 3.2.3 tag, so the 3.2.2 source is the nearest public one and any difference is **Unknown**).
- A mouse press gives key focus to the nearest widget that needs it; there is no traversal: `InputManager` handles no Tab or arrows, `Button` has no key handling and no key-focus state.
- **Static** boxes ignore every key. **ReadOnly** boxes take focus, selection and copy (Cut copies only); typing, Backspace, Delete, Enter and Paste do nothing. **Password** boxes copy and cut nothing but paste.
- Keys (Windows/Linux table; the Apple build uses Meta): Left/Right one character with **no** Ctrl word jump (3.2.2 has none; later versions do); Home/End line start/end, with Ctrl text start/end; Up/Down/PageUp/PageDown
  move by lines, so in a single-line box to the start or end; Shift extends the selection; Backspace/Delete erase the selection else one character; Enter adds a line in a multi-line box (Ctrl+Enter accepts) and accepts in any other
  (the `eventEditSelectAccept`); Ctrl+A select all, Ctrl+C / Ctrl+Insert copy, Ctrl+X / Shift+Delete cut, Ctrl+V / Shift+Insert paste, Ctrl+Z undo, Ctrl+Y redo (Shift is not looked at). With Ctrl held a key never types.
  Escape drops the key focus; Tab types a tab only with `TabPrinting`.
- Copy with no selection clears MyGUI's clipboard entry. Typed text and pastes delete the selection first, then insert; a box already at `MaxTextLength` (default 2048) rejects the insert, else the text is cut back from the end
  (`OverflowToTheLeft`: from the start, **Observed**: read from function names). `TextIterator`: CR+LF is one break, and a box that is not multi-line (nor word-wrapped) turns each break into a space; tabs and other controls pass through.
- Undo keeps 128 steps. A keystroke is one step; deleting a selection and inserting is one step; edits, pastes and cuts clear the redo steps, undo and copy do not; `setText` and a mode change clear both.
- **Observed** (grep of the installed `data/gui/layout`): `MaxTextLength` is set once (the character editor's `NameText`, 30), `Password` only `false`, `ReadOnly` `true` on 7 boxes (descriptions, the message log), `OverflowToTheLeft` only in the interior-mode layout.

Meitou (`UiTextBuffer`, `MyGuiScreenHost.Keyboard.cs`, `PlayerInterface.Keyboard.cs`; the shortcuts above are all implemented, Ctrl on every system): the clipboard is the window's system clipboard (Silk keyboard `ClipboardText`; the boxes keep their own
without one); an empty Copy leaves it alone; pasted tabs become spaces and other control characters are dropped; the host gives a box its `MaxTextLength`, else 2048 (`UiTextBuffer` alone defaults to 512); the history is a stack of text
and caret snapshots with MyGUI's step rules. Read-only boxes can be focused by a click (caret at the end; Ctrl+A, Ctrl+C), take only edit keys, so Tab, Enter and the hotkeys work as without a focus; Esc still closes the window. Ctrl+C / Ctrl+V reach the free camera's code only
when no box is focused. Not built: caret placing and selecting with the pointer, a password mask (no layout turns it on), `OverflowToTheLeft`, `TabPrinting`, pixel-exact Up/Down in wrapped multi-line boxes (they move by `\n` lines); a focused wrapped box (the read-only descriptions and message log) draws its caret and selection as one unwrapped line, so Ctrl+A shows a bar on the first line only.

**Button traversal (Meitou only)**: Tab / Shift+Tab walk the top window's editable boxes and its enabled, visible buttons (not close/pin, scroll bar parts, tab headers or list rows) in layout order; the first Tab goes to the first edit box, else the first
button. Buttons are reached on modal windows and on windows with an editable box; any other window leaves Tab to change-squad. Enter presses the focused button (else the `DefaultAction` button), Space too; the press runs as a click on the button's middle. A click or a
new window ends it (Esc is unchanged). Look: the Meitou UI mode draws a 2 px `brass-light` ring offset 2 ([meitou-ui](../meitou-ui.md)); the Faithful mode draws nothing (the original has no key-focus look: **Unknown**, `Button` has no such state), so the focus is invisible there.
The settings panel and the native Meitou UI have their own keyboard focus ([native-ui](../native-ui.md)).

### Debug menu

Meitou only (no original counterpart). F10 (`ToggleDebugMenu`, rebindable) opens a cheat list; while open it takes every key (Up/Down, typing, Enter, Esc).
Rows send `DebugCommand`s, applied in the next tick's Inputs phase: teleport the selection to the camera target, spawn a character record there
(own squad, first faction hostile to the player), give an item record (`name x5 q80`; quality 0-100: armour's quality, crossbows' and limbs' level, a weapon the `PLAYER_WEAPONS` model of the highest level at or below it (else the lowest) and that level; what does not fit drops at their feet) to the first selected, add or remove money (signed number),
heal fully (`MedicalState.Heal`), every stat to 100, one cut of n damage. Heal, stats and damage fall back to the inspected character. Names match a record's string id,
its exact name, else the shortest name containing the text. Replaces the game's F10 key list (the viewer keeps its own).

## Unknowns

- Meaning of the listener's suspended field at +0x50 and of FUN_1409134c0 (possibly splash skip).
- What exactly the version-below-1 branch of the controls loader does with its action set.
- Identity of the widget behind FUN_1406e1fd0 (GUI+0x20) and of PlayerInterface+0x2f0, which gate most game events.
- Which window event 7 toggles (taken to be Tutorials); targets of events 1 and 2 inferred from the action names.
- Building selection: the meaning of slot 107 (+0x198) and `FUN_14004a0cf` in the loot branch, the lockpick tooltip text,
  whether the renderer marks the selected building, and what clears the selection when the building is destroyed.
- Press versus release timing of the command button, and the exact rotation marker condition.
- Whether a held, steering right click shows it again each order.
- Conditions inside FUN_140917120 (which windows count as closable) and the meaning of the held/edge flags.
- Box select handling of non-player squads and animals.
- Whether the controls loader accepts every composed name the formatter writes (`Alt+`, `Key_<n>`) when reading
  a hand-edited file.

## Expression reference

- Binding code encoding: `=`
- controls.cfg: `action=KeyName`
- Action table: `map[code] = action`

## Evidence index

Evidence scopes below apply only to their named sources/checks.

- Context menu verbs (FUN_14079b2d0)
