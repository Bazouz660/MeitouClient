using System.Numerics;
using Meitou.Data;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Gameplay.Construction;
using Meitou.Data.World;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Items;

namespace Meitou.Simulation.Construction;

/// <summary>The numbers of <see cref="ConstructionSystem"/>; the engine choices where the original is Unknown say so (docs/simulation.md "Buildings").</summary>
public sealed class ConstructionSettings
{
    /// <summary>The <i>Building speed</i> option (0.5 to 2): multiplies build work. Its default is Unknown; 1 is neutral.</summary>
    public float BuildingSpeed { get; init; } = 1;
    /// <summary>A builder works from within this far (X/Z units) of the site's footprint edge (<b>Unknown</b>; the engine reading is footprint edge + 20).</summary>
    public float Reach { get; init; } = 20;
    /// <summary>
    /// A builder whose path has ended counts as arrived within this much beyond <see cref="Reach"/>: a site that is a navmesh obstacle cannot be walked into and the simulation does not know
    /// the footprint (<b>engine choice</b>).
    /// </summary>
    public float SettleReach { get; init; } = 150;
    /// <summary>The footprint radius of a BUILDING record (the host knows it from the mesh); null: 0, so the reach is <see cref="Reach"/> plus <see cref="SettleReach"/> once the path has ended.</summary>
    public Func<string, float>? Footprint { get; init; }
    /// <summary>Ticks between the auto-job's looks (the medic job's rhythm); 0 turns the auto-job off.</summary>
    public int AutoEveryTicks { get; init; } = 15;
}

/// <summary>
/// Player construction (docs/simulation.md "Buildings"; the original's rules in docs/game/buildings-production.md "Construction" and docs/game/ai-tasks.md "Build tasks").
/// <list type="bullet">
/// <item><b>Sites</b> (<see cref="World.Buildings"/>): <see cref="PlaceBuildings"/> creates one per plan that passes the data half of the placement rules (a BUILDING, unlocked by research, not
/// too close to another faction's town); the geometric half is the host's. <see cref="CancelSite"/> removes a site with nothing delivered, <see cref="PauseSite"/> sets its <c>pause</c> flag.</item>
/// <item><b>Builders</b>: <see cref="BuildOrder"/> (orders 2 and 95) and <see cref="DismantleOrder"/> (96) set <see cref="CharacterTask.Build"/> / <see cref="CharacterTask.Dismantle"/> and
/// <see cref="CharacterCold.BuildSite"/>; <c>Schedule</c> walks the character to the site (the first-aid pattern), <c>SlowWorld</c> works it, all serial. At the site the builder pushes build work
/// each tick until the site stalls (<c>sum(min(delivered, required)) &lt;= progress</c>); then, if it carries a missing material, it fills the 5 s delivery bar
/// (<see cref="CharacterTask.DeliverMaterials"/>), hands over one unit per missing type and goes on building; empty-handed it stops (fetching, task 247, is not built).</item>
/// <item><b>Work</b> per tick (<b>Verified</b>): <c>lerp(0.3, 2, engineering/100) x dt x Building speed x CONSTANTS build speed x 0.75^k</c>, k the builders that have worked the site this tick
/// (capped at 15; the reset is <b>Unknown</b>, per tick is the engine reading), progress += <c>build speed mult / (1200/11)</c> x that. Dismantling: <c>dt x lerp(0.05, 4, engineering/100) x 4</c> x rate
/// off the progress; at 0 the delivered materials come back (60 to 100 %, at least 1 each) into the dismantler's inventory.</item>
/// <item><b>Auto-job</b> (<c>SlowWorld</c>, every <see cref="ConstructionSettings.AutoEveryTicks"/> ticks): an idle, wandering or following player character with the Jobs order takes the best-scoring
/// unpaused site (<c>max(2 (1 - d^2/9000^2), 0.0001) / (n + 1)</c>, n builders on it), skipping a stalled site it carries nothing for (the original's planner cost is <b>Unknown</b>).</item>
/// </list>
/// Placed after the carry system and before combat. Everything here is serial and reads only the world's state and the command order.
/// </summary>
public sealed class ConstructionSystem : ITickSystem
{
    /// <summary>The delivery bar fills at this much per second (<b>Verified</b>, <c>Task_AddMaterialsToBuilding</c>: 5 s).</summary>
    public const float BarRate = 0.2f;
    /// <summary>The 0.75 falloff counter is capped at this (<b>Verified</b>); above <see cref="CounterRestart"/> it restarts at 0.</summary>
    public const int CounterCap = 15, CounterRestart = 100;
    public const string FurnitureFirst = "I have to dismantle the furniture first.";
    public const string UnmountFirst = "I have to unmount the buildings first.";

