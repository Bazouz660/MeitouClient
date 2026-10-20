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
| Barriers between passes | **not needed** - Apple silicon tracks hazards itself (the engine's `Access` and `GpuDependency` say what must be ordered; the backend decides) |

A picture of the sky alone, without the world, has the raw linear colours: the post chain (the tonemap and the grade the Vulkan
path's picture goes through) is a later pass, so the two windows compare **structure** here — the gradient's shape, the sun's
place, the clouds — not the tone. The pixel diff against Vulkan becomes the check when both draw the same passes.

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

**The one binding table per stage.** Metal maps a resource to its MSL index by the binding
decoration alone, set ignored, so two sets with the same binding number would land on one
`[[buffer(n)]]` (the terrain's set 0 and set 2 both have a binding-0 block; the default block sits
at set 1). Every resource's binding therefore flattens to base + set × 8 + binding, in both places
that must agree: the SPIR-V before the translation (`SpirvPatch.RemapBindings`) and the runtime
binds (`MetalShaders.Flatten`, called by the command list's binds). The vertex stage's base is 15 —
the indices below it are the vertex buffers', at Vulkan's own slot numbers (the pipeline's
`[[stage_in]]` descriptor) — and the stage's top index, 30, belongs to the push constants (this
spirv-cross has no option for them, so `MetalShaders` moves the block from the `[[buffer(0)]]` it
assigns; the block is found by the engine's name for it, `Push`). Both stages' tables hold 31
entries, so the scheme fits the engine's sets (bindings within a set stay under 8).

## The bindless table

`BindlessTable` declares seven **combined** image samplers. Metal has no combined sampler, and
spirv-cross refuses the case outright ("Argument buffer runtime array currently not supported for
combined image sampler"). The way through is **separate textures and separate samplers**, which the
backend rewrites into the GLSL before compiling (the engine's compiler already rewrites sources).

The verified model (the MSL that spirv-cross makes, the pipeline reflection, and the host's
`--table` draw), on Metal 3:

- the table's set becomes a **tier-2 argument buffer** (`MslArgumentBuffers` +
  `MslArgumentBuffersTier` 2, in the **constant** address space); the shader takes it as
  `constant spvDescriptorSetN& [[buffer(n)]]` — n is the set's number, and the backend moves it to
  a fixed free index (28) so it cannot collide with the ordinary bindings, in both stages. The
  device-address-space variant (`CompilerMslSetArgumentBufferDeviceAddressSpace`) samples nothing
  that is put in by hand; the constant one does;
- the set's arrays are **sized** (the table's capacities): a sized array is a real member
  (`array<texture2d<float>, 2048>`) with a real offset in the buffer. spirv-cross's *unsized*
  arrays are a trap: they come out as one-element members aliased onto each other ("Overlapping
  binding"), which cannot hold distinct texture and sampler handles;
- **the members' offsets are sequential from 0, one entry (8 bytes) per element**; the `[[id]]`
  decorations in the MSL do not move them (the pipeline reflection reports `textures2D` at 0).
  The order is the members' declaration order: the texture arrays in kind order, then the
  sampler arrays. Gaps that are not declared (the shadow kinds today) close up;
- **per-entry samplers work**: the rewrite declares a parallel sampler array per kind
  (`sampler samplers2D[capacity]`), and every use constructs the pair with the same index —
  `sampler2D(textures2D[i], samplers2D[i])` — so GL's per-texture sampler state survives. A
  sampler only has a `gpuResourceID` valid in an argument buffer if its descriptor sets
  `SupportArgumentBuffers` (Metal's default is no), or sampling reads nothing;
- the runtime writes the handles into one shared buffer per frame slot: a texture's handle is
  `MTLTexture.gpuResourceID`, a sampler's `MTLSamplerState.gpuResourceID`, at the members'
  offsets (`MetalBindlessTable`). A resource read only through the argument buffer must also be
  told to the encoder (`useResource`), as `BindBindlessTable` does;
- **every** descriptor set of a program translated with argument buffers becomes one (spirv-cross's
  `MslArgumentBuffers` is global). A set's buffer members are `constant T*` pointers, so the
  runtime fills them with the buffers' **GPU addresses** (`MTLBuffer.gpuAddress` + offset; the CPU
  contents pointer is a different address on Apple silicon) at the flattened ids, and binds the
  set's buffer at the stage's set index (the vertex stage's `15 + set`, the fragment's `set`, the
  table at 28, the push constants at 30 — Metal's buffer table ends at 30, so the sets cannot be
  spread by 8);
- a stage drops the arrays it never reads, so the members' offsets differ per program (they are
  packed in declaration order): the runtime parses each stage's MSL struct and keeps one filled
  argument buffer per (layout, frame slot) (`MetalTableLayout`, `MetalBindlessTable.BufferFor`);
- a program whose fragment writes a second colour output (the terrain's GI albedo) needs the second
  attachment: the scene pass carries an `Rgba8ui` target (`MetalContext.Albedo`) and the pipeline
  declares it, or Metal drops the draws.

The rewrite (`MetalShaders.RewriteBindless` + `SpirvPatch.BindlessFetchImages`), verified by the
`--table` dump and `spirv-val` on both stages:

- capacities, in `BindlessKind` order: `[2048, 512, 128, 128, 128, 128, 64]` (index 3, shadow 2D,
  and index 6, the shadow array, are not declared today: their capacities do not count). The
  declared texture arrays' entries run 0 to 2943 — `textures2D`, `textures2DArray`,
  `texturesCube`, `utextures2D`, `itextures2D` — then `samplers2D`, `samplers2DArray`,
  `samplersCube` to 5631 (the buffer 45056 bytes);
- the float kinds are the separate arrays + sampler pairs above; the integer kinds have no
  separate type in GLSL, so they stay combined and sized (`usampler2D utextures2D[128]`) and the
  SPIR-V patch drops the sampler part: the variable's, the array's, each chain's and each load's
  types become the plain image's, and the stages' `OpImage` (100) extractions become
  `OpCopyObject` (83) of the already-plain image. `OpImage` = 100, `OpCopyObject` = 83, `OpPhi` =
  54 — and an instruction's word count includes the op word itself;
- the shadow kinds' declarations are dropped; a use throws `NotSupportedException` until the
  shadows step.

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
  the CPU), `MetalSky` (the spike's hand-driven sky), `MetalDisplay` (the window, the layer, the present).
- `src/Meitou.Metal.Host/` - the host: the sky in a window of its own.
- The game: `--renderer metal` (macOS only). The game boots without the Vulkan renderer, the
  simulation runs, and the sky is drawn by Metal. The sky is the game's **simple** sky (the
  colour model, no textures); the camera's own view and the clock's own sun.

The game reaches the Metal backend through a conditional project reference (the `MEITOU_METAL`
constant), so the Windows build compiles the game without any of it.

## The device behind the seam

`MetalContext` implements `IGpuContext` (docs/resource-layer.md), sized to the sky pass: every
operation a later pass needs first throws (`InvalidOperationException` naming it). With it:

- `MetalFrame` (the `IGpuFrame`): a slot ring of three (the layer's drawable depth) - one drawable,
  one command buffer and one constants ring per slot (a shared `MTLBuffer`, the CPU bump-allocates
  slices; unified memory, so no staging split for the constants).
- `MetalCommandList` (the `IGpuCommandList`): renderings (one colour + one depth attachment), the
  dynamic draw state (Metal's encoder state maps one to one: viewport, scissor, cull, winding,
  depth test/write/compare, depth bias, fill), `BindPipeline`, `BindProgramResources` and
  `BindSets`, labels, `Draw`, mip generation. **The stages have their own binding tables**: a
  set's slots bind straight onto the encoder at the SPIR-V binding numbers (the MSL is compiled
  with `MslEnableDecorationBinding`), so no argument buffer exists yet and none is needed - a
  texture at [[texture(n)]], its sampler at [[sampler(n)]], a block at [[buffer(n)]] per stage.
- The services: `MetalShaders` (the engine's compiler, fragment bindings at 0; the MSL per stage),
  `MetalPipelines` (an `MTLRenderPipelineState` per description; depth clip mode is the *encoder's*
  on Metal, set when the pipeline binds), `MetalSamplers` (Metal's sampler has no LOD bias; the
  description's is applied at bind, as clamps), `MetalUploads` (a batch is its own command buffer:
  blit copies from a shared staging ring, committed in order before the frame's own).
- Resources: textures (`MTLTextureDescriptor`, shared storage - Apple silicon's unified memory is
  the device-local memory), views (`NewTextureView`), buffers (shared `MTLBuffer`s), persistent
  sets (the layout and bindings recorded; the command list binds them out with dynamic offsets).
- The frame loop and the present are the context's (as `GpuContext.BeginFrame`/`EndFrame` are the
  Vulkan display's): the renderers draw into an offscreen Rgba8 backbuffer (the engine's format, as
  `VulkanPresenter` has), and the present is a **flipped copy into the drawable** - Metal's blit
  can do neither the flip nor the format, so a five-line MSL pass samples the backbuffer. The
  host's pass model (`BeginHostPass`) mirrors `GpuContext`'s.
- The frame's timestamps are the seam's `IGpuTimestamps` now (the Vulkan `QueryArena` implements
  it); Metal's hands out slots and reads nothing - Metal's GPU counters are a sampling-point model
  of their own (MTLCounterSampleBuffer, later if wanted).
- The backend constructs the engine's handles, so `Meitou.Rendering` grants `InternalsVisibleTo`
  `Meitou.Metal` (as it does to the viewer, the game and the tests).

What the sky-sized scope does not have yet (each member throws): compute, the bindless table,
indirect draws, dispatches, buffer/texture copies, in-pass clears, swizzled views, secondary
command lists. The vertex vocabulary is in: vertex and index buffers, indexed draws and push
constants (the flatten above), with the quad testbed in the host (`--tri`) and the same per-stage
binding scheme for the native model (`MetalShaders.Native`).

The host's `--shot out.bmp` reads the backbuffer back (a blit into a host buffer; a texture is not
CPU-mappable even in shared storage - `MetalContext.Readback`) and writes a BMP, so the sky's
picture is checkable without a screen shot: night is near black, dawn warm, noon bright.

## What the passes state

The renderers above `Gpu/` describe a draw in the engine's GL vocabulary, not in Vulkan's words
(docs/renderer-native.md "Synchronization and shaders"):

| What | The vocabulary | Vulkan's words it replaced |
|---|---|---|
| The draw state | `DrawState`, `BlendState` | `CullModeFlags`, `FrontFace`, `CompareOp`, `BlendFactor`, `BlendOp`, `ColorComponentFlags`, `PolygonMode` |
| The geometry | `PrimitiveType`, `VertexInput` | `PrimitiveTopology` |
| The targets | `AttachmentFormats`, `PixelViewport`, `PixelRect` | `Format`, `Viewport`, `Rect2D` |
| The shaders | `ShaderProgram`, `GraphicsPipelineDesc`, `ShaderReflection` | (already neutral) |

`GlConventions` maps the vocabulary to Vulkan (the front face flips, GL's 24-bit depth is a
32-bit float, the sRGB and the integer formats are named), and a second backend maps it to
itself. The passes that still speak Vulkan are the ones that touch the resources themselves:
the descriptor sets, the images and the buffers.

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
- The **bindings and the resources**: the passes now hold the engine's own handles (the samplers,
  the views, the textures, the buffers, the sets), state a draw's resources as a flat binding list,
  and record into an `IGpuCommandList` (the commands in the engine's words). What is left in the
  passes is the GI's acceleration structure and the vendor upscalers' command buffers, both
  Vulkan-only on purpose.

## The road to a Metal frame
Each step ends with something visible; the order is the cheap one first.

| Step | Work | What it gives |
|---|---|---|
| Done | The sky on Metal, in the game (`--renderer metal`) | the strategy: the shaders, the uniforms, the present |
| Done | The state and the formats in the GL vocabulary | the passes state what they want in neutral words |
| Done | The handles (the samplers, the views, the textures, the buffers, the sets) and the binding list | the renderers hold no Vulkan object |
| Done | The synchronization in the engine's words (the accesses and the dependencies) | the backend decides the barriers |
| Done | The command list as the engine's interface (`IGpuCommandList`) | the renderers state commands, not Vulkan's |
| Done | The context's interface (the resources and the services) | a backend can be another one |
| Done | The frame's interface (`IGpuFrame`), the pipelines (`GpuPipeline`) and the programs (`GpuProgram`: the index and the engine's compile result — sources, SPIR-V, reflection, sets) as the engine's handles | the frame, the pipelines and the programs are backend-neutral; the Vulkan objects live in the backend's tables (the device's, the pipeline library's, the shader library's) |
| Done | The sky path on the seam: the recording model (`BeginGuest`/`BeginNative`/`Interleave`), the device facts (`IsIntegrated`, `UniformBufferAlign`), the services behind interfaces (`IGpuShaders`/`IGpuPipelines`/`IGpuSamplers`/`IGpuUploads`), the frame's constants ring and timestamps (`WriteConstants`, `Timestamps`), the persistent sets (`CreateSet`) — and `SkyRenderer` with its helpers and the legacy program's flush holding no Vulkan type | the sky pass runs on `IGpuContext` alone; a second backend implements the seam and the sky draws |
| Done | The Metal device: `MetalContext`, `MetalFrame`, `MetalCommandList` and the four services behind the seam ([resource-layer](resource-layer.md)), sized to the sky pass (everything else throws); the offscreen backbuffer and the flipped present pass; the host's `--shot` readback | a Metal device that can run any pass whose vocabulary the command list has; the sky runs on it (the host's window, the frame loop, the real `SkyRenderer`) |
| Done | The game's `--renderer metal` on the device: the real `SkyRenderer` with the world's sky textures and switches, the scene's host pass as `SceneHost` opens it, the session's hour and camera; `--screenshot` (one picture, then out) | the sky in the game, verified: the gradient follows the camera and the hour, the clouds and stars are the world's own; the tone is raw linear until the post chain's step |
| Done | The vertex vocabulary: vertex and index buffers, indexed draws, push constants; the stages' one binding table flattened (base + set × 8 + binding, the vertex buffers keep Vulkan's slots, the push constants the top index); the native-model programs (`MetalShaders.Native`) | the terrain's own draw vocabulary, verified by the host's quad (`--tri`: the four corners' colours and the centre's interpolation land exactly) |
| Done | The bindless table's MSL shape, verified: tier-2 argument buffers, sized arrays, per-entry sampler arrays | the model the backend's table runtime builds to (below) |
| Done | The flatten's kind refinement: a vertex module's buffers shift by the vertex base, its images and samplers flatten from 0 (`RemapBindings` classifies by the SPIR-V storage class, `Flatten` takes the kind) | the terrain's vertex-stage table samplers fit the stage's sampler table |
| Done | The bindless table's GLSL rewrite (`--table`'s five kinds through the real path): the float kinds become separate sized texture and sampler arrays, the integer kinds stay combined and sized with their samplers dropped in SPIR-V, the shadow kinds throw until the shadows step; verified: both stages' patched SPIR-V pass `spirv-val`, the argument buffer's members are `texture2d/texture2d_array/texturecube/texture2d<uint>/texture2d<int>` arrays then `sampler` arrays | the table's MSL shape at the capacities |
| Done | The table's runtime (`MetalBindlessTable`: the journal, a shared argument buffer per frame slot, the GPU resource ids, the sampler flag, `useResource`) and `BindBindlessTable` at buffer 28 | the terrain's textures can bind; verified by the host: red → blue across the quad, the update's swap visible |
| Doing | The terrain in the game: the Vulkan-free widening (`IGpuContext` through `TerrainRenderer`, `NativeFrame`, `TerrainTextures`), the programs, the textures, the pass (depth + GI-albedo attachment), the CDLOD draws at the camera. **Open:** the draws are recorded with a valid pipeline, buffers and a trivial shader, yet produce no fragments (depth and albedo both empty); the same program shape draws in the host, so the difference is still unexplained — see the note below | the world appears |
| Then | Compute and the post chain (the tone parity) | the picture is the game's, not raw |
| Then | The objects, the characters, the foliage, the shadows, the water, the UI | the whole frame, and `--renderer metal` becomes the macOS default |

