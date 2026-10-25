using System.Numerics;

namespace Meitou.Simulation.Items
{
    /// <summary>
    /// Open a container (<see cref="Locks.Fixture.Id"/>): the nearest of the characters (the selection when empty) who may walks to it and its window opens. Another faction's locked one:
    /// with <paramref name="Alt"/> its lock is picked (76 PICK_LOCK), else the hint and a walk to it; a shop's without Alt: the steal hint and a walk.
    /// </summary>
    public sealed record OpenContainerOrder(IReadOnlyList<CharacterId> Characters, string Fixture, bool Alt = false) : SimCommand;

    /// <summary>Closes the open container's window.</summary>
    public sealed record CloseContainer : SimCommand;
}

namespace Meitou.Simulation
{
    /// <summary>
    /// Opening a container of a building's interior (docs/game/locks.md "Containers"): a character walks up to it, its inventory shows in a window beside the character's, and items are
    /// moved with <see cref="Production.PutInMachine"/> / <see cref="Production.TakeFromMachine"/> addressed by the fixture id. <b>Engine choices</b>: the reach, a locked one refusing, a shop's
    /// containers staying the shop's (each its own rectangle of the shop's stock, <see cref="Trading.Shop.ForBuilding"/>), what is taken from another faction's marked stolen with no witness test.
    /// </summary>
    public sealed partial class PlayerSystem
    {
        /// <summary>A character opens or uses a container within this distance of it (an engine choice).</summary>
        public const float ContainerReach = 20;

        /// <summary>How far above or below a container a character may stand to open or use it (engine choice: a storey is about 66).</summary>
        public const float ContainerFloorSlack = 20;

        /// <summary>
        /// How far (X/Z) a character may be from a container it puts into or takes from: the original's limit for an item moved out of a character's inventory (60; out of a building's, its
        /// bounds radius + 40, which a chest's is close to; docs/game/economy.md "Trading range").
        /// </summary>
        public const float TradingRange = 60;

        static void OpenContainerInput(World world, Items.OpenContainerOrder order)
        {
            if (world.Fixtures.Find(order.Fixture) is not { Contents: not null } fixture) return;
            var table = world.Characters;
            int best = -1;
            float bestDistance = float.MaxValue;
            foreach (var id in order.Characters.Count > 0 ? order.Characters : world.Player.Selection.ToArray())
            {
                if (!table.TryResolveNext(id, out int s) || table.Cold(s) is not { IsPlayer: true, Medical.Incapacitated: false, Inventory: not null, IsImprisoned: false } c || !c.CarriedBy.IsNone) continue;
                float d = Vector2.Distance(Flat(table.Next[s].Position), Flat(fixture.Position));
                if (d < bestDistance) (best, bestDistance) = (s, d);
            }
            if (best < 0) return;
            var walker = table.Cold(best)!;
            bool theirs = fixture.Owner >= 0 && fixture.Owner != world.Player.Faction;
            if (fixture.Lock is { EffectivelyLocked: true })
            {
                if (!theirs)
                {
                    world.Post($"{walker.Name}: It's locked.");
                    return;
                }
                // Another faction's locked container (the original's FUN_1407fc6e0): Alt picks the lock, else the hint and a walk to it.
                walker.Shackles = null;
                walker.PickingLock = order.Alt ? fixture.Id : null;
                if (!order.Alt) world.Post("Hold down the ALT key to pick the lock.");
                MovementSystem.GoToFloor(world, best, fixture.Position, walker.RunSpeed);
                return;
            }
            if (ShopOf(world, fixture) is not null && !order.Alt && theirs)
            {
                // A shop's stock without Alt: the original's hint and a move; with Alt (the menu's Loot) the walker steals from it (the window shows that container's part of the shop's stock).
                world.Post("Hold down the ALT key to steal items");
                MovementSystem.GoToFloor(world, best, fixture.Position, walker.RunSpeed);
                return;
            }
            var player = world.Player;
            walker.PickingLock = null;
            walker.Shackles = null;
            walker.PickingUpItem = null;
            player.OpenContainer = null;
            player.OpeningContainer = order.Fixture;
            player.ContainerWalker = table.IdOf(best);
            if (bestDistance > ContainerReach || !SameFloor(table.Next[best].Position, fixture.Position)) MovementSystem.GoToFloor(world, best, fixture.Position, walker.RunSpeed);
        }

