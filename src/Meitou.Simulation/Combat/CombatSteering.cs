using System.Numerics;

namespace Meitou.Simulation.Combat;

/// <summary>
/// A fighter's fight steering (docs/game/combat.md "Fight steering"): the band its fight state asks for (set by the <see cref="PursuitSystem"/> every tick, as the original's
/// states call <c>combatMovementOffensive</c>), the last check that it may steer at its target, and the velocity smoothing. Derived state: not saved; hashed while in use.
/// </summary>
public sealed class SteerState
{
    /// <summary>Steering this tick (else the fighter walks paths or stands), at the target in <see cref="Target"/>.</summary>
    public bool Active;
    public int Target = -1;
    /// <summary>The band of the centre distance beyond the two radii it keeps (min and max of <see cref="CombatSteering.Band"/>), the speed limit, and <c>powerThrough</c>.</summary>
    public float Min = 10, Max = 20, Limit = 200;
    public bool PowerThrough;
    /// <summary>The tick of the next same-place and clear-line check, and its answer.</summary>
    public long LineTick;
    public bool LineClear;
    /// <summary>The smoothing's last samples, newest first (<see cref="CombatSteering.FilterLength"/>).</summary>
    internal readonly float[] HistX = new float[CombatSteering.FilterLength], HistZ = new float[CombatSteering.FilterLength];

    internal void Hash(ref StateHasher h)
    {
        h.Add(0x53746572);
        h.Add(Active); h.Add(Target); h.Add(Min); h.Add(Max); h.Add(Limit); h.Add(PowerThrough); h.Add(LineTick); h.Add(LineClear);
        for (int k = 0; k < CombatSteering.FilterLength; k++) { h.Add(HistX[k]); h.Add(HistZ[k]); }
    }

    /// <summary>Back to a standing start: the smoothing forgets (engine choice: the original's filters keep what they had from its last fight).</summary>
    internal void Reset()
    {
        Array.Clear(HistX);
        Array.Clear(HistZ);
    }
}

/// <summary>
/// The original's fight steering (<c>CombatMovementController::combatMovementUpdate</c>, <b>Observed</b>, docs/game/combat.md "Fight steering"): a velocity built from
/// forces each tick, a seek along the facing to keep the band, repulsion from the bodies round it (enemies and, twice as strong, the rest), a dead zone, the side-step
/// snap, the footwork speed caps and a smoothing filter. Running fighters (gap over 90) and those with no clear line to the target walk paths instead (<see cref="PursuitSystem"/>).
/// </summary>
public static class CombatSteering
{
    /// <summary>The share of the repulsion kept (TOTALREPULSION_DAMPING) and of the off-axis part of the velocity (STRAFE_STATE_DAMPING).</summary>
    public const float RepulsionDamping = 0.105f, StrafeDamping = 0.105f;
    /// <summary>The weight of the non-enemies' repulsion against the enemies'.</summary>
    public const float AllyWeight = 2;
    /// <summary>The most bodies one repulsion sum takes.</summary>
    public const int MaxRepulsions = 26;
    /// <summary>The flocking list's reach beyond the two radii and its height limit (<c>FUN_14085a070</c>).</summary>
    public const float FlockReach = 40, FlockHeight = 20;
    /// <summary>Speed caps in the footwork: sideways, backing off, and in all (not running).</summary>
    public const float SideSpeed = 18, BackSpeed = 16, FootworkCap = 18;
    /// <summary>The smoothing: samples and the weight ratio per 60 fps frame (<c>MotionFilter</c>, 10 and 0.8).</summary>
    public const int FilterLength = 10;
    public const float FilterDecay = 0.8f;

    /// <summary>
    /// The band from a state's near and far (<c>combatMovementOffensive</c>): with m = 1.4 x the raw footprint, min = clamp(near - m, 1, far'), max = max(far', min + 5),
    /// far' = clamp(far - m, 0, 9999).
    /// </summary>
    public static (float Min, float Max) Band(float near, float far, float footprint)
    {
        float m = 1.4f * footprint;
        float f = Math.Clamp(far - m, 0, 9999);
        float min = MathF.Max(MathF.Min(near - m, f), 1);
        return (min, MathF.Max(f, min + 5));
    }

