using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.World;
using Meitou.Simulation.Bodies;

namespace Meitou.Simulation;

/// <summary>
/// What the weather does to characters (docs/formats/weather.md "Effects on characters"; the character's weather update <c>FUN_1405cf790</c>, <b>Verified</b> by
/// decompilation). Every tick, for each living character with a body, in the slow-world phase (serial) with the tick's seconds of character time:
/// <list type="number">
/// <item>Acid, unless the race is immune to it: swimming (water state 3) in a region with <c>acidic water</c> burns every part; otherwise, outdoors, standing (not down or
/// carried), not in a bed or cage and with an empty boots slot, <c>acidic ground</c> burns the legs.</item>
/// <item>Local effects: an outdoor character within an affecting unit's radius takes its burning or gas (dust units do nothing here).</item>
/// <item>The region's weather, while exposed (outdoors or on top of a building) and not immune: its kind and strength go on the character for sight and hearing
/// (<see cref="CharacterCold.Weather"/>); a dust storm sets the medical dust value (accuracy), acid rain burns every part. Rain, burning and gas weathers only do the first.</item>
/// </list>
/// Protection is the sum of the worn pieces' <c>weather protection amount</c> for the kind. Not modelled: the status-bar notices and the dialogue events (61 acid feet, 62 acid rain,
/// 63 acid water, 65 poison gas, 69 burning), the acid-shelter test (a building with <c>is shelter</c> over the character stops acid rain), the eye-shielding overlay
/// animation, the smoke effect on a burnt character and the global switch at <c>DAT_142134690+0x2f0</c> that skips it all (<b>Unknown</b>).
/// </summary>
public sealed class WeatherSystem(GameConstants constants) : ITickSystem
{
    public GameConstants Constants { get; } = constants;

    public void SlowWorld(World world)
    {
        if (world.Weather is not { } source) return;
        var table = world.Characters;
        var next = table.Next;
        float dt = world.TickSeconds;
        var zones = source.Zones;
        for (int i = 0; i < table.HighWater; i++)
        {
            if (!next[i].Alive || table.Cold(i) is not { } cold) continue;
            cold.Weather = WeatherAffecting.None;
            cold.WeatherStrength = cold.WeatherProtection = 0;
            if (cold.Medical is not { Dead: false } medical || cold.Race is not { } race) continue;
            var at = next[i].Position;
            if (source.At(at.X, at.Z) is not { } here) continue;
            bool outdoors = world.Sight.Outdoors(i);

            // Acid water and acid ground (immunity to WA_ACID covers both).
            if (!race.ImmuneTo(WeatherAffecting.Acid))
            {
                var carrier = cold.CarriedBy;
                int water = cold.InSomething != Locks.HeldState.None ? 0
                    : table.IsAlive(carrier) && table.Cold(carrier.Slot) is { } c ? c.WaterState : cold.WaterState;
                if (water == 3) medical.AcidWater(dt * here.AcidicWater, Constants);
                else if (outdoors && !medical.Incapacitated && carrier.IsNone && cold.InSomething == Locks.HeldState.None && Barefoot(cold, race))
                    medical.AcidGround(dt * here.AcidicGround, race, Constants);
            }

            // Local effects: the last unit of each kind that reaches the character (a later one replaces an earlier one's strength).
            if (outdoors && zones.Count > 0)
            {
                float burning = 0, gas = 0;
                foreach (var z in zones)
                {
                    if (z.Affect is not (WeatherAffecting.Burning or WeatherAffecting.Gas) || Vector3.DistanceSquared(z.Position, at) > z.Radius * z.Radius) continue;
                    if (z.Affect == WeatherAffecting.Burning) burning = z.Strength; else gas = z.Strength;
                }
                if (burning > 0 && !race.ImmuneTo(WeatherAffecting.Burning)) medical.Burning(dt * burning, Protection(cold, WeatherAffecting.Burning));
                if (gas > 0 && !race.ImmuneTo(WeatherAffecting.Gas)) medical.Gas(dt * gas, Protection(cold, WeatherAffecting.Gas), Constants);
            }

            // The region's weather.
            var kind = here.Affect;
            if (kind == WeatherAffecting.None || world.Sight.Inside(i) || race.ImmuneTo(kind)) continue;
            float protection = Protection(cold, kind);
            cold.Weather = kind;
            cold.WeatherStrength = here.Strength;
            cold.WeatherProtection = protection;
            if (kind == WeatherAffecting.DustStorm) medical.DustStorm(here.Strength, protection);
            else if (kind == WeatherAffecting.Acid) medical.AcidRain(dt, protection, Constants);
        }
    }

    /// <summary>
    /// The worn protection from <paramref name="kind"/> (<c>FUN_140887030</c> builds it per kind into CharStats <c>+0x58</c>; the medical functions sum the worn list the same way):
    /// the sum of <c>weather protection amount</c> over the worn pieces that list the kind, unclamped.
    /// </summary>
    public static float Protection(CharacterCold cold, WeatherAffecting kind)
    {
        float sum = 0;
        if (cold.Fighter is { } f)
            foreach (var piece in f.Armour)
                if (piece.Data.Protects(kind)) sum += piece.Data.WeatherProtectionAmount;
        return sum;
    }

    /// <summary>
    /// The boots slot exists and is empty (the original: inventory section 9 found and empty). Engine choice: a race with <c>no shoes</c> has no boots slot, as the character
    /// generator gives it none.
    /// </summary>
    static bool Barefoot(CharacterCold cold, Meitou.Data.Gameplay.Bodies.RaceData race) =>
        !race.NoShoes && (cold.Inventory is not { } inv || !inv.Items.Exists(item => item.Section == "boots"));

    /// <summary>
    /// The sight range factor of the weather on a looker (<c>FUN_140851bc0</c>, <b>Verified</b>): lerp(s x (1 - protection), 1, 0.4) in a dust storm, lerp(.., 1, 0.75) in any other
    /// affecting weather (unclamped: protection above 1 lengthens the sight); 1 without weather.
    /// </summary>
    public static float SightFactor(CharacterCold cold) => cold.Weather == WeatherAffecting.None ? 1
        : 1 + ((cold.Weather == WeatherAffecting.DustStorm ? 0.4f : 0.75f) - 1) * cold.WeatherStrength * (1 - cold.WeatherProtection);

    /// <summary>
    /// The noise range factor of the weather on a mover (<c>FUN_140851d10</c>, <b>Verified</b>): lerp(s, 1, 0.25) in a dust storm, lerp(s, 1, 0.75) in rain or acid rain, 1 otherwise;
    /// no protection term. It scales both ends of the noise range except a sneaker's 80.
    /// </summary>
    public static float NoiseFactor(CharacterCold cold) => cold.Weather switch
    {
        WeatherAffecting.DustStorm => 1 - 0.75f * cold.WeatherStrength,
        WeatherAffecting.Rain or WeatherAffecting.Acid => 1 - 0.25f * cold.WeatherStrength,
        _ => 1,
    };
}
