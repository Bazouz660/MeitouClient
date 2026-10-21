## 1. Capturing and loading a save in memory

**Short answer: yes. Every piece is a public API, but nothing bundles a whole save into one byte blob.**

- **Capture is already in memory.** `SaveCapture.Capture(World, PopulationData, SaveClock, LoadedSave?, SaveCaptureOptions?, SaveBuilder?)` returns a `SaveGame` object and touches no files (`src/Meitou.Simulation/Saving/SaveCapture.cs:69-129`). Disk is only involved when the host calls `game.Write(folder)` on a worker (`src/Meitou.Game/GameHost.Saving.cs:128-142`).
- **Turning a save into bytes:**
  - `SaveGame.AllFiles()` yields `(Name, SaveFile)` for `quick.save`, then the platoon files, then the zone files (`src/Meitou.Data/Save/SaveGame.cs:247-255`).
  - `SaveFile.ToBytes()` (`SaveFile.cs:55-60`) gives each file's bytes.
  - `SaveGame.Portraits` is a `byte[]`.
- **Turning bytes back into a save:**
  - `SaveFile.Read(byte[])` (`SaveFile.cs:22-41`).
  - `new SaveGame(SaveFile quick)` (`SaveGame.cs:45`), then set the settable `SavePlatoon.File` (`SaveEntities.cs:117`).
  - `Zones[coord] = new SaveZone(coord, file)`, then `Reindex()` and `CheckZoneFiles()`.
  - `SaveGame.Clone()` already does exactly this round trip through memory: *"A deep copy made by writing every file to memory and reading it back"* (`SaveGame.cs:156-170`). It is the template for a network archive.
- **What is tied to disk:**
  - `SaveFolder` only reads directories (`Directory.EnumerateFiles`, `SaveFolder.cs:23-32`).
  - `SaveGame.Load(string)` and `SaveGame.Write(string)` use a `_current<N>` working folder and an atomic rename (`SaveGame.cs:263-302`).
  - `GameHost.BeginQuickLoad` checks that `quick.save` exists on disk (`GameHost.Saving.cs:175`).
  - However, `SwapInSave(Task<SaveGame> read, ...)` (`GameHost.Saving.cs:191`) only needs a `SaveGame`, so `Task.FromResult(receivedGame)` would work. `SaveFolder` is not needed after the read stage.
- **Quicksave location:** `%LOCALAPPDATA%\Meitou\save\quicksave` (`QuickSave.cs:14-17`). The user's own save folder is never needed.

**File layout** (`docs/formats/save.md` "Folder layout"):
- `quick.save`: CAMERA, factions, wars, towns, research, biomes, decals and the platoon index.
- `platoon/<faction>_<n>.platoon`: one file per squad, holding the character records.
- `zone/zone.X.Y.zone`: one file per visited zone.
- `portraits_texture.png`: the portrait image.

Meitou writes a zone file for every zone in `World.VisitedZones` (`ZoneSave.WriteVisited`, `ZoneSave.cs:16-31`).

**Typical size:**
- The doc's sample has a 2.26 MB `quick.save`, platoon files of 6-120 KB and zone files of 2 KB to 1.4 MB.
- I measured (read-only) the original game's saves on this machine, `%LOCALAPPDATA%\kenshi\save`:
  - Early game, "123": 3.9 MB in 29 files.
  - Mid game, "Onton 5": 97 MB in 845 files, with a 19.7 MB `quick.save`.
  - Late game: "Siwa 102" is 277 MB in 4,094 files; "New 116" is 645 MB in 5,224 files, of which 2,938 are zone files.
- Meitou-written saves of a real game were not measured. How well they compress is unknown.

## 2. Runtime state a save does not capture

**Admitted in the docs and code:**