    /// <summary>
    /// Left out of the flocking list (<c>FUN_14085a070</c>, docs/game/combat.md "Attack slots and circling"): down, in a cage or a bed, or sitting on a chair or throne (<c>isSitting</c>;
    /// Meitou: resting on a seat). Engine reading: a carried body too.
    /// </summary>
    internal static bool OutOfFlock(in CharacterHot h, CharacterCold c) => c.Medical is { Incapacitated: true } || c.IsImprisoned || c.InSomething == Locks.HeldState.Bed
        || !c.CarriedBy.IsNone || h.Task == (byte)CharacterTask.Rest && c.Ai?.SitClip is not null;

    /// <summary>A character's steering radius (<c>getRadius</c>): its footprint x its size.</summary>
    public static float Radius(CharacterCold? c) => CombatTuning.Footprint(c) * (c?.Medical?.ScaleK ?? 1);

    /// <summary>The fighter's own draws (<c>setupCombatMovementVariables</c>): ENEMY_DIST 15 + 30u, ALLY_DIST 1.4r + 30u, and the dead zone's STOP and START.</summary>
    public static (float Enemy, float Ally, float Stop, float Start) Draws(ulong seed, ulong key, CharacterCold cold)
    {
        float r = CombatTuning.Footprint(cold);
        float u(ulong k) => CombatRolls.Float(seed, key, CombatRoll.Steering, k);
        float stop = 3 + 3 * u(2), start = 30 + 3 * stop + 12 * u(3);
        if (cold.Fighter is { IsAnimal: false } f)
        {
            float e = 1 - f.EncumbranceFactor;
            stop = e * 2 * stop + 1;
            start = e * 3 * start + 1;
        }
        stop = Math.Clamp(stop, 0, 20);
        start = MathF.Max(MathF.Min(start, 90), stop + 10);
        return (15 + 30 * u(0), 1.4f * r + 30 * u(1), stop, start);
    }

    struct Push
    {
        public World World;
        public CombatSystem Combat;
        public int Me, Target, Faction;
        public Vector3 At;
        public float Radius, Own, EnemyReach, AllyReach;
        public Vector2 Face;
        public bool Turn;
        public Vector2 Enemies, Allies;
        public int EnemyCount, AllyCount;
    }

