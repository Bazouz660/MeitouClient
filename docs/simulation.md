# Simulation

Engine contracts for `Meitou.Simulation`; game facts live in [game/](README.md#gameplay-research).
Source authority: `World*.cs`, `StandardSystems.cs` and each subsystem. Implementation choices
below do not claim original-game parity. Retain Verified/Observed/Unknown scopes in linked research.

## Time model

Control: 30 Hz real time, input/UI/camera continue while paused. Simulation: 1/30 s game time;
accumulate `realDt*speed`, speeds 0/1/2/5. Budget `6*speed` ticks per frame; excess is dropped and
`AchievedSpeed` reports slowdown. Control ticks precede simulation ticks; a speed change applies
to the entire frame. `WorldSession` rejects unequal world/clock tick lengths, owns snapshots,
clocks, input and camera, and disposes systems/services.

At player speed 0 (without a modal/loading pause), queued selection, held-item, inventory right-click/arrange
and open-shop buy/sell/close commands apply between ticks and republish snapshots. Other
commands stay queued; neither world ticks, game time nor simulation phases advance.

**Verified** original clock: 1200/11 seconds per game hour ([game-loop.md](game/game-loop.md)).
`BodySystem.HoursPerTick=11/36000`; medical dt units remain **Unknown**. Navmesh unload uses
real time (30 s); population unload grace uses game time (towns 60 s, roaming 10 s).
`RequestedPause` is independent of remembered nonzero speed. Render previous/current states
with simulation alpha; camera uses control alpha. Positions are float world coordinates;
rendering is camera-relative.

## Threading

Tick order: Inputs → Schedule → Think → Move → Act → ordered Apply → SlowWorld → Publish.
Inputs/Schedule/Apply/SlowWorld are serial; each implemented parallel phase has a system barrier.
`CharacterHot.Previous` is read-only; a partition writes only its own `Next` slots. Spawn/remove
only in serial phases after consumers finish; generation IDs prevent stale slot references.
Combat has separate double buffers swapped at the end of its SlowWorld, not at global publish.

Effects sort by `(target,source,system,sequence)`; spatial-neighbour iteration order is fixed.
Commands apply by tick and queue order. In-tick orders use serial `World.Defer`; deferred orders
precede host orders for their due tick. No concurrent host enqueue may alter that ordering.
Randomness: stateless `Rng.Hash(seed,entityKey,purpose,counter)`; changing purpose/salt changes rolls.
Worker partitions are `threads*2`, minimum 16 slots (game 128); one worker means caller execution.
Async paths, navmesh and population results enter at tick boundaries; arrival timing is not
repeatable in play. Determinism tests use synchronous services.

`StateHash` is ordered, includes slot/generation/free-list and hashed subsystem state, normalizes
negative zero, and is a diagnostic rather than gameplay work. Golden scenarios must agree at
1/4/16 threads before pins change; pins are platform-specific. Snapshots expose living-character
identity, appearance, transform, animation, faction/squad/player/selection and body status;
selected characters also expose skills, inventory and path. Host reads double-buffered active
zones within the frame after ticks. Null appearances draw markers.

## World model

Dependencies: Core ← Data ← Simulation ← Engine ← Game; Navigation/Rendering are sibling
libraries. Characters use hot double buffers plus cold objects; squads/platoons use sorted
registries. `(slot,generation)` IDs and the free list participate in hashes. Static typed data
views are built over `GameRecord` once; missing fields use documented defaults. Population shares
factories/database/relations and lazy body/combat/item assembly. Research owns original formulas.

## Systems and their order

`StandardSystems.Build` is the single wiring authority. Ordered systems:
Population, Player, AI, Pursuit, Movement, FirstAid, Body, Carry, Construction, Combat, Animation, Ragdoll
(only with `StandardSystemOptions.Ragdolls`), Feed, Retaliation, Aggro, Flee, Crime, Arrest, Locks, Trade, Dialogue (Arrest and
Locks come with the Crime part), with Production right after Construction. Tests selecting subsets retain this relative order.
Population spawns before later SlowWorld work; player selection precedes movement orders;
AI writes tasks before movement; pursuit schedules paths before movement answers them.
First aid reads patients in its own Act barrier and applies treatment during serial commit.
Body handles medical ticks before combat (`TickMedical=false`); carry sees knockouts before
combat and drops carried targets before attacking. Combat swaps before animation/retaliation;
crime sees deeds and final positions/carry links. Game combat uses `SelfApproach=false`.

## Population

`PopulationSystem`, `SquadFactory`, `CharacterAssembly`, `GeneratedAppearances`: activate zones
around focus, gate spawning on navmesh readiness, create residents/bar/roaming/wild/nest squads,
and assemble race appearance, gear, stats and bodies. Counts, roles, offsets, caps, spawn weights
and world-state gating follow [population research](game/factions-squads-towns.md).
Missing references become `SquadPlan.Problems`; world states are answered live (`WorldStates`, see Campaigns under the off-screen tier). Residents are housed by the original's building rules with housemates and
default residents ([town residents](game/factions-squads-towns.md#town-residents)); prisoners/cages remain
incomplete. A town that unloads turns each squad it made into a pinned platoon holding a `StoredSquad`
(`PopulationStored.cs`): each living member's own cold state (name, look, body, kit, bounties, crime marks) with its place,
cleared of path, AI, carry, treatment, combat and stealth state, plus the squad's home building, shop, purse and AI package.
The town then holds squads and makes none of its own; the platoon is made of the same characters when the town's zone is
active again and leaves with the town's grace. This is the original's model: residents are persistent platoons kept as their
characters ([persistent platoons](game/factions-squads-towns.md#1-object-model)). Saves write stored squads as the town's platoons (`sqt` 1). Any platoon's
activation waits for the navmesh gate of its town's zone (a traveller: its own). A town made once (its pools' `started`) makes no residents again; its
badly hurt squads are made afresh within its pools (see "Off-screen tier"). `ActiveZones` is double-buffered. Zone loading/spawn planning stays off the
simulation thread; publish results at boundaries.
`World.PreparePopulation` queues nearby residents/wild squads, activates existing nearby roaming
platoons and drains completed builds during loading. It publishes matching snapshots without advancing
ticks, game hours, AI, movement or roaming timers; readiness includes navmesh-gated sites.

### Unique characters

The original's rules: [unique characters](game/factions-squads-towns.md#unique-characters). `World.Uniques` (`UniqueCharacters.cs`) is the
original's table (CHARACTER → claimed, live character, state 1/0/2, player flag), changed in serial phases only and hashed once it has an entry.
- **Create gate** in `SpawnSquad` (serial, so two squads built at once cannot both make one): a free unique human member is claimed and made; a
  claimed one gets its first `unique replacement spawn` (look rolled then) or nobody, and the first member left leads. Animals are not gated, as
  the original (unique pets such as Settler's Pup can repeat). The player's and sandbox squads skip the gate.
- **Handles**: bound whenever a unique enters the world (made, stored or saved squads re-made), which also claims it (engine choice: the original
  records only the handle there); dropped when a squad is stored or a corpse removed.
- **State**: deaths in the combat commit (player flag when the killer is the player's); a refresh at every population look marks the dead, the
  caged (player: the cage owner is the player's), the alive carried off by a non-ally, and frees one held or carried no more (engine summary of
  the original's ally/town tests). Off-screen losses (Meitou fights) die.
- **Release**: a squad leaving for good without being kept (Faithful despawn, a wild site unloading, a town squad remade by regeneration) frees its
  living uniques so its template makes them again, only when the entry is that character's (Meitou: knocked-out but alive counts as living); the
  dead stay claimed. Town squads stored on unload keep their claims.
- **Seed**: at the first population look, the uniques of each template's first `prisoners` template are imprisoned and unclaimed (kept when a
  save already has them).
- **Save**: CAMERA `usedUniques*` as the original (claimed entries; prisoners derived again). Handles are the written character's for player
  squads, else the loaded save's, else null (NPC handles are not tracked back from the writer).
- **Not built**: FACTION `special squads` (the original never spawns them), the import rules, `UniquePlatoon` missions.

### Off-screen tier

The original's rules: [off-screen world](game/factions-squads-towns.md#off-screen-world). It keeps only persistent platoons (residents) and
campaign squads off screen, moves them in straight lines (roads for long trips), never fights or heals there, and drives the rest by clock-based
budgets. The tier follows it, with Meitou choices marked.

Six Faithful/Meitou switches (the fifth, campaign inheritance, under Campaigns below; `PopulationSettings`, live on `PopulationSystem`; Advanced settings (the fifth on the Fixes page), Meitou on by default; `PopulationOffscreen.cs`):
- **Squads keep their people** (`KeepRoamers`). Meitou: a roaming squad the population made keeps its characters as a `StoredSquad` from the
  first time it leaves, travels on as them and comes back as them (moved along with its stand-in); saves write it as a new platoon (`sqt` 2,
  `persistent` false). Faithful (the original): a platoon that is not a town's own is despawned as it leaves, and a stand-in out of range for
  60 s is despawned (the orphan timer); a save's despawned platoon leaves the save; loaded towns refill their pools. **Observed** (headless):
  Meitou brings back the same characters; Faithful replaces them from the pool, and save 2 loses 15 of its 15 travelling platoons in six minutes.
- **Fights far away** (`OffscreenFights`). Meitou: every 2 s, an unloaded travelling or wild stand-in (not a town's own, not the player's) met
  by an enemy (`AggroSystem.IsEnemy` either way) within 300 fights the first such in id order; strength per member able to fight is 10 + the
  mean of melee attack, melee defence and toughness (25 for a squad never made); A wins with S_A² / (S_A² + S_B²); the loser is despawned and the
  winner loses its weakest members in the share min(1, (S_loser / S_winner)²) / 2. Faithful: none.
- **Healing far away** (`OffscreenHealing`). Meitou: when a kept squad comes back, `MedicalState.Recover` steps the hours it was away in one go:
  wounds treated and closed, flesh healing at the part rate, stun gone, blood back at the resting rate, a KO ended if vital parts allow; no
  hunger, degeneration or death. Faithful: it comes back as it left.
- **Wild squads stay** (`KeepWild`). Meitou: a wild site (sector or nest) that unloads keeps each squad with someone alive as a `StoredSquad`
  (nothing leaves a nest's pool) and makes them first, where they stood, when it loads; they count against the site like a save's wild
  platoons, heal as above, and a save writes them as wild platoons. Faithful (the original): dropped, a nest paying for its hurt squads, and a
  save's wild platoon despawned on leaving (the orphan timer). **Observed** (headless, synthetic sectors and nests): Meitou keeps 12 squads and
  brings back a renamed character; Faithful remakes the squads from the seed.
- **Towns keep their schedule** (`ResidentSchedule`). Meitou: a town squad that comes back after at least an hour unloaded (`ScheduleAfterHours`) is
  marked `Squad.Resume`; each member's first think picks where its plan's walk ends (its bed at night, the counter, its node, seat or gun; a ring of 4 by member index, walkable only), the serial step moves it there (Movement would overwrite a move from the parallel think) and
  the act-out goes on from there, so the town looks as if it had lived through the time; nothing runs while it is unloaded. Faithful (the
  original, which runs nothing for residents off screen): where they stood when it unloaded.

Implemented (`PopulationRoaming.cs`):

- **Step**: at every population look (15 ticks), every unloaded platoon that is not a town's own steps by the look's game time in a parallel pass
  (`World.ParallelFor`, 64 stand-ins a job): the Faithful orphan timer and its travel. A step changes only its own platoon, so the result does not
  depend on the order (the roaming hashes agree at 1, 4 and 16 threads). Despawns, activations, loaded squads, pools and fights follow serially in
  id order. Town squads do no off-screen work (the original's GOHOME at home).
- **Speeds**: per job as the original (GOHOME 55, TRAVEL_TARGET 25, TRAVEL_TARGET_FAST 60 or 90 beyond 25000), the engine's travel stand-in (job
  NONE) 25 or 90, all capped at the original mover's 70 (`StandInSpeedCap`); straight lines. A short-range patrol keeps walking at 25 (the
  original's stands still). Destinations stay the engine's (towns, patrol points near home): the original keeps no roaming squad off screen,
  and saves give no usable target (**Observed**, 4 owner saves: `mission target` always type 0, `target town` on 1-3 platoons, `mission town`
  mostly a TOWN).
- **Roads** (`RoadNetwork`, `PopulationData.Roads` from the level's ROAD records; the same instance serves on-screen long routes,
  [pathfinding](game/pathfinding.md)): a stand-in, or a campaign's leader, takes the roads by the
  original's rule ([roads](game/factions-squads-towns.md#off-screen-world)), worked out when its goal changes and walked point by point; a
  campaign's followers go straight for their leader (engine choice: their goal moves every look). Hidden roads count. Engine reading: the
  join pull is clamp(w) × the point's forward distance. **Observed** (base data): 2109 roads, built in 33 ms; a route takes 0.5-16 ms; 193 of
  200 random town pairs go by road at preference 0.6; about 10 % of roads have a `length` well under their drawn length (0.17-0.44 ×, median
  1.00), which the original's cost uses as is, so a faction with preference 0.3 still takes the Hub-Stack road (walked 34000 vs 16900 straight).
- **Hand-off**: a stand-in that travelled comes back on the nearest walkable point (`WalkableSpots.Nearest`: rings of 4 to 128 units, eight
  directions each); its kept characters keep their offsets round it, each moved to a walkable point within 32 if needed, else onto it. A town's
  own or wild platoon, or one that did not move, keeps its characters' exact places.
- **Cost** (**Observed**, headless, save 2 with 2000 extra travelling stand-ins, warm): the step 0.08-0.1 ms per look at 1 or 8 threads (too little
  work for the threads to pay), pools and fights 0.23-0.26 ms per look (fights run every 4th look), about 0.03 ms a tick in all; save 2 alone
  0.08-0.1 ms per look. Not measured in the viewer's bench harness, which does not run the population.
- **Pools** (`PopulationPools.cs`): every town and nest has the original's two pools (residents `pop`/`popd`, roaming `pop2`/`popd2`),
  read from the save's town and nest states and written back, with `started`, `ded`, `tod`; the hour they last grew is not saved (set at load,
  as the original). Every 2 s (60 ticks) they grow 0.21818 per game hour up to the desired max (roaming only once residents are within 1 of full), for every
  town with pools (the save's from the first look, a new one once made) and the save's nests from load (a rolled nest from its first load), loaded or not. Growth is not state-hashed (as the population's other site state).
  - **Towns**: one made from its data counts its resident squads (those with a home; bar squads not, engine choice after the census) into `pop`
    and `popd` and is `started`; a started town never makes residents again, even with every squad dead (it holds what is left). Town roaming
    pools stay as loaded (0 in every base save) and `RoamingShare` still caps roaming squads (see the census conflict in
    [off-screen world](game/factions-squads-towns.md#off-screen-world)).
  - **Regeneration**: on a town's load, each of its resident squads (platoon id order; of the town's faction, engine choice after the census) that lost ≥ 60 % of its spawn size,
    while the losses fit `pop` less the living resident members so far, comes back made afresh from its template at its place and home, as a
    new kept platoon (the old one's characters are dropped; a save's leaves the save). A template whose members are all by chance gets up to
    8 rolls. **Observed** (headless): new game, Hub squatters 1 of 3 left → 2 fresh; save 2, Squin's Town Guard 2 of 5 → 4 fresh; both the
    same after save and load.
  - **Nests**: the pool starts at 0.7 × `nest resident population` (or the save's `pop2`); a load fills it to `floor(pop2)` members; every 2 s
    a loaded nest with room makes one squad (rolls keyed after the load's), unless a player character is within 1250, then it only weighs.
    An entry must fit the free budget, except while the pool is empty with more than 4 free (the Fog Deathyard's `Fog hive nest`, expected
    size 1 + (12 + 24) / 2 = 19, against a budget of floor(0.7 × 25) = 17). A roll that finds no entry (all weights 0 or none fits the free budget) with no squad of the nest left alive sets `ded` for good, `tod`,
    and posts "<nest> has been wiped out" (engine choice: "intact" = someone alive); so does a pool spent below one member with no one alive. A squad that lost someone takes its living out of `pop2`
    when the nest unloads. Dead squads lying there still count against the template weights. **Observed** (headless): a swamp raptor nest
    (N 20, pool 14) refills an 8-duck squad after its two squads die; drained to 1 with all dead it is wiped out and saved with `ded`, `tod`,
    `pop2` 1.008, `popd2` 20.
  - **Homeless squads** (`PopulationHomeless.cs`; [original rules](game/factions-squads-towns.md#off-screen-world)): every 2 s a squad whose
    leader patrols (a PATROL goal outside a patrol-class package) walks to its patrol point (its home centre, homeless radius) and picks the
    next within 500. A squad belongs to the sector it stands in: one that walks into an unloaded sector leaves the world (after the roaming
    10 s grace outside the active zones, ≥ 1382.4 from every player character), kept there with
    `KeepWild` up to that sector's count, else dropped. The area tick refills the loaded sectors, all taken as one island (engine choice).
    **Observed** (headless, Fog Islands, one zone loaded): a fogman squad walks a sector a few minutes; leaving, a fresh one is made.

Implemented (`PopulationCampaigns.cs`; [original rules](game/factions-squads-towns.md#campaigns),
[save](formats/save.md#war_savestate-type-9-faction-campaigns)). Almost all campaigns aim at player towns (outposts), so nothing rolls until
the player has one; requests (victory/loss chains, ally help) can follow.

- **Data** (`Meitou.Data`, `CampaignData` from FACTION_CAMPAIGN, cached per inheritance mode): ranges × 10, `repeat limit` × 24, weighted
  trigger lists, phase packages. Fourth switch, **Raids as written** (`CampaignAncestorsOnce`): Meitou applies each ancestor's list entries
  once; Faithful twice, as the original (doubled weights and trigger chances).
- **War manager** (serial, in faction order at the population look; every rule reads the game clock, so the cadence does not matter): the
  6 h roll, the chance with the linear falloff and `raidFrequencyMult`, targets among `World.Buildings.Towns` (biome groups, no-go zones;
  slot 70 and Town +0x128 taken as passing, tech level as 0: engine choices), requests with due times, redirects and retries, the
  `trigger player ally` help, and DIALOGUE_LINE `trigger campaign` (`TriggerFromDialogue`, keyed off the speaker and tick). Rolls
  are keyed off the world seed, faction and roll hour, so a run repeats. The state hash includes the managers only once one has started or
  queued something, so worlds without a campaign hash as before.
- **Squads**: required men = int(`num forces` × `raidSizeMult`); the `special leader`'s platoon (or the template it leads, made near the
  faction's town), then for the 4 own towns nearest the target their free roaming platoons, then new unloaded stand-ins from `squads to use`
  within 700. Engine choice: homeless and a town's own platoons are not drafted. After the campaign a platoon goes home as a roaming squad:
  Meitou (`KeepRoamers`) with its people, Faithful under the normal despawn.
- **Phases** off screen as the original (0.8 × radius arrival, 4 / 12 / 1 h, the 20 % rule, 3 h removal); the leader travels to the target
  and the others to it at the `travel speed unloaded` speeds (serial, outside the parallel movers). With `OffscreenFights` (Meitou) campaign
  stand-ins fight like other travellers and a squad lost there counts as not intact for the 20 % rule; Faithful: an unloaded assault wins at 4 h. Engine choice: a
  campaign with no platoon left is defeated. End effects: the messages and the trigger roll or the 40-96 h retry; nothing else changes.
- **Loaded**: campaign squads are platoons the AI does not run, so the campaign drives them; the phase's package is what the next population
  look does with `Phase` (the original installs it at the next AI update). Column (`GatherAndMove` as the original): the leader's trail is
  laid each look and follower i heads for its point i × slots × 150 / N back (engine choice for the Unknown: the trail point, also off screen);
  loaded followers jog, the leader marching out keeps its `travel speed loaded` (WALK walk, JOG run mode, RUN and GROUPED free, NO_SPEED_CHANGE
  as it was) while every intact platoon is within 6000, else it waits within 8294.4 of the target or walks. They fight through the normal aggro;
  arrival, timers and the 20 % rule run. **Observed** (headless, Dust Bandits, 4 squads of 49 at `raidSizeMult` 4, camera on the leader): the
  followers keep 190, 380 and 470 behind a walking leader (the rule's 187, 375, 562).
- **Assault**: in phase 1 a loaded campaign squad also fights (`Squad.War`, read by the aggro) everyone not of its faction in the target town when its
  `pt2` package is a Package_WarAssault (20, 32), else the target faction when the campaign is hostile. Looting is not built. A cannibal assault (class 32) goes to phase 2
  when three of its members carry someone or 15 game minutes after the first did (`CampaignInstance.CarryStart`, not saved); kidnapping: [below](#kidnapping).
- **Announcement**: a loaded leader in phase 0 says `dialog announcement` once (camera and a target-faction member within 2500): its lines are said as speech bubbles 2 + 0.12 × length seconds apart, or DA 12's wait, and their `interrupt` makes the next talk
  with it the negotiation, also when its faction is hostile. After arriving it waits for the announcement and then while the negotiation is
  pending, at most 1 game hour (engine choice for the Unknown event-2 state); off screen it goes on. Dialogue DA 37 sets phase 1 (2 without a
  pt2 leader package), DA 38 phase 2; DA 39 (phase 3, unhandled in the original, unused in the data) is not built. **Observed** (headless, real
  data): Dust Bandits "Invasion with demands" shout their four lines 20 s apart; talking to the boss opens the negotiation; paying takes 2000
  and sends them back (phase 2), refusing starts the assault (phase 1, the squad's targets set), stabbing him sends them back.
- **World states** (`WorldStates`, `PopulationSystem.States`): WORLD_EVENT_STATEs answered live by the original's rule
  ([world states](game/factions-squads-towns.md#relations-and-world-states)) from the unique table and the relations; the squad factory,
  wild sites, towns and campaigns all ask it. A population given its own `IWorldStates` (the pinned tests) uses that instead.
- **Save**: WAR_SAVESTATE read and written with the original's keys (unknown keys kept; numbered keys past the new counts removed); a
  characterless campaign stand-in is written as a PLATOON with content file `platoon/` so `p<i>` resolves; a loaded campaign platoon travels.
  The phase, fight, arrival, announcement and carry clocks are Meitou keys ([WAR_SAVESTATE](formats/save.md#war_savestate-type-9-faction-campaigns)), so
  a loaded campaign goes on where it was (the original restarts its phase); the trail is laid again, the announcer is not kept.
- **Observed** (headless, real data, `raidFrequencyMult` 1000): Dust Bandits "Invasion with demands" against an outpost 1500 from Dust King
  Tower gathers 1 squad of 12 for 10 men, arrives after 0.28 h, survives a save and load in phase 1 (campaign, id, phase, squads), wins at 4 h,
  is removed 3 h later and queues its follow-up. The 9 owner saves' 103 war states each come out with the same keys and values.

## Player

`PlayerSystem` / Orders: selection, stop, move/queued move, attack, treat/rest/medic, carry/rescue,
loot, stances and squad toggles are commands. Resolve empty-ID orders against current selection.
New-game start data seeds squad/appearance/gear/money; the start's characters are then remade in the
character editor while `PlayerState.InCreation` ([characters](characters.md#character-editor)); original start
relation/research semantics remain partly unknown. Picking/HUD are host responsibilities, never simulation input.
Squads: `SquadManagement` orders the player's squads (`Squad.Index`), keeps `PlayerState.Squad` the active one and applies the squads screen's
commands with the original's limits ([Squads tab](game/ui-screens.md#squads-tab)); the snapshot's `PlayerSquads` lists them for the UI.

### Order toggles

Passive/Hold/Block/Taunt/Jobs state belongs to the character (medic and rescue are job-list entries, [job lists](#production)) and relevant
systems. Explicit player commands and automatic retaliation have different eligibility;
Idle players self-defend unless Passive, and Hold prevents pursuit beyond attack reach.
New player characters start with Jobs and Ranged enabled. Jobs gates the job list and automatic rescue (the medic job runs regardless, as the original); direct orders still run. Saved toggles override
defaults, including explicit false values. Jobs is GAMESTATE_AI `jobs` ([perma-jobs](game/ai-tasks.md#perma-jobs)); Meitou's older `meitou jobs enabled` is read, not written.
Sneak: sneaking speed cap and clips, quieter, smaller view factor; a hostile looker sees the sneaker only once its
spot meter fills ([perception](game/perception.md#spotting-a-sneaker)); the exposure feeds stealth XP.
Ranged (tooltip, **Verified** in `main.pot`, mainbar.cpp:1279: "prioritise ranged attacks, melee will be a last resort"): a
crossbow carrier takes the crossbow at any distance and melees only when dry or a melee attacker is on it; off, the distance
rule of `ChooseMode` applies. Taunt (mainbar.cpp:1273: "draw more of the enemy to attack him"): a hostile looker picking a
new fight prefers a visible taunter over any non-taunter (nearest first); its squad joins as usual. Fights already under way
are not retargeted; a sneaking taunter does not count. Both rules are engine choices; the original's are **Unknown**.
New games select the squad's first member. The game host also selects the first member after
loading, before publishing the initial snapshot; the save reader itself retains saved selection.

## Movement and paths

`MovementSystem`, `PursuitSystem`, `IAgentWalkability`: schedule/query asynchronously, publish at
boundaries, follow ordered path points, steer/separate using previous state and project to ground.
New move orders preserve reusable paths; attack pursuit reschedules target paths. Turning can
hold translation while facing changes. Original speeds/encumbrance and research are in
[pathfinding.md](game/pathfinding.md); footprint clearance/water costs come from nav queries.
Heights use the character's (or clicked) Y to pick stacked floors; long paths have a provisional tail.
Body contact (engine choice): standing bodies are solid over `BodyShare` 0.6 of the footprint (2.4 for a human). After its step, each
character takes half the overlap with each separation neighbour's predicted position (last position + last velocity × dt), so a walker
shoves a stander aside; one in a bed or cage does not give, lying and carried bodies are not in the way, a character closing on its spot
is not stopped, and an unwalkable result falls back to its own step.

## Navmesh

Own DotRecast builder/cache (`Meitou.Navigation`), not Havok runtime. Default cells 2, height 1,
48-cell tiles, agent height 18/max climb 5; ground/water/door areas. A* checks portal clearance
(radius per wall end, default radius 4), water factor and doors. `NavAdapter` exposes simulation interfaces;
`ZoneGate` holds population until ready. Collision and world placement rules:
[collision.md](formats/collision.md), [pathfinding.md](game/pathfinding.md).
Cache key: the zone's building hash, the build settings and a content hash (the data files in load order, `seeds.def` and the
height, blend, biome and overlay maps by size and write time, the DotRecast version). Unloading a zone skips its queued build;
a running one is still cached but not published, and its task is cancelled.

## Animation

`AnimationSystem` produces named layers/weights/times; `AnimationLibrary` supplies race clips,
lengths, masks and selection data. Movement, held care/lying clips, melee/ranged actions and hit
reactions use researched animation rules. Root motion advances with clip time; relocation keeps
separation by footprint radii and drops blocked components. Rendering consumes snapshot layers;
shape/skinning belongs to the character renderer. [animation.md](animation.md).
`PursuitSystem` sets `CharacterCold.CombatRunning` by the original's rule (body gap over `CombatTuning.RunGap` 90, off under
`FootworkGap` 70, on while the target flees; footprints stand in for the radii, the navmesh-blocked chase is not modelled). Running, a
fighter keeps the plain walk/run clips, full speed and the drawn weapon; otherwise it takes the combat clips and the signed speed, capped
at `FootworkSpeed` 18 ([combat](game/combat.md#combat-stance)).

## Bodies

`BodySystem` / `FirstAidSystem`, `Meitou.Data.Gameplay.Bodies`: race/stat/part factories,
medical tick, blood/hunger/parts, knockout/death, healing, limb state, move speed/encumbrance and
moving XP. Combat hits use the same body state; avoid double medical ticks. Treat/rest/medic
orders find eligible still patients, walk to reach and apply aid serially. Medical time scale
is explicit and uncertain; [character-stats.md](game/character-stats.md) owns original rules.

Limbs: `HealthPart.Limb` / `Replacement` / `ReplacementItem` per slot; saves round-trip them ([save.md](formats/save.md#limbs-medical_state-limbs)).
A severed limb queues (character, limb) on `CharacterTable.LimbsChanged`; between ticks the population system rebuilds the look
(`Loadout.Limbs`: stump or prosthetic mesh, natural limb hidden) and drops the race's severed-limb item (Ground items). Prosthetics are
fitted and removed by holding them onto or off the limbs window's slots (a `*_replace` section takes a LIMB_REPLACEMENT of its side on a stump; lifting a fitted one leaves a
stump; [held item](#items-and-eating), rules in [character-stats.md](game/character-stats.md)) or by a right click (`QuickMoveItem`).

Ground items: `World.GroundItems`, loose items: the save's (zone `1-itemlist`, read and written by `Saving/GroundItemSave`,
[save.md](formats/save.md#zone-files)) and the severed limbs combat drops; at most `GroundItems.Limit` dropped in play (default 1024, up to 4096 by the Advanced setting "Dropped items kept"; the oldest of those goes; read ones
are not capped); published with a revision. A dropped limb lies 5 units to its side of the character, an item dropped from the inventory 3 units ahead, both at a random
yaw (the original drops them and lets physics settle them); the host lays a dropped item flat (`GameHost.LaidFlat`, engine choice: its mesh bounds' thinnest axis up, lowest corner on the
ground, so a weapon, modelled along +Y, lies on its blade's flat); a read item is drawn by its saved position and whole rotation. The host adds each as a runtime world-object
placement of its record's `ground mesh` / `mesh` (`GameHost.GroundItems`, `WorldObjectLayout.Item`). A right click on one (within 24 px of its
projected middle, pointer `pick_item`) gives `PickUpItemOrder`: the nearest able selected character walks to it and, within 15 units
(`PlayerSystem.PickUpReach`), puts it in its inventory (`Inventory.Add`); what does not fit stays on the ground ("Not enough room in my
inventory"). Another order or a stop ends the walk. A picked-up read item keeps its ITEM record's keys in its inventory record. Engine choices.

## Items and eating

`Inventory`, `ItemFactory`, `FeedSystem`: grids, stack limits, backpacks, quantities/charges,
load and food provision. ITEM `stackable` is an integer, not boolean; gear stacks cap at one.
Transfer/split/top-up through inventory APIs. Food/auto-eat uncertainty and container bonuses:
[character-stats.md](game/character-stats.md), [economy.md](game/economy.md).

Inventory moves validate snapshot identity, bounds, occupancy, stack limits and equipment
type/armour slot. Weapon sections are 10×2 (back) and 7×1 (hip); other equipment slots hold
one compatible item (the belt: an ITEM with `slot` 14, the lanterns, [lights](formats/lights.md#carried-lanterns)). These are engine constraints, not a complete original-game rule set.
Backpacks move intact between pockets and their attachment slot; a bag goes inside a bag only empty. Equipment moves refresh worn appearance and combat gear; pocket armour
does not grant worn bonuses. Race restrictions remain absent. `InventoryCommands.Arrange` and `Inventory.AddTo` follow the original's ARRANGE and auto-add; a limited storage's
stacks start from 1 (`Inventory.Limited`). Rules: [holding an item](game/ui-screens.md#6-inventory-trade-dialogue-build).

Held item (`Items/HeldItems.cs`, part of `PlayerSystem`): `PlayerState.Held` is the item on the mouse with its origin (window `ItemOwner`: a character, the open trade's stock
or a site; its root inventory, section and cell) and the character holding it. `LiftItem` takes it out (the whole stack or one unit; a fitted prosthetic comes off; a machine slot's
units are made into an item), `PlaceHeld` places, merges or swaps it (the swapped item becomes the held one), `GiveHeld` adds it to a character in range, `DropHeld` puts it on the
ground (own windows only), `ReturnHeld` puts it back (old cell, else `Inventory.Add`, else after arranging, else the ground). Stock goes only to the customer and is bought when placed
(`TradeSystem.AffordHeld` / `PayHeld`, a stolen sale's `CaughtSelling`); out of the open loot it is marked stolen as a loot is and may butcher the body (`CarrySystem.TookHeld`); out of
another faction's container it is marked stolen. It goes back each tick when its window closed or its holder is down or gone (`KeepHeld`) and before a save (`World.ReturnHeldItem`,
`SaveCapture.Capture`). Hashed (record and quantity), not saved; the snapshot publishes `Held` and the serial of the last held command applied (`HeldSerial`).

## Combat

`CombatSystem`, `CombatDatabase`, `Fighter`: attack scheduling, equipment/techniques, stance,
block/dodge, armour and per-part damage, stun/KO/death, combat XP. Read previous combat state;
ordered effects apply hits. `TickMedical=false` with BodySystem, `SelfApproach=false` with pursuit.
Original formulas, constants and unresolved technique terms: [combat.md](game/combat.md).
Hits, pauses and initiative follow combat.md "Hit reactions" / "Pacing"; the combat system picks the stumble
clip (through the animation system's chooser) and holds the fighter for its length. Engine choices: a miss waits
0.2 s; the first attack after an order waits 0.15–0.6 s; the initiative roll is drawn per pair and tick so both
sides agree, and the loser holds off for 1 s; a target that would not swing back (stumbling, defending, not fighting, Block or
Passive order) is simply attacked; one that lost the roll to a fighter becomes its target; a defence that starts late
joins its clip partway so the passed roll counts when "Late dodges and blocks" is on (Advanced setting, a balance
change: Faithful off, Meitou on);
attack slots go to the nearest fighters at the target (Advanced setting "Attackers per target", 1-5: Faithful 1,
the CONSTANTS value; Meitou 3), the rest wait instead of circling; the block break-out ignores the 10% lower bound; only a heavy bolt staggers.

### Ranged

Crossbow reload/steady aim, skill spread, bolt flight, ammo, obstruction and friendly hits are
simulation state; `Schedule` advances bolts. Targets without medical bodies do not stop bolts. Bolts and the line of fire stop at the
terrain heightmap and the buildings' collision triangles (the ragdolls' environment, `SolidHits`), not the walkable surfaces (a roof over
the shooter counted as ground). A building is there once its zone's collision has been read on a worker; until then bolts pass it, which
breaks determinism for those first shots.
Ranged state is hashed when used. Weapon/turret research: [combat.md](game/combat.md).

### Animals

`ANIMAL_CHARACTER`, animal body/stats/size/fists and creature techniques come from typed data;
animals use faction hostility and carrying/damage rules where eligible. Unarmed humans use
martial-arts category 5. [animals.md](game/animals.md), [characters.md](characters.md).

### Hostility and relations

`AggroSystem`, `RetaliationSystem`, `FleeSystem`: previous-position sight/hearing, faction
relations, shared targets, idle-player self-defence, pursuit leash and return to routine.
Orders defer to the next tick. Relations are live state and deed/witness effects have distinct
paths; preserve current ordering. Original threshold/sense/retreat facts:
[perception.md](game/perception.md), [AI](game/ai.md), [factions](game/factions-squads-towns.md).

## Carrying, rescue and looting

`CarrySystem`: serial links, pickup/put-down, dropping on KO/death/attack, rescue jobs and inventory
looting. Carried bodies follow the carrier and affect load. Loot can raise witnessed theft and
stolen ownership marks. [carry-loot.md](game/carry-loot.md). Loot window (engine choice):
`OpenLootOrder` walks the looter over, then `PlayerState.OpenLoot` publishes the body's whole inventory
(`WorldSnapshot.OpenLoot`, the body's `ItemGrid`: grids, bag and worn gear); items are held across with the mouse ([held item](#items-and-eating)),
or right-clicked over (`TakeLootItem` / `PutLootItem`, `InventoryCommands.MoveInto`; taking counts as a loot for theft, locked-on items stay, moved gear re-dresses both); it closes as a container does, or when the
body comes round or is lifted. It also runs the stealth knock-out
(`StealthKo` task, reusing `CarryTarget`/`LiftTime`): [combat](game/combat.md#stealth-knock-out).

## Ragdolls

`Ragdolls/RagdollSystem` (game behaviour: [ragdolls](game/ragdolls.md)) runs serially after animation
when the host passes `CharacterRigs` (the game does; tests and goldens do not). Engine choices:
- **Physics**: BepuPhysics2, one scene stepped on the calling thread (deterministic, 2 solver
  iterations, 4 substeps per tick). Bodies and joints come from the race's ragdoll `.phs`
  (`RagdollDefinition`, scaled by the animal size); revolute → hinge + twist limit, spherical →
  ball socket + swing + twist limits, fixed → weld. Joined pairs do not collide.
- **Parts**: `Whole` when incapacitated, `CarryMode` when carried, a human's downed limbs by their
  LOCATIONAL_DAMAGE `collapse part` (not robotic ones). Bodies outside the mask are kinematic, driven
  by velocity to the animated pose. Animation holds its layers while the whole body is a ragdoll.
- **Published pose**: `CharacterSnapshot.Ragdoll` (model-space `BonePose` overrides, weight for the
  get-up blend); the renderer poses with it. While `Whole`, the character's position follows the root.
- **Scene**: 32-unit tiles round each moving ragdoll, terrain sampled every 2 units, plus building
  collision from `Meitou.Navigation.RagdollGeometry` (per zone on a worker, doors and passable parts
  skipped, ≤16 zones). A tile has ground only until its zones are gathered. Tiles unused for 90 ticks go.
  The terrain is the full-resolution heightmap (blocks of 64 samples read on demand), not the simulation's `GroundHeights`:
  outside the area loaded at the start that is a coarse grid (a sample per 144 units) off the real ground by 2.4 units
  at the median and 15 at the 90th percentile over the land, so bodies met a ground the character was not standing on.
- **Falling through**: the ground is a one-sided mesh. A new whole-body ragdoll with a body centre under the terrain (a
  foot on a slope; 4% of starts) is lifted as one to 0.25 above it (up to 8 units); speed is capped at 200 units/s and
  spin at 30 rad/s and the contact margin is 8 units (Bepu, 30 Hz ticks: a thin limb moving more than its radius per tick
  tunnels); a dynamic body that crosses from above the terrain to more than 1 unit under it is put back on it.
- **Hash**: mask, frozen, age, blend and body poses in system order.

- **Save** (Meitou key, [ragdolls](game/ragdolls.md#saves)): a whole-body ragdoll's body poses, frozen flag and age (`Saving/RagdollSave`); the loaded
  character's first ragdoll starts from them when its root lies within 20 of where it stands, else (and for carried or limb ragdolls) from the animation.

Gaps: ragdolls collide with each other but not with characters that have none (as the original if its groups 0/1 are the characters' shapes:
ragdoll group 3/4 skips them, [ragdolls](game/ragdolls.md#collision-groups)); the building gather's
timing can change a live game's hash (goldens have no environment).

## Crimes and bounties

`CrimeSystem`: pending deeds, witnesses, booking, police eligibility, faction ledgers and expiry.
Carry/combat report deeds; serial sweep observes current positions. Bounty expiry uses
`0.04 hours/cat`, permanent at >=10000. Original categories, witness rules, imprisonment and
remaining uncertainties: [economy.md](game/economy.md). Expiry is paused while imprisoned. The police's response:
[Arrest and jail](#arrest-and-jail).

## Cages, beds and locks

`Locks/`: `World.Fixtures` (`FixtureRegistry`) holds every door, `has lock` container, cage (function 8, pole/peeler
`Restrains`) and bed of a town (a destroyed building: no doors or own lock, `TownBuilding.Destroyed`), registered once when the population first spawns the town (`RegisterSite`), kept on
unload, hashed only once non-empty. Facts: [locks](game/locks.md), [economy 11.5/11.7](game/economy.md#115-bail-and-sentence).
- **Ids** (engine): a building's own lock = its instance id; door i `<inst>#i`; interior item `<inst>/<layout index>`
  (`TownBuildings` collects them as `TownFixture`s); bed k `<inst>@bed<k>`. Owner: the placement's `owner faction ID`, else
  the town's faction. Lock levels roll from `Rng.Hash(seed, StableHash(id), Lock, 0)`: load order never changes them.
- **Doors**: `AiSystem` applies the template's `initial door state` when it assigns a home (first squad, not to restored
  doors). `IDoorGate` (the game's `NavAdapter` -> `NavDoors`, reset on `ReplaceWorld`) closes a building for every path while
  all its doors are effectively locked and none broken (engine: one nav door state per building; closed-unlocked doors stay passable).
  Motion (`LockSystem.Doors.cs`, serial `SlowWorld`): `StepDoors` moves opening/closing leaves by `door move speed` per second and
  locks a closed one that `wants lock`; locking a home's doors closes them (animated, the lock holds at once). Engine stand-in for
  Task_OpenDoor: every 3 ticks a walker within 40 of a closed door whose current or next path leg crosses the leaf's plane within
  its half width + 6 (`IDoorGate.DoorSpotOf`: the door collision's box via `NavDoorGeometry`; headless: none, so no walk-through opening) opens it;
  at an effectively locked one a walker within 15 whose current leg crosses it stops ("It's locked." for the player). Both measure the walker's height from the
  leaf's foot (`DoorSpot.Foot`, the box's bottom; within 40): measured from its centre, tall gates (Stack's leaf centre is 45 up) were never opened and walked through shut.
  Engine stand-in for the original's door faces, closed until `doa` 0.6 ([locks](game/locks.md#doors) "Navigation"): every tick a door less than 0.6 open is solid
  (`HoldAtLeaves`), so a character that moved into or through its leaf from one side within its width is put back at its footprint radius and waits while the leaf opens
  (**Observed**, real-data probe 2026-10-21: a walker at a Stack gate waits about 3 s and passes at 0.62). **Meitou difference**
  (the original closes no door behind a walker, [locks](game/locks.md#doors)): with `DoorsCloseBehind` (Advanced setting "Doors close behind",
  on; off = Faithful) a door a walker opened closes 5 game seconds after the last character crossed it or stood in its leaf box (depth 12);
  a door that `wants lock` (gates) is left open, since closing would lock it and a locked gate shuts its building for every path.
  `AutoOpened`/`LastPass` are engine state, not saved (a loaded open door stays open), hashed only while set.
  The player's finished buildings register their doors (`RegisterPlayerBuilding`, site -1, unregistered when no longer finished;
  keys written by `BuildingSave`); the site menu has Open/Close Doors (`ToggleDoorsOrder`) and Lock/Unlock Doors
  (`ToggleDoorLocksOrder`), the door panel's buttons for all doors at once. Any door: `DoorOrder` (a quick right click,
  [ui-input](game/ui-input.md)) walks the nearest selected to the leaf's near side (12 out, reach 20) and opens or closes it, or picks another
  faction's locked lock (`PickingLock` on the door; a door is worked from that spot). `WorldSnapshot.DoorPicks` lists every door's leaf for picking
  (rebuilt when buildings register or go); `WorldSnapshot.Doors` publishes the player's doors and every door that is not closed and unlocked; the object renderer slides door parts by `DoorLeaf.LocalOffset` ([render-objects](render-objects.md)).
- **Containers** (`OpenContainerOrder`, [locks](game/locks.md) "Containers"): the opener walks onto the container's own floor (`FloorNear` its origin, the goal height set) and
  opens it only within 20 of that height (engine); without the height a ground-floor chest over a furniture hole was walked to on the storey above.
- **Cages/beds**: `FindCage` (original weights; engine: skips a cage the carrier can't reach, `Reachable`: its building
  shut by another faction's doors, or a walk of that faction there failed in the last 24 game hours, `MarkUnreachable`;
  no path query runs in a tick), `Imprison` (state 2, `CharacterTable.Place` at the item's origin, cage
  locked, sentence; optional carrier; every caging is in `CagingsOf(tick)` and the `Caged` event, the bounty-claim hook),
  `LiftOut` (a `PickUpOrder` on the occupant of an unlocked cage, awake or down, engine; out of another faction's cage
  the lifter gets a pending code 7 and the freed one Escaping Prison + `Escaped`), `Release` (unlock, clear, erase the releaser's and jail's bounty entries for a booking releaser),
  `GetOut` (refused while locked; Escaping Prison unless served), `EnterBed`/`LeaveBed` (a move order gets a sleeper up).
  `CharacterCold.InSomething`/`InWhat` name the fixture; an imprisoned character is skipped by AI, movement (held still),
  aggro, answering, `CombatSystem.Engage` as attacker (it can still be attacked) and carrying. Sentence: `SentenceStart`
  (`World.GameHours`) + `SentenceLength`; served when start + length <= now; no bounty with the cage owner: none (served).
- **Lockpicking** (`Lockpicking`, serial; the player's order is in [Arrest and jail](#arrest-and-jail)): `Step` per task update (pending crime,
  timer on the lock, seeded roll by the lock's roll counter, XP), `Allowed` (min chance; engine: conscious, an arm, not
  carried, not on a pole), `Break` (strength > 3 x hardness). Not built: cutting locks other than shackles.
- **Bashing doors** (`LockSystem.Bash`; blow rule in [locks](game/locks.md)): engine stand-in for the planner. Every 30 ticks an
  NPC attacking (armed or an animal) whose target is inside a building another faction's locked doors shut (`IsShut`; inside:
  nearer the door fixture's origin than its farthest door spot) while it is outside takes the nearest door (`CharacterCold.Bashing`);
  the pursuit leaves it alone, it walks to 12 out from the leaf and swings every 1.5 s (engine; the clip length is not read).
  Broken: lock unlocked, door broken and opening, the pursuit resumes. Engine: no crime for NPCs, the `Unlocked` sound for
  `Break`; headless (no door gate, no spots) nothing is bashed. Player characters never bash.

## Arrest and jail

`Crime/ArrestSystem` (serial `SlowWorld`, after `CrimeSystem`) and `Locks/LockSystem`. Facts: [economy 11.5, 11.7, 11.8](game/economy.md#117-arrest).
Arrests, the recognition meter and the marker are hashed only while set. What a save keeps of them: [Saves](#saves).
- **Triggers**: a police-like witness that books a crime acts at once (`CrimeSystem.BookingsOf`, the witnessed-crime
  events); every 15 ticks an awake NPC within 400 that sees a player character (`Visibility`/`SneakVisibility`) whose
  bounty with its faction reaches its class threshold spots it, at once while `RecentlyWanted` > 0 (250 game seconds after
  a booking or a witnessed crime; engine: the countdown), else after 45 s of being seen (engine: one meter per target,
  rate not decoded); only the nearest free police-like looker acts. MILITARY threshold: engine 2000 (original 4000 or 2000);
  the police-like NPCs of the five towns `CrimeInstallTests` loads are MILITARY (**Observed**).
  A police-like NPC in its own town acts on an escaped prisoner (`CharacterCold.Escaped`) it sees. `AggroSystem` no longer
  attacks wanted characters.
- **Spotting talks** (`ArrestSystem.Talk`, the dialogue system, while its event lines are on): the looker fires event 44 (escapee: 45) at the target
  (`DialogueSystem.FireEvent`, a bubble now) and the data decides ([economy 11.7](game/economy.md#117-arrest)): DA 29 arrests (`ArrestFromDialogue`,
  below), "Small bounty" sends the leader to talk (DA 2) and its interrupt opens the fine window (DA 8 + 49 pay and clear; the rest DA 29), others
  say nothing more. The target is then left alone for 60 game s (`SpotRetrySeconds`, engine), longer while the guard still talks or walks up; paying ends it.
- **Fine or arrest** (engine fallback, only without dialogue events, e.g. headless tests; with them a booking witness is left to its witness
  event unless it is already fighting, then it arrests at once): outside his town (engine: farther than 2 x his resident squad's
  home radius from its centre) the guard and his free squad mates attack. In it, 1..1049 is fined (`FineFor`; 300 up to 250
  is engine, the data's line being unreachable): he walks up
  (`Engaged`, so the AI leaves him alone), and within 40 takes the fine when the player's money is above it, erasing his
  faction's entry; else, when the target runs past 800 or after 60 s, and for any bigger bounty: arrest.
- **Arrest**: attack until the target is down (combat ends the fight; nobody beats a downed target), lift
  (`CarrySystem.BeginPickUp`), `FindCage` from the guard (his squad's home building; other arrests' cages reserved), walk,
  and within `use range` + 15 (or a walk that ended within 200) `LetGo` + `Imprison` (`prison time` CONSTANTS). The fight
  stopping with the target up re-orders it within 800, else he gives up; no free cage: he drops the captive (engine).
- **Release**: player and NPC prisoners alike are freed by the release rounds below.
- **Rounds** (`LockSystem.Rounds.cs`, serial; a sweep every 30 ticks starts at most 4, at most 8 run; the worker is
  `Engaged`; each lock takes the owner's 3.3 s): **release** (211): an awake prisoner whose sentence is served, in a cage of
  a faction that books bounties, is freed by an NPC of that faction (one with goal 211 in its role list and hours first,
  else police-like, nearest within 1500) who walks there, unlocks and `Release`s in its faction's name; engine: none such,
  3 failed walks or 120 s, and the system frees it; cages of non-booking factions free nobody. **Lock-up** (155, 197): while
  a member has the goal in its hours a free member with it locks every door of the home (155 only with every member
  inside, engine: within the home's farthest node + 10; neither with a stranger inside); when no member's goal is in its
  hours, what a round locked is unlocked (engine: the morning unlock is not traced; a loaded night's locked homes come back flagged, `meitou house locked`). **Wardens**
  (195): occupied unlocked cages of the faction in its town are locked. **Keys** (engine): a worker whose target is behind
  its own faction's or home's shut doors unlocks them standing before it walks (the sim doesn't know door positions); an
  arrest's guard walking to such a cage has them opened at once. Rounds and locked homes are hashed only while any exist.
- **Lock orders** (`LockSystem`, context-menu verbs on a caged character): `PickLockOrder` (the prisoner its own cage, a mate
  walks to it; `Lockpicking.Step` each tick in reach, messages "Pick success!"/"Pick failed"), `GetOutOrder` (refused while
  locked; an unserved sentence books a pending Escaping Prison and sets `Escaped`, as does any cage of a lawless owner).
  `Imprison`/`Release` clear `Escaped`.
- **HUD**: `CharacterSnapshot.Prison`; the State line shows the status panel's prison text.
- **Bail** (DA 43, [economy 11.5](game/economy.md#115-bail-and-sentence); only the Shek bounty desk and the Inquisitor offer it): the conversation
  closes and the window reopens as the bail list (engine stand-in for the trading window): "Pay Bounty", one choice "Name (c.N)" per prisoner of
  `ArrestSystem.BailOffers` (cages within the speaker's town extent, as `InOwnTown`; player-faction or not hostile to the speaker; `BailFor` not
  null: the cage's owner neither the player's faction nor lawless), `[Leave]`. A click pays (`PayBail(prisoner, payer)`): erases the jail's entry,
  `Release` (no escape). NPC prisoners are only freed, not recruited. `PayBailOrder` (no UI) is the same bail paid by a player character within 500 of the cage.
- **Put in Cage / claim** (`PutInCageOrder`, verb on a body a selected character carries; engine): the carrier walks to the
  best free cage within 1500 (`FindCage`; others' cages reserved), `LetGo` + `Imprison` as an arrest does, then `Claim`
  (11.4 caging tests; the slaver exception never applies): the cage owner's unclaimed entry, halved for a dead captive, is
  paid and marked claimed ("Received c.N reward."). A new order or 3 failed walks end it.
- **Spawn bounties** (`SpawnBounties`, 11.2): rolled for new NPCs (not the player's, not sandbox spawns) under `Spawn` keyed
  by the character id, one fuzz roll for all its factions; no `bounty factions` -> "the empire" (fcs.def), engine: the
  faction named United Cities. Inspecting an NPC shows its bounty in the HUD's bounty panel.
- **Bounty hunters** (engine identification: NPCs of an ADVENTURER faction with a `legal system`, in the base data exactly
  Bounty Hunters): in the spotting sweep they see the player's entry for their faction's booking faction (`legal system`,
  `CrimeFactions.BookingOf`); above 4000 and once recognised (the same meter) the nearest free one and his squad attack
  (`Engaged`), re-ordered while the target is up and within 3000. When it is down, carried, caged or out of reach the hunt
  ends: no cage (11.8), and that hunter leaves that target alone for 600 game seconds (engine). Hunts and cagings are
  hashed only while any exist; restarted after a load ([Saves](#saves)).
- **Bounty desk** (dialogue, [economy 11.4](game/economy.md#114-claiming)): `DA_PAY_BOUNTY` (`ArrestSystem.PayBounty`) pays the carried body's
  unclaimed entry for the speaker's booking faction (halved dead) and marks it claimed; `DA_ARREST_TARGETS_CARRIED_PERSON` (`TakeOver`) runs
  after the line's other effects (data lists them in either order): the carrier lets go and the speaker starts an arrest in the lift
  phase, so it carries the body to a cage as above (none: dropped).
- **Shackles** (`Locks/LockSystem.Shackles.cs`, [locks](game/locks.md)): ARMOUR `is locked` items get a `LockState` (`ItemInstance.Lock`);
  `LockedOn` (effectively locked, worn) refuses moving, unequipping and selling. `ShackleOrder` (verbs Unlock Shackles / Use Tools /
  Use Strength) sends the first eligible worker (the wearer or a mate within 25): pick (`Lockpicking.Step` rules), cut (needs a tool,
  `item function` 9; the best is consumed when hardness reaches 0) or break (strength > 3 x hardness). Another faction's wearer: a pending
  code 7. The lock is saved on the item (`lock lvl`, `locked`, `broken`, `hardness`). Shackled characters show the portrait Slave overlay.
- **Spawn prisoners** (`PopulationSystem.MakePrisoners`; facts: [squad factory](game/factions-squads-towns.md#squad-factory)): a housed squad's `prisoners` templates are made
  as squads of the template's faction (else the owner's) and each member is imprisoned in a random free cage of the home building. Engine choices: members beyond the free cages
  are not made; they are held without a sentence (`Kidnapped`: no release round, no bail, getting out is no escape). Observed: Cat (`Dust King Prisoner`) caged in the Dust King
  Tower's watchtower; Tengu's Vault and the Tower of Abuse have no cages in their homes, so none are made. A caged prisoner is no aggro target (engine choice).
- **Putting shackles on** (`PutShacklesOrder`, verb "Put on Shackles"; engine stand-in for dragging them onto the target's armour slot, an inventory window Meitou lacks): a
  selected character carrying unworn, unbroken shackles walks to a live NPC that is down or caged and wears none, and within `ShackleReach` puts them on, locked again
  (re-locking is an engine choice); what the target wears in that slot goes into its inventory. No crime is booked (**Unknown**). Prisoner Shackles take the boots slot.
- **Slave state** (`CharacterCold.SlaveState` 0/1/2, `SlaveOwner`; facts: [carry-loot](game/carry-loot.md#unknowns) item 3): `DA_ENSLAVE_TARGETS_CARRIED_PERSON` (40) is the bounty
  desk's hand-over (`ArrestSystem.TakeOver(enslave: true)`), and the captive becomes the guard faction's slave once caged ("{1} has been enslaved" for the player's). A slave's
  sentence is never served; getting out of the cage makes it an escaped slave (2, recently wanted 250); a release or bail frees it (0); an owned slave caged in another owner's cage
  changes owner. Dialogue conditions 59, 61, 62 and 64 read it. Saved as `slavestate` and `meitou slave owner` (`SlaveSave`). Engine: the slavers' processing (task 182) is folded
  into the caging. CHARACTER `slave` ("sets initial slave state"; **Verified**: fcs.def and the editor's `SlaveStateEnum` NOT_SLAVE 0, IS_SLAVE 1, ESCAPING_SLAVE 2, EX_SLAVE 3) is
  applied to a new game's characters (`StartPlayer`; The Slaves' "start slave" is 1, in locked Prisoner Shackles), owned by the start town's faction (engine; the original's owner is
  **Unknown**); 3 is free.
- **Slave squads** (`PopulationSystem.MakeSlaves`): a squad's planned `slaves` squads (the factory's role 4) are made at its spot as squads of their template's faction (else the
  owner's), every member a slave of the owner squad's faction, put into random free cages of the home after its prisoners (engine: their "Slave only squad" package's one goal is
  Slave get in Cage, 172; the rest stand by the owner). A slave is never police-like (engine: many slave records keep the default MILITARY class); an owned slave starts no fights and is no aggro target but for its owner's
  enemies (engine: Heng's Outlaw slaves, uncaged without building data, drew its Samurai into fights). **Observed** (real-data
  probe 2026-10-21): Rebirth's 10 Rebirth Cage Guards squads make 34 caged Rebirth Slaves.
- **Slave catching** (`ArrestSystem.CatchSlave`, the spotting sweep; the packages' Capture Slaves, 167, in `addon- slave catching`, which Rebirth's gate guards and garrison inherit;
  **engine** rules, the original's escape test and finder are **Unknown**): an awake NPC of the owner faction in its own town that sees a player slave (daylight sight, full range)
  tags it escaping (2) when it wears no locked shackles or stands outside the town; the nearest police-like or slaver one then captures an escaping slave as an arrest with
  `Enslave` (fight down, lift, cage; event 36 ESCAPING_SLAVE_SPOTTED as a bubble), and the caging makes it a slave (1) again. A town here is `InOwnTown`'s extent, else no farther
  from its centre than its gate nearest the point plus 100 (Rebirth's gates stand about 1500 out, its radius is 184); without gates, the extent only. **Observed** (probe): a start
  slave walking out of Rebirth is tagged just past the south gate, and once down is carried back and caged as a slave.
- **Slave trading** (DA 41 / 42, `DialogueSystem.CharacterTrade`; facts: [economy 12](game/economy.md#12-slaves-prisoners-animals)): the line ends the conversation and the window
  opens as it closes (a speech-bubble line opens it at once; only for a player-character target). Rows, prices and the confirm's effects as in the original, with these **engine**
  choices: the buying list is every character in slave state 1 whose `SlaveOwner` is the slaver's faction, not dead (the original's per-owner set is not modelled); the selling
  buyer's money is the slaver's squad purse (`Squad.Money`, a trader's shop money); the confirm re-checks the window's gate (a row ticked, total within the buyer's money) and
  does nothing otherwise; a slave bought free out of a squad of others leads a new "Wandering Squad" (`SquadManagement.DismissTemplate`, as a dismissed character) of its faction, or a new player squad for
  one of the player's; event 51 fires after it is out of its cage; the window closes when the slaver or the customer is no longer awake. It is neither saved nor hashed.
- Not built: Slave AI Goals (obedience, work, following a master), beatings of idle slaves, the status panel's Slave / Escaped slave lines, kidnapped captives made slaves
  ([Kidnapping](#kidnapping)); from the slave windows: the squads' long-term memory tags (3 FREED_ME, 5 MY_CAPTOR; Meitou keeps none), task 133 as such (Meitou walks a bought caged
  slave out of its open cage to within 15 units of the buyer; the original task's destination is **Unknown**), the obedient-slave order (187) and the shackles put on a sold character; DA 48 (animals).


## Kidnapping

`Kidnap/KidnapSystem` (serial `SlowWorld`, right after Carry; needs the population). Facts: [carry-loot 3.2](game/carry-loot.md#32-goals-and-their-target-finders)
(the finder), [campaigns](game/factions-squads-towns.md#campaigns) (which packages). The goal-254 rule, per member, every 15 ticks, while any downed character exists:
- **Duty** (`PopulationSystem.KidnapDuty`): the member's role list in its campaign's package for the current phase (a loaded campaign squad is driven by the campaign, so its phase,
  not the AI, names the package: the retreat of the cannibal-type raids, the cannibal assault), or in its AI squad's running package (cannibals on patrol and at home), in the hour's window.
- **Victim** (`IsVictim`): the nearest within sight (`RangeOf`, the rescue range; the sensed list's size is **Unknown**) that is alive, down, of a carriable race, not carried, caged or in a bed,
  not an animal, not already someone's pick, and not of the kidnapper's faction or an ally (engine reading of the **Unknown** class bits). Player characters included; sleepers in beds are not taken.
- **Lift**: the free member (awake, not fighting, fleeing or held, able to carry, not a player character) lifts it with `CarrySystem.BeginPickUp`, then goes back to following its leader.
  An AI squad's member starts only when a free cage of its faction is in range (the original's "all cages full" stops the goal); a campaign member always does.
- **Carry**: a campaign carrier marches home with its squad (a retreating squad mills round its own town again, not the raid target, engine choice). Within 1.5 x the radius of a town of its faction it registers the town's fixtures (`RegisterSite`, no spawn needed) and
  takes the best free cage of its faction in range (`FindCage`, the original's weights; other carriers' cages reserved), walks there and within `use range` + 15 lets go and imprisons
  the victim (`Imprison`, `CharacterCold.Kidnapped`). Home with no free cage, three failed walks, or an AI carrier waiting 60 s: it drops the victim, who is then left alone for 2 game hours
  (engine choices; the original's give-up rule is **Unknown**). A carrier that goes down drops its load (Carry). Zones follow a carried player character, so its platoon stays loaded all the way.
- **Prisoner**: `Kidnapped` = held without a sentence: it is never served (no release round), no bail is offered, and getting out is no escape crime. The cage is locked; rescue is the usual
  lock work, or a lift out of an unlocked cage ([Cages](#cages-beds-and-locks)). Messages for player victims ("is carrying X away", "has been kidnapped by F").
- **Save**: the cage state is the existing `in something` / `in what` (Meitou keys for interior cages), and `meitou kidnapped` ([save](formats/save.md)). Carry links are not saved: a save mid-journey
  leaves the victim down where it was and the raiders (retreating campaign squads) lift it again. The kidnap records themselves are not saved. Hashed only while one runs.
- **Not built**: eating, killing or enslaving a captive (goals 139, 135, 220, slaves), lifting sleepers from beds, kidnapping as a crime for witnesses, a carrier's platoon unloading with an NPC victim
  (the carry is dropped when the platoon is stored). A campaign carrier cages in the first town of its faction it reaches, not necessarily the campaign's home; the leader keeps marching to the home.
- **Observed** (headless, real data, `raidFrequencyMult` 1000): the Skin Bandits' "skin bandit standard raid small" against an outpost with four weak player characters: all four are lifted once down (the
  cannibal rule sends phase 2 at the third carrier), three are caged at the faction's Skinhouse HQ (it has three cages), the fourth, on the leader, is carried towards the campaign's far home and put down
  at the first own town without a free cage. A save and load keeps the three caged and `Kidnapped`; a second capture writes the same keys. Cannibal AI squads at home lift and cage a downed player character in about two seconds.

## Buildings

Player construction contract (Faithful UI). Facts: [construction](game/buildings-production.md#construction),
[placement](game/buildings-production.md#placement), [build menu](game/buildings-production.md#build-menu),
[build tasks](game/ai-tasks.md#build-tasks), [save keys](formats/zones.md#placement-keys). Out of scope: the
outpost behaviour beyond the record (residents, raids, renaming, the map marker), the door x3 rule,
power, fetching from storage (task 247); production and research: [Production](#production).

**Shared types** (stubs f4cb78c, filled by the simulation track):
- `Meitou.Data/Gameplay/Construction/BuildingBuildData.cs`: `BuildingBuildData.From/LoadAll` (build fields, material
  entries), `MaterialCost`, `Target` (sum of required), `Rate` (`build speed mult` / (1200/11)), `EstimatedHours(buildSpeed)`
  (the build window's estimate, **Verified** algebra: materials / (`build speed mult` x CONSTANTS `build speed` x option)).
- `.../ResearchGate.cs`: `StartResearch(db, start)` (the default start research, then the start-off's `research`),
  `Unlocked(db, completed)` (union of `enable buildings`, BUILDING targets only), `DefaultStartId`.
- `.../PlacementRules.cs`: `PlacementFailure` (one value per original message), `PlacementTown`,
  `TownBlock(BuildingBuildData, Vector2 at, IReadOnlyList<PlacementTown>, string playerFactionId)`, `Message(PlacementFailure)`
  (the original's texts; `InvalidFloor` is "Invalid placing floor number." because the rest of that text is not in the
  docs, `NotAvailable` is the engine's "That building is not available yet.").
- `Meitou.Simulation/Construction/ConstructionCommands.cs`: `PlannedBuilding(Record, Position, Rotation, Floor, Parent)`;
  `PlaceBuildings(Plans)` (Confirm), `BuildOrder(Characters, Site, Repair)` (orders 2/95), `DismantleOrder(Characters, Site)`
  (96), `CancelSite(Site)` (engine: only a site with nothing delivered), `PauseSite(Site, Paused)`. Sites are named by instance id.
- `.../ConstructionSnapshot.cs` + `WorldSnapshot`: `Buildings` (`BuildingSnapshot`: `Id`, `Record`, `Position` absolute,
  `Rotation`, `Floor`, `Parent`, `Owner` faction index, `Progress`/`Target` in material units, `Materials` (item,
  required, delivered), `Complete`, `Paused`, `Dismantling`), `BuildingsRevision`, `UnlockedBuildings`.

**Simulation state.** `World.Buildings` (`Construction.BuildingRegistry`, sites as `BuildSite`): player-built sites in
creation order, by id, holding the snapshot's fields plus rate, builder counter and the destroyed flags. Base-game
placements are not in it. Ids `<n>-zone<X>.<Y>-INGAME`, n from a per-zone counter (hashed) that starts above every such id
in the loaded save (generated interiors share the pattern; `Reserve(id)`). The 106 `-zoneX.Y-INGAME` ids of the level data
are not reserved by the simulation (**engine gap**, the host may call `World.Buildings.Reserve`); a collision with a base
placement is the host's to avoid. `World.Research` (`Construction.ResearchState`): completed RESEARCH ids and the
`Unlocked` set (replaced, never changed in place). A new game completes the default start research
(`ResearchGate.DefaultStartId`, [build menu](game/buildings-production.md#build-menu)) and the start-off's `research`
(`PopulationSystem.StartPlayer`); a load takes the save's. Static this milestone, not hashed.

**Outposts.** `World.Buildings.Towns` (`PlayerTown`: `Number` 1.., `Name`, `Position`, `Owner`; never removed) and
`BuildSite.Town` (a number, 0 none). `PlaceBuildings` gives each new site its town by the
[outpost rule](game/buildings-production.md#placement): the parent's; else the nearest player town within 5000 (X/Z);
else the town of the nearest player site within 2500; else a `creates player town` non-wall makes "Your Outpost" at its
position, and every player site whose nearest town (level towns and outposts) within 8000 is it moves in. After the
batch, `links together` sites still without a town take the batch's last player town, else the nearest town within 16000
of their mean, else a new outpost there; a level town of another faction leaves them without one (engine:
`BuildSite.Town` names player towns only). Hashed (towns, and `Town` when set) only once there is one.

**ConstructionSystem** (`StandardParts.Construction`, in `All`; `StandardSystemSet.Construction`) runs after Carry, before
Combat; all of it is serial. `ConstructionSettings`: `BuildingSpeed` (the option, 1), `Reach` 20, `SettleReach` 150,
`Footprint` (record -> radius, optional), `AutoEveryTicks` 15.
- `Inputs`: `PlaceBuildings` re-checks only the data half (a BUILDING, unlocked, `TownBlock`); a failing plan is dropped
  with `World.Post(Message)`. A total of 0 completes at once, so a free building is complete from the first snapshot.
  `BuildOrder` (player characters, awake, not carried or caged; a complete site is ignored, repair needs building health
  and does nothing) and `DismantleOrder` set the `CharacterTask`s `Build` / `Dismantle` and `CharacterCold.BuildSite`
  (the selection when the list is empty). Dismantling is refused with the original's texts while a site stands in the
  site ("I have to dismantle the furniture first." for interior furniture and nodes, else "I have to unmount the
  buildings first."). `CancelSite` removes an incomplete site with nothing delivered, else posts "Dismantle it to get the
  materials back."; `PauseSite` sets `Paused` (only the auto-job skips it). `PlaceBuildings`, `CancelSite` and `PauseSite`
  also apply while paused (`ApplyPausedInterfaceCommands`).
- `Schedule`: a worker walks to the site like a medic to a patient (goal = site XZ, free speed, asked again after a wait
  when the path failed). It has arrived within `Reach` + footprint of the site, or once its path has ended within
  `SettleReach` more (**Unknown**: the original's work distance; a site that is a navmesh obstacle cannot be walked
  into). Work ends when the site is gone, complete (building), the character is down, fighting, carried, carrying or caged,
  or another order replaced the task.
- `SlowWorld`: arrived workers in slot order. *Build*: a stalled site (`sum(min(delivered, required)) <= progress`) turns
  into *DeliverMaterials* if the builder carries a missing item, else the work ends (fetching, task 247, is not built);
  otherwise progress += rate x `lerp(0.3, 2, e/100)` x dt x `BuildingSpeed` x CONSTANTS `build speed` x 0.75^k, e the
  engineering stat times its injury multiplier (engine choice), dt = `TickSeconds`, k the builders that already worked
  the site this tick (the 0.75 counter resets each tick: the engine reading of the **Unknown** reset; capped at 15, above
  100 it restarts), engineering XP (`XpService.Engineering`); a rate of 0 or less completes at once; at
  `progress >= target` the site completes (`Complete`, message "Building '<name>' construction complete." for the player's
  faction). *DeliverMaterials*: the bar fills at 0.2 per second (`CharacterCold.BuildBar`); full, it hands over one unit
  of each missing type (inventory first, then backpack) and the task returns to *Build* (whether the original repeats
  deliveries before building is **Unknown**; fetching aside, the two alternate). *Dismantle*: a complete site first
  becomes incomplete with progress at most target - 1; then progress -= dt x `lerp(0.05, 4, e/100)` x 4 x rate (no option
  or constant), XP as building; at 0 the delivered materials come back and the site is removed. The `medic` clip plays
  for building and delivering, `stonemine` for dismantling (`AnimationSystem.HeldClip`).
- Refund: entry i with something delivered gives `max(1, trunc(U x delivered))`, U = m + (1 - m) x
  `Rng.Float(Rng.Hash(seed, StableHash(id), RngPurpose.Build, i))`, m = CONSTANTS `min dismantle materials percentage` /
  100. Engine (no ground items yet): the items go to the dismantler's inventory, then backpack; what does not fit is lost
  with a message. A destroyed flag (a save's; nothing sets it in play) turns a building into a ruin on the next tick
  (**Verified** rule: progress 0.4 x target, delivered floor(delivered x U[0.3, 0.5) + 0.5)).
- Auto-job (not in `AiSystem`: player characters have no AI mind): every `AutoEveryTicks` an idle, wandering or following
  player character with the Jobs order, a race and stats takes the site with the best score `max(2 (1 - d^2/9000^2),
  0.0001) / (n + 1)` (n workers on it); not complete, paused, destroyed or being dismantled, and not stalled unless the
  character carries a missing item (the original's planner cost is **Unknown**; this avoids a walk to a site it cannot
  work). It goes back to its wander or follow task afterwards.
- `BuildingsRevision` bumps on add, complete, a complete building becoming a site (dismantling started), destroyed and
  remove. `Version` (progress, delivery, pause) only rebuilds the published list.

**Hash and determinism.** `World.StateHash` adds the registry (per-zone counters, then each site's fields in creation
order, then the outposts) only once it holds a site or an outpost, like `Fixtures`; `CharacterCold.BuildSite` with its bar and resume task hashes only
when set. Existing golden hashes are unchanged while there are no sites; the `build` scenario pins one with sites.
Decisions read only world state and command order, never collision or navmesh readiness (paths stay the usual
asynchronous input).

**Host seam: snapshot diff.** The host keeps the last `BuildingsRevision` it acted on (-1 after `ReplaceWorld`) and,
when it differs, diffs `Buildings` by id: added → render overlay (and navmesh if it qualifies), removed → out of both,
newly complete → navmesh and lamps (lamps leave when it is no longer complete). Progress does not bump the revision; the scaffold reads `Progress/Target` each frame (rule in
[construction look](game/buildings-production.md#construction-look)). An event queue would lose changes across dropped
ticks and loads; the diff does not. Navmesh membership (engine reading of the Observed rebuild on placing):
`PathMode != 0` and (`Complete` or `BuildThreshold == 0`). After each rebuilt zone publishes, the host enqueues
`RepathCommand`.
- Overlay type: the existing `BuildingPlacement` (`StateId` null, `WorldY` = absolute Y, zone of the position).
- Rendering: `WorldObjects.AddRuntime/RemoveRuntime(BuildingPlacement)` merged into `BuildZone`. Engine choice: a loaded
  zone takes the placement incrementally (no reload); the scaffold is an approximation ([render-objects](render-objects.md#player-buildings-and-the-placement-ghost)).
- Navigation: one thread-safe overlay store shared by every `ZoneGeometryGatherer` (`NearBuildings` and `BuildingHash`
  see it, so the cache key changes), `NavSystem.AddRuntimeBuilding/RemoveRuntimeBuilding` and `RebuildZone(zone)` for
  each zone within `BuildingReach`.

**Placement validation.** Host: `Meitou.Game/Building/PlacementCheck.Evaluate(PlacementQuery) -> PlacementFailure`,
pure over the query (build data, transform, floor, queued ghosts, snapshot characters and sites, towns) and services
(heightmap, collision cache, water level). It calls `PlacementRules.TownBlock` first and returns the first failure in
the original's order. The geometric half depends on asynchronously loaded collision, so the simulation trusts it and
re-checks only the data half. Ghost controls, tilt and the unconfirmed ghost queue (Undo) are host UI state.
Implemented host-side, in the original's order: town block, site under the ghost, static building under it, interior
rule, footprint overlap with static buildings, sites and queued ghosts (convex hulls of the collision shape, SAT),
characters within 2.5 of the hull, LAND parts below the water level 98, slope (four rotated bounding-box corners, `max slope`,
`match slope` tilts fully) and normal.y < 0.2. **Not decided** (never fail, so the ghost may show valid where the original
refuses): resource/crop yield, snap nodes, usage nodes, blocked use nodes, indoor floors and ceilings, vertical margins,
the 5 deg object-surface rule. Messages: `PlacementRules.Message`, else the original's text table in `PlacementCheck`.

Build mode (B, `Kenshi_BuildWindow`, Faithful UI only): categories and groups list `WorldSnapshot.UnlockedBuildings` in
database order (the original's order is **Unknown**); the window estimates hours as materials / (`build speed mult` x 6),
rounded up (rounding **Unknown**). Left click or Space queues a valid ghost, Backspace or Undo drops the last, Confirm sends
`PlaceBuildings`; leaving build mode drops the queue. The HUD panels the window covers are hidden meanwhile. Right click
on a site with characters selected gives `BuildOrder` (held 0.2 s: menu with Build, Dismantle -> `DismantleOrder`, Cancel
Construction -> `CancelSite` while nothing is delivered); the pointer is `build` over a site.

Walls (`Building/WallLine.cs`, facts in [walls](game/buildings-production.md#walls)): a `links together` normal wall, or
a snapped tower/gate, is laid as a line: first click (or Space) starts it at the ghost, which snaps to the nearest link
point of a queued wall or player wall site; each frame `WallLine.Layout` lays bend pieces at snapped ends and the
straight run to the cursor (end snapped to a start, or an end for a backwards line), and every piece runs `Evaluate`
as a `WallPiece` (own pitch for slope; `Ignore` skips connectors, wedges and walls at a snapped joint). The next click
queues every piece as its own ghost and chains a line from the far end; right click or Backspace drops the line. Each
piece becomes its own site; `ConstructionSystem` allows a subsection when a BUILDING listing it in `wall subsections`
is unlocked. `ConstructionSystem.AttachWalls` (after placing, and at the end of `BuildingSave.Read`) gives each short,
connector or wedge without parent or children a `WallParent`: the root of the first wall site, with the same complete
flag, that has a link point within sqrt 10 of one of the piece's link points or its middle (engine choice: the middle
catches crest shorts; a normal/single root wins over a subsection root; gates never parent). Orders, builders and the
auto-job act on the root; its progress sets the children's (`min(p/target, 1) x child target`) and completes them; its
removal removes them. So an attached piece costs nothing; the link is not saved or hashed. A destroyed attached piece
just follows its wall again (original **Unknown**). **Engine gaps**: level (NPC town) walls are not snap candidates;
indoor lines (radius 25), the steep-climb straight extension, the offset clamp and the arrows are not built; the stretch
is recomputed each frame from the signed overshoot. The snapshot carries `WallParent`; picking an attached piece in the
UI picks its wall (name, verbs and Cancel's nothing-delivered check are the wall's).

**Saves.** `Saving/BuildingSave.cs` writes each site as a GAMESTATE_BUILDING (`<id>-S23`) plus its entry in the zone
file's `0-buildinglist` (keys per [zones](formats/zones.md#placement-keys)); a zone without a file gets one, the slot
list and the handle (`C` = zone container, `CS` 11111, next free `I`) follow, and player-built entries the world no
longer has (a dismantled building) are removed. The instance Y is the absolute one, as in all game-made
`-zoneX.Y-INGAME` buildings of the samples (**Observed**); a load prefers `world Y pos`. `is complete` is written
explicitly. The parent is `isInsideBuilding` plus Meitou's `meitou parent` (instance id, wins on load). On load it
reserves every `-zoneX.Y-INGAME` id of the save and reads the player faction's such buildings back, in zone and list
order (the player's start has none, base placements use other container names). `town` is the outpost's handle,
`residents` the null handle. Outposts go to `quick.save` as [town-list entries](formats/save.md#player-outposts)
(made when the save has no list); an outpost read back keeps its entry id, state and handle, a new one gets the next
town container `C` (engine choice, the original's handle allocation is **Unknown**) and a seeded `CS`; a load reads the
player faction's non-nest entries in list order and matches buildings by the handle's `C`/`CS`. The state's name
is "Town state <name>" and gives the name back; a new state is written `discovered` and `explored` (Meitou choice). `Saving/ResearchSave.cs` reads and writes the RESEARCH record's `finished<k>`
and `num finished`. Not compared with a real file that has a player-built building (the sample saves have none).
The first snapshot after a load carries every site, so the host seam needs nothing extra.

**Files.** Simulation track: `Meitou.Data/Gameplay/Construction/*`, `Meitou.Simulation/Construction/*`,
`StandardSystems.cs`, `World.cs`, `World.Interface.cs`, `WorldPublish.cs`, `Movement.cs`, `CharacterTable.cs` (cold fields),
`Rng.cs`, `PopulationSystem.Player.cs`, `AnimationSystem.cs` (held clips), `Saving/*`, `Meitou.Data/Save/SaveBuilder.cs`
(a comment). Host track:
`Meitou.Game/GameHost.Building.cs` (diff, overlays, rebuilds, repath), `PlayerInterface.Build.cs` (Kenshi_BuildWindow,
ghost, commands), `Building/PlacementCheck.cs`, `Building/BuildMenu.cs` (categories and groups from
`BuildingBuildData` and `UnlockedBuildings`), `KeyMap.cs` (build map), `PlayerInterface.ContextMenu.cs` and
`PlayerInterface.Picking.cs` (site verbs), `GameHost.Interactive.cs` (hook), `Meitou.Engine` if needed,
`Meitou.Ui/*`; `Meitou.Rendering/WorldObjects.cs`, `ObjectStreamer.cs` and the object shader plumbing;
`Meitou.Navigation/NavSystem.cs`, `NavMeshService.cs`, `ZoneGeometryGatherer*.cs`, new `NavRuntimeBuildings.cs`.

## Production

`Production/ProductionSystem` runs the player's complete buildings: mines, wells, refiners (function 4), farms, research and
crafting benches, generators and batteries. Facts: [production](game/buildings-production.md#production), [crafting](game/buildings-production.md#crafting),
[farming](game/buildings-production.md#farming), [research](game/buildings-production.md#research), [power](game/buildings-production.md#power).
- **Machines** (`Production/Machines.cs`, data `Construction/MachineData.cs`). A machine is made when a player site completes
  or loads complete, and dropped with the site; its items fall on the ground. It holds:
  - the recipe inputs and the output, as item counts plus a level or progress (engine choice: crops and water have no quality);
  - the storage grid of a `has inventory` building (a bench's books), as a real `Inventory`;
  - the farm's plants.
- **Yield.** Set at creation from the ground under the building: a resource's value (`ResourceField.Yield`; 0 stays 0 for the player), ore from the
  nearest foliage rock (`FoliageResourceNodes`, given by the host; none headless), and for a farm its
  fertility factor, crop match and water rate.
  - Fertility and the crop match come from the BIOME_GROUP's ENVIRONMENT_RESOURCES at the weather-area cell.
  - The altitude test uses the building's y.
  - **Observed** with the vanilla map: only the central biomes give farming ground.
- **Operating.** `OperateOrder` sets `CharacterTask.Operate`. The character walks within use range of the building and
  works it every tick; work is scaled by its skill factor and trains the stat.
  - Producers make output from their inputs.
  - Farms are harvested and cleared.
  - Research benches add to the research queue; crafting benches work their queue.
  - More characters than `max operators` are refused with "X is already in use.".
  - Farms tick whether anyone is there; there is no offline catch-up on load.
- **Rocks** (`ProductionSystem.Natural.cs`, UI `PlayerInterface.Rocks.cs`; facts in [resources](game/buildings-production.md#resources)). A foliage
  rock is a natural site, operated like a mine: a right click on it with characters selected is an `OperateOrder`, with Shift an Operating machine
  job. Its site id `natural:<BUILDING>@x,y,z` (the rock's position) is all the sim needs: the site is made from it (an `is foliage` BUILDING
  with an operable mine; with a rock source, one of it must stand there), owned by no faction, so player-only lookups skip it. Its machine
  yields `ResourceField.NodeYield` (at least 0.1) and is kept in memory, not saved; a saved job keeps the id (`meitou pjob<i>`). The ore stays in
  the rock's output (5) until taken: a left click selects the rock and opens its machine window once it has been mined (**Observed** by the
  owner: the base game's ore is picked up by hand), or a storage within `HaulRange` takes it as for any machine. Engine choices: the rock is picked by its mesh's triangles where the instance places them (the original picks its building's collision); its radius, the longest half-extent of the mesh's bounds box scaled by the instance and at least `NodeRadius` 25, sizes its resource richness (the original takes the mesh's bounds the same way); without the mesh file a rock is picked as a sphere of 25 round its origin; the pointer over a rock is `mine` (with characters selected, after sites and doors, as the click order is); level-placed `is foliage`
  buildings (not drawn, not in `FoliageResourceNodes`) cannot be mined. NPC town mines are not run (the original's make output while NPCs
  operate them).
- **Items.** `PutInMachine` and `TakeFromMachine` move items between a character within 200 of the building and the machine:
  inputs, output, or storage cells.
- **Research** (`Construction/ResearchState`). A queue paid at queue time from the benches' storage; a paid id is never
  charged again.
  - Bench level is the best complete bench's `tech level`.
  - Several operators add with the 0.6^k falloff.
  - Partial progress is kept off the queue (in memory only).
- **Hash.** Machines and research join the world hash only once the player has one; golden hashes are unchanged. Yields read the
  biome map and rock source, so a replay without them (headless) hashes other yields than the host.
  RNG: `RngPurpose.Farm` (replanting, harvest rolls), `RngPurpose.Craft` (critical rolls).
- **Crafting** (`ProductionSystem.Crafting.cs`, data `Construction/CraftingData.cs`). `QueueCraft` (at most 20),
  `CancelCraft`, `RepeatCrafts`. The first craft binds the input sections to its ingredients (other items in a section
  fall on the ground); finished items wait in the output (section `out`) up to the option's store, then the bench reports
  output full. Benches listing fewer `consumes` than a recipe's ingredients get extra sections. A repeating queue puts
  each finished craft back at its end. Crafts no research lists are always available.
- **Research repeats.** A `repeats` research can be done repeats+1 times, each paid at `cost x repeat mult^n`.
- **Power** (`ProductionSystem.Power.cs`). Generators (fuelled or wind), batteries and consumers are machines; each
  town (sites grouped by `Town`, 0 included) shares power as the [original](game/buildings-production.md#power) after the
  operators, so grants follow last tick's operators. The factor scales production, crafting, research and farm growth;
  research `improve buildings` (`ResearchState.Upgrades`) scales rates and adds output and capacity. Engine choices: health
  is 1; wind generators give `WindFraction` 0.5 of their output; battery mode is not saved. HUD: output, fuel use, charge,
  "No power" / "Low power".
- **Hauling** (`ProductionSystem.Hauling.cs`, `ProductionSettings.AutoHaul`, default on; facts in
  [buildings-production](game/buildings-production.md#hauling)). An operator at its machine first puts carried items
  into empty input room. It takes a full output only when a storage will accept it (else the machine stays full) and
  carries it to the nearest player storage (function 2 or 13) that accepts it and has room; or it fetches an empty input's item from the nearest storage holding it. A
  storage accepts an item when `itemtype limit` matches and `limit inventory` is empty or lists it; `stackable bonus
  mult` scales its stacks. The player's drops into a storage follow the same rule ("does not take").
  Excess fetched inputs stay with the operator. Engine choices: storage within `HaulRange` 3000 of the machine, ties
  by site id, a fetch takes what the inputs have room for; the trip is not saved. Crafted items are not hauled.
- **Job lists** (`PermaJobs.cs`, `ProductionSystem.Jobs.cs`, `Saving/PermaJobSave`; facts in [perma-jobs](game/ai-tasks.md#perma-jobs)).
  `CharacterCold.Jobs` is the ordered list; `AddJob` (Shift + right click on a finished machine or storage) adds through the original's rules
  (remap, duplicate refusal, companion, switch on); `RemoveJob` is the list's X. Every `JobCheckTicks` 10 (staggered by slot) a player character
  that is idle (no task, following or wandering, as for the medic job) or on a job takes the first job that is not blocked, so a higher job takes over as soon as it can run and a blocked one passes
  to the next; an order replaces the job's task, and the list resumes once the character is idle. A job that ends returns the
  character to the following or wandering it took it from. Meitou runs two types; the others are kept,
  listed and saved:
  - 87 Operating machine: blocked while others hold every operator place or the machine has no work (no power; no queued research or craft; a
    farm with nothing ripe or dead; an empty input the operator cannot fill from its pack or a storage; a full output no storage takes).
  - 124 Hauling to (`CharacterTask.Haul`): the nearest player machine within `HaulRange` of the storage whose output the storage takes is
    emptied into the hauler's pack and carried there; items the hauler already carries that some machine makes go first. Blocked with neither.
  - Implicit jobs (`ImplicitPick`): with no list job to run, the switch on and not a slave, the squad AI options (`PlayerState.SquadAi`, the
    overview's AI tab, all on by default) start, in this order: Ditch items (242: carried items some player machine makes go to the nearest
    storage within `HaulRange` that takes them, as a Haul trip; only with no queued orders), Sleep when injured (153: a body part below full
    health lies in the nearest free player bed within `HaulRange`, resting, until healed), Sit when idle (275: the nearest free player chair or
    throne within `SeatRange` 600). The one running is kept while it holds; any list job preempts it. Distances are from the character, or its
    squad leader while it follows, so it gets up when the leader walks off. Sitters and sleepers are put on the seat's origin facing its way;
    the medic job counts them as idle.
  - The list is reordered by dragging a row (`MoveJob`).
  - 58 Medic and 148 Find and rescue (+ companion 106): the Medic and Rescue buttons (H, J) start one look for a patient or body; with Shift they add
    the job. The first aid and carry passes run them for a character that is idle, on an implicit job or on a list job below them.
  - Engine choices: the check period; kind-2 (turret)
    jobs are not run; the injury test, the ranges, a bed's clip (`sleeponfloor`) and a seat's facing (its own, not the table it faces).
  - Saves: GAMESTATE_AI `pjob<i>` / `pjobT<i>` / `jobs` as the original, plus `meitou pjob<i>` (the site id) for a building the handle cannot
    name; the older `meitou jobs enabled` is read, not written. The squad AI options are the CAMERA bools `ditch`, `sleep`, `sit`. A job's
    trip is not saved.
- **Saves** (`Saving/MachineSave`, `ResearchSave`). Each building's production and farm keys go into its
  GAMESTATE_BUILDING. Items go into an INVENTORY_STATE `<site>-inventory`, listed first by the building, with sections
  `in<k>`, `out` and `main`.
  - A loaded farm's plants all take its growth.
  - The original's reading of these inventories is unverified: no sample holds a working player building.
- **UI** (Faithful, `PlayerInterface.Production.cs`).
  - Selecting a machine opens its window beside the inventory: `Kenshi_GenericBuildingWindow`, or for a bench
    `Kenshi_InventoryResearchWindow`. Items are held in and out with the mouse (an input takes its record up to its capacity; `out` takes nothing).
  - The site menu gains Operate (also the quick right click); Shift + quick right click adds a job instead ([job lists](#production)).
  - The HUD shows the farm's growth, ripeness and fertility, the output, workers and tech level.
  - A crafting bench's window shows the current craft and QUEUE... (opens the Craft tab); finished items are lifted out of `out`.
  - The overview's Craft tab lists stations, their crafts (click to queue; red: locked), the queue (click to cancel),
    Repeat Queue and the hovered craft's time and inputs.
  - The overview's research tab lists categories, blueprints (click to queue), the queue (click to cancel) and a
    description with the cost.
  - The original's verb names, row widgets and colours are **Unknown**.
- **Engine gaps:**
  - No rain filling farm water.
  - Furnaces do not burn and turrets do not fire. A quick right click on the player's furnace walks the selection there and opens its window
    (the original's 26 LOOT_TARGET); on the player's turret the nearest able selected character walks to its gunner spot (146 USE_TURRET,
    `UseTurretOrder`; the spot as for town turrets below; one gunner, engine choice).
  - Plants are not drawn.

### Prospecting

Facts: [Prospecting](game/ui-screens.md#prospecting), [Task_Prospecting](game/ai-tasks.md#prospecting). `ProductionSystem` (`ProductionSystem.Prospecting.cs`, `Production/Prospecting.cs`) owns it because it holds the `ResourceField`.
- `ProspectOrder` names one player character (the host picks the selected one nearest the camera pivot). If it is awake, not fighting, carried, carrying, caged or swimming (water state 3) it stops and takes
  `CharacterTask.Prospect` with `CharacterCold.Prospecting` = 0 (hashed only while set, not saved). A character already prospecting is left alone; when done it is `Idle`.
- Each tick (`SlowWorld`, before the early returns) the progress grows by the tick's game seconds. At `ProspectSeconds` = 1 (**Verified**) the task ends (`Idle`) and a `ProspectReport` is published as `World.Prospected`: the
  prospector's name and science (raw stat), the spot (its position), the zone name and the crop coverages, and the five resource values from `ResourceField.Yield` at the spot. Another task (a move, an attack, a stop) or the character going down
  ends it with no report. The report is display only: nothing reads it, the state hash does not see it, a save does not hold it.
- Animation: the `medic` clip (`AnimationSystem.HeldClip`). HUD goal text "Prospecting". **Not modelled:** the progress bar over the character, the map overlay, the state field `+0x198`.
- Engine choices: the nearest-to-pivot rule uses the selection (the original's iterates the player interface's list, **Observed**); wind is the host's weather state at the camera.

## Trade

`TradeSystem`: trader-squad stock/purse/restock, open trade, buying/selling/fencing. Charge,
quality, culture, market, forbidden/stolen factors follow [economy.md](game/economy.md).
A squad shop holds one 18 x 18 stock grid (the building storage size and the vanilla trader window width); `ArrangeStock` arranges it. A housed trader whose template makes no
prisoners or slaves and whose home has stock containers gets a building shop instead ([building shops](game/economy.md#building-shops-observed-decompile)): one merged stock
(`Shop.ForBuilding`, `Shop.Building`), its own purse rule, the containers' locks opened at each fill. Stock cells stack 50 x an item's `stackable`. A housed squad with vendors that is no
trader has its home's containers filled instead (`FillHome`). Only members that trade (`DialogueSystem.Trades`, [who trades](game/economy.md#who-trades)) publish `IsTrader`, the
Trade verb and pointer. Shop state is hashed; the open trade is not, except its buyback receipts (below). A shop is saved on its platoon (`Saving/ShopSave`: stock, fill count,
`inventory refresh time`, [economy](game/economy.md#building-shops-observed-decompile)) and kept by a saved platoon that leaves the world; a save without it gets a fresh fill.
Transfers and money changes must succeed together.

Bags (engine choice; how the original trades bags and their contents is **Unknown**): a bag trades whole, the same instance with its contents grid, and only empty ("That bag
isn't empty."), since its contents would otherwise go unpriced and unchecked for theft; a bag's contents are not shown as stock. A right click puts a bought bag into a free
backpack slot, else the main grid; a drag puts it where it is dropped, the backpack slot included, never into another bag. Other items: a right click tries `TryEquip`, then the grids.

Buyback (the original's: [economy](game/economy.md#buyback-during-an-open-trade)): a purchase or sale leaves a receipt on the moved units (`TradeReceipt`, the coins paid per
unit, kept through stack splits); moving them back in the same open trade costs or refunds exactly those coins and clears it. Units of the same trade and direction share a stack
(their coins join); they top up plain stacks only when no free cell is left, losing the receipt for those units. Closing or replacing the trade voids every receipt, moved items'
too; stacks it kept apart stay apart. Receipts are in the state hash but not saved, so a save made mid-trade reloads without them, with a different hash.

## Dialogue

`DialogueSystem` (game facts: [dialogue](game/dialogue.md)). The context menu's Talk (only when a dry run of the line pick, dice and memo put back, finds something to say) sends `TalkOrder`: the
talker walks up (60 units, engine choice; 60 s to arrive), the NPC is held, then event 1 fires and the NPC's dialogue set (record, squad
template, leader role; `DialogueDatabase`) is chosen from by score, line checks and a seeded pick. The NPC line is said (text, tokens and
word swaps, effects at once); passing replies become choices, an NPC-side child a single `[...]`. The host opens `Kenshi_ConversationPanel`
modal, which pauses the game; `DialogueChoice` and `EndConversation` apply between ticks at speed 0; effects that order fights are deferred
to the next tick. A hostile target is attacked instead.

- **Effects built**: locks, interrupts, `change relations`, trade, relations, the three attacks, take and give money, faction rank (DA 35, read by DC 10), the surgeon's editor
  (paid by the line, `EditCharacter.Paid`), assign and clear bounty, lock this dialogue, DA 37 / 38 (a campaign's phase), `trigger campaign` (`PopulationSystem.TriggerFromDialogue`: the rules in
  [campaigns](game/factions-squads-towns.md#campaigns); `world state` conditions read the live states), recruiting (DA 3 / 18, `DialogueSystem.Recruit`), crowd
  triggers, `give item`, `change AI` and `AI contract` (`Ai.ChangeAi`, `Ai.StartContract` with the target: `Blackboard.MissionTarget`, followed by goals aimed at the
  squad mission through `AiMind.FollowTarget`; on its end the leader's `contract end talk passive`), DA 25 / 53 (`FixtureRegistry.TagEscaped` / `FreeSlave`) and DA 29
  (`ArrestSystem.ArrestFromDialogue`), DA 43 (the bail list, [Arrest and jail](#arrest-and-jail)); the rest is listed in [dialogue](game/dialogue.md#meitou-design).
- **Events from other systems**: `DialogueEvent` (deferred to the next tick) fires an event at an NPC as bubbles, or a given DIALOGUE (the contract end talk);
  first aid, shackles and the release rounds send them. The police's spotting fires 44 / 45 at once (`FireEvent`, which says whether a line was said).
- **Recruiting** ([facts](game/dialogue.md#recruiting)): at the `max faction size` cap the line "It looks like your squad is a little full." is
  posted and the conversation ends; otherwise the speaker leaves its squad (its leader passes to the first member left) and joins the player's
  active squad (the first selected player character's, else `Player.Squad`, else the talker's), or a new "blank squad" squad when that one has
  `max squad size` members: player faction, role Squad1, NPC class Adventurer (as every player-faction character, `CharacterAssembly`), player
  default orders, speed mode Free (2), no AI mind, stopped, its save link dropped (a save writes it as a new member of the player's platoon).
  Its dialogue becomes the player set (`DialogueDatabase.SetFor`). DA 3 ends the conversation and opens the editor in recruit mode
  (`EditKind.Recruit`: free, race and gender kept). Not modelled: the original's extra empty platoon at the `max squads` cap, and the
  uniform clear (Meitou has no uniforms).
- **Conditions**: most DCs are computed (slavery: 59, 61, 62, 64); those needing state Meitou lacks (squad memory flags beyond "met", building locks, walls,
  missions, the personality functions, tech level) compute "no". `met` and "last seen" come from finished conversations (per squad). IN_MY_BUILDING uses the squad's home building and the lock rounds' extent.
- **Speech bubbles**: `WorldSnapshot.Speech` lists the lines on show (`SpeechBubble`: speaker, text, large font); `PlayerInterface.AddSpeech` draws them over the head. Sources and engine choices: [dialogue](game/dialogue.md#meitou-design).
- **Personality**: the save's `personality` when above 0, else an engine roll from the record's PERSONALITY tags (`Personality`).
- **Engine choices**: a childless NPC line shows `[Leave]`; a reply leading to another reply offers it alone; number keys pick choices.
- **State**: last use per line, permanent chance rolls, event counters, met memory, interrupts and the open conversation are hashed, not
  saved (as the original). Locks are on `CharacterCold.DialogueLocks`, hashed while set, saved as `lok*` (`DialogueSave`).

## AI

`AiSystem`, blackboards/minds, package/task data: slot/class/time/weight selection, homes/beds/nodes,
patrol/bar/shop/sleep/retreat/contracts, combat stances and off-screen roaming jobs. Data and
TaskData semantics: [ai.md](game/ai.md), [ai-tasks.md](game/ai-tasks.md). Wars and prisoners are incomplete; residents
off screen only come back where their schedule puts them ([off-screen tier](#off-screen-tier)). Hash blackboards/minds/jobs when present.

### Planner

`TaskPlanner` runs the original's search ([ai-tasks](game/ai-tasks.md#planner)) over `TaskDataTable` (all 253 rows) for the
goals the engine acts out. The think orders each tier's goals by score, leaves out one whose list A holds, and takes the first
whose plan succeeds (`AiSystem.Choose`); `AiMind.Plan` holds it leaf first and `AiMind.Step` (hashed) its first step. The goal's
act-out does the walk and the goal's own action; steps of their own are getting out of bed and sheathing (one think each). A plan
whose first step is neither the goal nor a step the engine carries out (`AiSystem.Carried`) fails. Engine choices:
- Everyone plans with category 0x1; no character is squadless, so restricted rows (lockpicking, cutting, opening gates) never
  enter a plan. A row without a finder wants its effect for its own target. At most 64 nodes per search.
- A node is made once per row and target (the original allows four copies), so the ancestor-repeat test never fires; a child already
  closed is skipped rather than accepted from the node.
- Only the step being carried out counts as queued, and only while it can run (Meitou's steps do not end on their own).
- Finders: the spots, beds, seats, turrets and towns the act-outs already pick, at the floor point the walk ends on (`PlanContext.Find`); other finders find nothing,
  so such goals do not plan. Rows on the way have their `TaskProfiles` desire, else 1.
- States (`PlanContext.Holds`): location, distance, bed, sitting, home (within 20 of the home), home town, town, weapon drawn and
  carrying are evaluated; moving, getting up, arms, power, buildings, doors, gates, accessibility, machine slots and readiness are
  held true (movement and the lock-up round deal with doors); every other state is false.

Combat stances (`AiStance`, the most aggressive combat goal in its hours; none at all attacks on sight): PROTECT_OWN_SQUAD joins only
fights of its own squad, against a mate's target or a mate's attacker (as the original's finder, [ai-tasks](game/ai-tasks.md#combat-goals));
the other PROTECT goals still join any fighting enemy (engine).

Scoring follows the original (`TaskProfiles`: desire, commitment, crowding per goal type); a desire
the original computes in a function of its own counts as 1. Town goals ([ai-tasks.md](game/ai-tasks.md#town-goals)):
wandering and patrolling in town walk to a general node of a town building, shopping to a node of a
shop, relaxing under a bar package and sitting around (275, in the home) sit on a seat claimed per
member on the package cadence, and the wanderer walks to the nearest town. A home node goal takes a
node of its kind, a guard post a general node next (as the original), then any node but a shopkeeper's
(engine; only the shopkeeper goal falls back onto the counter). Gates have no nodes: the gate guard
(180, the home gate, else the town's gate nearest the squad) stands 15 out from the gate's first door
leaf, on the side away from the town centre (the side is an engine choice), facing out; a member whose
guard or general node goal finds nothing in its home gate flanks the opening on that side, beyond the
leaf's edges (half width + 6, steps of 7), alternately left and right in two rows 8 apart (engine).
Headless (no door gate, no leaf) they keep their posts, the gate building's origin, which on Defensive
Gates is about 110 along the wall from the opening. Seats are the home's
interior furniture with a CHAIR functionality (`HomeSpotKind.Chair`; never a standing node), free
in that building this pass; relaxing falls back to a bar node, then a general node, sitting around
has none and is not wanted without a chair (without a home it sits where it is). A seated character
is put on the seat's origin with no offset, facing the seat's way ([ai-tasks](game/ai-tasks.md) "Sitting"), and plays the functionality's clip ("sitting chair", pillows
"sitting idle"). A town's bars and shops are
the buildings whose residents (the squads living there, else the level data's `residents template`)
have designation BAR or a shop (`AiSystem.RefreshPlaces`). Walks to nodes, beds and seats pass the
spot's height and count as arrived only on that floor, and end within `MovementSystem.SpotArrive` (2) of
it, not `ArriveRadius` (10), without separation over the last 10 (engine choice: the barman stands at his node). Furniture cuts holes in a floor's navmesh and
the ground below shows through them (Observed: the Storm House at the Hub, counter, tables, stools, 22..28
below), so a spot whose floor is not under it is walked to (and residents spawned) at the nearest floor
point within 12 (`IWalkability.FloorNear`), and no step goes down into such a hole (`StandDrop`,
[pathfinding](game/pathfinding.md)). The Storm House's upper storey (+66) is on the navmesh except under its beds, whose
spots therefore stand on the floor below (Observed, 2026-10-18 probe). Engine choices: patrolling town acts as
wandering (its task class is undecoded); a committed goal chosen again after its commitment ran out
starts over with a new target; training and throne furniture of 275's mask are not used; shops stand customers at a general
node, else a guard post, else any node; the first bar of a town is the package's; a home or seat given up
after three failed walks is tried again when a new goal is taken up, and a member that gave its seat up
leaves it to the next; a member keeps its seat while it still relaxes or sits (every squad's kept seats are reserved before any claims), so a
patron coming or going does not move every later sitter to another chair. Headless Hub, Squin and Stack bars (2026-10-21): each patron sits once
and stays for 12000 ticks, with or without that rule (the reported sit/stand churn is not reproduced).

Turret goals (149, 222, 234, 146) man a turret of the squad's town: a placed BUILDING with a
`BUILDING_FUNCTIONALITY` function 12 (`TownBuilding.Turret`: its `use range` and `gun turret`
GUN_DATA). In member order each slot claims the nearest free turret and walks to a spot behind its
gun, facing its way; the home-building goal (222) takes only a turret mounted on its home (the
state's `mounted` handle). Claim order and gunner spot are engine choices (the game's turret state
is undecoded); turrets do not fire yet (no power, aiming or damage model,
[combat.md](game/combat.md) "Ranged and turrets").

## New-game difficulty

`NewGameSettings` in the game host binds seven numeric options to each new world: body hunger/death/damage,
production/research, construction and nest density. Defaults are neutral (1); ranges follow
[researched bindings](game/character-stats.md#new-game-advanced-options-that-change-these-mechanics).
The two new-game panels share one modal screen; expanding raises the start panel, and its Begin/Cancel remain usable.
Opening a fresh new-game dialog resets the draft; collapsing keeps it. Failed world replacement retains the running
world's options and body factory. Saves carry seven `meitou difficulty <key>` CAMERA floats (hunger, death, damage,
production, research, building, nests). Missing/nonfinite values use 1; out-of-range values clamp to the slider range.
These are Meitou extensions; original difficulty save encoding is not implemented here. Saves without the extensions
use neutral values. Bandit looting and easy prospecting are not exposed.

## Saves

`Meitou.Data.Save` parses/writes folders; `Meitou.Simulation.Saving` maps world state. Capture
serially at a boundary, write/read on workers; `ReplaceWorld` publishes a successfully loaded
world and preserves the running world on failure. [save.md](formats/save.md) owns file keys,
handles and original saved/derived/regenerated state.

Code map:
- **Capture.** `SaveCapture.PrepareBase` clones the loaded `SaveGame` (else builds a fresh one with `SaveBuilder`). `Capture` writes what
  the world models over the clone, so every key Meitou does not model passes through. Characters from the loaded save keep their own records
  (`WriteCharacter` gets their `SaveCharacter`; `SaveBuilder.AddCharacter` only makes new ones). Per-system key helpers live in `Saving/*Save.cs`.
- **Host hooks.** `SaveCaptureOptions` carries what only the host knows: `Bodies`, `View` (the strategy camera). `GameHost.CaptureOptions`
  fills them in. `GameHost.SwapInSave` applies host-side state after `SaveLoader.Load`: the camera and the weather.
- **Load.** `SaveLoader.Load` makes characters before the first tick. The character table copies its previous state over the next one at
  `BeginTick`, so state that must reach a tick is not written into `Next` there. It goes through `World.Commands` like player input.

### Quicksave and quickload

F5/F9 and named save menus share mapping. Restore clock, player money/faction, relation/prosperity,
player squads, character transforms/names/roles, stats/medical parts, appearance, inventories,
bounties, bed/cage state and sentence, and registered buildings' locks and doors (into the zone files: every visited zone gets one, made from the level data when the
loaded save has none, [save.md](formats/save.md#zone-files)) and selection; the host restores and writes every region's season, weather, strength and wind ([BIOMES](formats/save.md#biomes-type-28-quicksave-weather-per-region)). NPC platoons whose `basetown` is a placed town load as their saved characters at their saved positions: town-bound ones (not travellers) are pinned to the town (no trips, AI-run, homed in the building their `homebuilding` handle names, else at the site) and a town holding any makes no squads of its own; travellers become roaming stand-ins made from the saved characters on activation. A platoon that unloads keeps its members' state. Meitou saves write them back and the population's own squads as new platoons with their town and home building by handle ([save.md](formats/save.md#npc-platoons-meitou-mapping)). A save's nests are the wild layout's first, and its wild platoons load as themselves bound to their nest (`hometown`) or the sector they stand in, which make only the rest; Meitou saves write the nests made so far and the wild squads the original's way, the others rolled again from the saved seed ([save.md](formats/save.md#nests-town-list)); nest buildings and debris are not placed at all (`TownData.Debris` / `DebrisBuilding` are read, unused), so there are none to save. Shops, campaign phase clocks and whole-body ragdolls are saved (Trade, Campaigns, Ragdolls above). Orders/paths, animation,
combat engagement, wound-list/pain, base-game building state beyond locks and doors (interiors, inventories: a new zone file leaves them to the original's
generation), ground items and game speed are not fully restored. The camera view is ([saved camera](formats/camera.md#saved-camera)). Loaded hashes need not match captured hashes: compare carried fields and agreement
between repeated loads. QuickSaveTests checks carried fields and repeatable loading.

**Crime and jail** (keys in [save.md](formats/save.md#locks-cages-and-sentences)). Saved: bounty ledger, held state and sentence, the escaped flag, the recently-wanted
and recognition timers (player platoon characters, written only while set), the locks of interior cages and containers (an imprisoned character stays locked in), and the homes a
lock-up round locked (without the flag a loaded night's locks would never be undone). **Restarted, not saved** (engine choice; the guard, hunter, releaser and warden of each job is a
town NPC the population makes again with a new id, so a saved link would dangle): arrests (a downed target lies where it fell, is spotted again once awake, the saved markers make
that quick), cagings (the carry link is not saved: order Put in Cage again), hunts and their rest timers, release, lock-up and warden rounds (the 30-tick sweep finds
them again), the unreachable-cage memo (relearned by a failed walk), lock picking (an order), pending crimes
(seconds long, victim id unsaveable). Spawn bounties are not saved: they are rolled when the population makes an NPC, never for a loaded character.

## Sandbox

Headless simulation/debug harnesses use selected StandardParts and synchronous services for
repeatability. Sandbox omits Aggro when its scripted foe must remain idle; benchmarks must
separate simulation work, startup and rendering.

## Duel

The deterministic duel harness locates a scripted knockout, runs the pre-lap fight headless,
then replays the last 40 seconds and 4 after KO with cinematic keys. Replacing the world/clock
and resetting camera evaluation is required for repeatable recording.

## The game host

`Meitou.Game/GameHost*.cs` adapts input to commands, drives WorldSession, navmesh upkeep,
interpolated CharacterDrawList, HUD, loading/transitions and saves. It does not duplicate system
order. Renderer state lives in Rendering; snapshots are the simulation-to-host boundary.

## Open questions and known gaps

Original-game unknowns are maintained in each linked subsystem reference; do not infer parity from our determinism tests.

**Engine gaps:** remaining AI is listed in [AI](#ai); persistent town/world state, power, hauling, crafting, outposts, full UI fidelity and physics remain incomplete (walls: see [Buildings](#buildings)). Save and combat limitations are listed in their subsystem sections.

**Behaviour and hash constraints.**
- No end-of-tick hook: the combat system swaps its arrays at the end of its `SlowWorld`, so the result depends on the system order
  ([Systems and their order](#systems-and-their-order)). Moving the swap would change what retaliation reads.
- The two `MedicalContext` builders (`BodySystem` and `CombatSystem.MakeContext`) agree on toughness, strength, encumbrance (the body system writes it to
  `Fighter.EncumbranceFactor`, 2026-10-13) and seed, and differ in `BodyTimeScale` (combat's is 1) and `Resting` (combat's is false). So they are equivalent only at time scale 1 and for a
  character that is not resting; with the game's `TickMedical = false` the combat one only serves hits, where neither matters.
- Combat ticks are `int` (`CombatSlot.ReadyTick`, `StunUntil`) while the world's tick is `long`.
- `ItemInstance.StolenFrom` is not included in the inventory hash. Adding it requires updating the affected carry/crime pins; pins are platform-specific.
- `BodyHash` does not cover the private `starveGrace` and `starvationKo` of `MedicalState` (nor every flag such as `BloodKo`), so a divergence
  there shows in the hash only later.
- `CharacterHot.Animation`/`AnimationTime` (the old movement layers) and the name length are still hashed; removing them moves every hash.
- `HoursPerTick` is a constant of its own, not derived from `WorldSettings.TickSeconds`.
- Navmesh: the cache key does not see a changed collision file; a running build is not stopped on unload (it finishes into the cache).
