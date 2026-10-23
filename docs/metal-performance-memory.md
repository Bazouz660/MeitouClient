# Metal performance and memory plan

Status: **partially implemented** (2026-10-21): presentation diagnostics and vsync wiring, the map-tile/source memory
releases, an idle LOH trim, retired-texture lifetime and a three-cycle soak are in. The GPU cull/indirect port and the
remaining frame-rate work are not. Read this with [metal-backend](metal-backend.md); the macOS Vulkan numbers are in
[macos-port](macos-port.md) section 7.

## Baseline

Apple M1 Pro, **Release**, Hub view, windowed 1280x856 points (2560x1712 drawable), TAA at 0.50 unless stated.

| Run | FPS | GPU time | CPU/frame | Notes |
|---|---:|---:|---:|---|
| Metal, scale 0.83 | 21-27 | 30-36 ms | ~50 ms trace `cpu` | older scale |
| Metal, scale 0.50 | **30-32** | **19-22 ms** | 32-35 ms trace `cpu` | current default |
| Vulkan, scale 0.50 | **36-40** | 27.5 ms | 6.3 ms | macOS report, vsync on |

Metal's GPU time is already **lower** than Vulkan's at the same scale. The native Metal advantage exists; the frame is
limited by CPU work, command recording and `CAMetalLayer.NextDrawable`/present pacing. A sampled Release thread spent
~73% of wall time in `NextDrawable` and only ~27% in managed/native CPU time. The frame is not GPU-bound at 0.50.

Memory before this plan's fixes: 97,000 live texture views / 143,000 total after 75 s, 6.9 GB in `IOAccelerator`;
173 resident impostor atlases retained ~1.6 GB of CPU levels; managed LOH 2.8 GB and RSS ~2.3 GB. The view cache,
`ImpostorTextures.DropCpu` and the upstream pooled-buffer commit reduced LOH to ~1.2 GB and RSS after load to
~1.4-1.6 GB. There is **no observed OOM now**; the remaining growth is high-water caches, native resources and GC
commitment, not a proven single leak.

## Progress (2026-10-21)

- **Presentation:** `MetalDisplay` now sets three drawables (matching `MetalContext.FrameRingDepth`), exposes
  `displaySyncEnabled` (so `--vsync`/`--no-vsync` and the saved setting reach Metal as they do Vulkan), keeps
  `allowsNextDrawableTimeout`, and records the actual `NextDrawable` wait. `MEITOU_METAL_TRACE=1` now prints
  `drawable ... ms, record ... ms`; `ResourceSummary` prints managed/LOH/RSS/GPU allocation.
- **Measured:** the built-in display is 120 Hz. At 0.50 with vsync the steady frame is **30-31 fps**:
  `NextDrawable` 25-27 ms, record 5-7 ms, GPU 19-22 ms. With `--no-vsync` the same view reaches **~45-48 fps**
  (drawable 14-18 ms, record 5-7 ms); the GPU is not the only wall. A 120 Hz vsync frame has to fit the 25 ms
  three-refresh window; 20 ms GPU + 6 ms record lands in the four-refresh (33 ms) bucket. The documented Vulkan
  36-40 fps at 27.5 ms GPU does not fit that bucket, so normalize its actual present mode/refresh before treating it
  as a like-for-like target.
- **Memory:** `MapWindows` retains 4 overlay/2 colour decoded tiles only during a compose and clears them afterwards
  (was 24 + 12 full decoded maps, hundreds of MB of LOH at 2048²/4096²). `TerrainTextures.Source` drops its blend/ground/world-colour/biome
  CPU images after upload. When terrain/object/foliage streaming has been quiet for two seconds, the host does one
  compacting LOH collection (at most once a minute).
- **Result:** over a 75 s Release run at the Hub, managed LOH fell from ~1.8 GB to **~0.67-0.69 GB**, managed heap to
  ~1.3-1.6 GB and RSS stayed ~0.9-1.7 GB without OOM or monotonic growth. The overview/map screenshot and the
  `MTL_DEBUG_LAYER=1` gate are clean.
