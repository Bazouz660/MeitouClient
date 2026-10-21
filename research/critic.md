## Verified claims

1. **The navmesh goes live in the middle of a tick: Verified.**
   - `NavMeshService.cs:16-18` says so in its own words: "the simulation can see a zone appear in the middle of a tick".
   - `:128` calls `Walkability.SetWorld(world.With(own))` from the worker thread.
   - `NavmeshWalkability.cs:16` holds the mesh in `volatile NavWorld world`, and `:72,:75,:80,:89` re-read that field on every call.
   - `docs/simulation.md:39-40` says navmesh results "enter at tick boundaries". For the navmesh that is wrong.

2. **The interactive game runs its async services: Verified.**
   - `GameHost.Saving.cs:60` sets `Background = interactive && !IsDuel`, and `:64` sets `SynchronousPaths = !interactive || IsDuel`.
   - With `Background` on, towns are built on the worker (`PopulationSystem.cs:253-261`).
   - `GameHost.cs` `BootSimulation` sets `ZoneGate = zone => nav.Walkability.World.Contains(zone)`.
   - `UpdateNav` (`GameHost.cs:308-327`) unloads zones after 30 s measured with `Stopwatch`, and stamps `RepathCommand` with whatever `World.Tick` is current when the nav thread finishes.

3. **Ragdolls write gameplay state, and their state is hashed: Verified.**
   - Writes: `RagdollSystem.cs:223` (`hot.Position = r.Pose[root].Position`), `:529` (Yaw) and `:532` (Position). The hash is at `:574-590`.
   - Ragdolls are on in every game mode: `BuildSystems` passes `Ragdolls = characterRigs` and `RagdollEnvironment = ragdollGeometry`.
   - The geometry is gathered with `Task.Run` at `RagdollGeometry.cs:72`.
   - New fact: `RagdollPhysics.Step` is `Simulation.Timestep(dt)` with no thread dispatcher, so Bepu runs on one thread and the thread count does not affect it. Bepu's risks are the SIMD width and when the geometry gather finishes.

4. **Windows and Linux already diverge: Verified (Observed in the commit).**
   - `GoldenHashTests.cs:74-75` skips the test off Windows.
   - The message of commit dc4fe02e (lines 23-24) says that wander, roaming, wander-parts and town-parts hash differently on Linux.
   - Trig calls in Simulation, by grep: Sin 21, Cos 23, Atan2 11, Asin 1, Acos 1, Pow 2, SinCos 1, so 60 in all.
   - Navigation adds `MathF.Cos` ×2 and `MathF.Tan` ×1 in the slope thresholds.

5. **`GameHours` is a host input that is not hashed: Verified.**
   - `World.cs:155-159` documents it as an input, `:231` advances it, and `WorldSession.cs:95` overwrites it from the host clock before every tick.
   - Related checks, also Verified:
     - Late commands slide forward to the next tick silently (`Commands.cs:12, 58`).
     - The tie-break `Sequence` is a per-process counter (`Commands.cs:44, 64`).
     - A loaded world does not hash like the live one (`QuickSaveTests.cs:136-137`).
     - `Publish` writes only `HealShown`/`HealTick` (`CharacterTable.cs:141`), and the `Hash` loop never reads them.

## Refuted or corrected claims

- **`StateHash` costs more than the report estimated (0.3–0.7 µs per character).**
  - `StateHasher.Add` (`Rng.cs:132`) is one long serial chain: each call does a SplitMix, a multiply and an add, and each depends on the previous one. Speed is set by latency, not throughput.
  - I measured a copy of `StateHasher` outside the repo: .NET 10, `TieredCompilation`/`TieredPGO` off, 3000 characters × 230 `Add` calls, including the 39-float `stats.ToArray()` clone (`StatsInfo.Count` = 39). Result: **3.5 ns per `Add`, 0.8 µs per character, 2.4 ms per hash.**
  - That is a lower bound, because a real world also chases pointers through cold rows, inventories and AI state.
  - Effect: hashing every tick at 5× speed (150 ticks/s; `SimulationClock.Speeds = [1,2,5]`) would take about 35% of a core. Hashing every 30 ticks costs well under 1%.
  - Files: `D:\TEMP\claude\D--Github-MeitouClient\c161db95-e19c-4784-9759-b1467629f936\scratchpad\hashbench\hashbench.cs`

- **Randomness is not entirely stateless, contrary to `simulation.md:37`.**
  - `DialogueSystem` keeps one global counter, `rolls++` (`DialogueSystem.cs:338`); it is hashed at `:359`.
  - `MedicalState` keeps a per-character `rollCounter++` (`MedicalState.cs:21`, `MedicalState.Hits.cs:30`).
  - Both are deterministic, but only if calls happen in the same order. **`rollCounter` is not in `BodyHash` (`BodyHash.cs:8-36`)**, so `StateHash` cannot see it. None of the reports listed this gap.

- **Pinning one `NavWorld` per tick is not enough as the code stands.**
  - `Exact` and `Toward` receive a `NavWorld w`, but they snap every path point with the public `GroundHeight(x,z,y)` (`NavmeshWalkability.cs:224, 232`). That method re-reads the volatile field (`:75`).
  - `GroundHeight` itself reads `world` twice (`:72, :75`), so a single call can mix two meshes.
  - Lockstep therefore needs an explicit snapshot passed through every `IWalkability` call, not just a delayed `SetWorld`.

