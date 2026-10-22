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
texture images/swizzle change, or the look below does; animation and frame counts do not.

### Meitou stage and portraits

`PreviewLook` (set from the world's options every frame): enhanced when Meitou's tone map, grade or
temporal AA is on. The editor stage (stage light unclamped, fog distance -3, platform included) is
drawn into RGBA16F at 2x each way with Meitou AA (1x past 16.6 Mpx); each portrait (plain model) alone
into a 1024 px RGBA16F square (4x its cell). `PreviewResolve` tone maps each sample, averages by
coverage (anti-aliased edges on the transparent background), grades and dithers into the 8-bit
picture. Atlas cells are 256 px apart; enhanced portraits fill them (the original's 128 px were stretched to the
squad bar's 120-230 px from 1080 to 4K lines and looked soft), faithful ones keep 128 px in the corner. Portraits keep the
fixed plain light: a still drawn under the live sky would depend on the hour it was drawn. Faithful
keeps the original's direct 8-bit stage and 128 px plain portraits. No SSAO, shadows or GI (no scene).
Atlas layout (Meitou): squad members take cells 0-29; the conversation window's speaker and player
character take the last two (30, 31) while a conversation is open.
Item icons (`CharacterRenderer.Icons.cs`) are the same: an item's mesh drawn through the item shader into a texture of its own, as a pseudo-instance that the world and shadow views skip;
the camera, lighting and sizes are in [game/ui-screens.md](game/ui-screens.md) "Item icons".
Harness `--crowd` exercises instance scaling; compare pose/upload/draw/shadow separately.
**Observed** human_male:23 poses, no normal offsets,2986/15504 vertices affected, max offset0.18.
**Verified** (`FUN_140071ac0`, `skin.program`; survey 2026-10-21): the game bakes a look's poses on the CPU once (`Ogre::Mesh::softwareVertexPoseBlend`
per non-zero weight into a clone `<mesh>_morph_<n>`, then `removeAllPoses`) and never rebuilds normals or tangents; its vertex programs take no
pose input. No pose in base data (13 posed meshes, 292 poses), installed mods or the Workshop folder carries normals, so the bind normals stay,
as in Meitou (offsets only).

## Remaining limits

Far shadow casters: in Meitou's coarse cascades (`ShadowLod`) a part whose diameter is under one texel
(`ShadowPass.MinCharacterCasterTexels`) casts nothing (hair, beards, weapons, hats); A/B `char-casters`. Release, 400-character
crowd at The Hub: render-thread shadows -0.28 ms (-15 %), 11k of 921k pixels differ by 4/255 or more.
Texture stand-ins: a part whose cut-out texture is still streaming in is not drawn (shadow and motion passes too); the head's
region shows the skin constant and a flat normal until the head maps are resident; vest layers are drawn per ready layer
(`AlphaPending`, `HeadPending`, `HeadNormalPending`, the vest mask).
GPU culling, merged far shadow casters (one skinning per frame for every cascade), tail-mip texture stand-ins and
original animation-update budgets remain incomplete. The `charlod` switch picks the files' own levels instead (Faithful; [characters](characters.md#lod-of-character-meshes)). Tests pin layouts/data; visual parity requires named screenshots across
races, bodies, gear, animals, attachments, motion and shadow views. [bench](bench.md).
