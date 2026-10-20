namespace Meitou.Simulation;

/// <summary>
/// The player faction in the world (docs/game/ui-input.md section 7, economy.md): the faction that is theirs, their money, map knowledge and squad AI options, shared by
/// every human playing it; and the humans' <see cref="Sessions"/> (selection, active squad, windows; docs/simulation.md "Multiplayer (host-authoritative co-op)"). Selection
/// is world state, changed by <see cref="SelectCommand"/>, so a scripted game of selects and orders replays the same. There is no player until a new game starts
/// (<see cref="Exists"/>).
/// </summary>
public sealed class PlayerState
{
    // Ascending Number; [0] is always player 0.
    readonly List<PlayerSession> sessions = [new PlayerSession(0)];

    /// <summary>The player's faction, a position in the world's faction list (-1 before a game starts).</summary>
    public int Faction { get; set; } = -1;
    public bool Exists => Faction >= 0;
    /// <summary>Money in cats (NEW_GAME_STARTOFF <c>money</c>; how it is spent is the economy stage). One purse for every human.</summary>
    public long Money { get; set; }
    /// <summary>Player 0's session: the host's, or the single player's. Always there.</summary>
    public PlayerSession Local => sessions[0];
    /// <summary>Every human's session, by ascending <see cref="PlayerSession.Number"/> (the first is <see cref="Local"/>).</summary>
    public IReadOnlyList<PlayerSession> Sessions => sessions;

    /// <summary>Player <paramref name="player"/>'s session; null when there is none.</summary>
    public PlayerSession? Find(int player)
    {
        foreach (var s in sessions)
            if (s.Number == player) return s;
        return null;
    }

    /// <summary>Player <paramref name="player"/>'s session, added when there is none (commands and <see cref="Join"/> only: a session beyond 0 is hashed).</summary>
    public PlayerSession Session(int player)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(player);
        int at = 0;
        while (at < sessions.Count && sessions[at].Number < player) at++;
        if (at < sessions.Count && sessions[at].Number == player) return sessions[at];
        var added = new PlayerSession(player);
        sessions.Insert(at, added);
        return added;
    }

    /// <summary>Adds player <paramref name="player"/>'s session, or updates its roster entry (<see cref="PlayerSession.Guid"/>, <see cref="PlayerSession.Name"/>).</summary>
    public PlayerSession Join(int player, Guid guid, string name)
    {
        var s = Session(player);
        s.Guid = guid;
        s.Name = name;
        return s;
    }

    /// <summary>The session of the player with roster id <paramref name="guid"/>; null when none (or <see cref="Guid.Empty"/>).</summary>
    public PlayerSession? FindByGuid(Guid guid)
    {
        if (guid == Guid.Empty) return null;
        foreach (var s in sessions)
            if (s.Guid == guid) return s;
        return null;
    }

    /// <summary>The number a new player gets: one more than the highest.</summary>
    public int NextNumber() => sessions[^1].Number + 1;

    /// <summary>Whether some session's HUD inspects <paramref name="id"/> (read by the parallel body pass).</summary>
    public bool Inspects(CharacterId id)
    {
        if (id.IsNone) return false;
        foreach (var s in sessions)
            if (s.Inspected == id) return true;
        return false;
    }

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

    /// <summary>Drops characters that are gone from every session (serial phases, after the commit; <see cref="PlayerSession.Prune"/>).</summary>
    internal void Prune(World world)
    {
        foreach (var s in sessions) s.Prune(world);
    }

    internal void Hash(ref StateHasher h)
    {
        var local = Local;
        h.Add(Faction);
        h.Add(Money);
        h.Add(local.Squad);
        if (SquadAi != SquadAi.Defaults) h.Add(0x53714169 + (int)SquadAi);
        h.Add(local.Selection.Count);
        foreach (var c in local.Selection)
        {
            h.Add(c);
        }
        for (int k = 1; k < sessions.Count; k++) sessions[k].Hash(ref h);   // nothing while player 0 is alone, so the pinned hashes stay
    }
}

