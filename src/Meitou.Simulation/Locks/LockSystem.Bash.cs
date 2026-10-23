using System.Numerics;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Gameplay.Combat;
using Meitou.Simulation.Combat;

namespace Meitou.Simulation.Locks;

/// <summary>
/// Bashing doors (Task_BashDoor, docs/game/locks.md "Bash door"; docs/simulation.md "Cages, beds and locks"). <b>Engine</b> stand-in for the original's planner, which bashes a door
/// when a locked one blocks a walk: an NPC attacking a target inside a building shut by another faction's locked doors (<see cref="FixtureRegistry.IsShut"/>), itself outside, walks
/// to the nearest door and swings at it. Each swing (<c>FUN_14029d690</c>, <b>Observed</b>) takes 0.015 x clamp(2 x blunt + 0.5 x cut, 20, 60) off the door lock's hardness, an
/// animal's damage at a quarter; at 0 the door breaks: broken, unlocked and opening, so the building opens for paths and the attacker's pursuit carries on.
/// </summary>
public sealed partial class LockSystem
{
    /// <summary>Ticks between looks for attackers whose target is shut in (engine choice).</summary>
    public const int BashCheckTicks = 30;
    /// <summary>Game seconds between swings (engine choice: the original swings at 0.6 of its <c>stonemine</c> clip, whose length Meitou does not read).</summary>
    public const float BashSwingSeconds = 1.5f;
    /// <summary>How far out from the leaf the basher stands, and how near that spot counts as there.</summary>
    public const float BashStandOff = 12, BashReach = 20;

    /// <summary>Whose attack targets the bashers follow (null: no bashing, as in worlds without combat).</summary>
    internal CombatSystem? Combat { get; set; }
    /// <summary>The blow formulas' constants (null: every swing does the minimum, 20).</summary>
    internal CombatConstants? CombatConstants { get; set; }

    void Bashers(World world)
    {
        if (Combat is null) return;
        var table = world.Characters;
        var next = table.Next;
        if (world.Tick % BashCheckTicks == 0) FindBashers(world);
        for (int s = 0; s < table.HighWater; s++)
        {
            if (!next[s].Alive || table.Cold(s) is not { Bashing: { } doorId } c) continue;
            var door = world.Fixtures.Find(doorId);
            if (door is null || door.DoorBroken || door.Lock is not { EffectivelyLocked: true } || c.Medical is { Incapacitated: true } || (!c.BashOrdered && next[s].Task != (byte)CharacterTask.Attack) || world.Fixtures.DoorSpotOf(door) is not { } spot)
            {
                StopBashing(table, s, c);
                continue;
            }
            var stand = StandSpot(door, spot);
            float d = Vector2.Distance(Flat(next[s].Position), stand);
            if (d > BashReach && c.BashOrdered)
            {
                // An ordered smash walks by its order's own goal; a new order (another task) ends it.
                if (next[s].Task != (byte)CharacterTask.GoTo) StopBashing(table, s, c);
                continue;
            }
            if (d > BashReach)
            {
                // Walk there (the pursuit leaves a basher alone); a failed path ends it.
                if ((next[s].Flags & (ushort)MoveFlags.AnyPath) == 0)
                {
                    if (c.BashTime < 0) { StopBashing(table, s, c); continue; }
                    next[s].Goal = stand;
                    next[s].Mode = SpeedMode.Free;
                    next[s].Flags |= (ushort)MoveFlags.NeedPath;
                    c.BashTime = -1;   // asked once; arriving resets it
                }
                continue;
            }
            if ((next[s].Flags & (ushort)MoveFlags.AnyPath) != 0) { next[s].Flags = (ushort)(next[s].Flags & ~(ushort)MoveFlags.AnyPath); next[s].PathCursor = 0; c.PathRequest = 0; }
            var face = new Vector2(spot.Centre.X, spot.Centre.Z) - Flat(next[s].Position);
            if (face.LengthSquared() > 1e-4f) next[s].Yaw = MathF.Atan2(face.X, face.Y);
            if (c.BashOrdered && next[s].Task == (byte)CharacterTask.GoTo) MovementSystem.Stop(table, s);
            if (c.BashTime < 0) c.BashTime = 0;
            c.BashTime += world.TickSeconds;
            if (c.BashTime < BashSwingSeconds) continue;
            c.BashTime = 0;
            // Each update of the task books Burglary against the door's owner (Task_BashDoor, locks.md); the pursuit's bashers book none yet.
            if (c.BashOrdered && Crimes is not null && door.Owner >= 0 && door.Owner != c.Faction) Crimes.SetPending(world, s, Crime.CrimeCode.Burglary, door.Owner, CharacterId.None, Crime.CrimeSystem.MinTimer);
            if (Hit(door, c))
            {
                world.Fixtures.Touch(door);
                world.EmitSound(SoundKind.Unlocked, s, next[s].Position);
                StopBashing(table, s, c);
            }
        }
    }

    /// <summary>The door lock hardness one point of <see cref="BlowDamage"/> takes off.</summary>
    public const float HardnessPerDamage = 0.015f;

    /// <summary>A blow's damage to a door (<c>FUN_14029d690</c>): clamp(2 x blunt + 0.5 x cut, 20, 60), an animal's packet at a quarter first (<c>FUN_140348c10</c>).</summary>
    public static float BlowDamage(float cut, float blunt, bool animal)
    {
        float scale = animal ? 0.25f : 1;
        return Math.Clamp(2 * blunt * scale + 0.5f * cut * scale, 20, 60);
    }

