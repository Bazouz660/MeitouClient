using System.Numerics;
using System.Text;
using Meitou.Data.Characters;
using Meitou.Data.Gameplay;
using Meitou.Net;
using Meitou.Net.Replication;
using Meitou.Simulation;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Dialogue;
using Meitou.Simulation.Items;
using Meitou.Simulation.Locks;

namespace Meitou.Tests.Net;

/// <summary>
/// The delta layer end to end (docs/net.md, "Interest, delta, replicator and mirror"): a <see cref="ClientReplicator"/> feeding a <see cref="ClientMirror"/>
/// over a seeded link that drops, reorders and duplicates snapshot parts and client frames and holds views back, on synthetic snapshots. After a quiet
/// period the mirror must hold exactly the host's quantized view of the interest set and the host's views.
/// </summary>
public class ReplicationTests(ITestOutputHelper output)
{
    static readonly AnimationLibrary Library = new(
        [.. Enumerable.Range(0, 6).Select(i => new AnimationDefinition { Id = "a" + i, Name = "anim " + i, Clip = "clip" + i })]);

    static readonly ViewState Camera = new(Vector3.Zero, Vector3.Zero, 6000);

    sealed class NoLooks : IAppearanceSource, ILookResolver
    {
        public CharacterAppearance? Create(string recordId, string? factionId, int seed) => null;
        public CharacterAppearance? Resolve(uint lookId) => null;
        public void Define(uint lookId, AppearanceRecipe recipe) { }
    }

    /// <summary>
    /// A host-to-client link: Views is reliable and ordered but may be held back a round (parts then overtake it); Snapshots parts are lost, delayed to
    /// the next round (so older Seqs arrive after newer ones), duplicated and shuffled with the given odds.
    /// </summary>
    sealed class Link(int seed) : IPacketSink
    {
        readonly Random random = new(seed);
        readonly List<byte[]> parts = [], late = [], views = [];
        public double Loss, Reorder, Duplicate, ViewHold;
        public long SnapshotBytes, ViewBytes;
        public int Packets;

        public int MaxUnreliablePayload => 1200;

        /// <summary>The snapshot parts sent since the last <see cref="Deliver"/>.</summary>
        public IReadOnlyList<byte[]> Parts => parts;

        public void Send(NetChannel channel, ReadOnlySpan<byte> packet)
        {
            if (channel == NetChannel.Snapshots)
            {
                Assert.True(packet.Length <= MaxUnreliablePayload, $"a part of {packet.Length} bytes");
                parts.Add(packet.ToArray());
                SnapshotBytes += packet.Length;
                Packets++;
            }
            else
            {
                Assert.Equal(NetChannel.Views, channel);
                views.Add(packet.ToArray());
                ViewBytes += packet.Length;
            }
        }

        public void Deliver(ClientMirror mirror)
        {
            if (random.NextDouble() >= ViewHold)
            {
                foreach (var v in views) mirror.Receive(NetChannel.Views, v);
                views.Clear();
            }
            var batch = new List<byte[]>(late);
            late.Clear();
            foreach (var p in parts)
            {
                double roll = random.NextDouble();
                if (roll < Loss) continue;
                if (roll < Loss + Reorder) { late.Add(p); continue; }
                batch.Add(p);
                if (random.NextDouble() < Duplicate) batch.Add(p);
            }
            parts.Clear();
            for (int i = batch.Count - 1; i > 0 && Reorder > 0; i--)
            {
                int j = random.Next(i + 1);
                (batch[i], batch[j]) = (batch[j], batch[i]);
            }
            foreach (var p in batch) mirror.Receive(NetChannel.Snapshots, p);
        }
    }

    sealed class Person(CharacterId id, Vector3 position, string name)
    {
        public CharacterId Id = id;
        public Vector3 Position = position;
        public string Name = name;
        public string? Activity;
        public float Yaw, Time, Hunger = 3;
        public int Definition, Faction = 1;
        public bool IsPlayer, Selected;
    }

