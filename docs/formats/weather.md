# Weather

Research for Kenshi 1.0.68; record/schema values and binary functions are scoped in the evidence
index. Record tables below retain the surveyed fields and base-game data. Meitou scheduler and
particle implementation: `WeatherWorld`, `WeatherScheduler`, `EffectGroups`, `ParticleSimulation`.
Rendering: [clouds](clouds.md), [particles](particle-universe.md), [fog](fogfeatures.md).

## Scheduling rules

**Verified (decompiled):** season/weather/wind scheduling uses game minutes/days. Frame clocks
are real dt, game-speed dt (0 paused), and game-speed dt with a 0.01 floor at zero speed for wetness/dust/haze.
Seasons sort by val0; length is rounded share/sum * days-per-year, last receives remainder,
share<1 uses equal division. Start/end/current season persist in saves; equal-order ties unknown.

Choose positive-weight weather inside its inclusive whole-hour window; the original reversed
window test (`hour<=start or hour>=end`) effectively accepts all hours when start>end. Base
windows are 7-22 and 5-23. Weighted pick uses rand()/32768; no candidate uses last fallback.
Duration is inclusive integer minutes val1..val2, cut at today's end-time for restricted weather.
Strength is clamp(season random min/max * region random mult min/max,0,1). Effect strength reads
the minimum key twice, hence always minimum. Weather can follow itself.

**Verified** `WeatherTests.Areas_map_places_known_towns_in_their_regions`: decode areasmap TGA
into top-to-bottom rows; cell `(floor(x/4608)+32,floor(z/4608)+32)` reads `(4cx+2,4cz+2)` and
RGB packed `R<<16|G<<8|B`. All 71 colours match BIOME_GROUP indices; no offset/flip. Match by
StringId/colour, never name (two NONE records). **Observed:** whether original sampling chooses
exactly the centre on mixed border blocks. Seasonless regions use Default.

## Wind and camera transitions