- **Soak:** Release, 1280x856, TAA 0.50, vsync; Hub -> four zones east (~18.4k units) -> Hub, three cycles, a
  12 s settle at every endpoint. 260 s / 7,725 frames at 30 fps. GPU allocation **2.81-3.20 GB** (final 2.91), with
  **0 retired views** at idle; LOH after the four trims was 805, 736, 656 and **584 MB**; managed heap 1.2-1.4 GB;
  RSS 1.0-2.3 GB. No OOM or crash. A separate `MTL_DEBUG_LAYER=1` one-zone round trip also exited 0.
- **Retired textures:** `MetalContext.ReleaseTexture` now retains the command buffer that was current at release and
  disposes the views and parent only after the three-frame ring and that command have completed, at most 64 views a
  frame. Before it: 890 retired views and ~4.5 GB GPU; after: 0 retired views and ~2.9 GB, stable across the soak.
- **Mip views and deferred frees:** mip-bake level views are retained by the command that encoded them and disposed
  once it completes; `DeferDelete` now waits for the frame ring instead of releasing immediately. The trace prints
  `levelViews` and `deferredDeletes`; the short one-zone route ended with both at 0 and a clean teardown.
- **Foliage cull:** the CPU cull now feeds the render jobs in chunks of 16 groups instead of 1,404 separate work
  items at the Hub. Cull outputs and batch order are unchanged; the same 45 s Release trace dropped record time from
  **6.15 ms to 5.23 ms** (~0.9 ms/frame). The trace prints the last cull group count.
- **Also:** the large per-frame foliage/object/terrain matrix arrays are written directly into the frame constants
  (`IGpuFrame.AllocateConstants`) instead of being allocated and copied twice; the GLFW API lookup for edge scrolling
  is cached; the trace prints terrain/object/foliage streaming pending work.
- **Gate:** build 0/0; tests 424/425 (the known arm64 `GoldenHashTests.Scripted_scenarios_hash_as_pinned` pin).
- **Still open:** the same-window vsync >40 fps gate, GPU culls/indirect draws/grass, remaining shadow recording
  (~2 ms per frame in the steady profile), and a longer 60 s-per-idle/multi-direction soak.

## Targets

### Frame rate

- At 0.50 TAA, **the same window and vsync as Vulkan**, average **>40 fps** and 1% low no worse than Vulkan's.
- At 0.83, >30 fps.
- Same Release build, view, time/weather, shadows, clouds and `--no-gi`; normalize window size and render scale before
  comparing. Do not compare a windowed Metal run with a fullscreen Vulkan run.
- `MTL_DEBUG_LAYER=1` is a validation gate only (~2 fps); never use it for performance numbers.

### Memory

- A three-cycle soak (fly through 5-10 zones, return, idle 60 s) must plateau: no monotonic growth across cycles.
- Managed LOH after idle **<1 GB**; process footprint **<8 GB**; GPU allocation below the Metal working-set budget;
  no OOM. Post-return footprint should be close to the first-cycle baseline, not the tour peak.

## Track A: frame rate

### A0. Measure the frame first

- Add/keep a trace that prints, per frame: total frame time, command-buffer GPU time, CPU record time, and
  `NextDrawable` wait. `MEITOU_METAL_TRACE=1` already prints CPU/GPU and resource counts.
- Take one `dotnet-trace --profile dotnet-sampled-thread-time` capture while the window is frontmost; separate
  loading/LOD construction from the steady frame.
- Measure the frame-time distribution, not only the title average: 1% low, drawable stalls, command-buffer completion.
- Record `MTLDevice.CurrentAllocatedSize` (or the SharpMetal equivalent), resident texture bytes, and process
  `vmmap`/`footprint` categories for the memory track.

### A1. Remove the present/drawable stall

The profile's `NextDrawable` wait means the frame is partly serialized on presentation, not shading. Before changing
rendering:

- Keep **three frames in flight** end to end; use command-buffer completion/presented handlers instead of implicit
  pacing. Acquire the next drawable only when a slot is free.
- Review `CAMetalLayer` pacing: drawable count, `displaySyncEnabled`, `presentsWithTransaction` and
  `allowsNextDrawableTimeout`. Test with vsync on (shipping) and off (diagnostic) to tell a true 20/30 ms frame from
  a vsync-quantized one. Do not disable vsync in the shipping default.