    /// <summary>A synthetic world: people walking, animating and being renamed, spawning into freed slots, bolts flying, and views that change now and then.</summary>
    sealed class Town
    {
        readonly Random random;
        public readonly SortedDictionary<int, Person> People = [];
        readonly Dictionary<int, int> generations = [];
        readonly List<(ProjectileSnapshot Bolt, int Age)> bolts = [];
        readonly List<BuildingSnapshot> buildings = [];
        readonly List<GroundItemSnapshot> ground = [];
        readonly List<SpeechBubble> speech = [];
        readonly Dictionary<(string Building, int Door), DoorView> doors = [];
        float[] relations = [0.5f, -20f, 3f];
        long buildingsRevision = 1, groundRevision = 1, relationsRevision = 1, money = 1000;
        int serial, boltSerial;
        public long Tick;

        public Town(int seed, int count, float spread)
        {
            random = new Random(seed);
            for (int slot = 0; slot < count; slot++)
            {
                var p = Spawn(slot, new Vector3(Next(spread), 0, Next(spread)));
                p.IsPlayer = slot % 10 == 0;
                p.Selected = slot == 0;
            }
            doors[("house 1", 0)] = new DoorView(0f, DoorMotion.Closed, false, false, false, new Vector3(5, 0, 5), "door");
        }

        float Next(float spread) => (float)(random.NextDouble() * 2 - 1) * spread;

        public Person Spawn(int slot, Vector3 at)
        {
            int generation = generations.TryGetValue(slot, out int g) ? g + 1 : 0;
            generations[slot] = generation;
            var p = new Person(new CharacterId(slot, generation), at, $"Person {serial++}") { Definition = serial % Library.Definitions.Count };
            People[slot] = p;
            return p;
        }

        public void Despawn(int slot) => People.Remove(slot);

        /// <summary>One tick: <paramref name="active"/> of the people move and animate; the rest of the world changes with small odds.</summary>
        public void Step(double active, bool churn = true)
        {
            Tick++;
            foreach (var p in People.Values)
            {
                if (random.NextDouble() >= active) continue;
                p.Position += new Vector3(Next(3), 0, Next(3));
                p.Yaw = Next(3);
                p.Time = (p.Time + 1 / 30f) % 2f;
                p.Hunger = Math.Max(0, p.Hunger - 0.01f);
                if (random.NextDouble() < 0.02) p.Activity = $"task {serial++}";
                if (random.NextDouble() < 0.01) p.Name = $"Renamed {serial++}";
            }
            foreach (var bolt in bolts.ToArray())
            {
                bolts.Remove(bolt);
                if (bolt.Age < 12) bolts.Add((bolt.Bolt with { Position = bolt.Bolt.Position + bolt.Bolt.Direction * 20, Stuck = bolt.Age == 11 }, bolt.Age + 1));
            }
            if (!churn) return;
            if (random.NextDouble() < 0.2)
                bolts.Add((new ProjectileSnapshot(boltSerial++, new Vector3(Next(500), 10, Next(500)), Vector3.Normalize(new Vector3(1, -0.1f, Next(1))), 1.4f, false, "bolt"), 0));
            if (random.NextDouble() < 0.05 && People.Count > 10) Despawn(People.Keys.ElementAt(1 + random.Next(People.Count - 1)));
            if (random.NextDouble() < 0.05)
            {
                int slot = Enumerable.Range(0, People.Count + 1).First(s => !People.ContainsKey(s));
                Spawn(slot, new Vector3(Next(3000), 0, Next(3000)));
            }
            if (random.NextDouble() < 0.05) money += random.Next(-50, 100);
            if (random.NextDouble() < 0.05)
            {
                buildings.Add(new BuildingSnapshot($"b{serial++}", "wall", new Vector3(Next(100), 0, Next(100)), Quaternion.Identity, 0, null, 0, 0, 10,
                    [new MaterialView("iron", 10, 0)], false, false));
                buildingsRevision++;
            }
            if (buildings.Count > 0 && random.NextDouble() < 0.1)
            {
                int i = random.Next(buildings.Count);
                buildings[i] = buildings[i] with { Progress = buildings[i].Progress + 1 };
            }
            if (random.NextDouble() < 0.05)
            {
                ground.Add(new GroundItemSnapshot(serial++, "item", $"Thing {serial}", new Vector3(Next(100), 0, Next(100)), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.5f), true));
                groundRevision++;
            }
            if (random.NextDouble() < 0.1)
            {
                speech.Clear();
                foreach (var p in People.Values.Take(3)) speech.Add(new SpeechBubble(p.Id, $"Line {serial++}", false));
            }
            if (random.NextDouble() < 0.05)
            {
                doors[("house 1", 0)] = doors[("house 1", 0)] with { Open = (float)random.NextDouble(), Motion = DoorMotion.Opening };
            }
            if (random.NextDouble() < 0.02)
            {
                relations = [.. relations.Select(r => r + 1)];
                relationsRevision++;
            }
        }

