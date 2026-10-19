# MeitouClient on macOS — port report (test team, first session)

Contact session: 2026-10-19, report compile same day. Written by an external tester working
with the maintainer; intended to be hand-off-able to an AI agent or another person.

A native Metal path next to Vulkan: [metal-backend](metal-backend.md).

## TL;DR

`MeitouClient` runs on macOS / Apple Silicon today, with **25 inserted lines across 4 files**.
The ModelViewer (`meitou-viewer --world --town "The Hub"`) renders the streamed world through
Silk.NET → Vulkan → MoltenVK → Metal at **~36–40 fps** at render scale 0.50 (vsync on), and the
whole runtime supports live camera, streaming, profiler, VRAM pie, weather, water, shadows.
The engine builds with **0 errors / 0 warnings** on arm64; the non-asset test suite passes
**419/425** (the 6 skips are asset/GPU-marked; the single failure is the cross-architecture
golden-hash pin, see below). Original game simulation code was not touched — every fix is
`OperatingSystem.IsMacOS()`-guarded, i.e. inert on Windows.

Stack: macOS 15.1, Apple M1 Pro, MoltenVK 1.4.2 (brew), Vulkan loader 1.4.363 (brew),
.NET SDK 10.0.401, Silk.NET 2.23 (as pinned), repo at 2026-10-19 master.

Everything in this report was produced empirically; error excerpts are verbatim.

---

## 1. Environment setup that worked

```sh
brew install molten-vk vulkan-loader vulkan-tools   # MoltenVK 1.4.2, loader auto-finds the ICD
brew install glfw                                   # not strictly required (Silk.NET ships its own), harmless
curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --version 10.0.401
```

Two facts worth noting for other macOS testers:

- The brew loader **discovers** MoltenVK automatically (`vulkaninfo` shows `Apple M1 Pro,
  apiVersion 1.4.357, MoltenVK`), but Silk.NET's `Vk.GetApi()` still fails to *find* the loader
  dylib, because `/opt/homebrew/lib` is not in dlopen's default path. Runtime needs:
  ```sh
  export DYLD_LIBRARY_PATH=/opt/homebrew/lib
  ```
- Build hosts must have the 10.0.4xx SDK band (repo `global.json` pins 10.0.401 with
  `rollForward: latestFeature`; a plain 10.0.1xx SDK does not match).

Game assets: downloaded depot `233861` (Kenshi Content, Windows) with DepotDownloader 3.4.0
(macos-arm64 build): `DepotDownloader -app 233860 -os windows -osarch 64 -dir ~/Games/Kenshi`.
Two pitfalls other Mac testers will hit: (a) the Steam "confirm in mobile app" flow can hang
DepotDownloader's login if the phone app is not logged in (GitHub SteamRE issue trackers have
reports; retry works); (b) Steam's own console `download_depot 233860 233861` in the Steam app
stalled for us at 13 MB (with its main-loop stall assertion), but Steam *did* remember and
resume after restart. Final asset tree: 12 GB, 15,692 files, depot root == install root, pointed to via
`meitou.local.json` `{ "kenshiPath": "/Users/<user>/Games/Kenshi" }`.

## 2. Blocker-by-blocker (the 6 things between `git clone` and a rendered world)

### 2.1 Loader discovery — `FileNotFoundException` in `Silk.NET.Vulkan.Vk.CreateDefaultContext`
Fixed by `DYLD_LIBRARY_PATH` (above). No code change needed. A `meitou-run.sh` wrapper was made
so the tester does not have to remember it (see §6).

### 2.2 `vkCreateInstance` → `ErrorIncompatibleDriver` (headless, windowless!)
The macOS Vulkan loader hides portability drivers unless the instance opts in:
`VK_KHR_portability_enumeration` + `VK_INSTANCE_CREATE_ENUMERATE_PORTABILITY_BIT_KHR`.
Without it you get `ErrorIncompatibleDriver` even when `vulkaninfo` works (vulkaninfo sets it).
Windows/Linux unaffected — patch is `IsMacOS()`-guarded.

