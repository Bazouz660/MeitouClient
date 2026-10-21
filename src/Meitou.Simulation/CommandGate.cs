using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Numerics;
using System.Reflection;
using Meitou.Data.Fcs;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Carrying;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Crime;
using Meitou.Simulation.Dialogue;
using Meitou.Simulation.Editing;
using Meitou.Simulation.FirstAid;
using Meitou.Simulation.Items;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Production;
using Meitou.Simulation.Trading;

namespace Meitou.Simulation;

/// <summary>The host's settings for the <see cref="CommandGate"/> (docs/simulation.md "Command gate").</summary>
public sealed record GateOptions
{
    /// <summary>The lobby's cheats option: remote players' <see cref="DebugCommand"/>s pass (on their own characters only).</summary>
    public bool Cheats { get; init; }
    /// <summary>The host's geometric placement check of a planned building (the simulation checks only the rest, <see cref="ConstructionSystem"/>); null: every plan passes it.</summary>
    public Func<PlannedBuilding, bool>? Placeable { get; init; }
    /// <summary>The longest character or plan list, the most plans in one <see cref="PlaceBuildings"/>, and the longest string a remote command may carry.</summary>
    public int MaxList { get; init; } = 256;
    public int MaxPlans { get; init; } = 64;
    public int MaxText { get; init; } = 256;
}

/// <summary>What the gate made of a remote command: the command to enqueue (restamped, maybe filtered or stripped), or why it was refused (<see cref="Command"/> null).</summary>
public readonly record struct GateResult(SimCommand? Command, string? Refusal);

/// <summary>
/// The host's trust boundary for remote players' commands (docs/simulation.md "Command gate"): each command type has a rule, an unknown type is refused, and an admitted
/// command is restamped with its sender's number and the current tick, so a client can speak only for itself and act only on its own characters, squads and windows. It only
/// reads the world: it never adds a session or uses up a grant. Host thread, between ticks, before <see cref="CommandQueue.Enqueue"/>; the host's own and deferred commands
/// never pass it.
/// </summary>
public static class CommandGate
{
    public const string UnknownPlayer = "Unknown player.", NoGame = "No game is running.", NotAllowed = "That command is not allowed.",
        HostOnlyText = "Only the host can do that.", NoCheats = "The host has not allowed cheats.", BadNumber = "Invalid number.", TooLong = "Too much in one command.",
        NotYours = "Not your characters.", NotYourCharacter = "That character is not yours.", Gone = "That character is gone.", NoPlace = "That is not there.",
        NotYourSquad = "Not your squad.", NotYourBuilding = "Not your building.", BadValue = "Invalid command.", CannotPlace = "That cannot be placed there.",
        NoGrant = "That character cannot be edited.";

    /// <summary>The highest <see cref="PlannedBuilding.Floor"/> a plan may name.</summary>
    public const int MaxFloor = 16;
    /// <summary>The <see cref="QueueCraft.Count"/> range a remote craft order is clamped to.</summary>
    public const int MaxCraftCount = 999;

    delegate GateResult Rule(Check c, SimCommand command);

    static readonly FrozenSet<Type> hostOnly = new[] { typeof(RepathCommand), typeof(SandboxMovement), typeof(StartContract), typeof(ChangeAi), typeof(Dialogue.DialogueEvent), typeof(JoinPlayer), typeof(TransferSquad) }.ToFrozenSet();
    static readonly FrozenDictionary<Type, Rule> rules = Rules();
    static readonly ConcurrentDictionary<Type, PropertyInfo[]> shapes = new();

    /// <summary>Every command type with a rule: every concrete <see cref="SimCommand"/> of this assembly (a test enumerates them). Any other type is refused.</summary>
    public static IReadOnlySet<Type> Ruled { get; } = rules.Keys.ToFrozenSet();

    /// <summary>Whether a remote player may never send <paramref name="type"/> (the host's own bookkeeping: repaths, the sandbox, contracts and AI changes, dialogue events, joins, squad transfers). <see cref="DebugCommand"/>s are not: they pass with <see cref="GateOptions.Cheats"/>.</summary>
    public static bool HostOnly(Type type) => hostOnly.Contains(type);

