using System.Numerics;
using System.Runtime.CompilerServices;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Combat;

namespace Meitou.Simulation.Combat;

public sealed partial class CombatSystem
{
    /// <summary>The most characters a blow's zone holds (FUN_1407862e0's limit of 20).</summary>
    const int ZoneCapacity = 20;

    [InlineArray(ZoneCapacity)]
    struct ZoneSlots
    {
        int first;
    }

    [InlineArray(ZoneCapacity)]
    struct ZoneKeys
    {
        float first;
    }

    struct ZoneScan
    {
        public CharacterTable Table;
        public CombatSlot[] Read;
        public FactionRelations? Relations;
        public int Self, Target, Faction, Squad, Count;
        public Vector2 From, Centre, Forward;
        public float Y, Half, High, Deep;
        public ZoneSlots Slots;
        public ZoneKeys Keys;
    }

    /// <summary>
    /// Emits blow <paramref name="blow"/> of fighter <paramref name="i"/>'s attack to everyone in its attack zone (<b>Verified</b>, <c>calculateTargetsInAttackZone</c> FUN_1406087a0
    /// and <c>attackImpactCheck</c> FUN_14060a2f0, docs/game/combat.md "Attack zone"), at most the technique's <c>max simultaneous hits</c>; nobody in it is a whiff and the swing
    /// keeps its 0.5 s pause. The zone is a box ahead along the heading, k the fighter's size, s = lerp(k, 0.45, 1), r its footprint, L its weapon's length: its centre
    /// (L / 4 + r + max(0, attack distance − the distance moved since the swing started)) s ahead, half-extents (r + 0.5, 25, L / 2) s, or (13, 8, L / 2) s for a left or
    /// right cut; then h = max(0, (offset − k r) / 2) moves the centre back by h and deepens it by h. In it: a body not knocked out that is the fighter's target or an enemy
    /// that is not an ally. Engine readings: a body counts when its footprint circle (size-scaled) meets the box, the height test is the box's half-height plus 9 from the
    /// fighter's feet, and the order is the target first, then by distance (the original's is the physics query's).
    /// </summary>
    void Strike(World world, int i, in CombatSlot s, CharacterCold cold, CombatTechnique tech, int blow, EffectBuffer effects)
    {
        var table = world.Characters;
        var prev = table.Previous;
        var p = prev[i].Position;
        var from = new Vector2(p.X, p.Z);
        float k = cold.Medical!.ScaleK, scale = WeaponStats.Lerp(k, 0.45f, 1), r = CombatTuning.Footprint(cold), length = cold.Fighter!.WeaponLength;
        var forward = new Vector2(MathF.Sin(prev[i].Yaw), MathF.Cos(prev[i].Yaw));
        float moved = Vector2.Distance(from, new Vector2(s.AttackFrom.X, s.AttackFrom.Z));
        float ahead = (length * 0.25f + r + MathF.Max(0, tech.AttackDistance - moved)) * scale;
        bool sweep = tech.DirectionOf(blow) is 2 or 3;   // CUT_LEFT, CUT_RIGHT
        float deep = length * 0.5f * scale;
        float back = MathF.Max(0, (ahead - k * r) * 0.5f);
        ahead -= back;
        deep += back;
        var scan = new ZoneScan
        {
            Table = table, Read = read, Relations = Relations, Self = i, Target = s.TargetSlot, Faction = cold.Faction, Squad = cold.SquadId,
            From = from, Centre = from + forward * ahead, Forward = forward, Y = p.Y,
            Half = (sweep ? 13 : r + 0.5f) * scale, High = (sweep ? 8 : 25) * scale + 9, Deep = deep,
        };
        world.Grid.Query(scan.Centre.X, scan.Centre.Y, deep + scan.Half + RangedTuning.MaxBodyRadius, ref scan, static (int c, ref ZoneScan z) =>
        {
            if (c == z.Self || z.Count == ZoneCapacity) return;
            var state = z.Table.Previous;
            if (!state[c].Alive || z.Read[c].Down || z.Table.Cold(c) is not { Medical.Incapacitated: false } tc) return;
            if (c != z.Target)
            {
                if (tc.Faction == z.Faction || z.Squad >= 0 && tc.SquadId == z.Squad) return;
                if (z.Relations is { } relations && !AggroSystem.IsEnemy(relations, z.Faction, tc.Faction)) return;
            }
            var q = state[c].Position;
            if (MathF.Abs(q.Y - z.Y) > z.High) return;
            var d = new Vector2(q.X, q.Z) - z.Centre;
            float along = MathF.Abs(Vector2.Dot(d, z.Forward)), across = MathF.Abs(d.X * z.Forward.Y - d.Y * z.Forward.X);
            float ex = MathF.Max(0, across - z.Half), ez = MathF.Max(0, along - z.Deep);
            float radius = CombatTuning.Footprint(tc) * tc.Medical.ScaleK;
            if (ex * ex + ez * ez > radius * radius) return;
            // Kept sorted: the target first, then the nearest, then the lower slot.
            float key = c == z.Target ? -1 : Vector2.DistanceSquared(new Vector2(q.X, q.Z), z.From);
            int at = z.Count++;
            while (at > 0 && (z.Keys[at - 1] > key || z.Keys[at - 1] == key && z.Slots[at - 1] > c))
            {
                z.Keys[at] = z.Keys[at - 1];
                z.Slots[at] = z.Slots[at - 1];
                at--;
            }
            z.Keys[at] = key;
            z.Slots[at] = c;
        });
        int hits = Math.Min(scan.Count, Math.Max(1, tech.MaxSimultaneousHits));
        for (int n = 0; n < hits; n++) effects.Emit(i, scan.Slots[n], BlowEffect, s.AttackSeq, default, blow);
    }
}
