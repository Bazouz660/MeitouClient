# Pathfinding and movement

Kenshi NavMesh/Havok/CharMovement research, scoped binary/log/file evidence below. Runtime uses
our DotRecast builder (`Meitou.Navigation`), not proprietary Havok SDK. [Collision](../formats/collision.md),
[terrain](../formats/terrain.md), [stats](character-stats.md), [simulation](../simulation.md#navmesh).

## Space and tile layout

**Verified** Havok coordinates=Kenshi*0.1;1 game unit=dm, speed*mph factor0.22369362.
Zone4608,64² grid; exterior UID=(Z<<8)|X, interiors>=0x10000 with unknown composition.
**Observed** face key upper10 bits instance, lower22 face. Floating origin keeps Havok small.
NavMeshMain owns mutation/messages, sleeps10ms idle; generator sleeps100ms and dispatches queued
full/partial/interior/stitch jobs. 3x3 active-zone loads and runtime stitching observed in logs.

**Verified** navtiles:3995 Havok tagfiles, magic1E0DB0CA CEFA11D0, SDK hk_2014.2.0-r1,
hkRootLevelContainer; hkaiNavMesh faces16bytes/edges20, graph/streaming/mediator/static-tree objects.
Named variants NavMesh,Graph,Mediator,Hash then interior groups with Info=`uidhex;x;y;z`.
294 tiles contain1249 interiors; Info positions are absolute world units, possibly outside tile.
**Observed** (tile20.32, Storm House `600815`): an interior group is NavMesh,Graph,Mediator,Info,Hash; its vertices are
0.1-scale offsets from the Info point, which lies below and outside the min corner of the building's interior hull (~55 in X/Z, 66 in Y).
Tile vertices zone-local at0.1 scale (tile0.0 waterY10, width460.8). Seeds.def is4585 float3,
55020 bytes/no header; Y=-99 unspecified height. Hash mismatch/missing tile triggers generation.
**Observed** hash_combine over building positions/rotation/filter fields, zone value last;
empty tile hash9e3779b9. Mods override through zone resource path.

## Geometry and queries

Original generator gathers terrain, waterY100 material3, collision-group mask0x809de40,
placed building/foliage/interior geometry, seeds and material/face flags. Relative-origin input
scales0.1. Walkability/slope, clearance, floors/doors and stitch transitions differ; do not infer
navigation geometry from render meshes alone. Source collision decoding: [collision](../formats/collision.md).
Runtime face/edge filters include doors/base walls, water-avoidance and interior restrictions.
Havok paths, local steering and unloaded-platoon movement are distinct code paths.

## Speed and orders

**Observed** S=max(11,(V-11)*leg*hunger*replacement*encumbrance*statScale+11),
V=lerp(speedMin,speedMax,athletics/100)*(0.5+0.5*slot115). Path agent sets top-speed velocity
immediately; pathfind-acceleration brakes before corners, not a universal acceleration ramp.
Combat/follow steering ramps15/s, with turning/backward/arrival caps. Combat-move-speed multiplier
and water factors0.75/0.5/0.4 apply separately (`FUN_14065dba0`, by water state 1/2/3; Meitou does not apply them yet). No independent slope-speed term was found.

### Water states

**Verified** (`FUN_1405c7fd0`, `FUN_140886460`, `FUN_140885eb0`; Meitou `Speed.WaterState/InWater/Swim`, `MovementSystem.WaterStateAt`):
- State of a character at height Y with body size g (`vtable 0x390`, 1 for humans): 0 when Y+2 is above 100 (the water level). Else 3 (deep) when 15g+Y <= 100,
  2 (waist) when 6g+Y <= 100, else 1 (shallow) only for a race with `swims` (race +0x79), otherwise 0. A carried character uses its carrier's.
- Top speed S replaces the run speed: state 1 caps it at 45; in 2/3 a race that does **not** swim (skeletons, fishmen, robots) walks the bottom at 2 x race `walk speed`; a swimmer's
  state-2 speed is its `walk speed`, its state-3 speed the swim speed.
- Swim speed = clamp(`swim speed mult` x swimming x 0.02, 6, 25) x leg x hunger x limb-item (all four limbs, stat 23) x encumbrance factors x 0.6 per unusable arm, at least 4; 1 with no usable
  arm. A human swims at 6 whatever its skill.
- Training: in state 3 swimming gains 5 XP (10 carrying) per second instead of athletics.
- Animation: category 4 (SWIMMING) records (`swim`, `swim carry`, `swim carry left`, `swim idle`) are the swimming clips; getting up in deep water plays `swim idle`.

**Observed/engine choice:** the original's Y is the height it stands at, in water the seabed; Meitou keeps a character on the navmesh's flat water surface (Y about 100), so the depth is read from
the terrain under it while Y is within 2 of the water level (a deck above that is dry), and size is taken as 1. Only the drawn height follows the original's idea (`World.DrawnPosition`,
`Speed.WadingHeight`): the seabed, and for a swimmer never below 100 - 15g = 85, exactly where the deep state begins, so wading and swimming join without a jump. **Observed** (`male_skeleton`): the swim clips' `Bip01` sits
about 12 above the origin (`swim2` +2 over the 9.99 binding height), so a swimmer whose origin is on the surface floats; at 85 its pelvis is about 3 under the surface. A deck within 2 of the water level counts as water (engine limit).
**Unknown:** what `+0x2f8` (nonzero returns 0) and `+0x3d4` (carried) of the character are exactly; swim offset (`+0x68`) use; the HUD/AI `me swim` gating
([ai-tasks.md](ai-tasks.md)) and animals' swimming.