    /// <summary>
    /// The steering velocity of fighter <paramref name="i"/> this tick (units per second, X and Z), with <paramref name="face"/> the flat unit vector to its target; zero when
    /// the target is gone. <paramref name="strafing"/> is its side-stepping state (the animation's, last tick); <paramref name="moving"/> whether it moved last tick (the dead
    /// zone's STOP or START). Reads the last tick's places only; partition-safe (writes the fighter's own smoothing).
    /// </summary>
    public static Vector2 Velocity(World world, CombatSystem combat, int i, CharacterCold cold, SteerState s, float maxSpeed, bool strafing, bool moving, out Vector2 face)
    {
        face = Vector2.Zero;
        var prev = world.Characters.Previous;
        int t = s.Target;
        if ((uint)t >= (uint)prev.Length || !prev[t].Alive) return Vector2.Zero;
        var me = prev[i].Position;
        var to = prev[t].Position;
        var flat = new Vector2(to.X - me.X, to.Z - me.Z);
        if (flat.LengthSquared() < 1e-6f) return Vector2.Zero;
        face = Vector2.Normalize(flat);
        float mult = cold.Race?.CombatMoveSpeedMult ?? 1;
        float radius = Radius(cold);
        float rs = radius + Radius(world.Characters.Cold(t));
        float d = Vector3.Distance(me, to);

        // The seek: walk in beyond max, back off inside min, else stand.
        float seek = d > rs + s.Max ? (d / (rs + s.Max) - 1) * 100 * 2 * mult : MathF.Min((d / (rs + s.Min) - 1) * 100, 0);

        var (enemyDist, allyDist, stop, start) = Draws(world.Seed, Rng.Key(world.Characters.IdOf(i)), cold);
        var q = new Push
        {
            World = world, Combat = combat, Me = i, Target = t, Faction = cold.Faction, At = me, Radius = rs, Own = radius, Face = face, Turn = s.PowerThrough,
            EnemyReach = rs + enemyDist, AllyReach = rs + allyDist,
        };
        float reach = MathF.Max(q.EnemyReach, q.AllyReach) + CombatTuning.DefaultFootprint * 4;
        world.Grid.Query(me.X, me.Z, reach, ref q, static (int c, ref Push p) =>
        {
            if (c == p.Me || c == p.Target) return;
            var table = p.World.Characters;
            ref readonly var o = ref table.Previous[c];
            if (!o.Alive || MathF.Abs(o.Position.Y - p.At.Y) >= FlockHeight) return;
            if (table.Cold(c) is not { } oc || OutOfFlock(o, oc)) return;
            float rc = Radius(oc);
            var away = new Vector2(p.At.X - o.Position.X, p.At.Z - o.Position.Z);
            float flatDistance = away.Length();
            if (flatDistance >= p.Own + rc + FlockReach) return;   // not in the flocking list
            bool enemy = p.Combat.IsEnemyOf(p.World, c, oc, p.Me, p.Faction);
            if (enemy ? p.EnemyCount >= MaxRepulsions : p.AllyCount >= MaxRepulsions) return;
            float d = Vector3.Distance(o.Position, p.At);
            float magnitude = (1 - d / (rc + (enemy ? p.EnemyReach : p.AllyReach))) * 100;
            if (!(magnitude > 0) || flatDistance < 1e-4f) return;
            var v = away / flatDistance * magnitude;
            // powerThrough: the push is turned to the side of the facing it is on, its length kept (FUN_1402e9530).
            if (p.Turn)
            {
                var side = new Vector2(p.Face.Y, -p.Face.X);
                v = (Vector2.Dot(side, v) > 0 ? side : -side) * magnitude;
            }
            if (enemy) { p.Enemies += v; p.EnemyCount++; }
            else { p.Allies += v; p.AllyCount++; }
        });
        var vel = (q.Enemies + q.Allies * AllyWeight) * RepulsionDamping + face * seek;

        // The dead zone: under STOP while moving, under START while standing, it stands.
        if (!s.PowerThrough && vel.Length() < (moving ? stop : start)) vel = Vector2.Zero;

        // The side-step snap: forward/back, or sideways while strafing; the other part is damped.
        var sideAxis = new Vector2(face.Y, -face.X);
        float z = Vector2.Dot(vel, face), x = Vector2.Dot(vel, sideAxis);
        vel = strafing ? face * (z * StrafeDamping) + sideAxis * x : face * z + sideAxis * (x * StrafeDamping);

        // The caps: facing forward the limit (never over the footwork's 18), backing off 16, sideways 18.
        float speed = vel.Length();
        if (speed > 1e-4f)
        {
            var dir = vel / speed;
            float dot = Vector2.Dot(dir, face);
            float cap = dot > 0.5f ? MathF.Min(s.Limit, maxSpeed) : dot < -0.5f ? BackSpeed : SideSpeed;
            cap = MathF.Min(cap, FootworkCap);
            if (speed > cap) vel = dir * cap;
        }

        vel = new Vector2(Filter(s.HistX, vel.X, world.TickSeconds), Filter(s.HistZ, vel.Y, world.TickSeconds));
        return vel * mult;
    }

    /// <summary>
    /// The smoothing of one axis (<c>MotionFilter::Apply</c>): the newest sample first, the weighted mean with weights decaying 0.8 per 60 fps frame, over the samples of
    /// <see cref="FilterLength"/> frames (engine reading for ticks longer than a frame: fewer samples, each weight ratio 0.8^(60 dt)).
    /// </summary>
    internal static float Filter(float[] history, float sample, float dt)
    {
        int n = Math.Clamp((int)MathF.Round(FilterLength / (60 * dt)), 1, FilterLength);
        Array.Copy(history, 0, history, 1, n - 1);
        history[0] = sample;
        float ratio = MathF.Pow(FilterDecay, 60 * dt), w = 1, sum = 0, weights = 0;
        for (int k = 0; k < n; k++)
        {
            sum += history[k] * w;
            weights += w;
            w *= ratio;
        }
        return sum / weights;
    }
}
