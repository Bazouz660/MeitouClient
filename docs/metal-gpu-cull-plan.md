# Metal GPU foliage cull plan

Status: **proposed, not implemented** (2026-10-23). This is the Metal port of the existing Vulkan GPU foliage
cull and grass submission. It is the A2 item in [metal-performance-memory](metal-performance-memory.md), not a
new renderer feature; the visible result must stay the same.

## Why

At the save-start view the Metal scene encoder is 6.75-7.77 ms, with **672-673 draws** and **14.7M vertices**.
`FoliageRenderer` still takes the CPU path on Metal:

```csharp
gpuCull = gpu is GpuContext vk ? new FoliageGpuCull(vk) : null;   // Metal takes the CPU cull
```

The CPU walks the zones, culls every group/instance and emits the draws. The Vulkan path instead runs
`FoliageGpuCull` (cull, scan, compact) and `FoliageGrassGpu` (cull, order) on the GPU, then binds a small
indirect argument buffer. Porting that path gives Metal:

- less CPU culling and draw submission;
- indirect draws whose counts/placement come from the GPU;
- the same LOD/culling inputs the Vulkan path already uses;
- fewer per-frame command-list bindings.

It does **not** replace the scene/object/terrain shader ALU work; both tracks are needed.

## Existing pieces

- `FoliageGpuCull`: instance arena, cull/scan/compact compute kernels, per-view scratch, indirect arguments,
  readbacks for statistics. It currently requires `GpuContext` and push descriptors.
- `FoliageGrassGpu`: blade arena, slot table, cull/order kernels, indirect draws. `Supported` currently requires
  Vulkan push descriptors, `DrawIndirectCount`, `DrawIndirectFirstInstance` and `MultiDrawIndirect`.
- `FoliageRenderer`: has both paths behind `GpuCull`/`GpuGrassActive`; the CPU path is the correctness fallback.
- `MetalCommandList`: already has indexed indirect draws, but no multi-draw and no draw-count buffer. It records a
  loop over slots; unused slots must contain zero index counts. This is the main Metal constraint.

## Implementation order

### 1. Make the shared pieces backend-neutral

- Widen `FrameScratch` to `IGpuContext` or add a Metal-specific per-frame bump allocator with the same
  `TryAllocate`/`Cap` contract.
- Widen the arenas and `DeviceBuffer` helpers used by the cull/grass stores, or put a thin backend interface in
  front of them.
- Keep `FoliageCullChunk`, `FoliageCullDraw`, `FoliageCullWork`, `FoliageCullResult` and `GrassResult` unchanged:
  they are the seam between the renderer and the kernels.
- Do not make `GpuContext` public API; keep the Vulkan path compiling and behaviourally unchanged.

### 2. Metal compute and argument buffers

- Remove the `PushDescriptors` requirement from the cull constructors. Bind the same compute sets through
  Metal argument buffers (`BindSets`/`BindSetArguments`), as the native scene programs already do.
- Verify the cull kernels' reflected storage-buffer sets against the Metal set layouts; the shaders are already
  compute GLSL and should translate through the existing native compiler path.
- Dispatch the cull work in `IGpuFrame.PreFrame` (or the Metal equivalent before the host pass), as Vulkan does.
- Keep the cull's output buffers in the frame's slot; do not allocate them per draw or per shadow cascade.

### 3. Indirect arguments

Metal has no `DrawIndexedIndirectCount` or `MultiDrawIndirect`, so the renderer must:

- pad the indirect argument list to `DrawCount` and zero every unused slot's `indexCount`;
- record one `drawIndexedPrimitives` per slot, using the existing `MetalCommandList.DrawIndexedIndirect`;
- keep the count buffer only as diagnostic data, not as a Metal draw argument;
- ensure the cull/scan kernels zero the tail before the draw list is used.

This means the CPU still issues one loop iteration per candidate draw. The win is that the CPU no longer culls
instances or builds matrices; the number of loop iterations must stay bounded by the existing scratch caps.

Verify whether Metal's indirect arguments preserve `baseInstance`. `FoliageGrassGpu` uses the first blade
(`firstInstance`) per page. If `baseInstance` is not supported or is unreliable, either:

- encode the blade offset into the vertex/index data for the Metal grass path, or
- keep CPU grass submission until the grass-specific indirect path is ready, while porting mesh/rock culling
  first.

### 4. Meshes and terrain-mode rocks first

Port `FoliageGpuCull` first, because the mesh/rock draw path already has a CPU fallback and the outputs are
standard indexed indirect arguments. Use it for:

- main near-slice colour/depth;
- shadow cascades (the work list is shared per frame when the eye/range match);
- reflection (with grass disabled).

Validate against `--no-foliage` and the CPU path; the picture and draw counts must match.

### 5. Grass second

Port `FoliageGrassGpu` after the mesh cull is stable. The grass cull/order kernels already handle density,
wind, page ordering and LOD; the Metal-specific part is the indirect loop and the blade-offset handling.
If the Metal indirect loop becomes more expensive than the CPU grass for low blade counts, keep the CPU path
for that view and select the GPU path by visible blade count.

### 6. Guards, memory and fallback

- Keep the CPU path as the fallback when compute/indirect support is unavailable.
- Respect `VramGuard`: `FrameScratch.Cap`, arena growth and the guard's range shortening must remain wired.
- Do not grow a Metal buffer in place while a frame still names its slice; use the existing frame-chunk/ring
  lifetime rules.
- Readbacks used for statistics must remain a frame or two behind, never a stall.

## Validation

- Vulkan build and quick tests unchanged.
- Metal run at the save-start view with `--no-foliage` and with foliage; compare F11 instance/draw counts.
- Capture a Metal frame and confirm the cull compute encoders appear before the scene encoder and the indirect
  arguments produce the same instance counts as the CPU path.
- Check `MTL_DEBUG_LAYER=1` for indirect-buffer/argument errors.
- Measure CPU record time, draw calls, vertices and GPU time before/after. A GPU-cull port that lowers CPU but
  leaves GPU ALU unchanged is still useful, but must be reported as a CPU win.

## Other ideas worth keeping

These are deliberately **not** part of the first port.

- **Half precision:** Apple GPUs run 16-bit ALU faster, but the engine compiles GLSL to SPIR-V/MSL as float.
  A Metal-specific `half` variant for colours/normals/UVs/lighting (not world positions, depth or large
  coordinates) could reduce ALU and bandwidth. It needs a separate pipeline variant and a visual/perf A/B.
- **Scene shader specialization:** compile out branches for features that are off (GI on this Mac, unused
  atmosphere/specular paths). This is likely more valuable than half precision if the ALU limiter stays high.
- **Post shafts:** 11.27% of a recent save-start frame. Check resolution/step count and whether the title's
  29% case is representative; do not lower quality blindly.
- **Shadow cascades:** already have cadence and cached far cascades. A static/dynamic split is only worth it if
  the current redraw cadence still shows up in the frame profile.
- **Tight foliage geometry:** the existing depth prepass and early-depth variants already address most alpha-test
  overdraw. Remeshing grass is not a priority while grass is ~2% of the frame.
- **Mesh shaders:** a long-term project, not justified by the current 15-27 ms frames.
- **Encoder merging and purgeable textures:** low-priority bandwidth/memory work; see
  [metal-performance-memory](metal-performance-memory.md).
