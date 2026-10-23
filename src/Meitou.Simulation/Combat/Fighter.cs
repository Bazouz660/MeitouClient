using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Gameplay.Combat;
using Meitou.Simulation.Bodies;

namespace Meitou.Simulation.Combat;

/// <summary>
/// What a character fights with: the weapon in hand (null for fists) and the worn armour, with the sums of the gear's bonuses taken once
/// (docs/game/combat.md "Armour": the worn-gear totals of <c>FUN_140887030</c>). Cold state of <see cref="CharacterCold"/>; it changes only in the serial phases
/// (equipping, an inventory change), never in a parallel phase. The order of <see cref="Armour"/> is the order the pieces are stacked in.
/// </summary>
public sealed class Fighter
{
    /// <summary>
    /// The weapon in hand (docs/game/combat.md "Arms"): with both arms working the <see cref="BackWeapon"/>, else the hip one; with only the right arm the hip one; without
    /// the right arm none (fists). An animal's natural weapon always.
    /// </summary>
    public WeaponInstance? Weapon => IsAnimal ? hip : !rightArm ? null : leftArm ? BackWeapon ?? hip : hip;
    /// <summary>The first weapon in the <c>back</c> section, if any (the constructor's weapon is the <c>hip</c> one).</summary>
    public WeaponInstance? BackWeapon { get; init; }
    /// <summary>Whether <see cref="Weapon"/> is the <see cref="BackWeapon"/> (the renderer shows that one drawn).</summary>
    public bool WeaponIsBack => Weapon is { } w && w == BackWeapon;
    readonly WeaponInstance? hip;
    bool rightArm = true, leftArm = true;
    /// <summary>The crossbow carried in a weapon section (back or hip), if any: the ranged weapon (docs/simulation.md "Combat", ranged). The melee <see cref="Weapon"/> stays what it was.</summary>
    public CrossbowInstance? Crossbow { get; init; }
    /// <summary>The worn pieces, in the order they are stacked in.</summary>
    public ReadOnlySpan<ArmourPiece> Armour => armour;
    readonly ArmourPiece[] armour;
    /// <summary>A creature (an ANIMAL_CHARACTER): the target kind for the weapon multipliers, and the techniques of its own weapon category apply.</summary>
    public bool IsAnimal { get; init; }
    /// <summary>
    /// The creature kind its techniques are picked by (COMBAT_TECHNIQUE <c>animal</c>): its natural weapon's <c>skill category</c> (9 to 20), 0 for a person (docs/game/animals.md).
    /// </summary>
    public int AnimalCategory => IsAnimal && Weapon is { } w ? w.Data.SkillCategory : 0;
    /// <summary>
    /// The weapon category a technique's <c>animal</c> value is compared with (<see cref="TechniqueChooser.Fits"/>): the weapon's <c>skill category</c>, 5 (unarmed) for fists.
    /// The martial arts techniques have <c>animal</c> 5, the creature ones 9 to 20, the weapon ones 0 (docs/game/animals.md).
    /// </summary>
    public int TechniqueCategory => Weapon?.Data.SkillCategory ?? (int)WeaponCategory.Unarmed;
    /// <summary>Whether it has an attack: an animal without a natural weapon (a swamp turtle) has none; it can still be hit.</summary>
    public bool CanAttack => !IsAnimal || Weapon is not null;
    /// <summary>The guarding state (+20 melee defence in the block chance).</summary>
    public bool Guarding { get; set; }
    /// <summary>The encumbrance factor in [0, 1] (<see cref="Encumbrance.Factor"/>); 1 is unburdened.</summary>
    public float EncumbranceFactor { get; set; } = 1;

