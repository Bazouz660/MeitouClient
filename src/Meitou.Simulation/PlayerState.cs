namespace Meitou.Simulation;

/// <summary>
/// The player in the world (docs/game/ui-input.md section 7, economy.md): the faction that is theirs, their money and the characters
/// selected. Selection is world state, changed by <see cref="SelectCommand"/>, so a scripted game of selects and orders replays the
/// same. There is no player until a new game starts (<see cref="Exists"/>).
/// </summary>
public sealed class PlayerState
{
    /// <summary>The player's faction, a position in the world's faction list (-1 before a game starts).</summary>
    public int Faction { get; set; } = -1;
    public bool Exists => Faction >= 0;
    /// <summary>Money in cats (NEW_GAME_STARTOFF <c>money</c>; how it is spent is the economy stage).</summary>
    public long Money { get; set; }
    /// <summary>The selected characters, in the order they were selected.</summary>
    public List<CharacterId> Selection { get; } = [];
    /// <summary>
    /// The active squad (the original's cached squad, <c>FUN_1407f5520</c>): the one the squad bar shows and recruits join. The start's squad at first; the squad tabs, Tab
    /// (<see cref="ActiveSquadCommand"/>) and selecting a character of another squad change it (<see cref="SquadManagement"/>).
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
    /// <summary>The container (<see cref="Locks.Fixture.Id"/>) whose window is open and the character who opened it (<see cref="Items.OpenContainerOrder"/>), and the one a character is still walking to. Display only: not hashed and not saved.</summary>
    public string? OpenContainer { get; set; }
    public CharacterId ContainerOpener { get; set; } = CharacterId.None;
    public string? OpeningContainer { get; set; }
    public CharacterId ContainerWalker { get; set; } = CharacterId.None;
    /// <summary>The downed character whose inventory window is open and who opened it (<see cref="Carrying.OpenLootOrder"/>), and the one a looter is still walking to. Display only, as the container.</summary>
    public CharacterId OpenLoot { get; set; } = CharacterId.None;
    public CharacterId LootOpener { get; set; } = CharacterId.None;
    public CharacterId OpeningLoot { get; set; } = CharacterId.None;
    public CharacterId LootWalker { get; set; } = CharacterId.None;
    /// <summary>The door (<see cref="Locks.Fixture.Id"/>) a character is walking to open or close (<see cref="Locks.DoorOrder"/>), and who. Not hashed and not saved, as the container walk.</summary>
    public string? DoorTarget { get; set; }
    public CharacterId DoorWalker { get; set; } = CharacterId.None;
    /// <summary>
    /// What the player knows of each placed town (<see cref="PopulationData.Sites"/> order): discovered, explored, recently discovered (docs/game/ui-screens.md "Map").
    /// Replaced, never changed in place, so a snapshot may hold it. Not hashed: it follows from positions and nothing in the simulation reads it.
    /// </summary>
    public TownKnowledge[] Towns { get; private set; } = [];
    /// <summary>The squad AI options of the overview's AI tab that Meitou runs (<see cref="Simulation.SquadAi"/>); saved as CAMERA bools (<see cref="SquadAiKeys"/>).</summary>
    public SquadAi SquadAi { get; set; } = SquadAi.Defaults;

    /// <summary>Adds <paramref name="flags"/> to a town's knowledge; true when that changed it.</summary>
    public bool Know(int site, TownKnowledge flags)
    {
        if (site < 0) return false;
        if (site < Towns.Length && (Towns[site] & flags) == flags) return false;
        var next = new TownKnowledge[Math.Max(Towns.Length, site + 1)];
        Towns.CopyTo(next, 0);
        next[site] |= flags;
        Towns = next;
        return true;
    }

    /// <summary>The camps and nests known (<see cref="CampView"/>), by wild site. Replaced, never changed in place.</summary>
    public IReadOnlyList<CampView> Camps { get; private set; } = [];

    /// <summary>Adds <paramref name="flags"/> to a camp's knowledge (adding the camp when new) and fills in its position when it has none; true when that changed it.</summary>
    public bool KnowCamp(CampView camp, TownKnowledge flags)
    {
        int i = -1;
        for (int k = 0; k < Camps.Count; k++) if (Camps[k].Site == camp.Site) { i = k; break; }
        var old = i >= 0 ? Camps[i] : null;
        var next = (old ?? camp) with { Known = (old?.Known ?? 0) | flags, Position = old?.Position ?? camp.Position };
        if (next == old) return false;
        var list = Camps.ToList();
        if (i >= 0) list[i] = next; else list.Add(next);
        Camps = list;
        return true;
    }

