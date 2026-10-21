using System.Buffers.Binary;
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

namespace Meitou.Tests.Net;

/// <summary>The host's handshake over the loopback network with a hand-driven client (docs/net.md, "Host"): accept, refusals, duplicate ids and kicks.</summary>
public class NetHostTests
{
    static readonly DataSetIdentity Data = new([new("gamedata.base", 1, 100)]);
    static readonly AnimationLibrary Animations = new([new AnimationDefinition { Id = "idle", Name = "idle", Clip = "idle" }]);

    sealed class Game : IHostGame
    {
        public GateOptions Gate { get; } = new();
        public SessionRules Rules { get; } = new(3, false, true, true, false, true, false, false, false);
        public WeatherSync? Weather() => null;
    }

    sealed class NoLooks : IAppearanceSource
    {
        public CharacterAppearance? Create(string recordId, string? factionId, int seed) => null;
    }

    /// <summary>A host on an empty local world, its loopback network and clock.</summary>
    sealed class Rig : IDisposable
    {
        public readonly ManualNetClock Clock = new();
        public readonly LoopbackNetwork Network;
        public readonly WorldSession Session = new(Vector3.Zero, (0, 0, 100, 100), (_, _) => 0);
        public readonly NetIdentity Self = new(Guid.NewGuid(), "Hosty", "b1", Data);
        public readonly NetHost Host;
        public readonly List<NoticeNet> Notices = [];

        public Rig(NetHostOptions? options = null)
        {
            Network = new LoopbackNetwork(new LoopbackOptions { LatencyMs = 5 }, Clock);
            Host = new NetHost(Session, Network.Server(), options ?? new NetHostOptions(), Self, new Game(), new ReplicaCatalog(new GameDatabase(), Animations, new NoLooks()), Clock);
            Host.Notice += Notices.Add;
            Host.Start();
        }

        /// <summary>Runs host frames of 10 ms for <paramref name="frames"/> frames, each client polling after the host.</summary>
        public void Run(int frames, params Client[] clients)
        {
            for (int i = 0; i < frames; i++)
            {
                Clock.Advance(TimeSpan.FromMilliseconds(10));
                Host.BeginFrame();
                Host.EndFrame(false);
                foreach (var c in clients) c.Poll();
            }
        }

        public void Dispose()
        {
            Host.Dispose();
            Session.Dispose();
        }
    }

    /// <summary>A client that speaks the protocol by hand.</summary>
    sealed class Client(LoopbackNetwork network, Guid? id = null)
    {
        public readonly INetClientTransport Transport = network.Client();
        public readonly TransportRecorder Side = new();
        public readonly Guid Id = id ?? Guid.NewGuid();
        public NetConnectionId Peer;

        public void Connect(ushort version = NetProtocol.Version)
        {
            var data = new byte[NetProtocol.Magic.Length + 2];
            NetProtocol.Magic.CopyTo(data);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(NetProtocol.Magic.Length), version);
            Peer = Transport.Connect("localhost", NetProtocol.DefaultPort, data);
        }

        public void Poll() => Transport.Poll(Side);

        public IEnumerable<(MessageType Type, byte[] Data)> Control =>
            Side.Packets.Where(p => p.Channel == NetChannel.Control).Select(p => ((MessageType)p.Data[0], p.Data));

        public T Read<T>(MessageType type, Func<byte[], T> read) => read(Control.Last(m => m.Type == type).Data);

        public void Hello(string name, string? password = null)
        {
            var challenge = Read(MessageType.Challenge, d => { var r = new NetReader(d); ProtocolCodec.ReadType(ref r); return ProtocolCodec.ReadChallenge(ref r); });
            using var w = new NetWriter();
            ProtocolCodec.Write(w, new Hello(NetProtocol.Version, "b1", Id, name, Handshake.Proof(password, challenge.Nonce, Id), Data, Handshake.AnimationHash(Animations)));
            Transport.Send(Peer, NetChannel.Control, w.Written);
        }

        public Welcome Welcome => Read(MessageType.Welcome, d => { var r = new NetReader(d); ProtocolCodec.ReadType(ref r); return ProtocolCodec.ReadWelcome(ref r); });

        public WorldReset Reset => Read(MessageType.WorldReset, d => { var r = new NetReader(d); ProtocolCodec.ReadType(ref r); return ProtocolCodec.ReadWorldReset(ref r); });

        public Reject DroppedWith
        {
            get
            {
                var drop = Assert.Single(Side.Drops);
                var r = new NetReader(drop.Data);
                Assert.Equal(MessageType.Reject, ProtocolCodec.ReadType(ref r));
                return ProtocolCodec.ReadReject(ref r);
            }
        }