    /// <summary>Sum of the pieces' <c>combat def bonus</c> (the "float equipment bonus" of the block chance).</summary>
    public float DefenceBonus { get; }
    /// <summary>Sum of the pieces' <c>combat attk bonus</c> and, for fists, <c>unarmed bonus</c>.</summary>
    public float AttackBonus { get; }
    public float UnarmedBonus { get; }
    /// <summary>Product of the pieces' <c>damage output mult</c>.</summary>
    public float DamageOutput { get; }
    /// <summary>Product of the pieces' <c>dexterity mult</c>; 1.1 with no armour and no backpack (<see cref="Naked"/>).</summary>
    public float DexterityMult => Naked ? NakedBonus : dexterity;
    /// <summary>Product of the pieces' <c>dodge mult</c> (the gear's dodge value for <see cref="DefenceFormulas.DodgeSkill"/>, x 100); 1.1 when <see cref="Naked"/>.</summary>
    public float DodgeMult => Naked ? NakedBonus : dodgeMult;
    readonly float dexterity, dodgeMult, assassin, ranged;
    /// <summary>The dodge and dexterity factor of a <see cref="Naked"/> character (Verified literal).</summary>
    public const float NakedBonus = 1.1f;
    /// <summary>
    /// The gear factors of the stat multiplier (<see cref="StatFactors"/>, <c>CharStats</c> +0x40 to +0x50 as <c>FUN_140887030</c> writes them, <b>Verified</b>): the products of
    /// <c>asassination mult</c>, <c>dexterity mult</c>, <c>damage output mult</c> and <c>ranged skill mult</c>. No dust storm here (its factor is 1).
    /// </summary>
    public StatFactors Factors => new(assassin, DexterityMult, DamageOutput, ranged, 1);
    /// <summary>
    /// Wearing no armour and no backpack: the gear refresh then puts 1.1 for dodge and dexterity and 1.2 for stealth (<c>FUN_140887030</c>, <b>Verified</b> literals). Meitou's
    /// stealth does not read the gear's <c>stealth mult</c> yet, so only the first two apply. Never for an animal (engine choice: whether the refresh runs for animals is <b>Unknown</b>).
    /// </summary>
    public bool Naked => !IsAnimal && armour.Length == 0 && !backpack;
    readonly bool backpack;
    /// <summary>Product of the pieces' <c>combat speed mult</c>: scales the speed attack animations play at.</summary>
    public float CombatSpeed { get; }

    /// <param name="backpack">Whether a backpack is worn (only for <see cref="Naked"/>).</param>
    public Fighter(WeaponInstance? weapon, IEnumerable<ArmourPiece>? armour = null, bool backpack = false)
    {
        hip = weapon;
        this.armour = armour is null ? [] : [.. armour];
        this.backpack = backpack;
        float def = 0, atk = 0, unarmed = 0, output = 1, dex = 1, dodge = 1, speed = 1;
        assassin = ranged = 1;
        foreach (var p in this.armour)
        {
            def += p.Data.CombatDefenceBonus;
            atk += p.Data.CombatAttackBonus;
            unarmed += p.Data.UnarmedBonus;
            output *= p.Data.DamageOutputMult;
            dex *= p.Data.DexterityMult;
            dodge *= p.Data.DodgeMult;
            speed *= p.Data.CombatSpeedMult;
            assassin *= p.Data.AssassinationMult;
            ranged *= p.Data.RangedSkillMult;
        }
        DefenceBonus = def;
        AttackBonus = atk;
        UnarmedBonus = unarmed;
        DamageOutput = output;
        dexterity = dex;
        dodgeMult = dodge;
        CombatSpeed = speed;
    }

    /// <summary>Sets which arms work (<see cref="Weapon"/>); true when that changes the weapon in hand. Only in the owner's own Act or a serial phase.</summary>
    public bool SetArms(bool right, bool left)
    {
        var before = Weapon;
        (rightArm, leftArm) = (right, left);
        return Weapon != before;
    }

    /// <summary>Fights with fists: no weapon, or a weapon record of the unarmed category (a creature attack).</summary>
    public bool Unarmed => Weapon is null || Weapon.Data.IsUnarmed;

    /// <summary>The skill that rates the fighter's blows: its weapon's, martial arts for fists.</summary>
    public StatsEnumerated WeaponSkill => Unarmed ? StatsEnumerated.MartialArts : Weapon!.Skill;

    /// <summary>The weapon type techniques are matched with (the unarmed bit for fists).</summary>
    public Meitou.Data.Gameplay.WeaponKinds Kind => Weapon?.Kind ?? Meitou.Data.Gameplay.WeaponKinds.Unarmed;

    /// <summary>A weapon that can parry (fists cannot: those fighters dodge).</summary>
    public bool CanBlock => Weapon is { } w && !Unarmed && w.CanBlock;
}
