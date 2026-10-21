using System.Numerics;
using Meitou.Data;
using Meitou.Data.Characters;
using Meitou.Data.Gameplay;
using Meitou.Data.World;
using Meitou.Engine;
using Meitou.Engine.Time;
using Meitou.Net;
using Meitou.Net.Events;
using Meitou.Net.Replication;
using Meitou.Net.Session;
using Meitou.Net.Transport;
using Meitou.Simulation;
using Meitou.Tests.Simulation;

namespace Meitou.Tests.Net;

/// <summary>
/// A host and its clients in one process (docs/net.md, "Host", "Client and jitter buffer"): the host's local session runs the world, each guest a replica
/// session, all over one <see cref="LoopbackNetwork"/> on a <see cref="ManualNetClock"/>. A frame is the game's order: host BeginFrame, Advance,
/// EndFrame, then each guest's BeginFrame, Advance (control ticks only) and EndFrame.
/// </summary>
sealed class SessionHarness : IDisposable
{
    public const double FrameSeconds = 1 / 30.0;
    static readonly DataSetIdentity Data = new([new("gamedata.base", 1, 100)]);
    public static readonly AnimationLibrary Animations = new([]);

    public sealed class Game : IHostGame
    {
        public GateOptions Gate { get; set; } = new();
        public SessionRules Rules { get; set; } = new(3, false, true, true, false, true, false, false, false);
        public WeatherSync? Weather() => null;
    }

    sealed class NoLooks : IAppearanceSource
    {
        public CharacterAppearance? Create(string recordId, string? factionId, int seed) => null;
    }

    /// <summary>One client and its replica session, with what it was told.</summary>
    public sealed class Guest : IDisposable
    {
        public required NetClient Client { get; init; }
        public required WorldSession Session { get; init; }
        public required Guid Id { get; init; }
        public WorldReset? Reset { get; set; }
        public List<string> Errors { get; } = [];
        public List<string> Messages { get; } = [];
        public List<(ClockChange Change, string Name)> ClockChanges { get; } = [];
        public List<NoticeNet> Notices { get; } = [];
        /// <summary>The camera this guest reports every frame (null: above <see cref="SyntheticTown.Centre"/>, 8000 draw distance).</summary>
        public ViewState? View { get; set; }
        public int Player => Client.Player;
        public Meitou.Simulation.World World => Session.World;

        /// <summary>The UI's way: a command into the replica's queue, which <see cref="NetClient.EndFrame"/> sends.</summary>
        public void Say(SimCommand command) => World.Commands.Enqueue(command with { Tick = World.Tick });

        public void Dispose()
        {
            Client.Dispose();
            Session.Dispose();
        }
    }

    public readonly ManualNetClock Clock = new();
    public readonly LoopbackNetwork Network;
    public readonly WorldSession HostSession;
    public readonly NetIdentity Self = new(Guid.NewGuid(), "Hosty", "b1", Data);
    public readonly Game HostGame = new();
    public readonly NetHost Host;
    public readonly GameDatabase Database;
    public readonly List<Guest> Guests = [];
    public readonly List<(ClockChange Change, string Name)> HostClockChanges = [];
    /// <summary>What the host published for each player at each tick (taken after every EndFrame), for comparing with what the guests assembled.</summary>
    public readonly Dictionary<(int Player, long Tick), WorldSnapshot> Published = [];
    /// <summary>The host's population system when the harness built the host (<see cref="SyntheticTownHost"/>).</summary>
    public PopulationSystem? Population { get; private init; }
    readonly ReplicaCatalog catalog;

    public SessionHarness(WorldSession hostSession, GameDatabase database, NetHostOptions? options = null, int latencyMs = 10)
    {
        HostSession = hostSession;
        Database = database;
        Network = new LoopbackNetwork(new LoopbackOptions { LatencyMs = latencyMs }, Clock);
        catalog = new ReplicaCatalog(database, Animations, new NoLooks());
        Host = new NetHost(hostSession, Network.Server(), options ?? new NetHostOptions(), Self, HostGame, catalog, Clock);
        Host.ClockChanged += (c, n) => HostClockChanges.Add((c, n));
        Host.Start();
    }

