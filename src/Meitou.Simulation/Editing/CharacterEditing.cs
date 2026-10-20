using Meitou.Data.Characters;
using Meitou.Data.Fcs;
using Meitou.Simulation.Saving;

namespace Meitou.Simulation.Editing;

/// <summary>Why the character editor is open (docs/characters.md "Character editor"): making the start's characters, or a plastic surgeon's work.</summary>
/// <summary>The editor's use: the new game's characters, a plastic surgeon, or a recruit (DA_JOIN_SQUAD_WITH_EDIT; free, race and gender kept as at a surgeon).</summary>
public enum EditKind : byte { Creation, Surgery, Recruit }

/// <summary>
/// The character editor's result for one character: its new look (a CHARACTER_APPEARANCE, normally a <see cref="CharacterDraft"/> of its current one) and name.
/// Validated when applied (<see cref="CharacterEditing.Apply"/>): one of the issuer's player characters; at creation only while the issuer's new game editor is open
/// (<see cref="PlayerSession.InCreation"/>), in surgery only beside the <see cref="Surgeon"/> and with the race and gender unchanged.
/// </summary>
public sealed record EditCharacter(CharacterId Character, FcsRecord Appearance, string Name, EditKind Kind) : SimCommand
{
    /// <summary>The plastic surgeon doing the work (surgery only).</summary>
    public CharacterId Surgeon { get; init; } = CharacterId.None;
    /// <summary>
    /// The surgery was paid already (a surgeon's dialogue line takes the money, DA_TAKE_MONEY, before it opens the editor). Trusted from player 0; a remote player's counts only with
    /// the matching <see cref="PlayerSession.Grant"/> the conversation gave.
    /// </summary>
    public bool Paid { get; init; }
}

/// <summary>Ends the issuer's new game character creation: the start's characters cannot be remade any more.</summary>
public sealed record FinishCreation : SimCommand;

/// <summary>Applies <see cref="EditCharacter"/> (the player system's inputs); every refusal is posted to the issuer.</summary>
public static class CharacterEditing
{
    /// <summary>The longest name the editor takes (the layout's <c>NameText</c> <c>MaxTextLength</c>).</summary>
    public const int MaxNameLength = 30;
    /// <summary>How near (world units) the surgeon must stand: the talk range of a dialogue (engine choice, <b>Unknown</b> in the original).</summary>
    public const float SurgeonReach = 60;

    /// <summary>What <paramref name="id"/>'s surgery costs: its record's dialogue, or its squad template's leader dialogue when it leads the squad; null: not a surgeon.</summary>
    internal static int? SurgeryCost(World world, Meitou.Data.Characters.SurgeonDialogues surgeons, CharacterId id, CharacterCold cold)
    {
        var squad = cold.SquadId >= 0 ? world.Squads.Find(cold.SquadId) : null;
        return surgeons.CostOf(cold.RecordId, squad?.TemplateId, squad is not null && squad.Leader == id);
    }

    public static void Apply(World world, PopulationData data, EditCharacter edit)
    {
        var table = world.Characters;
        int player = edit.Player;
        if (!table.TryResolveNext(edit.Character, out int slot) || table.Cold(slot) is not { IsPlayer: true } cold || world.OwnerOfSlot(slot) != player || cold.Appearance is not { Animal: null } look)
        {
            world.Post(player, "That character cannot be edited.");
            return;
        }
        var session = world.SessionOf(edit);
        var currentRace = look.Race;
        var proposedRace = edit.Appearance.References.TryGetValue("race", out var refs) && refs.Count > 0 ? data.Db.Find(refs[0].TargetStringId) : null;
        if (proposedRace is not { Type: FcsRecordType.RACE }) { world.Post(player, "The edited look has no race."); return; }
        if (edit.Kind == EditKind.Creation)
        {
            if (!session.InCreation) { world.Post(player, "Characters can only be made at the start of a new game."); return; }
            if (!CharacterDraft.PlayableRaces(data.Db, session.CreationRaces).Any(g => g.Races.Any(r => r.StringId == proposedRace.StringId)) && proposedRace.StringId != currentRace.StringId)
            {
                world.Post(player, $"{proposedRace.Name} is not a playable race.");
                return;
            }
        }
        else if (edit.Kind == EditKind.Recruit)
        {
            // A remote player's recruit edit needs the editor the recruiting conversation opened for them.
            if (player != 0 && !session.TakeGrant(EditKind.Recruit, edit.Character, CharacterId.None)) { world.Post(player, "That character cannot be edited."); return; }
            bool female = edit.Appearance.Bools.TryGetValue("sex female", out var f) && f;
            if (proposedRace.StringId != currentRace.StringId || female != look.Female) { world.Post(player, "A recruit's race and gender cannot be changed."); return; }
        }
        else
        {
            if (!table.TryResolveNext(edit.Surgeon, out int surgeon) || !table.IsAlive(edit.Surgeon) ||
                System.Numerics.Vector3.Distance(table.Next[surgeon].Position, table.Next[slot].Position) > SurgeonReach)
            {
                world.Post(player, "The surgeon is not here any more.");
                return;
            }
            // Paid counts from a remote player only with the surgeon's grant of this very edit.
            bool paid = edit.Paid && (player == 0 || session.TakeGrant(EditKind.Surgery, edit.Character, edit.Surgeon));
            int? known = paid ? 0 : SurgeryCost(world, data.Surgeons, edit.Surgeon, table.Cold(surgeon)!);
            if (known is not { } cost) { world.Post(player, "That is not a surgeon."); return; }
            // The dialogue's gate is DC_PLAYERMONEY > cost (strict), then DA_TAKE_MONEY takes it.
            if (cost > 0 && world.Player.Money <= cost) { world.Post(player, $"Surgery costs c.{cost}: not enough money."); return; }
            bool female = edit.Appearance.Bools.TryGetValue("sex female", out var f) && f;
            if (proposedRace.StringId != currentRace.StringId || female != look.Female)
            {
                world.Post(player, "A surgeon cannot change race or gender.");
                return;
            }
            world.Player.Money -= cost;
        }
        var record = data.Appearances?.Sanitize(edit.Appearance, proposedRace) ?? CharacterDraft.Copy(edit.Appearance);

        // A new race at creation: a body of that race (its stats and limbs), its walk and footprint.
        if (proposedRace.StringId != currentRace.StringId)
        {
            var (raceData, stats, medical) = data.Bodies.Create(proposedRace, data.Db.Find(cold.RecordId), world.Seed, Rng.Key(edit.Character));
            cold.Race = raceData;
            cold.Stats = stats;
            cold.Medical = medical;
            cold.FootprintRadius = raceData.PathfindFootprintRadius;
            ref var hot = ref table.Next[slot];
            hot.MaxSpeed = Bodies.Speed.Run(raceData, stats, medical, 1);
            hot.WalkSpeed = raceData.WalkSpeed;
        }

        // The look: the new record wearing what the character wears now.
        var worn = cold.Inventory is { } inventory ? SavedKit.FromInventory(inventory) : [];
        if (SavedKit.LoadoutOf(record, worn, data.Db, look.Loadout?.Seed ?? 0) is { } kit && data.Appearances?.Create(cold.RecordId, kit) is { } updated)
        {
            cold.Appearance = updated;
            cold.LookEdited = true;
        }
        else world.Post(player, "The edited look could not be made.");
        string name = edit.Name.Trim();
        if (name.Length > MaxNameLength) name = name[..MaxNameLength];
        if (name.Length > 0) cold.Name = name;
    }
}
