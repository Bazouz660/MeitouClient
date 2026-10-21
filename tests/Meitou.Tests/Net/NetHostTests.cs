using System.Numerics;
using Meitou.Data;
using Meitou.Data.Characters;
using Meitou.Data.Gameplay;
using Meitou.Engine;
using Meitou.Net;
using Meitou.Net.Events;
using Meitou.Net.Replication;
using Meitou.Net.Session;
using Meitou.Net.Transport;
using Meitou.Simulation;
using Meitou.Simulation.Trading;

namespace Meitou.Tests.Net;

/// <summary>
/// <see cref="NetHost"/>'s handshake over <see cref="LoopbackNetwork"/> with a hand-driven client (docs/net.md, "Host"): accept, wrong password, wrong
/// protocol, a full game, the same player id twice, and a kick.
/// </summary>
public class NetHostTests
{
    const int Port = 40120;
    const string Password = "sesame";
    static readonly AnimationLibrary Library = new([new AnimationDefinition { Id = "a0", Name = "anim 0", Clip = "clip0" }]);
    static readonly DataSetIdentity Data = new([new("gamedata.base", 1, 100)]);
    static readonly NetIdentity HostId = new(Guid.NewGuid(), "Hostess", "b1", Data);

    delegate T Reader<T>(ref NetReader r);

    sealed class NoLooks : IAppearanceSource
    {
        public CharacterAppearance? Create(string recordId, string? factionId, int seed) => null;
    }

    sealed class Game : IHostGame
    {
        public GateOptions Gate { get; } = new();
        public SessionRules Rules { get; } = new(4, false, true, false, true, true, false, false, false);
        public WeatherSync? Weather() => null;
    }

    sealed class Rig : IDisposable
    {
        public readonly ManualNetClock Clock = new();
        public readonly LoopbackNetwork Net;
        public readonly WorldSession Session = new(Vector3.Zero, (-100, -100, 100, 100), (_, _) => 0);
        public readonly NetHost Host;
        public readonly List<NoticeNet> Notices = [];
        readonly List<Client> clients = [];

        public Rig(int maxPlayers = 4)
        {
            Net = new LoopbackNetwork(new LoopbackOptions(), Clock);
            Host = new NetHost(Session, Net.Server(), new NetHostOptions { Port = Port, MaxPlayers = maxPlayers, Password = Password }, HostId, new Game(),
                new ReplicaCatalog(new GameDatabase(), Library, new NoLooks()), Clock);
            Host.Notice += Notices.Add;
            Host.Start();
        }

        public Client Connect(ushort version = NetProtocol.Version)
        {
            var client = new Client(Net.Client());
            clients.Add(client);
            client.Id = client.Transport.Connect("localhost", Port, [.. NetProtocol.Magic, (byte)version, (byte)(version >> 8)]);
            Settle();
            return client;
        }

        /// <summary>A few host frames, each followed by every client's poll.</summary>
        public void Settle()
        {
            for (int i = 0; i < 4; i++)
            {
                Clock.Advance(TimeSpan.FromMilliseconds(5));
                Host.BeginFrame();
                Host.EndFrame(false);
                foreach (var c in clients) c.Transport.Poll(c.Seen);
            }
        }

        public void Dispose()
        {
            Host.Dispose();
            foreach (var c in clients) c.Transport.Dispose();
            Session.Dispose();
        }
    }

    sealed class Client(INetClientTransport transport)
    {
        public INetClientTransport Transport { get; } = transport;
        public TransportRecorder Seen { get; } = new();
        public NetConnectionId Id { get; set; }

        public IEnumerable<byte[]> On(NetChannel channel) => Seen.Packets.Where(p => p.Channel == channel).Select(p => p.Data);

        /// <summary>The single Control message of <paramref name="type"/> received.</summary>
        public T Single<T>(MessageType type, Reader<T> read) => Decode(On(NetChannel.Control).Single(d => d[0] == (byte)type), read);

        public void Send(Action<NetWriter> write)
        {
            using var w = new NetWriter();
            write(w);
            Transport.Send(Id, NetChannel.Control, w.Written);
        }

        public void Hello(Guid player, string name, string? password)
        {
            var challenge = Single(MessageType.Challenge, ProtocolCodec.ReadChallenge);
            Assert.True(challenge.NeedsPassword);
            var hello = new Hello(NetProtocol.Version, "b1", player, name, Handshake.Proof(password, challenge.Nonce, player), Data, Handshake.AnimationHash(Library));
            Send(w => ProtocolCodec.Write(w, hello));
        }

        /// <summary>The Reject the host dropped this connection with (or refused it with).</summary>
        public Reject Dropped(NetDropReason reason)
        {
            var drop = Assert.Single(Seen.Drops);
            Assert.Equal(reason, drop.Reason);
            return Decode(drop.Data, ProtocolCodec.ReadReject);
        }
    }

    static T Decode<T>(byte[] data, Reader<T> read)
    {
        var r = new NetReader(data);
        ProtocolCodec.ReadType(ref r);
        return read(ref r);
    }

    static Client Join(Rig rig, Guid player, string name)
    {
        var client = rig.Connect();
        client.Hello(player, name, Password);
        rig.Settle();
        return client;
    }