- **The tick counter.** A load requires `world.Tick == 0` (`SaveLoader.cs:45`), so every loaded world restarts at tick 0. `QuickSaveTests.cs:136` asserts `Assert.Equal(0, b.World.Tick)`.
- **Sub-minute game time.** `SaveClock(Day, Hour, Minute)` holds whole minutes only (`SaveModel.cs:8`). `World.GameHours` is not set by `SaveLoader`; the host sets it from `session.Clock.SetTime` (`GameHost.Saving.cs:229`, `WorldSession.cs:95`), and it is not hashed (`World.cs:156-159`).
- **The main list** (`docs/simulation.md:934-936`): *"Orders/paths, animation, combat engagement, wound-list/pain, base-game building state beyond locks and doors … ground items and game speed are not fully restored."* `QuickSaveTests.cs:127,138-139` confirms an empty `Medical.Wounds`, `OrderQueue` and `Path` after a load.
- **Ragdolls** (`simulation.md:388`): *"no ragdoll state in saves (a loaded downed character's ragdoll starts from the lying clip)"*. The luckyren gibs are *"not saved; a new world clears them"* (`docs/game/luckyren.md:32`).
- **Crime and jail jobs, restarted rather than saved** (`simulation.md:941-945`): arrests, cagings (the carry link), hunts and their rest timers, release, lock-up and warden rounds, the unreachable-cage memo, lock picking and pending crimes. Spawn bounties are not saved either.
- **Kidnapping** (`simulation.md:542-543`): carry links and the kidnap records themselves are not saved.
- **Town population pools:** *"the hour they last grew is not saved (set at load)"* (`simulation.md:161`).
- **War campaigns:** *"phase timers are not saved"* (`save.md:410`), and `CampaignInstance.CarryStart` is not saved (`simulation.md:211`).
- **AI:** only the platoon's `currentPackage` string is read (`SaveLoader.cs:247`). Blackboard fields are not in the save: contract end, `CommitUntil`, `NextPatrol` and `PatrolGiveUp` (`AiSystem.cs:350,731,892`) are rebuilt.
- **Dialogue:** the `repetition limit` cooldown is not saved (`docs/game/dialogue.md:241`). My inference: the `DialogueSystem` dictionaries `counters`, `lastUse`, `met` and `ambientAt` are in memory only.
- **Doors and buildings:** door `AutoOpened`/`LastPass` (`simulation.md:419`) and battery mode (`:763`) are not saved.
- **Production:** hauling trips (`:772,798`), prospecting progress (`CharacterTable.cs:235`) and shop stock (`:839`) are not saved. Attached-wall links are rebuilt (`BuildingRegistry.cs:35`).
- **Weather:** the fog fade (`save.md:178`). A sky transition in progress is snapped to finished (`GameHost.Saving.cs:152-167`).
- **Display-only state:** `PlayerState.Inspected`, `OpenContainer`, `DoorTarget` and `InCreation` (`PlayerState.cs:22-37`).
- **Commands deferred inside a tick** (retaliation, auto-aggro): the `World.deferred` list (`World.cs:240-246`) has no save path, so I infer they are lost.
- **NPCs made since loading:** wild squads and roaming stand-ins without characters are not written; *"the population makes them again"* (`SaveCapture.cs:468`).
- **Random numbers:** there are no RNG stream counters to save. Rolls are keyed on seed, entity and purpose (`WorldSettings.Seed` doc, `World.cs:41`). The seed is restored from the Meitou-only CAMERA key `meitou seed` (`SaveLoader.cs:37,47`). An original-game save has no such key, so the world keeps the host's `--seed` (`GameHost.cs:106`).

**Not saved and also not hashed:** `World.Saved` (the `SavedPopulation`), described as *"Not hashed: the platoons carry what matters"* (`World.cs:123`). It still holds mutable runtime fields per saved member and platoon: `Live`, `Gone`, `Dead`, `Position`, `StoredTick`, `Despawned` and `Touched` (`SavedPopulation.cs:23-59`). The town pools' growth is also not hashed (`simulation.md:163`). A lockstep desync in either would not appear in `StateHash`.

## 3. Is loading deterministic?