### 2.3 Terrain shader → `[mvk-error] Shader library compile failed: reference to 'coord' is ambiguous`
SPIRV-Cross output collides with **Metal's newer `metal::coord` namespace type** (modern Metal
headers). Meitou's terrain GLSL declares `struct Coord` + `Coord coord(vec2)`; every call site
`coord(uv * sB.xy)` then compiled as a cast to `metal::coord` — 7 errors like
`functional-style cast from 'float2' to 'metal::coord' is not allowed`.
Fix: pure rename in `TerrainShaders.cs` (`Coord` → `GradCoord`, `coord` → `gradCoord`).
No behavioural change; also worth considering for future-proofing on Windows (any SPIRV-Cross
update on Windows could pull the same new Metal-era headers into translation). Rename only
touches terrain; the other shader files were swept for `coord` — clean.

### 2.4 Water shader → `'sampler' attribute parameter is out of bounds: must be between 0 and 15`
The GL-translation water program (~27 descriptors: ocean + 6 shadow maps + atmosphere +
rivers + foam + reflections…) fails because MoltenVK binds push descriptor sets through the
legacy MSL path (`[[sampler(n)]]`), capped at 16 per function on Apple silicon. Same signature
as Cemu's old bug (KhronosGroup/MoltenVK #1779). Fixes combined (both required):

1. **Runtime env** `MVK_CONFIG_USE_METAL_ARGUMENT_BUFFERS=1` — lifts the 16-sampler cap for
   non-push sets (Tier 2 argument buffers, Apple silicon only).
2. **Code clamp**: MoltenVK advertises `maxPushDescriptors = 287`, so ShaderLibrary put those
   big GL sets into **push** sets, and push sets *cannot* be argument buffers → legacy path
   → doomed. `VulkanDevice.MaxPushDescriptors` is clamped to 16 on macOS so sets above that
   size fall back to the **regular descriptor set path**, which is already implemented and
   exercised on Windows (where the driver limit is ~32) and *does* get argument buffers.

Neither change hurts Windows: both are macOS-guarded / env-only.

### 2.5 `bindless Texture2D array is full (512)` crash ~2 min into streaming
`BindlessTable` derives capacity from `MaxPerStageDescriptorUpdateAfterBindSamplers` /
`MaxDescriptorSetUpdateAfterBindSampledImages`: MoltenVK advertises **1024** → 512 × 2D slots.
The Hub's resident foliage textures exceed 512 (eviction works and was verified to free
slots; 512 residents just really occur). Windows GPUs advertise 16k+ so the author never
saw it. Fix: on macOS raise the derivation floor to 8192 (→ 4096 × 2D) — MoltenVK **accepts**
the larger table at device creation despite advertising less, i.e. the advertised number is
conservative, the argument-buffer reality is bigger. Worth knowing when upstreaming: we
deliberately exceed the advertised limit; if that is considered too spicy, an alternative is
driving it from an env override (`MEITOU_BINDLESS_LIMIT`?) instead.

### 2.6 Debug overlay rendered lines but no text (F9/F11/F12 graphs without labels)
`DebugOverlay` bakes glyphs with stb_truetype, which cannot bake from `.ttc` collections
without a font index. macOS's `/System/Library/Fonts/Menlo.ttc` is a `.ttc` → empty atlas.
Fix: add plain `.ttf` candidates before the `.ttc`:
`/System/Library/Fonts/Supplemental/Courier New.ttf`, `/System/Library/Fonts/SFNS.ttf`.
After the fix, F9 fps, F11 stats, F12 profiler and the Tab panel are fully readable.
Author had already anticipated Linux (DejaVu) and macOS (Menlo) — just not macOS's `.ttc`s.

## 3. The four patches (verbatim diff)

All 25 lines, `git diff` against 2026-10-19 master:

