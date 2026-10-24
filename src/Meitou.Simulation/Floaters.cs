using System.Numerics;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Combat;

namespace Meitou.Simulation;

public enum FloaterKind : byte
{
    Damage,
    /// <summary>A line of text (<see cref="Floater.Part"/>), white: "Ate {1}" over a character that ate (<c>FUN_1405d0780</c>).</summary>
    Text,
}

/// <summary>
/// A number to float over a character (docs/game/ui-screens.md "Damage floaters"): the damage a blow did, with the armour's share.
/// Display only: the world queues these for the host (<see cref="World.Floaters"/>) and nothing in the simulation reads them.
/// </summary>
/// <param name="Part">The name of the part hit, or the text of a <see cref="FloaterKind.Text"/>.</param>
/// <param name="Damage">What the medical system took, after armour, toughness and the difficulty multiplier.</param>
/// <param name="Absorbed">What the armour alone stopped.</param>
public readonly record struct Floater(FloaterKind Kind, CharacterId Target, Vector3 Position, bool TargetIsPlayer, bool SourceIsPlayer, string Part, HitDamage Damage, ArmourAbsorbed Absorbed)
{
    public static Floater Hit(CharacterId target, Vector3 position, bool targetIsPlayer, bool sourceIsPlayer, string part, in HitDamage damage, in ArmourAbsorbed absorbed) =>
        new(FloaterKind.Damage, target, position, targetIsPlayer, sourceIsPlayer, part, damage, absorbed);

    public static Floater Text(CharacterId target, Vector3 position, string text) =>
        new(FloaterKind.Text, target, position, false, false, text, default, default);
}

public sealed partial class World
{
    volatile System.Collections.Concurrent.ConcurrentQueue<Floater>? floaters;

    /// <summary>
    /// Where hits and texts queue their floaters for the host to draw; null (the default) records none. The host sets it while it shows floaters and empties it every frame.
    /// </summary>
    public System.Collections.Concurrent.ConcurrentQueue<Floater>? Floaters { get => floaters; set => floaters = value; }
}
