using System.Numerics;

namespace Meitou.Simulation.Items
{
    /// <summary>Open a container (<see cref="Locks.Fixture.Id"/>): the nearest of the characters (the issuer's selection when empty) who may walks to it and the issuer's window opens.</summary>
    public sealed record OpenContainerOrder(IReadOnlyList<CharacterId> Characters, string Fixture) : SimCommand;

    /// <summary>Closes the issuer's open container window.</summary>
    public sealed record CloseContainer : SimCommand;
}

namespace Meitou.Simulation
{
    /// <summary>
    /// Opening a container of a building's interior (docs/game/locks.md "Containers"): a character walks up to it, its inventory shows in a window beside the character's, and items are
    /// moved with <see cref="Production.PutInMachine"/> / <see cref="Production.TakeFromMachine"/> addressed by the fixture id. <b>Engine choices</b>: the reach, a locked one refusing, a shop's
    /// containers staying the shop's (its stock is one merged inventory, <see cref="Trading.Shop.ForBuilding"/>), the window closing when the opener walks off. Each player has a
    /// window of their own (<see cref="PlayerSession.OpenContainer"/>); two may have the same container open.
    /// </summary>
    public sealed partial class PlayerSystem
    {
        /// <summary>A character opens or uses a container within this distance of it (an engine choice).</summary>
        public const float ContainerReach = 20;

        static void OpenContainerInput(World world, Items.OpenContainerOrder order)
        {
            if (world.Fixtures.Find(order.Fixture) is not { Contents: not null } fixture) return;
            var table = world.Characters;
            int best = -1;
            float bestDistance = float.MaxValue;
            foreach (var id in world.Actors(order, order.Characters))
            {
                if (!table.TryResolveNext(id, out int s) || table.Cold(s) is not { IsPlayer: true, Medical.Incapacitated: false, Inventory: not null, IsImprisoned: false } c || !c.CarriedBy.IsNone) continue;
                float d = Vector2.Distance(Flat(table.Next[s].Position), Flat(fixture.Position));
                if (d < bestDistance) (best, bestDistance) = (s, d);
            }
            if (best < 0) return;
            var walker = table.Cold(best)!;
            if (fixture.Lock is { EffectivelyLocked: true })
            {
                world.Post(order.Player, $"{walker.Name}: It's locked.");
                return;
            }
            if (world.Squads.All.Any(s => s.Shop?.Building == fixture.Building))
            {
                world.Post(order.Player, $"{walker.Name}: That stock is the shop's. Trade with the shopkeeper.");
                return;
            }
            var player = world.SessionOf(order);
            walker.PickingLock = null;
            walker.Shackles = null;
            walker.PickingUpItem = null;
            player.OpenContainer = null;
            player.OpeningContainer = order.Fixture;
            player.ContainerWalker = table.IdOf(best);
            if (bestDistance > ContainerReach) MovementSystem.GoTo(table, best, Flat(fixture.Position), walker.RunSpeed);
        }

        /// <summary>
        /// Each tick, for each session: the walker in reach opens its container; one given another order, downed or gone gives up. An open window closes when its opener is far,
        /// down or gone.
        /// </summary>
        static void ContainerWorkers(World world)
        {
            foreach (var session in world.Player.Sessions) ContainerWorkers(world, session);
        }

        static void ContainerWorkers(World world, PlayerSession player)
        {
            var table = world.Characters;
            if (player.OpeningContainer is { } wanted)
            {
                if (world.Fixtures.Find(wanted) is not { Contents: not null } fixture || !table.TryResolveNext(player.ContainerWalker, out int s) || table.Cold(s) is not { Medical.Incapacitated: false } || !table.Cold(s)!.CarriedBy.IsNone)
                    player.OpeningContainer = null;
                else
                {
                    var at = Flat(fixture.Position);
                    if (Vector2.Distance(Flat(table.Next[s].Position), at) <= ContainerReach)
                    {
                        if (table.Next[s].Task == (byte)CharacterTask.GoTo) MovementSystem.Stop(table, s);
                        (player.OpenContainer, player.ContainerOpener, player.OpeningContainer) = (wanted, player.ContainerWalker, null);
                    }
                    else if (table.Next[s].Task != (byte)CharacterTask.GoTo || Vector2.Distance(table.Next[s].Goal, at) > ContainerReach) player.OpeningContainer = null;
                }
            }
            if (player.OpenContainer is { } open)
            {
                bool keep = world.Fixtures.Find(open) is { Contents: not null } f && table.TryResolveNext(player.ContainerOpener, out int o) && table.Cold(o) is { Medical.Incapacitated: false }
                    && Vector2.Distance(Flat(table.Next[o].Position), Flat(f.Position)) <= ContainerReach * 1.5f;
                if (!keep) player.OpenContainer = null;
            }
        }

        /// <summary>
        /// The container Take/Put address by <paramref name="site"/> (a fixture id) and the character, when it is open and the character is in reach; posts "too far" to
        /// <paramref name="player"/> otherwise. Null when <paramref name="site"/> is not a container.
        /// </summary>
        internal static Locks.Fixture? ContainerFor(World world, int player, string site, CharacterId who, out CharacterCold? cold)
        {
            cold = null;
            if (world.Fixtures.Find(site) is not { Contents: not null } f) return null;
            var table = world.Characters;
            if (!table.TryResolveNext(who, out int slot) || table.Cold(slot) is not { IsPlayer: true, Inventory: not null } c) return f;
            if (Vector2.Distance(Flat(table.Next[slot].Position), Flat(f.Position)) > ContainerReach * 1.5f) { world.Post(player, $"{c.Name} is too far away."); return f; }
            cold = c;
            return f;
        }
    }
}
