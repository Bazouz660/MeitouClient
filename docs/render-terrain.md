# Terrain rendering

`TerrainQuadtree`, `TerrainLod`, `TerrainRenderer`, `TerrainTextures`, `TerrainStreamer`.
Original height/blend/map/material facts: [formats/terrain.md](formats/terrain.md).
Engine LOD/streaming rules below; GPU lifetime: [renderer-native.md](renderer-native.md).

## Geometry and material

CDLOD chooses patches by error in rendered pixels, morphing between levels without cracks.
Whole-world coarse heights keep the horizon; a fine moving window follows the eye. Use fine
height directly at weight1, blend at edges. Height/normal sampling must agree between terrain,
objects, water/shore bakes and collision queries. Near/far slices retain consistent depth.
TERRAIN-mode rock geometry is foliage work, not terrain-patch stage timing.

Biome materials resolve blendinfo slots, mask/overlay/colour maps and layer colour/normal inputs.
Preserve source tile scales, layer order and weights; the engine's texture window does not alter
world coordinates. `terrain-layer-skip` can omit insignificant layer work; thresholds are quality
choices requiring boundary/material checks. `terrain-late-colour` separates depth from shading
where occluders justify it. A depth prepass is extra geometry, not automatically a speedup.

## Ground relief

Meitou look, not in the game (`GroundRelief`, on by default; Tab: "Ground parallax", "Ground displacement (tessellation)",
"Ground relief depth (cm)", default 12; viewer K toggles the parallax). Every layer gets a height map integrated from its normal
map (`NormalHeight`: FFT, slopes `dh/du = x/z`, `dh/dv = -y/z` in the terrain tangent frame, below 2 cycles a tile dropped,
scaled by 1st/99th percentile, so every layer spans the full depth), in an R8 array (≤512², one slice per pair slot; a layer
without a readable normal map flat 255). Only the base layer has relief, weighted by how much of it shows (not grass, slope,
dirt, road or cliff). One depth for all layers. **Observed** (decoded at 512²): `DesertSandLoop03_NML` integrates to ripple
bands along u, `DesertSands_NML` to fine wind-aligned grain; the game's terrain normal maps are BC1 2048².

**Displacement** (needs `tessellationShader`): main-view patches within 260 units of the eye (horizontally; not interior-cut
ones) draw with tessellated colour and prepass programs (patch lists of 3, culling off). Edge levels depend on the edge alone
(length, midpoint distance, relief at ends/middle), so shared edges split alike; the evaluation stage interpolates the flat
triangle and raises it by `depth × height × base cover`, full to 150 units, none from 250, where edges are unsplit and unraised
and meet plain patches. Raised upward so the (unraised) shadow casters never shadow the troughs. Shadow casters, reflections
and the rock bake are not tessellated.

**Parallax**: the patch shader marches the base layer's coordinate along the view ray (16 steps) and toward the sun (8 steps)
for a self-shadow that darkens the albedo, fading out from 300 to 600 units. Where the ground is displaced the view-ray march
gives way (150 to 250 units); the sun march stays. It is also the fallback without tessellation.

## Interior cut

Patches over a building's interior mask are cut: [render-interior-cut](render-interior-cut.md).

## Streaming and budgets

Worker jobs decode/sample; render thread uploads/publishes through BackgroundWork/UploadQueue.
Keep valid old windows until replacements are ready. Prioritize useful nearby work, limit each
frame's upload/allocation steps and retire resources only after in-flight references finish.
WorldTextureCache and mesh caches share memory pressure/range policy; avoid unloading resources
immediately needed by adjacent zones or a new mip level. Cold load/settle and steady-state frames
are separate performance measures. Height-window changes invalidate dependent bakes/probe bases.

## Validation

Use matched rendered resolution/error, not display resolution alone. Inspect seams/morph bands,
biome edges, fine/coarse window movement, horizon and teleport. **Observed** RTX4070 swamp/Hub
probes: near views can be material/shadow-bound; distant views can be geometry-bound. Changing
anisotropy, error or LOD need not change the limiting cost. [bench.md](bench.md).
Faithful material/placement claims remain in the format reference; keep output/noise evidence
with any deliberate Meitou quality change.
