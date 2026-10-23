# Benchmarking

The world viewer's offscreen harness measures frame cost and visual effects of runtime switches.
Implementation: `WorldApp.Bench.cs`, `src/Meitou.Rendering/Bench/`, `BenchHarnessTests`.
Options: [viewer.md](viewer.md).

## Runs

```text
meitou-viewer --view swamp --ab shadows --ab-period 64 --bench-frames 600
meitou-viewer --view dust --ab particles
meitou-viewer --view swamp --bench-motion turn --bench-frames 600
meitou-viewer --view hub --bench-frames 600 --bench-out baseline.json
meitou-viewer --bench-compare baseline.json candidate.json
```

The world loads and settles before measurement. Two frames are in flight without VSync;
recording overlaps GPU execution. `--bench-serial` waits between frames and does not represent
normal pipelined throughput.

`--view` expands a `NamedViews.Table` preset; later options override it. Presets include
`swamp`, `swamp-rain`, `swamp-low`, `dust` and `hub`. Specify resolution, upscaler, render scale,
weather, time, seed and camera when comparing builds. Rain presets avoid dependence on initial
scheduled weather. A camera code preserves an exact view.

## A/B methodology

`--ab <name>` alternates a registered switch: A on/Meitou, B off/Faithful.
`--bench-frames` is the measured total across both sides (default 600); `--ab-period` sets frames
per side. Long periods suit target reallocations and shadow cadences. An unknown name lists
`AbToggles`; consult the registry for supported names. `--sway-step` advances grass motion;
offscreen sway otherwise stays fixed.

Compare A-minus-B and confidence intervals, means, medians, p95/p99 and stage costs. Differences
within uncertainty are inconclusive. GPU clocks, other programs, motion, cached cascades,
warm-up and streaming affect results. Alternate builds and repeat comparisons without a runtime
switch. Record conditions beside retained results. One view does not establish a general speedup.

Check the log's actual upscaler: DLSS falls back to FSR, then TAA, without Streamline or an NVIDIA card. Two settled
baseline stills establish temporal noise before comparison. Use A/B images and
`meitou-tools image-diff` to inspect changed pixels.

Throughput is approximately the slower of overlapping CPU/GPU work, rather than their sum.
A faster GPU stage may leave a CPU-bound frame unchanged. Parallel CPU stage sums are not
elapsed time. Evaluate cached-shadow redraws by tail latency as well as mean cost.

The frame profiler splits the tail into `ui` (overlays/UI), `submit` (window-copy recording and frame
submission), and `driver-wait` (present call, including queue-lock waits; CPU only). CPU values are
elapsed time, not CPU utilisation; submission may also block inside the driver. GPU `ui` ends before
the window copy; GPU `submit` measures that copy and the command tail, not the CPU submission call.
Offscreen frames have no present wait. Render-thread benchmark totals exclude `driver-wait`.
In the game the head is split too: `host` (the host's frame work before the world draw), `blood` (the host's blood marks and pools, then
the terrain decal layers redrawn), so `upd-terrain` and `gi` hold only their own work.
`sim` (game only) is the simulation ticks run on the render thread before the frame's clock starts (`FrameProfiler.Outside`); it counts in the total.
F12's legend groups stages (update, pre-frame, scene, present) into one summed row and line each; a click on a `+` row opens it (plotting
its stages instead), `-` closes it. The last column is the largest frame of the side shown over the 300 kept (a closed group's largest summed frame). `upd-chars` opens to its render-thread parts (`StageClock.Part`; not plotted; the pass meter shows them as subs): `fill` (the host's draw list),
`textures` (streaming pump), `gather` (characters in range, appearances), `pose` (the parallel posing and part placement), `materials`,
`bolts`, `portraits`, `hover` (hover masks) and `icons` (item and building icons). The offscreen bench reports them as `upd-chars/<part>`.

## Geometry and pass diagnostics

`--bench-tris` reports pass triangle counts and projected size distributions. Inspect main-view,
reflection and shadow work separately; geometry is repeated across views. Screen-size histograms
identify subpixel work that draw counts conceal.

`MEITOU_PASS_STATS=1` enables pass timing; `MEITOU_PASS_STATS_SKIP` excludes warm-up.
`MEITOU_SHAFT_STAMPS=1` separates shafts grid passes. Compare matching instrumentation settings.
Details: [renderer-native.md](renderer-native.md), [render-shafts.md](render-shafts.md),
[render-distance-benchmark.md](render-distance-benchmark.md).

## Loading and runtime settings

Measure cold and warm starts separately. `--no-load-cache` bypasses cache reads/writes.
Foliage logs report layout hits/misses and settle/busy times. The layout key hashes relevant
source rather than module/build-path identity; see [formats/foliage.md](formats/foliage.md).
OS file-cache state is separate. GPU setup, biome texture uploads, river-map generation and
streamer settling remain costs with warm caches. Separate start-up from steady-state frames.
The game's `--quit-after` log reports exposed-menu FPS, longest frame and stalls over 100ms separately from covered loading work.

The game/viewer disable tiered compilation and tiered PGO. Experiments can use
`DOTNET_TieredCompilation` / `DOTNET_TieredPGO`; record these settings. ReadyToRun and tiering
affect code generation and loading. Compilation/GC stalls matter to tail latency.

The flight benchmark (`--fly-benchmark <frames>`) prints a `gc` line: gen0/gen1 counts, pause total and bytes allocated during the flight.
Allocated bytes vary about 20% between identical runs (what streams in depends on timing) and a single blocking gen2 dominates the pause total,
so compare several alternating runs of two builds (copy each build's output folder). To rank allocation sites, run the viewer under
`dotnet-trace collect --providers Microsoft-Windows-DotNETRuntime:0x1:5 -- <viewer> ...` (GC events with `AllocationTick` stacks, one sample per
~100 KB) and count only events after the induced compacting gen2 of `FinishLoading` (the flight). Tracing lengthens GC suspensions: take pause
numbers from untraced runs.

Detailed logs/screenshots belong outside the repository. Evidence for original-game claims
belongs with the corresponding research and its Verified/Observed/Unknown label.
