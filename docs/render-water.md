# Water rendering

`WaterRenderer`, `ReflectionPass`, ocean/shore/river bake and WaterShaders implement two modes.
Original water/material records: [formats/terrain.md](formats/terrain.md). Meitou motion is visual;
gameplay water stays Y100. Evolution follows the game clock and stops paused.

## Surface and waves

Faithful: flat surface and game shading/reflection rules. Meitou: camera-centred polar grid,
default256 segments; `--water-grid 128` reduces geometry with visible quality differences.
Three256² FFT cascades cover250/47/9m tiles with separate wave-number bands. JONSWAP wind spectrum,
mirror-conjugate coefficients and normalized directional spreading; zero/Nyquist/out-of-band
modes excluded. Evolve omega=sqrt(g*k), quantized to2pi/600s; modulo clock keeps phases precise.
Stockham row/column FFT packs displacement/slopes/derivatives into two mipmapped RGBA16F arrays.
Vertices choose mips by spacing; fragment slopes preserve fine shading. Jacobian<0.7 produces
crest foam. Wind change>10deg or12% rebuilds on a worker and blends10 game seconds.
**Verified** OceanTests single-mode transform and symmetry under synchronization validation.

Wave displacement fades in shallow/sheltered/shore water; surf uses baked distance/gradient and
exposure, with breaker/swash/foam envelopes, rather than bottom-slope distance approximation.
Game normal maps remain with reduced near strength. Shore-field bakes/publishing must not stall
frames; retain ready textures until replacements finish. RiverFlowBake derives channel flow from
heights because source flowmap RG does not follow channels; suppress ocean/surf in river channels.
River coverage, biome scum and object-contact foam are incomplete.

## Shading and reflection

Water combines Fresnel, normal distortion, depth absorption/refraction, sky/scene reflection,
caustics, crest light/glitter and persistent foam. Surface colour/clarity depend on biome/Tab
settings. Foam uses diffuse sun/ambient/weather lighting, reads the sun shadow and cloud-shadow
tile, and dims at night, not emissive white. Metal uses the same native bindless water program as
Vulkan; there is no Metal-only water fragment. The reflected scene remains mirrored around Y100
despite waves. Reflect level/range, frustum,
clip plane, water coverage and reuse age determine work; use original rules in Faithful.
Meitou reflection skips unnecessary casters/shadows and shortens object/foliage reaches. The half-resolution
target uses 4x MSAA by default on both backends (`--reflection-samples 1` disables it); level 2 reflects
characters, level 3 placed objects and level 4 foliage. Fog in reflection must evaluate reflected world geometry
against the appropriate air/water bounds; ordinary camera fog cannot be applied indiscriminately.

## Safe work skips

Surf branches only for surf/swash contribution. Zero ocean strength skips displacement/slope
reads with explicit fragment gradients. Caustics skip beyond2500, at depth<=1.5 or sun below
horizon; bubble reads skip beyond600. Reuse depth/shore reads when compatible.
Refraction copy omitted when slice near plane>=max(1.5*clamped clarity distance,10000):
transmission is zero and swash faded. Higher clarity restores the copy. Do not infer absence
from terrain/reflection visibility. `--ab water-lean` isolates shader/copy skips; geometry quality
is independent. A scene with no nearby water can skip water work, with conservative bounds.

## Verification and limitations

**Verified** MeitouWaterTests.Refraction_slice_keeps_transparent_floor_and_shore_runup checks
near water, high-clarity far water, short-clarity shore and the15000 default boundary. Grid tests
check both topology/winding. **Observed** close-shore screenshots show256/128 differences;
a still does not establish moving-wave quality. GPU savings on a shared card remain uncertain.
Use a fixed water clock, wind, clarity, resolution/upscaler and texture quality for comparisons.
**Unknown:** incomplete river/scum/object interactions, and behaviour over all winds/biomes. A
shutdown VkImageView leak was observed in validation;
zero rendering-time errors alone does not establish clean destruction.