    internal void SeeDiscoveries()
    {
        if (Towns.Any(t => (t & TownKnowledge.Recent) != 0)) Towns = [.. Towns.Select(t => t & ~TownKnowledge.Recent)];
        if (Camps.Any(c => (c.Known & TownKnowledge.Recent) != 0)) Camps = [.. Camps.Select(c => c with { Known = c.Known & ~TownKnowledge.Recent })];
    }

    /// <summary>Drops characters that are gone (serial phases, after the commit).</summary>
    internal void Prune(CharacterTable table)
    {
        bool hadSelection = Selection.Count > 0;
        Selection.RemoveAll(c => !table.TryResolveNext(c, out _));
        // Somebody stays selected (the original keeps one of the player's characters selected): when every selected one is gone or dead, the first living one is.
        if (hadSelection && Exists && !Selection.Any(c => table.TryResolveNext(c, out int slot) && table.Cold(slot) is { DiedTick: < 0 }))
            for (int i = 0; i < table.HighWater; i++)
                if (table.Next[i].Alive && table.Cold(i) is { IsPlayer: true, DiedTick: < 0 })
                {
                    Selection.Clear();
                    Selection.Add(new CharacterId(i, table.Next[i].Generation));
                    break;
                }
        if (!Inspected.IsNone && !table.TryResolveNext(Inspected, out _)) Inspected = CharacterId.None;
    }

    internal void Hash(ref StateHasher h)
    {
        h.Add(Faction);
        h.Add(Money);
        h.Add(Squad);
        if (SquadAi != SquadAi.Defaults) h.Add(0x53714169 + (int)SquadAi);
        h.Add(Selection.Count);
        foreach (var c in Selection)
        {
            h.Add(c);
        }
    }
}