### What kept the terrain's fragments away (fixed)

The patch draws were recorded but produced nothing. Metal's validation layer names the causes
(`MTL_DEBUG_LAYER=1`):

- **An argument buffer struct's members are laid out by declaration order, not by `[[id]]`.** The
  id names the binding (the flattened index), but the entry a member reads is its position in the
  struct. The table's layout already followed that rule; the descriptor sets did not — the runtime
  wrote set 0's view block at entry 19 (its id) while the shader read entry 0 (the frame block),
  so the view matrix was garbage and every patch vertex landed outside the clip volume. Sets are
  now filled per stage by the position parsed from the stage's MSL (`MetalSetLayout`), one
  argument buffer per stage and set, at the stage's set index.
- **Metal checks the pushed bytes against the MSL block's size.** The terrain's push block is 24
  bytes of members, 32 in the shader (std430 rounds the struct to 16), so `PushConstants` now
  pads to the 16-byte alignment before `SetVertexBytes`/`SetFragmentBytes`; a short push fails the
  draw's length check.
- A stage that does not read a set has no struct for it: the bind skips that stage rather than
  binding a buffer nothing reads.

The terrain draws now (patches, culling, the depth and GI-albedo targets) with its real material
and atmosphere (verified with the `--screenshot` comparison at the same camera and hour): the
layer arrays, the biome maps and the physical sky's atmosphere cubes are all sampled through the
bindless table. Three facts this step settled:

