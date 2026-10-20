namespace Meitou.Simulation;

/// <summary>
/// One human's view and windows (docs/simulation.md "Multiplayer (host-authoritative co-op)"): the selection, the active squad, what the HUD inspects, the open
/// container, the door and container walks, the character creation and the camera focus. Player 0 is the host (or the single player, <see cref="PlayerState.Local"/>);
/// 1..N are remote players, stable per <see cref="Guid"/>. Only <see cref="Selection"/> and <see cref="Squad"/> are hashed (session 0 in <see cref="PlayerState"/>'s
/// old place, the others after it). Changed in the serial phases or on the host thread between ticks.
/// </summary>
public sealed class PlayerSession
{
    Production.ProspectReport? prospected;

    internal PlayerSession(int number) => Number = number;

    /// <summary>The player number (0: the host or the single player).</summary>
    public int Number { get; }
    /// <summary>The player's roster identity: set by the host when the player joins and by a save's roster. Not hashed.</summary>
    public Guid Guid { get; set; }
    /// <summary>The player's name (roster). Not hashed.</summary>
    public string Name { get; set; } = "";
    /// <summary>The selected characters, in the order they were selected; only characters this player owns (<see cref="World.OwnerOf"/>).</summary>
    public List<CharacterId> Selection { get; } = [];
    /// <summary>
    /// The active squad (the original's cached squad, <c>FUN_1407f5520</c>): the one the squad bar shows and recruits join, one of this player's
    /// (<see cref="SquadManagement.PlayerSquads(World, int)"/>). The start's squad at first; the squad tabs, Tab (<see cref="ActiveSquadCommand"/>) and selecting a character of
    /// another squad change it.
    /// </summary>
    public int Squad { get; set; } = -1;
    /// <summary>The new game's character editor is open: the start's characters may still be remade, race and gender too (<see cref="Editing.EditCharacter"/>). Not saved.</summary>
    public bool InCreation { get; set; }
    /// <summary>The start's <c>force race</c> RACE ids the creation is limited to (empty: every playable race).</summary>
    public IReadOnlyList<string> CreationRaces { get; set; } = [];
    /// <summary>The character of another faction the HUD shows (<see cref="InspectCommand"/>), or <see cref="CharacterId.None"/>. Display only: not hashed and not saved.</summary>
    public CharacterId Inspected { get; set; } = CharacterId.None;
    /// <summary>The instance id of the building the HUD shows (<see cref="InspectBuildingCommand"/>), or null. Display only: not hashed and not saved.</summary>
    public string? InspectedBuilding { get; set; }
    /// <summary>The container (<see cref="Locks.Fixture.Id"/>) whose window is open and the character who opened it (<see cref="Items.OpenContainerOrder"/>), and the one a character is still walking to. Not hashed and not saved.</summary>
    public string? OpenContainer { get; set; }
    public CharacterId ContainerOpener { get; set; } = CharacterId.None;
    public string? OpeningContainer { get; set; }
    public CharacterId ContainerWalker { get; set; } = CharacterId.None;
    /// <summary>The door (<see cref="Locks.Fixture.Id"/>) a character is walking to open or close (<see cref="Locks.DoorOrder"/>), and who. Not hashed and not saved, as the container walk.</summary>
    public string? DoorTarget { get; set; }
    public CharacterId DoorWalker { get; set; } = CharacterId.None;
    /// <summary>The camera focus (<see cref="FocusCommand"/>): zones use it only while no player character exists, campaign announcements always. Not hashed.</summary>
    public System.Numerics.Vector3? Focus { get; set; }
    /// <summary>The last prospecting this player's character finished (null: none yet). Written by the production system's serial step, read by the host between ticks; not hashed.</summary>
    public Production.ProspectReport? Prospected { get => Volatile.Read(ref prospected); internal set => Volatile.Write(ref prospected, value); }
    /// <summary>An editor a conversation opened for this player: the <see cref="Editing.EditCharacter"/> it allows takes it. Not hashed: ingress bookkeeping.</summary>
    public EditGrant? Grant { get; internal set; }

    /// <summary>True, and the grant is used up, when the open grant is exactly this edit's.</summary>
    internal bool TakeGrant(Editing.EditKind kind, CharacterId character, CharacterId surgeon)
    {
        if (Grant is not { } g || g.Kind != kind || g.Character != character || g.Surgeon != surgeon) return false;
        Grant = null;
        return true;
    }

    /// <summary>
    /// Drops characters that are gone (serial phases, after the commit). Somebody stays selected (the original keeps one of the player's characters selected): when every
    /// selected one is gone or dead, the first living one this player owns is, so a session never selects another human's character.
    /// </summary>
    internal void Prune(World world)
    {
        var table = world.Characters;
        bool hadSelection = Selection.Count > 0;
        Selection.RemoveAll(c => !table.TryResolveNext(c, out _));
        if (hadSelection && world.Player.Exists && !Selection.Any(c => table.TryResolveNext(c, out int slot) && table.Cold(slot) is { DiedTick: < 0 }))
            for (int i = 0; i < table.HighWater; i++)
                if (table.Next[i].Alive && table.Cold(i) is { IsPlayer: true, DiedTick: < 0 } && world.OwnerOfSlot(i) == Number)
                {
                    Selection.Clear();
                    Selection.Add(new CharacterId(i, table.Next[i].Generation));
                    break;
                }
        if (!Inspected.IsNone && !table.TryResolveNext(Inspected, out _)) Inspected = CharacterId.None;
    }

    /// <summary>The hash terms of a session after the first (session 0 hashes in <see cref="PlayerState"/>'s old place).</summary>
    internal void Hash(ref StateHasher h)
    {
        h.Add(0x50536E00 + Number);
        h.Add(Squad);
        h.Add(Selection.Count);
        foreach (var c in Selection) h.Add(c);
    }
}

/// <summary>An editor a conversation opened for one player (a plastic surgeon's, a recruit's): the matching <see cref="Editing.EditCharacter"/> may be paid or allowed.</summary>
public sealed record EditGrant(Editing.EditKind Kind, CharacterId Character, CharacterId Surgeon);

/// <summary>A system that holds an NPC for one player's window (a conversation or its approach, a trade), so another player cannot take it.</summary>
public interface INpcHolder
{
    /// <summary>The number of the player holding <paramref name="npc"/>; -1 none.</summary>
    int HolderOf(CharacterId npc);
}

/// <summary>A message for one player (<see cref="World.Post(int, string)"/>), or for everyone (<see cref="Everyone"/>).</summary>
public readonly record struct PlayerMessage(int Player, string Text)
{
    public const int Everyone = -1;
    /// <summary>Whether player <paramref name="player"/> sees it.</summary>
    public bool For(int player) => Player < 0 || Player == player;
}
