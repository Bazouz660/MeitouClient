# Meitou Metal spike

An experiment: how far can a **native Metal** path get, using the engine's own idioms?

This is not a port and not part of the solution. It is a probe of the risky parts of a possible
Metal backend, on a local branch (`metal-spike`), so that the answer to "could we rewrite the
renderer against Metal?" is measured rather than guessed.

## What is proved so far

| Stage | What it shows | Status |
|---|---|---|
| 0 | C# -> SharpMetal -> `CAMetalLayer` on the GLFW window: device, layer, clear, present | works |
| 1 | The shader chain: GLSL -> SPIR-V (glslang) -> MSL (spirv-cross) -> runtime compile, with a sampled texture | works |
| 2 | The engine's binding pattern: uniform block (std140), storage table (std430) indexed dynamically, sampled texture | works |

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
