using System.Numerics;
using Meitou.Simulation;
using Meitou.Simulation.Dialogue;
using Meitou.Simulation.Items;
using Meitou.Simulation.Trading;

namespace Meitou.Net.Session;

/// <summary>
/// The host's only calls into the simulation's player API (docs/net.md, "Host"), so a change there is fixed in one file: player numbers, joins, focus,
/// join point, squad counts, the gate, per-viewer publish and the leave commands. Frame thread, between ticks.
/// </summary>
static class HostWorldLink
{
    /// <summary>Player numbers are a byte on some paths: a roster id that would need a higher one is refused (<see cref="RejectCode.NoSlot"/>).</summary>
    public const int MaxNumber = 255;

    /// <summary>The number of roster id <paramref name="id"/> in <paramref name="world"/>: its session's, else the next free one.</summary>
    public static int NumberFor(World world, Guid id) => world.Player.FindByGuid(id)?.Number ?? world.Player.NextNumber();

    /// <summary>Adds or refreshes player <paramref name="number"/>'s session (roster id and name).</summary>
    public static void Join(World world, int number, Guid id, string name) => world.Player.Join(number, id, name);

    /// <summary>Where player <paramref name="number"/> should look: their first living character in the last snapshot, else their session's focus, else the join point.</summary>
    public static Vector3 FocusOf(World world, int number, Vector3 fallback)
    {
        if (FirstCharacterOf(world, number) is { } at) return at;
        return world.Player.Find(number)?.Focus ?? JoinPoint(world, fallback);
    }

    /// <summary>Where a joiner's squad is made: near the host's first living character, else the host's focus, else <paramref name="fallback"/> (the host camera's target).</summary>
    public static Vector3 JoinPoint(World world, Vector3 fallback) => FirstCharacterOf(world, 0) ?? world.Player.Local.Focus ?? fallback;

    /// <summary>How many squads player <paramref name="number"/> owns.</summary>
    public static int Squads(World world, int number) => SquadManagement.PlayerSquads(world, number).Count;

    /// <summary>Player <paramref name="number"/> is ready in this world: <see cref="JoinPlayer"/> at the current tick (taken by the next tick or the paused path; a rejoin is idempotent).</summary>
    public static void EnqueueJoin(World world, int number, Vector3 near) =>
        world.Commands.Enqueue(new JoinPlayer(null, near) { Tick = world.Tick, Player = number });

    /// <summary>Player <paramref name="number"/> left: their trade, conversation and container windows close; their squads stay theirs and finish their orders.</summary>
    public static void PlayerLeft(World world, int number)
    {
        world.Commands.Enqueue(new CloseTrade { Tick = world.Tick, Player = number });
        world.Commands.Enqueue(new EndConversation { Tick = world.Tick, Player = number });
        world.Commands.Enqueue(new CloseContainer { Tick = world.Tick, Player = number });
    }

    /// <summary>A remote command through the gate; an admitted one is enqueued (restamped by the gate) and null returned, else the refusal text.</summary>
    public static string? Admit(World world, int number, SimCommand command, GateOptions gate)
    {
        var result = CommandGate.Admit(world, number, command, gate);
        if (result.Command is null) return result.Refusal ?? "Not allowed.";
        world.Commands.Enqueue(result.Command);
        return null;
    }

    /// <summary>Player <paramref name="number"/>'s view of the world.</summary>
    public static WorldSnapshot Publish(World world, int number) => world.PublishFor(number);

    static Vector3? FirstCharacterOf(World world, int number)
    {
        foreach (var c in world.Snapshot.Characters)
            if (c.IsPlayer && c.Owner == number && c.Body is not { Dead: true }) return c.Position;
        return null;
    }
}
