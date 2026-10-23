using System.Collections.Concurrent;
using System.Numerics;
using Meitou.Data;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation.Bodies;

namespace Meitou.Simulation;

/// <summary>
/// The bodies of the characters (stage 7, docs/game/character-stats.md): every tick it runs each living character's <see cref="MedicalState.Tick"/> (blood,
/// bleeding, part healing, hunger, the knock-out timer), sets the top speed from the speed chain (<see cref="Speed.Run"/>: athletics, legs, hunger,
/// encumbrance) and stops a character that has just been knocked out or has died. It runs in the Act phase, before the animation system, and writes only its own
/// slots. A corpse is removed <see cref="CorpseHours"/> game hours after it died (an engine choice; the original's rule is <b>Unknown</b>), one luckyren Mode burst at the end of its tick (<see cref="Burst"/>).
/// </summary>
/// <remarks>
/// The medical tick takes the tick's seconds of character time (<see cref="World.TickSeconds"/>, character-stats.md "Time"); hunger converts them to game hours.
/// <see cref="BodyTimeScale"/> multiplies the time of the part and blood rates (not hunger or the knock-out timer), see <see cref="MedicalContext.BodyTimeScale"/>.
/// The bed a character lies in comes from <paramref name="db"/> (none without it).
/// </remarks>
public sealed class BodySystem(GameConstants constants, BodyOptions? options = null, float bodyTimeScale = 1, GameDatabase? db = null) : ITickSystem
{
    /// <summary>Game hours in one tick: 11/36000 h is 1/30 game s at the Verified 1200/11 s per game hour (<c>GameClock</c>).</summary>
    public const float HoursPerTick = 11f / 36000;
    /// <summary>How long a corpse lies before it is removed, in game hours.</summary>
    public const float CorpseHours = 12;
    public const long CorpseTicks = (long)(CorpseHours / HoursPerTick);

    public GameConstants Constants { get; } = constants;
    public BodyOptions Options { get; } = options ?? BodyOptions.Default;
    /// <summary>The <see cref="MedicalContext.BodyTimeScale"/> every character gets (default 1: the documented rates).</summary>
    public float BodyTimeScale { get; } = bodyTimeScale;
    readonly XpService xp = new(constants, options ?? BodyOptions.Default);
    /// <summary>A bed record's kind and healing value without research (<see cref="BedOf"/>); filled from the parallel phase.</summary>
    readonly ConcurrentDictionary<string, (BedKind Kind, float Factor)> beds = new();

    /// <summary>How far from a bed's spot an AI sleeper counts as lying in it (it sleeps at the spot without being held in the bed).</summary>
    const float BedReach = 3;

    /// <summary>
    /// The bed a character lies in (character state 1; <c>FUN_140652000</c>, character-stats.md "Beds"): the one it is held in, else for an AI asleep at its home's bed spot
    /// (or the "Sleep when injured" bed task) the bed fixture there. Its kind is the functionality's <c>function</c> (6 BF_BED, 25 BF_SKELETON_BED) and its factor the
    /// Building's <c>+0x3c0</c>: <c>output rate</c> x <c>production mult</c> x the research production multiplier. None outside a bed.
    /// </summary>
    (BedKind Kind, float Factor) BedOf(World world, CharacterCold cold, in CharacterHot n)
    {
        if (db is null) return (BedKind.None, 1);
        var fixture = cold.InSomething == Locks.HeldState.Bed && cold.InWhat is { } id ? world.Fixtures.Find(id)
            : n.Task == (byte)CharacterTask.Rest && cold.Ai?.Behaviour == Ai.AiBehaviour.Sleep
              || n.Task == (byte)CharacterTask.Operate && cold.Implicit == Meitou.Data.Gameplay.Ai.TaskType.GO_HOME_AND_GO_TO_BED && cold.Operating
                ? world.Fixtures.BedAt(n.Position, BedReach) : null;
        if (fixture is null) return cold.InSomething == Locks.HeldState.Bed ? (BedKind.Bed, 1) : (BedKind.None, 1);
        var (kind, factor) = beds.GetOrAdd(fixture.RecordId, rid =>
        {
            if (db.Find(rid) is not { } bed) return (BedKind.Bed, 1);
            foreach (var f in bed.GetReferences("functionality").Select(r => db.Find(r.TargetStringId)))
                if (f?.GetInt("function") is (int)BedKind.Bed or (int)BedKind.RepairBed)
                    return ((BedKind)f.GetInt("function"), bed.GetFloat("output rate", 1) * f.GetFloat("production mult", 1));
            return (BedKind.Bed, bed.GetFloat("output rate", 1));
        });
        return (kind, factor * world.Research.Upgrade(fixture.RecordId).ProductionMult);
    }

