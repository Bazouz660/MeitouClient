using System.Numerics;
using System.Text;
using Meitou.Data.World;
using Meitou.Engine.Time;
using Meitou.Net.Transport;
using Meitou.Net;
using Meitou.Net.Session;

namespace Meitou.Tests.Net;

/// <summary>Every protocol message survives the wire and the readers refuse malformed input (docs/net.md, "Protocol and handshake").</summary>
public class ProtocolCodecTests
{
    static readonly byte[] Nonce = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
    static readonly NetRules Net = new(20, ClockPolicy.HostOnly, 6, true, true, 40120);
    static readonly SessionRules Rules = new(3, true, false, true, false, true, false, true, true);
    static readonly DataSetIdentity Data = new([new("gamedata.base", 0xDEADBEEFCAFEF00DUL, 123_456_789), new("Mod ü.mod", 7, 0)]);
    static readonly RosterEntry[] Roster =
    [
        new(0, "Host", NetPlayerState.Host, 0, 0, 3),
        new(2, "Üñï 名前", NetPlayerState.Loading, 45, 5, 0),
        new(3, "Gone", NetPlayerState.Away, -1, -1, 1),
    ];
    static readonly ClockState Clock = new(7, 2, 5, true, 1.75, 123_456_789_012, 8.5, new ClockChange(3, ClockChangeKind.Speed, 5));

    static WeatherSync Weather() => new(9, "Rain", 41, 13.25,
    [
        new WeatherRegionSnapshot("region-1", "summer", 2, 60, "clear", 4, 1000.5, 2000.25, 0.75f,
            new WindInterpolation(990, 30, 1.5f, -3f, 2.5f, 3f), 1500, new RegionFog(0.5f, new Vector3(0.1f, 0.2f, 0.3f), 800), 12.5),
        new WeatherRegionSnapshot("r2", "", 0, 0, "", 0, 0, 0, 0, default, 0, default, 0),
    ]);

    delegate T ReadFn<T>(ref NetReader r);

    static byte[] Bytes(Action<NetWriter> write)
    {
        using var w = new NetWriter();
        write(w);
        return w.Written.ToArray();
    }

    static T Read<T>(byte[] bytes, MessageType expected, ReadFn<T> read)
    {
        var r = new NetReader(bytes);
        Assert.Equal(expected, ProtocolCodec.ReadType(ref r));
        return read(ref r);
    }

    static void AssertWeather(WeatherSync a, WeatherSync b)
    {
        Assert.Equal((a.Revision, a.Forced, a.Day, a.Hour), (b.Revision, b.Forced, b.Day, b.Hour));
        Assert.Equal(a.Regions, b.Regions);
    }

    [Fact]
    public void Challenge_Hello_Welcome_Reject_and_Ready_round_trip()
    {
        var challenge = Read(Bytes(w => ProtocolCodec.Write(w, new Challenge(Nonce, "Host ü", true))), MessageType.Challenge, ProtocolCodec.ReadChallenge);
        Assert.Equal(Nonce, challenge.Nonce);
        Assert.Equal(("Host ü", true), (challenge.HostName, challenge.NeedsPassword));

        var guid = Guid.NewGuid();
        var proof = Handshake.Proof("pw", Nonce, guid);
        var hello = Read(Bytes(w => ProtocolCodec.Write(w, new Hello(1, "build 1.2", guid, "Name", proof, Data, ulong.MaxValue))), MessageType.Hello, ProtocolCodec.ReadHello);
        Assert.Equal((1, "build 1.2", guid, "Name", ulong.MaxValue), (hello.Protocol, hello.Build, hello.Player, hello.Name, hello.AnimationHash));
        Assert.Equal(proof, hello.Proof);
        Assert.Equal(Data.Files, hello.Data.Files);
        Assert.Equal(Data.Combined, hello.Data.Combined);
        var noProof = Read(Bytes(w => ProtocolCodec.Write(w, new Hello(1, "b", guid, "N", [], new DataSetIdentity([]), 0))), MessageType.Hello, ProtocolCodec.ReadHello);
        Assert.Empty(noProof.Proof);

        var welcome = Read(Bytes(w => ProtocolCodec.Write(w, new Welcome(2, Net, Roster))), MessageType.Welcome, ProtocolCodec.ReadWelcome);
        Assert.Equal(2, welcome.Player);
        Assert.Equal(Net, welcome.Rules);
        Assert.Equal(Roster, welcome.Roster);

        var reject = Read(Bytes(w => ProtocolCodec.Write(w, new Reject(RejectCode.Kicked, "Kicked by the host: ü"))), MessageType.Reject, ProtocolCodec.ReadReject);
        Assert.Equal(new Reject(RejectCode.Kicked, "Kicked by the host: ü"), reject);

        Assert.Equal(new Ready(65535), Read(Bytes(w => ProtocolCodec.Write(w, new Ready(65535))), MessageType.Ready, ProtocolCodec.ReadReady));
    }

