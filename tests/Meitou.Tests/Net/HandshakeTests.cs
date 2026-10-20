using Meitou.Data;
using Meitou.Data.Gameplay;
using Meitou.Net;
using Meitou.Net.Session;

namespace Meitou.Tests.Net;

/// <summary>The handshake checks: each reject path in order, the password proof, name sanitizing and the colour rule (docs/net.md, "Protocol and handshake").</summary>
public class HandshakeTests
{
    static readonly byte[] Nonce = new byte[16];
    static readonly DataSetIdentity Data = new([new("gamedata.base", 1, 100), new("a.mod", 2, 200)]);
    static readonly NetIdentity Host = new(Guid.NewGuid(), "Host", "b1", Data);

    static byte[] Connect(ushort version) => [.. "MEITOU"u8, (byte)version, (byte)(version >> 8)];

    static Hello Good(Guid? player = null, string? password = null)
    {
        var id = player ?? Guid.NewGuid();
        return new Hello(NetProtocol.Version, "b1", id, "Joiner", Handshake.Proof(password, Nonce, id), Data, 77);
    }

    static Reject? Check(Hello hello, string? password = null, bool allowBuildMismatch = false, bool banned = false) =>
        Handshake.CheckHello(hello, Host, 77, password, Nonce, allowBuildMismatch, banned);

    [Fact]
    public void Connect_requests_are_checked_for_protocol_then_room_then_ban()
    {
        Assert.Null(Handshake.CheckRequest(Connect(NetProtocol.Version), 1, 4, false));

        var bad = Handshake.CheckRequest(Connect(99), 1, 4, true)!;
        Assert.Equal(RejectCode.Protocol, bad.Code);
        Assert.Equal($"Incompatible network protocol (yours 99, host's {NetProtocol.Version}). Update Meitou.", bad.Reason);
        Assert.Equal(RejectCode.Protocol, Handshake.CheckRequest("NOPE00\u0001\0"u8.ToArray(), 0, 4, false)!.Code);
        Assert.Equal(RejectCode.Protocol, Handshake.CheckRequest([], 0, 4, false)!.Code);

        var full = Handshake.CheckRequest(Connect(NetProtocol.Version), 3, 4, true)!;
        Assert.Equal((RejectCode.Full, "The game is full (4 players)."), (full.Code, full.Reason));
        var banned = Handshake.CheckRequest(Connect(NetProtocol.Version), 2, 4, true)!;
        Assert.Equal((RejectCode.Banned, "You are banned from this game."), (banned.Code, banned.Reason));
    }

    [Fact]
    public void Hello_is_checked_in_order_and_accepts_a_matching_client()
    {
        Assert.Null(Check(Good()));

        Assert.Equal(RejectCode.Protocol, Check(Good() with { Protocol = 0 })!.Code);
        Assert.Equal(RejectCode.Banned, Check(Good() with { Build = "other" }, banned: true)!.Code);
        Assert.Equal(RejectCode.Duplicate, Check(Good(Host.PlayerId))!.Code);

        var build = Check(Good() with { Build = "b0" })!;
        Assert.Equal((RejectCode.Build, "Version mismatch: you run b0, the host runs b1."), (build.Code, build.Reason));
        Assert.Null(Check(Good() with { Build = "b0" }, allowBuildMismatch: true));

        var data = Check(Good() with { Data = new DataSetIdentity([new("gamedata.base", 1, 100)]) })!;
        Assert.Equal((RejectCode.Data, "Your game data differs from the host's. Missing: a.mod."), (data.Code, data.Reason));
        Assert.Equal(RejectCode.Data, Check(Good() with { AnimationHash = 78 })!.Code);
        Assert.Equal(RejectCode.Data, Check(Good(password: "x") with { AnimationHash = 1 }, "y")!.Code);
    }

    [Fact]
    public void Password_proof_matches_only_for_the_nonce_player_and_password()
    {
        Assert.Empty(Handshake.Proof(null, Nonce, Guid.Empty));
        Assert.Empty(Handshake.Proof("", Nonce, Guid.Empty));

        var id = Guid.NewGuid();
        var proof = Handshake.Proof("secret", Nonce, id);
        Assert.Equal(32, proof.Length);
        Assert.Equal(proof, Handshake.Proof("secret", Nonce, id));
        Assert.NotEqual(proof, Handshake.Proof("Secret", Nonce, id));
        Assert.NotEqual(proof, Handshake.Proof("secret", Nonce, Guid.NewGuid()));
        Assert.NotEqual(proof, Handshake.Proof("secret", [.. Nonce.Take(15), (byte)1], id));

        Assert.Null(Check(Good(id, "secret"), "secret"));
        Assert.Null(Check(Good(), null));
        var none = Check(Good(), "secret")!;
        Assert.Equal((RejectCode.Password, "This game needs a password."), (none.Code, none.Reason));
        var wrong = Check(Good(password: "nope"), "secret")!;
        Assert.Equal((RejectCode.Password, "Wrong password."), (wrong.Code, wrong.Reason));
        Assert.Equal("Wrong password.", Check(Good(id, "secret") with { Player = Guid.NewGuid() }, "secret")!.Reason);
    }

