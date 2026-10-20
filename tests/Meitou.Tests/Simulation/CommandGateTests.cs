using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Simulation;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Editing;
using Meitou.Simulation.Items;
using Meitou.Simulation.Trading;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

/// <summary>The host's command gate for remote players (docs/simulation.md "Command gate").</summary>
public class CommandGateTests
{
    /// <summary>The start squad (player 0's: its first two members) and a second squad of player 1's holding the rest, as in <see cref="MultiplayerSessionTests"/>.</summary>
    sealed record Game(SimWorld World, Squad Ours, Squad Theirs) : IDisposable
    {
        public void Dispose() => World.Dispose();
        public PlayerSession Guest => World.Player.Find(1)!;
        public GateResult Admit(SimCommand command, GateOptions? options = null) => CommandGate.Admit(World, 1, command, options);
    }

    static Game Start()
    {
        var db = SyntheticTown.Database();
        var data = SyntheticTown.Data(db);
        var walk = new OpenGroundWalkability(SyntheticTown.Ground);
        var systems = SyntheticTown.Standard(data, walk, StandardParts.Population | StandardParts.Player | StandardParts.Movement, new PopulationSettings { CheckEveryTicks = 1, UnloadGraceSeconds = 5 });
        var world = new SimWorld(new WorldSettings { Seed = 4, PublishSnapshots = false }, walk, systems.Systems);
        var ours = systems.Population!.StartPlayer(world, NewGameStart.From(db.Find("40-t")!));
        world.Commands.Enqueue(new FocusCommand(new Vector3(-90000, 0, -90000)) { Tick = 0 });
        world.Player.Join(1, Guid.NewGuid(), "Guest");
        var theirs = SquadManagement.NewSquad(world, db, world.Characters.Previous[ours.Leader.Slot].Position, "Nameless", owner: 1);
        foreach (var id in ours.Members.Skip(2).ToList())
        {
            ours.Members.Remove(id);
            theirs.Members.Add(id);
            world.Characters.Cold(id.Slot)!.SquadId = theirs.Id;
        }
        theirs.Leader = theirs.Members[0];
        return new Game(world, ours, theirs);
    }

    [Fact]
    public void Every_concrete_command_type_has_a_rule()
    {
        var types = typeof(SimCommand).Assembly.GetTypes().Where(t => t.IsSubclassOf(typeof(SimCommand)) && !t.IsAbstract).ToList();
        Assert.NotEmpty(types);
        Assert.Empty(types.Where(t => !CommandGate.Ruled.Contains(t)).Select(t => t.FullName));
        Assert.True(CommandGate.HostOnly(typeof(JoinPlayer)));
        Assert.True(CommandGate.HostOnly(typeof(TransferSquad)));
        Assert.False(CommandGate.HostOnly(typeof(MoveOrder)));
    }

    [Fact]
    public void Foreign_characters_are_filtered_out_or_refused_and_admitted_commands_are_restamped()
    {
        using var g = Start();
        g.World.RunTick();
        var (mine, guest) = (g.Ours.Members[0], g.Theirs.Members[0]);
        // A mixed list keeps the sender's characters; the sender and tick are the host's, whatever the client wrote.
        var moved = Assert.IsType<MoveOrder>(g.Admit(new MoveOrder([mine, guest], Vector3.Zero) { Tick = 999, Player = 0 }).Command);
        Assert.Equal([guest], moved.Characters);
        Assert.Equal((1, g.World.Tick), (moved.Player, moved.Tick));
        // An empty list stays empty: it means the sender's own selection.
        Assert.Empty(Assert.IsType<MoveOrder>(g.Admit(new MoveOrder([], Vector3.Zero) { Tick = 0 }).Command).Characters);
        // Nothing of the sender's left: refused with a reason.
        Assert.NotNull(g.Admit(new MoveOrder([mine], Vector3.Zero) { Tick = 0 }).Refusal);
        Assert.NotNull(g.Admit(new SelectCommand([mine]) { Tick = 0 }).Refusal);
        Assert.NotNull(g.Admit(new DismissCommand(mine) { Tick = 0 }).Refusal);
        Assert.NotNull(g.Admit(new RenameSquadCommand(g.Ours.Id, "Mine now") { Tick = 0 }).Refusal);
        Assert.NotNull(g.Admit(new MoveToSquadCommand(mine, g.Theirs.Id) { Tick = 0 }).Refusal);
        Assert.Null(g.Admit(new RenameSquadCommand(g.Theirs.Id, "Ours") { Tick = 0 }).Refusal);
    }

