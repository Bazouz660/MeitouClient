using Meitou.Data.Gameplay;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Dialogue;

namespace Meitou.Simulation;

/// <summary>
/// One member per predicate of the original, named after it and citing the original function, so that a fact the simulation and the UI both need has one definition. Merge by
/// original function, not by English word: "down" is three different originals (<see cref="Down"/>, <see cref="TargetDown"/>, and the capture finders' KO / crawling sense tags,
/// <c>CaptiveSystem</c>). Pure reads; callable from any phase that may read the character. docs/simulation.md "Shared predicates".
/// </summary>
public static class CharacterFacts
{
    /// <summary>
    /// <c>Character</c> slot 6 <c>isDown</c> (vfunc 0x30, <c>FUN_1405c9e10</c>, <b>Verified</b>): dead or out cold, playing dead (which is unconscious to everyone), ragdolled or carried.
    /// A crawler is not down. A ragdoll is only ever whole for an incapacitated character, so <see cref="Ragdolls.RagdollSystem.IsRagdoll"/> adds nothing the first test lacks, and is kept for the original's reading.
    /// </summary>
    public static bool Down(CharacterCold cold) =>
        cold.Medical is { Incapacitated: true } or { PlayingDead: true } || !cold.CarriedBy.IsNone || Ragdolls.RagdollSystem.IsRagdoll(cold);

    /// <summary>
    /// <c>isTargetDown</c> (state 7, <c>FUN_14099a5a0</c>, <b>Verified</b>): gone, carried, unconscious or dead, in a bed or cage, or crawling or playing dead (posture 2 or more; staying low is up).
    /// </summary>
    public static bool TargetDown(CharacterCold? c) =>
        c is null || !c.CarriedBy.IsNone || c.Medical is { Incapacitated: true } or { Crawling: true } or { PlayingDead: true } || c.InSomething != Locks.HeldState.None;

    /// <summary>
    /// Set Free's precondition 125 TARGET_IS_CONSCIOUS (<c>FUN_14099a480</c>, <b>Verified</b>; no row provides it): not dead, unconscious or carried. A ragdolled prisoner also fails in the original
    /// (not modelled apart from the knock-out).
    /// </summary>
    public static bool Conscious(CharacterCold c) => c.Medical is not { Incapacitated: true } && c.CarriedBy.IsNone;

    /// <summary>Awake: not dead or unconscious (a body that can be fought or traded with, whether carried or not).</summary>
    public static bool Standing(CharacterCold c) => c.Medical is not { Incapacitated: true };

    /// <summary>Plans with the animal repertoire (<see cref="Repertoire.Animal"/>, <see cref="SquadRegistry.IsAnimal"/>).</summary>
    public static bool Animal(CharacterCold c) => SquadRegistry.IsAnimal(c);

    /// <summary>
    /// The faction is hostile to the player's (<c>Relations.IsHostile</c>; another faction counts as hostile when the world has no relation table or the faction is unknown), or the character
    /// <paramref name="fightsPlayer"/>: a neutral squad robbing the player's characters is an enemy. Engine reading of the original's enemy test (docs/game/ui-input.md "Context menu verbs").
    /// </summary>
    public static bool HostileToPlayer(FactionRelations? relations, int player, int faction, bool fightsPlayer) =>
        fightsPlayer || relations is null || player < 0 || !relations.Contains(faction) || relations.IsHostile(player, faction);

    /// <summary>The "getting up" byte (<c>Character</c> <c>+0x278</c>): set while the character rises from a knock-down (<see cref="CharacterCold.GettingUpUntil"/>).</summary>
    public static bool GettingUp(CharacterCold cold, long tick) => cold.GettingUpUntil > tick;

    /// <summary><c>Character::isInCombatMode(melee, ranged)</c> (<c>FUN_140436610</c>, <see cref="CombatSystem.IsInCombatMode"/>).</summary>
    internal static bool InCombatMode(CombatSystem combat, CharacterTable table, CharacterId id, CharacterCold cold, bool melee, bool ranged) =>
        combat.IsInCombatMode(table, id, cold, melee, ranged);

    /// <summary>Slot 126, "A regards B as friendly" (<c>FUN_1407923d0</c>, <see cref="DialogueSystem.RegardsFriendly(World, int, int)"/>), by slot.</summary>
    internal static bool RegardsFriendly(DialogueSystem dialogue, World world, int a, int b) => dialogue.RegardsFriendly(world, a, b);

    /// <summary>
    /// Slot 125, "A regards B as an enemy" (<c>FUN_14079c950</c>), faction part: hostile and not coexisting in either direction (<see cref="AggroSystem.IsEnemy"/>), another faction
    /// when the world has no relation table. Engine reading: the original's squad memory tests are not read.
    /// </summary>
    public static bool RegardsEnemy(FactionRelations? relations, int mine, int theirs) =>
        relations is not null ? AggroSystem.IsEnemy(relations, mine, theirs) : mine != theirs;

    /// <summary>A fight is on in the squad: a member in combat or with an attack order (engine reading of the squad's "in combat" test).</summary>
    public static bool SquadFighting(CharacterTable table, Squad squad)
    {
        foreach (var m in squad.Members)
            if (table.TryResolveNext(m, out int s) && (table.Cold(s)!.InCombat || table.Next[s].Task == (byte)CharacterTask.Attack)) return true;
        return false;
    }

    /// <summary>The planner's <c>iAmBeingMeleeAttacked immediate</c> (<c>+0x2b0</c>, <c>FUN_1404378f0</c>, <see cref="CombatSystem.ImmediateThreat"/>).</summary>
    internal static bool ImmediateThreat(CombatSystem combat, World world, int self) => combat.ImmediateThreat(world, self);
}