        /// <summary>Each tick: the walker in reach opens its container; one given another order, downed or gone gives up. An open window stays until the player closes it; it closes when its opener is down or gone.</summary>
        static void ContainerWorkers(World world)
        {
            var player = world.Player;
            var table = world.Characters;
            if (player.OpeningContainer is { } wanted)
            {
                if (world.Fixtures.Find(wanted) is not { Contents: not null } fixture || !table.TryResolveNext(player.ContainerWalker, out int s) || table.Cold(s) is not { Medical.Incapacitated: false } || !table.Cold(s)!.CarriedBy.IsNone)
                    player.OpeningContainer = null;
                else
                {
                    var at = Flat(fixture.Position);
                    if (Vector2.Distance(Flat(table.Next[s].Position), at) <= ContainerReach && SameFloor(table.Next[s].Position, fixture.Position))
                    {
                        if (table.Next[s].Task == (byte)CharacterTask.GoTo) MovementSystem.Stop(table, s);
                        (player.OpenContainer, player.ContainerOpener, player.OpeningContainer) = (wanted, player.ContainerWalker, null);
                    }
                    else if (table.Next[s].Task != (byte)CharacterTask.GoTo || Vector2.Distance(table.Next[s].Goal, at) > ContainerReach) player.OpeningContainer = null;
                }
            }
            if (player.OpenContainer is { } open)
            {
                // The original has no walk-away test: the windows stay until the player closes them (docs/game/locks.md "Containers"); only an opener who is down or gone closes it here.
                bool keep = world.Fixtures.Find(open) is { Contents: not null } && table.TryResolveNext(player.ContainerOpener, out int o) && table.Cold(o) is { Medical.Incapacitated: false };
                if (!keep) player.OpenContainer = null;
            }
        }

        /// <summary>The building shop <paramref name="container"/> is a stock container of, if any.</summary>
        internal static Trading.Shop? ShopOf(World world, Locks.Fixture container) =>
            world.Squads.All.Select(s => s.Shop).FirstOrDefault(s => s?.Building == container.Building && s.RegionOf(container.Id, out _));

        /// <summary>
        /// The grid a container window works on: the container's own, but for a building shop's stock container the shop's stock (<see cref="Trading.Shop.ForBuilding"/>), of which the
        /// container's grid is <paramref name="region"/>: the window shows and takes only the items there (at cells relative to its corner), taking them is theft.
        /// </summary>
        internal static Items.Inventory GridOf(World world, Locks.Fixture container, out Items.GridRegion region)
        {
            if (ShopOf(world, container) is { } shop && shop.RegionOf(container.Id, out region)) return shop.Stock;
            region = new(0, 0, container.Contents!.Width, container.Contents.Height);
            return container.Contents;
        }

        internal static Items.Inventory GridOf(World world, Locks.Fixture container) => GridOf(world, container, out _);


        static bool SameFloor(Vector3 a, Vector3 b) => MathF.Abs(a.Y - b.Y) <= ContainerFloorSlack;

        /// <summary>The container Take/Put address by <paramref name="site"/> (a fixture id) and the character, when it is open and the character is in reach; posts "Out of trading range." otherwise (<see cref="TradingRange"/>). Null when <paramref name="site"/> is not a container.</summary>
        internal static Locks.Fixture? ContainerFor(World world, string site, CharacterId who, out CharacterCold? cold)
        {
            cold = null;
            if (world.Fixtures.Find(site) is not { Contents: not null } f) return null;
            var table = world.Characters;
            if (!table.TryResolveNext(who, out int slot) || table.Cold(slot) is not { IsPlayer: true, Inventory: not null } c) return f;
            if (Vector2.Distance(Flat(table.Next[slot].Position), Flat(f.Position)) > TradingRange) { world.Post("Out of trading range."); return f; }
            cold = c;
            return f;
        }
    }
}
