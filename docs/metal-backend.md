# Metal backend

A second native path for the renderer, next to Vulkan. Status: **a spike on the branch
`metal-spike`** (local, not upstream; no engine changes). It answers which mechanisms a Metal
backend can use on Apple silicon, and what such a backend would need from the engine.

Bindings: `SharpMetal 1.1.0` (IsaacMarovitz; Metal, MetalFX, QuartzCore, the Objective-C
runtime). `Silk.NET.GLFW` has no `glfwGetCocoaWindow`, so the `NSWindow*` comes from the same
brew `libglfw.3.dylib` the Vulkan path loads.

## What the spike proves

| Mechanism | Result |
|---|---|
| Device, `CAMetalLayer` on the GLFW window, clear and present | works |
| GLSL -> SPIR-V (glslang) -> MSL (spirv-cross) -> runtime compile | works |
| Uniform block (std140), storage buffer (std430), dynamic indexing | works |
| Runtime-sized texture **and** sampler tables, dynamically indexed (tier-2 argument buffer) | works |
| Compute pass writing an rgba16f storage image, sampled back | works |
| Barriers between passes | **not needed** - Apple silicon tracks hazards itself |

## The shader chain

`sdk`-free: the engine's own `GlslProgramCompiler` produces SPIR-V; `spirv-cross` turns it into
MSL. Per stage, with the flags that matter:

```
spirv-cross --msl --msl-version 30000 --msl-decoration-binding \
  --msl-argument-buffers --msl-argument-buffer-tier 1 \
  --msl-device-argument-buffer <set> --msl-discrete-descriptor-set 0 \
  --rename-entry-point main <entry> <stage> in.spv --output out.metal
```

- `--msl-decoration-binding` keeps the MSL resource indices equal to the SPIR-V binding
  numbers, so a Metal backend mirrors the Vulkan binding scheme instead of carrying a
  per-shader mapping table. Without it spirv-cross renumbers resources by its own traversal
  order.
- Both stages come out as `main0` with a `main0_out` struct, so two stages in one source
  collide: one library per stage, the vertex function from one and the fragment function from
  the other.
- MSL 3.0 (macOS 13+) is the floor: tier-2 argument buffers with runtime-sized arrays need it.

## The bindless table

`BindlessTable` declares seven **combined** image samplers. Metal has no combined sampler, and
spirv-cross refuses the case outright ("Argument buffer runtime array currently not supported for
combined image sampler"). The way through is **separate textures and separate samplers**
(`uniform texture2D table[]; uniform sampler samp;`), which the engine's shader compiler can
rewrite - it already rewrites sources (interface locations, per-stage bindings).

The runtime model is MoltenVK's, on Metal 3:

- the table's descriptor set becomes a tier-2 argument buffer in the device address space;
  the argument buffer takes the buffer index of its descriptor set, so that set's number must
  not collide with the binding numbers of the ordinary set;
- one shared buffer holds the argument buffer: the two pointers (to the texture handle array and
  to the sampler handle array) followed by the handles themselves; a texture's handle is
  `MTLTexture.gpuResourceID`, a sampler's `MTLSamplerState.gpuResourceID`, a buffer's address
  is `MTLBuffer.gpuAddress`;
- the arrays live **inside that buffer** and the pointers address them by offset. Keeping them
  in separate buffers faults the GPU.

Two sharp edges:

- `MTLRenderPassDescriptor.ColorAttachments.Object(0)` returns a struct copy, so a value-type
  property (the clear colour) set on the copy is lost and the pass clears to uninitialised
  memory. Write it back with `SetObject`.
- The `CAMetalLayer`'s drawable size follows the view's bounds, and the view has no bounds yet
  while the window is being created: set the size from the window's size times the scale factor
  or the drawable is 1x1 and the window shows one magnified pixel.

## What a second backend would need of the engine

`Gpu/` has no backend abstraction: two abstract classes (`Uniform`, `RecordJob`), and eight
types carry Vulkan handles (`Image`, `ImageView`, `Sampler`, `Buffer`, `GraphicsPipeline`,
`ComputePipeline`, `CommandBuffer`, the frame slot). A Metal backend therefore needs a seam
first - a change to the engine's core, and the author's to choose. The neutral descriptions
(`GraphicsPipelineDesc`, `PipelineLayout`, `ShaderProgram`) are already backend-neutral, and the
passes above `Gpu/` only use the neutral types: the sky pass needs `Pipelines`, `Globals`,
`Samplers`, `Uploads`, `LodBias` and eight `CommandList` calls (`BindPipeline`, `Draw`,
`SetViewport`, `SetScissor`, `SetRaster`, `SetDepth`, `SetDepthBias`).

## Constraints

- **GI**: the probe GI needs ray queries (`VK_KHR_ray_query`), which have no Metal
  translation; Metal's ray tracing is a different programming model. The engine already runs
  without GI where the device has no ray queries (macOS today), so a Metal path starts with GI
  off, like the Vulkan path on macOS.
- **Retina**: the drawable size has to be set explicitly (see above), or the Metal path renders
  at 1x where the Vulkan path is at 2x.
- **Upscaler**: MetalFX is in SharpMetal (`MTLFXTemporalDenoisedScaler`) and would sit behind
  `IUpscaler`; the FSR path stays for Vulkan.

## Open questions

- The shape of the seam: backend-neutral handle types, a backend interface, or a parallel Metal
  implementation of the neutral API.
- Per-entry sampler state: the table's entries carry their own sampler, so the table needs a
  sampler array as well as a texture array.
- Whether a Metal path is worth a second renderer at all: the macOS Vulkan path works, and the
  measured cost of MoltenVK is concentrated in the FSR compute passes, not in the frame.
