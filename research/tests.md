## Test and build infrastructure for multiplayer (read-only research)

### 1. Toolchain and global build settings
- `global.json`: SDK `10.0.401`, `rollForward: latestFeature`. There is no `Directory.Packages.props`, so every csproj pins its own package versions.
- `Directory.Build.props:4-11` applies to every project: `net10.0`, `LangVersion latest`, `<Nullable>enable</Nullable>`, `ImplicitUsings`, `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`, `AllowUnsafeBlocks`, `PackageLicenseExpression GPL-3.0-or-later`.
- `Directory.Build.targets:4-12` only copies the Streamline/DLSS DLLs into `Meitou.Game` and `Meitou.ModelViewer`. It has nothing else that matters here.
- No `AnalysisLevel`, `AnalysisMode`, `EnforceCodeStyleInBuild`, `NoWarn`, `GenerateDocumentationFile` or `InvariantGlobalization` is set anywhere (grep over all csproj/props/targets).
- Rules that will bite new code (Observed from config, not checked with a build):
  - Nullable warnings (CS86xx) are build errors.
  - The SDK's default-enabled CA rules are errors too, e.g. CA1416 (platform compatibility, which matters for `Socket`/Windows-only APIs), CA2200, CA2014 and CA2252 (preview APIs).
  - `.editorconfig:82` `dotnet_style_readonly_field = true:warning` and `:121` `csharp_prefer_static_local_function = true:warning` are IDE rules. Because `EnforceCodeStyleInBuild` is not set, they should not fail CLI builds.
  - Culture: InvariantGlobalization is off, so a text or number wire format must pass `CultureInfo.InvariantCulture` explicitly.

### 2. Projects, the solution and adding `src/Meitou.Net`
- `MeitouClient.slnx` has folders `/launcher/`, `/src/` (10 projects), `/tests/` (one project) and `/tools/` (Meitou.ModelViewer, Meitou.Tools). A new project needs `<Project Path="src/Meitou.Net/Meitou.Net.csproj" />` under `/src/`.
- The library csprojs are minimal. `Meitou.Simulation.csproj` only has refs to Core and Data, `<InternalsVisibleTo Include="Meitou.Tests" />` and BepuPhysics 2.4.0. A new csproj should copy that shape. Elsewhere, `Meitou.Navigation.csproj` uses the same `InternalsVisibleTo` item, and `Meitou.Game/AssemblyInfo.cs:3` uses the attribute form.
- The test project lists every library explicitly (`tests/Meitou.Tests/Meitou.Tests.csproj:20-28`), so a new project also needs a `ProjectReference` there.
- Dependency direction (docs/simulation.md "World model"): "Core ← Data ← Simulation ← Engine ← Game; Navigation/Rendering are sibling libraries".
  - `Meitou.Engine` (WorldSession) references only Core, Data and Simulation.
  - A networking layer that should run on a GPU-less server belongs at the Engine level or below, never under Rendering.

### 3. Trimming, AOT and serialization
- Only the launcher is Native AOT (`launcher/Meitou.Launcher/Meitou.Launcher.csproj:11` `<PublishAot>true</PublishAot>`). It references only `Meitou.Core`, which has `<IsAotCompatible>true</IsAotCompatible>` with the comment "trim/AOT analyzer warnings here are build errors".
  - **Any wire or protocol type put in Meitou.Core must be AOT-safe.**
  - The existing pattern is source-generated JSON: `LauncherConfig.cs:76-78` (`[JsonSerializable(typeof(LauncherConfig))] ... LauncherJson : JsonSerializerContext`) and `Meitou.Core/Settings/SettingsSchema.cs:81-82`.
- The game and viewer are published self-contained, not trimmed and not single-file:
  - `tools/scripts/package-release.ps1:31-38`: `dotnet publish ... -r $Rid --self-contained true -p:DebugType=embedded`.
  - The Satori GC SDK is applied on publish (`Meitou.Game.csproj:4`).
  - Also `TieredCompilation=false`, `TieredPGO=false`, `ServerGarbageCollection=true`, 4 heaps.
  - Reflection-based serialization therefore works in the game, but MemoryPack-style or hand-written binary is what fits the project's style. Existing binary I/O uses `BinaryReader`/`BinaryWriter` (`FcsReader`/`FcsWriter`, `NavMeshCache`, `ContainerSave`).
