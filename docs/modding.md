# C# modding API (preliminary concept)

**Status: concept, not decided (2026-10-25).** Nothing here is implemented. It is Meitou's own interface; the
RE_Kenshi decision ([rekenshi](formats/rekenshi.md)) stands and its DLLs stay ignored. Managed DLLs run unchanged
on the Windows, Linux and macOS builds.

## Discovery

- A mod carries code when its folder (next to the `.mod`) has `meitou.json`:
  `{ "id", "apiVersion", "assemblies": ["Foo.dll"], "entry": "Foo.FooMod" }`. A mod may carry both `meitou.json`
  and `RE_Kenshi.json`.
- `ModInfo` reports it beside `HasNativePlugin`; the launcher shows which mods carry code.
- Load set: the enabled mods of `mods.cfg` (`ModList.ReadEnabled`) in order, filtered to those with a manifest.
  A disabled mod's DLL is never opened. Enabling and disabling take effect at the next launch, like data mods.

## Loading

- One non-collectible `AssemblyLoadContext` per mod, loaded once at startup. Resolution: an assembly already in
  the default context (`Meitou.*`, the framework, shared third-party such as Bepu) comes from there, so a mod never
  holds a private copy of engine types; anything else comes from the mod folder, so two mods may ship different
  versions of the same library.
- No unload and no hot reload: the systems are held by reference and registered delegates would keep a collectible
  context alive.
- Constraint: the game must stay a JIT publish (self-contained, never `PublishTrimmed` or `PublishAot`). The
  launcher's AOT is unaffected; it loads no mod code.
- Mods compile against the release's `Meitou.*.dll` and a small `Meitou.Modding.dll` contract assembly.

## Lifecycle

- `IMod.Initialize(ModContext)` in `Meitou.Modding`; the type comes from the manifest's `entry`, no reflection scan.
- Runs after the data load and mod merge, inside the wiring at `GameHost.Saving.cs` before `StandardSystems.Build`
  returns, in `mods.cfg` order (later mods see earlier registrations, like data overrides).
- `ModContext`: the merged `GameData`, the mod folder, a logger, a mod-scoped `Rng` purpose salt (hash of the
  manifest id), `Enqueue(SimCommand)`, and the registration surface below.
- A throwing `Initialize` disables that mod for the session and logs; it does not stop the game.

## Extension points (first version: simulation only)

- **Tick systems:** `AddSystem(ITickSystem, Anchor)` with `Anchor` = before/after a `StandardParts` member.
  `StandardSystems.Build` stays the single wiring authority and splices mod systems at anchors. `IStateHashed`
  puts a system's state into `World.StateHash`. Open: anchors from the start, or append-only first (simpler to
  keep deterministic across mod order).
- **Dialogue conditions and effects:** `DialogueSystem.Condition` is a switch on numeric codes today; mods would
  need a registry for codes above the vanilla range, referenced from FCS data. Open: conditions only, or effects too.
- **Commands:** console commands (`GameConsole`) and the mod's own `SimCommand` types, handled in its system's `Inputs`.
- **Custom FCS record types:** `FcsRecord` keeps unknown int types with their properties, so loading works; survival
  through merge and save is **Unknown** until tested.
- No UI or rendering hooks in the first version.

## Determinism contract

Mod systems obey the rules of [simulation](simulation.md) "Threading": parallel phases write only their partition's
`Next`, read `Previous` and the grid, send cross-character effects through `EffectBuffer`; randomness through
`Rng.Hash` with the mod's salt; no `World` access outside the tick (tick-ahead runs on `Meitou.Tick` during the
draw), outside input goes through `Enqueue`. Violations break golden hashes and any future replay. A debug mode that
runs a mod system twice and compares `StateHash` would catch most offenders.

## Persistence

Vanilla save files stay byte-exact ([save](formats/save.md)). Mod state goes in a sidecar file per mod in the save
folder, keyed by manifest id (`ISaveable.Write/Read(Stream)`). A save loaded without the mod keeps the sidecar; the
original game ignores it, so saves stay portable.

## Versioning

Tension: exposing `Meitou.Simulation` directly (every refactor breaks mods) against a stable contract (slow to grow,
the world ends up mirrored behind accessors). Choice: the contract assembly for lifecycle, registration and
persistence; mods reference `Meitou.Simulation` directly for `World`, `CharacterTable`, `EffectBuffer`. The manifest's
`apiVersion` is checked against the game's; a mismatch refuses the mod with a message. Compatibility is per game
version. Open: ship `Meitou.*` reference assemblies with each release so modders build without a game install.

## Trust

A workshop DLL is arbitrary code running as the user, as with RE_Kenshi. The launcher marks code mods and the game
confirms each new one on first run. No sandboxing: .NET has none, and claiming one would be worse than saying so.