    [Fact]
    public void Accepts_a_client_welcomes_it_and_starts_it_at_Ready()
    {
        using var rig = new Rig();
        var id = Guid.NewGuid();
        var client = Join(rig, id, "Hostess");   // the host's name: the joiner gets " (2)"

        var welcome = client.Single(MessageType.Welcome, ProtocolCodec.ReadWelcome);
        Assert.Equal(1, welcome.Player);
        Assert.Equal(new NetRules(20, ClockPolicy.AnyPlayer, 4, false, true, Port), welcome.Rules);
        Assert.Equal([(0, NetPlayerState.Host), (1, NetPlayerState.Loading)], welcome.Roster.Select(e => (e.Player, e.State)));
        var reset = client.Single(MessageType.WorldReset, ProtocolCodec.ReadWorldReset);
        Assert.Equal((rig.Host.Epoch, 1), (reset.Epoch, reset.Player));
        Assert.Equal(new Game().Rules, reset.Rules);
        var session = rig.Session.World.Player.FindByGuid(id)!;
        Assert.Equal((1, "Hostess (2)"), (session.Number, session.Name));
        Assert.Equal(HostId.PlayerId, rig.Session.World.Player.Local.Guid);
        Assert.Same(rig.Host, rig.Session.ClockAuthority);

        var joined = new NoticeNet(NoticeKind.Joined, 1, "Hostess (2)", "");
        Assert.Equal([joined], rig.Notices);
        var events = new NetReader(client.On(NetChannel.Events).Single());
        var batch = EventCodec.Read(ref events);   // the event codec reads its own type byte
        Assert.Equal([joined], batch.Events);

        client.Send(w => ProtocolCodec.Write(w, new Ready(rig.Host.Epoch)));
        rig.Settle();
        Assert.Equal(NetPlayerState.Playing, rig.Host.Roster[1].State);
        var join = Assert.IsType<JoinPlayer>(Assert.Single(rig.Session.World.Commands.TakeDue(long.MaxValue)));
        Assert.Equal(1, join.Player);
        Assert.Contains(client.On(NetChannel.Snapshots), p => p[0] == (byte)MessageType.SnapshotPart);
    }

    [Fact]
    public void Refuses_a_wrong_password_and_a_wrong_protocol()
    {
        using var rig = new Rig();
        var wrong = rig.Connect();
        wrong.Hello(Guid.NewGuid(), "Guesser", "open sesame");
        rig.Settle();
        Assert.Equal(new Reject(RejectCode.Password, "Wrong password."), wrong.Dropped(NetDropReason.Closed));

        var old = rig.Connect(version: NetProtocol.Version + 1);
        var refused = old.Dropped(NetDropReason.Rejected);
        Assert.Equal(RejectCode.Protocol, refused.Code);
        Assert.Empty(rig.Notices);
        Assert.Equal([0], rig.Host.Roster.Select(e => e.Player));
    }

    [Fact]
    public void Refuses_a_connection_when_the_game_is_full()
    {
        using var rig = new Rig(maxPlayers: 2);
        Join(rig, Guid.NewGuid(), "First");
        var late = rig.Connect();
        Assert.Equal(new Reject(RejectCode.Full, "The game is full (2 players)."), late.Dropped(NetDropReason.Rejected));
    }

    [Fact]
    public void The_same_player_id_again_replaces_the_older_connection_and_keeps_its_number()
    {
        using var rig = new Rig();
        var id = Guid.NewGuid();
        var first = Join(rig, id, "Twin");
        var second = Join(rig, id, "Twin");

        Assert.Equal(new Reject(RejectCode.Duplicate, "Signed in from another place."), first.Dropped(NetDropReason.Closed));
        Assert.Equal(1, second.Single(MessageType.Welcome, ProtocolCodec.ReadWelcome).Player);
        Assert.Equal([(0, NetPlayerState.Host), (1, NetPlayerState.Loading)], rig.Host.Roster.Select(e => (e.Player, e.State)));
        Assert.Equal("Twin", rig.Host.Roster[1].Name);
        Assert.Equal([NoticeKind.Joined, NoticeKind.Joined], rig.Notices.Select(n => n.Kind));
    }

    [Fact]
    public void Kick_drops_the_client_and_keeps_the_player_away()
    {
        using var rig = new Rig();
        var client = Join(rig, Guid.NewGuid(), "Pest");
        rig.Session.World.Commands.TakeDue(long.MaxValue);

        rig.Host.Kick(1, "spam");
        rig.Settle();

        Assert.Equal(new Reject(RejectCode.Kicked, "Kicked by the host: spam"), client.Dropped(NetDropReason.Closed));
        Assert.Equal((1, NetPlayerState.Away), (rig.Host.Roster[1].Player, rig.Host.Roster[1].State));
        Assert.Equal(new NoticeNet(NoticeKind.Kicked, 1, "Pest", "spam"), rig.Notices[^1]);
        var closes = rig.Session.World.Commands.TakeDue(long.MaxValue);
        Assert.Contains(closes, c => c is CloseTrade { Player: 1 });
        Assert.Equal(3, closes.Count);
    }
}