    public void Act(World world, Partition part, EffectBuffer effects)
    {
        var table = world.Characters;
        var prev = table.Previous;
        var next = table.Next;
        List<MedicalEvent>? events = null;
        for (int i = part.Start; i < part.End; i++)
        {
            if (!prev[i].Alive) continue;
            var cold = table.Cold(i)!;
            if (cold.Medical is not { } medical || cold.Race is not { } race || cold.Stats is not { } stats) continue;
            ref var n = ref next[i];
            if (medical.Dead && cold.DiedTick < 0) cold.DiedTick = world.Tick;   // also when something outside the tick killed it (a hit)
            if (medical.Incapacitated && (n.Task != (byte)CharacterTask.Idle || (n.Flags & (ushort)MoveFlags.AnyPath) != 0))
            {
                MovementSystem.Stop(table, i);
                n.Velocity = Vector3.Zero;
            }
            if (medical.Dead) continue;
            // The load: the inventory weight, plus carry person weight while carrying someone, against the strength the injuries leave (character-stats.md "Encumbrance and carrying").
            float load = cold.Inventory?.ContentWeight() ?? 0;
            bool carrying = !cold.Carrying.IsNone;
            float encumbrance = load <= 0 && !carrying ? 1 : Encumbrance.Factor(Constants, load, carrying, stats.Strength, medical.StatMultiplier(Meitou.Data.Gameplay.Bodies.StatsEnumerated.Strength));
            var (bed, bedFactor) = BedOf(world, cold, in n);
            var ctx = new MedicalContext(Constants, Options, race)
            {
                EncumbranceFactor = encumbrance,
                Toughness = stats.Toughness,
                Strength = stats.Strength,
                Seed = world.Seed,
                CharacterKey = Rng.Key(table.IdOf(i)),
                BodyTimeScale = BodyTimeScale,
                // In a bed (state 1) a character rests: degeneration stops, the bed scales healing, blood and hunger take the resting rates; held (state 2) the organic rate doubles.
                Bed = bed,
                BedFactor = bedFactor,
                RepairRate = bed == BedKind.RepairBed ? bedFactor : 0,
                Resting = bed != BedKind.None,
                HealMult = cold.InSomething == Locks.HeldState.Imprisoned ? 2 : 1,
            };
            // The combat system's own context (hits) reads the encumbrance from the fighter, so it matches the one used here.
            if (cold.Fighter is { } fighter) fighter.EncumbranceFactor = encumbrance;
            if (world.BloodMarks is { } marks && medical.Bleeding > 0 && cold.WaterState == 0) BloodTrail(world, marks, i, cold, medical.Bleeding, race, in n);
            events ??= [];
            events.Clear();
            medical.Tick(world.TickSeconds, ctx, events);
            n.MaxSpeed = Speed.Run(race, stats, medical, encumbrance);
            cold.SwimSpeed = Speed.Swim(race, stats, medical, encumbrance);
            TrainOnTheMove(stats, race, medical, in n, cold.WaterState == 3, carrying, encumbrance, world.TickSeconds);
            if ((cold.Orders & CharacterOrders.Sneak) != 0 && !medical.Incapacitated)
            {
                // An exposure older than a second (a looker's scan interval with room to spare) has lapsed.
                var exposure = cold.StealthExposureTick >= world.Tick - (long)MathF.Ceiling(1 / world.TickSeconds) ? cold.StealthExposure : StealthExposure.Unobserved;
                xp.Sneaking(stats, race, exposure, world.TickSeconds);
            }
            if (cold.IsPlayer || table.IdOf(i) == world.Player.Inspected) cold.Extended = ExtendedOf(stats, race, medical, n.MaxSpeed, encumbrance, ctx.Resting);
            n.Health = Math.Clamp(medical.Blood / MathF.Max(MedicalState.BloodCapacity(race, stats.Strength), 1) * 100, 0, 100);
            foreach (var e in events)
            {
                if (e.Kind is MedicalEventKind.Died or MedicalEventKind.KnockedOut)
                {
                    MovementSystem.Stop(table, i);
                    n.Velocity = Vector3.Zero;
                }
            }
        }
    }