- **Live world vs. loaded copy: not equal, by design.** `QuickSaveTests.cs:137` asserts `Assert.NotEqual(a.World.StateHash(), b.World.StateHash())`. The docs say: *"Loaded hashes need not match captured hashes: compare carried fields and agreement between repeated loads"* (`simulation.md:936`).
- **Same save loaded twice: there is a test, with limits.** `A_world_loaded_from_a_quicksave_runs_the_same_whatever_the_thread_count_and_captures_again` (`QuickSaveTests.cs:144-177`):
  - It loads one in-memory `SaveGame` into two fresh worlds, with 1 and 4 threads.
  - Both get the same `FocusCommand`, `SelectCommand` and `MoveOrder`, then hashes are taken every 40 ticks for 240 ticks.
  - It asserts `Assert.Equal(hashes[0], hashes[1])` at line 174.
- **Limits of that test:**
  - It uses a synthetic town with only `StandardParts.Population | Player | Movement | Body` (line 23). There is no combat, AI, aggro, dialogue, ragdoll or production.
  - Paths are synchronous and the population builds inside the tick (`PopulationSettings.Background` defaults to false, `PopulationData.cs:200`).
  - It does not compare the hash right after loading, does not use a real-install save, does not compare two separate processes or machines, and does not reload from bytes. Both worlds reuse the same `save` object. Whether `SaveLoader.Load` changes that object is not verified, though `Capture` works on a clone (`SaveCapture.cs:60`).
- **Host saves, then host and client both load:** they should match only if all of these hold:
  - Both throw away the live world, because the host's own running world ≠ the loaded one (see the first bullet).
  - Both have the same data files (only reported, not enforced: `LoadedSave.MissingMods`, `SaveLoader.cs:52-55`).
  - Both set the same `GameHours`, from the clock.
  - Both use the same systems in deterministic mode.
- **The interactive game is not in deterministic mode.** `GameHost.BuildSystems(interactive: true)` sets:
  - `Population.Background = interactive && !IsDuel` (`GameHost.Saving.cs:60`). Towns are then *"built on a background thread … and taken in at the first tick after they are ready"* (`PopulationSystem.cs:21-22`, drained at `:107`). The tick that NPCs appear on depends on wall-clock time.
  - `SynchronousPaths = !interactive || IsDuel` (`:64`). The comment at `:63` states paths on a thread *"whose timing differs from lap to lap"*.
  - `population.ZoneGate = zone => nav.Walkability.World.Contains(zone)` (`:223`). Towns are held back until the navmesh has loaded asynchronously.
  - `PreparePopulation(cameraFocus)` changes the world outside `RunTick`, once per covered frame (`World.Loading.cs:8-28`, `GameHost.Transition.cs:112`).
- **Shared data:** `SaveLoader` writes the save's relations into the shared `data.Relations` (`ApplyRelations`, `SaveLoader.cs:15-16,183-185`). `AggroSystem` hashes that table only once a deed has changed it (`AggroSystem.cs:42,484-489`).

## 4. Load cost, background work, and loading in place

- **Load timings: no recorded numbers.**
  - The host logs `"made in {watch.ElapsedMilliseconds} ms"` (`GameHost.Saving.cs:261`).
  - Code comments say capture takes *"a few milliseconds"* (`GameHost.Saving.cs:14-15`) and `PrepareBase` takes *"tens of milliseconds"* (`SaveCapture.cs:53`).
  - `PrepareBase` clones the whole loaded save through bytes (`:60`). For a 20 MB `quick.save` plus hundreds of files that is likely much more than tens of ms. This is my estimate, not measured.
- **Load stages** (`BeginQuickLoad`, `GameHost.Saving.cs:170-184`):
  1. "read": `SaveGame.Load` on a worker.
  2. "build": `SwapInSave` on the frame thread. It runs `BuildSystems`, then `session.ReplaceWorld(systems, w => SaveLoader.Load(...))`, then restores the clock, weather (`weather.Restore`), blood decals and camera. It returns `NavmeshAround(leader)`, which waits for the navmesh of nearby zones and gives up after 15 s (`:275-291`).
  3. "finishing": `SettleNewWorld()` runs the first `RunTick` (`GameHost.Transition.cs:176-185`).
  4. Warm-up (`CoveredFrame`, `:102-159`): calls `PreparePopulation` each frame until the population, characters, terrain streamer, objects and foliage are ready. Then it waits for a 0.5 s smooth-frame run, or 5 s after content is ready.