- Releases (`.github/workflows/release.yml`):
  - Nightly or manual. The `version` job runs `dotnet test --filter "Category!=Slow&Category!=Bench" -c Release` on **windows-latest only** (`:90-92`).
  - It then packages win-x64, linux-x64 and osx-x64/arm64 zips, each on its own OS (`:106-111`).
  - The Linux AppImage is the **launcher only** (`launcher-release.yml`: `dotnet publish launcher/Meitou.Launcher ... --self-contained`, then `vpk pack`; the notes say "the linux-x64 AppImage").
  - The game for Linux is a zip.
  - There is no pull-request CI and no test run on Linux or macOS.
- NuGet licensing:
  - `THIRD_PARTY_NOTICES.md:95-111` is a table (`| Package | Used by | License |`); a new package (e.g. LiteNetLib, MIT) gets a row there. Packages under licences that require the text (DotRecast, zlib) also get a section quoting the licence (`:116-138`).
  - The table already lacks BepuPhysics 2.4.0 (Simulation), NVorbis (Data) and Silk.NET.OpenAL / OpenAL.Soft.Native (Audio) (Observed by grep).
  - The licence is GPL-3.0-or-later plus a DLSS-only linking exception (`LICENSE-EXCEPTION.md`), so dependencies must be GPL-compatible.

### 4. Test framework and conventions
- **xunit v3** (`xunit.v3 3.1.0`, `Microsoft.NET.Test.Sdk 17.14.1`, `xunit.runner.visualstudio 3.1.4`). The test project is `OutputType Exe`, with a global `using Xunit` (`Meitou.Tests.csproj:16`). There is no `xunit.runner.json` and no `CollectionBehavior`, so default parallelism applies.
- Traits:
  - `SlowAttribute.cs:10-12`: `ITraitAttribute` returning `("Category","Slow")`. It is used in about 73 files and is required for anything that needs the install, a GPU or a long run.
  - `BenchAttribute.cs`: `("Category","Bench")`, used for timing asserts (e.g. `AiBenchTests.cs:33` `[Theory, Bench]`).
- Skipping without the game: `Assert.SkipWhen(install is null, "Kenshi install not found")` (`HubTests.cs:22-23`). Install data is shared through `InstallData.cs:13-16`, where `Lazy<GameInstall?>`, `BaseGame`, `FullLoadOrder` and `Levels` are loaded once and must not be mutated.
- Output: primary-constructor `ITestOutputHelper` (`GoldenHashTests(ITestOutputHelper output)`).
- Repo rule (`CLAUDE.md`/`AGENTS.md` "Build"): **"Get the user's explicit approval before adding tests, including new cases in existing tests."** Multiplayer tests must be proposed and approved before they are written.
- Waiting on helper threads: `TestWaits.TickUntil(world, done, deadline)` (`Simulation/TestWaits.cs:15`), with a 20 s deadline and `Thread.Sleep(1)` between ticks.

### 5. Determinism and golden tests (what "same commands, same world" is checked against)
- `DeterminismTests.cs:9-21` `Hashes(seed, threads, characters)` builds `WanderSystem.Create(...)`, runs to checkpoints `[1,5,30,90,200,300]` and calls `world.StateHash()` at each. `:23-29` asserts `Hashes(7,1) == Hashes(7,4) == Hashes(7,16)`.
- Per-feature `Scripted(threads)` helpers do the same with commands. For example, `PlayerTests.cs:30-55` enqueues `SelectCommand`, `MoveOrder`, `StopCommand` with explicit `{ Tick = n }` and asserts that 1, 4, 16 and 1 threads agree (`:58-65`).
- `GoldenHashTests.cs`:
  - `:12-29` lists 15 scenarios: wander, player, roaming, bodies, fight, duels, wander-parts, town-parts, first-aid, aggro, ranged, carry, crime, build, dialogue.
  - `:52-69` pins `ulong[]` hashes per checkpoint, e.g. `["wander"] = [0x9C9775CE6597DD6BUL, ...]`. A behaviour change must update the pins in the same commit.
  - `:74-75`: **"The pins were made on Windows; the wandering scenarios use MathF.Sin/Cos/Atan2, which come from the platform's C library and can differ in the last bit elsewhere."** followed by `Assert.SkipWhen(!OperatingSystem.IsWindows(), ...)`.
  - docs/simulation.md:42-43 adds "pins are platform-specific".
  - Simulation has 60 `MathF.Sin/Cos/Atan2/Pow/Asin/Acos/SinCos` calls in 22 files.
  - **Lockstep between a Windows peer and a Linux or macOS peer is therefore not shown to work, and is expected to desync.** Whether same-OS peers on different CPUs agree is not tested either. That UCRT picks different code paths on FMA3 CPUs is my outside knowledge, not checked here.
