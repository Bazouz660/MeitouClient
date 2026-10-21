# Characters and appearances

Original race/loadout/appearance/body/attachment/material research from FCS, .bod2/.phs assets,
binary functions and shaders. Assembly: CharacterGenerator/CharacterAppearance/CharacterShape;
rendering: [character-renderer](character-renderer.md), animation: [animation](animation.md).

## Assembly and generation

Resolve race, sex, body/head/hair/beard, body sliders and clothing/weapon/backpack from record
references and .bod2 overrides. Keep generated appearance/loadout seed separate from per-frame
live stats. Record lists' weighted chances, forced equipment, quality/manufacturer rolls and
race restrictions have distinct semantics; missing references do not imply a universal item.
Worn records' lanterns (and an animal's backpack lantern) become `CharacterAppearance.Lights` ([lights](formats/lights.md#carried-lanterns)).
NPC generation order determines random consumption; original RNG sequence is not reproduced.

### Random appearance

**Observed** (`data/editor/editor_data_human.xml`): race/gender limits specify `min`, `max`, optional
`mid`, `random_variation` and `random_group`; male height, leg length and hands share group 0.
The generator rolls `round(mid + variation × (2r − 1))`, sharing `r` within each group/category.
Default variation uses the runtime deviation (raw CONSTANTS ×0.5; **Verified** constants-loader
evidence in [character-stats](game/character-stats.md#constants-used-by-this-subsystem)), not the raw field.
Base-game 0.4 therefore gives a ±20% range around the midpoint: human height 92–108, not 84–116.
Explicit XML `random_variation` remains a percentage of the full slider range. Rolls are not
clamped; **Unknown** whether the original applies a later clamp for extreme modded limits.

**Observed** (Kenshi x64 disassembly: template initialization `@ 140073fb2`, randomizers'
final copies `@ 14007acc2`, `@ 14007bc2d`, `@ 14007bfda`): randomization restores `Posture`
from the appearance template, whose default is 20, rather than retaining the randomized value.
Generated NPCs use 20; authored body files retain their posture.

Parts can have different meshes/skeleton links for male/female/race and slot. Clothing layers,
vests/backpacks, hair-hidden/part-map flags, stumps and missing limbs are not ordinary visibility
checkboxes. Weapon inventory choice, equipped/drawn location and sheath/back/hip attach bone
are separate; .phs attachment points are data. Part scales/transforms must follow skeletal axes.

## Shading, poses and LOD

**Observed HLSL** body/head texture layering uses head UV+(0,1), border-black and body normal.wy.
Skin tone, body normals/muscle/starvation, hair/beard overlays, dye/cloth/cutout and wet/dust inputs
have separate parameters. Material runtime facts: [runtime-materials](formats/runtime-materials.md).
Bone/positional sizes and face morph deltas apply before skinning; meshes with their own LOD or
manual linked LOD need compatible remaps/bone weights. Skin/head/hair colour-space assumptions
remain scoped to tested shader/screenshots, not every asset.

Body/race generation, mesh-per-item and clothing/weapon choice source addresses below retain
traceability. Tests check our factories/readers; screenshots cover named appearances only.

## Body files (`.bod2`)

CHARACTER `body` names a body file ("Leave blank for random body", fcs.def). **Verified** (FcsReader on the
files, `CharacterAppearanceTests`): a `.bod2` is an ordinary FCS file (no header in type 15) holding one
record of type 66, CHARACTER_APPEARANCE, typically named `default` with id `1--INGAME`. Of the 80
CHARACTER records (merged load order) that name a body file, 60 point at type 15 and 4 at type 16 files (read by `AppearanceFile`); 14 at
legacy layouts (types 5, 8, 9, 10, 13: the `.body` files and three `.bod2`), and 2 at files that don't ship
(`Crow Face.bod2`, `Cat.bod2`). Observed fields (Ruka.bod2, Beep.bod2):

| Kind | Fields |
| --- | --- |
| bool | `sex female` |
| float | body and face sliders (`Height`, `Waist`, `Head size`, `Posture`, `Neck position`, `Shoulder set`, `Hair Colour`, `Hair Brightness`, `Hair Saturation`, `Skin Tone`, `Age`...; mostly 0–200 with 100 neutral) and **pose weights keyed by the body mesh's pose names** (`bone_wide_jaw = 0.37`, `stick2_long_horns = -0.6`...) |
| int | `Age` |
| vec3 | `Skin Tone` (RGB, e.g. (0.99, 0.90, 0.90)) |
| string | `head` (HEAD id), `hair style`, `beard` (ATTACHMENT ids, may be empty), `idle stance` (an animation name, e.g. `idle_stand_relax`) |
| reference | `race` (RACE) |

CHARACTER `race` overrides the body file's race (fcs.def "will override race in .body file"; the viewer
follows this, not traced).

### Hidden body parts (Verified, `@ 140075600`, `@ 140071f90`, `@ 140531cc0`; HLSL `skin.hlsl`)

| Colour | RGB | Bit | PartMapColours |
  | --- | --- | --- | --- |
  | White | FFFFFF | 0x1 | White |
  | Red | FF0000 | 0x2 | Red |
  | Green | 00FF00 | 0x4 | Green |
  | Blue | 0000FF | 0x8 | Blue |
  | Yellow | FFFF00 | 0x10 | Yellow |
  | Magenta | FF00FF | 0x20 | Magenta |
  | Cyan | 00FFFF | 0x40 | Cyan |
  | Orange | FF8000 | 0x80 | Orange |
  | Purple | 8000FF | 0x100 | Purple |
  | Teal | 008080 | 0x200 | (not in the enum) |
  | Black | 000000 | none | |

The body's per-vertex `partData.y` holds the part-map bit and, from `@ 140071f90`, a limb bit 1 << (12 + k) (k: 0 L arm, 1 R arm,
2 L leg, 3 R leg) when the vertex's heaviest bone belongs to that limb and its bind-pose Y is below 13.83 (male) / 14.1 (female) for an
arm, 8.6 for a leg. Bones map to limbs through the race's `combat anatomy` LOCATIONAL_DAMAGE `bone name`/`bone name 2`/`bone name 3`
(an unnamed bone takes its nearest named ancestor's part); an arm (`body part type` 2) or leg (1) is the right one when `collapse part`
& 0x12. `hiddenMask` (`uses_hide_parts`) = OR of worn items' `hide parts`, 1 << (`slot` - 38) per worn LIMB_REPLACEMENT, and bit 12 + k
for every limb not in state 0 (`FUN_1400cd470`); `character.hlsl` clips where the interpolated flag is above 0. Meitou: the limb bits
ride in the body vertices' colour Y, the mask in the instance flags (`CharacterContent.MarkLimbs`, `CharacterAppearance.HiddenLimbs`);
an unnamed root-chain bone (pelvis) is no limb (the original's fallback part index 1 is **Unknown** in effect). Part-map bits are not built.
Boots (slot 9, not `is locked`) on a character with exactly one fitted leg prosthetic lose that leg's half (`AppearanceBase_SetupItemMaterial`,
**Observed**): left leg replaced collapses the x >= 0 half, right the x < 0 half. Meitou: `CharacterPart.HideSide`, instance flag bits 16-17.

### LOD of character meshes

Bodies, hair and armour use their meshes' own generated LOD levels (no separate LOD meshes); each entity picks
its level on its own with the `distance_sphere` rule ([formats/ogre-mesh.md](formats/ogre-mesh.md#stream-and-compatibility)). The rule now lives in `Meitou.Data.Ogre.MeshLod`; `CharacterLod` forwards to it.
**Verified** (scratch survey with `OgreMeshReader` of the meshes the base game's records name; `CharacterLodTests`
checks `human_male.mesh`):

| Records' meshes | LOD |
| --- | --- |
| RACE `male mesh` / `female mesh` | counted per record field: 2 levels at 200 for 18 male / 11 female (`human_male`, `bone_male`, `robot`, the three `Stick_person`), 2 at 400 for 32 (animals), none for the rest (`human_female`, `bone_female`, ...) |
| ATTACHMENT hair (`haircut_*`) | 2 levels at 200 (81 references); animal fur 400 or none |
| ARMOUR `mesh` / `mesh female` | 2 levels at 400 for 193 references (masks, hats, coats... meshes with no skeleton link), none for the 51 to meshes with a skeleton link (`samurai pants` ...) |
| CONTAINER backpacks | 2 levels at 400 |
| LIMB_REPLACEMENT, WEAPON (`mesh`, `bare sword`, `sheath`) | none |

## Names

**Observed** (character setup `FUN_14062b210`, pool loader `@ 140641d80`): CHARACTER `named` false → the record's name; true → a uniform pick
from the look's sex's list (appearance `sex female`): `data/namesM.txt` (men) or `data/namesF.txt` (women), with `data/namesMF.txt` appended to
both. Lines are trimmed, blank ones skipped; each file is optional. An empty list gives a fixed fallback string (**Unknown**; Meitou: the record
name). 135 of 639 merged CHARACTERs are `named`, among them most start-squad members (Wanderer, Nobody, start slave; not Citizen, Exile).
Meitou: `NamePool`, rolled at spawn (`PopulationSystem.SpawnName`, `RngPurpose.Name`); files read from the install, not mods.

## Character editor

`CharacterEditWindow` (constructor `FUN_1405f4dd0(chars, mode, forceRaces)`, layout `Kenshi_CharacterEditor`). Facts **Observed** from the
exe decompile unless marked; Meitou: `CharacterDraft` (data), `EditCharacter` / `FinishCreation` commands (`Simulation/Editing`), `PlayerInterface.Editor`.

- **Modes**: 0 new game (`FUN_140872e00`: every player character after the start's `squad` spawns, with the start's `force race`), 1 in play
  (dialogue `DA_CHARACTER_EDITOR` on the talking player character; `DA_JOIN_SQUAD_WITH_EDIT` and slave purchases on the recruits). Mode 1
  hides the race, subrace and gender arrows; they are also hidden when there is no choice. Everything else works in both modes.
- **Races**: RACE_GROUP name on RACE, RACE name on SUBRACE; a race is listed if not an animal, in a group, has its editor limits XML, `playable`
  (mode 0) and, with a `force race` list, in it (none left: "Game start has no valid races", the list is unrestricted). Base data: Human
  {Greenlander, Scorchlander}, Shek, Hive {Prince, Soldier Drone, Worker Drone}, Skeleton (**Verified** by probe). Group order **Unknown**
  (Meitou: data order). Gender blank for single-gender races. Description = RACE `description`. Stats panel: "Hit Points:", "No bleeding"
  below `bleed rate` 0.1, then each stat multiplier as (m-1)x100 % (Meitou omits the hit points line).
- **Tabs** (`FUN_1405ebd50`, Body first): FACE = head list (if more than one) + XML Face ranges + colours; BODY = "Idle stance" slider, then
  `Skin Tone` (vec3 interpolated; only with more than one colour), then XML Body ranges, then other colours (order also seen in the original's
  screen); HAIR = Hair (attach slot 2) and Beard (13) lists with
  "None" (if more than one entry), hair colour multi-slider `Hair Colour` / `Hair Saturation` / `Hair Brightness` 0..100. No Personality tab, no
  Age. Plain ranges store the integer as the float key; target ranges store value x 0.01 in `target`, a negative value x -0.01 in
  `target_opposite` (the editor shows the reverse). Meitou: women get no beard list (generator rule).
- **Idle stance**: slider 1..n over a list built once (`FUN_14007d480`, from the idle list of `FUN_1405bbd40`): ANIMATION records in data
  order that are `idle`, not `disabled`, `weather type` < 1, `layer` not `overlay`, `has weapon L` and `R` both 0, not `crouched`. Writes
  the record name to `idle stance` (`FUN_1405eaa30`); a stance not in the list shows as 1. **Verified** by probe on base data: 8 stances
  (`idle_stand_relax`, `stand 1`, `idle_stand_guard`, `idle_stand_timid`, `idle_stand_confident`, `idle_stand_normal`, `idle_stand_stiff`,
  `squat`), so the default `idle_stand_normal` is 6, as the original's screen shows. Whether RANDOMISE rolls it is **Unknown** (Meitou: no).
- **Buttons**: RANDOMISE = current tab only; RANDOMISE ALL = every randomiser; RESET ALL = the appearance template's defaults (hair, beard,
  colours, `Posture`, idle stance `idle_stand_normal`; slider values **Unknown**, Meitou: XML mid, poses 0, Posture 20); CLOTHES toggles worn
  clothing in the preview; IMPORT/EXPORT: see [below](#import-and-export). CONFIRM asks first when some
  characters were never shown, closes, unpauses at speed 1; it charges nothing. No cancel widget; Esc **Unknown** (Meitou: Esc leaves a
  surgeon's editor unchanged and unpaid; the new game's must be confirmed).
- **Preview**: a separate scene. **Verified** (install's compositor files): workspace `Kenshi_Character_Editor` = `GBuffer_Minimal` (characters only,
  render queues 50-70) then a `Character_Editor` node, then the GUI. The background is a full-screen `render_quad` of
  `data/gui/gfx/SandyGround.dds` (1920x1080 DXT1, no depth). Lighting is `Deferred_RTT` / `rtt.hlsl`: one directional light (-3, 2, -3), colour
  0.6, exposure 0.5, IBL from `mp_irradiance` / `mp_specularity`; no fog, sky or shadows (light sign convention **Unknown**). The stage sits at
  y -3000 (**Verified**, editor ctor in the RE dump). Camera rig (**Verified**, same dump): a pivot node takes yaw (about Y) and pitch (about X);
  the camera is its child at (-3, 0, -30) + normalize(-3, 0, -30) x zoom. Mode flag 1 = body (fixed pivot, zoom clamp -9..7), 0 = head (pivot at
  the head bone y - 12, default zoom -21, clamp -23..-17). Any of three mouse buttons drags (yaw/pitch += delta x sens x dt x -0.1); the wheel
  zooms (zoom -= wheel x sens x dt x 0.6, clamped). **Observed**: FACE and HAIR tabs give head mode, BODY the body mode; changing mode or
  character resets angles and zoom. **Unknown**: default angles, pitch limits, speeds, body default zoom (set at run time).
  Meitou (`EditorOrbit`, `CharacterRenderer.Editor`, `PlayerInterface.EditorBackdrop`): while the editor is open the world is not drawn; the
  renderer draws the edited character alone into a stage texture (transparent, key light plus a hemispheric ambient, an engine choice: the stage
  shader branch for a fog distance of -2; icons keep none), the interface puts `SandyGround.dds` and the
  texture behind the panels, centred between them. Drag with any button orbits, wheel zooms, mode/character change resets; limits, speeds and
  start angles are engine choices (`EditorOrbit`). **Blend**: a slider moves the character along: when a new preview look is ready it is drawn
  as the mix of the old and new values over 0.12 s, the interval of the throttled rebuild (`CharacterRenderer.Blend`): body shape, posture
  layers, face/body pose weights (morph set recomputed per frame in a scratch ring of `MorphArena`) and skin/hair colours; a look with another
  head, hair or clothes swaps at once. The original's own slider behaviour is **Unknown**.
- **Keys**: `in editor` true while open; a mode 0 character without a body file is randomised on open (Meitou's start characters are already
  rolled). Closing in mode 0 also sets health from the race's `starting health` (Meitou: a new race at creation gets a new body).
- **Meitou validation** (`CharacterEditing.Apply`, `CharacterDraft.Sanitize`): player character with a person's look; creation only while
  `PlayerState.InCreation` (new game until CONFIRM) and within the start's force races; surgery needs the surgeon alive within 60 units
  (engine choice), the same race and gender, money > price, and takes the price. Head/hair/beard must be the race's, sliders and poses are
  clamped to the limits, the name to 30 characters. **Verified** by a scratch round trip on save 2: edit, capture, load gives the edited
  values with the original key set.

### Import and export

Folder `./data/character/bodies/export/` under the install, extension `.bod2` (`FUN_141539ad0`/`b10`). **Observed**: EXPORT (`FUN_1405ea410`)
needs no dialog: it writes the edited appearance record to the folder as `<NameText>.bod2` and shows "Character '{1}' exported to file {2}".
IMPORT opens `Kenshi_OpenSaveFileDialog` titled "Import character" on the folder (`FUN_1405f4dd0`); the chosen file must hold a type 66 record,
else "Error loading appearance file '{1}'. The file is in an old format or invalid." (`FUN_1405f4280`), and its look replaces the editor's;
what the original does with another race in mode 1 is **Unknown**. **Verified** (FcsReader + FcsWriter on the install's 12 exports): all are
type 15, one record, id 1, `1--INGAME`, name `default` or `0`, and round-trip byte-exact. Meitou (`CharacterDraft.Import`/`Export`,
`PlayerInterface.Editor`): export writes that layout, overwriting a file of the same name; import sanitizes the file for its race and refuses a
race the editor doesn't offer (creation) or another race or sex (surgeon); the dialog lists the folder only (no browsing).

### Plastic surgeons

**Verified** by probe of the merged data: 9 dialogue lines carry `DA_CHARACTER_EDITOR` (11); the price is a `DA_TAKE_MONEY` (8) effect on the
same line (val0), gated by `DC_PLAYERMONEY` (2) strictly greater. "plastic surgeon" (CHARACTER 2132 Plastic Surgeon, leader of the bar squads
"bar plastic surgeon" / "plastic surgeon": Holy Nation, Empire Peasants, United Cities, Deadcat, Waystation, Bark, Sho-Battai...) 200;
"doctor shop" (hospital squads' `dialog leader` package) 500; Doctor Chung (Mud Town) 200, no money check; Thieves Guild surgeon free
behind `DC_FACTION_RANK`; Grey free for escaped slaves. Meitou: the surgeon is talked to; its line's DA 8 pays and DA 11 opens the editor in
mode 1, already paid ([dialogue](game/dialogue.md#meitou-design)). An unpaid `EditCharacter` (no UI sends one) is checked against `SurgeonDialogues`:
a line gated only by money or `DC_IS_RECRUITABLE`.

## Open questions

- ~~Body-shape slider formulas~~, ~~`hide parts` bits~~, ~~`hide stump`~~, ~~`muscleBlend`~~: answered (animation.md
  "Body shape sliders", "Hidden body parts" above). Left: which body region each part-map colour marks, and the
  stats/nutrition fields listed in animation.md's open questions.
- Random appearance details: the untraced part of the morph-face name, the faction flag that re-rolls the skin tone
  (and a body file's gender); crossbow placement; extra hip copies; `RaceLimiter` rules.

## Expression reference

- Random appearance: `gender="Female"`
- Colour space: `hdr_to_gamma = false`

## Evidence index

Evidence scopes below apply only to their named sources/checks.

- Meshes per item (Verified, `@ 140537690`, `@ 1405358a0`)
- Order (Verified, `@ 140581770`)
- Body shading (Observed, HLSL `character.hlsl` main_fs; parameters Verified where noted)
- Hidden body parts (Verified, `@ 140075600`, `@ 140071f90`, `@ 140531cc0`; HLSL `skin.hlsl`)
- Hair meshes (Observed, HLSL; parameters Verified `@ 140532600`)
- Worn clothing (Observed, HLSL `skin.hlsl`; parameters in runtime-materials.md)
- Colour space (Observed)