**Verified (decompiled):** initial speed lerp(min,max,strength); normalized random ground direction.
Every wind-update-time minutes, target speed moves limit/360 toward weather speed; heading adds
random[-pi,pi]*limit/360. Interpolate over rounded 5% of update time. Unsigned angleBetween
folds headings (**Observed** consequence). Foliage sway uses object wind factors, not weather.
**Verified** (`FUN_1409dc0e0`, six saves' BIOMES): the game's wind angle `a` gives direction `(cos a, 0, −sin a)` (rotation of +X about Y)
and is lerped linearly, not the short way round. Meitou's heading `h` gives `(cos h, sin h)` in (x, z), so a save's angle is −h; but Meitou
reads the unsigned angle back as `h` at a retarget, so its retargeted wind is the game's mirrored in z (open). Saved state per region:
[save.md BIOMES](save.md#biomes-type-28-quicksave-weather-per-region).

Camera region changes 500 units beyond current cell; squared displacement>8000 is teleport.
Fog samples four ±1300 corners, weights `1-min(1,distanceToCell²/1300²)`, combines duplicates
and normalizes. **Observed Meitou interpretation:** distance/colour average only fog-enabled
regions; enable weight averages all. Fog distance lerps wind min/max; equal wind endpoints use
fog-distance min after loader clamps. Fade duration is 2.5% of new weather duration.
Sky/cloud colour-density transition is 30 game-speed seconds; teleports snap. Drift=wind*speed.
Clock rewinds snap current fog/sky in Meitou rather than retaining a previous-weather fade.

## Surface effects

**Verified** binary/shaders: rain=rain-intensity*strength, rainAmount=saturate(rain/50).
Ripple normal weights 2,2,0.8, grazing/distance fade `saturate(2*viewDir.y-dist*0.0001)`.
Wetness rises 0.01/s to target; dries 0.005/s only for target zero; lower nonzero target holds.
Wet shaders combine waterline and weather; waterline edges 0.5 objects/foliage, 2 terrain.
Absorbance derives from gloss/metalness or biome layers; foliage 0.9, interiors weather wetness 0.
**Verified** `common/wet.hlsl::makeWet`: albedo darkening
`min((1-1/(wet+0.7))*absorbance+0.2*underwater,0.4)/2`; gloss lerp toward0.5 by
`saturate(2*max(0,wet-0.15*absorbance)^(1+4*absorbance))` (excluding underwater).
Object absorbance=(1-gloss)*(1-metalness); terrain=1-albedo.a plus blended BIOMES
`absorbance 0..road`. **Verified** objects.hlsl/triplanar.hlsl dust blend:
`saturate((saturate((N.y-0.7+z)*4)-6*gloss+2.2-6*noise)*2+x-1)*min(1,8*x)`,
noise sampled at world.xz*0.002, x=current dust (inside: x*dust-inside), z=slope.
Blend toward biome ground colour; normal flattening is half that blend for objects only.

Dust approaches `dust*strength` at 0.017*target/s upward, 0.001/s downward; slope changes 0.1/s.
Interior factor uses dust-inside. DUST materials blend biome ground colour and flatten normals;
triplanar features omit normal flattening. Haze target=heat-haze*strength*saturate(6*sunY),
step<=dt/3; animation uses gameTime and freezes paused even while strength settles.
Weather audio events: Rain_Start/End, Wind_Start/Stop; Rain_Intensity and Intensity RTPCs.

## Effect lifecycle

**Verified (decompiled):** replace region effect groups on weather change. Place units in random
same-region cells' central 80%, ground height, up to ten altitude/slope tries (normal step12).
Life is min/max; max0 immortal. POINT/lightning/wandering respawn while below count (count0 unlimited).
GLOBAL count0 creates none; all units follow camera xz at ground+100, sphere wrap radius from
emitter size (restored units use1000). Out-of-radius particles move 1.5d toward camera travel,
then random-reset if still >=sqrt(1.5)d. CAMERA groups create one unit regardless of count.
GLOBAL_POINT R defaults1000: ground rings within R and R..2R, bounded at3*count, stop beyond4R.
Emitter wind factor=clamp((speed-min)/(max-min),0,1); invalid span leaves rate unchanged.
Particle fade comes from affectors, fog volumes from effect fade/lifecycle.

**Observed Meitou choices:** warm groups off-thread; region changes debounce2s, weather changes
within a region immediate; static map-feature units immortal. Missing particle images/materials
fall back to white. Fog-volume ground movement and effect lights are not fully implemented.
**Verified** plugin constructor default quota500; ash techniques total1550. Denser original ash
remains unexplained. **Observed** gameplay: dust accuracy, acid/ground/water, gas/burning,
weather-protection, collector rain and generator wind; full effects untraced.

### WEATHER (type 80; Verified: fcs.def and the loader FUN_1409de7b0)

| Field | Use |
|---|---|
| `clouds density` | the cloud layer's density `c` ([clouds.md](clouds.md)) |
| `sky color mult` (RRGGBB) | the sky controller's colour multiplier: multiplies the two SkyX colours that light the clouds and `horizonClouds` (`zenithLight`, `nadirLight`; [sky.md](sky.md)) |
| `fog enabled`, `fog color`, `fog distance min/max`, `fog wind min/max` | the weather fog ([sky.md](sky.md#haze-and-weather)); see "Fog" below for the distance rule |
| `wind speed min/max` (units/s), `wind update time` (game minutes; 0 means 1,000,000), `wind update limit` (degrees, "0-360") | the wind, below |
| `rain intensity` (0..100) | rain sound level and the water's rain ripples |
| `wetness` (0..1) | target wetness of surfaces |
| `dust`, `dust inside`, `dust slope` | dust on objects outside, inside buildings, and the slope it sticks to |
| `heat haze` (0..1) | the heat-haze post effect's strength |
| `affect type` (`WeatherAffecting`: WA_NONE, WA_DUSTSTORM, WA_ACID, WA_BURNING, WA_GAS, WA_RAIN), `affect strength` | direct effect on characters (gameplay) |
| `effect strength min/max` | "Maximum effect strength"; the game reads `min` twice (below) |
| `start time`, `end time` (hours) | time-of-day window; equal values = any time |
| `effects` → EFFECT (val0 max count active at once, 0 = no limit; val1/val2 min/max respawn time in real seconds; 0/0 = all spawned at once) | the weather's particle effects |
| `wind intensity` (0..100) | wind sound level |

### Spawning (Verified (decompiled), FUN_1409dcaf0, FUN_140103210; behaviour from fcs.def where marked)

| `type` | Group | Behaviour (fcs.def, **Observed**) |
|---|---|---|
| CAMERA (1), CAMERA_RAIN (5), CAMERA_ACID_RAIN (6) | camera group | in front of the camera, moving with it; affects the whole region. The system sits at the camera node and the particles are kept in a cube centred `d` ahead of the camera along its view direction and wrapped modulo it (**Verified (decompiled)**, FUN_140101800 / FUN_140100dc0 / FUN_140101a20: edge `2d / 1.5`; `d` is the float at offset 0x7c of the effect data, the **size** the EFFECT loader computes: 0.75 × the largest Box extent (× scale), so the cube is the emitter box; details in [particle-universe.md](particle-universe.md#simulation-and-rendering)) |
| POINT (2), POINT_LIGHTING (7) | point group | spawned at random places in the area; POINT_LIGHTING "based on the amount of metal", hits once |
| WANDERING (3), WANDERING_STORM (8), WANDERING_GAS (9) | wandering group | random place, then moves at `wandering speed` (particles that get too far from their anchor are put back near it: **Observed**, FUN_140101be0, its group not confirmed) |
| GLOBAL (4) | global group | on the ground at the camera centre; whole region |
| GLOBAL_POINT (10) | global point group | points around the camera area; needs a max time to live |
| NONE (0) | none | not a weather effect (torches, blood, fire on objects) |

### SEASON (type 81; Verified: fcs.def, FUN_1409df240)

`weathers` → WEATHER with val0 = **weight** ("probability"), val1/val2 = **min/max duration in game minutes**; `weather strength
limit min/max` (0..1); `sunlight color` (loaded, its use was not traced: **Unknown**). A season without weathers gets the
"Default" weather (`5460-weather.mod`, clear) with limits 0..1 and logs "Warning: Season ... has no weather". If any of its
weathers has a time window (`start time` ≠ `end time`), the "Default" weather is appended with weight 0 as the fallback.
**Verified** against the merged records (`WeatherTests.Base_game_records_follow_the_loader_rules`): every
season of the base game without weathers becomes Default with limits 0..1; the only windowed weathers are "Desert Calm hot 0.6" and
"venge beams", and every season that lists one ends with Default at weight 0. The duration given to the Default of an empty season (and
to a region without seasons) is **Observed**: not read from the game, 120..720 minutes (the wind update time is 360).

### BIOME_GROUP (type 95; Verified: fcs.def, FUN_1409df8d0, FUN_1408fd6e0)

`index` (the colour in `areasmap.tga`), `seasons` → SEASON with val0 = **order**, val1 = **share of the year** (relative to the
other seasons), `weather strength multiplier min/max`, `acidic ground`, `acidic water` (gameplay). The world's BIOMES records
(terrain texturing, [terrain.md](terrain.md)) take no part in the weather.

### EFFECT (type 82; Verified: fcs.def, FUN_1400fa0c0) and EFFECT_FOG_VOLUME (type 96)

`type` (`EffectType`: NONE, CAMERA, POINT, WANDERING, GLOBAL, CAMERA_RAIN, CAMERA_ACID_RAIN, POINT_LIGHTING, WANDERING_STORM,
WANDERING_GAS, GLOBAL_POINT), `particle system` (a ParticleUniverse system name from `data/particles/scripts/*.pu`), `affect
type` / `effect radius` / `effect strength min/max` (damage area), `min/max altitude`, `min/max slope`, `min/max time to live`
(s), `wandering speed`, `wind affected`, `wind direction emission`, `wind speed mult`, `min/max wind span rate` (wind speeds for
none / all particles), `colour multiplier`, `sky colour multiplier`, `ground colour` ("multiply particle base colour by biome
ground colour"), `maximum view distance`, fade times for particles, lights and fog volumes, and the lists `fog volumes` →
EFFECT_FOG_VOLUME, `lights` → LIGHT, `sound` → AMBIENT_SOUND. The loader also measures the particle system's Box and Circle
emitters (largest extent × the system's scale × 0.5 × 1.5) as the effect's size, and remembers its `LinearForce` affectors and
its emitters' emission rates so the game can drive them by the wind (**Verified (decompiled)**; how they are driven was not
traced). EFFECT_FOG_VOLUME: `type` SPHERE / CYLINDER, `radius`, `distance` (density distance), `colour`, `alpha`, `additive
colour`, offsets, `ground movement`.

## Unknowns

- The areas map's own origin and size fields in the terrain object (the placement itself is **Verified**, see "Where"); whether a
  cell reads its centre pixel or another of its 16 (they differ on 1045 border blocks).
- The season order for equal order values; the new-game start of the first season; the special case for a season with one
  weather in the SEASON loader (a ceiling of a scaled value, not decoded).
- `sunlight color` of SEASON and `sky colour multiplier` / `colour multiplier` of EFFECT: loaded, their use not traced.
- How effect groups use `maximum view distance`, `sky colour multiplier` and the strength (the wind fields and the emission-rate scaling are used as
  [particle-universe.md](particle-universe.md#simulation-and-rendering) says, **Observed**).
- `ground movement` of EFFECT_FOG_VOLUME (read by the game into the volume's holder; its use was not traced).
- Whether the game's ash is denser than the data says (see "Density"); the lights of effects (the plugin quota is Verified above).
- The wandering units' exact walk and turn rules, and where the game places lightning (it snaps to metal objects).
- The full gameplay effect of each `WeatherAffecting` value.

## Expression reference

- Where: regions from the areas map, FUN_1408fd6e0 and FUN_1408f4eb0): `(cx, cz) = (floor(x / 4608) + 32, floor(z / 4608) + 32)`
- Choosing a weather, FUN_1409dd980, FUN_1409dcf60, FUN_1409dc3e0): `s = clamp(random[limit min, limit max] × random[mult min, mult max], 0, 1)`
- Rain and wetness, FUN_1409e8f70, FUN_1409ea410, FUN_1409d6ae0, FUN_1409d6c40; Verified: shaders): `= rain intensity × s`; `wet = min(wetness + underwater, 1)`; `underwater = saturate((waterHeight − y + edge) / edge)`
- Heat haze, FUN_1409e8f70, FUN_1409ea410): `s = 1`; `HeatHaze=0`

## Evidence index

Evidence scopes below apply only to their named sources/checks.

- Time bases (Verified (decompiled), FUN_140788a00)
- WEATHER (type 80; Verified: fcs.def and the loader FUN_1409de7b0)
- SEASON (type 81; Verified: fcs.def, FUN_1409df240)
- BIOME_GROUP (type 95; Verified: fcs.def, FUN_1409df8d0, FUN_1408fd6e0)
- EFFECT (type 82; Verified: fcs.def, FUN_1400fa0c0) and EFFECT_FOG_VOLUME (type 96)
- Where: regions from the areas map (Verified (decompiled), FUN_1408fd6e0 and FUN_1408f4eb0)
- Seasons (Verified (decompiled), FUN_1409df8d0, FUN_1409dde50)
- Choosing a weather (Verified (decompiled), FUN_1409dd980, FUN_1409dcf60, FUN_1409dc3e0)
- Wind (Verified (decompiled), FUN_1409dcf60, FUN_1409dc0e0)
- Region at the camera (Verified (decompiled), FUN_1409e8f70)
- Fog: up to four regions (Verified (decompiled), FUN_1409e8f70, FUN_1409e8cc0, FUN_1409dc350)
- Sky and clouds (Verified (decompiled), FUN_1409dc4e0, FUN_14066cc80, FUN_14066f190)
- Rain and wetness (Verified (decompiled), FUN_1409e8f70, FUN_1409ea410, FUN_1409d6ae0, FUN_1409d6c40; Verified: shaders)
- Dust (Verified (decompiled), FUN_1409e8f70, FUN_1409ea2d0; Verified: `objects.hlsl`, `triplanar.hlsl`)
- Heat haze (Verified (decompiled), FUN_1409e8f70, FUN_1409ea410)
- Sounds (Verified (decompiled), FUN_1409e8f70; names only)
- Spawning (Verified (decompiled), FUN_1409dcaf0, FUN_140103210; behaviour from fcs.def where marked)
- The groups in detail (Verified (decompiled) where marked; the rest Observed)
- The particle scripts (Observed: `data/particles/scripts/*.pu`; the format, the survey and the implementation: [particle-universe.md](particle-universe.md))
- Effect placers on the map (Verified: records and `features.dat`)
- Base-game data (Verified: merged base records, 2026-10-05)