- The hash: `World.StateHash()` (`World.cs:301-319`) is `StateHasher` FNV-like mixing (`Rng.cs:121-138`, with negative zero normalized). It covers Seed, Tick, Characters, Squads, Platoons, Player, the optional registries and every `IStateHashed` system. The docs call it "a diagnostic rather than gameplay work" (simulation.md:42); its cost per tick has not been measured, so a per-tick desync check needs a benchmark first.
- **Coverage gaps relevant to lockstep (Observed):**
  - No test builds `StandardParts.All` (grep finds nothing in tests); goldens use subsets.
  - Ragdolls (Bepu, which writes `hot.Position` at `RagdollSystem.cs:223`) run only when the host passes `CharacterRigs`: "the game does; tests and goldens do not" (simulation.md "Ragdolls"). Whether Bepu's SIMD path gives identical results across CPUs is **Unknown**.
  - No test varies `MinPartitionSize` on its own. Partitioning is varied only indirectly through the thread count.

### 6. Running two worlds headless in one process (lockstep agreement test)
Feasible today with no GPU and no install, through the synthetic builders:
- `SyntheticTown.World(seed, threads, residentSquads, settings, synchronousPaths: true, db, bodies, bodyTimeScale, feed, bodyOptions, minPartition, params ITickSystem[] extra)` (`SyntheticTown.cs:146-157`):
  - builds `OpenGroundWalkability(Ground)` and `StandardSystems.Build(...)` with `SynchronousPaths` (`:143-144`)
  - creates `new SimWorld(new WorldSettings { Seed, Threads, PublishSnapshots = false, MinPartitionSize }, walk, systems)`
  - enqueues `FocusCommand(Centre) { Tick = 0 }`
  - `SyntheticTown.Database(roaming:, anatomy:, items:, extra:)` (`:28`) builds an invented `GameDatabase` from `FcsRecord`s.
- Other builders: `WanderSystem.Create(seed, threads, characters, publish, minPartition)` (`WanderWorkload.cs:20-26`), `DuelFixture.NewWorld(seed, threads, out combat, ...)` (`Combat/DuelFixture.cs:64`), and `GoldenHashTests.TownParts()` (`:32-36`, roaming + bodies + feed).
- Each `World` owns its `WorkerPool` (`World.cs` ctor `pool = new WorkerPool(settings.Threads)`), and the systems hold state, so **each peer needs its own `StandardSystems.Build` call**. `PopulationData` caches use `lock`/`LazyInitializer` (`PopulationData.cs:109-152`) and the game reuses one instance across worlds (`GameHost.Saving.cs` `BuildSystems`), so sharing it is probably safe (Observed). Grep found no mutable `static` fields in Simulation.
- The game already runs a second headless World in-process: the duel "Runs the duel once, headless, on a world of its own" (`GameHost.Duel.cs:99`).
- With the install: `[Slow]`, `InstallData.BaseGame` / `Levels`, `PopulationData.Create(db, levels.Towns())` (`HubTests.cs:19-41`). For the real navmesh, `new NavSystem(install, db, levels, heights, cache:)` then `await nav.LoadZone(...)` before ticking, with `new PathService(nav.Walkability, synchronous: true)` (`HubNavigationTests.cs:31-38`).
- **Must be synchronous for agreement** (the tests do this; the interactive game does not):
  - `PathService(walk, synchronous)`: "otherwise a thread of its own answers ... so timing can differ between runs" (`PathService.cs:12-16`).
  - `PopulationSettings.Background` (`PopulationData.cs:200`).
  - The game sets `Background = interactive && !IsDuel` and `SynchronousPaths = !interactive || IsDuel` (`GameHost.Saving.cs:60-64`).
  - Navmesh zones go live asynchronously (`NavMeshService.ZoneReady`), and their age uses real time: `Stopwatch.GetTimestamp()` in `NavmeshWalkability.cs:195-203`; docs: "Navmesh unload uses real time (30 s)".
  - Ragdoll geometry is "gathered per zone on a worker" (`GameHost.cs:132`).
