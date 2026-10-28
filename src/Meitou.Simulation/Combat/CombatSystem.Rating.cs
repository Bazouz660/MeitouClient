using Meitou.Data.Fcs;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation.Items;

namespace Meitou.Simulation.Combat;

public sealed partial class CombatSystem
{
    /// <summary>
    /// A character's combat rating (the status float the original's per-update step <c>FUN_140852a30</c> writes, <b>Observed</b>): (strength + melee attack rating + melee defence
    /// rating) / 3, x 0.1 for a non-animal with no weapon equipped. What dialogue conditions 21 / 22 compare. Engine reading: strength is the effective stat (<see cref="Eff"/>), the two
    /// ratings the ones the blow uses (attack with the weapon and indoors bonuses less the dust penalty; defence with the weapon and guard terms, the fist rating unarmed).
    /// </summary>
    public static float Rating(World world, int slot)
    {
        if (world.Characters.Cold(slot) is not { Stats: not null, Medical: not null, Fighter: { } f } c) return 0;
        float strength = Eff(c, StatsEnumerated.Strength);
        float attack = Eff(c, f.Unarmed ? StatsEnumerated.MartialArts : StatsEnumerated.MeleeAttack)
            + IndoorsAttackBonus(f.Unarmed ? f.UnarmedBonus : f.AttackBonus + (f.Weapon?.Stats.AttackMod ?? 0), IndoorsMod(world, slot, f)) - c.Medical.DustAttackPenalty;
        float defence = f.Unarmed ? UnarmedRating(c, f)
            : Eff(c, StatsEnumerated.MeleeDefence) + f.DefenceBonus + (f.Weapon?.Stats.DefenceMod ?? 0) + (Guarding(c) ? 20 : 0);
        float rating = (strength + attack + defence) / 3;
        bool armed = c.Inventory?.Items.Any(x => x.Type == FcsRecordType.WEAPON && InventoryCommands.IsEquipmentSection(x.Section)) == true;
        return f.IsAnimal || armed ? rating : rating * 0.1f;
    }
}
