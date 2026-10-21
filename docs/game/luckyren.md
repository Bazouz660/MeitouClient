# luckyren Mode

A Meitou-only switch (settings menu, off by default, `UserConfig.LuckyrenMode`): a character killed by a
hit bursts. No original behaviour; every rule here is an engine choice. Implementation:
`World.Luckyren`, `CombatSystem` (melee `Apply`, ranged `BoltHit`), `BodySystem.Burst`,
`GameHost.Debris`, `DebrisRenderer`.

## Simulation

- A hit whose medical events include a death sets the victim's `CharacterCold.Burst` to the blow's
  direction (melee: attacker → victim; bolt: its flight). Bleeding out or other deaths do not burst.
- At the end of that tick (`BodySystem.SlowWorld`): every top-level inventory item (worn, carried,
  fitted limbs; a backpack with its contents) drops as a ground item within 30 units (section reset
  to `main`, place and yaw from `RngPurpose.Luckyren`, height the walkable ground under it), the character leaves the uniques registry and
  is removed. No corpse remains to loot or carry.
- An `Explosion` blood mark carries the position, blow direction, race id and blood colour
  (amount 0 for a bloodless race, `bleed rate` 0: no blood, but bones still fly).
- Play-dropped ground items keep the `GroundItems.Limit` (default 1024, "Dropped items kept" up to 4096): many bursts evict the oldest drops.
- The host sets `World.Luckyren` from the option every frame, so it changes a run's outcome.

## Display (host)

- Blood (with the `Blood` setting on, terrain only, not above 20 units over it): a pool of size 1.5
  at the spot and 24 splashes (size 0.3–0.5) up to 60 units away, biased along the blow.
- Pieces: the race's `severed limbs` items' meshes (`ground mesh`, else `mesh`) at their own size and
  12 nest bones (`bone1..14.mesh`, beast-sized up to 3.4 m, scaled to 25–75 cm). Thrown from 10 units
  up at 30–90 units/s outwards (biased along the blow) and 30–80 up, spinning; gravity 98 units/s²
  (taking 1 unit = 1 dm, [terrain.md](../formats/terrain.md)). On the ground they bounce (vertical 0.35, horizontal and spin 0.6), leave a small
  splash on the first contact, and settle on their thinnest side once slow. Above the terrain (an
  upper floor) they land on the burst's level.
- They move in game time, last as long as a corpse (`BodySystem.CorpseHours`), at most 400, and are
  not saved; a new world clears them.
- Drawn in the near slice's scene pass before the characters, colour only (the alpha is the
  characters' SSAO mask), lit with `kenshiLight` under the game sky; no shadows or motion vectors.
  Textures come from the object material resolver; a mesh without one is bone-coloured.