    /// <summary>
    /// Admits remote player <paramref name="player"/>'s <paramref name="command"/>: the result's command is it restamped <c>with { Player = player, Tick = world.Tick }</c>,
    /// possibly with foreign characters filtered out of its list or a trust field stripped (<see cref="AttackOrder.Defensive"/>, an unmatched <see cref="EditCharacter.Paid"/>),
    /// or a refusal. The common checks come first: a known player of a running game, every number finite, lists and strings within <paramref name="options"/>' limits.
    /// </summary>
    public static GateResult Admit(World world, int player, SimCommand command, GateOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(command);
        options ??= new GateOptions();
        if (world.Player.Find(player) is not { } session) return No(UnknownPlayer);
        if (!world.Player.Exists) return No(NoGame);
        if (!rules.TryGetValue(command.GetType(), out var rule)) return No(NotAllowed);
        if (hostOnly.Contains(command.GetType())) return No(HostOnlyText);
        if (command is DebugCommand && !options.Cheats) return No(NoCheats);
        if (Shape(command, options, 0) is { } bad) return No(bad);
        var result = rule(new Check(world, player, session, options), command);
        return result.Command is { } admitted ? new GateResult(admitted with { Player = player, Tick = world.Tick }, null) : result;
    }

    static GateResult Ok(SimCommand command) => new(command, null);
    static GateResult No(string reason) => new(null, reason);

    /// <summary>
    /// The common value checks over every public property of a command (and of the records it carries: item tokens, plans): floats and vectors finite, strings at most
    /// <see cref="GateOptions.MaxText"/>, lists at most <see cref="GateOptions.MaxList"/>. Read by reflection so a field added later is covered too.
    /// </summary>
    static string? Shape(object value, GateOptions options, int depth)
    {
        if (depth > 4) return BadValue;
        foreach (var property in shapes.GetOrAdd(value.GetType(), PropertiesOf))
            if (Field(property.GetValue(value), options, depth) is { } bad) return bad;
        return null;
    }