    // 0.75^k by repeated multiplication, so the factor never depends on a library's pow.
    static readonly float[] Falloff = BuildFalloff();

    static float[] BuildFalloff()
    {
        var table = new float[CounterCap + 1];
        float f = 1;
        for (int k = 0; k <= CounterCap; k++, f *= 0.75f) table[k] = f;
        return table;
    }

    readonly GameDatabase db;
    readonly GameConstants constants;
    readonly ItemFactory items;
    readonly XpService xp;
    readonly IReadOnlyList<PlacementTown> towns;
    readonly string playerFactionId;
    readonly Dictionary<string, BuildingBuildData?> buildData = new(StringComparer.Ordinal);
    // A wall subsection (short piece, connector, wedge) to the BUILDINGs that list it in `wall subsections`.
    Dictionary<string, List<string>>? sectionOf;

    /// <summary>
    /// Research allows a plan: its record is unlocked, or it is a wall subsection of an unlocked BUILDING (a wall line lays those pieces; no research enables them, docs/game/buildings-production.md "Walls").
    /// </summary>
    bool Unlocked(World world, string record)
    {
        if (world.Research.IsUnlocked(record)) return true;
        if (sectionOf is null)
        {
            sectionOf = new(StringComparer.Ordinal);
            foreach (var r in db.OfType(Meitou.Data.Fcs.FcsRecordType.BUILDING))
                foreach (var s in r.GetReferences("wall subsections"))
                    (sectionOf.TryGetValue(s.TargetStringId, out var owners) ? owners : sectionOf[s.TargetStringId] = []).Add(r.StringId);
        }
        return sectionOf.TryGetValue(record, out var masters) && masters.Exists(world.Research.IsUnlocked);
    }
    // Some character may still hold a site that is gone (the sweep in Schedule ends its task).
    bool sweep;

    public ConstructionSystem(GameDatabase db, GameConstants constants, BodyOptions options, ItemFactory items, IReadOnlyList<PlacementTown> towns, string playerFactionId, ConstructionSettings? settings = null)
    {
        this.db = db;
        this.constants = constants;
        this.items = items;
        this.towns = towns;
        this.playerFactionId = playerFactionId;
        Settings = settings ?? new ConstructionSettings();
        xp = new XpService(constants, options);
    }

    public ConstructionSystem(PopulationData data, ConstructionSettings? settings = null)
        : this(data.Db, data.Bodies.Constants, data.BodyOptions, data.Items, TownsOf(data), data.PlayerFaction >= 0 ? data.Factions[data.PlayerFaction].Id : "", settings)
    {
    }

    public ConstructionSettings Settings { get; }
    public GameConstants Constants => constants;

    static List<PlacementTown> TownsOf(PopulationData data)
    {
        var list = new List<PlacementTown>(data.Sites.Count);
        foreach (var site in data.Sites) list.Add(new PlacementTown(site.Town, new Vector2(site.Position.X, site.Position.Z)));
        return list;
    }

    /// <summary>The build data of a BUILDING record (null for any other record); read once.</summary>
    public BuildingBuildData? Data(string record)
    {
        if (!buildData.TryGetValue(record, out var data))
            buildData[record] = data = db.Find(record) is { Type: Meitou.Data.Fcs.FcsRecordType.BUILDING } r ? BuildingBuildData.From(r) : null;
        return data;
    }

    // ------------------------------------------------------------------ orders

    static IReadOnlyList<CharacterId> Who(World world, IReadOnlyList<CharacterId> ids) => ids.Count > 0 ? ids : world.Player.Selection.ToArray();

    public void Inputs(World world, IReadOnlyList<SimCommand> commands)
    {
        var table = world.Characters;
        var reg = world.Buildings;
        foreach (var command in commands)
        {
            switch (command)
            {
                case PlaceBuildings place:
                {
                    var placed = new List<BuildSite>(place.Plans.Count);
                    foreach (var plan in place.Plans)
                        if (Place(world, plan) is { } site) placed.Add(site);
                    TownForWalls(world, placed);
                    AttachWalls(world.Buildings, Data, placed);
                    break;
                }
                case BuildOrder order:
                {
                    // A complete building has nothing to build; repairing one (order 95) needs the building health, which is not modelled.
                    if (Root(reg, reg.Find(order.Site)) is not { Complete: false } site) break;
                    foreach (var id in Who(world, order.Characters))
                        if (table.TryResolveNext(id, out int slot) && table.Cold(slot) is { IsPlayer: true } cold && Able(cold))
                            Begin(world, slot, site, CharacterTask.Build, CharacterTask.Idle);
                    break;
                }
                case DismantleOrder order:
                {
                    if (Root(reg, reg.Find(order.Site)) is not { } site) break;
                    if (Blocker(reg, site) is { } text)
                    {
                        world.Post(text);
                        break;
                    }
                    foreach (var id in Who(world, order.Characters))
                        if (table.TryResolveNext(id, out int slot) && table.Cold(slot) is { IsPlayer: true } cold && Able(cold))
                            Begin(world, slot, site, CharacterTask.Dismantle, CharacterTask.Idle);
                    break;
                }
                case CancelSite cancel:
                    if (Root(reg, reg.Find(cancel.Site)) is not { Complete: false } cancelled) break;
                    if (cancelled.Deliverable > 0) world.Post("Dismantle it to get the materials back.");
                    else Remove(world, cancelled);
                    break;
                case PauseSite pause:
                    if (Root(reg, reg.Find(pause.Site)) is { } paused && paused.Paused != pause.Paused)
                    {
                        paused.Paused = pause.Paused;
                        reg.Touch();
                    }
                    break;
            }
        }
    }

