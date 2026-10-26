using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Combat;

namespace Meitou.Simulation;

/// <summary>
/// The HUD stats panel's lines with their tooltips (<c>FUN_140892120</c>) and the extended lines' tooltips (<c>FUN_140895350</c>), docs/game/ui-screens.md section 4 "Stat tooltips".
/// Rows are (key, value) with the exe's inline colour tags: Bad <c>#59231a</c>, Good <c>#a8b774</c>, Main <c>#afa68b</c>, and the text default <c>#140806</c> for a signed 0.
/// Meitou has no indoors flag, so the indoors rows show the original's "-".
/// </summary>
static class HudTips
{
    const string Bad = "#59231a", Good = "#a8b774", Main = "#afa68b", Ink = "#140806";
    const string None = "-";

    /// <summary>A signed whole number in its sign's colour (<c>FUN_140889320</c>): <c>+N</c> Good, <c>-N</c> Bad, <c>0</c> in the text default.</summary>
    static string Signed(int n) => (n < 0 ? Bad : n > 0 ? Good : Ink) + Loc.Tr(n > 0 ? "+{1,num}" : "{1,num}", n);

    static string Penalty(int percent) => Bad + "-" + Loc.Tr("{1,num}%", percent);

    static string Int(float v) => Loc.Tr("{1,num}", (int)v);

    static int Round(float v) => (int)MathF.Floor(v + 0.5f);

    /// <summary>A multiplier <c>{x.xx}x</c> (<c>FUN_1408894f0</c>): above 1 Bad, below 1 Good, 1 plain.</summary>
    static string Mult(float x) => (x > 1 ? Bad : x < 1 ? Good : "") + Loc.Tr("{1,num=fix,p=2}x", x);