Disabled legs,
body states and stealth modify distinct branches. Movement modes0 walk,1 cap55,2/3 cap999;
mode3 follows group's slowest. The HUD sets them as character orders 2/1/0/0x10 → modes 0/1/2/3
(Walk/Jog/Run/Group; **Verified**, character vtable slot 0x308 FUN_1405caa50, [ui-screens.md](ui-screens.md)). Whether
"group" is the squad or the selection is **Unknown** (the Group choice also calls a selection-wide FUN_1407faa70); Meitou
uses the squad members walking in mode3. FCS force-speed3 means NO_SPEED_CHANGE and remaps to4, not runtime
mode3. FCS `MoveSpeed` (editor enum, **Verified**): WALK 0, JOG 1, RUN 2, GROUPED 3, NO_SPEED_CHANGE 4. Follow/avoidance/formation use character footprint and previous neighbour state.
Original formation details and off-screen arrival remain partly unknown. Whether living characters block each other beyond the
separation steering is **Unknown**; sitting characters are never pushed (**Observed** in play) (Meitou adds solid body contact: [simulation](../simulation.md#movement-and-paths)).

## Own builder

DotRecast: cells2,height1,tile48 cells,agent18/climb5,ground/water/door areas. Build asynchronously,
cache tiles and gate population on readiness. Query A* through portal clearance (radius per wall end; tile-border, seam
and area-change ends are open) and water/door costs. **Observed** (zone 18.39, `meitou-tools navmesh`): a Swamp Village
shack's stair straddles the tile line x−62208 and its foot came out as 6..8-unit portals, so a 2*radius rule kept radius-4
agents under the house; the west half's foot meets the walkboard in a near-miss T, joined as an inner seam. Cache key and unload rules: [simulation](../simulation.md#navmesh). Original Havok tiles are
reference evidence, not a runtime dependency or proof of DotRecast polygon parity.
**Terrain** as the original: every second heightmap sample (36 units), slope 40° per triangle. **Observed** (zone 20.32, `meitou-tools navmesh`):
at 18 units each right triangle (legs 18) near 40° was walkable or not on its own, and 2-unit cells kept every one, so cliff fringes were saw teeth
and spikes; at 36 the outline matches `tile20.32.hkt`, terrain triangles drop 139k → 35k, polygons 15250 → 13602.

**Furniture** comes from building layouts (`BuildingLayouts`), none when destroyed. **Verified**
(Hub, `meitou-tools navmesh`): parts with collision 189→309.
**Interior parts**: the `interior` list (laid out like `parts` after the same position-seeded reseed, as drawn) is collision too,
none when destroyed; it holds interior stairs and upper floors. **Verified** (tile20.32 interior `600815`, the Hub Storm House
`4933-Newwworld-INGAME`): the original mesh ramps +4→+68 over (+30..+65, −45..−5) from the origin to the roof storey at +69..+72,
inside the bounds of `Storm_House_01_Interior-stairs` (`is stairs`, only in `interior`). **Observed** (scratch NavSystem probe):
without the list that storey and the Watchtower `2-Newwworld-INGAME` floors +130/+190/+290 were unkept; with it paths from the door seed reach them.
**Destroyed interiors**: a part with a `destroyed mesh` but no `destroyed collision` keeps its intact `xml collision` (**Verified**,
`FUN_14055f6e0`; with no `phs or mesh` its `xml collision` is cleared instead). A destroyed building has no doors, so its interior has no seed.
**Observed** (original tiles 20.32, 18.35, 19.29, 20.29, interior groups decoded as Info + 10 × vertex, matched to Meitou's interiors by box):
the original keeps a mesh in each destroyed seedless interior, on its ground floor only (e.g. Storm House `32-Newwworld-INGAME` 6 faces at
Y1571..1578, Bughouse `4937-` 32 faces at Y1608..1619). Meitou's largest region there can be wall tops (Bughouse: 149 polygons at Y1710..1757
under the ruined roof) or an upper floor (Stationhouse `4-`, Y1703). Engine choice: keep every region whose lowest point is within
`SeedPruner.DestroyedFloorBand` (40) of the interior's lowest polygon; upper storeys there are 60+ higher. Without it, squatters housed in
these nodeless ruins stood on the terrain under their floors. The island has no door join, as in the original.
**Doors**: every part of a `doors`-list building is group 5. **Observed** (35 door records): only
`Defensive Gate level 4/5 Door` lack `is door`, yet open in game; without the rule Squin was sealed.
**Entrance seeds** (engine choice): cross the door's width axis, then use the lowest collision floor with agent-height headroom.
**Interior seeds**: without a door seed the building's first shopkeeper or general node of its interior layout is the seed (the original's rule, below);
Meitou also uses it when the door seeds find no polygon (engine choice). **Observed** (`meitou-tools navmesh`): doorless Hive Commune Huts (10.31
`50-Newwworld-INGAME`, clipped at the zone border) kept nothing before and 75 of 102 polygons with it; Stack's Old Storm House `14-` (19.29), whose
door seed misses its floor, 0 → 73 of 113 (its door still has no join).
**Observed** (35 base door records and their box collision extents): `door navmesh axis` 0 selects exporter X width, 1 exporter Y width;
using that width as the crossing direction leaves Shark's Swamp House `321-Newwworld-INGAME` room unseeded.
Its platform has overlapping upward faces near Y122/133; the lower face lacks headroom. A zone21.42 route from
(-47010,133,48970) to (-46960,133,49005) crosses its doorway with the corrected markers/floor selection (`meitou-tools navmesh`).
**Door joins** (engine choice): an interior's door polygons link to the exterior's door polygons of the same door; when none face them,
to any kept exterior polygon facing their open edges. **Observed** (zone18.35): the L-House `13-rebirth-INGAME` and `15-rebirth-INGAME`
door leaves sit 5..14 inside the interior hull, so the hull-carved exterior got no door polygons and the rooms were sealed.
When neither joins, the interior polygons connected to the door polygons within 32 units of them join the exterior polygons their open
edges face: a raised sill reached by steps (~47..52°) that cross the hull, whose lower part is the exterior's (cut at the hull: Y351 for
`29-rebirth-INGAME`, door Y365..380; Y418 for `27-`, door Y430..441) and upper part the interior's, meeting on the hull's edge.
**Observed** (zone 18.35, scratch probe): both rooms reached from the street; no other door in 18.35, 19.29, 20.29, 20.32, 21.32, 21.42 needs it.
Joined edges may lie up to 6 apart (`NavInteriors.JoinDistance`), so an A* portal across a link takes the overlap with that gap. **Observed** (zone 21.31,
`meitou-tools navmesh`): the Rebel Base Storm House `3384-TwoStorey`'s exterior door edge slants 2.0..2.3 off the interior's (z −1156.3); with a
2.0 gap the portal was 0.2 wide and radius-4 agents never got in; with 6, (-50620,-1080) → (-50617,-1232) `--to-y 1080` walks in, and back out.
**Inner seams** (engine choice): Recast welds polygon vertices only within 2 height cells, and a step's corner height can come out
differently on each side, so a stair foot and the ground beside it can share an X/Z edge without being neighbours. The stitcher
joins open edges of one tile that coincide reversed and are within the climb at both ends. **Observed** (zone 21.42, `meitou-tools navmesh`):
Swamp House `48-Newwworld-INGAME`'s stair foot met the ground at Y124 vs 127 and kept only a 4-unit tile-border link, so radius-4
agents had no way up; with the seam, ground (-47000,125,48100) reaches the room (-47229,178,47969) (the dome `805-rebirth-INGAME`,
`6-` and `4-` were already reachable). Its deck (Y~178) was never pruned: the red there in `--png` is roofs, beams under the deck and interior fragments.
Contour simplification also leaves near-miss T seams (one edge against two whose middle vertex is off its line). Open edges of one tile left
unjoined are linked (`Links`, not `Neighbours`) when they run opposite, share one end in X/Z (so floors either side of a thin wall stay apart),
lie within 1.5 of each other's line at both ends of the overlap, and are within climb + 0.5 there; A* portals for such links take the overlap
with the door joins' 6 gap when no facing edge is within 0.2. **Observed** (zone 18.39, `meitou-tools navmesh`): the Swamp Village stair foot's edge
(-62220,108,34088 → -62208,105,34086) against two edges meeting at (-62216,106,34086) joins both; the path (-62218,34080) → (-62216,34148)
`--to-y 140` climbs through that vertex (length 83) instead of detouring to the tile line x−62208 (97).
**Tile seams** (engine choice): tiles are rasterised with a 3-cell border from their neighbours (Recast `borderSize`), so contour
vertices on a tile's edge are kept and those edges come out as portals. Without it Recast treats the edge as a wall and simplifies across
it (error 1.3 cells): a strip one cell deep along the edge loses its edge vertex and the stitcher finds no edge on the line. **Observed**
(zone 19.29, scratch probe): the Longhouse `4-Newwworld-INGAME`'s outside ramp ends on the tile line z−13152 one row short of its
doorstep, which was cut off from the street and its door never joined; with the border the path from the street reaches the room.
Kept area grows by 0.04..1.1% (zones 20.32, 21.32, 20.29, 18.35) while polygons drop 20..45%.
**Zone borders** (engine choice): each zone is pruned by its own seeds plus every region with an open edge on the zone's border line
at ground level (edge middle within `SeedPruner.BorderGroundSlack` 30 of the terrain, or the water height over it; roofs and walkways
crossing the border stay pruned), and cached that way. The original seeds a new zone from already built neighbours' border midpoints
(order dependent) and ships every tile built, so a region going on into the next zone is kept wherever its seed is; Meitou loads zones
alone (only those within 340 of a walker are active), so it cannot wait for the neighbours. **Observed** (scratch probe, original tile
polygons vs Meitou's unpruned polygons sampled every 25 units): with own seeds alone Meitou kept 3.1 % of the shared area in 14.26
(1 seed), 15.5 % in 25.13, 42 % in 26.14, 94..100 % elsewhere; with the border rule 98.9..99.9 % in all of 14.26, 13.26, 18.35, 19.29,
20.32, 21.32, 25.13, 26.14, while area kept where the original has none rises to 3..5 % of the zone. When a zone loads, points on the
kept edges of each shared border also seed the neighbour's full mesh (and back) until nothing grows (interiors cut by a border need it).
A path that fails from a pruned polygon keeps the region the walker stands on (`NavMeshService.KeepAt`, not cached) and asks again.
**Observed** ("6 dudes" save): a character in 14.26, 2,300 from its borders, stood on a pruned polygon and could not path; the original
`tile14.26.hkt` has a polygon under it (y 702). **Observed** (World's End, zones 25..27 × 12..14, `meitou-tools navmesh --around 1`): with neighbour seeds
zone 26.14 kept 7,943 polygons in raster order and 3,790 in reverse; with the closure every order gives the same kept sets and paths.
An interior whose mask crosses a zone border is built in each zone it reaches, its box ending on the border line; a part without a
seed keeps nothing (no largest-region fallback) and is kept through the closure from the part with the door. **Observed** (zones
19.29/20.29, scratch NavSystem probe): the Y-House `2-Newwworld-INGAME` (x−55338, border x−55296) and Old Storm House `14-` have their
doors in 20.29; built only in 19.29 they had no door join and their 20.29 half was invisible to point queries. Now a path from the
street reaches the door and the far end of the Y-House. Doorless interiors on a border (20.32/21.32 `20-`, `26-Newwworld`) lose their
fallback island.
**Heights**: **Observed** polygons are up to ~15 off over stairs/ramps (Hub gate), so standing
heights snap to walkable collision (`NavSurfaces`, also its edge within `EdgeReach`: the mesh overhangs raised floors, e.g. the Hub bar landing) or zone terrain (water-surface polygons keep their height); the layer is picked by mesh or real height within
`StandRise` of the character wins. A layer more than `StandDrop` (12) below the character is not stepped onto (engine choice: the
ground 22..28 under the Storm House floor, Observed, showed through furniture holes and walkers fell to it). A snap more than `StandRise` off the
character takes the collision surface at the character's own height instead when that is within `TerrainSnapRange` (30) of the mesh (engine choice;
**Observed** at the rock by the Hub's gate, -51686, 2740: our polygon spans its top (1595) and slope, sags to ~1580 and lies nearer a face inside the
rock (~1567), stranding a walker on the top; the original's cached tile has a polygon flat at 1594.8 there); with no layer in range, a
polygon within `EdgeTolerance` counts. A character already off every layer near its height still steps down.
**Long routes** (engine choice): goals in unloaded zones use `CoarseRoute` (192-unit terrain grid, slope and
water costs; a step whose centre line crosses a building's footprint hull costs `BuildingCost` 40×, so routes go round
walls and towns where open ground allows (a 5.5k-unit wall is walked round; 8× crossed it); the player's buildings are a second layer,
redrawn on the pool when one is added or removed; IGNORE `path mode`, gateways, destroyed buildings and WALKABLE ones that are features or
under the agent height, i.e. bridges, walkboards, pontoons, platforms, are left out; WALKABLE covers walls and houses too, **Verified**
against the placed records); the navmesh part ends inside loaded zones, the rest is provisional and
re-asked; its zones within `Lookahead` load early. A goal in a loaded zone with no navmesh path is a long route too when its zone is
in another block of side-by-side loaded zones than the walker's (two groups of walkers far apart); within one block, the navmesh's
nearest reachable point and a straight provisional tail (the terrain search, under one lock for every path thread, is not run there;
a way round through an unloaded zone within one block is not found). **Observed**: Hub→(-36000,-6000) and Hub→Squin arrive; "6 dudes"
save, scratch harness with the host's zone loading: Fog Islands (-80620,-24171) → the squad at (-31620,11420), 60.5k units, arrives
(17,506 ticks); before, it walked to the nearest reachable point and stopped on a cliff 10.6k short.
**Roads** (original, **Observed**): `CharMovement::vfunc_18` (`FUN_140661270`, set destination) tries the road route
(`FUN_14065f940`, the off-screen mover's [rule](factions-squads-towns.md#off-screen-world), its own gate 10000 units) for trips over 1000
units when no road path is active, with w from the character's faction `road preference` (CharMovement +0x100, set in `FUN_140661f90`);
after over 16 navmesh failures it falls back to the road route ("trying road network"). Nameless has 0.3, so player squads take roads too.
Ours: the long route asks `RoadNetwork.Route` first (`NavAgent.RoadPreference`, `CharacterCold.RoadPreference`, kept on recruit/dismiss);
when taken, the legs to and from the road are terrain-grid routes (engine choice) and the navmesh part reaches at most `RoadHandover`
(1500) along it, so bends are not cut. Not built: the rule for a trip within one loaded block (navmesh only), the failure fallback.
**Observed** (scratch harness, nothing loaded): Hub→Squin 17657 straight walks 25522 at 0.3 (99 % within 400 of a road; 43 % at 0);
Hub→Stack 34054; Squin→Admag at 0.8 walks 132184 for 26834 straight (short road `length` values, as off screen).

### Generator (runtime and tool-time navmesh building)

- Thread "NavMeshGenerator" (FUN_1403ce430): sleeps 100 ms when idle, works only when the manager's queues are empty
  (**Verified**: the thread reads the manager's counters and only then calls the job dispatcher). Jobs are dispatched by
  FUN_1403ce170 on a type in `job+0x58 & 7` (**Observed**): 0 gather inputs (FUN_1403cbfa0, "Starting generation task") then
  stitch (FUN_1403cb840); 1 gather inputs, build the mesh (FUN_1403ca3d0, logs vertices/faces/edges and "Stitched N
  additional edges") then stitch; 2 interior (FUN_1403c7560, FUN_1403c9cb0) then stitch; 3 an interior with a given AABB:
  gather inputs and stitch with FUN_1403cbbf0; 4 stitch only (FUN_1403cb840 for exterior uids < 0xffff, FUN_1403cbbf0
  otherwise; FUN_1403cb280 stitches with completed tasks and existing or unloaded zones). Job adders: FUN_1403c6820 "Full",
  1403c6a20 "Partial", 1403c6fd0 "Interior", 1403c71c0 "Stitch", 1403c6b60; the log prints "Job added: ...". Which adder
  produces which numeric type is **Unknown**.
- **Inputs** (the gather pass FUN_1403cbfa0, **Observed** unless marked; collision files, placement and groups in
  [../formats/collision.md](../formats/collision.md)). The job carries a zone (or, for interiors, a building), an AABB in
  Kenshi units and an origin; everything is put into one `hkGeometry` (triangles with a material int each) relative to
  the origin and multiplied by 0.1. The job type (`job+0x58 & 7`) chooses the branch: 3 builds an interior, 0 and 1 an
  exterior box.
  - **Terrain** (FUN_1403c16c0): the zone's height grid (FUN_140a08a20) as an N × N vertex grid over the zone, two
    triangles per cell, material −1. FUN_140a08a20 was not traced, but **Observed** (`tile20.32.hkt`): the cliff outlines step on every second
    heightmap sample, a 36-unit grid (N 128) from the zone origin, cut diagonally as our grid (corner (i+1, j) to (i, j+1)); of the tile's 8642
    mesh-end vertices 2739 lie on a 36-unit line and 2740 on an 18-unit one (chance ~1600), so the odd samples are not used.
  - **Water** (FUN_1403c0250): one quad over the zone at the water level (from the node DAT_142135c48, 100 in the base game,
    [../formats/terrain.md](../formats/terrain.md)), material 3, only when the water level is above the lowest terrain
    vertex of the zone.
  - **Building and foliage shapes** (FUN_1403c8660): every collision shape in the AABB whose PhysX group is in the mask
    0x809de40 = groups 6, 9, 10, 11, 12, 14, 15, 16, 19, 27 (**Verified**: mask bits counted). So floor 0..3 parts,
    stairs on floors 0 and 1, floor-0 furniture, unwalkable roofs, both kinds of foliage collision; not doors (5),
    interior hulls (13), upper stairs and furniture (17, 18, 20..22), passable / IGNORE / unfinished parts (23..26 and
    above). Per shape, with the owner found through the shape's handle (only handles of type BUILDING resolve):
    - no owner: group 14 (walkable foliage) → material −1; group 6 (other foliage collision) with an empty handle →
      material 0; anything else is skipped.
    - owner a building with `path mode` IGNORE (0): skipped (and dropped from the list).
    - `path mode` WALKABLE (3): material 1, except 0 for an `is unwalkable roof` shape (group 27), and 0 for shapes outside
      groups 9 and 19 when a building sub-object flag (+0x1f0 → +0x2c, meaning **Unknown**) is clear. When that flag is
      set and the building is neither a child nor a door, its upper-floor contents are added too: shapes in groups 17, 18,
      20, 21, 22 (mask 0x760000) through the interior collector below.
    - PROJECTED (1) and OBSTACLE (2): material 0. None of the nine functions that read `path mode` compares it with 1
      (**Verified** by reading each comparison), so **PROJECTED behaves exactly like OBSTACLE**; the tooltip's "2d
      projection" has no counterpart in the code.
    - other owners (not buildings, not doors, not the building's own interior parts) are skipped by flags of the owner
      (+0x1a0 door flag, +0x1f0 sub-object), not decoded further.
    - shapes become triangles as in [collision.md](../formats/collision.md#shape-to-triangles-observed-fun_1403c1cf0)
      (box, capsule as a 16-sided prism, convex hull, triangle mesh; planes and spheres give nothing).
  - **Carvers** (FUN_1403c4390, "N building carvers"): shapes of group 13 (the `interior mask` trigger hull) whose building
    has `BuildingClassType` 12 = BCTYPE_SHELL_WITH_INTERIOR (the class at +0x198, virtual slot 0x358; enum order from the
    FCS) and the +0x1f0 → +0x2c flag clear; each becomes a carver (FUN_1403c1ae0 builds the volume, FUN_140dd9e80 wraps it as an `hkaiCarver`
    with flags 0, i.e. without CARVER_ERODE_EDGES, the only flag). A carver removes the navmesh inside it, so the exterior navmesh has a hole where a house's interior is; the interior is built separately.
  - **Door painters** (FUN_1403c4520): door shapes (group 5) of door buildings, searched in the AABB grown by 20 units on
    every side (the 20 is a search margin, not a painter size); the door shape's hull, its pose shifted by 5 units along
    one local axis, becomes a convex painter volume with material 4 (open door), so faces under a door get face data 4.
    Doors are therefore **painted, not obstacles**: group 5 is absent from the shape mask, and material 4 is the face data
    the door toggle and the cost modifier use (**Verified** as a consistent pair: mask bits, painter material and the
    toggle's 4/5 test).
  - **Foliage cutters** (FUN_1403bfa10): every foliage instance with a FOLIAGE_MESH `navmesh cutter` radius r (168 of 748
    meshes, **Verified** count; 37 of them also have a `collision` file, and fcs.def says the radius is then unused, which
    was not traced) becomes an axis-aligned box carver, centre ± r horizontally and
    ± 100 units (10 Havok) vertically (FUN_1406cd7f0 collects them as (x, y, z, r)). Square, not round.
  - **Seeds** (`regionSeedPoints`, Havok space). Exterior jobs:
    - doors (exterior jobs of both types): for each building in the zone with a door whose outer marker point (door
      building +0x3bc) is inside the box and not below the water level, that point. `door navmesh axis` (FUN_14029cc90) selects a local axis used to
      place two markers on either side of the door (exact direction mapping **Unknown**), dropped to the ground with a ray: the
      outer one is this exterior seed, the inner one (+0x3c8) the interior seed (10 BUILDING records use axis 1, the
      rest 0, **Verified** count).
    - job type 0 (whole zone): the zone's `seeds.def` points (bucketed per zone when loaded, FUN_1403c5050; 4,585 points
      in 563 zones, 31 with Y = −99, the rest at Y ≥ 100, **Verified**); each is first tested with a ray down from
      Y 9000 against floor-0/1 building parts (FUN_1403d4dd0, groups 9 and 10): a point with no height that hits a
      building is moved onto it; a point with a height that hits a building is dropped; others are kept as stored, so a Y = −99 point
      that hits nothing goes in at Y −9.9 Havok (what Havok makes of it is **Unknown**). A
      zone with no `seeds.def` points gets a 3 × 3 grid of ground seeds over its box instead. Then the midpoints of the
      open border edges of each already built neighbour zone (FUN_1403c9820, the four neighbours) so regions connect
      across zone borders; points of a global list (DAT_14212f470, not identified) inside the box; and linked wall
      sections (class 9 WALL with `link length` > 0): three seeds on top of WALKABLE walls (ray hits) and, for walls that
      leave the zone, three ground seeds clamped into the zone box.
    - job type 1: two ground seeds on each edge of the job's box (corner table DAT_1416ccd30).
    - ground seeds (FUN_1403d8ac0): terrain height at (x, z), raised to the water level, then a ray down against
      groups 2, 6, 9, 14, 19 (mask 0x84244); the seed is added only when the ray finds nothing (or a hit of one
      particular kind, **Unknown**).
    - interior jobs: the door's inner marker point, else the first node of the building with type 0 or 1; otherwise the
      log says "Warning: building interior has no seed point".
  - **Interiors** (FUN_1403c79b0, job type 3): shapes in the building's AABB with mask 0x87fbe00 (groups 9..13, 15..22,
    27) or 0x82600 (groups 9, 10, 13, 19) when the building is destroyed. The building's own shapes: the points of
    its group-13 shapes (the `interior mask` hull) become one convex volume with `isInverted` set (hkaiConvexVolume
    +0x50, **Verified** against the reflection table), added as a carver, so everything **outside** the hull is
    removed; its other shapes give material 1, or 0 for group 27. Shapes of other buildings that belong to it (furniture: its children or buildings inside it) give material
    1 for WALKABLE and 0 otherwise; IGNORE ones are skipped. Vertices closer than 0.05 (squared, Havok) are merged. A
    destroyed building adds the triangle meshes of its `destroyed boundary` XML to the geometry (fcs.def calls it the
    "Navmesh cutter for destroyed interior"; the material it gets was not traced). 23 of the 68 buildings with `interior` parts have collision on them; `interior terrain` is set on 3
    (**Verified** counts); what `interior terrain` ("building interior includes terrain for generating navmesh") changes
    in the interior pass was not traced (**Unknown**).
  - BUILDING `path mode` counts 92/84/304/123 for 0/1/2/3 (**Verified**, 603 records). Enum names (FCS `PathMode`, the
    editor's enum): 0 NAVMESH_IGNORE, 1 NAVMESH_PROJECTED, 2 NAVMESH_OBSTACLE (default), 3 NAVMESH_WALKABLE (**Verified**).
    Other fields: `destroyed navmesh` (6 buildings, walls; the "Wall termites" accessibility check FUN_1402ef1f0 reads it),
    `build threshold` (19 buildings: below it a part is in a +14 group and drops out of the shape mask), `is gateway`
    (13), `max slope` (placement only, [buildings-production.md](buildings-production.md)).
- **Generation settings** (FUN_1403c4a10 on an `hkaiNavMeshGenerationSettings`, 0x220 bytes, built by Havok's constructor
  FUN_140dda8a0). Offsets **Verified** against the Havok reflection tables in the executable (hkClassMember arrays: name,
  type and offset per member, read with a probe; class `hkaiNavMeshGenerationSettings` and its `EdgeMatchingParameters`,
  `RegionPruningSettings`, `hkaiNavMeshSimplificationUtils::Settings` and `OverrideSettings`); values re-read from the
  constants (**Verified**). Havok units (Kenshi units × 0.1):

| Offset | Field | Kenshi | Havok default | Meaning / Recast counterpart |
  |---|---|---|---|---|
  | +0x10 | characterHeight | **1.8** (18 units) | 1.75 | clearance above a walkable face; `walkableHeight` |
  | +0x20 | up | (0, 1, 0) | (0, 0, 1) | Y up |
  | +0x34 | maxWalkableSlope | **40°** (0.6981 rad) | 60° | `walkableSlopeAngle` (per-material overrides below) |
  | +0x4c | edgeMatchingParams.maxStepHeight | **0.5** (5 units) | | step between faces that still connects; ~`walkableClimb` |
  | +0x50 | edgeMatchingParams.maxSeparation | **0.2** | | max gap between matched edges |
  | +0x68 | edgeMatchingParams.edgeTraversibilityHorizontalEpsilon | **0.02** | | |
  | +0x90 | regionPruningSettings.minRegionArea | **1e8** | | every region is "too small", so only seeded regions survive |
  | +0x94 | regionPruningSettings.minDistanceToSeedPoints | **0.4** (4 units) | | a region is kept if a seed is within 0.4 of it |
  | +0x98 | regionPruningSettings.borderPreservationTolerance | 0 | | |
  | +0xa0 | regionPruningSettings.regionSeedPoints | cleared, then filled per job | | the seeds above |
  | +0xd0 | boundsAabb | the job's box | | |
  | +0x118 | defaultConstructionProperties | 3 (default kept) | 3 | MATERIAL_WALKABLE_AND_CUTTING for unmapped materials (−1, 3, 4) |
  | +0x120 | materialMap | 0 → CUTTING (2); 1 → WALKABLE_AND_CUTTING (3); 2 → WALKABLE_AND_CUTTING (3) | empty | material 0 shapes cut holes and are never walkable; material 1 shapes are walkable |
  | +0x140 | weldInputVertices | true | true | (weldThreshold default 0.01) |
  | +0x148 | minCharacterWidth | **0.9** (9 units) | 0 | narrowest passage kept |
  | +0x14c | characterWidthUsage | 1 BLOCK_EDGES | 1 | narrow passages are marked blocked; the mesh is not shrunk (2 = SHRINK_NAV_MESH not used) |
  | +0x14d | enableSimplification | true | true | |
  | +0x150 | simplification.maxBorderSimplifyArea | 1.0 | | |
  | +0x16c | simplification.maxBorderHeightError | (0.1 in job type 1) | | |
  | +0x170 | simplification.maxBorderDistanceError | 1.0 | | ~`maxSimplificationError` |
  | +0x178 | simplification.useHeightPartitioning | false | | |
  | +0x17c | simplification.maxPartitionHeightError | 4.0 | | |
  | +0x190 | simplification.boundaryEdgeFilterThreshold | 0.1 | | |
  | +0x208 | overrideSettings | 4 entries, below | | per-material or per-volume overrides |

### Face data (per-face material, **Observed** except where marked)

| value | meaning |
|---|---|
| low nibble 1 | gate region marker used by `Gates` and `GateFilter` |
| 3 | water surface |
| 4 | open door (cost +5 in WaterCostModifier) |
| 5 | closed door (blocked by every filter) |

### Meitou debug view

Shift+F11 in the game (`GameHost.NavOverlay.cs`, `NavmeshOverlayRenderer`) steps off, Meitou's mesh, the original's cached exterior tiles
(`OriginalNavTiles`, [tagfile](../formats/havok-tagfile.md#navmesh-tiles)), both; for the zones the walkers use now (`NavmeshWalkability.World`), 3 (original 4) units
above the mesh, over the finished picture and fainter where the scene hides it. Meitou: polygons cyan (door polygons blue, seed-pruned red), mesh ends
yellow. Original: polygons magenta, ends orange, edges joined to the next tile violet. Rebuilt on a worker when the world or view changes.
**Observed** (Hub, zone 20.32): the original's polygons are large and simplified, building footprints are holes (interiors separate), and rock and cliff
cuts follow the blocky 36-unit terrain grid (generator inputs, terrain). The original has no such view (`rebuild_navmesh` Ctrl+Shift+F11 only rebuilds).

## Unknowns

- How the game uses `InteriorFilter` (its admit test is "uid < 0x10000", the same side the `ExteriorFilter` requires) and the
  composition of interior instance uids.
- Which numeric job types the five job adders create (the settings each type uses are now known: 0 whole zone, 1 box
  patch, 3 interior).
- The terrain grid resolution of the generator (FUN_140a08a20); the material of `destroyed boundary` triangles; the
  building flag at +0x1f0 → +0x2c that gates carvers and part of the WALKABLE rule; the global seed list DAT_14212f470;
  the ray-hit kind that still allows a ground seed; whether OverrideSettings material −1 means "any material".
- Whether the path request converts the footprint radius to Havok units.
- Steering rules for formation slots (Meitou's engine choice: [simulation](../simulation.md#movement-and-paths)) and the full avoidance parameters
  (the repulsion's absolute magnitude).
- Whether face data values other than 3, 4, 5 and the gate nibble exist; how building construction and destruction
  update faces (path mode switches at runtime).
- Havok tagfile field layouts for the face/edge arrays beyond sizes (16 / 20 bytes).
- Whether player-faction characters default to speed mode 2 and how the faction field +0x250 is defined; the +0x2f8 / +0x3d4
  fields of the water state function ([Water states](#water-states)).

### Builder constants (engine choices)

Current Recast parameters are authoritative in `NavBuildSettings`, `ZoneGeometryGatherer`,
`SeedPruner`, `NavInteriors`, `NavQuery`, `NavmeshWalkability` and `CollisionCache`; see
[simulation.md](../simulation.md). They are Meitou choices, not original-game constants.
**Unknown** original linked-wall seed fractions (Meitou uses 0.15/0.5/0.85).
**Observed** original start snap500 Havok units and goal snap about30 Kenshi units;
Meitou uses60/30 Kenshi units. Original height/climb1.8/0.5 Havok correspond to18/5 Kenshi.
## Expression reference

- Tile files: `h ^= v + 0x9e3779b9 + (h<<6) + (h>>2)`

## Evidence index

Evidence scopes below apply only to their named sources/checks.

- Face data (per-face material, **Observed** except where marked)