    /// <summary>
    /// Why a site cannot be taken apart yet: a building standing in it (<b>Observed</b>; the original's texts, <b>Verified</b>, refuse while furniture or mounted buildings exist). Furniture is
    /// what is placed inside (interior furniture or a node), the rest counts as mounted.
    /// </summary>
    string? Blocker(BuildingRegistry reg, BuildSite site)
    {
        string? text = null;
        foreach (var other in reg.All)
        {
            if (other.Parent != site.Id) continue;
            var data = Data(other.Record);
            if (data is { IsInteriorFurniture: true } or { IsNode: true }) return FurnitureFirst;
            text = UnmountFirst;
        }
        return text;
    }

    BuildSite? Place(World world, PlannedBuilding plan)
    {
        var data = Data(plan.Record);
        if (data is null || !Unlocked(world, plan.Record))
        {
            world.Post(PlacementRules.Message(PlacementFailure.NotAvailable));
            return null;
        }
        var fail = PlacementRules.TownBlock(data, new Vector2(plan.Position.X, plan.Position.Z), towns, playerFactionId);
        if (fail != PlacementFailure.None)
        {
            world.Post(PlacementRules.Message(fail));
            return null;
        }
        var reg = world.Buildings;
        var materials = new MaterialSlot[data.Materials.Count];
        for (int i = 0; i < materials.Length; i++) materials[i] = new MaterialSlot(data.Materials[i].Item, data.Materials[i].Required);
        var site = new BuildSite
        {
            Id = reg.NewId(WorldLayout.ZoneOf(plan.Position.X, plan.Position.Z)),
            Record = data.Id,
            Name = data.Name,
            Position = plan.Position,
            Rotation = plan.Rotation,
            Floor = plan.Floor,
            Parent = plan.Parent,
            Owner = world.Player.Faction,
            Materials = materials,
            Target = data.Target,
            Rate = data.Rate,
        };
        site.Complete = site.Target <= 0;   // a total of 0 completes at once (Verified)
        site.Town = TownFor(world, site, data);
        reg.Add(site);
        return site;
    }

    // ------------------------------------------------------------------ player towns

    /// <summary>A ghost joins the nearest player town within this far (X/Z) (<b>Verified</b>, <c>FUN_1404d6d90</c>).</summary>
    public const float JoinTownRange = 5000;
    /// <summary>... or the town of the nearest player building within this far (<b>Verified</b>).</summary>
    public const float JoinBuildingRange = 2500;
    /// <summary>A new outpost takes in the player buildings whose nearest town within this far is it (<b>Verified</b>).</summary>
    public const float AdoptRange = 8000;
    /// <summary>Queued walls without a town take the nearest town within this far of their mean position, else a new outpost there (<b>Verified</b>, <c>FUN_1404db180</c>).</summary>
    public const float WallTownRange = 16000;