    /// <summary>The stats panel's lines (docs/game/ui-screens.md section 4), each with its tooltip.</summary>
    public static List<HudStatLine> Lines(CharacterCold cold, MedicalState med, CharacterStats stats, HudCombat? hc, GameConstants? constants)
    {
        var fighter = cold.Fighter;
        var factors = (fighter?.Factors ?? StatFactors.None) with { Stun = med.DustStatFactor };
        float e = fighter?.EncumbranceFactor ?? 1;
        float Stored(StatsEnumerated stat) => stats[stat] * stats.Scale;
        float Eff(StatsEnumerated stat, StatUse use = StatUse.All) => Stored(stat) * med.StatMultiplier(stat, use, factors);
        int dust = (int)(med.DustInEffect * 10);
        bool defensive = (cold.Orders & CharacterOrders.Block) != 0;
        HudStatLine Line(string label, StatsEnumerated stat, float factor = 1) => new(label, stats[stat] * med.StatMultiplier(stat) * factor, stats[stat]);

        var lines = new List<HudStatLine>(6);
        bool ranged = (cold.Orders & CharacterOrders.Ranged) != 0 && fighter?.Crossbow is not null;
        if (ranged)
        {
            lines.Add(Line("Crossbows:", StatsEnumerated.Crossbows) with { Tip = Breakdown(med, stats, factors, StatsEnumerated.Crossbows, "Crossbows") });
            lines.Add(Line("Precision:", StatsEnumerated.FriendlyFire) with { Tip = Breakdown(med, stats, factors, StatsEnumerated.FriendlyFire, "Careful Shooting") });
            lines.Add(Line("Perception:", StatsEnumerated.Perception) with { Tip = Breakdown(med, stats, factors, StatsEnumerated.Perception, "Perception") });
        }
        else if (fighter is null || fighter.Unarmed)
        {
            // FUN_140884e00 and FUN_140884680: the value lerp(e, 0.2, 1) x (bonus + skill), the change the bonus less 0.8 (1 - e) x skill.
            float ma = Eff(StatsEnumerated.MartialArts);
            int gear = (int)(fighter?.UnarmedBonus ?? 0), bonus = gear - dust, encumbrance = (int)-(0.8f * (1 - e) * ma);
            int value = (int)((0.2f + 0.8f * e) * (bonus + ma)), change = bonus + encumbrance;
            // The original labels the encumbrance row of this tooltip "Strength XP:".
            var tip = new List<(string, string)> { (Loc.Tr("Total equipment bonuses:"), Signed(gear)), (Loc.Tr("Indoors bonus:"), None),
                (Loc.Tr("Weather penalty:"), dust != 0 ? Signed(-dust) : None), (Loc.Tr("Encumbrance penalty:"), Signed(encumbrance)),
                (Loc.Tr("Strength XP:"), Loc.Tr("{1,num}%", (int)((1 - e) * 100))) };
            if (hc is { } fists) tip.Add((Loc.Tr("Damage:"), Int(fists.UnarmedDamage)));
            lines.Add(new HudStatLine("Martial Arts:", value, value - change) { Tip = tip });
        }
        else
        {
            // FUN_1408844c0 / FUN_140884e80: the equipment bonus less the dust penalty, then the effective skill on top; the change is the bonus.
            var weapon = fighter.Weapon!.Stats;
            int gear = (int)(fighter.AttackBonus + weapon.AttackMod), change = gear - dust, value = (int)(change + Eff(StatsEnumerated.MeleeAttack));
            lines.Add(new HudStatLine("Attack:", value, value - change)
            {
                Tip = [(Loc.Tr("Total equipment bonuses:"), Signed(gear)), (Loc.Tr("Weapon indoors penalty:"), None), (Loc.Tr("Weather penalty:"), dust != 0 ? Signed(-dust) : None),
                    (Loc.Tr("Base hit chance:"), Loc.Tr("{1,num}%", value))],
            });
            // FUN_140884710 / FUN_140884f50: the defence bonus, +20 in defensive mode, then the effective skill on top.
            int def = (int)(fighter.DefenceBonus + weapon.DefenceMod), defChange = def + (defensive ? 20 : 0);
            int defValue = (int)(defChange + Eff(StatsEnumerated.MeleeDefence));
            var defTip = new List<(string, string)> { (Loc.Tr("Total equipment bonuses:"), Signed(def)) };
            if (defensive) defTip.Add((Loc.Tr("Defensive combat mode:"), Good + "+20"));
            defTip.Add((Loc.Tr("Weapon indoors penalty:"), None));
            defTip.Add((Loc.Tr("Your defense skill is your chance of blocking enemy attacks."), ""));
            lines.Add(new HudStatLine("Defense:", defValue, defValue - defChange) { Tip = defTip });
        }

        var strengthTip = new List<(string, string)>();
        if (cold.Extended is { } x) strengthTip.Add((Loc.Tr("Strength XP rate:"), Loc.Tr("{1,num}%", (int)(x.StrengthXp * 100))));
        // FUN_140892120 writes the carry weight with a '%' (the inventory's own line says kg).
        if (constants is not null)
            strengthTip.Add((Loc.Tr("Max carry weight:"), Loc.Tr("{1,num}%", (int)(Eff(StatsEnumerated.Strength, StatUse.Hunger | StatUse.Limbs | StatUse.DamageState | StatUse.LimbItems) + constants.EncumbranceBase))));
        if (hc is { } c) strengthTip.Add((Loc.Tr("Blunt weapon damage:"), Int(c.Blunt)));
        strengthTip.AddRange(Breakdown(med, stats, factors, StatsEnumerated.Strength, "Strength"));
        lines.Add(Line("Strength:", StatsEnumerated.Strength) with { Tip = strengthTip });

        var dexterityTip = new List<(string, string)>();
        if (hc is { } d)
            dexterityTip.AddRange([(Loc.Tr("Attack speed:"), Loc.Tr("{1,num=fix,p=2}x", d.AttackRate)), (Loc.Tr("Block speed:"), Loc.Tr("{1,num=fix,p=2}x", d.BlockRate)),
                (Loc.Tr("Weapon cutting damage:"), Loc.Tr("{1,num=fix,p=2}", d.Cut))]);
        dexterityTip.AddRange(Breakdown(med, stats, factors, StatsEnumerated.Dexterity, "Dexterity"));
        lines.Add(Line("Dexterity:", StatsEnumerated.Dexterity, fighter?.DexterityMult ?? 1) with { Tip = dexterityTip });

        var toughnessTip = new List<(string, string)> { (Loc.Tr("Toughness XP rate:"), Loc.Tr("{1,num}x", (int)XpService.ToughnessFactor(med))) };
        float tough = stats.Toughness * stats.Scale;
        if (constants is not null)
            toughnessTip.AddRange([(Loc.Tr("Health KO point:"), Int(MedicalState.KoPoint(constants, stats.Toughness))),
                (Loc.Tr("Wound degeneration speed:"), Loc.Tr("{1,num=fix,p=2}x", MedicalState.DegenerationMult(constants, tough)))]);
        if (hc is { } r) toughnessTip.Add((Loc.Tr("Damage resistance:"), Loc.Tr("{1,num}%", (int)(r.Resistance * 100))));
        lines.Add(new HudStatLine("Toughness:", stats.Toughness, stats.Toughness) { Tip = toughnessTip });

        if (!ranged)
        {
            // FUN_140885190: the dodge skill less the encumbrance and leg terms plus the gear's; the +20 of defensive mode needs a combat state (+0x1f0 == 5) not modelled.
            float dodge = Stored(StatsEnumerated.Dodge);
            float encumbrance = -(1 - Math.Clamp(e, 0.4f, 1)) * dodge, legs = -(1 - Math.Clamp(Speed.LegFactor(med), 0.5f, 1)) * dodge;
            float gear = dodge * (fighter?.DodgeMult ?? 1) - dodge, total = dodge + encumbrance + legs + gear;
            int value = (int)total, change = Round(total - dodge);
            var dodgeTip = new List<(string, string)> { (Loc.Tr("Dodge skill:"), Signed(Round(dodge))), (Loc.Tr("Encumbrance penalty:"), Signed(Round(encumbrance))),
                (Loc.Tr("Leg injury penalty:"), Signed(Round(legs))), (Loc.Tr("Equipment penalty:"), Signed(Round(gear))) };
            if (defensive) dodgeTip.Add((Loc.Tr("Defensive mode:"), Signed(20)));
            dodgeTip.Add((Loc.Tr("-End total:"), Signed(Round(total))));
            lines.Add(new HudStatLine("Dodge:", value, value - change) { Tip = dodgeTip });
        }
        return lines;
    }