- Command-feed rules a lockstep test or harness must respect (`Commands.cs`):
  - `SimCommand.Tick` is `required`, and "a command for a tick already run applies at the next one" (`:12-13`), so late packets silently shift.
  - `CommandQueue.TakeDue` orders by `(Tick, local enqueue Sequence)` (`:64`), so peers must enqueue in one canonical order (for example by player index).
  - `SimCommand` has no issuer field. `World.Player` is singular (`World.cs` "The player: faction, money, selection").
  - `World.ApplyPausedInterfaceCommands()` (`World.Interface.cs:6-11`) applies Select, Inventory, Buy/Sell, Dialogue, PlaceBuildings and Edit commands **while paused without advancing Tick**. `WorldSession.AdvanceSimulation` calls it every paused frame (`WorldSession.cs:84-89`). Lockstep needs an extra sequence number within a paused tick.
  - Thread counts differ between machines: the game uses `Threads = config.SimThreads ?? Clamp(ProcessorCount/2,1,8)` and `MinPartitionSize = 128` (`GameHost.cs:106`). That is covered by the 1/4/16-thread tests.
- Save as a join or state transfer: not exact. `QuickSaveTests.cs:17-19`: "What differs is what the save does not carry (the tick, paths and orders, animation, combat state, the resident population)". simulation.md: "Loaded hashes need not match captured hashes". There is no full-state serializer.

### 7. Headless or dedicated-server entry points
- **None exists.**
  - `GameHost.Run()` (`GameHost.cs:76-80`) is `o.Screenshot is not null ? Screenshot() : Interactive()`, and both go through `Boot(VulkanDisplay display, ...)` (`:82`).
  - `BootSimulation(GroundHeights, interactive)` (`GameHost.cs:115-160`) is a GameHost instance method. It reads `scene.Database` and `scene.Objects?.Levels`, builds `NavSystem`, `RagdollGeometry`, `BiomeMap`, `TownBuildings` and `PopulationData`.
  - `scene` is `WorldScene` / `WorldFrame.Load(install, o)`, which are **internal types in Meitou.Rendering** (`WorldFrame.cs:451,475,486`), exposed through `InternalsVisibleTo MeitouClient`.
  - A server needs that boot path extracted into a GPU-free library (heightmap, levels, database, navmesh and population).
- `tools/Meitou.Tools` (`Program.cs:5-27`) has survey commands only: formats, fcs, load, meshes, navmesh, weather, audio, world-map and so on. It has **no command that runs a World**, and it does not reference Simulation directly (it gets it only through Navigation).
- `Meitou.Engine.WorldSession` is GPU-free and already built headless in tests (`Ui/FaithfulInteractionTests.cs:33`: `new WorldSession(Vector3.Zero, (-100,-100,100,100), (_, _) => 0, systems: [new PlayerSystem()])`). It drives ticks from real time (`AdvanceSimulation(realSeconds)`) and owns pause and speed locally (`Tick()`: `Simulation.TogglePause()` / `SetSpeed`). Lockstep or a server needs a tick gate in place of `Simulation.Advance`.
- The docs' "**Observed** (headless ...)" claims (simulation.md:122-224, save.md:233-337) and the "Sandbox" section ("Headless simulation/debug harnesses use selected StandardParts and synchronous services", simulation.md:949-951) point to harnesses that are not committed. Apart from the tests and the duel's in-process pre-run, I found none in `tools/` or `src/`.

### Implications
- A lockstep check in tests is cheap to write. Run two `SyntheticTown.World(...)` peers, one with `threads` 1 and one with 4, feed both the same tick-stamped `SimCommand`s in a canonical order, call `RunTick` and compare `StateHash` each tick. It only means something on one OS, with synchronous services and without ragdolls or `StandardParts.All`.
- Mixed-OS lockstep is contradicted by the repo's own golden test. The interactive game's asynchronous path, population, navmesh and ragdoll services would also have to become tick-deterministic.
- Host-authoritative replication (S) sidesteps all of that. But a dedicated or headless host needs the boot path pulled out of `GameHost` and Meitou.Rendering, and an AOT-safe serializer if anything lands in Meitou.Core.