    /// <summary>
    /// The player town a new site belongs to (docs/game/buildings-production.md "Placement", Outposts): its parent building's; else the nearest player town within
    /// <see cref="JoinTownRange"/>; else the town of the nearest player building within <see cref="JoinBuildingRange"/>; else, for a <c>creates player town</c> building that is not a
    /// wall, a new outpost at its position, which also takes in the player buildings whose nearest town within <see cref="AdoptRange"/> it is. 0 when none.
    /// </summary>
    int TownFor(World world, BuildSite site, BuildingBuildData data)
    {
        var reg = world.Buildings;
        if (site.Parent is { } parent) return reg.Find(parent)?.Town ?? 0;
        int faction = world.Player.Faction;
        PlayerTown? nearest = null;
        float best = float.MaxValue;
        foreach (var t in reg.Towns)
        {
            if (t.Owner != faction) continue;
            float d = DistanceXZ(t.Position, site.Position);
            if (d < best) { best = d; nearest = t; }
        }
        if (nearest is not null && best <= JoinTownRange) return nearest.Number;

        BuildSite? near = null;
        best = float.MaxValue;
        foreach (var s in reg.All)
        {
            if (s.Owner != faction) continue;
            float d = Vector3.Distance(s.Position, site.Position);
            if (d < best) { best = d; near = s; }
        }
        if (near is { Town: > 0 } && best < JoinBuildingRange) return near.Town;

        if (data.LinksTogether || !data.CreatesPlayerTown) return 0;
        var town = reg.AddTown(site.Position, faction);
        foreach (var s in reg.All)
            if (s.Owner == faction && NearestTown(reg, s.Position, AdoptRange) == town.Number) s.Town = town.Number;
        return town.Number;
    }

    /// <summary>Confirm's second pass (<b>Verified</b>, <c>FUN_1404db180</c>): queued walls still without a town take the last player town of the queue, else the nearest town within
    /// <see cref="WallTownRange"/> of their mean position, else a new outpost there. A nearest town of another faction leaves them without one (engine: <see cref="BuildSite.Town"/> names player towns only).</summary>
    void TownForWalls(World world, List<BuildSite> placed)
    {
        int count = 0, last = 0;
        var sum = Vector3.Zero;
        foreach (var s in placed)
        {
            if (Data(s.Record) is { LinksTogether: true } && s.Town == 0)
            {
                count++;
                sum += s.Position;
            }
            else if (s.Town > 0) last = s.Town;
        }
        if (count == 0) return;
        int town = last;
        if (town == 0)
        {
            var mean = sum / count;
            town = NearestTown(world.Buildings, mean, WallTownRange);
            if (town == 0) town = world.Buildings.AddTown(mean, world.Player.Faction).Number;
            else if (town < 0) return;
        }
        foreach (var s in placed)
            if (Data(s.Record) is { LinksTogether: true } && s.Town == 0) s.Town = town;
    }

    /// <summary>The nearest town (X/Z) within <paramref name="radius"/> of any faction: a player town's number, -1 for a placed town of the level data, 0 for none.</summary>
    int NearestTown(BuildingRegistry reg, Vector3 at, float radius)
    {
        int found = 0;
        float best = radius * radius;
        var p = new Vector2(at.X, at.Z);
        foreach (var t in towns)
        {
            float d = Vector2.DistanceSquared(t.Position, p);
            if (d <= best) { best = d; found = -1; }
        }
        foreach (var t in reg.Towns)
        {
            float d = Vector2.DistanceSquared(new Vector2(t.Position.X, t.Position.Z), p);
            if (d <= best) { best = d; found = t.Number; }
        }
        return found;
    }