    /// <summary>
    /// A stat's breakdown (<c>FUN_140890b00</c>): the stored skill, a row per term of the stat multiplier that costs anything (injuries, hunger, weather, darkness, the fitted limbs, the
    /// gear), for strength the missing-arm penalty instead of the gear, then the effective value.
    /// </summary>
    static List<(string, string)> Breakdown(MedicalState med, CharacterStats stats, StatFactors factors, StatsEnumerated stat, string name)
    {
        float stored = stats[stat] * stats.Scale;
        int Lost(StatUse use) => (int)((1 - med.StatMultiplier(stat, use, factors)) * 100);
        var rows = new List<(string, string)> { (Loc.Tr("{1} skill:", Loc.Tr(name)), Int(stored)) };
        foreach (var (use, label) in new[] { (StatUse.Limbs, "Injuries:"), (StatUse.Hunger, "Hunger:"), (StatUse.Stun, "Weather:"), (StatUse.Light, "Working in darkness:") })
            if (Lost(use) is > 0 and var lost) rows.Add((Loc.Tr(label), Penalty(lost)));
        int limbs = Lost(StatUse.DamageState);
        if (limbs > 0) rows.Add((Loc.Tr("Robotics:"), Penalty(limbs)));
        else if (limbs < 0) rows.Add((Loc.Tr(med.HasProsthetics ? "Robotics:" : "Limbs disabled:"), Good + "+" + Loc.Tr("{1,num}%", -limbs)));
        if (stat == StatsEnumerated.Strength)
        {
            if (med.Aggregates().EitherArmDisabled) rows.Add((Loc.Tr("Missing arm penalty:"), Penalty((int)((1 - 0.66f) * 100))));
        }
        else if (stat != StatsEnumerated.Perception && Lost(StatUse.LimbItems) is not 0 and var gear)
            rows.Add((Loc.Tr("Armour/equipment:"), Signed(-gear) + "%"));
        rows.Add((Loc.Tr("Total:"), Int(stored * med.StatMultiplier(stat, StatUse.All, factors))));
        return rows;
    }

