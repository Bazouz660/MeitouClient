# Meitou Metal spike

An experiment: how far can a **native Metal** path get, using the engine's own idioms?

This is not a port and not part of the solution. It is a probe of the risky parts of a possible
Metal backend, on a local branch (`metal-spike`), so that the answer to "could we rewrite the
renderer against Metal?" is measured rather than guessed.

The backend that grew out of it is `src/Meitou.Metal/`; its state and the findings are in
[docs/metal-backend.md](../../docs/metal-backend.md).

## What is proved so far

| Stage | What it shows | Status |
|---|---|---|
| 0 | C# -> SharpMetal -> `CAMetalLayer` on the GLFW window: device, layer, clear, present | works |
| 1 | The shader chain: GLSL -> SPIR-V (glslang) -> MSL (spirv-cross) -> runtime compile, with a sampled texture | works |
| 2 | The engine's binding pattern: uniform block (std140), storage table (std430) indexed dynamically, sampled texture | works |
| 3 | A texture table whose layer is picked at runtime - the capability the engine's sampler arrays need | works |
| 4 | A compute pass writing an rgba16f storage image, sampled back by the fragment stage - the ocean FFT / Hi-Z shape | works |
| 5 | The bindless table itself: separate textures and separate samplers, both indexed at runtime, in a tier-2 argument buffer | works |

Findings worth keeping:

- **SharpMetal 1.1.0** (IsaacMarovitz, on NuGet) is the bindings to use: Metal, MetalFX,
  QuartzCore (`CAMetalLayer`) and the Objective-C runtime are all there. `haolange/SharpMetal`
  is a fork of it; `haolange/SharpGPU` is an RHI *above* Metal, the wrong layer for this.
- **Silk.NET.GLFW has no `glfwGetCocoaWindow`**, so the `NSWindow*` comes straight from the brew
  `libglfw.3.dylib` the engine already loads (`meitou-run.sh` points `DYLD_LIBRARY_PATH` at it).
- **`spirv-cross --msl-decoration-binding`** keeps the MSL resource indices equal to the SPIR-V
  binding numbers. Without it spirv-cross renumbers resources by its own traversal order (the
  storage buffer became `buffer(0)` although its binding was 2), which would force a per-shader
  mapping table. With it, a Metal backend can mirror the Vulkan binding scheme directly.
- spirv-cross names both stages' entry point `main0` and their interface structs `main0_out`, so
  two stages in one source collide. Each stage becomes its own library; Metal takes the vertex
  function from one and the fragment function from the other.
- The `CAMetalLayer` needs `setAutoresizingMask:` (width|height sizable) to fill the content view;
  its drawable size then follows the view's bounds times the scale factor, retina included.

## The bindless tables: the one that needed real digging

The engine declares its runtime-sized tables as **arrays of combined image samplers**
(`DescriptorType.CombinedImageSampler` with an `ArrayLength`). Metal has no such thing, and
spirv-cross walks the whole path to say so:

1. a runtime-sized descriptor array needs argument buffer tier 2 ->
   `--msl-argument-buffers --msl-argument-buffer-tier 1`
2. a runtime-sized argument buffer must live in the device address space ->
   `--msl-device-argument-buffer 0`
3. mutable descriptor aliasing needs Metal 3 -> `--msl-version 30000` (macOS 13+)
4. and then: **"Argument buffer runtime array currently not supported for combined image
   sampler"** - the engine's exact model.

The way through is to declare the table as **separate textures plus a shared sampler**
(`uniform texture2D uTexs[]; uniform sampler uSamp;`). spirv-cross then emits

```metal
struct spvDescriptorSetBuffer0 {
    spvDescriptor<texture2d<float>> uTexs [[id(0)]][1] /* unsized array hack */;
};
```

- a device-side descriptor array, indexed dynamically, with the sampler folded into the
  descriptor ("Overlapping binding: sampler uSamp [[id(0)]]").

So the port needs one **engine-side** change: the shader compiler rewrites `sampler2D table[]`
into `texture2D table[]` plus a shared sampler. That fits the engine's existing compiler, which
already rewrites sources (interface locations, per-stage bindings). The alternative, a texture
array per table, only works for tables that share a size and a format.

