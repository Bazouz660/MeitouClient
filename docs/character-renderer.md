# Character renderer

`Meitou.Rendering.CharacterRenderer` partials: core content/material/lifetime; Draw cull/LOD/batches;
Motion previous state; Portraits offscreen portraits; Stats diagnostic rings. Inputs are
interpolated simulation snapshots and data appearances, not simulation-owned GPU resources.
Game assembly rules: [characters](characters.md), [animation](animation.md).

## GPU layout and shading

Instance bones/material offsets index shared storage buffers; group compatible mesh parts.
CharacterShaders set0 bindings: bones6, materials7, morphs8, previous bones9; material20 texture
slots. CharacterLayoutTests pins CPU/GLSL layouts. Push depthIndex is near-depth bindless index
for motion,0 otherwise. Camera-relative transforms preserve precision; previous bone/transform
state must match motion history. Skin/body/head/hair/item modes share world lighting/haze.
Head uses UV+(0,1); dye/vest/overlays and cutout rules come from research. Same-instance caster
pass preserves alpha coverage. Face morphs and body shape precede skinning.

## Content, LOD and memory

`CharacterContent` owns buffers/textures/morph arena; worker fetching/preparation precedes render
uploads. Reuse appearances/content; replace only after ready and retire after GPU consumers.
`CharacterTextureBudget` shares mip/VRAM pressure policy; unavailable content uses a stand-in.
The character texture cache is `Essential`: under `VramGuard` pressure it still loads while an image fits the guard's room
(below its ceiling) instead of waiting for the pressure to end. **Observed** (RTX 5060 Laptop, 8 GB, 2560×1440, use held at 85-90 %:
every character white for good, 114 textures unloaded and none reloaded; reproduced with `MEITOU_VRAM_BUDGET_MB=4300`: 0
character textures before, 102 resident after).
Loading readiness includes used textures deferred by memory pressure, not just queued decodes/uploads.
Generated four-level reductions are always enabled; pose updates stagger by distance:120/300/600
units every2/4/8 frames, animal scale by radius/16. `CharacterInstance.FullRateAnimation` bypasses
distance throttling for presentation actors and the player's characters. Shadow LOD doubles choice with cascade floors.
Clamp stored bounds to real mesh extent. Animation rendering does not independently integrate
walk-loop root movement; simulation owns relocation. Quads/morph and motion buffers retain
correct previous-frame data across skipped poses and content changes.

## Animals and portraits

Animal race/body/mesh/fur/clips/size use shared assembly with animal inputs, not human fallback.
Portraits use the same content/shading with a dedicated camera/render target; avoid mutating
world draw state. Asset/appearance failure must not corrupt another instance or portrait.
Portrait cells redraw only when their character, appearance, size, camera serial or streamed
texture images/swizzle change; animation and frame counts do not invalidate the cached still.
Atlas layout (Meitou): squad members take cells 0-29; the conversation window's speaker and player
character take the last two (30, 31) while a conversation is open.
Item icons (`CharacterRenderer.Icons.cs`) are the same: an item's mesh drawn through the item shader into a texture of its own, as a pseudo-instance that the world and shadow views skip;
the camera, lighting and sizes are in [game/ui-screens.md](game/ui-screens.md) "Item icons".
Harness `--crowd` exercises instance scaling; compare pose/upload/draw/shadow separately.
**Observed** human_male:23 poses, no normal offsets,2986/15504 vertices affected, max offset0.18.

## Remaining limits

GPU culling, coarser/merged far shadow casters, better texture stand-ins, face-morph normals and
original animation-update budgets remain incomplete. Generated LOD as a Faithful switch also
needs explicit design. Tests pin layouts/data; visual parity requires named screenshots across
races, bodies, gear, animals, attachments, motion and shadow views. [bench](bench.md).