- **NPC platoons are made later.** Saved NPC platoons become stand-ins that are *"made when a zone near them is active"* (`SaveLoader.cs:18-22,230-233`), and activation follows the single camera focus (`FocusCommand`).
- **The sky flight is optional.** `skyArrival` is created only when `UiMode == Meitou` and `config.InGameTransitions` is on (`GameHost.Transition.cs:52-54`). The loading overlay is always used (`loading?.Restart()`).
- **In-place loading:** `WorldSession.ReplaceWorld` is synchronous and atomic. If setup throws, the old world stays (`WorldSession.cs:100-123`). It must be called *"between frames or from a control tick's handler, never from inside a world tick"*. So an in-place swap is mechanically possible, but:
  - `SwapInSave` and `SettleNewWorld` are private to `GameHost` and assume the scene-load stages.
  - Skipping the warm-up means NPCs fill in on later ticks (background builds and the navmesh gate).
  - Host state is reset: `worldEpoch++`, `preparedBase = null`, the population and aggro references are swapped, and the door gate is reset.

## 5. How saves record the player faction and squads

- **Player faction:** `SaveGame.PlayerFaction` is *"the one with a `known` list, else the one whose id is `204-gamedata.base`"* (`SaveGame.cs:142-143`). `FactionCollections[0]` is *"the player's faction"* (`:28`).
- **Load side:**
  - `world.Player.Faction` is set to that faction's index; `Money` comes from CAMERA `PlayerMoney`; `SquadAi` comes from CAMERA bools (`SaveLoader.cs:60-64`).
  - Only platoons with `platoon.FactionId == playerFaction.Id && platoon.IsLoaded` become player squads (`:106-108`).
  - Each becomes a `Squad` with `Faction = playerIndex`, `Index` from the `squad index` int, `SaveName = platoon.Name`, and the shown name from `Record.Name` (`:195-207`).
  - Selection comes from CAMERA `selected_character` and `selected_characters<k>` (`:115-119`).
- **Capture side:**
  - `WritePlayerSquads` uses the single `data.PlayerFaction` (`SaveCapture.cs:234-235`).
  - It writes `SquadManagement.PlayerSquads(world)`, which filters on `s.Faction == world.Player.Faction && s.PlatoonId < 0` (`SquadManagement.cs:46-54`).
  - Each squad stays on its own platoon file (`squad.SaveName`), and `squad index` is written (`:257-270`).
  - CAMERA `Squads`/`Members`, selection and uniques are written with player flags (`:303-304,639-680`).
- **One player only:** `PlayerState` is a single object (`World.Player`) holding one faction, one money value, one selection and one active squad, and selection is hashed world state (`PlayerState.cs:4-21`). There is no per-player owner field anywhere.
- **Problem for a second player faction:** its platoons would go through `LoadNpcPlatoon`. With no placed `basetown` they fall to `LoadWildPlatoon`, or are dropped with the note *"its platoon stays in the save only"* (`SaveLoader.cs:240-244`).
- **How Meitou already extends saves:** extra keys that the original game ignores:
  - `meitou seed` (CAMERA string, `SaveLoader.cs:36-37`).
  - `meitou parent` (`save.md` "Player-built buildings").
  - `meitou kidnapped` (`simulation.md:542`).
  - the `meitou-nest:` instance prefix (`SaveCapture.cs:165`).
- **Suggested option (a design idea, not existing code):** keep one shared player faction. Add a Meitou-only PLATOON key such as `meitou owner`, and per-player money and selection in CAMERA. Then extend `SaveLoader`, `SaveCapture`, `PlayerSquads` and `PlayerState` to work per player.

## Implications for L vs S (my reading)

- **Under L**, a save works only as a full world restart. Every peer, host included, rebuilds a fresh world at tick 0. Paths, orders, combat, ragdolls, AI timers and job rounds are lost, and the population must run with `Background = false`, synchronous paths and a deterministic zone gate. The only existing proof that loading is repeatable is one synthetic test with a reduced set of systems.
- **Under S**, the in-memory capture → bytes → `SaveGame` route is enough for a client's initial snapshot or a late join, and the client never runs the simulation authoritatively. Its losses only affect what the client sees until replication catches up.