    [Fact]
    public void Host_only_commands_and_cheats_without_the_option_are_refused()
    {
        using var g = Start();
        SimCommand[] hostOnly = [new JoinPlayer(null, Vector3.Zero) { Tick = 0 }, new TransferSquad(g.Ours.Id, 1) { Tick = 0 }, new RepathCommand { Tick = 0 },
            new SandboxMovement(g.Theirs.Members[0], 1, 10) { Tick = 0 }, new StartContract(g.Theirs.Id, "x", 1) { Tick = 0 }];
        foreach (var command in hostOnly)
        {
            Assert.True(CommandGate.HostOnly(command.GetType()));
            Assert.NotNull(g.Admit(command, new GateOptions { Cheats = true }).Refusal);
        }
        var heal = new DebugHeal([g.Ours.Members[0], g.Theirs.Members[0]]) { Tick = 0 };
        Assert.NotNull(g.Admit(heal).Refusal);
        Assert.Equal([g.Theirs.Members[0]], Assert.IsType<DebugHeal>(g.Admit(heal, new GateOptions { Cheats = true }).Command).Characters);
    }

    [Fact]
    public void Defensive_and_an_unmatched_Paid_are_stripped_and_a_matching_grant_is_only_peeked()
    {
        using var g = Start();
        var (mine, guest) = (g.Ours.Members[0], g.Theirs.Members[0]);
        var attack = Assert.IsType<AttackOrder>(g.Admit(new AttackOrder([guest], mine) { Tick = 0, Defensive = true }).Command);
        Assert.False(attack.Defensive);

        var surgery = new EditCharacter(guest, new FcsRecord(), "Guest", EditKind.Surgery) { Tick = 0, Surgeon = mine, Paid = true };
        Assert.False(Assert.IsType<EditCharacter>(g.Admit(surgery).Command).Paid);
        var grant = new EditGrant(EditKind.Surgery, guest, mine);
        g.Guest.Grant = grant;
        Assert.True(Assert.IsType<EditCharacter>(g.Admit(surgery).Command).Paid);
        Assert.Equal(grant, g.Guest.Grant);
        // A grant for another character does not pay this one.
        Assert.False(Assert.IsType<EditCharacter>(g.Admit(surgery with { Character = g.Theirs.Members[1] }).Command).Paid);
    }

    [Fact]
    public void A_non_finite_number_anywhere_in_a_command_is_refused()
    {
        using var g = Start();
        var guest = g.Theirs.Members[0];
        Assert.NotNull(g.Admit(new MoveOrder([guest], new Vector3(float.NaN, 0, 0)) { Tick = 0 }).Refusal);
        Assert.NotNull(g.Admit(new FocusCommand(new Vector3(0, float.PositiveInfinity, 0)) { Tick = 0 }).Refusal);
        Assert.NotNull(g.Admit(new PlaceBuildings([new PlannedBuilding("wall", Vector3.Zero, new Quaternion(float.NaN, 0, 0, 1))]) { Tick = 0 }).Refusal);
        Assert.NotNull(g.Admit(new BuyItem(new InventoryItemToken(0, "item", "main", 0, 0, 1, float.NaN), 1) { Tick = 0 }).Refusal);
        Assert.Null(g.Admit(new FocusCommand(Vector3.One) { Tick = 0 }).Refusal);
    }

    [Fact]
    public void An_unknown_player_is_refused_and_gets_no_session()
    {
        using var g = Start();
        var result = CommandGate.Admit(g.World, 5, new FocusCommand(Vector3.One) { Tick = 0 });
        Assert.Null(result.Command);
        Assert.NotNull(result.Refusal);
        Assert.Null(g.World.Player.Find(5));
    }
}