/// <summary>Applies the player's selection and stop commands (step 1 of the tick). Move orders are the movement system's.</summary>
public sealed partial class PlayerSystem(PopulationData? data = null) : ITickSystem
{
    readonly CharacterAssembly? assembly = data is null ? null : new(data);
    internal PopulationData? Data => data;
    public void Inputs(World world, IReadOnlyList<SimCommand> commands)
    {
        var table = world.Characters;
        var player = world.Player;
        foreach (var command in commands)
        {
            switch (command)
            {
                case SelectCommand select:
                    var selectable = select.Characters.Where(id =>
                        table.TryResolveNext(id, out int slot) && table.Cold(slot)!.IsPlayer).ToArray();
                    if (selectable.Length == 0) break;
                    // Picking one of your own shows them: the inspected character of another faction no longer takes the HUD.
                    player.Inspected = CharacterId.None;
                    if (!select.Additive) player.Selection.Clear();
                    foreach (var id in selectable)
                        if (!player.Selection.Contains(id)) player.Selection.Add(id);
                    // Selecting someone of another squad shows that squad (engine reading of the original's cached squad).
                    if (!select.Additive && table.TryResolveNext(selectable[0], out int first) && world.Squads.Find(table.Cold(first)!.SquadId) is { } theirs
                        && theirs.Faction == player.Faction) player.Squad = theirs.Id;
                    break;
                case SquadCommand squadCommand:
                    SquadManagement.Apply(world, data, squadCommand);
                    break;
                case SeenDiscoveriesCommand:
                    player.SeeDiscoveries();
                    break;
                case InspectCommand inspect:
                    player.Inspected = inspect.Target;
                    break;
                case Items.OpenContainerOrder open:
                    OpenContainerInput(world, open);
                    break;
                case Items.CloseContainer:
                    player.OpenContainer = player.OpeningContainer = null;
                    player.OpenLoot = player.OpeningLoot = CharacterId.None;
                    break;
                case InspectBuildingCommand building:
                    player.InspectedBuilding = building.Instance;
                    break;
                case StopCommand stop:
                    foreach (var id in stop.Characters.Count > 0 ? stop.Characters : player.Selection.ToArray())
                    {
                        if (!table.TryResolveNext(id, out int slot) || !table.Cold(slot)!.IsPlayer) continue;
                        MovementSystem.Stop(table, slot);
                    }
                    break;
                case Items.ReadMapItem read:
                    ReadMap(world, read);
                    break;
                case Items.MoveInventoryItem move:
                    if (table.TryResolveNext(move.Character, out int owner) && table.Cold(owner) is { IsPlayer: true, Inventory: { } inventory } cold)
                    {
                        if (Items.InventoryCommands.IsLimbSection(move.Item.Section) || Items.InventoryCommands.IsLimbSection(move.Section))
                        {
                            if (!MoveLimb(cold, inventory, move)) world.Post("Cannot fit or remove that limb: it needs a missing limb of its side, or the slot changed.");
                        }
                        else if (inventory.All().ElementAtOrDefault(move.Item.Index) is { LockedOn: true } && move.Section != move.Item.Section)
                            world.Post("This item is locked in place and cannot be removed. You can try to pick the lock, find a saw, or use brute strength.");
                        else if (!Items.InventoryCommands.Move(inventory, move.Item, move.Section, move.X, move.Y))
                            world.Post("Cannot move that item: the slot is occupied, incompatible or the inventory changed.");
                        else if (Items.InventoryCommands.IsEquipmentSection(move.Item.Section) || Items.InventoryCommands.IsEquipmentSection(move.Section))
                            assembly?.RefreshEquipment(cold);
                    }
                    break;
                case SquadAiCommand option:
                    player.SquadAi = option.On ? player.SquadAi | option.Option : player.SquadAi & ~option.Option;
                    break;
                case OrdersCommand or RunSpeedCommand:
                    Orders.Apply(world, command);
                    break;
                case Editing.EditCharacter edit:
                    if (data is not null) Editing.CharacterEditing.Apply(world, data, edit);
                    break;
                case DebugCommand debug:
                    DebugCheats.Apply(world, data, debug);
                    break;
                case Editing.FinishCreation:
                    player.InCreation = false;
                    break;
                case Items.PickUpItemOrder pick:
                    PickUpInput(world, pick);
                    break;
                case Items.DropInventoryItem drop:
                    if (table.TryResolveNext(drop.Character, out int dropper) && table.Cold(dropper) is { IsPlayer: true, Inventory: { } held } holder &&
                        held.All().ElementAtOrDefault(drop.Item.Index) is { } item && item.Record == drop.Item.Record && item.Section == drop.Item.Section && item.X == drop.Item.X &&
                        item.Y == drop.Item.Y && item.Quantity == drop.Item.Quantity && Items.InventoryCommands.IsMovableSection(item.Section))
                    {
                        if (item.LockedOn) { world.Post("This item is locked in place and cannot be removed. You can try to pick the lock, find a saw, or use brute strength."); break; }
                        bool worn = Items.InventoryCommands.IsEquipmentSection(item.Section);
                        held.Remove(item);
                        item.Section = "main";
                        // At the feet, a little ahead (engine choice), on the ground.
                        ref var hot = ref table.Next[dropper];
                        var at = hot.Position + new System.Numerics.Vector3(MathF.Sin(hot.Yaw), 0, MathF.Cos(hot.Yaw)) * 3;
                        at.Y = world.Walkability.GroundHeight(at.X, at.Z, hot.Position.Y);
                        // Turned at random (the original's physics tumbles it), by a roll of the dropper and the tick.
                        world.GroundItems.Drop(item, at, Rng.Float(Rng.Hash(world.Seed, (ulong)dropper, RngPurpose.Loot, (ulong)world.Tick)) * MathF.Tau);
                        if (worn) assembly?.RefreshEquipment(holder);
                    }
                    break;
                case Items.ArrangeInventory arrange:
                    if (table.TryResolveNext(arrange.Character, out int character) && table.Cold(character) is { IsPlayer: true, Inventory: { } carried } &&
                        !Items.InventoryCommands.Arrange(carried)) world.Post("Cannot arrange inventory: the items do not fit.");
                    break;
            }
        }
    }

    public void SlowWorld(World world)
    {
        world.Player.Prune(world.Characters);
        SquadManagement.KeepActive(world);
        PickUpWorkers(world);
        ContainerWorkers(world);
    }

    /// <summary>
    /// A prosthetic dragged into or out of the limbs window (docs/game/character-stats.md "Limb replacements and robots"): into a slot it is fitted when that limb is a stump and the
    /// item's <c>slot</c> is that limb; out of one it is taken off and the limb becomes a stump. A move between two limb slots is refused.
    /// </summary>
    bool MoveLimb(CharacterCold cold, Items.Inventory inventory, Items.MoveInventoryItem move)
    {
        if (data is null || cold.Medical is not { } medical) return false;
        int from = Items.InventoryCommands.LimbSlotOf(move.Item.Section), to = Items.InventoryCommands.LimbSlotOf(move.Section);
        if (from >= 0 && to >= 0) return false;
        var item = inventory.All().ElementAtOrDefault(move.Item.Index);
        if (item is null) return false;
        if (to >= 0)
        {
            if (data.Items.Limb(item.Record) is not { } limb || limb.LimbSlot != to || medical.LimbPart(to) is not { Limb: Bodies.LimbState.Stump }) return false;
            if (!Items.InventoryCommands.Move(inventory, move.Item, move.Section, move.X, move.Y)) return false;
            medical.FitReplacement(limb, item, item.LimbDamage, item.LimbWear, item.LimbStun);
            (item.LimbDamage, item.LimbWear, item.LimbStun) = (0, 0, 0);
        }
        else
        {
            if (medical.LimbPart(from) is not { Limb: Bodies.LimbState.Replaced } part || part.ReplacementItem != item) return false;
            if (!Items.InventoryCommands.Move(inventory, move.Item, move.Section, move.X, move.Y)) return false;
            medical.RemoveReplacement(from);
        }
        assembly?.RefreshEquipment(cold);
        return true;
    }

