# Metal Effects Parity I (MEP-I)

Status: **MEP-1 implemented, awaiting visual validation** (2026-10-24); MEP-2..MEP-5 not started.

Starting point: `metal-spike` at `7eb04a96` (`docs: plan Metal Effects Parity I (MEP-I) milestones`), after the
effect-parity audit `84193e4a` and the upstream `57a7968a` merge. Read
[the effect-parity audit](../HANDOFF.md#effect-parity-audit-2026-10-24) for the measured/known gaps.

## MEP-1 implementation (2026-10-24)

The code is implemented; the user still has to validate the temporal and anti-aliasing behaviour in game.

- **Particle coverage** is now passed through `IWorldPostChain.Particles`: full-size quads write their coverage in
  `ParticleRenderer.DrawCoverage`, and `PostComposite.ParticleCoverage` upsamples the half/quarter-size accumulations
  (including their depth-aware upsampling). The low-res upsample now uses the backend's depth convention, fixing
  Metal's 0..1 depth weights. `PostTaa` feeds MetalFX's R32F reactive mask with the full-size particles and TAA's
  motion-alpha channel with both full- and low-resolution particles; the low-resolution dust share is not fed to
  MetalFX because its depth-aware upsample is unstable at distant terrain/LOD edges (the MetalFX spatial path is
  unaffected).
- **Sun motion** moved into the shared `SunMotionTracker`, used by both `PostProcess` (Vulkan) and `PostTaa` (Metal),
  so Metal no longer passes zero sun uniforms.
- **TAA velocity depth** now follows the backend's clip convention (`uClipDepthZeroToOne`): Metal's depth buffer is
  0..1, while Vulkan's is recovered with `2d - 1`. It also flips the depth sample for Metal's top-down targets
  (`uFlipDepthY`) while keeping the projection NDC for the motion vector. This also makes the motion vectors and
  water reactivity use the correct view depth on Metal.
- **TAA reset** now includes a camera cut (>5000 units), and MetalFX gets the same reset.
- **MetalFX motion convention:** the scaler takes render-pixel motion in its y-down framebuffer convention. The
  velocity shader's NDC is y-up, so MetalFX's Y scale is now positive (X stays negative); the old negative Y scale
  mirrored temporal history vertically.
- **SMAA** was ported into `PostComposite` (edges, weights, blend; heat-haze and final-target ordering match Vulkan).
  Metal still has no separate anti-aliasing slider, so SMAA is reachable through `--smaa`/the saved options.
- **FogRate** is documented as a performance-only difference: Metal has no fragment-shading-rate attachment and
  renders fog/shafts at full rate. No no-op toggle was added.

Validation still needs: a still camera with moving sun/clouds, rain and dust with camera motion, MetalFX temporal and
TAA (including a long turn/teleport), and SMAA with no temporal upscaler.

**Goal:** close the remaining visual effect gaps between the Vulkan reference and Metal so Metal can move from an
opt-in preview to a parity/default candidate. This iteration is about **visual effects**, not GPU culling, Hi-Z,
grass compute or general frame-rate work.

**Working rule:** Vulkan stays the reference and the default while MEP-I runs. Do not hide a missing effect behind a
Metal-only visual change; if a fallback is unavoidable, hide or label the setting and document the difference. The
experimental GPU foliage cull stays off and unexposed.

## Current baseline

- Release build: **0 warnings / 0 errors**.
- Non-Metal quick suite: **543 passed / 24 skipped / 1 known upstream failure** (`ItemIconRenderTests`, 0.964 clothing
  overlap against its 0.99 threshold; clean `origin/master` also reports 0.964).
- Metal upload tests are still excluded while the host's `MTLCompilerService` XPC is unhealthy.
- Metal smoke after the merge: 120 s `--new-game Wanderer`, 800x600, 8120 MB budget, 6,693 frames / 56 fps, no GPU
  timeout. A 30-minute soak and the full visual acceptance are still owed.
- The water, character, duel and still paths have been exercised; not every effect has a deterministic still yet.

## Milestones, in order

| ID | Milestone | Main outcome |
|---|---|---|
| **MEP-1** | **Post Effects Parity** | Particle coverage, sun-motion reprojection, SMAA/FogRate decisions and TAA/MetalFX stability |
| **MEP-2** | **Reflection Quality** | Reflection MSAA/resolve and sampling parity at water views |
| **MEP-3** | **Debris Lighting** | Full atmosphere lighting and haze for debris within Metal's sampler budget |
| **MEP-4** | **Tessellation & Terrain Relief** | Metal tessellation: ground displacement, rock smoothing, outline refinement |
| **MEP-5** | **Water Lighting Parity** | Full Meitou water lighting, foam shadows and lighting parity |

### MEP-1: Post Effects Parity

**Purpose:** make the temporal and anti-aliasing paths behave like Vulkan's, and remove no-op effect paths.

Work items:

- **Particle coverage in the reactive mask.** Vulkan's `PostProcess.MarkParticleDraws`/`ParticleRenderer.DrawCoverage`
  adds weather-particle coverage to the upscaler's reactive mask so rain and dust keep their strength under temporal
  history. `MetalPostChain.MarkParticleDraws`/`MarkParticles` are currently no-ops, and `PostTaa` has no coverage
  pass. Port the coverage pass to the seam chain and cover both MetalFX and TAA.
- **Sun-motion reprojection.** `PostTaa` currently sets `uSun`/`uSunMotion` to zero. Reuse Vulkan's
  `PostProcess.PrepareSunMotion` logic (or move it behind a shared helper) so moving clouds/sun do not ghost under
  temporal reconstruction.
- **SMAA decision.** Vulkan has three SMAA passes (`PostProcess.SmaaPasses`); `PostComposite` has no SMAA and the Metal
  UI does not expose it. Either port SMAA to `PostComposite`, or explicitly keep it Vulkan-only and document that the
  Metal anti-aliasing choices are FXAA/MetalFX/TAA. Do not add a visible setting that does nothing.
- **FogRate decision.** Vulkan uses a shading-rate target for fog/shafts (`PostProcess.FogRate`); Metal has no
  equivalent VRS attachment and `MetalPostChain.BuildShadingRate` is a no-op. Treat this as a **performance/quality
  difference**, not a missing visual pass: Metal renders the fog at full rate. Document it and do not claim parity for
  the VRS path. If a non-VRS fallback is added later, keep it behind a measured quality gate.
- **TAA stability.** The selectable Metal TAA path still ghosts on camera motion. Either fix its motion reprojection
  and object/particle terms, or keep TAA labelled experimental and make MetalFX Balanced the tested default. Do not
  make TAA the default while it ghosts.

Likely files:

- `src/Meitou.Game/MetalPostChain.cs` (`MarkParticleDraws`, `MarkParticles`, `BuildShadingRate`, `RunGiResolve`,
  `End`)
- `src/Meitou.Rendering/PostTaa.cs` (sun motion, particle coverage, velocity)
- `src/Meitou.Rendering/PostComposite.cs` (SMAA if ported)
- `src/Meitou.Rendering/PostProcess.cs` and `PostProcess.Merged.cs` (reference behavior and shared helpers)
- `src/Meitou.Rendering/ParticleRenderer.cs` (`DrawCoverage`, `HasCoverage`)

Acceptance:

- Rain, dust and moving clouds compare against Vulkan without new trailing/ghosting.
- MetalFX and TAA both run with the correct reactive-mask inputs; MetalFX remains the default if TAA is still
  experimental.
- No visible anti-aliasing or fog setting is a no-op on Metal.
- Build 0/0, quick non-Metal suite, fixed-scene Metal/Vulkan comparison, and a 120 s Metal run with weather particles.

### MEP-2: Reflection Quality

**Purpose:** finish the reflection pass's visual parity after the shared `ReflectionPass` was enabled on Metal.

Work items:

- Metal's reflection is currently **single-sample**; Vulkan resolves MSAA. Add the Metal MSAA/resolve path where the
  Vulkan path uses it, or document and measure why single-sample is visually equivalent.
- Check reflection sampling at the water edge, horizon, shoreline, objects and characters at levels 1-4.
- Verify reflection memory/lifetime under resize and the 8120 MB budget; keep the shared `ReflectionPass` semantics.
- Keep the existing water reflection settings visible only because they work; fix or hide any that do not.

Likely files:

- `src/Meitou.Rendering/ReflectionPass.cs`
- `src/Meitou.Rendering/WaterRenderer.cs`
- `src/Meitou.Game/GameHost.Metal.cs` (settings exposure)

Acceptance:

- Reflection levels 0-4 behave and look equivalent at the sea, river and Hub water views.
- No reflection aliasing regression versus Vulkan's reference path.
- Resize, alt-tab, night and rain views stay stable.
- Build/tests and side-by-side stills at a fixed camera/time.

### MEP-3: Debris Lighting

**Purpose:** replace Metal's simple debris lighting with Vulkan's atmosphere-lit and hazed path.

Current gap: `DebrisRenderer.MetalFragment` uses simple sun/ambient light because the full atmosphere declarations
plus the diffuse map exceed Metal's 16 fragment samplers. Vulkan uses `kenshiLight`/`atmoApply` and the haze.

Work items:

- Move the debris diffuse texture into the bindless table (or an atlas/packed route) so the full atmosphere shader fits
  in Metal's 16-sampler limit.
- Use the shared atmosphere lighting and haze path on Metal; no Metal-specific lighting approximation.
- Validate debris in normal play, luckyren Mode, combat and stills; preserve the existing depth/alpha behavior.
- Build the sampler-budget helper so MEP-5 can reuse it for water.

Likely files:

- `src/Meitou.Rendering/DebrisRenderer.cs`
- `src/Meitou.Rendering/AtmosphereShaders.cs`
- `src/Meitou.Rendering/Gpu/BindlessTable.cs` / bindless registration
- `src/Meitou.Game/GameHost.Debris.cs`

Acceptance:

- Debris matches Vulkan under sun, shade, night and weather haze.
- No new sampler-limit compile failure; Metal validation clean.
- Fixed-scene debris stills and a live combat/luckyren pass.
- **MEP-3 is the preview gate:** after this milestone a public test may ship as an explicitly labelled Metal preview,
  with MEP-4/MEP-5 listed as known limitations.

### MEP-4: Tessellation & Terrain Relief

**Purpose:** bring Vulkan's tessellation-dependent terrain effects to Metal.

Current gap: Metal reports `HasTessellation == false`; `GroundRelief.DisplaceActive` and
`RockSmoothing.TessellateActive` are not selectable on Metal. Ground parallax works; displacement, rock smoothing and
terrain-outline refinement do not.

Work items:

- Add Metal tessellation pipeline stages: compile the existing tess control/evaluation GLSL through the Metal shader
  path, add patch-list pipeline support and wire `MetalContext.HasTessellation` only when the device supports it.
- Wire `GroundRelief` displacement, `RockSmoothing` and `GroundRelief.RefinePixels` through the shared terrain programs;
  keep the flat/parallax fallback for older devices or unsupported patches.
- Re-enable the terrain settings toggles only after the path is validated; do not leave a visible no-op.
- Test performance and GPU stability carefully: this is the largest and riskiest milestone. Keep the experimental
  GPU foliage cull off.
- Preserve the same water/reflection/terrain cut behavior.

Likely files:

- `src/Meitou.Metal/MetalContext.cs` (capability)
- `src/Meitou.Metal/MetalPipelines.cs` / `MetalShaders.cs`
- `src/Meitou.Rendering/TerrainRenderer.cs`, `TerrainShaders.cs`, `GroundRelief.cs`, `RockSmoothing.cs`
- `src/Meitou.Rendering/WorldFrame.cs` / settings

Acceptance:

- Ground displacement, rock smoothing and outline refinement match Vulkan at matched views.
- Terrain/rocks/water still look correct with the effect off and on; no cracks or black terrain.
- No GPU timeout; measure frame cost before/after and respect the 8120 MB budget.
- Build/tests, fixed-scene comparisons and a long terrain flythrough.

### MEP-5: Water Lighting Parity

**Purpose:** remove the `METAL_WATER` lighting compromise and finish water parity.

Current gap: Metal keeps waves, foam, caustics, reflection and refraction, but `METAL_WATER` drops GI, lamps, dust,
environment specular and material lighting; foam has no sun shadow. The 16-sampler limit is the constraint.

Work items:

- Reuse the bindless/atlas helper from MEP-3 to bring the full atmosphere lighting to water without exceeding 16 direct
  fragment samplers.
- Restore foam sun shadows, lamp/GI response, dust and environment specular.
- Keep reflection/refraction and the MSAA decision from MEP-2.
- Validate sea, river, shoreline, night, rain, lamps and combat scenes against Vulkan.
- Remove or relabel any remaining `METAL_WATER` simplification; no Metal-only water look.

Likely files:

- `src/Meitou.Rendering/WaterRenderer.cs` (`METAL_WATER`, `MetalMeitouFragment`, sampler bindings)
- `src/Meitou.Rendering/AtmosphereShaders.cs` / bindless table
- `src/Meitou.Rendering/ReflectionPass.cs`
- `src/Meitou.Game/GameHost.Metal.cs` (settings)

Acceptance:

- Water and foam match Vulkan in daylight, night, rain and lamp light.
- No sampler compile failure; reflection/refraction remain stable.
- Fixed-scene water comparisons, a long sea/river route and a 30-minute soak.
- **MEP-5 is the full-parity gate.**

## Cross-cutting requirements

- **No Metal-only behavior.** If a Vulkan effect cannot be ported, hide the setting and document it as a known
  limitation rather than silently substituting a different look.
- **Vulkan reference.** Run a Vulkan comparison for every effect milestone; two baselines are allowed because the
  temporal path is noisy. Compare regions/means, not bytes.
- **Temporal comparisons.** Use the same camera, time, weather and simulation ticks; first compare at native/no
  temporal, then with MetalFX/TAA. Cloud/water time still follows real time on Metal, so stills are not byte-comparable.
- **Memory.** Keep the 5120/6144/8120 MB soft budget behavior; MEP-4 and MEP-5 are the largest new consumers. Do not
  raise the default to hide leaks.
- **GPU cull.** Leave the experimental GPU foliage cull off and hidden. It is not part of this iteration.
- **Visual acceptance.** The user validates visual changes manually; an automated build/test pass is not visual
  acceptance.
- **Upstream.** Rebase/merge `origin/master` before closing MEP-I; the branch is `metal-spike`.

## Preview and parity gates

| Gate | Requirement |
|---|---|
| **Preview test** | After MEP-3: build 0/0, non-Metal tests, Metal smoke, no GPU timeout, known gaps documented; Vulkan remains default |
| **Full effects parity** | After MEP-5: all five milestones accepted, Vulkan comparisons, 30-minute soak, no hidden no-op settings |
| **Default switch** | Only after full parity and a separate performance/stability decision; not part of MEP-I |

## Validation commands

Metal (do **not** set `DYLD_LIBRARY_PATH`):

```bash
dotnet build MeitouClient.slnx -c Release --no-restore
dotnet run --project src/Meitou.Game --no-build -c Release -- \
  --renderer metal --windowed --size 800x600 --new-game Wanderer --quit-after 120
```

Vulkan reference:

```bash
DYLD_LIBRARY_PATH=/opt/homebrew/lib dotnet run --project src/Meitou.Game --no-build -c Release -- \
  --renderer vulkan --windowed --size 800x600 --new-game Wanderer --quit-after 20
```

Non-Metal quick suite (known upstream item-icon failure):

```bash
DYLD_LIBRARY_PATH=/opt/homebrew/lib dotnet test tests/Meitou.Tests/Meitou.Tests.csproj -c Release --no-build \
  --filter 'FullyQualifiedName!~Metal'
```

For effect comparisons, use fixed camera/time/weather and the water views in
[metal-backend](metal-backend.md#resume-notes-facts-already-established). Keep `MTL_DEBUG_LAYER=1` for validation only,
not performance numbers.

## Risks and blockers

- **Metal sampler limit:** debris and water both need a bindless/atlas design; do it once in MEP-3.
- **Tessellation:** the largest engineering risk; isolate it and stop the milestone if the Metal pipeline path cannot be
  made stable.
- **TAA/MetalFX:** temporal artifacts are hard to compare; use native/no-temporal baselines first.
- **MTLCompilerService XPC:** the full Metal test suite may remain unavailable until a full shutdown/power-on; do not
  treat that as a code failure.
- **Memory/stability:** run the 30-minute soak again after the merge before public shipping; the 120 s smoke is only
  a smoke.
- **Existing no-op/quality issues:** TAA ghosting, FogRate/VRS and Hi-Z are documented; do not accidentally ship them as
  “done” without a decision.

## Immediate next action

Start MEP-1 with the two smallest correctness gaps:

1. Port the particle coverage pass into `MetalPostChain`/`PostTaa` and feed it to both MetalFX and TAA.
2. Move the sun-motion data from `PostProcess.PrepareSunMotion` into a shared helper used by `PostTaa`.

Then decide SMAA (port or document as Vulkan-only) and FogRate (document as performance-only unless a non-VRS fallback
is chosen). Do not start MEP-4 before MEP-1 through MEP-3 are stable.