    /// <summary>
    /// The blood a bleeding character out of the water leaves (<c>FUN_140652000</c>, docs/game/blood-decals.md): standing still (by its carrier's speed when carried) its pool
    /// grows; walking, a smear every 4 units, as opaque as the bleeding (at most 1); carried, drops of min(0.5, bleeding / 10) once a build-up of random × bleeding × dt × 10
    /// passes 1, where it has moved more than a unit. Display only (<see cref="World.BloodMarks"/>): the trail's state on the cold row is not hashed.
    /// </summary>
    static void BloodTrail(World world, System.Collections.Concurrent.ConcurrentQueue<BloodMark> marks, int slot, CharacterCold cold, float bleeding, RaceData race, in CharacterHot n)
    {
        var table = world.Characters;
        var id = table.IdOf(slot);
        var velocity = table.IsAlive(cold.CarriedBy) ? table.Previous[cold.CarriedBy.Slot].Velocity : n.Velocity;
        var at = n.Position;
        float moved = Vector2.DistanceSquared(new Vector2(cold.BloodMark.X, cold.BloodMark.Z), new Vector2(at.X, at.Z));
        if (velocity.X == 0 && velocity.Z == 0)
        {
            marks.Enqueue(new BloodMark(BloodMarkKind.Pool, id, at, default, bleeding, race.BloodColour));
            cold.BloodMark = at;
        }
        else if (cold.CarriedBy.IsNone)
        {
            if (moved <= 16) return;
            cold.BloodMark = at;
            marks.Enqueue(new BloodMark(BloodMarkKind.Smear, id, at, new Vector2(velocity.X, velocity.Z), Math.Clamp(bleeding, 0, 1), race.BloodColour));
        }
        else if (bleeding > 0.01f)
        {
            float roll = Rng.Float(Rng.Hash(world.Seed, Rng.Key(id), RngPurpose.BloodTrail, (ulong)world.Tick * 3));
            cold.BloodDrip += roll * bleeding * world.TickSeconds * 10;
            if (cold.BloodDrip <= 1) return;
            cold.BloodDrip = 0;
            if (moved <= 1) return;
            float x = Rng.Float(Rng.Hash(world.Seed, Rng.Key(id), RngPurpose.BloodTrail, (ulong)world.Tick * 3 + 1)), z = Rng.Float(Rng.Hash(world.Seed, Rng.Key(id), RngPurpose.BloodTrail, (ulong)world.Tick * 3 + 2));
            marks.Enqueue(new BloodMark(BloodMarkKind.Drip, id, at, new Vector2(x - 0.5f, z - 0.5f), MathF.Min(bleeding * 0.1f, 0.5f), race.BloodColour));
            cold.BloodMark = at;
        }
    }

    /// <summary>
    /// The numbers behind the information panel's extended lines (<c>FUN_140895350</c>, docs/game/ui-screens.md section 4). Toughness XP: 1, or 2 with the left arm crippled, plus 2
    /// for the right arm and 2 for blood-loss KO (the original adds a fourth term for a counter at <c>+0xa4</c> under 15, <b>Unknown</b>, left out). Strength XP is the carrying rate;
    /// what the original shows instead with a weapon in hand ("Weapon weight") is not modelled.
    /// </summary>
    ExtendedStats ExtendedOf(CharacterStats stats, RaceData race, MedicalState medical, float speed, float encumbrance, bool resting)
    {
        float runValue = Speed.RunValue(race, stats.Athletics);
        var arms = medical.Aggregates();
        float toughness = (arms.LeftArmDisabled ? 2 : 1) + (arms.RightArmDisabled ? 2 : 0) + (medical.BloodKo ? 2 : 0);
        float hunger = race.HungerRate * MedicalState.HungerSituationFactor(Constants, resting || medical.Unconscious || medical.Dead, false, encumbrance);
        return new ExtendedStats(XpService.AthleticsRate(encumbrance, speed, runValue), xp.StrengthCarryRate(encumbrance), toughness,
            MedicalState.KoPoint(Constants, stats.Toughness), speed, runValue, hunger);
    }

