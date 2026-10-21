# Metal performance and memory plan

Status: **plan, not implemented** (2026-10-21). Read this with [metal-backend](metal-backend.md);
the macOS Vulkan numbers are in [macos-port](macos-port.md) section 7.

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

This is the largest CPU win and the main reason Vulkan still leads:

- Widen `HizPyramid`, `FoliageGpuCull` and `FoliageGrassGpu` to `IGpuContext`; replace push-descriptor checks with
  Metal argument buffers.
- Drive the indirect draws from GPU culls and use Metal indirect command buffers/indirect draw calls for terrain,
  foliage, grass, objects and impostors.
- Keep the CPU cull as the correctness fallback and for stills; the live path should use the GPU outputs.
- Give `FrameScratch` a Metal bump allocator over shared buffers.

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

- TAA at 0.50 is the current macOS reference and the first target.
- MetalFX behind `IUpscaler` (and 0.33/quality scales) is a later lever; it will not fix a CPU/present-bound frame.
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

- `retiredViews` and `levelViews` currently live until context teardown. Drain them after the frame ring or a
  command-buffer completion, when no recorded frame can reference them.
- Dispose texture views and buffers when a texture is unloaded; do not only null the table slot.
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
- `MetalContext.View` caches by texture and level/layer/swizzle; released views are retired until teardown.
- `ImpostorTextures.DropCpu` keeps only metadata after the last upload; `PostTaa` uses a display-size history and a
  render-size motion target; Metal defaults to 0.50 with `--render-scale` overriding.
- The remaining LOH is terrain source/resident texture data; the old view and impostor leaks are fixed.
- Release is the performance build; Debug was ~20 fps. The clean Release window is the current reference.
- No GI on either backend on this Mac (`VK_KHR_ray_query` is unavailable to Vulkan); compare with `--no-gi`.

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
