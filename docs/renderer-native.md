# Native renderer

Current API: `src/Meitou.Rendering/Gpu/` (Core/Shaders included), windowing in Rendering.Display.
Vulkan only; engine contracts below are checked against the named source, not game-format facts.
Feature algorithms: render-* docs/[index](README.md).

## Device, frame and lifetime

GpuContext owns device/libraries/bindless/uploads/frame/globals. GpuFeatures.Require needs
bindless descriptor indexing, multiDrawIndirect/drawIndirectFirstInstance. VulkanDevice requires
Vulkan1.3/dynamic-rendering/synchronization2/timeline; check optional capabilities before use.
FrameLoop: two default slots, pre-frame primary (uploads/culls/bakes) then draw primary, full
barriers around pre-frame. Reuse/retire only after GPU completion. Updating uploads while
recording affects the whole submitted frame; never overwrite a used DeviceBuffer range.

Buffers/Textures/Samplers define owned/borrowed resource bindings. FrameScratch per-slot bump
allocation tracks120-frame maximum+25%headroom, shrinks at>2x need; Cap overflow fails and caller
handles it. Staging can shed unused chunks; descriptor-referenced constants retain storage.
PipelineLibrary caches explicit descriptions and records late creation (LatePipelines). `Prepare` has no caller: the loading warm-up draws what makes them, with the reflection and cloud passes forced on ([bench](bench.md) "Play benchmark").
CommandList records bindings/draw/dispatch/transfer/dynamic rendering, preserving host pass order.

## Synchronization and shaders

Sync.PassUse/ResourceStates track whole-resource identity, not independent subresource histories.
Read/read no barrier; write→access requires visibility, read→write execution ordering. GpuPass
applies merged synchronization2 dependencies/label immediately; no pass reordering. Repeated
upload writes require transfer ordering; draw-buffer blits/mips/readback follow command order.

Own GLSL through bundled shaderc; GlslProgramCompiler legacy cache3/native4, stage interface
locations and SPIR-V binding shifts. LegacyProgram set0 named blocks/samplers pushed, set1 dynamic
loose uniforms; resolve handles ahead. Native program layouts come from reflection, not one
assumed global set scheme. FrameGlobals publishes render-thread getters, Version invalidates
resolution, ApplyCount permits within-application caching; jobs consume prepared values.

BindlessTable has seven typed sampler arrays (BindlessKind bindings0..6). Per-slot descriptor
journal replays at begin/before submission; freed indices wait for consumers. Updating an index
changes every submitted draw using it, including already-recorded draws. Use new indices for
mid-frame binding changes. Tables/views/globals are render-thread state.

## Parallel recording and validation

Recording.Mode0 inline,1 serial secondary,2 default worker secondary. ParallelPass executes
insertion order; workers require>1 job and>=32 draws. RecordJobs cannot allocate/upload or
access mutable render-thread state; InJob guards even inline. Prepare constants/bindings first.
GlConventions keeps bottom-up images; presentation flips once, shader clip/front-face/upscaler
matrices agree. QueryArena reads completed slot timestamps without waits; GPU frame time includes
pre-frame. Parallel CPU stage sums are work, not elapsed duration.
Parity scripts compare10 Faithful views; repeat baseline first, pixel identity for unchanged
output, temporal/FSR baseline noise for nondeterministic output. Synchronization validation checks
resource/barrier/descriptor changes. [Benchmarking](bench.md). Logs/screenshots stay outside repo.
