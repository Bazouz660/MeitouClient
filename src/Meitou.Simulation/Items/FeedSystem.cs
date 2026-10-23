using Meitou.Data.Fcs;
using Meitou.Simulation.Bodies;

namespace Meitou.Simulation.Items;

/// <summary>Settings of <see cref="FeedSystem"/>; all engine choices (the original's eating rules are <b>Unknown</b>, docs/simulation.md "Items and eating").</summary>
public sealed class FeedSettings
{
    /// <summary>A character eats when its hunger level is below this (2: the "Hungry" line of character-stats.md) and its stomach is empty.</summary>
    public float HungryBelow { get; init; } = 2;
    /// <summary>Ticks between looks at who is hungry.</summary>
    public int CheckEveryTicks { get; init; } = 30;
    /// <summary>A squad mate shares its food with a hungry member standing within this many units.</summary>
    public float SquadReach { get; init; } = 60;
    /// <summary>Hungry NPCs (not the player's) with no food in reach get a ration (<see cref="ItemFactory.CheapestFood"/>) at most every <see cref="ProvisionHours"/> game hours: a stand-in for the town markets and camps the real ones eat from.</summary>
    public bool ProvisionNpcs { get; init; } = true;
    public float ProvisionHours { get; init; } = 12;
}

/// <summary>
/// Eating. In the slow-world step, every <see cref="FeedSettings.CheckEveryTicks"/> ticks, each living, conscious character whose hunger is below
/// <see cref="FeedSettings.HungryBelow"/> and whose stomach is empty eats one food item: from its own inventory, else a squad mate's within
/// <see cref="FeedSettings.SquadReach"/>. The item is the one whose nutrition fills the missing hunger best without overshooting (else the smallest); eating is instant and
/// adds the item's charges / 100 to the stomach (<see cref="MedicalState.Feed"/>). The player's characters eat the same way (the original's hunger test is <b>Unknown</b>), except that
/// they take a mate's food only from its backpack within <see cref="SquadAiRules.ShareReach"/> and only with <see cref="SquadAi.Share"/>, and their animals do not eat with
/// <see cref="SquadAi.Feed"/> off; only NPCs get provisions when they have nothing. Serial, in slot order, so it does not depend on threads.
/// </summary>
public sealed class FeedSystem(ItemFactory items, FeedSettings? settings = null) : ITickSystem
{
    public FeedSettings Settings { get; } = settings ?? new FeedSettings();
    public ItemFactory Items { get; } = items;

    public void SlowWorld(World world)
    {
        if (world.Tick % Math.Max(Settings.CheckEveryTicks, 1) != 0) return;
        var table = world.Characters;
        var state = table.Next;
        for (int i = 0; i < table.HighWater; i++)
        {
            if (!state[i].Alive || table.Cold(i) is not { Medical: { } med, Race: { } race, Inventory: { } own } cold) continue;
            if (med.Incapacitated || race.HungerRate <= 0 || med.Hunger >= Settings.HungryBelow || med.Fed > 0.001f) continue;
            // Feed Animals off: the player's animals do not eat (FUN_1405d29c0 returns before looking for food).
            if (cold.IsPlayer && (world.Player.SquadAi & SquadAi.Feed) == 0 && (cold.Fighter?.IsAnimal ?? cold.Appearance?.Animal is not null)) continue;
            float want = MedicalState.MaxHunger - med.Hunger;
            var (owner, food) = Find(world, i, cold, own, want);
            if (food is null && !cold.IsPlayer && Settings.ProvisionNpcs && world.Tick >= cold.NextProvisionTick && Items.CheapestFood() is { } given)
            {
                cold.NextProvisionTick = world.Tick + (long)(Settings.ProvisionHours / BodySystem.HoursPerTick);
                if (own.Add(given)) (owner, food) = (own, given);
            }
            if (food is null || owner is null) continue;
            med.Feed(food.Nutrition);
            world.EmitSound(SoundKind.Eat, i, state[i].Position);
            owner.RemoveOne(food);
        }
    }

    (Inventory? Owner, ItemInstance? Food) Find(World world, int slot, CharacterCold cold, Inventory own, float want)
    {
        var best = Best(own, want);
        if (best is not null) return (own, best);
        if (cold.SquadId < 0 || world.Squads.Find(cold.SquadId) is not { } squad) return (null, null);
        // The player's characters share only with Share Food on, and only what a mate carries in its backpack (FUN_1405d29c0, docs/game/ai-tasks.md "Squad AI options").
        bool player = cold.IsPlayer;
        if (player && (world.Player.SquadAi & SquadAi.Share) == 0) return (null, null);
        var table = world.Characters;
        var here = table.Next[slot].Position;
        float reach = player ? SquadAiRules.ShareReach : Settings.SquadReach, reach2 = reach * reach;
        foreach (var m in squad.Members)
        {
            if (m.Slot == slot || !table.TryResolveNext(m, out int s) || table.Cold(s) is not { Inventory: { } mates, Medical: { Dead: false } }) continue;
            var d = table.Next[s].Position - here;
            if (d.X * d.X + d.Z * d.Z > reach2) continue;
            var from = player ? mates.Backpack?.Contents : mates;
            if (from is not null && Best(from, want) is { } food) return (from, food);
        }
        return (null, null);
    }

    /// <summary>The food item that fills <paramref name="want"/> best without overshooting; else the smallest.</summary>
    static ItemInstance? Best(Inventory inventory, float want)
    {
        ItemInstance? fits = null, smallest = null;
        foreach (var item in inventory.All())
        {
            if (!item.IsFood || item.Quantity <= 0 || item.Death) continue;   // a live owner's death items are not counted (FUN_1407465d0)
            if (smallest is null || item.Nutrition < smallest.Nutrition) smallest = item;
            if (item.Nutrition <= want && (fits is null || item.Nutrition > fits.Nutrition)) fits = item;
        }
        return fits ?? smallest;
    }
}
