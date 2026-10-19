# Metal backend

A second native path for the renderer, next to Vulkan. Status: **the sky runs in the game with
Metal** (`--renderer metal`, macOS): the game's own GLSL, the engine's own compiler, the
engine's own uniform block. The world is not drawn yet.

Bindings: `SharpMetal 1.1.0` (IsaacMarovitz; Metal, MetalFX, QuartzCore, the Objective-C
runtime). `Silk.NET.GLFW` has no `glfwGetCocoaWindow`, so the `NSWindow*` comes from the same
brew `libglfw.3.dylib` the Vulkan path loads.

## What works

| Mechanism | Result |
|---|---|
| Device, `CAMetalLayer` on the GLFW window, clear and present | works (in the game) |
| The engine's GLSL -> SPIR-V (shaderc) -> MSL (spirv-cross) -> runtime compile | works |
| The engine's uniform block (std140), uploaded unchanged | works |
| Storage buffer (std430), dynamic indexing | works |
| Runtime-sized texture **and** sampler tables, dynamically indexed (tier-2 argument buffer) | works |
| Compute pass writing an rgba16f storage image, sampled back | works |
| Barriers between passes | **not needed** - Apple silicon tracks hazards itself |

## The shader chain

The engine's own `GlslProgramCompiler` produces the SPIR-V; `Silk.NET.SPIRV.Cross` 2.23.0 (with
its `.Native` package) turns it into MSL **in process** - no subprocess, no `spirv-cross` CLI.
The options that matter:

| Option | Value | Why |
|---|---|---|
| `MslVersion` | 30000 | Metal 3 (macOS 13+); tier-2 argument buffers with runtime-sized arrays need it |
| `MslPlatform` | `Macos` | |
| `MslEnableDecorationBinding` | on | the MSL resource indices equal the SPIR-V binding numbers, so a Metal backend mirrors the Vulkan binding scheme instead of carrying a per-shader mapping table |

Three facts about the translation:

- **The engine's legacy shader path goes to MSL as it is.** spirv-cross splits a combined image
  sampler into a `texture2d<float>` and a `sampler` at the same index - exactly Metal's model -
  so no GLSL rewrite is needed for the fixed samplers. Only the runtime-sized bindless table
  needs the rewrite (below).
- **The uniform block keeps its std140 layout.** The offsets the engine's `ShaderReflection`
  reports (`uInverseViewProjection` @0, `uSkySun` @64, ...) are the MSL struct's offsets, so the
  bytes the engine's `GlUniforms.Scatter` writes are the bytes a Metal constant buffer takes.
- **One library per stage.** spirv-cross names both stages' entry point `main0` and their
  interface structs `main0_out`, so two stages in one source collide: the vertex function comes
  from one library, the fragment function from the other.

On Metal the stages have their own binding tables, so the Vulkan scheme (the fragment stage's
bindings at 32, to keep them apart from the vertex stage's) is not needed: the fragment stage's
bindings start at 0.

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
- The `CAMetalLayer`'s drawable size follows the view's bounds, and the view has no bounds while
  the window is being created: attach the layer and let the window lay out, or set the size from
  the window's size times the scale factor. Without it the drawable is 1x1 and the window shows
  one magnified pixel.

## The backend so far

- `src/Meitou.Metal/` - the backend (macOS only, outside the solution so the Windows build never
  sees it): `MslTranslator` (SPIR-V -> MSL), `MetalUniformBlock` (the engine's uniform block on
  the CPU), `MetalSky` (the sky), `MetalDisplay` (the window, the layer, the present).
- `src/Meitou.Metal.Host/` - the host: the sky in a window of its own.
- The game: `--renderer metal` (macOS only). The game boots without the Vulkan renderer, the
  simulation runs, and the sky is drawn by Metal. The sky is the game's **simple** sky (the
  colour model, no textures); the camera's own view and the clock's own sun.

The game reaches the Metal backend through a conditional project reference (the `MEITOU_METAL`
constant), so the Windows build compiles the game without any of it.

## What the second backend still needs

- The **physical** sky: the game's DDS textures (the starfield, the planets, the clouds) as
  Metal textures, and the atmosphere's own uniforms.
- The rest of the passes: the terrain, the objects, the post chain.
- A seam for the passes above `Gpu/`. `Gpu/` has no backend abstraction: two abstract classes
  (`Uniform`, `RecordJob`), and eight types carry Vulkan handles (`Image`, `ImageView`,
  `Sampler`, `Buffer`, `GraphicsPipeline`, `ComputePipeline`, `CommandBuffer`, the frame slot).
  The neutral descriptions (`GraphicsPipelineDesc`, `PipelineLayout`, `ShaderProgram`) are
  already backend-neutral, and the passes above `Gpu/` only use the neutral types: the sky pass
  needs `Pipelines`, `Globals`, `Samplers`, `Uploads`, `LodBias` and eight `CommandList` calls
  (`BindPipeline`, `Draw`, `SetViewport`, `SetScissor`, `SetRaster`, `SetDepth`,
  `SetDepthBias`).
- The **formats**: the passes still state theirs as Vulkan's `Format` (166 references). The
  engine's GL vocabulary has the rest (`InternalFormat` with `GlConventions.VkFormat`), so the
  swap is the same shape as the state vocabulary's.

## Constraints

- **GI**: the probe GI needs ray queries (`VK_KHR_ray_query`), which have no Metal
  translation; Metal's ray tracing is a different programming model. The engine already runs
  without GI where the device has no ray queries (macOS today), so a Metal path starts with GI
  off, like the Vulkan path on macOS.
- **Upscaler**: MetalFX is in SharpMetal (`MTLFXTemporalDenoisedScaler`) and would sit behind
  `IUpscaler`; the FSR path stays for Vulkan.

## Open questions

- The shape of the seam: backend-neutral handle types, a backend interface, or a parallel Metal
  implementation of the neutral API.
- Per-entry sampler state: the table's entries carry their own sampler, so the table needs a
  sampler array as well as a texture array.
- Whether a Metal path is worth a second renderer at all: the macOS Vulkan path works, and the
  measured cost of MoltenVK is concentrated in the FSR compute passes, not in the frame.
