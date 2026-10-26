using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Gameplay.Combat;

namespace Meitou.Simulation.Combat;

/// <summary>
/// The combat numbers the HUD's stats tooltips show (<c>FUN_140892120</c>, docs/game/ui-screens.md section 4): the attack and block play rates (CharStats <c>+0x188</c> / <c>+0x18c</c>),
/// the weapon's cut and blunt damage at the fighter's skills (<c>FUN_140883ba0</c>, <c>FUN_140883c60</c>; fists' for an unarmed fighter) and the fists' cut plus blunt (<c>FUN_1408868b0</c>).
/// </summary>
public readonly record struct HudCombat(float AttackRate, float BlockRate, float Cut, float Blunt, float UnarmedDamage, float Resistance);

public sealed partial class CombatSystem
{
    /// <summary>The <see cref="HudCombat"/> numbers of a fighter with stats and a body; null without them.</summary>
    internal HudCombat? HudNumbers(CharacterCold cold)
    {
        if (cold is not { Fighter: { } fighter, Stats: { } stats, Medical: not null }) return null;
        float str = Eff(cold, StatsEnumerated.Strength), dex = Eff(cold, StatsEnumerated.Dexterity) * fighter.DexterityMult, tough = stats.Toughness * stats.Scale;
        var fists = DamageFormulas.Unarmed(Eff(cold, StatsEnumerated.MartialArts), tough, str, combat);
        float cut = fists.Cut, blunt = fists.Blunt;
        if (!fighter.Unarmed)
        {
            float skill = Eff(cold, fighter.WeaponSkill);
            cut = DamageFormulas.Cut(skill, dex, fighter.Weapon!.Stats, combat);
            blunt = DamageFormulas.Blunt(skill, str, fighter.Weapon.Stats, combat);
        }
        return new HudCombat(Rate(cold, true), Rate(cold, false), cut, blunt, fists.Cut + fists.Blunt, HitResolver.ToughnessResistance(tough, combat));
    }
}
