using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation.Bodies;

namespace Meitou.Simulation;

/// <summary>The game's F10 debug menu (Meitou only, docs/game/ui-input.md "Debug menu"): cheats sent as commands, so they apply between ticks like any input.</summary>
public abstract record DebugCommand : SimCommand;

/// <summary>Adds <see cref="Delta"/> cats to the player's money (negative takes it away, never below 0).</summary>
public sealed record DebugMoney(long Delta) : DebugCommand;

/// <summary>Puts the characters on the ground at <see cref="Target"/> (side by side, 10 units apart) and drops their orders.</summary>
public sealed record DebugTeleport(IReadOnlyList<CharacterId> Characters, Vector3 Target) : DebugCommand;

/// <summary>Heals the characters fully (<see cref="MedicalState.Heal"/>).</summary>
public sealed record DebugHeal(IReadOnlyList<CharacterId> Characters) : DebugCommand;

/// <summary>Sets every stat of the characters to 100.</summary>
public sealed record DebugMaxSkills(IReadOnlyList<CharacterId> Characters) : DebugCommand;

/// <summary>One cut of <see cref="Cut"/> damage to each character, on a part chosen as a blow's is.</summary>
public sealed record DebugDamage(IReadOnlyList<CharacterId> Characters, float Cut) : DebugCommand;

/// <summary>Puts <see cref="Quantity"/> of item record <see cref="Record"/> into the character's inventory.</summary>
public sealed record DebugGiveItem(CharacterId Character, string Record, int Quantity) : DebugCommand
{
    /// <summary>The record types an item can be made from.</summary>
    public static readonly FcsRecordType[] ItemTypes =
        [FcsRecordType.WEAPON, FcsRecordType.ARMOUR, FcsRecordType.ITEM, FcsRecordType.CONTAINER, FcsRecordType.BLUEPRINT, FcsRecordType.MAP_ITEM, FcsRecordType.CROSSBOW, FcsRecordType.LIMB_REPLACEMENT];
}

/// <summary>Spawns a character of record <see cref="Record"/> at <see cref="At"/>, alone in a squad of the first faction hostile to the player (<see cref="PopulationSystem"/> applies it).</summary>
public sealed record DebugSpawn(string Record, Vector2 At) : DebugCommand;

static class DebugCheats
{
    public static void Apply(World world, PopulationData? data, DebugCommand command)
    {
        var table = world.Characters;
        switch (command)
        {
            case DebugMoney money:
                world.Player.Money = Math.Max(0, world.Player.Money + money.Delta);
                break;
            case DebugTeleport teleport:
                int k = 0;
                foreach (var id in teleport.Characters)
                {
                    if (!table.TryResolveNext(id, out int slot)) continue;
                    float x = teleport.Target.X + 10 * k++, z = teleport.Target.Z;
                    MovementSystem.Stop(table, slot);
                    table.Next[slot].Position = new Vector3(x, world.Walkability.GroundHeight(x, z, teleport.Target.Y), z);
                    table.Next[slot].Velocity = Vector3.Zero;
                }
                break;
            case DebugHeal heal when data is not null:
                foreach (var id in heal.Characters)
                    if (Body(world, data, id) is ({ } medical, var ctx)) medical.Heal(ctx);
                break;
            case DebugMaxSkills max:
                foreach (var id in max.Characters)
                    if (table.TryResolveNext(id, out int skilled) && table.Cold(skilled)?.Stats is { } stats)
                        for (var s = StatsEnumerated.None + 1; s < StatsEnumerated.End; s++) stats[s] = 100;
                break;
            case DebugDamage damage when data is not null:
                // Combat and the body pass read KO and death from the medical state; what they do only on the hit's events is done here.
                var events = new List<MedicalEvent>();
                foreach (var id in damage.Characters)
                {
                    if (Body(world, data, id) is not ({ } medical, var ctx) || medical.ChoosePart(ctx) is not (var part and >= 0) || !table.TryResolveNext(id, out int slot)) continue;
                    events.Clear();
                    medical.ApplyHit(part, new HitDamage(damage.Cut, 0, 0), ctx, events);
                    foreach (var e in events)
                    {
                        if (e.Kind is MedicalEventKind.Died or MedicalEventKind.KnockedOut) { MovementSystem.Stop(table, slot); table.Next[slot].Velocity = Vector3.Zero; }
                        else if (e.Kind == MedicalEventKind.PartSevered) table.LimbsChanged.Enqueue((slot, medical.Parts[e.Part].Template.LimbSlot));
                    }
                }
                break;
            case DebugGiveItem give when data is not null:
                if (!table.TryResolveNext(give.Character, out int owner) || table.Cold(owner)?.Inventory is not { } inventory) break;
                if (data.Items.Create(give.Record, give.Quantity) is not { } item) { world.Post(give.Player, $"No item record {give.Record}."); break; }
                if (!inventory.Add(item)) world.Post(give.Player, $"{item.Quantity} x {item.Name} did not fit.");
                break;
        }
    }

    static (MedicalState, MedicalContext)? Body(World world, PopulationData data, CharacterId id)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(id, out int slot) || table.Cold(slot) is not { Medical: { } medical, Race: { } race } cold) return null;
        return (medical, new MedicalContext(data.Bodies.Constants, data.Bodies.Options, race)
        {
            Toughness = cold.Stats?.Toughness ?? 0,
            Strength = cold.Stats?.Strength ?? 50,
            HasStats = cold.Stats is not null,
            Seed = world.Seed,
            CharacterKey = Rng.Key(table.IdOf(slot)),
        });
    }
}