### The recipe (stage 5, working)

Translation, per stage:

```
spirv-cross --msl --msl-version 30000 --msl-decoration-binding \
  --msl-argument-buffers --msl-argument-buffer-tier 1 \
  --msl-device-argument-buffer <set> --msl-discrete-descriptor-set 0 \
  --rename-entry-point main <entry> <stage> in.spv --output out.metal
```

- The table's descriptor set goes into a **tier-2 argument buffer in the device address
  space**; the other set stays a set of ordinary bindings.
- The argument buffer **takes the buffer index of its descriptor set**, so that set's number
  must not collide with the binding numbers of the ordinary set (the table is in set 6 here,
  because the ordinary set uses 0 to 4).
- The MSL that comes out declares the table as a device-side descriptor array:

  ```metal
  struct spvDescriptorSetBuffer6 {
      spvDescriptor<texture2d<float>> uTable [[id(0)]][1] /* unsized array hack */;
      spvDescriptor<sampler> uSamplers [[id(1)]][1] /* unsized array hack */;
  };
  fragment fragment_main_out fragment_main(..., const device spvDescriptorSetBuffer6& s [[buffer(6)]])
  ```

Runtime, the way MoltenVK does it on Metal 3:

- one **shared** buffer holds the argument buffer: the two pointers (to the texture handle
  array and to the sampler handle array) followed by the handles themselves;
- a texture's handle is `MTLTexture.gpuResourceID`, a sampler's is `MTLSamplerState.gpuResourceID`
  (both exposed by SharpMetal), and a buffer's address is `MTLBuffer.gpuAddress`;
- the array addresses point **into the same buffer** at an offset. Keeping the arrays in
  separate buffers faults the GPU; inside one buffer it works, which is what MoltenVK's
  "outlined data" case does.

Two sharp edges worth knowing:

- `MTLRenderPassDescriptor.ColorAttachments.Object(0)` returns a **struct copy**: a
  value-type property (the clear colour) set on the copy is lost, and the pass then clears to
  whatever the descriptor's memory held. Write it back with `SetObject`.
- The `CAMetalLayer`'s drawable size follows the view's bounds, and the view has no bounds yet
  while the window is being created - set the size from the window's size times the scale
  factor, or the drawable comes out 1x1 and the window shows one magnified pixel.

## Compute and barriers

The engine's ocean FFT, Hi-Z pyramid and fog passes are compute passes that write storage
images. On this M1 Pro:

- writing **rgba16f** storage images works, and the result reads back cleanly;
- a compute pass and the render pass that samples it need **no explicit barrier** between them -
  Apple silicon's automatic hazard tracking (`MTLHazardTrackingModeTracked`, the default) already
  orders them. That is the opposite of Vulkan, where the engine records explicit barrier graphs.

This matters more than it looks: the "barriers become encoder boundaries" step was the riskiest
part of a Metal backend, and on Apple silicon most of it disappears - the engine's barrier graphs
would mostly become "use the right encoder order", with `MemoryBarrier(MTLBarrierScope)` (in
SharpMetal) for the cases that still need one. On other Apple GPUs (and any future non-Apple
Metal target) the automatic tracking is not there, and this part of the work comes back.

## Not proved yet (the remaining risk)

- Push descriptors (43 call sites in the engine) have no Metal equivalent: `setBytes` or argument
  buffers per draw.
- Bindless (21 files) means becoming the argument-buffer allocator.
- The barrier graphs become encoder boundaries — the riskiest part of the whole exercise.
- MetalFX as an `IUpscaler` (Apple's upscaler, `MTLFXTemporalDenoisedScaler` is in SharpMetal).
- The `Gpu/` layer itself: ~8.6k lines of Vulkan that a Metal backend would replace.

## Running

```
brew install glslang spirv-cross      # the two tools the shader chain needs
dotnet run --project src/Meitou.MetalSpike            # 300 frames, then exit
dotnet run --project src/Meitou.MetalSpike -- --hold  # 1800 frames, for a screenshot
```

The engine's `meitou-run.sh` sets `DYLD_LIBRARY_PATH=/opt/homebrew/lib` for the Vulkan path; the
spike loads `libglfw.3.dylib` by path itself, so it runs without it.