- **Why `DOTNET_EnableFMA=0` had no effect.** My understanding (not tested) is that .NET 10 merged the x86 ISA switches, so FMA now sits in the AVX2 (x86-64-v3) group. Also, `Vector<T>` stays 256 bits wide on AVX-512 machines by default.
  - On x64, the CPU split is therefore AVX2 versus older CPUs. AVX2 machines should agree on `Lerp` and `Matrix4x4`, which use fused multiply-adds.
  - ARM64 is the real outlier: `Vector<T>` is 4 wide there, which changes how Bepu bundles its work.

## Gaps filled

1. **No exact full-state transfer exists. This is the main gap the reports missed for lockstep.**
   - The save is lossy (`QuickSaveTests.cs:137`; `simulation.md:934-936`).
   - So a lockstep join, rejoin or desync recovery can do one of two things:
     - Restart every peer from a save. Everyone then loses combat, paths, AI timers, crime jobs and ragdolls.
     - Use a new serializer that captures everything exactly. It would have to cover hashed state plus unhashed state: `MedicalState.rollCounter`, `World.Saved`, the shared `PopulationData.Relations`, the navmesh's load history (point 2), `PathService.nextId` and in-flight async work.
   - Host-authoritative replication only needs a snapshot.

2. **Walkability depends on the order zones were loaded and unloaded, not only on which are loaded.**
   - `Unload` (`NavMeshService.cs:84-91`) removes the zone from `full` and from the world.
   - Neighbours whose kept polygons `ZoneKeptClosure.Spread` grew stay grown, because `Spread` only grows them (`ZoneKeptClosure.cs:16-20`).
   - Two peers with different camera or route histories can therefore walk on different meshes. Under lockstep, every load and unload must come from simulation state, identically on all peers from tick 0. (Read from the code; not tested.)

3. **The simulation's own concurrency is clean (from the code).**
   - Only three threads are created: `PathService.cs:32`, `PopulationSystem.cs:71` and `WorkerPool.cs:40`.
   - There is one serial-phase `ParallelFor`, which writes only its own items (`PopulationRoaming.cs:58`).
   - `LimbsChanged` is filled only from serial phases (`CombatSystem.Apply.cs:100`, `CombatSystem.Ranged.cs:640`) and drained in `Inputs` (`PopulationSystem.cs:119`). It also allocates ground-item ids, and does so in a fixed order.
   - A grep of Simulation, Data and Navigation found no mutable statics except `[ThreadStatic]` scratch lists and two Data UI/texture switches.
   - Dialogue state is held in `SortedDictionary`s (`DialogueSystem.cs:69-75`).
   - `CanTalk`'s dry run puts back `rolls` and removes any new `permanent` keys (`:251-254`), so the host's UI queries do not change state.

4. **Navmesh builds look order-independent on one machine (from the code).**
   - The per-tile `Parallel.For` writes only `meshes[tile]` (`ZoneNavMeshBuilder.cs:73-78`).
   - The foliage gather writes into a `ConcurrentDictionary` and is read back in zone order (`ZoneGeometryGatherer.Foliage.cs:15, 33-50`).
   - Whether builds are bit-identical across machines is still Unknown (DotRecast 2026.3.1 internals, and `MathF.Tan`/`Cos` in the slope thresholds).

5. **`config.TickRate` is not a source of desync.** It drives only the control clock (`WorldSession.cs:31-35`). The simulation's 1/30 s tick is fixed, and `WorldSession` throws if the two disagree.

6. **Speed scales the lockstep cost.** At 5× speed with 100 ms round trips the input delay must be at least 15 ticks. Any synchronous path or town build would stall every peer at once.

7. **Same-process evidence with ragdolls exists but has no recorded result.**
   - The duel builds a second `World` with the interactive systems, ragdolls included, and its pre-run uses synchronous paths.
   - It compares `StateHash` live and prints whether the lap "ended as measured" or "DIFFERS" (`GameHost.Duel.cs:101-125, 180-181`).
   - No docs record the outcome.

## Remaining unknowns

- How much synchronous paths (answered inline in the serial `Schedule`) and synchronous town builds (`BuildTown` inline in `SlowWorld`, `PopulationSystem.cs:253-256`) cost in a real town. No timings exist. This decides whether lockstep would hitch.
- Whether the navmesh, from cache or a fresh build, is bit-identical across machines.
- Whether Bepu agrees across SIMD widths, and whether glibc's or UCRT's float trig picks code paths by CPU.
- `StateHash` cost and character counts on a real loaded world, which also set the bandwidth for host-authoritative replication.
- Whether duel laps match their hash with ragdolls active.
- Whether `SaveLoader.Load` changes the `SaveGame` object it reads from.

**Net:** The evidence favours host-authoritative replication. Lockstep would need snapshot-passing walkability, simulation-driven navmesh history, synchronous services that would stall every peer, a new full-state serializer, and a trig replacement for cross-OS play. All of that comes on top of the per-player `PlayerState` work, which both designs need.