```diff
diff --git a/src/Meitou.Rendering/DebugOverlay.cs b/src/Meitou.Rendering/DebugOverlay.cs
--- a/src/Meitou.Rendering/DebugOverlay.cs
+++ b/src/Meitou.Rendering/DebugOverlay.cs
@@ -25,6 +25,8 @@
     [
         @"C:\Windows\Fonts\consola.ttf", @"C:\Windows\Fonts\CascadiaMono.ttf", @"C:\Windows\Fonts\cour.ttf",
         "/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf", "/usr/share/fonts/TTF/DejaVuSansMono.ttf",
+        // stb_truetype cannot bake from .ttc collections without an index, so macOS needs a plain .ttf (Menlo is a .ttc here).
+        "/System/Library/Fonts/Supplemental/Courier New.ttf", "/System/Library/Fonts/SFNS.ttf",
         "/System/Library/Fonts/Menlo.ttc",
     ];
diff --git a/src/Meitou.Rendering/Gpu/BindlessTable.cs b/src/Meitou.Rendering/Gpu/BindlessTable.cs
--- a/src/Meitou.Rendering/Gpu/BindlessTable.cs
+++ b/src/Meitou.Rendering/Gpu/BindlessTable.cs
@@ -89,6 +89,10 @@ public sealed unsafe class BindlessTable : IDisposable
         device.Vk.GetPhysicalDeviceProperties2(device.PhysicalDevice, &props);
         uint limit = Math.Min(props12.MaxPerStageDescriptorUpdateAfterBindSamplers, props12.MaxDescriptorSetUpdateAfterBindSampledImages);
+        // MoltenVK advertises only 1024 update-after-bind sampled images ("Metal caps the legacy path"), but its Metal
+        // argument buffers on Tier 2 (Apple silicon) hold far more. The 512-slot 2D array overflows when 512+ unique
+        // textures are resident (The Hub's foliage alone); Windows GPUs advertise 16k+ and never see this.
+        if (OperatingSystem.IsMacOS() && limit < 8192) limit = 8192;
diff --git a/src/Meitou.Rendering/Gpu/Core/VulkanDevice.cs b/src/Meitou.Rendering/Gpu/Core/VulkanDevice.cs
--- a/src/Meitou.Rendering/Gpu/Core/VulkanDevice.cs
+++ b/src/Meitou.Rendering/Gpu/Core/VulkanDevice.cs
@@ -328,6 +328,12 @@
         {
             extNames.Add("VK_KHR_surface");
         }
+        // macOS: the loader hides portability drivers (MoltenVK) from instances that do not opt in (VK_INSTANCE_CREATE_ENUMERATE_PORTABILITY_BIT_KHR).
+        bool portability = OperatingSystem.IsMacOS();
+        if (portability && !extNames.Contains("VK_KHR_portability_enumeration"))
+        {
+            extNames.Add("VK_KHR_portability_enumeration");
+        }
@@ -361,6 +367,7 @@
             var ci = new InstanceCreateInfo
             {
                 SType = StructureType.InstanceCreateInfo,
+                Flags = portability ? InstanceCreateFlags.EnumeratePortabilityBitKhr : 0,
                 PNext = settingCount > 0 ? &layerInfo : null,
@@ -870,6 +877,13 @@ // in the push-descriptor properties block:
             MaxPushDescriptors = pushProps.MaxPushDescriptors;
+            // MoltenVK (macOS): push descriptor sets cannot go into Metal argument buffers, so they bind through MSL
+            // attributes, where the per-function sampler cap is 16 — a set with more (water: ocean + shadows + sky) fails
+            // to compile. Lower the budget so the existing regular descriptor set path (argument buffers) is used instead.
+            if (OperatingSystem.IsMacOS() && MaxPushDescriptors > 16)
+            {
+                MaxPushDescriptors = 16;
+            }
diff --git a/src/Meitou.Rendering/TerrainShaders.cs b/src/Meitou.Rendering/TerrainShaders.cs
--- a/src/Meitou.Rendering/TerrainShaders.cs
+++ b/src/Meitou.Rendering/TerrainShaders.cs
@@ -205,9 +205,9 @@
-        struct Coord { vec2 p, dx, dy; };
-        Coord coord(vec2 p) { Coord c; c.p = p; c.dx = dFdxCoarse(p); c.dy = dFdyCoarse(p); return c; }
-        vec4 tex(sampler2DArray s, Coord c, float layer) { return textureGrad(s, vec3(c.p, layer), c.dx, c.dy); }
+        struct GradCoord { vec2 p, dx, dy; };
+        GradCoord gradCoord(vec2 p) { GradCoord c; c.p = p; c.dx = dFdxCoarse(p); c.dy = dFdyCoarse(p); return c; }
+        vec4 tex(sampler2DArray s, GradCoord c, float layer) { return textureGrad(s, vec3(c.p, layer), c.dx, c.dy); }
@@ -238,8 +238,8 @@
-            Coord base = coord(uv * sB.xy), grass = coord(uv * sB.zw), slopeUv = coord(uv * sA.xy), dirt = coord(uv * sC.xy), road = coord(uv * sC.zw);
-            Coord cliffX = coord(vec2(uv.y, vert) * sA.zw), cliffZ = coord(vec2(uv.x, vert) * sA.zw);
+            GradCoord base = gradCoord(uv * sB.xy), grass = gradCoord(uv * sB.zw), slopeUv = gradCoord(uv * sA.xy), dirt = gradCoord(uv * sC.xy), road = gradCoord(uv * sC.zw);
+            GradCoord cliffX = gradCoord(vec2(uv.y, vert) * sA.zw), cliffZ = gradCoord(vec2(uv.x, vert) * sA.zw);
```
Upstream split suggestion: (a) portability enumeration + (b) push clamp + (c) bindless
floor are three *separable* commits — each self-contained, each with its own rationale above;
(d) the rename and (e) the font list can be two more. Splitting them keeps review easy and
lets Windows-side considerations (rename, fonts) ship without the macOS-only ones.

