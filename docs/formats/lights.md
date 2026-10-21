# Placed lights

Original LIGHT schema, building assembly, deferred light shader and on-state binary research.
Record table/formulas below. Engine WorldLights/WorldLamps: [render-lights](../render-lights.md).

## Instances and lighting

LIGHT records instantiate through building/part/layout transforms with colour/radius/power/spot/
effect parameters and rolled power. Per-instance node/state/power ownership matters; mesh emissive
material is not the same as a lamp. Visible positive-power sphere/cone volumes add diffuse/GGX
in sun-light units; radius clips, NdotL<0.01 skips. Attenuation/spot equation block retained.
PULSE=0.5+0.5sin4t, SHIMMER=0.95+0.05sin30t*sin17t; clock t units unknown. No flicker case found
in the decoded pass, factor1 there; another writer may affect it.

Original pass has no discovered hard count cap; engine spatial selection/budgets are separate. When lights are on: below.

### The LIGHT record (Verified: fcs.def, the merged base game, the exe's light builder FUN_140815420)

| Field | List | Meaning |
|---|---|---|
| `type` | int | `LightType`: 0 POINT, 1 SPOT. The game makes an Ogre point light (0) or spotlight (2) of it |
| `diffuse` | int 0xRRGGBB | Light colour. Unpacked as bytes / 255 (Ogre `ColourValue::setAsARGB`), used as is in the linear HDR light pass (no sRGB decode) |
| `specular` | int 0xRRGGBB | Not in fcs.def, read by the exe as the specular colour; set only on `Streetlight-Droidtype-OmniLIGHT` (FFFFFF). The light shader declares `specularColour` but does not use it (it uses `diffuseColour` for both terms) |
| `brightness` | float | Base power (Ogre `setPowerScale`); 0.2 to 1 in the base game, 8 and 15 on two effect lights |
| `variance` | float | Each instance's power is `brightness + U(−variance/2, +variance/2)` (below) |
| `radius` | float | The light's reach, Ogre `setAttenuation(radius, 1, 0, 0)`; the shader uses only the range. 25 to 750 (3000 on lightning) |
| `inner`, `outer` | float, degrees | Spot cone full angles (`setSpotlightRange` after degree→radian; the light volume's cone uses tan(outer/2)). Set on point lights too, unused there |
| `falloff` | float | Spot falloff exponent between the cones ("1 linear, >1 soft, <1 hard", fcs.def) |
| `effect` | int | `LightEffect`: 0 NONE, 1 PULSE, 2 FLICKER, 3 SHIMMER (below) |
| `landscape`, `buildings`, `characters` | bool | "Cast shadows" switches. **Dead data**: the light builder always calls `setCastShadows(false)` and no code references the strings (FCS sets all three from the scene's `castShadows`) |

| LIGHT | Type | diffuse | brightness ± variance/2 | radius | inner/outer | effect | Used by |
|---|---|---|---|---|---|---|---|
| `torch_light` | point | FFCE5E | 0.4 | 70 | — | FLICKER | Torch Post, Large Torch Post |
| `HolyStreetTorch` | point | FFCE5E | 0.7 ± 0.05 | 150 | — | FLICKER | Holy Street / Sinner Torch |
| `campfire light` | point | FFCE5E | 0.6 | 140 | — | SHIMMER | Campfire, Cooking Pit |
| `strip_light_blue` | point | 9DEBF9 | 0.8 ± 0.2 | 60 | — | — | Electrical Torch Post |
| `street_light_for lampposts` | spot | A8FFE2 | 0.6 ± 0.125 | 170 | 75/120 | — | City Lamp Post, Light Post II |
| `LIGHT` (14380-Newwworld) | spot | CEFDF0 | 0.35 | 100 | 74.7/120 | — | Quad Streetlamp (4 per lamp) |
| `indoor_lamp_light` | spot | E9FF9B | 0.7 ± 0.1 | 120 | 90/100 | — | Ceiling Lamp, Ceiling Fan Lamp |
| `street_light_yellow` | spot | F1E8B4 | 0.6 ± 0.15 | 300 | 40/90 | — | Spotlight, Wall Spotlight |
| `spotlight powerful` | spot | F1FFC4 | 1 ± 0.1 | 750 | 60/80 | — | Searchlight |

### Shading (Verified: `deferred.hlsl` `light_fs`, `CalcPunctualLight`, the light pass FUN_1402d8540) — layout/example

```
x      = saturate(d / radius)
atten  = x < 0.649 ? 0.2 + 0.8 (1 − x)³
       : x < 0.8   ? 0.242 − 3 (x − 0.6)²
       :             3 (x − 1)²                       1 at the light, 0.30 at R/2, 0.235 at 0.649 R, 0.12 at 0.8 R, 0 at R
spot   = saturate(((L·dir − spot.y) / (spot.x − spot.y))^spot.z)    spot lights only
colour = (π · N·L · diffuse · Fresnel-diffuse · albedo + GGX specular(diffuse)) · atten · spot · power
power  = powerScale · global (1.0) · effect
```

### When a light is on

**Verified** (decompiled) unless marked:

- **Function.** `function` is on the BUILDING_FUNCTIONALITY a BUILDING references (`functionality`), not on the BUILDING.
  Probe of the merged data (Observed, 2026-10-20): every lamp building with a LIGHT is BF_LIGHT 15 (Light Post I-III,
  City Lamp Post, Torch Posts, wall lights, Spotlight, Searchlight, Ceiling Lamp) except Campfire (4), Cooking Pit (11),
  turrets (12) and the hydroponic farms (1). Draws: Light Post I/II/III 4/3/2, Spotlight 4, Searchlight 8, Ceiling Lamp 2.
- **Night flag, per light** (parts recursion `140553dc0`, builder `FUN_140815420`): `custom.x` = 1 when Building slot 59
  returns the null handle (type 0xb) or byte +0x240 is set. The light pass (`FUN_1402d8540`) draws a flagged light only
  while the daylight factor (`FUN_14066cb50`) is at most 0.1. What slot 59 returns (the own handle while +0xf8 is set and
  it is not a door) and what +0x240 is are **Unknown**; Meitou flags BF_LIGHT buildings' lights (**Observed**).
- **Daylight factor** (`FUN_14066cb50`): 0 outside sunrise..sunset, ramps over 1/12 of a day (2 hours) after sunrise and
  before sunset, 1 between. `GameClock_IsDaytime` (`FUN_14066cb20`): strictly between them.
- **Lights switch** (Building slot 140, offset 0x460): stores +0x26a, shows each light while on and +0x19d (the building's
  visibility, set by slot 32 from the floor shown). Called on with completion (slot 72), off by dismantling (slot 73),
  with the power switch (slot 99).
- **Each update** (slot 28, `UseableStuff::vfunc_28`) of a complete building: every light's power = its rolled power x
  slot 109; visibility flips (slot 140) when slot 109 crosses 0. Slot 109 = 0 when slot 164 is false, else
  granted / demand (1 with no demand) x health; plain Building and doors return 1.
- **Slot 164** (consumer active): 1 for every functional class; `LightBuilding`: 1 when slot 59 is a valid handle, else
  "not daytime". A null-handle light building thus draws power and lights only at night. Its `mounted` handle is +0x440.
- Town buildings' power and on state are **Unknown**; Meitou applies only the night flag to them.

### Carried lanterns

**Verified** (merged data, probe 2026-10-20; decompiled where an address is given) unless marked:

- **Records.** A worn record's `physics attachment` names a CHARACTER_PHYSICS_ATTACHMENT (`bone name`, `file male` /
  `file female` .phs, `light data` -> LIGHT). With a light: ITEM Lantern and Lantern of Radiance (`lantern hip`,
  `Bip01 Pelvis`), CONTAINER Garru Backpack (`Packbeast lantern`, `Bip01`; the Pack Beast's `backpack`), and two mod
  attachments on `Bip01 Spine2` that nothing references. All name `pack beast light`: point, F5F59C, 0.2 ± 0.05, r 300.
- **Belt slot.** The two lanterns are the only records with `slot` 14 (ATTACH_BELT), so the inventory's belt slot holds
  lanterns (**Observed**: Meitou lets an ITEM with slot 14 into `belt`). The item's `charges` 200 use is **Unknown**.
- **Lights** (`ScythePhysicsT`, ctor `1407e71f0` reads `light data`; `vfunc_15`): one light per entry of a map, made by the
  building light builder with the night flag set, so the light pass draws them only at daylight <= 0.1. Each frame
  (`vfunc_2`) a light's node takes a physics body's position and orientation. How the map is filled (which body) is
  **Unknown**.
- **Files.** The .phs bodies are in the character's binding-pose model space (a human pelvis is at y 10.4): the belt
  lantern hangs from a spherical joint at the hip, the pack beast's at the top of a pole (y 31).
- **Meitou** (engine choice): the light sits at the body hanging from the last spherical joint (not `root`), held rigidly
  relative to the bone; no swing. The lantern meshes (in the .phs points) and the animals' backpack meshes are not drawn.

## Expression reference

- Shading: ` x = saturate(d / radius) atten = x < 0.649 ? 0.2 + 0.8 (1 − x)³ : x < 0.8 ? 0.242 − 3 (x − 0.6)² : 3 (x − 1)² 1 at the light, 0.30 at R/2, 0.235 at 0.649 R, 0.12 at 0.8 R, 0 at R spot = saturate(((L·dir − spot.y) / (spot.x − spot.y))^spot.z) spot lights only colour = (π · N·L · diffuse · Fresnel-diffuse · albedo + GGX specular(diffuse)) · atten · spot · power power = powerScale · global (1.0) · effect `

## Evidence index

Evidence scopes below apply only to their named sources/checks.

- The LIGHT record (Verified: fcs.def, the merged base game, the exe's light builder FUN_140815420)
- Counts (Observed, `WorldLights.ForWorld`, 2026-10-09)
- Shading (Verified: `deferred.hlsl` `light_fs`, `CalcPunctualLight`, the light pass FUN_1402d8540)
- When a light is on (Verified (decompiled) unless marked)