    /// <summary>
    /// XP outside combat (character-stats.md "Levelling"; the movement tick <c>FUN_1408c6660</c>): athletics while running, strength while carrying a load. A character counts as
    /// running when it moves faster than its race walk speed (+1); whether walking trains too is <b>Unknown</b> (the doc names running). In deep water (<paramref name="deepWater"/>)
    /// swimming trains instead of athletics (<b>Verified</b>, <c>FUN_1408c6660</c>). Strength rises only when the load
    /// encumbers it, and only while it moves. The speed the stat sees is the velocity the movement system left this tick.
    /// </summary>
    void TrainOnTheMove(CharacterStats stats, RaceData race, MedicalState medical, in CharacterHot n, bool deepWater, bool carrying, float encumbrance, float dt)
    {
        if (medical.Incapacitated) return;
        float speed = MathF.Sqrt(n.Velocity.X * n.Velocity.X + n.Velocity.Z * n.Velocity.Z);
        if (speed < 1) return;
        if (deepWater) xp.SwimmingInWater(stats, race, carrying, dt);
        else if (speed > n.WalkSpeed + 1) xp.AthleticsFromRunning(stats, race, encumbrance, speed / MathF.Max(n.MaxSpeed, 1), dt);
        if (encumbrance < 1) xp.StrengthFromCarrying(stats, race, encumbrance, dt);
    }

    public void SlowWorld(World world)
    {
        var table = world.Characters;
        for (int i = 0; i < table.HighWater; i++)
        {
            if (table.Next[i].Alive && table.Cold(i) is { Burst: { } blow } burst) Burst(world, i, burst, blow);
            else if (table.Next[i].Alive && table.Cold(i) is { DiedTick: >= 0 } cold && world.Tick - cold.DiedTick >= CorpseTicks)
            {
                var corpse = new CharacterId(i, table.Next[i].Generation);
                world.Uniques.Left(corpse);
                table.Remove(corpse);
            }
        }
    }

    /// <summary>How far a burst character's gear falls (an engine choice).</summary>
    public const float BurstSpread = 30;

    /// <summary>
    /// luckyren Mode (<see cref="World.Luckyren"/>): a character a fatal hit burst. Everything it had (worn, carried, fitted) falls within <see cref="BurstSpread"/> units
    /// around it, an <see cref="BloodMarkKind.Explosion"/> goes to the host and the body is gone at once.
    /// </summary>
    static void Burst(World world, int slot, CharacterCold cold, Vector2 blow)
    {
        var table = world.Characters;
        var id = new CharacterId(slot, table.Next[slot].Generation);
        var at = table.Next[slot].Position;
        if (cold.Inventory is { } inventory)
        {
            ulong n = 0;
            foreach (var item in inventory.Items)
            {
                float angle = Rng.Float(Rng.Hash(world.Seed, Rng.Key(id), RngPurpose.Luckyren, n++)) * MathF.Tau;
                float far = MathF.Sqrt(Rng.Float(Rng.Hash(world.Seed, Rng.Key(id), RngPurpose.Luckyren, n++))) * BurstSpread;
                float yaw = Rng.Float(Rng.Hash(world.Seed, Rng.Key(id), RngPurpose.Luckyren, n++)) * MathF.Tau;
                item.Section = "main";
                var spot = at + new Vector3(MathF.Sin(angle), 0, MathF.Cos(angle)) * far;
                spot.Y = world.Walkability.GroundHeight(spot.X, spot.Z, at.Y);
                world.GroundItems.Drop(item, spot, yaw);
            }
            inventory.Items.Clear();
        }
        if (world.BloodMarks is { } marks && cold.Race is { } race)
            marks.Enqueue(new BloodMark(BloodMarkKind.Explosion, id, at, blow, race.BleedRate > 0 ? 1 : 0, race.BloodColour, race.StringId));
        world.Uniques.Left(id);
        table.Remove(id);
    }
}