## 4. What works / what degrades

Verbatim device feature report (headless probe; after patches):

```
Device: Apple M1 Pro, Vulkan 1.3.357(?)/1.4.357 via loader, IntegratedGpu, validation off
dynamicRendering=True synchronization2=True timelineSemaphore=True extendedDynamicState=True
extendedDynamicState2=True (logicOp=False, patchControlPoints=True)
fillModeNonSolid=True depthClamp=True samplerAnisotropy=True textureCompressionBC=True
bindless=True multiDrawIndirect=True drawIndirectFirstInstance=True drawIndirectCount=False
shaderDrawParameters=True pushDescriptor=True (max clamped) depthClipControl=True
rayQuery=False bufferDeviceAddress=False fragmentShadingRate=False extendedDynamicState3=False
Limits: timestampPeriod=1ns ...
```

| Feature | On M1/MoltenVK | Effect |
|---|---|---|
| Bindless (all 6 descriptor-indexing flags) | **works** | the architectural cornerstone |
| Dynamic rendering, sync2, timeline semaphores | works | hard requirements met |
| `drawIndirectCount` | **false** | GPU grass disabled by `FoliageGrassGpu.Supported`; falls back to CPU-recorded grass draws (by design, cosmetic) |
| Ray query GI | false | `--gi` off; the log prints the reason and continues (degradation path works) |
| DLSS / Streamline | absent | falls back to TAA; log reports it cleanly |
| **AMD FSR 3.1** | **works (round 4)** | macOS-built `libamd_fidelityfx_vk.dylib` loads through the game's own `MEITOU_FFX_PATH` integration — see "FSR on macOS" below |
| Push descriptors | works (clamped to 16 for large sets) | see §2.4 |

Runtime behaviours worth flagging:

- First-run MoltenVK shader compilation is heavy (expected); pipeline cache path persists per
  user and makes later boots better. `MEITOU_VK_VALIDATION` works as documented (validation
  layer availability via brew is a separate install; not exercised yet).
- `timestampPeriod=1ns` from MoltenVK was *maybe* misleading in one early reading (a 22 ms
  "terrain" GPU pass); the render-scale experiment proved the frame is genuinely pixel-bound,
  so the numbers are usable, just take single-pass readings with mild suspicion.
- Screenshots (`P`) land in a directory literally named `C:\Temp` inside the CWD on POSIX
  (Windows path default, harmless, fixed-scope We Should Normalise, see §5).
