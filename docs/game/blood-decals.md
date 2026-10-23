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
units = size × texels × 288 / 4096. Life starts at 99 (seconds; the medical and hit callers pass 99.0; the bleed pool's value is lost in the decompile, **Unknown**, we use 99).

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
size += increment / max(1, 10 × size), capped at 2. Farther: the token moves there, size =
increment. The pool decal at the token's exact cell place is replaced when its stored half size
changes; none there: a new pool with a random direction.

## Ageing and drawing

**Verified** (`FUN_1408dd860`, `FUN_1408de890`, `FUN_1408dec40`, `Terrain::createBloodPatches`,
`TerrainBlood` material): every update life −= the scaled frame time (0 paused); a decal at life ≤ 0
is removed unless its cell is on a texture now. The cells within `Decal Range` of the camera get up
to 8 `Kenshi_Decal_Texture_n` render targets of `Decal Resolution`² (Ogre format 0x1c, **Observed**
to be `PF_R8G8B8A8` from Ogre's enum). A target is cleared to 0 when it gets a new cell; decals are
drawn as quads (centre `lx·2/1000 − 1`, half sizes / 10240 in clip space, rotated by angle − π/2)
with `texture × colour`, colour alpha = min(alpha, life), alpha-blended colour and additive alpha.
The terrain draws the cell's nodes again with `TerrainBlood`: depth equal, the texture's colour
alpha-blended over the G-buffer albedo, gloss kept. The game ranks the candidate cells by decal
count and distance (**Observed**; we take the nearest).

## Meitou

One faithful path, no switch. Engine choices: the blood is mixed into the terrain colour shader's
albedo before lighting (our terrain is forward-lit, no second pass); the cells are a texture array
read by cell index. Marks more than 20 units above the ground are dropped (upper floors). A pool's
decal is grown in steps of a 25th of its half size (and at the cap), not at every texel change, and in place
in each cell's list. A cell's layer is cleared and drawn again only when decals leave the cell
(`CellRewrite`); otherwise only the decals placed or grown since are drawn over it (`Appended`). The
original likely draws incrementally too (**Observed**, inferred: it clears only for a new cell and keeps a drawn cell's dead decals); a log past twice the cell's decals + 64 forces a redraw. Decals
are saved and loaded as stored (`SaveDecals`, TERRAIN_DECALS in [save.md](../formats/save.md#folder-and-state-mapping));
the bleed tokens are not, so a pool under a still-bleeding character starts anew after a load.