    [Fact]
    public void Data_set_differences_read_as_sentences()
    {
        static DataSetIdentity Set(params (string, ulong)[] files) => new([.. files.Select(f => new DataSetEntry(f.Item1, f.Item2, 10))]);
        var host = Set(("gamedata.base", 1), ("a.mod", 2), ("b.mod", 3));

        Assert.Null(DataSetIdentity.Difference(host, Set(("gamedata.base", 1), ("a.mod", 2), ("b.mod", 3))));
        Assert.Equal("Same mods in a different load order.", DataSetIdentity.Difference(host, Set(("gamedata.base", 1), ("b.mod", 3), ("a.mod", 2))));
        Assert.Equal("Missing: b.mod; extra: c.mod; different version: a.mod.",
            DataSetIdentity.Difference(host, Set(("gamedata.base", 1), ("a.mod", 9), ("c.mod", 4))));
        Assert.Equal("Extra: z.mod.", DataSetIdentity.Difference(host, Set(("gamedata.base", 1), ("a.mod", 2), ("b.mod", 3), ("z.mod", 5))));

        var many = Set([.. Enumerable.Range(0, 8).Select(i => ($"m{i}.mod", (ulong)i))]);
        Assert.Equal("Missing: m0.mod, m1.mod, m2.mod, m3.mod, m4.mod (and 3 more).", DataSetIdentity.Difference(many, Set()));

        Assert.NotEqual(host.Combined, Set(("gamedata.base", 1), ("b.mod", 3), ("a.mod", 2)).Combined);
        Assert.Equal(host.Combined, Set(("gamedata.base", 1), ("a.mod", 2), ("b.mod", 3)).Combined);
    }

    [Fact]
    public void Data_set_hashes_file_content_not_names()
    {
        var dir = Path.Combine(Path.GetTempPath(), "meitou-ds-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string a = Path.Combine(dir, "a.mod"), b = Path.Combine(dir, "b.mod");
            File.WriteAllBytes(a, [1, 2, 3]);
            File.WriteAllBytes(b, [1, 2, 3]);
            LoadOrder Order(params LoadOrderEntry[] entries)
            {
                var order = new LoadOrder();
                order.Entries.AddRange(entries);
                return order;
            }
            var one = DataSetIdentity.Compute(Order(new LoadOrderEntry("a.mod", a), new LoadOrderEntry("b.mod", b)));
            Assert.Equal(one.Files[0].Hash, one.Files[1].Hash);
            Assert.Equal(3, one.Files[0].Length);
            Assert.Null(DataSetIdentity.Difference(one, DataSetIdentity.Compute(Order(new LoadOrderEntry("a.mod", a), new LoadOrderEntry("b.mod", b)))));
            File.WriteAllBytes(b, [1, 2, 4]);
            Assert.Equal("Different version: b.mod.", DataSetIdentity.Difference(one, DataSetIdentity.Compute(Order(new LoadOrderEntry("a.mod", a), new LoadOrderEntry("b.mod", b)))));
            Assert.Equal(-1, DataSetIdentity.Compute(Order(new LoadOrderEntry("gone.mod", Path.Combine(dir, "gone.mod")))).Files[0].Length);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Theory]
    [InlineData("  Alice  ", "Alice")]
    [InlineData("Bo\tb\r\nby", "Bobby")]
    [InlineData("", "Player 3")]
    [InlineData(" \u0007 ", "Player 3")]
    [InlineData("abcdefghijklmnopqrstuvwxyz", "abcdefghijklmnopqrstuvwx")]
    [InlineData("abcdefghijklmnopqrstuvw\U0001F600", "abcdefghijklmnopqrstuvw")]
    [InlineData("abcdefghijklmnopqrstuvwx   y", "abcdefghijklmnopqrstuvwx")]
    public void Names_are_trimmed_stripped_and_clamped(string raw, string expected) => Assert.Equal(expected, Handshake.SanitizeName(raw, 3));

    [Fact]
    public void Animation_hash_depends_on_the_ids_in_order_and_the_count()
    {
        static AnimationLibrary Library(params string[] ids) => new(ids.Select(id => new AnimationDefinition { Id = id, Name = id, Clip = id }));
        Assert.Equal(Handshake.AnimationHash(Library("a", "b")), Handshake.AnimationHash(Library("a", "b")));
        Assert.NotEqual(Handshake.AnimationHash(Library("a", "b")), Handshake.AnimationHash(Library("b", "a")));
        Assert.NotEqual(Handshake.AnimationHash(Library("a", "b")), Handshake.AnimationHash(Library("a")));
        Assert.NotEqual(Handshake.AnimationHash(Library()), Handshake.AnimationHash(Library("")));
    }

    [Fact]
    public void Colours_prefer_the_number_then_the_lowest_free_then_none()
    {
        Assert.Equal(0, PlayerColourRule.Assign([], 0));
        Assert.Equal(3, PlayerColourRule.Assign([0], 3));
        Assert.Equal(1, PlayerColourRule.Assign([0, 5], 8 + 5));
        Assert.Equal(2, PlayerColourRule.Assign([0, 1, 3], 3));
        Assert.Equal(7, PlayerColourRule.Assign([0, 1, 2, 3, 4, 5, 6], 5));
        Assert.Equal(-1, PlayerColourRule.Assign([0, 1, 2, 3, 4, 5, 6, 7], 2));
        Assert.Equal(7, PlayerColourRule.Assign([], -1));
    }
}