    /// <summary>
    /// A SyntheticTown host: the population, player and movement systems, the host's start squad, the camera on it; publishing every tick.
    /// <paramref name="database"/> and <paramref name="towns"/> replace the synthetic town's records and placements.
    /// </summary>
    public static SessionHarness SyntheticTownHost(NetHostOptions? options = null, GameDatabase? database = null, IEnumerable<TownPlacement>? towns = null)
    {
        var db = database ?? SyntheticTown.Database();
        var data = towns is null ? SyntheticTown.Data(db) : PopulationData.Create(db, towns);
        var walk = new OpenGroundWalkability(SyntheticTown.Ground);
        var systems = SyntheticTown.Standard(data, walk, StandardParts.Population | StandardParts.Player | StandardParts.Movement);
        var session = new WorldSession(SyntheticTown.Centre, (-20000, -20000, 20000, 20000), SyntheticTown.Ground,
            simulation: new WorldSettings { Seed = 4, PublishSnapshots = true }, systems: systems.Systems, walkability: walk);
        session.World.Commands.Enqueue(new FocusCommand(SyntheticTown.Centre) { Tick = 0 });
        systems.Population!.StartPlayer(session.World, NewGameStart.From(db.Find("40-t")!));
        return new SessionHarness(session, db, options) { Population = systems.Population };
    }

    /// <summary>A guest that connects now; it joins over the next frames (its replica is built at the <c>WorldReset</c>, then Ready).</summary>
    public Guest Connect(string name, Guid? id = null, string? password = null)
    {
        var guestId = id ?? Guid.NewGuid();
        var client = new NetClient(Network.Client(), Self with { PlayerId = guestId, Name = name }, catalog, Clock);
        var guest = new Guest { Client = client, Session = new WorldSession(Vector3.Zero, (-20000, -20000, 20000, 20000), SyntheticTown.Ground), Id = guestId };
        client.WorldReset += reset =>
        {
            guest.Reset = reset;
            guest.Session.ReplaceWorld([], w => w.MakeReplica(reset.Seed), SessionMode.Replica);
            double hours = reset.Clock.AnchorHours;
            guest.Session.Clock.SetTime((long)Math.Floor(hours / 24), hours - Math.Floor(hours / 24) * 24);
            client.Attach(guest.Session);
            client.Ready();
        };
        client.Message += (text, error) => (error ? guest.Errors : guest.Messages).Add(text);
        client.ClockChanged += (c, n) => guest.ClockChanges.Add((c, n));
        client.Notice += guest.Notices.Add;
        client.Connect("localhost", NetProtocol.DefaultPort, password);
        Guests.Add(guest);
        return guest;
    }

    /// <summary>Connects a guest and runs frames until it shows a snapshot of its own.</summary>
    public Guest Join(string name, Guid? id = null, int maxFrames = 120)
    {
        var guest = Connect(name, id);
        RunUntil(() => guest.Client.HasPresented, maxFrames);
        return guest;
    }

    /// <summary>Runs <paramref name="frames"/> frames.</summary>
    public void Run(int frames)
    {
        for (int i = 0; i < frames; i++) Frame();
    }

    /// <summary>Runs frames until <paramref name="done"/> holds; fails after <paramref name="maxFrames"/>.</summary>
    public void RunUntil(Func<bool> done, int maxFrames = 300)
    {
        for (int i = 0; i < maxFrames; i++)
        {
            if (done()) return;
            Frame();
        }
        Assert.True(done(), $"not done after {maxFrames} frames");
    }

    void Frame()
    {
        Clock.Advance(TimeSpan.FromSeconds(FrameSeconds));
        Host.BeginFrame();
        HostSession.Advance(FrameSeconds);
        Host.EndFrame(false);
        var world = HostSession.World;
        foreach (var guest in Guests)
            if (guest.Client.State == NetClientState.Playing && guest.Player > 0)
                Published[(guest.Player, world.Tick)] = world.PublishFor(guest.Player);
        foreach (var guest in Guests)
        {
            if (guest.Client.State == NetClientState.Closed) continue;
            guest.Client.SetView(guest.View ?? new ViewState(SyntheticTown.Centre + new Vector3(0, 1500, 0), SyntheticTown.Centre, 8000));
            guest.Client.BeginFrame(FrameSeconds);
            if (guest.Session.Mode == SessionMode.Replica) guest.Session.Advance(FrameSeconds);
            guest.Client.EndFrame();
        }
    }

    /// <summary>The host world's characters player <paramref name="player"/> owns.</summary>
    public List<CharacterId> OwnedBy(int player) =>
        [.. HostSession.World.Snapshot.Characters.Where(c => c.IsPlayer && c.Owner == player).Select(c => c.Id)];

    public Vector3 HostPosition(CharacterId id) => HostSession.World.Characters.Previous[id.Slot].Position;

    public void Dispose()
    {
        foreach (var guest in Guests) guest.Dispose();
        Host.Dispose();
        HostSession.Dispose();
    }
}