    /// <summary>One swing at a door (<c>FUN_14029d690</c>): true when it broke the door.</summary>
    bool Hit(Fixture door, CharacterCold c)
    {
        float damage = 20;
        if (CombatConstants is { } k && c.Fighter is { } f && c.Stats is { } stats && c.Medical is { } med)
        {
            float Eff(StatsEnumerated stat) => stats[stat] * stats.Scale * med.StatMultiplier(stat);
            float str = Eff(StatsEnumerated.Strength);
            var packet = f.Unarmed
                ? DamageFormulas.UnarmedPacket(Eff(f.WeaponSkill), Eff(StatsEnumerated.Toughness), str, f.Weapon, TargetKind.Human, null, f.DamageOutput, k)
                : DamageFormulas.Packet(Eff(f.WeaponSkill), Eff(StatsEnumerated.Dexterity) * f.DexterityMult, str, f.Weapon!, TargetKind.Human, null, f.DamageOutput, k);
            damage = BlowDamage(packet.Cut, packet.Blunt, f.IsAnimal);
        }
        var l = door.Lock!;
        l.Hardness -= damage * HardnessPerDamage;
        if (l.Hardness > 0) return false;
        l.Hardness = 0;
        l.Locked = false;
        door.DoorBroken = true;
        FixtureRegistry.OpenDoor(door);
        return true;
    }

    /// <summary>Gives the free attackers whose target is shut in another faction's building, while they are outside it, the nearest of its doors to bash.</summary>
    void FindBashers(World world)
    {
        var table = world.Characters;
        var next = table.Next;
        List<(string Building, Vector2 Origin, float Radius, List<(Fixture Door, DoorSpot Spot)> Doors)>? shut = null;
        for (int s = 0; s < table.HighWater; s++)
        {
            if (!next[s].Alive || next[s].Task != (byte)CharacterTask.Attack) continue;
            if (table.Cold(s) is not { IsPlayer: false, Bashing: null, IsImprisoned: false, Medical.Incapacitated: false, Carrying.IsNone: true, Fighter: { } fighter } c) continue;
            if (!fighter.IsAnimal && fighter.Unarmed) continue;   // the task wants a weapon (hasWeaponEquipped)
            var st = Combat!.StateOf(table.IdOf(s));
            int t = st.TargetSlot;
            if (t < 0 || t >= table.HighWater || !next[t].Alive || next[t].Generation != st.TargetGeneration) continue;
            shut ??= ShutBuildings(world);
            if (shut.Count == 0) return;
            var target = Flat(next[t].Position);
            var me = Flat(next[s].Position);
            foreach (var (building, origin, radius, doors) in shut)
            {
                if (Vector2.Distance(target, origin) > radius || Vector2.Distance(me, origin) <= radius) continue;
                if (world.Fixtures.DoorOwner(building) == c.Faction) continue;   // an owner unlocks instead
                var best = doors.MinBy(x => Vector2.DistanceSquared(StandSpot(x.Door, x.Spot), me));
                c.Bashing = best.Door.Id;
                c.BashTime = 0;
                break;
            }
        }
    }

    List<(string, Vector2, float, List<(Fixture, DoorSpot)>)> ShutBuildings(World world)
    {
        var list = new List<(string, Vector2, float, List<(Fixture, DoorSpot)>)>();
        foreach (var b in world.Fixtures.Buildings)
        {
            if (!world.Fixtures.IsShut(b)) continue;
            var doors = new List<(Fixture, DoorSpot)>();
            Vector2 origin = default;
            float radius = 0;
            foreach (var f in world.Fixtures.OfBuilding(b))
            {
                if (f.Kind != FixtureKind.Door || world.Fixtures.DoorSpotOf(f) is not { } spot) continue;
                origin = Flat(f.Position);
                radius = MathF.Max(radius, Vector2.Distance(origin, new Vector2(spot.Centre.X, spot.Centre.Z)));
                doors.Add((f, spot));
            }
            // Inside: nearer the origin than its farthest door (engine reading; the building's hull is not consulted).
            if (doors.Count > 0) list.Add((b, origin, radius, doors));
        }
        return list;
    }

    /// <summary>The spot <see cref="BashStandOff"/> out from the leaf, on the side away from the building's origin.</summary>
    static Vector2 StandSpot(Fixture door, DoorSpot spot)
    {
        var n = new Vector2(spot.Normal.X, spot.Normal.Z);
        if (n.LengthSquared() < 1e-6f) n = Vector2.UnitY;
        n = Vector2.Normalize(n);
        var centre = new Vector2(spot.Centre.X, spot.Centre.Z);
        if (Vector2.Dot(centre - Flat(door.Position), n) < 0) n = -n;
        return centre + n * BashStandOff;
    }

    static void StopBashing(CharacterTable table, int s, CharacterCold c)
    {
        c.Bashing = null;
        c.BashOrdered = false;
        c.BashTime = 0;
        ref var n = ref table.Next[s];
        n.Flags = (ushort)(n.Flags & ~(ushort)MoveFlags.AnyPath);
        n.PathCursor = 0;
        c.PathRequest = 0;
        n.TaskTime = 0;   // the pursuit asks for a path to the target at once
    }
}
