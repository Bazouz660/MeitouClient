using System.Numerics;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Combat;

namespace Meitou.Simulation;

public enum FloaterKind : byte
{
    Damage,
    Heal,
    /// <summary>A line of text (<see cref="Floater.Part"/>), white: "Ate {1}" over a character that ate (<c>FUN_1405d0780</c>).</summary>
    Text,
}

/// <summary>
/// A number to float over a character (docs/game/ui-screens.md "Damage floaters"): the damage a blow did, with the armour's share, or the health a player character got back.
/// Display only: the world queues these for the host (<see cref="World.Floaters"/>) and nothing in the simulation reads them.
/// </summary>
/// <param name="Part">The name of the part hit (empty for a heal).</param>
/// <param name="Damage">What the medical system took, after armour, toughness and the difficulty multiplier.</param>
/// <param name="Absorbed">What the armour alone stopped.</param>
/// <param name="Healed">The flesh restored (<see cref="FloaterKind.Heal"/>).</param>
public readonly record struct Floater(FloaterKind Kind, CharacterId Target, Vector3 Position, bool TargetIsPlayer, bool SourceIsPlayer, string Part, HitDamage Damage, ArmourAbsorbed Absorbed, float Healed)
{
    public static Floater Hit(CharacterId target, Vector3 position, bool targetIsPlayer, bool sourceIsPlayer, string part, in HitDamage damage, in ArmourAbsorbed absorbed) =>
        new(FloaterKind.Damage, target, position, targetIsPlayer, sourceIsPlayer, part, damage, absorbed, 0);

    public static Floater Heal(CharacterId target, Vector3 position, float healed) =>
        new(FloaterKind.Heal, target, position, true, false, "", default, default, healed);

    public static Floater Text(CharacterId target, Vector3 position, string text) =>
        new(FloaterKind.Text, target, position, false, false, text, default, default, 0);
}

public sealed partial class World
{
    /// <summary>Seconds of healing a player character collects into one floater.</summary>
    public const float HealFloaterSeconds = 1.5f;

    volatile System.Collections.Concurrent.ConcurrentQueue<Floater>? floaters;

    /// <summary>
    /// Where hits and heals queue their floaters for the host to draw; null (the default) records none. The host sets it while it shows floaters and empties it every frame.
    /// </summary>
    public System.Collections.Concurrent.ConcurrentQueue<Floater>? Floaters { get => floaters; set => floaters = value; }

    /// <summary>Queues the heal floater of a player character once <see cref="HealFloaterSeconds"/> of healing has gathered and it comes to a whole point or more.</summary>
    void PublishHeal(System.Collections.Concurrent.ConcurrentQueue<Floater> queue, int slot, CharacterCold cold, MedicalState medical, Vector3 position)
    {
        float healed = medical.Healed;
        if (healed < cold.HealShown) cold.HealShown = healed;   // a new body (a load, a new game)
        if (Tick - cold.HealTick < (long)MathF.Ceiling(HealFloaterSeconds / TickSeconds)) return;
        float gathered = healed - cold.HealShown;
        if (gathered < 1) return;
        cold.HealShown = healed;
        cold.HealTick = Tick;
        queue.Enqueue(Floater.Heal(Characters.IdOf(slot), position, gathered));
    }
}
