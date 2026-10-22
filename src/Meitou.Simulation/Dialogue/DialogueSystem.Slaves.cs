using System.Numerics;
using Meitou.Data.Gameplay.Bodies;

namespace Meitou.Simulation.Dialogue;

public sealed partial class DialogueSystem
{
    /// <summary>How near the slaver the seller's squadmates must stand to be offered (DA 41, <c>FUN_140680560</c>, <b>Observed</b>).</summary>
    const float SellRange = 300;

    /// <summary>
    /// A slave's value (<c>FUN_1406751f0</c>, docs/game/economy.md 12, <b>Observed</b>): trunc(strength + melee attack + dexterity) times its RACE <c>buy value</c>, clamped to
    /// 1000..20000. The original scales strength and dexterity by the stats multiplier, which is 1 for every humanoid.
    /// </summary>
    public static int SlaveValue(CharacterCold c)
    {
        var stats = c.Stats;
        float sum = stats is null ? 0 : stats[StatsEnumerated.Strength] + stats[StatsEnumerated.MeleeAttack] + stats[StatsEnumerated.Dexterity];
        long value = (long)(int)sum * (c.Race?.BuyValue ?? 100);
        return (int)Math.Clamp(value, 1000, 20000);
    }

    /// <summary>
    /// DA 41 <c>CHOOSE_SLAVES_SELLING</c>'s list (<c>FUN_140680560</c>, <c>CharacterTrading_SlaveSelling</c>, <b>Observed</b>): the living members of the talker's squad, the talker
    /// too, within <see cref="SellRange"/> of the slaver; each for the line's val0 when above 0, else clamp(<see cref="SlaveValue"/> / 2, 200, 2500).
    /// </summary>
    List<(CharacterId Who, string Name, int Price)> SellOffers(World world, CharacterId slaver, CharacterId seller, int price)
    {
        var offers = new List<(CharacterId, string, int)>();
        var table = world.Characters;
        if (!table.TryResolveNext(slaver, out int s) || Cold(world, seller) is not { SquadId: >= 0 } sc || world.Squads.Find(sc.SquadId) is not { } squad) return offers;
        var at = table.Next[s].Position;
        foreach (var id in squad.Members)
        {
            if (id == slaver || !table.TryResolveNext(id, out int m) || !table.Next[m].Alive || table.Cold(m) is not { Medical.Dead: false } mc) continue;
            if (Vector3.DistanceSquared(table.Next[m].Position, at) > SellRange * SellRange) continue;
            offers.Add((id, mc.Name, price > 0 ? price : Math.Clamp(SlaveValue(mc) / 2, 200, 2500)));
        }
        return offers;
    }

    /// <summary>
    /// DA 42 <c>CHOOSE_SLAVES_BUYING</c>'s list (<c>CharacterTrading_SlaveBuying</c>, <b>Observed</b>): the slaver platoon's living slaves in slave state 1, each for the line's val0
    /// when above 0, else <see cref="SlaveValue"/>, doubled for a character of the player's faction. <b>Engine reading</b> of the platoon's slave list (Meitou keeps none): slaves
    /// owned by the slaver's faction in its town (within twice its resident squad's home radius of the centre, as the police's town), else within <see cref="SellRange"/> of it.
    /// </summary>
    List<(CharacterId Who, string Name, int Price)> BuyOffers(World world, CharacterId slaver, int price)
    {
        var offers = new List<(CharacterId, string, int)>();
        var table = world.Characters;
        if (!table.TryResolveNext(slaver, out int s) || table.Cold(s) is not { } mc) return offers;
        var squad = mc.SquadId >= 0 ? world.Squads.Find(mc.SquadId) : null;
        var at = table.Next[s].Position;
        var centre = squad is { TownOwn: true } ? squad.HomeCentre : new Vector2(at.X, at.Z);
        float r = squad is { TownOwn: true } ? squad.HomeRadius * (Arrests?.Settings.TownSlack ?? 2) : SellRange;
        for (int i = 0; i < table.HighWater; i++)
        {
            if (i == s || !table.Next[i].Alive || table.Cold(i) is not { SlaveState: 1, Medical.Dead: false } c || c.SlaveOwner != mc.Faction) continue;
            var p = table.Next[i].Position;
            if (Vector2.DistanceSquared(new Vector2(p.X, p.Z), centre) > r * r) continue;
            int cost = price > 0 ? price : SlaveValue(c);
            if (world.Player.Faction >= 0 && c.Faction == world.Player.Faction) cost *= 2;
            offers.Add((table.IdOf(i), c.Name, cost));
        }
        return offers;
    }

    /// <summary>
    /// Selling (<c>CharacterTrading_SlaveSelling</c>'s confirm, <b>Observed</b>): the character becomes a slave of the slaver's faction, relations of the slaver's faction with the
    /// player's +4 and of the character's own faction -4, and the player is paid. Not built: the shackles it is put in out of a cage, the memory tag.
    /// </summary>
    void SellSlave(World world, CharacterId slaver, CharacterId who, int price)
    {
        if (Cold(world, slaver) is not { } mc || Cold(world, who) is not { SlaveState: 0 } c) return;
        Locks.FixtureRegistry.Enslave(world, c, mc.Faction);
        world.Player.Money += price;
        world.Post(Loc.Tr("Sold {1} for c.{2}", c.Name, price));
        PlayerRelation(world, mc.Faction, 4);
        PlayerRelation(world, c.Faction, -4);
    }

    /// <summary>
    /// Buying (<c>CharacterTrading_SlaveBuying</c>'s confirm, <c>FUN_1406add20</c>, <b>Observed</b>): the slave joins the buyer's squad, is let out of its cage, its slave state cleared
    /// and shackles unlocked; it says its event 51 (BOUGHT_ME_FROM_SLAVERY) to the buyer; relations of the slaver's faction with the player's +1; the player pays. False (the list stays as
    /// it is): too little money or no room in the squad. Not built: the memory tag.
    /// </summary>
    bool BuySlave(World world, CharacterId slaver, CharacterId buyer, CharacterId who, int price)
    {
        var table = world.Characters;
        if (Cold(world, slaver) is not { } mc || Cold(world, buyer) is not { } bc || !table.TryResolveNext(who, out int slot) || table.Cold(slot) is not { SlaveState: not 0 } c)
            return false;
        if (world.Player.Money < price) { world.Post(Loc.Tr("You can't afford that.")); return false; }
        if (!c.IsPlayer && PlayerHeads(world) >= data.Bodies.Constants.MaxFactionSize) { world.Post(Loc.Tr("It looks like your squad is a little full.")); return false; }
        if (c.IsImprisoned) world.Fixtures.Release(world, who);
        if (!c.IsPlayer && !JoinPlayer(world, slot, bc)) return false;
        Locks.FixtureRegistry.Unslave(world, c);
        world.Player.Money -= price;
        world.Post(Loc.Tr("Bought {1} for c.{2}", c.Name, price));
        FireEvent(world, who, buyer, BoughtMeFromSlavery);
        PlayerRelation(world, mc.Faction, 1);
        return true;
    }

    /// <summary>EV_BOUGHT_ME_FROM_SLAVERY.</summary>
    const int BoughtMeFromSlavery = 51;

    void PlayerRelation(World world, int faction, int amount)
    {
        if (faction < 0 || world.Player.Faction < 0 || faction == world.Player.Faction || !data.Relations.Contains(faction)) return;
        ChangeRelation(world, faction, world.Player.Faction, amount);
    }
}