        public void Join(Rig rig, string name, string? password = null)
        {
            Connect();
            rig.Run(3, this);
            Hello(name, password);
            rig.Run(3, this);
        }
    }

    [Fact]
    public void A_good_hello_is_welcomed_with_a_number_and_a_world()
    {
        using var rig = new Rig();
        var client = new Client(rig.Network);
        client.Join(rig, "Guest");

        Assert.Empty(client.Side.Drops);
        var welcome = client.Welcome;
        Assert.Equal(1, welcome.Player);
        Assert.Equal(rig.Host.Rules, welcome.Rules);
        var reset = client.Reset;
        Assert.Equal(1, reset.Player);
        Assert.Equal(rig.Host.Epoch, reset.Epoch);
        Assert.Equal(new Game().Rules, reset.Rules);
        Assert.Equal(["Hosty", "Guest"], rig.Host.Players.Select(p => p.Name));
        Assert.Equal(NetPlayerState.Loading, rig.Host.Players[1].State);
        Assert.Equal(client.Id, rig.Session.World.Player.Find(1)!.Guid);
        Assert.Equal(rig.Self.PlayerId, rig.Session.World.Player.Local.Guid);
        var joined = Assert.Single(rig.Notices);
        Assert.Equal((NoticeKind.Joined, 1, "Guest"), (joined.Kind, joined.Player, joined.Name));
    }

    [Fact]
    public void A_wrong_password_is_refused()
    {
        using var rig = new Rig(new NetHostOptions { Password = "secret" });
        var client = new Client(rig.Network);
        client.Join(rig, "Guest", "guess");
        Assert.Equal(new Reject(RejectCode.Password, "Wrong password."), client.DroppedWith);
        Assert.Single(rig.Host.Players);

        var right = new Client(rig.Network);
        right.Join(rig, "Guest", "secret");
        Assert.Empty(right.Side.Drops);
        Assert.Equal(1, right.Welcome.Player);
    }

    [Fact]
    public void Another_protocol_version_is_refused_at_connect()
    {
        using var rig = new Rig();
        var client = new Client(rig.Network);
        client.Connect((ushort)(NetProtocol.Version + 1));
        rig.Run(3, client);
        var drop = Assert.Single(client.Side.Drops);
        Assert.Equal(NetDropReason.Rejected, drop.Reason);
        Assert.Equal(RejectCode.Protocol, client.DroppedWith.Code);
    }

    [Fact]
    public void A_full_game_refuses_the_next_player()
    {
        using var rig = new Rig(new NetHostOptions { MaxPlayers = 2 });
        var first = new Client(rig.Network);
        first.Join(rig, "One");
        var second = new Client(rig.Network);
        second.Connect();
        rig.Run(3, first, second);
        Assert.Equal(new Reject(RejectCode.Full, "The game is full (2 players)."), second.DroppedWith);
        Assert.Empty(first.Side.Drops);
    }

    [Fact]
    public void The_same_id_signing_in_again_replaces_the_older_connection_and_keeps_the_number()
    {
        using var rig = new Rig();
        var id = Guid.NewGuid();
        var older = new Client(rig.Network, id);
        older.Join(rig, "Twin");
        var newer = new Client(rig.Network, id);
        newer.Join(rig, "Twin");
        rig.Run(3, older, newer);

        Assert.Equal(new Reject(RejectCode.Duplicate, NetHost.SignedInElsewhere), older.DroppedWith);
        Assert.Empty(newer.Side.Drops);
        Assert.Equal(1, newer.Welcome.Player);
        Assert.Equal(2, rig.Host.Players.Count);
        Assert.Equal(NetPlayerState.Loading, rig.Host.Players[1].State);
        Assert.Equal("Twin", rig.Host.Players[1].Name);
    }

    [Fact]
    public void A_kicked_player_is_dropped_with_the_reason_and_stays_in_the_roster_as_away()
    {
        using var rig = new Rig();
        var client = new Client(rig.Network);
        client.Join(rig, "Rowdy");
        rig.Host.Kick(1, "spam");
        rig.Run(3, client);

        Assert.Equal(new Reject(RejectCode.Kicked, "Kicked by the host: spam"), client.DroppedWith);
        var kicked = rig.Notices[^1];
        Assert.Equal((NoticeKind.Kicked, 1, "Rowdy", "spam"), (kicked.Kind, kicked.Player, kicked.Name, kicked.Detail));
        Assert.Equal(NetPlayerState.Away, rig.Host.Roster[1].State);
        Assert.Equal("Rowdy", rig.Host.Roster[1].Name);

        // Coming back with the same id gives the same number.
        var again = new Client(rig.Network, client.Id);
        again.Join(rig, "Rowdy");
        Assert.Equal(1, again.Welcome.Player);
    }
}