    /// <summary>The extended lines' tooltips (<c>FUN_140895350</c>); the run speed's rows are the host's (<see cref="ExtendedTips"/>).</summary>
    public static ExtendedTips Extended(CharacterCold cold, MedicalState med, RaceData race, CharacterStats stats, ExtendedStats x, GameConstants? constants)
    {
        float e = cold.Fighter?.EncumbranceFactor ?? 1;
        bool carrying = !cold.Carrying.IsNone;
        var athletics = new List<(string, string)> { (Loc.Tr("Encumbrance penalty:"), Penalty((int)((1 - e) * 100))) };
        if (x.RunSpeed > x.RunValue * 0.8f) athletics.Add((Loc.Tr("Full speed bonus:"), Good + "+50%"));
        athletics.Add((Loc.Tr("This is the rate at which your athletics skill is increasing while running."), ""));

        // The weapon-held branch ("Weapon weight:", CharStats +0x184) is not modelled: always the carrying rows.
        int rate = (int)(x.StrengthXp * 100);
        var strength = new List<(string, string)> { (Loc.Tr("Encumbrance bonus:"), Loc.Tr("{1,num}%", carrying ? (int)(rate * 0.5f) : rate)) };
        if (carrying) strength.Add((Loc.Tr("Carrying somebody:"), Good + "200%"));
        strength.Add((Loc.Tr("This is the rate at which your strength is increasing."), ""));

        var arms = med.Aggregates();
        var toughness = new List<(string, string)>();
        if (arms.LeftArmDisabled) toughness.Add((Loc.Tr("Crippled left arm:"), Good + "+1x"));
        if (arms.RightArmDisabled) toughness.Add((Loc.Tr("Crippled right arm:"), Good + "+2x"));
        if (med.BloodKo) toughness.Add((Loc.Tr("Blood loss trauma:"), Good + "+2x"));
        if (WorstFraction(med) < 0) toughness.Add((Loc.Tr("Critical wounds:"), Good + "+2x"));
        toughness.Add((Loc.Tr("This is the rate at which your toughness increases when you get hurt."), ""));

        var hunger = new List<(string, string)> { (Loc.Tr("Racial hunger rate:"), Mult(race.HungerRate)) };
        if (constants is not null)
        {
            hunger.Add((Loc.Tr("Encumbrance effect:"), Mult((1 - e) * constants.EncumbranceHungerRate + 1)));
            if (x.Resting) hunger.Add((Loc.Tr("Resting/KO state:"), Mult(constants.BedHungerRate)));
        }

        // FUN_140895350's sneak row: t = max((s - 40) / 60, 0) of the stealth term s, which is in 0..1, then lerp(12, S, t), so the cap reads 12.
        float? sneak = (cold.Orders & CharacterOrders.Sneak) != 0 ? 12 + (x.RunSpeed - 12) * MathF.Max((Orders.Stealth(cold) - 40) / 60, 0) : null;
        return new ExtendedTips(athletics, strength, toughness, hunger, stats.Athletics, Speed.LegFactor(med), med.LegItemFactor(StatsEnumerated.Athletics), sneak);
    }

    /// <summary>Medical <c>+0xa4 worstDamage</c>: the lowest part fraction, 1 without parts.</summary>
    static float WorstFraction(MedicalState med)
    {
        float worst = 1;
        foreach (var p in med.PartSpan) worst = MathF.Min(worst, p.Fraction);
        return worst;
    }
}