    static PropertyInfo[] PropertiesOf(Type type) =>
        [.. type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetIndexParameters().Length == 0 && p.DeclaringType != typeof(SimCommand))];

    static string? Field(object? v, GateOptions options, int depth) => v switch
    {
        null => null,
        float f => float.IsFinite(f) ? null : BadNumber,
        double d => double.IsFinite(d) ? null : BadNumber,
        Vector2 v2 => Finite(v2.X, v2.Y) ? null : BadNumber,
        Vector3 v3 => Finite(v3.X, v3.Y, v3.Z) ? null : BadNumber,
        Vector4 v4 => Finite(v4.X, v4.Y, v4.Z, v4.W) ? null : BadNumber,
        Quaternion q => Finite(q.X, q.Y, q.Z, q.W) ? null : BadNumber,
        string s => s.Length <= options.MaxText ? null : TooLong,
        IEnumerable list => Items(list, options, depth),
        _ when Carried(v.GetType()) => Shape(v, options, depth + 1),
        _ => null,
    };

    static string? Items(IEnumerable list, GateOptions options, int depth)
    {
        int count = 0;
        foreach (var item in list)
        {
            if (++count > options.MaxList) return TooLong;
            if (Field(item, options, depth) is { } bad) return bad;
        }
        return null;
    }

    /// <summary>A record a command carries whose fields are checked too: a class of this assembly (<see cref="InventoryItemToken"/>, <see cref="PlannedBuilding"/>).</summary>
    static bool Carried(Type type) => type.IsClass && type.Assembly == typeof(SimCommand).Assembly;

    static bool Finite(params ReadOnlySpan<float> values)
    {
        foreach (float f in values)
            if (!float.IsFinite(f)) return false;
        return true;
    }

    /// <summary>What a rule reads: the world, the sender and their session (never changed), the options.</summary>
    sealed class Check(World world, int player, PlayerSession session, GateOptions options)
    {
        public World World => world;
        public PlayerSession Session => session;
        public GateOptions Options => options;

        /// <summary>The character is the sender's (<see cref="World.OwnerOf"/>).</summary>
        public bool Owned(CharacterId id) => world.OwnerOf(id) == player;

        /// <summary>The character is alive (as <see cref="World.OwnerOf"/> resolves it).</summary>
        public bool Resolvable(CharacterId id) =>
            !id.IsNone && (world.Characters.Phase == TablePhase.Outside ? world.Characters.IsAlive(id) : world.Characters.TryResolveNext(id, out _));

        /// <summary>Alive and not of the player faction (someone to talk or trade with).</summary>
        public bool Other(CharacterId id) => Resolvable(id) && world.Characters.Cold(id.Slot) is { IsPlayer: false };

        /// <summary>An order's characters: an empty list stays empty (the issuer's selection is theirs by invariant); a listed one keeps the sender's characters, null when none is.</summary>
        public IReadOnlyList<CharacterId>? Actors(IReadOnlyList<CharacterId> listed)
        {
            if (listed.Count == 0) return listed;
            var owned = listed.Where(Owned).ToArray();
            return owned.Length == 0 ? null : owned.Length == listed.Count ? listed : owned;
        }

        /// <summary>A building of the player faction (<see cref="BuildingRegistry"/>).</summary>
        public bool PlayerSite(string site) => world.Buildings.Find(site) is { } b && b.Owner == world.Player.Faction;
        public bool Machine(string site) => world.Machines.Find(site) is not null;
        /// <summary>
        /// A natural rock's site id (<see cref="Production.ProductionSystem.NaturalId"/>) that names a rock (<see cref="Production.ProductionSystem.IsRock"/>): its machine is made at the first
        /// order. Without a production system or rock source no id passes.
        /// </summary>
        public bool Rock(string site) =>
            Production.ProductionSystem.IsNatural(site) && world.Systems.OfType<Production.ProductionSystem>().FirstOrDefault() is { } production && production.IsRock(site);
        public bool Container(string fixture) => world.Fixtures.Find(fixture) is { Contents: not null };

        /// <summary>One of the sender's player squads (of the player faction, not a platoon's active half).</summary>
        public bool OwnSquad(int id) => world.Squads.Find(id) is { } s && s.Faction == world.Player.Faction && s.PlatoonId < 0 && s.Owner == player;
    }

    /// <summary>An order with a character list: filtered to the sender's characters, refused when none is left.</summary>
    static GateResult Listed<T>(Check c, T command, IReadOnlyList<CharacterId> listed, Func<T, IReadOnlyList<CharacterId>, T> with) where T : SimCommand =>
        c.Actors(listed) is not { } actors ? No(NotYours) : Ok(ReferenceEquals(actors, listed) ? command : with(command, actors));

    /// <summary>An order with a character list and a target character, which must be alive.</summary>
    static GateResult Targeted<T>(Check c, T command, IReadOnlyList<CharacterId> listed, CharacterId target, Func<T, IReadOnlyList<CharacterId>, T> with) where T : SimCommand =>
        c.Resolvable(target) ? Listed(c, command, listed, with) : No(Gone);

    /// <summary>An order with a character list and a place (a door, a container, a ground item, a building), which must be there.</summary>
    static GateResult Placed<T>(Check c, T command, IReadOnlyList<CharacterId> listed, bool place, Func<T, IReadOnlyList<CharacterId>, T> with) where T : SimCommand =>
        place ? Listed(c, command, listed, with) : No(NoPlace);

    /// <summary>A command about one character, which must be the sender's.</summary>
    static GateResult One(Check c, SimCommand command, CharacterId character) => c.Owned(character) ? Ok(command) : No(NotYourCharacter);

    /// <summary>A bought item's cell section: none (wherever it fits) or one of the two grids.</summary>
    static bool Grid(string? section) => section is null or "main" or "backpack_content";

    static FrozenDictionary<Type, Rule> Rules()
    {
        var r = new Dictionary<Type, Rule>();
        void On<T>(Func<Check, T, GateResult> rule) where T : SimCommand => r.Add(typeof(T), (c, command) => rule(c, (T)command));

        // Host only: the gate refuses them before the rule (HostOnly).
        On<RepathCommand>((_, _) => No(HostOnlyText));
        On<SandboxMovement>((_, _) => No(HostOnlyText));
        On<StartContract>((_, _) => No(HostOnlyText));
        On<ChangeAi>((_, _) => No(HostOnlyText));
        On<Dialogue.DialogueEvent>((_, _) => No(HostOnlyText));
        On<JoinPlayer>((_, _) => No(HostOnlyText));
        On<TransferSquad>((_, _) => No(HostOnlyText));

        // Cheats, only with GateOptions.Cheats (checked before the rule): the sender's characters only.
        On<DebugMoney>((_, x) => Ok(x));
        On<DebugTeleport>((c, x) => Listed(c, x, x.Characters, (y, l) => y with { Characters = l }));
        On<DebugHeal>((c, x) => Listed(c, x, x.Characters, (y, l) => y with { Characters = l }));
        On<DebugMaxSkills>((c, x) => Listed(c, x, x.Characters, (y, l) => y with { Characters = l }));
        On<DebugDamage>((c, x) => Listed(c, x, x.Characters, (y, l) => y with { Characters = l }));
        On<DebugGiveItem>((c, x) => One(c, x, x.Character));
        On<DebugSpawn>((_, x) => Ok(x));

        // Stamp only: the sender's own windows and view, or shared faction state the systems filter themselves.
        On<SeenDiscoveriesCommand>((_, x) => Ok(x));
        On<SquadAiCommand>((_, x) => Ok(x with { Option = x.Option & SquadAi.Defaults }));
        On<InspectCommand>((c, x) => x.Target.IsNone || c.Resolvable(x.Target) ? Ok(x) : No(Gone));
        On<InspectBuildingCommand>((_, x) => Ok(x));
        On<CloseContainer>((_, x) => Ok(x));
        On<CloseTrade>((_, x) => Ok(x));
        On<ArrangeStock>((_, x) => Ok(x));
        On<EndConversation>((_, x) => Ok(x));
        On<FinishCreation>((_, x) => Ok(x));
        On<DialogueChoice>((_, x) => x.Index >= 0 ? Ok(x) : No(BadValue));
        On<TakeLootItem>((_, x) => x.Index >= 0 ? Ok(x) : No(BadValue));
        On<FocusCommand>((_, x) => Ok(x));
        On<QueueResearch>((_, x) => x.Research.Length > 0 ? Ok(x) : No(BadValue));
        On<CancelResearch>((_, x) => x.Research.Length > 0 ? Ok(x) : No(BadValue));
        On<ToggleDoorsOrder>((_, x) => Ok(x));
        On<ToggleDoorLocksOrder>((_, x) => Ok(x));

        // The sender's characters.
        On<MoveOrder>((c, x) => Listed(c, x, x.Characters, (y, l) => y with { Characters = l }));
        On<StopCommand>((c, x) => Listed(c, x, x.Characters, (y, l) => y with { Characters = l }));
        On<OrdersCommand>((c, x) => Listed(c, x, x.Characters, (y, l) => y with { Characters = l }));
        On<RunSpeedCommand>((c, x) => Listed(c, x, x.Characters, (y, l) => y with { Characters = l }));
        On<PutDownOrder>((c, x) => Listed(c, x, x.Carriers, (y, l) => y with { Carriers = l }));
        On<RescueJobCommand>((c, x) => Listed(c, x, x.Characters, (y, l) => y with { Characters = l }));
        On<RestOrder>((c, x) => Listed(c, x, x.Characters, (y, l) => y with { Characters = l }));
        On<MedicJobCommand>((c, x) => Listed(c, x, x.Characters, (y, l) => y with { Characters = l }));
        On<GetOutOrder>((c, x) => Listed(c, x, x.Characters, (y, l) => y with { Characters = l }));
        On<PutInCageOrder>((c, x) => Listed(c, x, x.Carriers, (y, l) => y with { Carriers = l }));

        // The sender's characters and a living target.
        On<PickUpOrder>((c, x) => Targeted(c, x, x.Carriers, x.Target, (y, l) => y with { Carriers = l }));
        On<LootOrder>((c, x) => Targeted(c, x, x.Looters, x.Target, (y, l) => y with { Looters = l }));
        On<OpenLootOrder>((c, x) => Targeted(c, x, x.Looters, x.Target, (y, l) => y with { Looters = l }));
        On<StealthKoOrder>((c, x) => Targeted(c, x, x.Attackers, x.Target, (y, l) => y with { Attackers = l }));
        On<TreatOrder>((c, x) => Targeted(c, x, x.Medics, x.Patient, (y, l) => y with { Medics = l }));
        On<SplintOrder>((c, x) => Targeted(c, x, x.Medics, x.Patient, (y, l) => y with { Medics = l }));
        On<PickLockOrder>((c, x) => Targeted(c, x, x.Characters, x.Prisoner, (y, l) => y with { Characters = l }));
        On<PutShacklesOrder>((c, x) => Targeted(c, x, x.Characters, x.Wearer, (y, l) => y with { Characters = l }));
        On<ShackleOrder>((c, x) => Enum.IsDefined(x.Work) ? Targeted(c, x, x.Characters, x.Wearer, (y, l) => y with { Characters = l }) : No(BadValue));

        // An empty select (a click on the ground) keeps the selection in the simulation, so it passes like an empty order list rather than raising a refusal at every click.
        On<SelectCommand>((c, x) => Listed(c, x, x.Characters, (y, l) => y with { Characters = l }));
        // No selection fallback exists for an attack; a remote player's fight is never a defence (only the retaliation system makes those).
        On<AttackOrder>((c, x) => x.Attackers.Count == 0 ? No(NotYours) : Targeted(c, x with { Defensive = false }, x.Attackers, x.Target, (y, l) => y with { Attackers = l }));

        // The sender's characters and a place.
        On<DoorOrder>((c, x) => Placed(c, x, x.Characters, c.World.Fixtures.Find(x.Door) is { Kind: FixtureKind.Door }, (y, l) => y with { Characters = l }));
        On<OpenContainerOrder>((c, x) => Placed(c, x, x.Characters, c.Container(x.Fixture), (y, l) => y with { Characters = l }));
        On<PickUpItemOrder>((c, x) => Placed(c, x, x.Characters, c.World.GroundItems.Find(x.Item) is not null, (y, l) => y with { Characters = l }));
        On<OperateOrder>((c, x) => Placed(c, x, x.Characters, c.Machine(x.Site) || c.PlayerSite(x.Site) || c.Rock(x.Site), (y, l) => y with { Characters = l }));
        On<AddJob>((c, x) => !Enum.IsDefined(x.Type) ? No(BadValue) : Placed(c, x, x.Characters, c.Machine(x.Site) || c.PlayerSite(x.Site) || c.Rock(x.Site), (y, l) => y with { Characters = l }));
        On<BuildOrder>((c, x) => c.PlayerSite(x.Site) ? Listed(c, x, x.Characters, (y, l) => y with { Characters = l }) : No(NotYourBuilding));
        On<DismantleOrder>((c, x) => c.PlayerSite(x.Site) ? Listed(c, x, x.Characters, (y, l) => y with { Characters = l }) : No(NotYourBuilding));

        // One of the sender's characters.
        On<TalkOrder>((c, x) => c.Other(x.Target) ? One(c, x, x.Talker) : No(Gone));
        On<OpenTrade>((c, x) => c.Other(x.Trader) ? One(c, x, x.Customer) : No(Gone));
        On<MoveInventoryItem>((c, x) => One(c, x, x.Character));
        On<ArrangeInventory>((c, x) => One(c, x, x.Character));
        On<DropInventoryItem>((c, x) => One(c, x, x.Character));
        // The sender's open loot window: its looter (the carry system checks the window is the sender's and still open).
        On<PutLootItem>((c, x) => One(c, x, x.Character));
        On<ReadMapItem>((c, x) => One(c, x, x.Character));
        On<RemoveJob>((c, x) => One(c, x, x.Character));
        On<MoveJob>((c, x) => One(c, x, x.Character));
        On<ProspectOrder>((c, x) => One(c, x, x.Character));
        On<DismissCommand>((c, x) => One(c, x, x.Character));
        On<PutInMachine>((c, x) => c.Machine(x.Site) || c.Container(x.Site) ? One(c, x, x.Character) : No(NoPlace));
        On<TakeFromMachine>((c, x) => c.Machine(x.Site) || c.Container(x.Site) ? One(c, x, x.Character) : No(NoPlace));

        // The sender's trade window (the trade system checks the rest).
        On<BuyItem>((_, x) => x.Quantity >= 1 && Grid(x.Section) ? Ok(x) : No(BadValue));
        On<SellItem>((_, x) => x.Quantity >= 1 ? Ok(x) : No(BadValue));

        // The faction's sites and benches.
        On<CancelSite>((c, x) => c.PlayerSite(x.Site) ? Ok(x) : No(NotYourBuilding));
        On<PauseSite>((c, x) => c.PlayerSite(x.Site) ? Ok(x) : No(NotYourBuilding));
        On<QueueCraft>((c, x) => c.Machine(x.Site) ? Ok(x with { Count = Math.Clamp(x.Count, 1, MaxCraftCount) }) : No(NoPlace));
        On<CancelCraft>((c, x) => c.Machine(x.Site) ? Ok(x) : No(NoPlace));
        On<RepeatCrafts>((c, x) => c.Machine(x.Site) ? Ok(x) : No(NoPlace));
        On<PlaceBuildings>((c, x) =>
        {
            if (x.Plans.Count < 1 || x.Plans.Count > c.Options.MaxPlans) return No(TooLong);
            foreach (var plan in x.Plans)
            {
                if (plan.Floor < 0 || plan.Floor > MaxFloor || plan.Parent is { } parent && c.World.Buildings.Find(parent) is null) return No(BadValue);
                if (c.Options.Placeable?.Invoke(plan) == false) return No(CannotPlace);
            }
            return Ok(x);
        });

        // The sender's squads.
        On<NewSquadCommand>((_, x) => Ok(x));
        On<RenameSquadCommand>((c, x) => c.OwnSquad(x.Squad) ? Ok(x) : No(NotYourSquad));
        On<RemoveSquadCommand>((c, x) => c.OwnSquad(x.Squad) ? Ok(x) : No(NotYourSquad));
        On<ReorderSquadCommand>((c, x) => c.OwnSquad(x.Squad) ? Ok(x) : No(NotYourSquad));
        On<ActiveSquadCommand>((c, x) => x.Squad == -1 || c.OwnSquad(x.Squad) ? Ok(x) : No(NotYourSquad));
        On<MoveToSquadCommand>((c, x) => !c.OwnSquad(x.Squad) ? No(NotYourSquad) : One(c, x, x.Character));

        On<EditCharacter>(Edit);
        On<PayBailOrder>((c, x) => c.Resolvable(x.Prisoner) ? Ok(x) : No(Gone));
        return r.ToFrozenDictionary();
    }

    /// <summary>
    /// An editor's result: one of the sender's characters, a finite look. A surgery's <see cref="EditCharacter.Paid"/> stands only with the session's matching grant (peeked: the
    /// simulation uses it up); a recruit's edit needs its grant; a creation's <see cref="PlayerSession.InCreation"/> is the simulation's check.
    /// </summary>
    static GateResult Edit(Check c, EditCharacter x)
    {
        if (!Enum.IsDefined(x.Kind) || x.Appearance is null || !FiniteLook(x.Appearance)) return No(BadValue);
        if (!c.Owned(x.Character)) return No(NotYourCharacter);
        switch (x.Kind)
        {
            case EditKind.Surgery:
                if (!c.Resolvable(x.Surgeon)) return No(Gone);
                return x.Paid && c.Session.Grant != new EditGrant(EditKind.Surgery, x.Character, x.Surgeon) ? Ok(x with { Paid = false }) : Ok(x);
            case EditKind.Recruit:
                return c.Session.Grant == new EditGrant(EditKind.Recruit, x.Character, CharacterId.None) ? Ok(x) : No(NoGrant);
            default:
                return Ok(x);
        }
    }

    static bool FiniteLook(FcsRecord look)
    {
        foreach (var (_, f) in look.Floats)
            if (!float.IsFinite(f)) return false;
        foreach (var (_, v) in look.Vector3s)
            if (!Finite(v.X, v.Y, v.Z)) return false;
        foreach (var (_, v) in look.Vector4s)
            if (!Finite(v.X, v.Y, v.Z, v.W)) return false;
        foreach (var instance in look.Instances)
            if (!Finite(instance.Position.X, instance.Position.Y, instance.Position.Z, instance.Rotation.X, instance.Rotation.Y, instance.Rotation.Z, instance.Rotation.W)) return false;
        return true;
    }
}