- Check whether 30 fps is a quantized 20 ms frame or a real 32 ms frame; the fix differs.
- Re-run the same-window comparison after this step; it may expose the existing GPU headroom without touching the
  renderers.

### A2. Port the GPU-side draw path

The mesh/rock cull is now ported and live on Metal: `FoliageGpuCull` uses `IGpuContext`, `FrameScratch` is
backend-neutral, and `MetalFrame.PreFrame` is a command buffer of its own committed before the frame. The remaining
A2 work is grass and the other indirect draw producers; the concrete plan is in
[metal-gpu-cull-plan](metal-gpu-cull-plan.md).

- `MetalFoliageCullTests` compares the cull's compacted rows, offsets and indirect arguments with the CPU reference on
  Metal; the live Metal run now takes the GPU mesh/rock path (grass still CPU).
- Port `HizPyramid` and `FoliageGrassGpu` next; replace their push-descriptor checks with argument buffers.
- Metal has no draw-count/multi-draw command: record the bounded slots and keep their unused arguments at zero.
- Verify whether Metal preserves `baseInstance` in indirect arguments before making the grass path depend on it.
- Keep the CPU cull as the correctness fallback and for stills.

### A3. Reduce the remaining main-thread work

- Profile the steady frame split: foliage recording/culling, shadow cascades, `PlayerInterface.Draw`/HUD,
  `FoliageRenderer.Draw`, simulation, GC.
- Hoist repeated GLFW/`WindowPointer` lookups; remove per-frame managed allocations; keep the view cache and
  pooled buffers.
- Use parallel recording where safe (`MTLParallelRenderCommandEncoder` for a pass, worker-recorded independent
  segments); do not introduce cross-thread encoder state.
- Unify the bespoke Metal loop with `WorldFrame`/`SceneHost` on `IGpuContext` or remove duplicate per-frame work.
- Shadows remain deferred by request; do not spend the frame budget on new shadow features here.

### A4. Upscaler and post, only after A1-A3

- TAA at 0.50 remains the performance baseline (its Metal motion-projection ghosting is a separate bug);
  native/no-TAA and MetalFX are clean comparison views. The game default is MetalFX Balanced 0.50, with Native as the
  fallback and spatial 0.40 for performance.
- MetalFX temporal measured slower than TAA at the same scale, so it stays out of the measured baseline while the
  scene is optimized; the F10 settings panel is the quality/performance user control.
- Add the rest of the Vulkan post chain (SSAO, shafts, particles, FXAA, GI resolve) after the frame has CPU/GPU
  headroom; do not let new effects consume the gains.

**Track A gate:** same-window 0.50 TAA average >40 fps, 1% low no worse than Vulkan, build 0/0, validation clean.

## Track B: bounded memory

### B0. Instrument the categories

Expose in F11/trace, at minimum:

- managed heap: gen0/1/2, LOH, POH, committed;
- `ArrayPool` retained bytes and pool high-water;
- `WorldTextureCache`: resident count/bytes, pending, unloads/reloads;
- `MetalContext`: live textures/views/buffers/sets, `retiredViews`, `levelViews`, frame chunks;
- Metal: `MTLDevice.CurrentAllocatedSize`, `RecommendedMaxWorkingSetSize`;
- per-cache sizes: terrain source/biome maps, foliage/object mesh+LOD, zone state, navmesh, impostor atlases.

RSS alone is not enough: it includes native, GPU, driver and compressed pages that GC cannot reclaim.

### B1. Evict by camera/zone and idle time, not only high-water

- `WorldTextureCache`: force an LRU trim when leaving a zone cluster/returning, in addition to the high-water/idle
  rule; lower the high-water target if the soak shows a larger plateau than needed.
- Terrain: release biome source/window maps outside the camera radius; `TerrainTextures.Source` is temporary CPU data
  and must not stay rooted after `Create` (the remaining ~1.2 GB LOH is mostly terrain source/resident texture data).
- Foliage/objects: unload mesh/LOD assets not referenced by a nearby zone; keep a small LRU rather than all assets
  ever visited.
- Zone state/navmesh: navmesh already unloads after 30 s; give foliage/object zone state and streamer pages the same
  explicit lifetime.