        public WorldSnapshot Publish()
        {
            var characters = new List<CharacterSnapshot>();
            foreach (var p in People.Values)
                characters.Add(new CharacterSnapshot(p.Id, null, p.Position, p.Yaw, [new AnimationLayer("anim " + p.Definition, p.Time, 1f) { Length = 2f, Definition = p.Definition }])
                {
                    Name = p.Name, Activity = p.Activity, Faction = p.Faction, IsPlayer = p.IsPlayer, Owner = p.IsPlayer ? 1 : -1, SquadId = p.IsPlayer ? 3 : -1,
                    Selected = p.Selected, Body = new BodyStatus(0.8f, 0.9f, p.Hunger, false, false),
                    Skills = p.Selected ? new SkillSummary(10, 11, 12, 13, 14, p.Hunger) : null,
                    Path = p.Selected ? [p.Position + new Vector3(100, 0, 0)] : [],
                });
            var players = characters.Where(c => c.IsPlayer).Select(c => c.Id).ToArray();
            return new WorldSnapshot(Tick, characters)
            {
                Projectiles = [.. bolts.Select(b => b.Bolt)],
                Viewer = 1, PlayerFaction = 1, Money = money, ActiveSquad = 3,
                PlayerSquads = [new SquadView(3, "Wanderers", players)],
                Towns = [TownKnowledge.Discovered, TownKnowledge.Explored],
                Buildings = [.. buildings], BuildingsRevision = buildingsRevision,
                GroundItems = [.. ground], GroundItemsRevision = groundRevision,
                Speech = [.. speech],
                Doors = new Dictionary<(string Building, int Door), DoorView>(doors),
                Relations = relations, RelationsRevision = relationsRevision,
                Talkable = new HashSet<CharacterId>(players.Take(2)),
            };
        }
    }

    /// <summary>The host replicator, the client mirror and the link between them, run one send at a time.</summary>
    sealed class Session
    {
        public readonly ClientReplicator Host;
        public readonly ClientMirror Mirror = new(Library, new NoLooks());
        public readonly Link Link;
        public readonly Random Uplink;
        public double FrameLoss;
        public WorldSnapshot? Latest;
        public int Sends;
        public readonly List<string> HostLogs = [], MirrorLogs = [];

        public Session(int seed, SnapshotEncoderOptions? options = null)
        {
            Host = new ClientReplicator(1, options ?? new SnapshotEncoderOptions(), new LookRegistry(new RecordingAppearances(new NoLooks())), Library) { Log = HostLogs.Add };
            Mirror.Log = MirrorLogs.Add;
            Link = new Link(seed);
            Uplink = new Random(seed + 1);
            Host.Reset(3);
            Mirror.Reset(3);
        }

        public void Round(WorldSnapshot view)
        {
            Host.Send(view, TimeSpan.FromMilliseconds(50 * Sends++), Link);
            Link.Deliver(Mirror);
            if (Mirror.TryAssemble(out var snapshot)) Latest = snapshot;
            using var w = new NetWriter();
            Mirror.WriteFrame(w, Camera);
            if (Uplink.NextDouble() < FrameLoss) return;
            var r = new NetReader(w.Written);
            Host.OnClientFrame(ClientFrameCodec.Read(ref r));
        }

        public void Quiet(Town town, int rounds)
        {
            (Link.Loss, Link.Reorder, Link.Duplicate, Link.ViewHold, FrameLoss) = (0, 0, 0, 0, 0);
            for (int i = 0; i < rounds; i++)
            {
                town.Tick++;
                Round(town.Publish());
            }
        }
    }

    static CharacterWire Wire(CharacterSnapshot c) => CharacterWire.Quantize(c, _ => 0u, Library);

    /// <summary>The canonical bytes of a view value (every string a literal).</summary>
    static byte[] Bytes<T>(ItemCodec<T> codec, T value)
    {
        using var w = new NetWriter();
        codec.Write(w, StringTableWriter.CreateLiteralOnly(), value);
        return w.Written.ToArray();
    }

    static byte[] Bytes<T>(ItemCodec<T> codec, IEnumerable<T> values) => [.. values.SelectMany(v => Bytes(codec, v))];

    /// <summary>The mirror holds exactly the host's quantized view of the interest set, its projectiles and its views.</summary>
    static void AssertMirrors(WorldSnapshot host, ClientReplicator replicator, WorldSnapshot? mirror)
    {
        Assert.NotNull(mirror);
        // A send with nothing to say is a keep-alive part only every SendHz / KeepAliveHz sends, so the newest Tick may lag.
        Assert.InRange(mirror.Tick, host.Tick - 3, host.Tick);
        var expected = host.Characters.Where(c => replicator.Interest.Contains(c.Id)).ToList();
        Assert.Equal(expected.Select(c => c.Id), mirror.Characters.Select(c => c.Id));
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.True(Wire(expected[i]).DiffMask(Wire(mirror.Characters[i])) == 0, $"{expected[i].Id} differs");
            Assert.Equal(Bytes(ViewCodecs.StatsCodec, ViewCodecs.StatsOf([expected[i]])), Bytes(ViewCodecs.StatsCodec, ViewCodecs.StatsOf([mirror.Characters[i]])));
            Assert.Equal(Bytes(ViewCodecs.PathCodec, ViewCodecs.PathOf([expected[i]])), Bytes(ViewCodecs.PathCodec, ViewCodecs.PathOf([mirror.Characters[i]])));
        }
        var bolts = host.Projectiles.OrderBy(p => p.Id).ToList();
        Assert.Equal(bolts.Select(p => p.Id), mirror.Projectiles.Select(p => p.Id));
        for (int i = 0; i < bolts.Count; i++) Assert.Equal(ProjectileWire.Quantize(bolts[i]), ProjectileWire.Quantize(mirror.Projectiles[i]));

        Assert.Equal(Bytes(ViewCodecs.HeaderCodec, HeaderView.From(host)), Bytes(ViewCodecs.HeaderCodec, HeaderView.From(mirror)));
        Assert.Equal(Bytes(ViewCodecs.TownsCodec, host.Towns), Bytes(ViewCodecs.TownsCodec, mirror.Towns));
        Assert.Equal(Bytes(ViewCodecs.SquadCodec, host.PlayerSquads), Bytes(ViewCodecs.SquadCodec, mirror.PlayerSquads));
        Assert.Equal(Bytes(ViewCodecs.Building, host.Buildings), Bytes(ViewCodecs.Building, mirror.Buildings));
        Assert.Equal(host.BuildingsRevision, mirror.BuildingsRevision);
        Assert.Equal(Bytes(ViewCodecs.GroundItem, host.GroundItems), Bytes(ViewCodecs.GroundItem, mirror.GroundItems));
        Assert.Equal(host.GroundItemsRevision, mirror.GroundItemsRevision);
        Assert.Equal(Bytes(ViewCodecs.SpeechCodec, host.Speech.Where(b => replicator.Interest.Contains(b.Speaker))), Bytes(ViewCodecs.SpeechCodec, mirror.Speech));
        Assert.Equal(host.Doors.OrderBy(d => d.Key).ToArray(), mirror.Doors.OrderBy(d => d.Key).ToArray());
        Assert.Equal(Bytes(ViewCodecs.RelationsCodec, RelationsView.From(host)), Bytes(ViewCodecs.RelationsCodec, RelationsView.From(mirror)));
        Assert.True(host.Talkable.SetEquals(mirror.Talkable));
    }

    [Fact]
    public void A_lossy_link_converges_to_the_hosts_quantized_view_and_views()
    {
        var town = new Town(7, 120, 7000);
        var session = new Session(11);
        (session.Link.Loss, session.Link.Reorder, session.Link.Duplicate, session.Link.ViewHold, session.FrameLoss) = (0.2, 0.15, 0.1, 0.3, 0.2);
        for (int i = 0; i < 300; i++)
        {
            town.Step(0.5);
            session.Round(town.Publish());
        }
        long lossyBytes = session.Link.SnapshotBytes, lossyViews = session.Link.ViewBytes;
        int lossyPackets = session.Link.Packets;
        session.Quiet(town, 40);

        AssertMirrors(town.Publish(), session.Host, session.Latest);
        Assert.Equal(0, session.Mirror.DroppedParts);
        Assert.Empty(session.HostLogs);
        // Some characters are beyond the draw distance and are not sent; players are sent wherever they are.
        Assert.True(session.Host.Interest.Count < town.People.Count);
        Assert.All(town.People.Values.Where(p => p.IsPlayer), p => Assert.Contains(p.Id, session.Host.Interest));
        output.WriteLine($"{town.People.Count} people, {session.Host.Interest.Count} in interest; {session.Mirror.SkippedRecords} records skipped and resent in full");
        output.WriteLine($"lossy: {lossyBytes / 300.0:F0} B of parts and {lossyViews / 300.0:F0} B of views per send, {lossyPackets / 300.0:F1} parts per send");
        output.WriteLine($"quiet: {(session.Link.SnapshotBytes - lossyBytes) / 40.0:F0} B of parts per send");
    }

    [Fact]
    public void Removals_slot_reuse_and_cancelled_removals_reach_the_mirror()
    {
        var town = new Town(3, 6, 500);
        var session = new Session(5);
        session.Quiet(town, 5);
        Assert.Equal(6, session.Latest!.Characters.Count);

        // A removal whose parts are lost is sent again until a part carrying it is acked.
        town.Despawn(1);
        session.Link.Loss = 1;
        town.Tick++;
        session.Round(town.Publish());
        town.Tick++;
        session.Round(town.Publish());
        Assert.Contains(session.Latest.Characters, c => c.Id.Slot == 1);
        session.Quiet(town, 3);
        Assert.DoesNotContain(session.Latest.Characters, c => c.Id.Slot == 1);

        // The freed slot comes back with the next generation: the new character replaces the old one.
        var newcomer = town.Spawn(1, new Vector3(20, 0, 20));
        newcomer.Name = "Newcomer";
        session.Quiet(town, 3);
        var reused = Assert.Single(session.Latest.Characters, c => c.Id.Slot == 1);
        Assert.Equal((new CharacterId(1, 1), "Newcomer"), (reused.Id, reused.Name));

        // Despawned and reused in the same send while parts are lost: the old generation's removal and the new record share a slot.
        town.Despawn(2);
        town.Spawn(2, new Vector3(-30, 0, 10)).Name = "Second";
        session.Link.Loss = 1;
        town.Tick++;
        session.Round(town.Publish());
        session.Quiet(town, 3);
        var second = Assert.Single(session.Latest.Characters, c => c.Id.Slot == 2);
        Assert.Equal((new CharacterId(2, 1), "Second"), (second.Id, second.Name));

        // Leaving the draw distance and coming back before the removal was acked: the removal is cancelled and a full record sent.
        var walker = town.People[3];
        var home = walker.Position;
        walker.Position = new Vector3(50_000, 0, 0);
        session.Link.Loss = 1;
        town.Tick++;
        session.Round(town.Publish());
        walker.Position = home + new Vector3(1, 0, 1);
        session.Quiet(town, 3);
        AssertMirrors(town.Publish(), session.Host, session.Latest);
        Assert.Equal(0, session.Mirror.DroppedParts);
    }

    [Fact]
    public void A_lost_string_definition_is_sent_again_until_acked_then_referenced()
    {
        var town = new Town(4, 4, 300);
        var session = new Session(6);
        session.Quiet(town, 5);
        const string name = "Ünïque wänderer";
        byte[] utf8 = Encoding.UTF8.GetBytes(name);
        bool Carried(IEnumerable<byte[]> parts) => parts.Any(p => p.AsSpan().IndexOf(utf8) >= 0);

        // The part that defines the name is lost: the next part defines it again (it was never confirmed) and the mirror decodes it.
        town.People[1].Name = name;
        town.Tick++;
        session.Link.Loss = 1;
        session.Host.Send(town.Publish(), TimeSpan.FromMilliseconds(50 * session.Sends++), session.Link);
        Assert.True(Carried(session.Link.Parts));
        session.Link.Deliver(session.Mirror);
        session.Link.Loss = 0;
        town.People[1].Position += new Vector3(5, 0, 0);
        town.Tick++;
        session.Host.Send(town.Publish(), TimeSpan.FromMilliseconds(50 * session.Sends++), session.Link);
        Assert.True(Carried(session.Link.Parts));
        session.Quiet(town, 3);
        Assert.Equal(name, session.Latest!.Characters.Single(c => c.Id.Slot == 1).Name);

        // Once acked, the string is only referenced: another character taking the name does not carry its text.
        town.People[2].Name = name;
        town.Tick++;
        session.Host.Send(town.Publish(), TimeSpan.FromMilliseconds(50 * session.Sends++), session.Link);
        Assert.False(Carried(session.Link.Parts));
        session.Link.Deliver(session.Mirror);
        session.Quiet(town, 3);
        Assert.Equal(name, session.Latest.Characters.Single(c => c.Id.Slot == 2).Name);
        AssertMirrors(town.Publish(), session.Host, session.Latest);
        Assert.Equal(0, session.Mirror.DroppedParts);
    }

    [Fact]
    public void Starved_entities_take_turns_under_a_small_budget_and_catch_up()
    {
        var town = new Town(9, 200, 1000);
        var session = new Session(8, new SnapshotEncoderOptions { BytesPerSecond = 20 * 700 });
        (session.Link.Loss, session.FrameLoss) = (0.1, 0.1);
        int starved = 0;
        long worst = 0;
        for (int i = 0; i < 120; i++)
        {
            town.Step(1, churn: false);
            var view = town.Publish();
            session.Round(view);
            starved += session.Host.LastSend.Starved;
            if (i < 60) continue;   // the first 2 s have a double budget
            Assert.True(session.Host.LastSend.SnapshotBytes <= 2 * 700, $"{session.Host.LastSend.SnapshotBytes} bytes in one send");
            foreach (var c in view.Characters) worst = Math.Max(worst, view.Tick - session.Mirror.TickOf(c.Id));
        }
        Assert.True(starved > 0);
        Assert.True(worst <= 20, $"a character waited {worst} ticks");
        session.Quiet(town, 30);
        AssertMirrors(town.Publish(), session.Host, session.Latest);
        output.WriteLine($"{starved} records left for later; the most stale character was {worst} ticks behind");
    }
}