    static float DistanceXZ(Vector3 a, Vector3 b)
    {
        float dx = a.X - b.X, dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    /// <summary>Removes a site and the wall pieces attached to it (slot 73 forwards a dismantle to the children).</summary>
    void Remove(World world, BuildSite site)
    {
        var reg = world.Buildings;
        if (IsWall(site))
            foreach (var child in reg.All.Where(s => s.WallParent == site.Id).ToList()) reg.Remove(child);
        reg.Remove(site);
        sweep = true;
    }

    // ------------------------------------------------------------------ attached wall pieces

    bool IsWall(BuildSite site) => Data(site.Record) is { LinksTogether: true };

    /// <summary>The site that takes a site's work and orders: the root of its <see cref="BuildSite.WallParent"/> chain, else the site itself (a child's slot 69 forwards to its parent's).</summary>
    public static BuildSite? Root(BuildingRegistry reg, BuildSite? site)
    {
        for (int guard = 0; guard < 64 && site?.WallParent is { } up && reg.Find(up) is { } parent; guard++) site = parent;
        return site;
    }

    /// <summary>Two link points closer than the square root of this are joined (<b>Verified</b>; <c>WallLine.JoinDistanceSquared</c>).</summary>
    public const float JoinDistanceSquared = 10;

    /// <summary>
    /// Attaches each short piece, connector or wedge (<c>link type</c> 4, 1, 2) of <paramref name="sites"/> without a parent or children to a nearby wall site (<b>Observed</b>, <c>FUN_14029ac50</c>
    /// on Confirm and zone load; its check <c>FUN_14029a890</c>: another building, the same complete flag, the candidate's root taken). Nearby: one of the piece's link points or its middle within
    /// sqrt(<see cref="JoinDistanceSquared"/>) of a link point of the candidate (the original finder's radius is <b>Unknown</b>; the middle catches a crest short centred on a joint). Engine choice:
    /// a root that is a normal wall or single wins over one that is itself a subsection; gates are never parents (not WallBuildings).
    /// </summary>
    public static void AttachWalls(BuildingRegistry reg, Func<string, BuildingBuildData?> data, IEnumerable<BuildSite> sites)
    {
        foreach (var child in sites)
        {
            if (child.WallParent is not null || data(child.Record) is not { LinksTogether: true, LinkType: 1 or 2 or 4 } cd || reg.Find(child.Id) != child) continue;
            if (reg.All.Any(s => s.WallParent == child.Id)) continue;
            var (c0, c1) = LinkEnds(cd, child);
            var mid = (c0 + c1) / 2;
            BuildSite? pick = null, fallback = null;
            foreach (var w in reg.All)
            {
                if (w == child || data(w.Record) is not { LinksTogether: true } wd) continue;
                var (w0, w1) = LinkEnds(wd, w);
                if (!Joined(c0, w0, w1) && !Joined(c1, w0, w1) && !Joined(mid, w0, w1)) continue;
                var root = Root(reg, w)!;
                if (root == child || root.Complete != child.Complete || data(root.Record) is not { IsWallGate: false } rd) continue;
                if (rd.LinkType is 0 or 3)
                {
                    pick = root;
                    break;
                }
                fallback ??= root;
            }
            if ((pick ?? fallback) is not { } parent) continue;
            child.WallParent = parent.Id;
            child.Progress = Share(parent, child);
            reg.Touch();
        }
    }

    static bool Joined(Vector3 p, Vector3 a, Vector3 b) => Vector3.DistanceSquared(p, a) < JoinDistanceSquared || Vector3.DistanceSquared(p, b) < JoinDistanceSquared;

    // A wall's start (its position) and end (position + rotation x (0, 0, link length)), as WallLine.Ends.
    static (Vector3 Start, Vector3 End) LinkEnds(BuildingBuildData wall, BuildSite site) =>
        (site.Position, site.Position + Vector3.Transform(new Vector3(0, 0, wall.LinkLength), Quaternion.Normalize(site.Rotation)));

    /// <summary>An attached piece's progress (<b>Verified</b>, parent slot 71): <c>min(parent progress / parent target, 1) x child target</c>.</summary>
    static float Share(BuildSite parent, BuildSite child) =>
        (parent.Target > 0 ? MathF.Min(parent.Progress / parent.Target, 1) : 1) * child.Target;

    /// <summary>Sets the attached pieces' progress and complete flag from their wall's (slots 71, 72).</summary>
    void SyncChildren(World world, BuildSite parent)
    {
        if (!IsWall(parent)) return;
        var reg = world.Buildings;
        foreach (var c in reg.All)
        {
            if (c.WallParent != parent.Id) continue;
            c.Progress = Share(parent, c);
            if (c.Complete == parent.Complete) continue;
            c.Complete = parent.Complete;
            if (c.Complete) c.Destroyed = c.Ruined = false;
            reg.Changed();
        }
    }

    // ------------------------------------------------------------------ builders

    static bool IsWork(byte task) => task is (byte)CharacterTask.Build or (byte)CharacterTask.Dismantle or (byte)CharacterTask.DeliverMaterials;

    /// <summary>A character that can work a site: awake, free to act, not in a fight, not carrying or carried, not in a cage.</summary>
    static bool Able(CharacterCold cold) =>
        cold.Medical is not { Incapacitated: true } && !cold.InCombat && cold.CarriedBy.IsNone && cold.Carrying.IsNone && !cold.IsImprisoned;

    float ReachOf(BuildSite site) => Settings.Reach + (Settings.Footprint?.Invoke(site.Record) ?? 0);

    static float Gap(in CharacterHot n, BuildSite site)
    {
        float dx = n.Position.X - site.Position.X, dz = n.Position.Z - site.Position.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    bool AtSite(in CharacterHot n, BuildSite site)
    {
        float reach = ReachOf(site), gap = Gap(n, site);
        return gap <= reach || ((n.Flags & (ushort)MoveFlags.AnyPath) == 0 && gap <= reach + Settings.SettleReach);
    }

    /// <summary>Gives the character a site to work (serial phases): it stands, then walks there unless it is there already. <paramref name="resume"/> is the task to go back to (kept if already working).</summary>
    void Begin(World world, int slot, BuildSite site, CharacterTask task, CharacterTask resume)
    {
        var table = world.Characters;
        var cold = table.Cold(slot)!;
        ref var n = ref table.Next[slot];
        var keep = IsWork(n.Task) ? cold.ResumeTask : resume;
        MovementSystem.Stop(table, slot);
        n.Task = (byte)task;
        n.Mode = SpeedMode.Free;
        n.TaskTime = 0;
        cold.BuildSite = site.Id;
        cold.BuildBar = 0;
        cold.ResumeTask = keep;
        if (!AtSite(n, site))
        {
            n.Goal = new Vector2(site.Position.X, site.Position.Z);
            n.Flags |= (ushort)MoveFlags.NeedPath;
        }
        sweep = true;
    }

    /// <summary>Ends the work (serial phases): back to the task before it, path dropped.</summary>
    static void End(CharacterTable table, int slot)
    {
        var cold = table.Cold(slot)!;
        ref var n = ref table.Next[slot];
        if (IsWork(n.Task))
        {
            MovementSystem.Stop(table, slot);
            n.Task = (byte)cold.ResumeTask;
            n.TaskTime = 0;
        }
        cold.BuildSite = null;
        cold.BuildBar = 0;
        cold.ResumeTask = CharacterTask.Idle;
    }

    public void Schedule(World world)
    {
        var reg = world.Buildings;
        if (reg.Count == 0 && !sweep) return;
        var table = world.Characters;
        var next = table.Next;
        float dt = world.TickSeconds;
        for (int i = 0; i < table.HighWater; i++)
        {
            ref var n = ref next[i];
            if (!n.Alive) continue;
            var cold = table.Cold(i)!;
            if (cold.BuildSite is not { } id) continue;
            if (!IsWork(n.Task))
            {
                // Another order replaced the work.
                cold.BuildSite = null;
                cold.BuildBar = 0;
                continue;
            }
            var site = Root(reg, reg.Find(id));
            if (site is null || !Able(cold) || (n.Task != (byte)CharacterTask.Dismantle && site.Complete))
            {
                End(table, i);
                continue;
            }
            const ushort Path = (ushort)MoveFlags.AnyPath;
            if (AtSite(n, site))
            {
                // There: stand still and let the slow world work.
                if ((n.Flags & Path) != 0)
                {
                    n.Flags = (ushort)(n.Flags & ~Path);
                    n.PathCursor = 0;
                    cold.PathRequest = 0;
                }
                n.TaskTime = 0;
                continue;
            }
            // On the way, or not started (a path that failed or ended short is asked for again after a wait).
            if ((n.Flags & Path) != 0) continue;
            n.TaskTime -= dt;
            if (n.TaskTime > 0) continue;
            n.Goal = new Vector2(site.Position.X, site.Position.Z);
            n.Mode = SpeedMode.Free;
            n.Flags |= (ushort)MoveFlags.NeedPath;
            n.TaskTime = PursuitSystem.RepathSeconds;
        }
        if (reg.Count == 0) sweep = false;
    }

    // ------------------------------------------------------------------ the work

    public void SlowWorld(World world)
    {
        var reg = world.Buildings;
        if (reg.Count == 0) return;
        foreach (var s in reg.All)
        {
            s.BuilderCounter = 0;
            if (s.Dismantling)
            {
                s.Dismantling = false;
                reg.Touch();
            }
            if (s is { Destroyed: true, Ruined: false })
            {
                Ruin(world, s);
                if (s.WallParent is null) SyncChildren(world, s);
                else if (reg.Find(s.WallParent) is { } wall)
                {
                    // A destroyed attached piece follows its wall again (what the original does with it is Unknown).
                    s.Destroyed = s.Ruined = false;
                    s.Complete = wall.Complete;
                    s.Progress = Share(wall, s);
                }
            }
        }
        var table = world.Characters;
        var next = table.Next;
        float dt = world.TickSeconds;
        for (int i = 0; i < table.HighWater; i++)
        {
            ref var n = ref next[i];
            if (!n.Alive || !IsWork(n.Task)) continue;
            var cold = table.Cold(i)!;
            if (cold.BuildSite is not { } id) continue;
            var site = Root(reg, reg.Find(id));
            if (site is null || !Able(cold))
            {
                End(table, i);
                continue;
            }
            if (!AtSite(n, site)) continue;
            if (n.Velocity.X == 0 && n.Velocity.Z == 0) n.Yaw = MathF.Atan2(site.Position.X - n.Position.X, site.Position.Z - n.Position.Z);
            switch ((CharacterTask)n.Task)
            {
                case CharacterTask.Build: BuildStep(world, i, cold, site, dt); break;
                case CharacterTask.DeliverMaterials: DeliverStep(world, i, cold, site, dt); break;
                case CharacterTask.Dismantle: DismantleStep(world, i, cold, site, dt); break;
            }
        }
        Auto(world);
    }

    static float Engineering(CharacterCold cold) =>
        (cold.Stats?[StatsEnumerated.Engineering] ?? 0) * (cold.Medical?.StatMultiplier(StatsEnumerated.Engineering) ?? 1);

    static float Lerp(float a, float b, float t) => a + (b - a) * t;

    void BuildStep(World world, int slot, CharacterCold cold, BuildSite site, float dt)
    {
        var table = world.Characters;
        if (site.Complete)
        {
            End(table, slot);
            return;
        }
        if (site.Stalled)
        {
            // Everything given is built: hand over what is carried, else the planner would fetch more (task 247, not built).
            if (Carries(cold, site))
            {
                table.Next[slot].Task = (byte)CharacterTask.DeliverMaterials;
                cold.BuildBar = 0;
            }
            else End(table, slot);
            return;
        }
        float skill = Engineering(cold);
        float amount = Lerp(0.3f, 2f, Math.Clamp(skill, 0, 100) / 100) * dt;
        int k = site.BuilderCounter;
        if (k > CounterRestart) k = site.BuilderCounter = 0;
        site.BuilderCounter++;
        float scaled = amount * Settings.BuildingSpeed * constants.BuildSpeed * Falloff[Math.Min(k, CounterCap)];
        site.Progress = site.Rate > 0 ? site.Progress + site.Rate * scaled : site.Target;
        world.Buildings.Touch();
        if (cold.Stats is { } stats && cold.Race is { } race) xp.Engineering(stats, race, dt);
        if (site.Progress >= site.Target) Finish(world, site);
        else SyncChildren(world, site);
    }

    void Finish(World world, BuildSite site)
    {
        site.Progress = site.Target;
        site.Complete = true;
        site.Destroyed = false;
        site.Ruined = false;
        world.Buildings.Changed();
        SyncChildren(world, site);
        if (site.Owner == world.Player.Faction) world.Post(Loc.Tr("Building '{1}' construction complete.", site.Name));
    }

    /// <summary>The first tick on a destroyed building (<b>Verified</b>, <c>FUN_1405595a0</c>): no longer complete, progress 0.4 x target, each delivered amount floor(delivered x U[0.3, 0.5) + 0.5).</summary>
    static void Ruin(World world, BuildSite site)
    {
        site.Complete = false;
        site.Ruined = true;
        site.Progress = 0.4f * site.Target;
        ulong key = Rng.StableHash(site.Id);
        for (int i = 0; i < site.Materials.Length; i++)
        {
            float u = 0.3f + 0.2f * Rng.Float(Rng.Hash(world.Seed, key, RngPurpose.Build, 1000 + (ulong)i));
            site.Materials[i].Delivered = MathF.Floor(site.Materials[i].Delivered * u + 0.5f);
        }
        world.Buildings.Changed();
    }

    // ------------------------------------------------------------------ materials

    /// <summary>One unit of <paramref name="record"/> in the inventory, then in its backpack (<b>Verified</b>, the order of <c>Task_AddMaterialsToBuilding</c>).</summary>
    static (ItemInstance Item, Inventory Owner)? Find(Inventory? inventory, string record)
    {
        if (inventory is null) return null;
        foreach (var item in inventory.Items)
            if (item.Record == record && item.Quantity > 0 && item.Contents is null) return (item, inventory);
        if (inventory.Backpack?.Contents is { } bag)
            foreach (var item in bag.Items)
                if (item.Record == record && item.Quantity > 0) return (item, bag);
        return null;
    }

    /// <summary>The character carries a unit of something the site still needs.</summary>
    static bool Carries(CharacterCold cold, BuildSite site)
    {
        foreach (var m in site.Materials)
            if (m.Required > m.Delivered && Find(cold.Inventory, m.Item) is not null) return true;
        return false;
    }

    static bool Outstanding(BuildSite site)
    {
        foreach (var m in site.Materials) if (m.Required > m.Delivered) return true;
        return false;
    }

    void DeliverStep(World world, int slot, CharacterCold cold, BuildSite site, float dt)
    {
        var table = world.Characters;
        cold.BuildBar += dt * BarRate;
        if (cold.BuildBar < 1) return;
        cold.BuildBar = 0;
        int given = 0;
        foreach (var m in site.Materials)
        {
            if (m.Required <= m.Delivered || Find(cold.Inventory, m.Item) is not { } found) continue;
            found.Owner.RemoveOne(found.Item);
            m.Delivered += 1;
            given++;
        }
        if (given > 0) world.Buildings.Touch();
        // Outstanding but nothing carried: back to the planner (here: the work ends). Otherwise on with building.
        if (given == 0 && Outstanding(site) && !site.Complete) End(table, slot);
        else table.Next[slot].Task = (byte)CharacterTask.Build;
    }

    // ------------------------------------------------------------------ dismantling

    void DismantleStep(World world, int slot, CharacterCold cold, BuildSite site, float dt)
    {
        if (site.Complete)
        {
            // A complete building drops to target - 1 first and stops being complete.
            site.Complete = false;
            site.Progress = MathF.Min(site.Progress, MathF.Max(site.Target - 1, 0));
            world.Buildings.Changed();
        }
        float amount = dt * Lerp(0.05f, 4f, Math.Clamp(Engineering(cold), 0, 100) / 100) * 4;
        site.Progress = site.Rate > 0 ? site.Progress - amount * site.Rate : 0;
        site.Dismantling = true;
        world.Buildings.Touch();
        SyncChildren(world, site);
        if (cold.Stats is { } stats && cold.Race is { } race) xp.Engineering(stats, race, dt);
        if (site.Progress > 0) return;
        Refund(world, cold, site);
        foreach (var c in world.Buildings.All)
            if (c.WallParent == site.Id) Refund(world, cold, c);   // nothing unless a save from before the link gave it materials
        world.EmitSound(SoundKind.DismantleDone, slot, world.Characters.Next[slot].Position);
        Remove(world, site);
    }

    /// <summary>
    /// The refund (<b>Verified</b>, <c>FUN_14029dc10</c>): each entry with something delivered gives <c>max(1, trunc(U[m, 1) x delivered))</c> items, m = CONSTANTS <c>min dismantle materials percentage</c> / 100.
    /// The original drops them on the ground; Meitou (engine choice, not yet moved to <see cref="World.GroundItems"/>) puts them into the dismantler's inventory (then its backpack) and what does not fit is lost with a message.
    /// </summary>
    void Refund(World world, CharacterCold dismantler, BuildSite site)
    {
        float m = constants.MinDismantleMaterialsFraction;
        ulong key = Rng.StableHash(site.Id);
        for (int i = 0; i < site.Materials.Length; i++)
        {
            var material = site.Materials[i];
            if (material.Delivered <= 0) continue;
            float u = m + (1 - m) * Rng.Float(Rng.Hash(world.Seed, key, RngPurpose.Build, (ulong)i));
            int count = Math.Max(1, (int)(u * material.Delivered));
            if (items.Create(material.Item, count) is not { } item) continue;
            if (dismantler.Inventory is { } inventory && inventory.Add(item)) continue;
            if (dismantler.IsPlayer) world.Post($"{dismantler.Name}: no room for {item.Quantity} {item.Name}; lost");
        }
    }

    // ------------------------------------------------------------------ the auto-job

    void Auto(World world)
    {
        var reg = world.Buildings;
        if (Settings.AutoEveryTicks <= 0 || world.Tick % Settings.AutoEveryTicks != 0 || !reg.AnyIncomplete) return;
        var table = world.Characters;
        var next = table.Next;
        var working = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < table.HighWater; i++)
            if (next[i].Alive && IsWork(next[i].Task) && table.Cold(i)!.BuildSite is { } id) working[id] = working.GetValueOrDefault(id) + 1;
        for (int i = 0; i < table.HighWater; i++)
        {
            ref var n = ref next[i];
            if (!n.Alive || n.Task is not ((byte)CharacterTask.Idle or (byte)CharacterTask.Wander or (byte)CharacterTask.Follow)) continue;
            var cold = table.Cold(i)!;
            if (!cold.IsPlayer || !Orders.Has(cold, CharacterOrders.Jobs) || cold.Race is null || cold.Stats is null || !Able(cold)) continue;
            BuildSite? best = null;
            float bestScore = 0;
            foreach (var site in reg.All)
            {
                if (site.Complete || site.Paused || site.Destroyed || site.Dismantling || site.WallParent is not null || site.Owner != cold.Faction) continue;
                if (site.Stalled && !Carries(cold, site)) continue;
                float dx = n.Position.X - site.Position.X, dz = n.Position.Z - site.Position.Z;
                float d2 = dx * dx + dz * dz;
                float score = MathF.Max(2 * (1 - d2 / (9000f * 9000f)), 0.0001f) / (working.GetValueOrDefault(site.Id) + 1);
                if (best is null || score > bestScore)
                {
                    best = site;
                    bestScore = score;
                }
            }
            if (best is null) continue;
            working[best.Id] = working.GetValueOrDefault(best.Id) + 1;
            var resume = n.Task is (byte)CharacterTask.Wander or (byte)CharacterTask.Follow ? (CharacterTask)n.Task : CharacterTask.Idle;
            Begin(world, i, best, CharacterTask.Build, resume);
        }
    }
}
