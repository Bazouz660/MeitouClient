using System.Numerics;
using Meitou.Simulation;
using Meitou.Simulation.Dialogue;
using Meitou.Simulation.Items;
using Meitou.Simulation.Trading;

namespace Meitou.Net.Session;

/// <summary>The host's calls into the simulation's player roster and squads (docs/net.md, "Host"), kept in one place. Frame thread, between ticks.</summary>
static class HostWorldLink
{
    /// <summary>The number of the player with <paramref name="guid"/> in <paramref name="world"/>: the one its roster gives, else the next free one.</summary>
    public static int NumberFor(World world, Guid guid) => world.Player.FindByGuid(guid)?.Number ?? world.Player.NextNumber();

    /// <summary>Where player <paramref name="player"/> looks first: their first living character, else their session's focus, else <see cref="JoinPoint"/>.</summary>
    public static Vector3 FocusOf(World world, int player, Vector3 fallback)
    {
        if (FirstOwned(world, player) is { } at) return at;
        if (world.Player.Find(player)?.Focus is { } focus && IsFinite(focus)) return focus;
        return JoinPoint(world, fallback);
    }

    /// <summary>Where a new player's start squad is made: by the host's first living character, else the host's focus, else <paramref name="fallback"/> (the host camera).</summary>
    public static Vector3 JoinPoint(World world, Vector3 fallback)
    {
        if (FirstOwned(world, 0) is { } at) return at;
        if (world.Player.Local.Focus is { } focus && IsFinite(focus)) return focus;
        return fallback;
    }

    /// <summary>How many squads player <paramref name="player"/> owns (the roster's count).</summary>
    public static int SquadsOf(World world, int player) => SquadManagement.PlayerSquads(world, player).Count;

    /// <summary>A player left: their trade, conversation and container windows close at the next tick; their squads stay owned and finish their orders.</summary>
    public static void PlayerLeft(World world, int player)
    {
        if (world.Player.Find(player) is null) return;
        world.Commands.Enqueue(new CloseTrade { Tick = world.Tick, Player = player });
        world.Commands.Enqueue(new EndConversation { Tick = world.Tick, Player = player });
        world.Commands.Enqueue(new CloseContainer { Tick = world.Tick, Player = player });
    }

    /// <summary>The join of player <paramref name="player"/>, enqueued at every <c>Ready</c>: a new player gets a start squad, a returning one their squads back.</summary>
    public static void EnqueueJoin(World world, int player, Vector3 near) =>
        world.Commands.Enqueue(new JoinPlayer(null, near) { Tick = world.Tick, Player = player });

    static Vector3? FirstOwned(World world, int player)
    {
        foreach (var c in world.Snapshot.Characters)
            if (c.IsPlayer && c.Owner == player && IsFinite(c.Position)) return c.Position;
        return null;
    }

    static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
