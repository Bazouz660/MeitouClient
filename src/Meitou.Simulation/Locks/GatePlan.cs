using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Combat;

namespace Meitou.Simulation.Locks;

/// <summary>
/// The planner's view of an NPC attacker whose target is behind a gate that is not open (docs/game/locks.md "Gates"): row 4 Attacking needs
/// DESTINATION_NOT_BLOCKED_BY_FORTIFICATIONS (90), whose provider 122 Open gate finds the gate and needs it unlocked (!54), which 123 / 290 Bashing gate give at the gate
/// (AT_LOCATION_FORTIFICATION_GATE 91, walked to by 121). Only the gate's states are evaluated; the rest of row 4's list B is held. Serial phase only.
/// 122 is a restricted row, which the original's planner offers to every plan of a character whose faction is not the player's (<c>FUN_14051bae0</c>, <b>Verified</b>); the attackers here are NPCs.
/// </summary>
sealed class GatePlan : IPlanWorld
{
    /// <summary>Kinds of <see cref="PlanTarget"/>: the attack target, and the gate (at its main door's leaf).</summary>
    public const byte Foe = 1, Gate = 2;
    /// <summary>A gateway's reach (<c>getReachRange</c>, docs/game/locks.md "Door and place"; x2 standing), and the most bashers one gate takes (crowdLimit8).</summary>
    public const float GateReach = 45;
    public const int CrowdLimit = 8;

    World world = null!;
    CombatSystem? combat;
    LockSystem locks = null!;
    int me, foe;
    CharacterCold cold = null!;
    string building = "";
    Fixture door = null!;
    DoorSpot spot;
    float aggressive;
    readonly List<PlanStep> plan = [];

    public int Category => 0x1;
    public bool Squadless => true;

    /// <summary>Plans row 4 for attacker <paramref name="s"/> at <paramref name="t"/> through gate <paramref name="gate"/> (its main door and leaf); true when the plan bashes it.</summary>
    public bool PlansBash(World w, CombatSystem? fights, LockSystem lockSystem, int s, int t, string gate, Fixture mainDoor, DoorSpot mainSpot)
    {
        (world, combat, locks, me, foe, building, door, spot) = (w, fights, lockSystem, s, t, gate, mainDoor, mainSpot);
        cold = w.Characters.Cold(s)!;
        aggressive = DoorPlan.AggressiveTowards(w, fights, cold, gate, mainSpot.Centre);
        if (TaskDataTable.Of(TaskType.MELEE_ATTACK) is not { } row) return false;
        var target = new PlanTarget(Foe, w.Characters.Next[t].Position);
        if (TaskPlanner.Local.Plan(row, 1, target, this, [], plan) <= 0) return false;
        foreach (var step in plan)
            if (step.Type is TaskType.BASH_GATE or TaskType.Unnamed290 or TaskType.BASH_DOOR) return true;
        return false;
    }

    public float Desire(TaskDataRow row, in PlanTarget target)
    {
        if (row.Type is TaskType.BASH_DOOR or TaskType.BASH_DOOR_HERE or TaskType.BASH_GATE or TaskType.Unnamed290) return aggressive;
        float d = TaskProfiles.Of(row.Type).Desire;
        return d >= 0 ? d : 1;
    }

    public bool Holds(TaskCondition condition, in PlanTarget target)
    {
        ref var n = ref world.Characters.Next[me];
        bool atGate = target.Kind == Gate;
        int owner = world.Fixtures.DoorOwner(building);
        bool value = condition.State switch
        {
            StateType.DESTINATION_NOT_BLOCKED_BY_FORTIFICATIONS => locks.NotBlocked(world, n.Position, world.Characters.Next[foe].Position),
            StateType.DOOR_IS_OPEN => door.DoorBroken || door.Motion is DoorMotion.Open or DoorMotion.Opening,
            StateType.isDoorLocked => door.Lock is { EffectivelyLocked: true } && !door.DoorBroken,
            StateType.DOOR_IS_BROKEN => door.DoorBroken,
            StateType.AT_LOCATION_FORTIFICATION_GATE or StateType.AT_LOCATION_BUILDING_DOOR => atGate && AtGate(n, spot),
            StateType.isObjectMine => owner == cold.Faction,
            StateType.isAlly => combat?.Relations is { } r && r.Contains(cold.Faction) && r.Contains(owner) && r.Get(cold.Faction, owner) >= FactionRelations.AllyThreshold,
            StateType.hasWeaponEquipped => cold.Fighter is { } f && (f.IsAnimal || !f.Unarmed),
            StateType.crowdLimit8 => LockSystem.BashersAt(world, door.Id) < CrowdLimit,
            StateType.isMovementAllowed or StateType.LOCATION_IS_ACCESSIBLE or StateType.DESTINATION_IS_ACCESSIBLE or StateType.isReadyForAction
                or StateType.isWithin50Meters => true,
            _ => false,
        };
        return value == condition.Holds;
    }

    /// <summary>122's finder: the gate (the first one on the route to the target, <c>FUN_1402ebdd0</c>; engine reading: the nearest of the equally good).</summary>
    public PlanTarget Find(TaskDataRow row, in PlanTarget target) =>
        row.Type == TaskType.OPEN_FORTIFICATION_GATE ? new PlanTarget(Gate, spot.Centre) : default;

    /// <summary>AT_LOCATION_FORTIFICATION_GATE (<c>FUN_1405a25b0</c> → <c>AI::isWithinChoppingRange</c>, <b>Verified</b> call): within <see cref="GateReach"/> of the leaf (x2 standing).</summary>
    internal static bool AtGate(in CharacterHot n, DoorSpot spot)
    {
        float reach = (n.Flags & (ushort)MoveFlags.AnyPath) == 0 ? GateReach * 2 : GateReach;
        return Vector2.Distance(new(n.Position.X, n.Position.Z), new(spot.Centre.X, spot.Centre.Z)) < reach && MathF.Abs(n.Position.Y - spot.Foot) <= DoorPlan.DoorHeight;
    }
}
