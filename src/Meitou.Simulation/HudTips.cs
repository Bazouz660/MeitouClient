using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Combat;

namespace Meitou.Simulation;

/// <summary>
/// The HUD stats panel's lines with their tooltips (<c>FUN_140892120</c>) and the extended lines' tooltips (<c>FUN_140895350</c>), docs/game/ui-screens.md section 4 "Stat tooltips".
/// Rows are (key, value) with the exe's inline colour tags: Bad <c>#59231a</c>, Good <c>#a8b774</c>, Main <c>#afa68b</c>, and the text default <c>#140806</c> for a signed 0.
/// "-" stands where the original shows "-": a row that does not apply (outdoors, no dust).
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

    /// <summary>
    /// The stats panel's lines (docs/game/ui-screens.md section 4), each with its tooltip. <paramref name="inBuilding"/>: in a building at all, on top included (the original's location
    /// type is not 0xb); <paramref name="inside"/>: in its indoor volume (<see cref="SightMap.Inside"/>), which the indoors modifiers of the values need. <paramref name="displayMode"/>:
    /// <c>getGUIStatsDisplayMode</c> (<see cref="CombatSystem.StatsDisplayMode"/>), 1 Martial Arts, 2 the crossbow lines, 0 Attack and Defense.
    /// </summary>
    public static List<HudStatLine> Lines(CharacterCold cold, MedicalState med, CharacterStats stats, HudCombat? hc, GameConstants? constants, bool inBuilding, bool inside, int displayMode)
    {
        var fighter = cold.Fighter;
        var factors = (fighter?.Factors ?? StatFactors.None) with { Stun = med.DustStatFactor };
        float e = fighter?.EncumbranceFactor ?? 1;
        float Stored(StatsEnumerated stat) => stats[stat] * stats.Scale;
        float Eff(StatsEnumerated stat, StatUse use = StatUse.All) => Stored(stat) * med.StatMultiplier(stat, use, factors);
        int dust = (int)(med.DustInEffect * 10);
        bool defensive = (cold.Orders & CharacterOrders.Block) != 0;
        HudStatLine Line(string label, StatsEnumerated stat, float factor = 1) =>
            new(label, (stats[stat] + Combat.CombatSystem.GearBonus(cold, stat)) * med.StatMultiplier(stat) * factor, stats[stat]);
        // An animal's age (slot 0x398) scales its fighting numbers (the stats multiplier, already in Stored); a person's is 1.
        var animal = cold.Animal;
        float age = animal is null ? 1 : stats.Scale;
        var weapon = fighter?.Weapon?.Stats;
        int indoorsMod = weapon?.IndoorsMod ?? 0;

        var lines = new List<HudStatLine>(7);
        if (animal is not null)
            lines.Add(new HudStatLine("Age:", age, age) { Text = Loc.Tr(AnimalData.AgeWord(age)), Tip = [(Loc.Tr("Stat Multiplier:"), Loc.Tr("{1,num=fix,p=2}x", age))] });
        bool ranged = displayMode == 2 && fighter?.Crossbow is not null;
        if (ranged)
        {
            // The gear's ranged factor as the breakdown's Total has it.
            lines.Add(Line("Crossbows:", StatsEnumerated.Crossbows, factors.Ranged) with { Tip = Breakdown(med, stats, factors, StatsEnumerated.Crossbows, "Crossbows") });
            lines.Add(Line("Precision:", StatsEnumerated.FriendlyFire, factors.Ranged) with { Tip = Breakdown(med, stats, factors, StatsEnumerated.FriendlyFire, "Careful Shooting") });
            lines.Add(Line("Perception:", StatsEnumerated.Perception) with { Tip = Breakdown(med, stats, factors, StatsEnumerated.Perception, "Perception",
                Combat.CombatSystem.GearBonus(cold, StatsEnumerated.Perception)) });
        }
        else if (displayMode == 1 && animal is null)
        {
            // FUN_140884e00 and FUN_140884680: the value lerp(e, 0.2, 1) x (bonus + skill), the change the bonus less 0.8 (1 - e) x skill. The bonus (FUN_140884580) takes the
            // weapon's indoors field like Attack's (0 for fists), while the row shows a flat +8 in any building.
            float ma = Eff(StatsEnumerated.MartialArts);
            int gear = (int)(fighter?.UnarmedBonus ?? 0), bonus = Indoors(gear) - dust, encumbrance = (int)-(0.8f * (1 - e) * ma);
            int value = (int)((0.2f + 0.8f * e) * (bonus + ma)), change = bonus + encumbrance;
            // The original labels the encumbrance row of this tooltip "Strength XP:".
            var tip = new List<(string, string)> { (Loc.Tr("Total equipment bonuses:"), Signed(gear)), (Loc.Tr("Indoors bonus:"), inBuilding ? Signed(8) : None),
                (Loc.Tr("Weather penalty:"), dust != 0 ? Signed(-dust) : None), (Loc.Tr("Encumbrance penalty:"), Signed(encumbrance)),
                (Loc.Tr("Strength XP:"), Loc.Tr("{1,num}%", (int)((1 - e) * 100))) };
            if (hc is { } fists) tip.Add((Loc.Tr("Damage:"), Int(fists.UnarmedDamage)));
            lines.Add(new HudStatLine("Martial Arts:", value, value - change) { Tip = tip });
        }
        else
        {
            // FUN_1408844c0 / FUN_140884e80: the equipment bonus (indoors with the weapon's indoors modifier) less the dust penalty, then the effective skill, times the age;
            // the change is the bonus. An animal always shows Attack, and no Defense.
            int gear = (int)((fighter?.AttackBonus ?? 0) + (weapon?.AttackMod ?? 0)), change = Indoors(gear) - dust, value = (int)(change * age + Eff(StatsEnumerated.MeleeAttack));
            lines.Add(new HudStatLine("Attack:", value, value - change)
            {
                Tip = [(Loc.Tr("Total equipment bonuses:"), Signed(gear)), (Loc.Tr("Weapon indoors penalty:"), inside ? Signed(indoorsMod) : None),
                    (Loc.Tr("Weather penalty:"), dust != 0 ? Signed(-dust) : None), (Loc.Tr("Base hit chance:"), Loc.Tr("{1,num}%", value))],
            });
            if (animal is null)
            {
                // FUN_140884710 / FUN_140884f50: the defence bonus (+ the indoors modifier inside), +20 in defensive mode, then the effective skill on top.
                int def = (int)((fighter?.DefenceBonus ?? 0) + (weapon?.DefenceMod ?? 0)), defChange = def + (inside ? indoorsMod : 0) + (defensive ? 20 : 0);
                int defValue = (int)(defChange + Eff(StatsEnumerated.MeleeDefence));
                var defTip = new List<(string, string)> { (Loc.Tr("Total equipment bonuses:"), Signed(def)) };
                if (defensive) defTip.Add((Loc.Tr("Defensive combat mode:"), Good + "+20"));
                defTip.Add((Loc.Tr("Weapon indoors penalty:"), inBuilding ? Signed(indoorsMod) : None));
                defTip.Add((Loc.Tr("Your defense skill is your chance of blocking enemy attacks."), ""));
                lines.Add(new HudStatLine("Defense:", defValue, defValue - defChange) { Tip = defTip });
            }
        }

        // The equipment bonus indoors: a positive bonus is dropped under an indoors penalty, then the modifier is added.
        int Indoors(int bonus) => !inside ? bonus : (bonus > 0 && indoorsMod < 0 ? 0 : bonus) + indoorsMod;

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
            // FUN_140885190: the dodge skill less the encumbrance and leg terms plus the gear's, +20 in defensive mode with fists (CharStats +0x1f0, the current weapon type, is 5).
            float dodge = Stored(StatsEnumerated.Dodge);
            float encumbrance = -(1 - Math.Clamp(e, 0.4f, 1)) * dodge, legs = -(1 - Math.Clamp(Speed.LegFactor(med), 0.5f, 1)) * dodge;
            float gear = dodge * (fighter?.DodgeMult ?? 1) - dodge, total = dodge + encumbrance + legs + gear + (defensive && (fighter is null || fighter.Unarmed) ? 20 : 0);
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
    /// gear), for strength the missing-arm penalty instead of the gear, for perception the armour's <paramref name="bonus"/> (added before the multiplier), then the effective value.
    /// </summary>
    static List<(string, string)> Breakdown(MedicalState med, CharacterStats stats, StatFactors factors, StatsEnumerated stat, string name, int bonus = 0)
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
        else if (stat == StatsEnumerated.Perception)
        {
            // FUN_140889320(bonus): +n Good or n Bad, no unit.
            if (bonus != 0) rows.Add((Loc.Tr("Armour/equipment:"), Signed(bonus)));
        }
        else if (Lost(StatUse.LimbItems) is not 0 and var gear)
            rows.Add((Loc.Tr("Armour/equipment:"), Signed(-gear) + "%"));
        rows.Add((Loc.Tr("Total:"), Int((stored + bonus) * med.StatMultiplier(stat, StatUse.All, factors))));
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

        // In combat mode the rate is the drawn weapon's weight factor, or 1 - e with nothing drawn (current weapon type 5); else the carrying rate, whose bonus row is halved while carrying.
        int rate = (int)(x.StrengthXp * 100);
        var strength = new List<(string, string)>();
        if (x.Fighting) strength.Add((Loc.Tr(x.Fists ? "Encumbrance:" : "Weapon weight:"), Loc.Tr("{1,num}%", rate)));
        else
        {
            strength.Add((Loc.Tr("Encumbrance bonus:"), Loc.Tr("{1,num}%", carrying ? (int)(rate * 0.5f) : rate)));
            if (carrying) strength.Add((Loc.Tr("Carrying somebody:"), Good + "200%"));
        }
        strength.Add((Loc.Tr("This is the rate at which your strength is increasing."), ""));

        var arms = med.Aggregates();
        var toughness = new List<(string, string)>();
        if (arms.LeftArmDisabled) toughness.Add((Loc.Tr("Crippled left arm:"), Good + "+1x"));
        if (arms.RightArmDisabled) toughness.Add((Loc.Tr("Crippled right arm:"), Good + "+2x"));
        if (med.BloodKo) toughness.Add((Loc.Tr("Blood loss trauma:"), Good + "+2x"));
        if (WorstFraction(med) < 0) toughness.Add((Loc.Tr("Critical wounds:"), Good + "+2x"));
        toughness.Add((Loc.Tr("This is the rate at which your toughness increases when you get hurt."), ""));

        var hunger = new List<(string, string)> { (Loc.Tr("Racial hunger rate:"), Mult(race.HungerRate)) };
        // An animal's age row (FUN_14088a2b0): when its age (slot 0x398) is not 1, the size factor (slot 0x390, lerp(age, scale min, scale max)).
        if (cold.Animal is { } animal && stats.Scale != 1) hunger.Add((Loc.Tr("Age:"), Mult(animal.Scale(stats.Scale))));
        if (constants is not null)
        {
            hunger.Add((Loc.Tr("Encumbrance effect:"), Mult((1 - e) * constants.EncumbranceHungerRate + 1)));
            if (x.Resting) hunger.Add((Loc.Tr("Resting/KO state:"), Mult(constants.BedHungerRate)));
        }
        // FUN_14088a2b0: with the AI idle flag clear (Meitou does not model it, so no Idle row), the used functionality's hunger rate (CharStats +0x178) when it is not 1.
        if (x.WorkHungerRate is { } work && work != 1) hunger.Add((Loc.Tr("Current work:"), Mult(work)));

        // FUN_140895350's sneak row: t = max((s - 40) / 60, 0) of the stealth term s, which is in 0..1, then lerp(12, S, t), so the cap reads 12.
        float? sneak = (cold.Orders & CharacterOrders.Sneak) != 0 ? 12 + (x.RunSpeed - 12) * MathF.Max((Orders.Stealth(cold) - 40) / 60, 0) : null;
        return new ExtendedTips(athletics, strength, toughness, hunger, stats.Athletics, Speed.LegFactor(med), med.LegItemFactor(StatsEnumerated.Athletics), sneak,
            cold.Fighter?.AthleticsFactor ?? 1);
    }

    /// <summary>Medical <c>+0xa4 worstDamage</c>: the lowest part fraction, 1 without parts.</summary>
    static float WorstFraction(MedicalState med)
    {
        float worst = 1;
        foreach (var p in med.PartSpan) worst = MathF.Min(worst, p.Fraction);
        return worst;
    }
}
