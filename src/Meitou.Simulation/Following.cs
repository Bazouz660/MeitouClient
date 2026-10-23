namespace Meitou.Simulation;

/// <summary>
/// The context menu's "Follow" (the original's verb and order 31, <c>STAY_CLOSE_TO_TARGET</c>; docs/game/animals.md "Following"): <paramref name="Characters"/> (the selection when
/// empty) keep beside <paramref name="Target"/>, one of the player's own characters, until another order. How a pack beast or a pet keeps up with its squad.
/// </summary>
public sealed record FollowOrder(IReadOnlyList<CharacterId> Characters, CharacterId Target) : SimCommand;

/// <summary>Gives <see cref="FollowOrder"/>s (serial, the player's inputs).</summary>
public static class Following
{
    /// <summary>
    /// Each ordered player character other than the target takes the follow task towards it. The original then drops a follow order that would close a circle
    /// (<c>FUN_1405c8c10</c>, run by the order giver <c>FUN_1405c8da0</c> for orders 31 and 45, <b>Observed</b>): walking from the target along whom each follows, the first
    /// one found following the ordered character stops following.
    /// </summary>
    public static void Apply(World world, FollowOrder order)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(order.Target, out int target) || table.Cold(target) is not { IsPlayer: true } || !table.IsAlive(order.Target)) return;
        foreach (var id in order.Characters.Count > 0 ? order.Characters : world.Player.Selection.ToArray())
        {
            if (id == order.Target || !table.TryResolveNext(id, out int slot) || table.Cold(slot) is not { IsPlayer: true, IsImprisoned: false } cold) continue;
            if (cold.InSomething == Locks.HeldState.Bed) world.Fixtures.LeaveBed(world, id);
            MovementSystem.Stop(table, slot);
            table.Next[slot].Task = (byte)CharacterTask.Follow;
            table.Next[slot].TaskTime = 0;
            cold.Following = order.Target;
            BreakCircle(table, id);
        }
    }

    /// <summary>Walks from whom <paramref name="leader"/> follows: the first character following <paramref name="leader"/> back stops (at most one per order, as the original).</summary>
    static void BreakCircle(CharacterTable table, CharacterId leader)
    {
        var at = table.Cold(leader.Slot)!.Following;
        for (int guard = 0; guard < 64 && !at.IsNone && table.TryResolveNext(at, out int slot); guard++)
        {
            var cold = table.Cold(slot)!;
            if (cold.Following == leader)
            {
                cold.Following = CharacterId.None;
                if (table.Next[slot].Task == (byte)CharacterTask.Follow) MovementSystem.Stop(table, slot);
                return;
            }
            at = cold.Following;
        }
    }
}