- Impostor atlases: keep the 20 s idle GPU unload; ensure CPU levels are dropped on completion (`DropCpu`).
- Pooled DDS/GI/card/grass buffers: cap the pool per size class and trim it on idle; `ArrayPool` retention is a
  deliberate RSS/GC trade, not free memory.

### B2. Fix native Metal resource lifetime

- `retiredViews`, mip-bake `levelViews` and `DeferDelete` now drain after the frame ring/command completion; `Finish`
  still keeps texture parents through the bindless ring, and `Dispose` forces the remainder. `ResourceSummary` reports
  all three counts.
- Trim `MetalFrame` constant chunks to the observed high-water and release unused chunks.
- Use `MTLHeap`/purgeable resources for large transient targets if the driver keeps freed allocations; verify with
  `MTLDevice.CurrentAllocatedSize`, not RSS.

### B3. GC and pools

- Do not rely on ordinary `GC.Collect` to lower RSS: it does not compact the LOH by default and server GC does not
  aggressively decommit.
- At safe points only (loading screen, return/teleport): set
  `GCSettings.LargeObjectHeapCompactionMode = CompactOnce` before a forced compacting collection; test
  `DOTNET_GCConserveMemory` / `DOTNET_GCRetainVM` in Release.
- Keep the upstream pooled-buffer changes, but distinguish their lower allocation/GC-pause benefit from peak RSS.
- Avoid per-frame managed allocations in the Metal host; they add GC pressure without showing in the FPS average.

### B4. Soak and classify

Fly a fixed route through 5-10 zones, return, idle 60 s, repeat three times. Record RSS, physical footprint,
managed LOH, GPU allocation, resident textures and per-cache counts after each idle.

- **Plateau after cycle 1-2:** bounded high-water cache; tune budgets/eviction.
- **Linear growth each cycle:** per-visit leak; find duplicate `ZoneState`, mesh/LOD, atlas, view or buffer objects.
- **Large managed heap with small RSS:** GC not decommitting; compact LOH / tune GC.
- **RSS high with small managed heap:** native/GPU/driver retention; drain views/buffers and check Metal allocation.

**Track B gate:** three-cycle soak plateaus, LOH after idle <1 GB, footprint <8 GB, no OOM, no monotonic growth.
The 2026-10-21 run met the managed/GPU plateau with 12 s endpoint settles; the 60 s endpoint variant is still to run.

## Order

1. **A0 + B0:** same-window benchmark and memory instrumentation; agree on the baseline.
2. **A1:** present/drawable pacing and easy CPU wins; establish how much of the gap is pacing.
3. **A2:** GPU culls/indirect/grass; remove main-thread culling and draw setup.
4. **A3:** remaining CPU phases, parallel recording and host unification.
5. **B1 + B2:** cache budgets/eviction and native view lifetime (can run alongside A2/A3 after B0).
6. **B3/B4:** GC/pool trim and the three-cycle soak.
7. **A4:** MetalFX/lower scale and post parity only after the frame budget is healthy.

## Current build facts (for a cold resume)

- Commits on `metal-spike`: `658456a` (zoom/layer reattach), `137582b` (view cache, impostor CPU release,
  0.50 TAA upscaling), `3cf2151` (upstream pooled buffers).
- `MetalContext.View` caches by texture and level/layer/swizzle; released views are retired after the command and
  frame ring have passed, then their parent texture is freed.
- `ImpostorTextures.DropCpu` keeps only metadata after the last upload; `PostTaa` uses a display-size history and a
  render-size motion target; Metal defaults to 0.50 with `--render-scale` overriding.
- The remaining LOH is terrain source/resident texture data; the old view and impostor leaks are fixed.
- Release is the performance build; Debug was ~20 fps. The clean Release window is the current reference.
- No GI on either backend on this Mac (`VK_KHR_ray_query` is unavailable to Vulkan); compare with `--no-gi`.

## Xcode insight triage

Xcode repeats a few generic API/performance insights on every capture. They are not all actionable in this
renderer; read them with the frame's pass table and limiter counters.

- **Unnecessary depth pass.** The 24,576-index draw is the terrain prepass (`64 × 64 × 6`). It is not just an
  HSR prepass: `TerrainLateColour` uses it to draw terrain colour only where objects and foliage leave it visible,
  and the depth is read later by SSAO, shafts and particles. Do not remove it without a controlled
  `TerrainLateColour` A/B; it is only ~0.5% of a normal frame.