- **The terrain quadtree is in world coordinates.** The Metal host placed its camera at
  `(o.X ?? 0, o.Z ?? 0)` and looked at the world origin, not at `scene.Focus` where the region is
  loaded; it now uses the focus (or `--at`), as the Vulkan camera rig does.
- **The diffuse layer array is BC3 (DXT5), the normal array BC1.** `MetalConventions` mapped only
  BC1, so the diffuse array's allocation threw, `TerrainTextures` fell back to no array, and the
  terrain sampled the stand-in (black albedo — the picture was dark blue from the sky's ambient).
  DXT5 now maps to `BC3RGBA` and DXT3 to `BC2RGBA`, both as 4x4 blocks of 16 bytes.
- **The frame inputs are the Vulkan path's.** The host runs the weather scheduler
  (`WorldWeather`) and `WeatherSurfaces`, passes the sky `fogDistance + 3*max(eyeY, 0)`, sets
  `camera.ViewDistance` from the light's fog distance, `render.TerrainPixelScale` from the render
  height, and streams the fine heights and biome maps with `TerrainStreamer` (without it the
  overlay/colour windows stay at state 1 and the terrain shows only the ground colour).

What the Metal picture still lacks is later steps' work, not this one's: the post chain's tone
mapping and exposure (step 2), the shadows (step 4), the objects, foliage and water (steps 3, 5,
6) and the UI (step 8).

**Compute (step 2's backend half).** A compute program is translated like the native model's
(every set an argument buffer, push constants at <c>buffer(30)</c>) and the GLSL's
<code>local_size_x/y/z</code> is its threadgroup; <code>Dispatch(x, y, z)</code> therefore means
threadgroups, as Vulkan's <code>vkCmdDispatch</code> does. The pipeline is an
<code>MTLComputePipelineState</code>. A storage image's argument-buffer member is its texture's
<code>gpuResourceID</code> and the encoder marks it <code>Read|Write</code>. <code>Order</code>
ending the compute and blit encoders is the seam: a command buffer orders its encoders and Apple
silicon tracks the hazards inside one. <code>FillBuffer</code> clears only (Metal fills with one
byte; no caller asks for another value). Verified with an 8x8 dispatch into an RGBA8UI storage
image through set 0's argument buffer, read back as (2, 3, 99, 255).

**Shadows (step 4's first slice).** The seam now carries the host-pass handoff and the device capabilities
the shadow pass needs (`BeginHostPass`/`SetPassViewport`/`EndHostPass`, `DepthClamp`, `RecordsSecondaries`), so
`ShadowPass` and `TerrainShadowMap` run on `IGpuContext`; Metal opens no secondaries and clamps depth. The Metal
host draws the terrain's and the objects' depth into the cascades before the scene, and the receivers sample the
atlas; foliage is not a caster yet. Two backend facts this surfaced:

- **A new Metal render encoder carries no pipeline state**, so the redundancy filter must be reset in
  `BeginRendering`: the multi-pass shadow atlas otherwise drew a later pass without `setRenderPipelineState`.
- **A depth-only pipeline has no colour attachment at slot 0**; the pipeline builder skips it when the format is
  `None`.

The noise texture uploads through an upload batch (the seam's `IGpuUploads` has no direct `Write`).

**Objects and buildings (step 3).** `WorldObjectRenderer` and its caches (`WorldTextureCache`, `ObjectMeshCache`) take
`IGpuContext`. The seam gained what their uploads and draws use: `HasRayQuery`, `EnsureFrame` and `StandIn2D` on the
context, batches from `IGpuUploads.Begin` (with `GenerateMips` on their command list) for the mesh and texture
slabs, `IGpuFrame.WriteConstants` for the per-instance matrices, and `GpuProgram.Info` for the vertex reflection.
The Metal host makes the renderer with the Vulkan distances and LOD bias, settles it on the first open frame (the
still's meshes and textures all resident), calls `SetView`/`Update` per frame, draws it after the terrain, and
draws its depth into the cascades. Metal vertex formats `Rgb32f`, `Rgba16f` and `Rgba8ui` were added for the
building meshes. The buildings stand where Vulkan draws them; characters, foliage and the UI remain (steps 5, 7
and 8).

**Foliage, grass and rocks (step 5's first slice).** `FoliageRenderer`, `FoliageGrassGpu`, `NativeProg` and
`BufferArena` take `IGpuContext`; the seam gained `FramesInFlight`, `VideoMemoryBudget`, `DeferDelete` and `Finish`.
The Vulkan-only pieces stay out of the Metal path for now: `FoliageGpuCull` (the compute cull), `FoliageGrassGpu`'s
kernels and `ImpostorBaker`/`ImpostorDraw` (the billboard bakes). The host runs the CPU cull (A1), the CPU grass
path and the terrain-shader rocks, settles them on the first open frame, updates and draws them after the objects,
and adds them as cascade casters. Two backend facts this surfaced:

- **Metal rejects GL's stride-0 constant vertex attribute.** A disabled attribute (the foliage shaders' bone
  inputs) reads the generic value from a repeating 16-byte-step dummy array instead, with a stride of 16: every
  vertex fetches the same (0, 0, 0, 1). `IGpuContext.ConstantVertexAttributes`/`DummyVertexArray` carry the
  distinction; Vulkan keeps the stride-0 binding.
- **A non-indexed draw may start at another instance.** The grass draws a blade range with `firstInstance` (the
  first blade in the arena); `Draw` passes it as Metal's `baseInstance`, and `firstVertex` is supported too.

**The far billboards (impostors).** `ImpostorBaker`, `ImpostorTextures` and `ImpostorDraw` take `IGpuContext`; the
seam gained `IGpuFrame.PreFrame` (Metal's frame list, recorded before its passes), `IGpuReadback` with
`CreateReadback`/`FrameCompleted`, and `CopyTextureToBuffer`. Metal implements the bake's halving blit as a fullscreen
sampling pass (Metal's blit cannot scale), copies the row into a shared readback buffer, and reports a frame completed
when the drawable ring has turned. Two Metal fixes the bake surfaced:

- **Argument buffers are padded to 16 bytes.** A one-member set was allocated 8 bytes and failed Metal's validation;
  the set size is rounded up.
- **A sub-rectangle upload still needs the destination level's full row stride.** The terrain's overlay and colour-map
  tiles are repacked with that stride and their x offset in the upload batch; BC5's 16-byte block was also missing
  from the texel-size table.

The GPU culls remain the step's rest (Metal takes the CPU cull and the CPU grass path).

**The composite (the first slice of the chain).** The Metal scene draws into an RGBA16F target and
`PostComposite` runs the game's own tone-map shader (`PostProcessShaders.Composite`, through
`LegacyProgram`) into the backbuffer. The fullscreen pass flips `vUv.y` on Metal: Vulkan's negative
viewport stores the scene flipped into its targets, Metal's do not (the present flips the picture
instead). The exposure is the game's own too: the luminance at 256² (16 taps), its nine-level mip
chain, and the 1×1 RG32F adaptation against the previous frame inside the sky's band
(`uAuto` 1; a still picture uses the instant blend). The upscalers/TAA and `PostProcess`'s widening
to the seam remain step 2's rest. At the same view the picture's mean channels are within a few
percent of the Vulkan screenshot's with GI and shadows switched off (98/78/72 against 96/71/67);
the large per-pixel differences left are the missing shadows and GI (steps 4 and the game loop).

### The plan for the remaining steps (kept exact, for resuming cold)

**Step D — verify the table in the host** (`--table`): the quad samples `textures2D[uIndex]` and
`textures2D[uIndex + 1]`, mixed by u across the quad, with the other kinds' index 0 black dummies
(the rewrite's five kinds run). Verified shots: bottom-left red, top-right blue, centre
(127, 0, 128); after `Update` swaps the two entries at frame 3, the picture swaps. The runtime is
`MetalBindlessTable` (the Vulkan table's journal contract, one shared buffer per frame slot, ids by
`TableTextureId`/`TableSamplerId`).

**Step E — the terrain in the game.**

1. The Vulkan-free widening (as `SkyRenderer` got in step 2): `TerrainRenderer(GpuContext` →
   `IGpuContext`, same for `NativeFrame` and whatever `TerrainProgram` wraps;
   `DeviceBuffer.Create` gets an `IGpuContext` path (it already wraps `ctx.CreateBuffer`);
   `gpu.Device.Limits.MinUniformBufferOffsetAlignment` → the seam's device fact
   (`UniformBufferAlign` exists on `IGpuContext`). Keep the Vulkan parity byte-for-byte: the
   widening must not change the Vulkan path's code paths, only their parameter types.
2. The game's Metal path (`GameHost.Metal.cs`): give the host pass a **depth attachment** (a
   per-slot depth texture at the backbuffer's size — the context owns it, resized with the
   backbuffer); the pass's state becomes `DrawState.Scene` (depth tested); the sky draws, then
   `terrain.Draw(viewProjection, eye, frustum, options, light)` with the light from
   `sky.Prepare`'s second return and a minimal `WorldRenderOptions`.
3. The terrain's boot in the Metal path: `TerrainTextures.Prepare(install, db)` (the CPU bakes),
   the heights (already in the ctor), the material's texture setup (find where the layers'
   arrays are created and bound — `TerrainRenderer`'s texture part), the constants set (set 2).
4. The patch draws (CDLOD) are the scope; the TERRAIN-mode meshes' indirect draws
   (`DrawIndexedIndirect`) come after — implement them then, or defer the meshes with a line in
   the doc.
5. Verification: the two-window comparison (the terrain's relief and layer colours, same
   `--time`/`--pitch`/`--distance`); the recipes all stay green: build 0/0, tests 424 pass /
   1 known fail, the Vulkan screenshot 0 differing pixels (warm runs; a cold first run may show
   the ~40 ±1 TAA edge pixels), the Metal game's sky shot unchanged.

**Step F — the remaining steps, in dependency order.** The terrain draws; what follows is breadth, not
research. Each step ends with the same gates: build 0/0, tests 424 pass / 1 known fail, the Vulkan
screenshot unchanged, and a Metal shot compared with the Vulkan one at the same view.

1. **The frame's inputs (small).** Give the Metal path the real `WorldFrame`/`FrameGlobals`: the
   atmosphere LUTs and textures, the sun and light, fog, and the camera's world framing. Today the
   terrain's frame textures are stand-ins, so it is dark. Ends when the terrain's colours and relief
   match Vulkan's at the same hour and camera.
2. **Compute (large).** `MTLComputePipelineState` and `MTLComputeCommandEncoder`, dispatches, compute
   sets as argument buffers, storage images/textures, then the post chain's passes: tone mapping,
   TAA, fog volumes, the HiZ pyramid, the GPU culls. `MetalCommandList.Dispatch` and the compute
   pipeline path throw today; they become the first work. Ends when the offscreen picture matches
   Vulkan's before the present.
3. **Indirect draws and the objects (medium).** `DrawIndexedIndirect(Count)` and
   `DrawIndirectCount`, buffer fills/copies, and the conversion between Vulkan's indirect argument
   layout and Metal's. The material and program path is already in place. Ends with the buildings
   and objects standing where Vulkan draws them.
4. **Shadows (medium).** The depth-only passes and the cascades; set 0's receiver and caster blocks
   already exist and hold zeros. Ends with the terrain's and objects' shadows.
5. **Foliage, grass, impostors (medium-large).** The GPU culls of step 2 feed the indirect draws of
   step 3; add the card masks, grass, LOD and the impostor bakes. Ends with the vegetation matching
   Vulkan's at the same view.
6. **Water (medium).** The reflection and refraction passes and the surface's compute, reusing steps
   2 and 3.
7. **Characters and skinning (medium).** The skinning buffers and the character draw list through the
   seam; the animation data already uses storage buffers.
8. **UI (small, architecturally separate).** The UI is immediate-mode Vulkan into the backbuffer
   image; it needs a backend-neutral twin (or a Metal path of its own) before the game's HUD and the
   panels exist on Metal.
9. **The physical sky (small).** `SkyRenderer`'s uniforms come from a Vulkan `GpuContext`; the
   atmosphere model (CPU) must become something both backends build their uniforms from.
10. **The full game loop (broad, mechanical).** The Metal game path is a minimal frame (sky +
    terrain). A full game widens the real `GameHost` render flow — objects, population, overlays,
    UI — to `IGpuContext`. Much of it is type-changing; the surface is large.
11. **Parity and the default.** The window comparison, tone and timing, resize and HDR, then
    `--renderer metal` becomes the macOS default.

Two names collide with Silk.NET.Vulkan's and are qualified where the file still imports it:
`InternalFormat` and `PrimitiveType`. Both collisions go away when the file sheds its Vulkan
imports.

The parity check for a step that changes the Vulkan path: the game's offscreen picture
(`--screenshot`, the same view) is byte for byte the same before and after. The temporal path
makes that check noisy at times: two runs of the *same* build can differ on about forty edge
pixels by one level (the TAA's history), so a step's picture is compared against several runs of
the baseline, not one.

## Where the port stands (resume here)

**Drawing on Metal today:** the sky, weather and atmosphere; the terrain (heights, biomes, the streamer); the
objects and buildings; the sun's shadows (terrain, objects and foliage casters); foliage meshes, grass and the
terrain-shader rocks (the CPU cull and CPU grass, not the compute ones); the impostor bakes and far billboards;
the scene's HDR target, the game's tone map and its auto exposure. `GameHost.Metal.cs` is still a still-frame
loop (sky + terrain + objects + foliage + composite), not the interactive game.

**The next steps, in order.** Each ends with the gates below (build 0/0 and a Metal shot under
`MTL_DEBUG_LAYER=1`; tests and a Vulkan comparison only when the step changed shared code).

### 1. The Metal operations the rest needs — done, with two limits

**Done:** `DrawIndexedIndirect` (a loop of `drawIndexedPrimitives…indirectBuffer`; Metal has no multi-draw),
`Resolve` (a render pass whose colour attachment is the MSAA source and whose `resolveTexture` is the target),
`CopyBufferToImage`, `Blit`/`BlitLevel` (a blit when the levels match, a sampling pass over level views when they
scale), swizzled views (Metal texture views; every texture carries `PixelFormatView`), partial / in-pass
`Clear`/`ClearDepth` (a scissored fullscreen triangle), and `PushConstants` offsets (the stage's block kept and the
named range set in it). MSAA textures are `Type2DMultisample`; a clear pipeline names every attachment of the pass,
including the GI mask at colour slot 1. The Metal.Host `--table` and the game's scene pass ran all of the above under
`MTL_DEBUG_LAYER=1`.

**Limits:** `DrawIndexedIndirectCount`/`DrawIndirectCount` have no Metal count-buffer command. Every slot is issued;
the arguments at and after the count must be zero (a zero index / vertex count draws nothing). The GPU grass's order
kernel must zero its draw list when that path comes to Metal. Timestamps are still no-ops.

Keep `RecordsSecondaries` false: Metal records inline into one list.

### 2. The post chain's rest

`PostComposite` (exposure + tone map) was the only post pass on Metal; `PostProcess` is still Vulkan-typed. Port, in
this order:

1. **TAA and the upscaler.** The **TAA slice is done** in `PostTaa` (seam-only): the game's velocity and TAA shaders,
   a render-size RGBA16F motion target, two RGBA16F histories, the same 8-phase `Jitter` on the scene projection and the
   pass, `uBlend` 0.1 and a reset on the first frame/cut. The Metal host now renders the scene jittered and composites
   the TAA output; the scene depth carries `Sampled`. Still to do: the render-scale upscaler and MetalFX behind
   `IUpscaler` (the FSR path stays Vulkan), the sun-motion term, moving geometry's motion (grass/characters) and
   widening the rest of `PostProcess` (SSAO, particles, FXAA, heat haze, GI resolve).
2. **Fog volumes and light shafts** (`FogVolumes`, `PostProcess.FogRate`, `PostProcess.Shafts`).
3. **The HiZ pyramid and the GPU culls** (`HizPyramid`, `FoliageGpuCull`, `FoliageGrassGpu`): widen them to
   `IGpuContext`, replace `HasPushDescriptor` support checks with argument buffers, give `FrameScratch` a Metal
   variant (a per-frame bump allocator over shared buffers), and drive them with the indirect draws of step 1.
   This is the performance gap only: the CPU cull and CPU grass already draw the same picture.

### 3. Water (plan step 6)

`WaterRenderer`, `ReflectionPass`, `ShoreField`, `OceanFft`, `WaterWaves`, `WaterFoam` and `RiverFlowBake` are
Vulkan-typed. The reflection pass renders the mirrored scene (terrain + objects + foliage + sky) into its own
target with `ReflectedProgram`, then the surface and the shore/foam passes read it. Depends on resolves, blits and
the scene draws already on the seam. Ends when the coast/river views match Vulkan's water.

### 4. Characters and skinning (plan step 7)

`CharacterRenderer` (internal to `Meitou.Rendering`), `CrowdHarness`, `CharacterContent`, `CharacterMorphs` and
the draw list are Vulkan-typed; the animation data already uses storage buffers. Widen the renderer, wire the
skinning buffers and the character draw list, add `Characters.DrawDepth` to the cascades (the call site exists in
`WorldFrame.DrawShadows`), and enable population in the Metal host. Ends with the town's crowd and the player
character rendering and casting shadows.

### 5. The UI (plan step 8)

`DebugOverlay` and the player interface are immediate-mode Vulkan into the backbuffer. Add a backend-neutral
painter (quads, text, textures) with a Metal implementation, or a Metal `DebugOverlay`; then the HUD, panels,
item icons and portraits (`PlayerInterface`, `PlayerUi`, `--ui` showcase). Architecturally separate from the
scene path; a small renderer of its own is fine.

### 6. The remaining effects

`VolumetricCloudRenderer` (the Meitou clouds), `ParticleRenderer` (weather), `WorldLamps` (the lamps and room
lights), `HoverOutlineRenderer`, `MoveMarkerRenderer`, `NavmeshOverlayRenderer` and `InteriorMaskRenderer` (the
object/foliage interior-cut hooks exist but nothing renders the mask). The faithful sky path the parity shots use
already works. Decide the default between the faithful and Meitou look.

### 7. The full game loop (plan step 10)

The interactive host: input and camera controls (`MetalDisplay` events), the title screen, resize, HDR/EDR and
the present details, warm-up frames, `--screenshot`/`--ui` parity, and the real `GameHost` render flow
(`WorldFrame`, `SceneHost`) widened to `IGpuContext` instead of the bespoke Metal loop.

### 8. Parity and the default (plan step 11)

Window comparison at several views, tone and timing, memory budgets, then `--renderer metal` becomes the macOS
default.

### Decisions still open

- **GI**: the probe GI needs ray queries; Metal's ray tracing is a different model. The parity shots use `--no-gi`
  and the Vulkan path on this Mac does trace GI. Either port `GiScene`/`GiProbes` to Metal ray queries (a project
  of its own) or accept the no-GI look as the macOS baseline.
- **Volumetric clouds and fog**: parity shots use `--faithful clouds,cloudshadows`; the Meitou cloud/fog path is
  step 6.
- **Upscaler**: MetalFX vs FSR, and whether TAA alone is the first target.

### Resume notes (facts already established)

- Binding: one table per stage at `(kind base) + set*8 + binding`; the bindless table at `buffer(28)` both stages;
  push at `(30)`. Argument-buffer structs lay members out by declaration; **the buffer is padded to 16 bytes**.
- Metal facts found on the way: a new render encoder has no pipeline state (reset the filter in `BeginRendering`);
  a depth-only pipeline skips colour slot 0; a sub-rectangle upload needs the destination level's full row stride
  (the batch repacks); disabled GL attributes use a repeating 16-byte dummy array (stride 16), not stride 0;
  non-indexed draws take `firstVertex`/`baseInstance`; BC5 is 16 bytes a block.
- MSAA: `TextureDesc.Samples > 1` is `Type2DMultisample` (`Type2DMultisampleArray` for arrays), one mip level;
  `Resolve` is a render pass with the source as its colour attachment, `ResolveTexture` the target, and
  `StoreAction = MultisampleResolve`. A clear pipeline must name every attachment of the pass (the GI mask is slot 1).
- Texture views: the textures carry `PixelFormatView`; a non-identity `GpuSwizzle` is
  `newTextureViewWithPixelFormat:textureType:levels:slices:swizzle:` (the BC4 masks' `RrrOne`). Metal's blit cannot
  scale: `BlitLevel` copies equal-sized levels and renders a sampling pass over level views when they differ.
- Partial clears: a fullscreen triangle under the rect's scissor (colour and/or depth outputs), then the previous
  viewport/scissor are restored and the pipeline filter reset. Sets the depth-stencil state for the clear.
- A Metal `drawPrimitives…indirectBuffer` is one command; `DrawIndexedIndirect` loops, and the count variants issue
  every `maxCount` slot (the slots at/after the count must hold zero counts).
- Metal screenshots are not byte-comparable run to run: the screenshot clock advances by the real frame time, so the
  sun and clouds differ. Compare regions / means, or freeze the clock for a pixel comparison.
- TAA (Metal-host slice): `PostTaa` runs the game's `UpscaleShaders.Velocity` (fragment, into RGBA16F) and
  `UpscaleShaders.Taa` (into two RGBA16F histories); the scene projection is jittered with `Jitter.Apply` and
  `PostTaa.Offset` (8 phases at 1×), the depth target is `DepthTarget | Sampled`. TAA resets on the first frame, a
  resize and a camera cut (>5000 units). The velocity pass's far-slice inputs are disabled; the sun-motion term is
  zero (the sky's sun compensation comes with the full chain).
- `IGpuContext` facts: `FramesInFlight`, `VideoMemoryBudget`, `DeferDelete`, `Finish`, `CreateReadback` +
  `FrameCompleted`, `ConstantVertexAttributes`/`DummyVertexArray`, `EnsureFrame`, `HasRayQuery`.
- `MTL_DEBUG_LAYER=1` catches the above immediately; keep it on for every new pass.

### Gates

Vulkan is the untouched reference: do not run the Vulkan path or the full test suite on Metal-only work. What
runs when:

**Every commit (cheap, Metal-only):**

```
dotnet build src/Meitou.Game -c Debug                                  # 0 warnings, 0 errors
DYLD_LIBRARY_PATH=/opt/homebrew/lib MTL_DEBUG_LAYER=1 dotnet run --project src/Meitou.Game --no-build -c Debug -- \
    --renderer metal --screenshot /tmp/x.png --no-population --time 8 --distance 2000 --pitch 45 \
    --faithful clouds,cloudshadows --no-gi
```

The host table (`--table`; BL red, centre `(127,0,128)`, TR blue, a `.swap.bmp` appears) when the change touches
the seam, the binding tables or the Metal host.

**When a step edits shared code** — a renderer widened to `IGpuContext`, a file Vulkan still compiles, the seam
itself — run the suite and a Vulkan comparison, because the change can alter the Vulkan path even when it is meant
to be neutral:

```
DYLD_LIBRARY_PATH=/opt/homebrew/lib dotnet test --filter "Category!=Slow&Category!=Bench"   # 424 / 1 known arm64
```

Parity: render the same view on the Vulkan path before and after (two baseline runs, since the temporal path is
noisy), compare differing samples and the worst channel (the noise floor is roughly 350k–550k samples, max
~38–40), never bytes. The screenshot is taken at frame 40; `--shadow-range 0` isolates the non-shadow path.

**End of a plan step / before reporting:** the Metal shot at the step's view, plus a Vulkan comparison only if
that step's picture is meant to match the Vulkan one (water, characters, UI, post). Metal-only steps (indirect
plumbing, profiling, docs) need no Vulkan run.

## Constraints

- **GI**: the probe GI needs ray queries (`VK_KHR_ray_query`), which have no Metal
  translation; Metal's ray tracing is a different programming model. The engine already runs
  without GI where the device has no ray queries (macOS today), so a Metal path starts with GI
  off, like the Vulkan path on macOS.
- **Upscaler**: MetalFX is in SharpMetal (`MTLFXTemporalDenoisedScaler`) and would sit behind
  `IUpscaler`; the FSR path stays for Vulkan.

## Open questions

- Metal's GPU timestamps (MTLCounterSampleBuffer), when the pass timings matter on Metal.

(Resolved in the plan: per-entry sampler state — sized sampler arrays in the argument buffer.
Resolved: the vertex-stage binding table — the kind-aware flatten, in the road table.)
