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
"Ground relief depth (cm, typical ground)", default 20; "Ground displacement distance (m)", default 25; "Ground parallax distance (m)", default 60; viewer K toggles the parallax). Every layer gets a height map integrated from its normal
map (`NormalHeight`: FFT, slopes `dh/du = x/z`, `dh/dv = -y/z` in the terrain tangent frame, below 2 cycles a tile dropped,
scaled by 1st/99th percentile, so every layer spans the full depth), in an R8 array (≤512², one slice per pair slot; a layer
without a readable normal map flat 255). Only the base layer has relief, weighted by how much of it shows (not grass, slope,
dirt, road or cliff). Each layer's depth is the slider's times `LayerDepth` = `clamp(sqrt(relief / 0.0042), 0.3, 1.5)`,
kept per slot in an R32F texture (0: flat); `relief` is the integrated height range as a fraction of the tile. **Observed**
(76 biomes' base normal maps, decoded at 512²): relief 0.0002 (`SandRippLoop_NML`) to 0.09 (`OrganicRock_NML`), median 0.0042;
sands 0.0014 (`Sand01bc_n`, `DesertSandLoop03_NML`), gravels about 0.02; times the tile (56 to 2500 units) the implied depths
run 0.04 to 41 units, so the strengths are a look, not a measure, and the tile is left out. `DesertSandLoop03_NML` integrates to
ripple bands along u, `DesertSands_NML` to fine wind-aligned grain; the game's terrain normal maps are BC1 2048².

**Displacement** (needs `tessellationShader`): main-view patches within the displacement distance + 10 units of the eye (horizontally; not interior-cut
ones) draw with tessellated colour and prepass programs (patch lists of 3, culling off). Edge levels depend on the edge alone
(length, midpoint distance, relief at ends/middle), so shared edges split alike; the evaluation stage interpolates the flat
triangle and moves it by `depth × (height − ½) × base cover`, full to 60% of the displacement distance, none from it, where edges are unsplit and
unmoved and meet plain patches. Centred on the ground (raising only, as first done, buried the bases of grass, objects and
characters); the troughs below the flat shadow casters look up the sun shadow at the ground's height (`atmoShadowLift`).
Shadow casters, reflections and the rock bake are not tessellated.

**Parallax**: the patch shader marches the base layer's coordinate along the view ray (16 steps) and toward the sun
for a soft self-shadow (12 steps, a step's rise over the ray weighted down along it) that dims the sun only (`atmoSelfShadow`
in `kenshiLight`); darkening the albedo, as first done, also darkened the ambient and gave black spots at 20+ cm. Fading out
from half the parallax distance to all of it. Where the ground is displaced the view-ray march
gives way over the displacement's fade; the sun march stays. It is also the fallback without tessellation.
The cliff layer gets the same march on each of its two vertical projections with a weight over 0.1 (terrain cliffs and the
TERRAIN-mode rocks, which draw with the terrain material), its depth along the axis the projection faces (sign of the normal's
x or z), its own `LayerDepth`. The `(x, height)` projection's height is read upside down: the shading turns its normal map
round in both directions (red always, green by the facing), so its integrated relief runs the other way. The `(z, height)`
projection's tangent frame is degenerate where the normal faces ±X (the game's `n × (−X)`), so its height matches the shading
only for some facings. Not in the rock impostor bake (parallax 0 there).

## Rocks

Meitou look, not in the game (`RockSmoothing`; Tab: "Rock smoothing (tessellation)"). Many TERRAIN-mode rocks have hard
edges ([foliage](formats/foliage.md), "Expression reference"). At decode (`FoliageRenderer.DecodeTrimmed`, not the leaves) a
position whose copies' normals disagree gets, per corner, the area-weighted normals of the faces round it turning less than 60°
from the corner's own; positions that agree keep theirs. Every position also gets one geometry normal (all its faces, or the
agreed one) in the tangent slot, w = 2 (the terrain material reads no tangents; a real tangent's w is ±1, so other TERRAIN-mode
meshes stay flat). The mesh programs then have tessellated twins (colour, solid and the casters, `meshDepthTess`, so the
shadow map curves alike): Phong tessellation (α 0.75) to the geometry normals, each edge split by `sqrt(bend / pixel)` (bend: α/4
of the ends' normals' components along it; pixel: 0.0015 × its midpoint's distance), at most 16, `fractional_odd_spacing`;
both sides of an edge curve it from the same two ends, so it does not crack. Levels by the camera eye in the shadow pass too.
The impostor bake and the GI's ray meshes stay flat.

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