    [Fact]
    public void WorldReset_carries_clock_rules_and_weather()
    {
        var focus = new Vector3(1.5f, -2, 3e5f);
        var weather = Weather();
        var m = Read(Bytes(w => ProtocolCodec.Write(w, new WorldReset(3, 4, 0x0123456789ABCDEFUL, focus, Clock, Rules, weather))), MessageType.WorldReset, ProtocolCodec.ReadWorldReset);
        Assert.Equal((3, 4, 0x0123456789ABCDEFUL, focus, Clock, Rules), (m.Epoch, m.Player, m.Seed, m.Focus, m.Clock, m.Rules));
        AssertWeather(weather, m.Weather!);

        var none = Read(Bytes(w => ProtocolCodec.Write(w, new WorldReset(0, 0, 0, default, default, Rules, null))), MessageType.WorldReset, ProtocolCodec.ReadWorldReset);
        Assert.Null(none.Weather);
        Assert.Equal(default, none.Clock);
    }

    [Fact]
    public void Rules_chat_clock_roster_and_weather_messages_round_trip()
    {
        var update = Read(Bytes(w => ProtocolCodec.Write(w, new RulesUpdate(Net, Rules))), MessageType.Rules, ProtocolCodec.ReadRulesUpdate);
        Assert.Equal((Net, Rules), (update.Net, update.Session));
        var off = new SessionRules(0, false, false, false, false, false, false, false, false);
        Assert.Equal(off, Read(Bytes(w => ProtocolCodec.Write(w, new RulesUpdate(Net, off))), MessageType.Rules, ProtocolCodec.ReadRulesUpdate).Session);

        Assert.Equal("héllo 名前", Read(Bytes(w => ProtocolCodec.Write(w, new ChatRequest("héllo 名前"))), MessageType.ChatRequest, ProtocolCodec.ReadChatRequest).Text);

        foreach (var request in new[] { ClockRequest.TogglePause, ClockRequest.Set(5), ClockRequest.Faster, ClockRequest.Slower })
            Assert.Equal(request, Read(Bytes(w => ProtocolCodec.Write(w, request)), MessageType.ClockRequest, ProtocolCodec.ReadClockRequest));

        Assert.Equal(Clock, Read(Bytes(w => ProtocolCodec.Write(w, Clock)), MessageType.ClockState, ProtocolCodec.ReadClockState));

        Assert.Equal(Roster, Read(Bytes(w => ProtocolCodec.Write(w, Roster)), MessageType.Roster, ProtocolCodec.ReadRoster));
        Assert.Empty(Read(Bytes(w => ProtocolCodec.Write(w, Array.Empty<RosterEntry>())), MessageType.Roster, ProtocolCodec.ReadRoster));

        var weather = Weather();
        AssertWeather(weather, Read(Bytes(w => ProtocolCodec.Write(w, weather)), MessageType.Weather, ProtocolCodec.ReadWeather));
        var empty = new WeatherSync(0, null, 0, 0, []);
        AssertWeather(empty, Read(Bytes(w => ProtocolCodec.Write(w, empty)), MessageType.Weather, ProtocolCodec.ReadWeather));
    }

    [Fact]
    public void Writers_clamp_texts_to_the_reader_limit_and_readers_refuse_malformed_input()
    {
        string long8 = new('ü', 5000);
        var reject = Read(Bytes(w => ProtocolCodec.Write(w, new Reject(RejectCode.Data, long8))), MessageType.Reject, ProtocolCodec.ReadReject);
        Assert.Equal(ProtocolCodec.MaxRejectReason, Encoding.UTF8.GetByteCount(reject.Reason));
        Assert.True(Bytes(w => ProtocolCodec.Write(w, new Reject(RejectCode.Data, long8))).Length <= INetTransport.MaxDropPayload);

        var bytes = Bytes(w => ProtocolCodec.Write(w, new Ready(1)));
        Assert.Throws<NetFormatException>(() => Read([.. bytes, 0], MessageType.Ready, ProtocolCodec.ReadReady));
        Assert.Throws<NetFormatException>(() => Read(bytes[..^1], MessageType.Ready, ProtocolCodec.ReadReady));
        Assert.Throws<NetFormatException>(() => Read([0x7F], MessageType.Ready, ProtocolCodec.ReadReady));
        Assert.Throws<NetFormatException>(() => Read([(byte)MessageType.Reject, 200, 0], MessageType.Reject, ProtocolCodec.ReadReject));
        Assert.Throws<ArgumentException>(() => Bytes(w => ProtocolCodec.Write(w, new Challenge([1, 2, 3], "h", false))));
        Assert.Throws<ArgumentException>(() => Bytes(w => ProtocolCodec.Write(w, new Hello(1, "b", Guid.Empty, "n", [1], Data, 0))));

        var clock = Bytes(w => ProtocolCodec.Write(w, new ClockState(0, 1, 0, false, 0, 0, 0, default)));
        BitConverter.GetBytes(double.NaN).CopyTo(clock, 3); // the type byte and the epoch come first, then Speed
        Assert.Throws<NetFormatException>(() => Read(clock, MessageType.ClockState, ProtocolCodec.ReadClockState));

        // The writers refuse what those readers would.
        Assert.Throws<ArgumentException>(() => Bytes(w => ProtocolCodec.Write(w, new ClockState(0, double.NaN, 0, false, 0, 0, 0, default))));
        Assert.Throws<ArgumentException>(() => Bytes(w => ProtocolCodec.Write(w, new ClockRequest(ClockRequestKind.SetSpeed, double.PositiveInfinity))));
        Assert.Throws<ArgumentException>(() => Bytes(w => ProtocolCodec.Write(w, new WeatherSync(0, null, 0, double.NaN, []))));
    }
}