/// <summary>
/// Applies the player's selection and stop commands (step 1 of the tick), each to its issuer's session (<see cref="World.SessionOf"/>). Move orders are the movement
/// system's.
/// </summary>
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
                    // Only the issuer's own characters: a session's selection never holds another human's.
                    var selectable = select.Characters.Where(id =>
                        table.TryResolveNext(id, out int slot) && table.Cold(slot)!.IsPlayer && world.OwnerOfSlot(slot) == select.Player).ToArray();
                    if (selectable.Length == 0) break;
                    var session = world.SessionOf(select);
                    // Picking one of your own shows them: the inspected character of another faction no longer takes the HUD.
                    session.Inspected = CharacterId.None;
                    if (!select.Additive) session.Selection.Clear();
                    foreach (var id in selectable)
                        if (!session.Selection.Contains(id)) session.Selection.Add(id);
                    // Selecting someone of another squad shows that squad (engine reading of the original's cached squad).
                    if (!select.Additive && table.TryResolveNext(selectable[0], out int first) && world.Squads.Find(table.Cold(first)!.SquadId) is { } theirs
                        && theirs.Faction == player.Faction && theirs.Owner == session.Number) session.Squad = theirs.Id;
                    break;
                case SquadCommand squadCommand:
                    SquadManagement.Apply(world, data, squadCommand);
                    break;
                case SeenDiscoveriesCommand:
                    player.SeeDiscoveries();   // shared map knowledge: one player closing the map clears the marks for everyone
                    break;
                case InspectCommand inspect:
                    world.SessionOf(inspect).Inspected = inspect.Target;
                    break;
                case Items.OpenContainerOrder open:
                    OpenContainerInput(world, open);
                    break;
                case Items.CloseContainer close:
                    var closing = world.SessionOf(close);
                    closing.OpenContainer = closing.OpeningContainer = null;
                    break;
                case InspectBuildingCommand building:
                    world.SessionOf(building).InspectedBuilding = building.Instance;
                    break;
                case StopCommand stop:
                    foreach (var id in world.Actors(stop, stop.Characters))
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
                            if (!MoveLimb(cold, inventory, move)) world.Post(move.Player, "Cannot fit or remove that limb: it needs a missing limb of its side, or the slot changed.");
                        }
                        else if (inventory.All().ElementAtOrDefault(move.Item.Index) is { LockedOn: true } && move.Section != move.Item.Section)
                            world.Post(move.Player, "This item is locked in place and cannot be removed. You can try to pick the lock, find a saw, or use brute strength.");
                        else if (!Items.InventoryCommands.Move(inventory, move.Item, move.Section, move.X, move.Y))
                            world.Post(move.Player, "Cannot move that item: the slot is occupied, incompatible or the inventory changed.");
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
                case Editing.FinishCreation finish:
                    world.SessionOf(finish).InCreation = false;
                    break;
                case Items.PickUpItemOrder pick:
                    PickUpInput(world, pick);
                    break;
                case Items.ArrangeInventory arrange:
                    if (table.TryResolveNext(arrange.Character, out int character) && table.Cold(character) is { IsPlayer: true, Inventory: { } carried } &&
                        !Items.InventoryCommands.Arrange(carried)) world.Post(arrange.Player, "Cannot arrange inventory: the items do not fit.");
                    break;
            }
        }
    }

    public void SlowWorld(World world)
    {
        world.Player.Prune(world);
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
        if (sites.Count == 0) { world.Post(read.Player, "You already know all these locations, no new information can be learned."); return; }
        foreach (int site in sites) world.Player.Know(site, TownKnowledge.Discovered | TownKnowledge.Recent);
        inventory.RemoveOne(item);
        world.Post(read.Player, $"{sites.Count} location(s) added to the map.");
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
