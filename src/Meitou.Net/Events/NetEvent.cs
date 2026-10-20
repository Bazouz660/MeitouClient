using Meitou.Simulation;
using Meitou.Simulation.Carrying;

namespace Meitou.Net.Events;

/// <summary>
/// One display-only happening the host tells clients about in an <see cref="EventBatch"/> (docs/net.md, "Events"). Kinds 1-3 are spatial
/// (<see cref="EventFilter.IsSpatial"/>); the host modules choose who receives the rest.
/// </summary>
public abstract record NetEvent;

/// <summary>Kind 1, spatial: a combat sound the client plays.</summary>
public sealed record SoundNet(SoundEvent Sound) : NetEvent;

/// <summary>Kind 2, spatial: blood on the ground.</summary>
public sealed record BloodNet(BloodMark Mark) : NetEvent;

/// <summary>Kind 3, spatial: a damage or heal number over a character.</summary>
public sealed record FloaterNet(Floater Floater) : NetEvent;

/// <summary>Kind 4: a line for the message log (a <see cref="PlayerMessage"/> the host chose to send this player).</summary>
public sealed record MessageNet(string Text) : NetEvent;

/// <summary>Kind 5: a sneak attack's knock-out roll for the stealth log. (Kind 6 is reserved: it was the prospect report, now a snapshot field.)</summary>
public sealed record KnockoutNet(StealthKoEntry Entry) : NetEvent;

/// <summary>Kind 7: a chat line from player <paramref name="From"/>, whose name the host stamps on it.</summary>
public sealed record ChatNet(int From, string Name, string Text) : NetEvent;

/// <summary>What a <see cref="NoticeNet"/> announces.</summary>
public enum NoticeKind : byte { Joined, Left, TimedOut, Kicked, Banned }

/// <summary>Kind 8: a player joined, left, timed out, was kicked or banned; <c>Detail</c> is the reason text (may be empty).</summary>
public sealed record NoticeNet(NoticeKind Kind, int Player, string Name, string Detail) : NetEvent;

/// <summary>Everything one client is told in one host frame; at most <see cref="EventCodec.MaxEvents"/> events. <c>Epoch</c> drops batches of an old world.</summary>
public sealed record EventBatch(ushort Epoch, long Tick, IReadOnlyList<NetEvent> Events);
