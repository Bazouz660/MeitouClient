# Blood decals

Blood left on the terrain: the game's `TerrainDecalsManager` (exe) and the terrain plugin's blood
patches (`Plugin_Terrain_x64.dll`). Implementation: `TerrainDecalStore`, `TerrainDecalCells`
(Meitou.Data), `BodySystem.BloodTrail` / `CombatSystem.Splash` (marks), `GameHost.Blood`,
`TerrainDecalRenderer` and the terrain colour shader. Settings: [settings.md](../formats/settings.md#graphics-decals-and-effects);
save layout: [save.md](../formats/save.md#folder-and-state-mapping).

## Types and textures

**Verified** (`FUN_1408dfaf0` strings, `resources.cfg`): four types by the folder name of each
`FileSystem` location of the `Blood` resource group: 0 `line`, 1 `splash`, 2 `pool`, 3 `smear`.
A type's variants are every texture in its folders (install: line 3 at 1024×512, splash 7 at 512² +
1 at 1024², pool 5 at 512², smear 1 at 512×256). Each variant keeps its full-size texel dimensions
(the loader shifts them back up by the texture-quality level for names ending `.dds`). Type 0 has
no caller found (**Unknown** whether anything makes `line` decals).

## Cells

**Verified** (`Terrain::getPatchInfo`; keys of two saves): decals are kept per terrain quadtree node
of level 10: 1024² cells of 294912 / 1024 = 288 units. Key = `1 << 20` | column bits on the even
bits | row bits on the odd bits. A decal is stored once in every cell one of its four corners falls
in, each copy placed relative to its own cell (checked: placing the saves' decals again reproduces
their cells and local places).

## Making a decal

**Verified** (`FUN_1408dce50`, `FUN_1408dc6b0`): random variant; angle = atan2(dir.x, dir.z), the
texture's width axis along (sin, cos) and its height axis along (cos, −sin); half extents in world
units = size × texels × 288 / 4096 (**Verified**: `FUN_1408dfaf0` sets the factor at `+0x1c0` to cell 288 × 0.5 × 1/2048 = 288/4096; the draw agrees, below). Life starts at 99 (seconds; the medical and hit callers pass 99.0; the bleed pool's value is lost in the decompile, **Unknown**, we use 99).

Sources (`FUN_140652000` medical update, `FUN_140793b20` hit), all only with `Blood` on and the
character out of the water (`FUN_1405c7fd0` = water state 0); colour = race `blood colour` (FCS
default #800000), alpha 1 unless stated:

| Case | Decal | Rule |
| --- | --- | --- |
| bleeding > 0, speed 0 (the carrier's when carried) | pool | grows the character's pool (`FUN_1408dcfa0`, below); remembers the place |
| bleeding > 0, moving, not carried | smear, size 0.2 | once more than 4 units from the last place; along the velocity; alpha = clamp(bleeding, 0, 1) |
| bleeding > 0.01, otherwise (carried) | pool, size min(0.5, bleeding × 0.1) | build-up += rand × bleeding × dt × 10; past 1: a drop if more than 1 unit from the last place, then build-up 0 |
| a hit (`FUN_1406508d0`), a severed limb (`FUN_14064edc0`) | splash, size clamp(min(1, (cut + blunt + pierce) / 100), 0.05, 0.5) | a direction (a character vector at `+0x640`, `+0xd0`), flipped or turned ±90° by a hit-side flag (2, 4, 8), turned by a random ±50°; placed up to 20 units along it |

**Unknown**: the second condition of the first two rows (`FUN_14028ddc0`, a flag or count on the
character; taken as true), the medical dt's unit in the drip row (taken as game seconds), the
splash's direction vector and side flag (we use attacker → victim, bolt flight), whether the 20 is
a random distance (we roll 0..20), and the race check of the hit row's `vfunc 0x330` (we skip
bloodless races, `bleed rate` 0). We emit no limb-sever splash.

**Pool growth** (**Verified**, `FUN_1408dcfa0`): per character a token (place, size). Increment =
bleeding × scaled frame dt × 0.2. No token: size = increment. Within √10 units (X/Z) of the token:
size += increment / max(1, 10 × size), capped at 2 (a size past 2 is set to 2). Farther: the token
moves there, size = increment. The pool decal at the token's exact cell place is replaced when its
stored half size (size × texels × 10, a short) differs from the new one: removed from that cell and
the cells its corners reach, then made again at the same turn (`FUN_1408dc6b0`, so at the end of
each list); none there: a new pool with a random direction. The cap is **Verified** (float
constants read from the exe: 2.0, 10.0, 0.2). A capped pool on a 512² texture is 2 × 512 × 288/4096 × 2
= 144 units across (half a cell, about 15 characters), reached after 100 / bleeding seconds of standing
still (from the growth rule), so a quickly bleeding body does reach it. One pool per character at a
time, but a new one each time it stands more than √10 units from the last.

## Ageing and drawing

**Verified** (`FUN_1408dd860`, `FUN_1408de890`, `FUN_1408dec40`, `Terrain::createBloodPatches`,
`TerrainBlood` material): every update life −= the scaled frame time (0 paused); a decal at life ≤ 0
is removed unless its cell is on a texture now. The cells within `Decal Range` of the camera get up
to 8 `Kenshi_Decal_Texture_n` render targets of `Decal Resolution`² (Ogre format 0x1c, **Observed**
to be `PF_R8G8B8A8` from Ogre's enum). A target is cleared to 0 when it gets a new cell; decals are
drawn as quads (centre `lx·2/1000 − 1`, half sizes / 10240 in clip space, rotated by angle − π/2)
with `texture × colour` (the unit quad runs −0.5..0.5, so its full width is the half size / 10240 of clip space, 2 clip = 288 units: the
same 288/4096 per texel and size as the placing above, **Verified** by the two agreeing; our store keeps the half sizes ×10 as the game does), colour alpha = min(alpha, life), alpha-blended colour and additive alpha.
The terrain draws the cell's nodes again with `TerrainBlood`: depth equal, the texture's colour
alpha-blended over the G-buffer albedo, gloss kept. The game ranks the candidate cells by decal
count and distance (**Observed**; we take the nearest). Consequence (**Observed**, vanilla battle
footage): a big battle's blood ends at a straight cell edge, and the cells past the eighth age out
unseen.

## Meitou

The `blood` switch (Meitou, default): every cell within `Decal Range` is drawn, up to 32 layers (at
most 256 MB of them: 16 at 2048²), the array growing by 8 as more cells are near; Faithful keeps the
game's 8. Expiry is unchanged (undrawn cells still age). Engine choices: the blood is mixed into the terrain colour shader's
albedo before lighting (our terrain is forward-lit, no second pass); the cells are a texture array
read by cell index. Marks more than 20 units above the ground are dropped (upper floors). A pool's
decal is grown whenever its stored half size changes (as the game does), in place in each cell's list. A cell's layer is cleared and drawn again
when decals leave the cell or one of its pools grew (`CellRewrite`, once a frame however many ticks the pool grew in, so growth is as smooth as the
frames); otherwise only the decals placed since are drawn over it (`Appended`). Growing pools used to be drawn over their old copy in 25th steps:
that was visibly stepped, stacked the translucent edge dozens of times and popped when the cell was later redrawn. The
original likely draws incrementally too (**Observed**, inferred: it clears only for a new cell and keeps a drawn cell's dead decals); a log past twice the cell's decals + 64 forces a redraw.
A layer is one instanced draw (each decal's texture by bindless index, colour and alpha blended separately in one pass).
The store finds a pool by a per-cell index of place → first pool there, not a list scan, and keeps each life as the store
clock's expiry time, so ageing visits only cells with a decal due. Decals
are saved and loaded as stored (`SaveDecals`, TERRAIN_DECALS in [save.md](../formats/save.md#folder-and-state-mapping));
the bleed tokens are not, so a pool under a still-bleeding character starts anew after a load.

## On the body and on severed limbs

**Verified** (shader sources `data/materials/deferred/character.hlsl`, `skin.hlsl`, `character.material`; exe `FUN_140530b30`, `FUN_1400cf490`):
characters carry blood on their skin, not only on the ground.
- Body and worn-item shaders (`BLOOD` define) lay `bloody.png` (256², RGBA splatter, `materials/`) over the albedo before the darkening:
  `albedo = lerp(albedo, tex × bloodColour, saturate(tex.a - 1 + amount))`. The map is a cylinder projection in the part's own space:
  u = height along the mesh Y × the race's `blood vertical`, v = angle round Y (atan2(normal.x, normal.z) / 2π, shifted half a turn where
  normal.z < 0) × `blood horizontal` (human: 0.15 and 1). `amount` is per body part, `bloodAmount[8]` (the first 8 parts), written by
  `FUN_140530b30` as 1 − clamp((flesh − stun + bandage) / (base × mult × scale − wear), 0, 1) (the +0x48 field is the bandage, not the rig): a damaged part is bloodier, a part far below zero is
  fully covered. Only `Character_VP` (bodies) defines `BLOOD` among the people's programs; worn clothing (`skin.program`) has none, animals have their own (`creature.hlsl`).
  **Unknown**: the part index a stump mesh's vertices carry, the exact `bloodColour` alpha, the animals' mapping.
- A severed limb item draws with the `SeveredLimb` material (`FUN_1400cf490`, cloned as `Limb_<name>`): textures from the item's MATERIAL record
  (`texture map`, `normal map`, `color map` = skin-tone mask; the human's are the female body's), shader `severed_limb_fs`: skin-tone mask tint
  (a vec4 stored on the item), then blood with the same texture, `bloodColour` (a vec4 on the item) and an extra alpha of
  max(−2 × 0.3 × mesh x, 0), so the cut end (negative x) is solid blood. Its mapping: u = 0.3 × x, v = the angle round X (atan2(normal.y, normal.z)).


**Meitou**
- Body: `CharacterContent.BodyBlood` gives a person's body material the race's blood colour (`blood colour`), scales (`blood horizontal`, `blood vertical`) and `bloody.png`
  (none for animals and bloodless races, `bleed rate` 0). `MedicalState.BloodAmounts` publishes the first 8 parts' amounts (`CharacterSnapshot.BloodAmounts`), the host hands them
  to the renderer (`CharacterInstance.Blood`), which writes them as exact integers in the w of the body instance's matrix rows (`CharacterRenderer.WithBlood`); the vertex program
  reads its vertex's part (colour Y bits 16-19, `CharacterContent.MarkBody`) and passes the amount on. **Observed**: a vertex's part is the part claimed by its heaviest bone or its
  nearest named ancestor (as the hidden limbs are, `LimbHideOf`), numbered in the health parts' order (`BodyPartTemplate.MedicalOrder`, the order `bloodAmount` is indexed in). The
  race's `combat anatomy` order is a different one: numbering by it put the left arm's blood on the head (sandbox render).
- Stump: a limb-replacement item without a material wears the body material at the full amount (`BloodFull`): a severed part is far below its health.
- Fallen limb: `GameHost.SeveredLimbs` draws a race's `severed limbs` items with the debris renderer, falling and at rest, instead of as a world object: the MATERIAL record's
  texture map, the race's blood colour, blood with the cut end solid, and the Meitou skin's scattering (the same table and rule per race as the body; the curvature baked by
  `CharacterContent.MarkCurvature`). Not carried: the cut character's skin tone (the original keeps it on the item), the normal map; a limb as a debris piece casts no shadow.
- **Observed** (sandbox, a dressed character): a stump under a coat or trousers is not visible at all; the blood shows on a bare stump and on a fallen limb. The ground limb of the
  sandbox is white without a material because the sandbox has no world-object layer, so the debris path (no material) drew it.