- **Separate encoders can be combined.** Real, but only for passes with the same attachments and no dependency.
  Shadow cascades, reflection, scene, water, post and UI do not meet that condition. Revisit after the ALU limit
  and load/store bandwidth are measured.
- **Texture opted out of lossless compression (`PixelFormatView`).** `MetalContext` now omits the view usage for
  plain 2-D single-level targets and uses the base texture for full-range identity views. Sub-ranges, layers and
  swizzles still use texture views. This is a memory/bandwidth improvement, not an ALU one.
- **Texture unused for 37 s / never accessed.** These are usually resident caches or title/UI textures. Purgeable
  state needs a reload policy; it is a memory-budget item, not frame time.
- **MTLBuffer created during GPU work.** A few frame constants/argument buffers grow or are made during the frame.
  Pool/preallocate them if CPU hitches remain; it is not the main GPU cost.
- **Redundant bindings and state.** `MetalCommandList` caches the vertex and fragment buffer plus offset, the
  viewport, the scissor and the depth-stencil state for the encoder's lifetime and skips an identical rebind. The
  remaining scene entries are mostly per-draw dynamic buffer offsets and push-constant blocks; texture/sampler
  binds are separate and not covered yet.

`Group by API Call` is the useful grouping for pass costs; `Group by Pipeline State` is for finding expensive
shader pipelines.

## GPU frame capture

`Ctrl+/` records one complete frame with `MTLCaptureManager` and writes it to
`~/Documents/Meitou Captures/meitou-<timestamp>.gputrace`; open that package in Xcode's Metal debugger.
The capture starts before the frame's command buffer and stops after the present commits, so it contains one frame,
not the three in flight. The first frame is deliberately rejected: pressing during loading would capture the world's
resource uploads as well and can produce a multi-gigabyte trace.

Metal capture must be enabled before the process starts:

```sh
MTL_CAPTURE_ENABLED=1 \
DYLD_LIBRARY_PATH=/opt/homebrew/lib \
dotnet run --project src/Meitou.Game --no-build -c Release -- \
  --renderer metal --windowed --size 800x600
```

Wait until the world is visible and has rendered a few frames, then press `Ctrl+/`. The trace is a GPU frame, not a
CPU profile: use `MEITOU_METAL_TRACE=1`, Instruments' Metal System Trace or `dotnet-trace` for CPU work. Do not use the
capture frame for FPS measurements. A full frame with its resources is large; check free disk space before capturing.

## Review commands

```sh
# Live Release, default 0.50, Hub
DYLD_LIBRARY_PATH=/opt/homebrew/lib dotnet run --project src/Meitou.Game -c Release -- \
  --renderer metal --new-game --time 12 --distance 2000 --pitch 20 \
  --faithful clouds,cloudshadows --no-gi

# Frame CPU/GPU, resource tables and native window state
MEITOU_METAL_TRACE=1 ...

# Same view at another scale
... --render-scale 0.83
... --render-scale native

# Validation gate (not a performance run)
DYLD_LIBRARY_PATH=/opt/homebrew/lib MTL_DEBUG_LAYER=1 dotnet run --project src/Meitou.Game -c Release -- \
  --renderer metal --screenshot /tmp/metal-gate.png --no-population --time 8 \
  --distance 2000 --pitch 45 --faithful clouds,cloudshadows --no-gi

# Managed heap / allocation sampling
~/.dotnet/tools/dotnet-counters collect -p <pid> --duration 00:00:15 --format csv -o /tmp/meitou-counters
~/.dotnet/tools/dotnet-trace collect -p <pid> --profile dotnet-sampled-thread-time \
  --duration 00:00:10 -o /tmp/meitou-cpu --format Speedscope
~/.dotnet/tools/dotnet-gcdump collect -p <pid> -o /tmp/meitou.gcdump
```

Tests: `dotnet test -c Release --filter "Category!=Slow&Category!=Bench"` is 424/425; the single failure is the known
arm64 `GoldenHashTests.Scripted_scenarios_hash_as_pinned` pin.