- Some terrain zones log `texture not found: .\data\newland\land\textures\Geobased_Rockloop_DIF_HI.dds` (and
  `Rockloop09_*`): these are `_HI` (high-res) variants the world data references that the current Steam
  depot simply does not ship (Kenshi's only DLC is the soundtrack; there is no HD-textures depot).
  The original engine resolves `.\data\...` the same way and falls back identically — **this is faithful
  behaviour, not a port bug**. (An earlier draft of this report blamed Windows path separators; the
  resolver already normalises via bare-file-name lookup — `runtime-materials.md` — and the files are
  genuinely absent on every platform.)

## 5. Remaining Windows-only inventory (for round 2 — the *game*)

`Meitou.Game` touches Win32 at three sites, found by `DllImport`/`LibraryImport` sweep:

| File | What | Status after round 3 |
|---|---|---|
| `src/Meitou.Game/WindowPointer.cs` | user32 `GetCursorPos`/`ScreenToClient` for edge-scroll camera | **Shimmed (round 3)**: on macOS the GLFW path (`window.Native.Glfw`, `GetCursorPos`, `WindowAttributeGetter.Focused`) implements the same contract — OS-level cursor, focused window. **Verified in game: all four edges pan.** One macOS-specific wrinkle the Win32 path never meets: in a windowed window the cursor *rests outside the content area* at the top/bottom edges (menu bar above, Dock below), so rejecting out-of-bounds coordinates killed those two axes — the shim clamps a small overshoot (≤128 px, window chrome; a second display is further) into the client area so the fraction pins to 0/1 and the pan fires, and still reads as nothing when the pointer is genuinely elsewhere or the window is unfocused. Diagnostic method that found it: temporary `MEITOU_POINTER_DEBUG` log of the `(x, y, Size)` fed to the camera — the top edge read `y = -66` (menu bar), the bottom `y = 916` of `856` (Dock), while left/right (maximised window sides have no chrome) were in-bounds. |
| `src/Meitou.Game/SplashWindow.cs` | full Win32 splash (user32/gdi32) + `AddFontResourceExW` | Kept Windows-only by the author's own guard (`Show` returns null on non-Windows); the Meitou title screen covers loading cosmetics. A GLFW/Cocoa port is optional polish. |
| `src/Meitou.Game/GameConsole.cs` | kernel32 console alloc/attach | No work needed: all calls are `IsWindows()`-guarded; on POSIX the terminal is always attached, prompts work, `log.txt` unchanged. |

## 6. Local test rig quickstart (for whoever reproduces)

```sh
# env
brew install molten-vk vulkan-loader
export DYLD_LIBRARY_PATH=/opt/homebrew/lib
export MVK_CONFIG_USE_METAL_ARGUMENT_BUFFERS=1

# repo + build + run (working tree at the patched state)
cd MeitouClient
dotnet build tools/Meitou.ModelViewer -c Release
dotnet run -c Release --no-build --project tools/Meitou.ModelViewer -- --world --town "The Hub" --windowed

# recommended M1 preset
#   --texture-quality 1 --water-reflection 2 --water-ocean 128 --shadow-quality 1 --shadow-filter 1
# in-app: Tab panel → Render scale (upscaler) 0.50; F9 fps / F11 stats / F12 profiler
```

Test suite (no assets needed for the filter below; note the golden-hash failure on arm64):

```sh
dotnet test -c Release --filter "Category!=Slow&Category!=Bench"   # 419 pass / 1 fail / 6 skip
```

The failing test `GoldenHashTests.Scripted_scenarios_hash_as_pinned` pins per-tick simulation
state hashes. On arm64 10 of 15 scenarios diverge (5 match exactly); it is a cross-architecture
floating-point identity issue (FMA/intrinsics), not a logic defect — the sim is deterministic
on a given machine. Suggested upstream handling: pin per-architecture hashes (or skip the
exact-hash arm on non-x64 and keep the thread-agreement checks). The window of precision loss
that matters for gameplay has not been observed; the rest of the simulation suite passes.

## 7. Performance (Apple M1 Pro, borderless-fullscreen 3024×1964, 3-image FIFO swapchain)

| Setting | FPS | Frame | Notes |
|---|---|---|---|
| Full everything, scale 0.83 | 21–25 | GPU 45–48 ms | terrain pass ~23–29 ms GPU; CPU total 9.4 ms |
| M1 preset, scale 0.83 | 22 | GPU 45 ms | water/shadow knobs barely moved it |
| M1 preset, **scale 0.50** | **36–40** | GPU 27.5 ms, CPU 6.3 ms | terrain 11.9 ms; visual crispness survived |

Scaling experiment: render scale 0.83 → 0.50 cut the GPU total 48 → 27.5 and the terrain pass
28.7 → 11.9 — i.e. **cost scales ~linearly with pixel count** (2.75× fewer pixels, ~2.4× cost),
so the frame is honestly shading-bound, not a translation/stall artifact. Render scale (Tab
slider) is the single most effective knob; VRAM sits at 3.7–4.7 of a 10.7 GB budget whatever
we throw at it.

Strategic aside: in our (tester+author) discussion we estimated native Metal might land
10–20% ahead of MoltenVK here; these runs are consistent with that estimate. Silk.NET's
binding overhead is irrelevant (thin P/Invoke, the engine is pointer-heavy already).

## 8. Round 2–3 gameplay verification (Apple M1 Pro, `--new-game --windowed`)

The **game binary booted unpatched** — the author's `IsWindows()` guards held at every site;
no shims were needed to reach a playable state. Verified in play:

- Character select, move orders, **attack**, jobs text; **game speed (F2–F4)**; **audio**
  (1252 sound emitters, Wwise banks through OpenAL Soft); **F5 quicksave / F9 load round-trip works**
- Original data-driven UI renders fully: HUD, character sheet with portrait, town/money/day,
  inventory and trade
- `Tab` intentionally no longer opens settings — it is Kenshi's `change_squad`
  (`GameHost.cs` comment: "Tab is the game's change_squad, not the settings (they open from
  the Esc menu)"); settings are the Esc menu. F11 conflicts with a macOS global shortcut;
  F12 profiler works.
- NPCs idle (sit/pose, react to aggro but do not stroll) — this is the roadmap's known
  "partial AI" frontier ("planner and full off-screen town simulation" listed as remaining
  in ROADMAP.md), not a platform issue. Sneak not triggering detection is likely the same
  AI thinness; needs a repro before calling it a bug.
- Open gameplay item: **container looting** — chests appeared untargetable in The Hub;
  needs a precise repro (which chest, whether the target highlight appeared, whether the
  character was stationary). Undecided whether sim or picking.

Round-3 code change (in addition to §3): the `WindowPointer` GLFW shim above — +10 lines in
`WindowPointer.cs`, `unsafe` method, `using Silk.NET.GLFW`. **Verified: edge scroll pans on all
four edges** (after the menu-bar/Dock overshoot clamp — see §5).

## 9. FSR on macOS (round 4)

**FSR 3.1 runs natively** — first known FidelityFX-under-MoltenVK build. The game's own
`MEITOU_FFX_PATH` integration loads it unmodified; the title bar shows `fsr 3.1 1512x856` and the
profiler shows FSR's passes in `post`. Verified in The Hub (viewer), M1 Pro.

Recipe — AMD ships `amd_fidelityfx_vk` only for Windows, but the FidelityFX SDK 1.1.4 builds for
macOS arm64 with modest patches; the patch series and build script are packaged at
[fsr3-native-macos](https://github.com/SpaceCowboyServer/fsr3-native-macos) (`./build.sh` →
`libamd_fidelityfx_vk.dylib`):

- The Vulkan backend compiles GLSL with **glslang** (brew, invoked as a subprocess by the SDK's
  `ffx_shader_compiler` tool) — no DXC needed. The tool itself is portable C++ except `wmain`,
  Windows paths and the HLSL compilers (dxc/fxc), which the VK build never touches.
- Build order: the `ffx_shader_compiler` tool → its output drives pre-compiling all shader
  permutations (headers with SPIR-V blobs) → `ffx-api/CMakeLists.txt` with
  `FFX_API_BACKEND=VK_X64` produces the shared library.
- SDK patches (all `#ifdef _WIN32`-shaped, no Windows behaviour change):
  portable UTF/path helpers in `ffx_sc` (`wmain`→`main` shim, `mkdir` loop, manual UTF-8↔UTF-16 —
  `std::wstring_convert` is gone from libc++); MSVC CRT shims (`_countof`, `wcscpy_s`, `strcpy_s`,
  `sprintf_s`) in `ffx_util.h`; `FFX_API_ENTRY` → `__attribute__((visibility("default")))`;
  `CMAKE_GENERATOR_PLATFORM` derivation for non-MSVC generators; the Windows toolchain/
  `CMAKE_SYSTEM_NAME=WINDOWS` block skipped on other hosts; the frame-interpolation component and
  its VK swapchain (Windows presentation integration) excluded, with the 16 frame-generation entry
  points stubbed to `FFX_ERROR_INVALID_VERSION` — **upscale paths are untouched**; private context
  buffers packed larger on arm64 than the Windows layout (`FFX_SDK_DEFAULT_CONTEXT_SIZE` 128→256
  K×4B, asserted in the components); an error-string parser that built a string from a `npos`
  iterator (crash on any glslang error, MSVC-tolerated); and a genuine portability finding —
  macOS's `/bin/sh` **brace-expands `{0,1}`**, so the shader compiler sees a permutation option as
  two conflicting plain defines ("Macro redefined"); fixed by re-merging duplicate `-D` macros of
  the same name back into the brace form inside `ffx_sc`.
- Game-side (`FsrUpscaler.cs`): the macOS library name and `libvulkan.1.dylib` for
  `vkGetDeviceProcAddr`; nothing else — the game hands FFX its own MoltenVK device.

Measured at render scale 0.5 (the swapchain is the full Retina 3024×1712, so FSR renders 1512×856
and upscales 2×): **28 fps**, versus **36–40 with TAA** and 39 with Off at the same scale — FSR's
passes cost ~12 ms GPU in `post` (TAA's resolve is nearly free), almost all of it MoltenVK
translating the upscaler's many small compute dispatches and barriers. Correct but a net fps loss
on Apple Silicon: **TAA remains the right choice on M1 Pro**; FSR is now merely *available* on
macOS, where before it could not load at all. Include the built dylib next to the executable (or
set `MEITOU_FFX_PATH`) and pick FSR in the Tab panel.

## 10. Decisions worth making upstream (input from this tester)

1. **Bindless floor** (§2.5): keep the static `8192` on macOS, or expose `MEITOU_BINDLESS_LIMIT`?
   MoltenVK accepted 4096-slot 2D arrays quietly; a driver update could conceivably start
   rejecting the non-advertised size — an env guard is the safer long-term shape.
2. **Push-descriptor clamp value**: 16 == Metal's legacy sampler cap. Alternative: clamp only
   when a program's set actually contains >16 samplers (more surgical, more code).
3. **Golden hash policy** per architecture (§6).
4. **`drawIndirectCount`**: MoltenVK does not implement it on our stack; GPU grass silently
   degrades. Fine as-is.
5. **Windows path defaults** (`C:\Temp` screenshots/impostors/cinema): POSIX-friendly defaults
   would remove a small surprise (screenshots landing in a literal `C:\Temp` folder). The
   `.\data\...` texture warnings turned out to be faithful-missing `_HI` assets (see §4), not a
   path bug — no action needed there.
6. **FSR on macOS**: done (round 4) — see the section below. AMD ships the FidelityFX runtime only
   for Windows, but the SDK builds for macOS with modest patches; upstreaming the portability
   guards (or AMD shipping a macOS dylib) would make it a copy-in dependency for any Mac build.

---

*Session artifacts: portability probe program outside the repo (`meitou-mac-probe` headless
VulkanDevice test), launch wrapper `meitou-run.sh` (env exports above), full debug logs
(`/tmp/meitou-debug*.log` equivalents) from the three compile-failure diagnoses.*