    /// <summary>A map item of a player character's inventory: its towns become discovered and recently discovered, and it is used up; with nothing new it stays.</summary>
    void ReadMap(World world, Items.ReadMapItem read)
    {
        var table = world.Characters;
        if (data is null || !table.TryResolveNext(read.Character, out int owner) || table.Cold(owner) is not { IsPlayer: true, Inventory: { } inventory }) return;
        var t = read.Item;
        var item = inventory.All().ElementAtOrDefault(t.Index);
        if (item is null || item.Record != t.Record || item.Section != t.Section || item.X != t.X || item.Y != t.Y || item.Type != Meitou.Data.Fcs.FcsRecordType.MAP_ITEM
            || data.Db.Find(item.Record) is not { } record) return;
        var sites = Items.MapItems.Reveals(data, record, world.Player.Towns, world.Seed);
        if (sites.Count == 0) { world.Post("You already know all these locations, no new information can be learned."); return; }
        foreach (int site in sites) world.Player.Know(site, TownKnowledge.Discovered | TownKnowledge.Recent);
        inventory.RemoveOne(item);
        world.Post($"{sites.Count} location(s) added to the map.");
    }
}

/// <summary>
/// The squad AI options that start implicit jobs (docs/game/ai-tasks.md "Perma-jobs"): the original's squad toggles at <c>DAT_142134690</c> +0x117, +0x116 and +0x118, all on in a
/// new game.
/// </summary>
[Flags]
public enum SquadAi : byte
{
    None = 0,
    /// <summary>Ditch items: idle characters put carried materials into storage (job 242).</summary>
    Ditch = 1,
    /// <summary>Sleep when injured: idle injured characters go to bed until healed (jobs 153 and 257).</summary>
    Sleep = 2,
    /// <summary>Sit when idle: idle characters at home sit on a free chair (job 275).</summary>
    Sit = 4,
    Defaults = Ditch | Sleep | Sit,
}

/// <summary>Turns a squad AI option on or off.</summary>
public sealed record SquadAiCommand(SquadAi Option, bool On) : SimCommand;

/// <summary>The squad AI options' CAMERA bools (<b>Verified</b>, the camera record writer <c>FUN_1403eb710</c>).</summary>
public static class SquadAiKeys
{
    public static readonly (SquadAi Option, string Key)[] Keys = [(SquadAi.Ditch, "ditch"), (SquadAi.Sleep, "sleep"), (SquadAi.Sit, "sit")];

    public static void Write(Meitou.Data.Fcs.FcsRecord camera, SquadAi options)
    {
        foreach (var (option, key) in Keys) camera.Bools[key] = (options & option) != 0;
    }

    /// <summary>The options a CAMERA record holds; an absent key stays on.</summary>
    public static SquadAi Read(Meitou.Data.Fcs.FcsRecord camera)
    {
        var options = SquadAi.Defaults;
        foreach (var (option, key) in Keys)
            if (camera.Bools.TryGetValue(key, out bool on) && !on) options &= ~option;
        return options;
    }
}

/// <summary>What the player knows of a town (the GAMESTATE_TOWN bools <c>discovered</c>, <c>explored</c>, <c>recently discovered</c>; docs/game/ui-screens.md "Map").</summary>
[Flags]
public enum TownKnowledge : byte
{
    None = 0,
    /// <summary>On the map.</summary>
    Discovered = 1,
    /// <summary>Visited: the map names it and shows its faction's colour.</summary>
    Explored = 2,
    /// <summary>Found but the map not yet opened since: the map marks it as new.</summary>
    Recent = 4,
}

/// <summary>
/// A camp or nest the player knows (a wild nest, docs/game/ui-screens.md "Map"): its wild site, TOWN record, name, owner faction, whether that faction is <c>not real</c>
/// (the map calls it a nest, else a camp), where it is (null until it has loaded once) and what is known.
/// </summary>
public sealed record CampView(int Site, string TownId, string Name, string FactionId, bool Wildlife, System.Numerics.Vector3? Position, TownKnowledge Known);
