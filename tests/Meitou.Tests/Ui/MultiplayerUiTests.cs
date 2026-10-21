using System.Numerics;
using Meitou.Game;
using Meitou.Net.Session;

namespace Meitou.Tests.Ui;

/// <summary>The multiplayer interface's pure parts: the join address parser and the player colours (docs/meitou-ui.md "Multiplayer").</summary>
public class MultiplayerUiTests
{
    [Theory]
    [InlineData("example.org", "example.org", 40120)]
    [InlineData("  192.168.1.20  ", "192.168.1.20", 40120)]
    [InlineData("192.168.1.20:27015", "192.168.1.20", 27015)]
    [InlineData("host-1.lan:1", "host-1.lan", 1)]
    [InlineData("localhost:65535", "localhost", 65535)]
    [InlineData("[::1]", "::1", 40120)]
    [InlineData("[2001:db8::7]:5000", "2001:db8::7", 5000)]
    [InlineData("fe80::1", "fe80::1", 40120)]
    [InlineData("2001:db8:0:0:0:0:0:1", "2001:db8:0:0:0:0:0:1", 40120)]
    public void Join_addresses_give_the_host_and_port(string text, string host, int port)
    {
        Assert.True(JoinAddress.TryParse(text, 40120, out var h, out var p));
        Assert.Equal((host, port), (h, p));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("host:")]
    [InlineData("host:0")]
    [InlineData("host:65536")]
    [InlineData("host:-1")]
    [InlineData("host:+80")]
    [InlineData("host:port")]
    [InlineData(":80")]
    [InlineData("a b")]
    [InlineData("host:12:34")]
    [InlineData("[::1")]
    [InlineData("[::1]x")]
    [InlineData("[::1]:")]
    [InlineData("[]")]
    [InlineData("[1.2.3.4]")]
    [InlineData("[::1]:70000")]
    public void Bad_join_addresses_are_refused(string text)
    {
        Assert.False(JoinAddress.TryParse(text, 40120, out var host, out var port));
        Assert.Equal(("", 0), (host, port));
    }

    [Fact]
    public void Colours_are_distinct_with_the_host_on_the_player_marker_and_strangers_grey()
    {
        var colours = Enumerable.Range(0, PlayerColours.Count).Select(PlayerColours.Of).ToArray();
        Assert.Equal(8, colours.Distinct().Count());
        Assert.Equal(new Vector4(0x8D / 255f, 0xCD / 255f, 1, 1), colours[0]);   // MarkerPlayer of kenshi_colours.xml
        Assert.Equal(PlayerColours.Absent, PlayerColours.Of(-1));
        Assert.Equal(PlayerColours.Absent, PlayerColours.Of(8));
        Assert.Equal("0.553 0.804 1", PlayerColours.MyGui(0));
        Assert.Equal(new Vector4(0.652f, 0.903f, 1, 1), PlayerColours.Selected(0));   // the map's selected marker
        var selected = PlayerColours.Selected(1);
        Assert.Equal(1, selected.W);
        Assert.True(selected.X > colours[1].X && selected.Z > colours[1].Z);
    }

    [Fact]
    public void A_joining_player_keeps_a_free_preferred_colour_else_takes_the_lowest_free()
    {
        Assert.Equal(0, PlayerColours.Assign([], 0));
        Assert.Equal(3, PlayerColours.Assign([0, 1], 3));
        Assert.Equal(2, PlayerColours.Assign([0, 1, 3], 3));
        Assert.Equal(1, PlayerColours.Assign([0], -1));
        Assert.Equal(1, PlayerColours.Assign([0], 9));
        Assert.Equal(-1, PlayerColours.Assign([0, 1, 2, 3, 4, 5, 6, 7], 2));
    }

    [Theory]
    [InlineData("/kick Bob being rude", ChatVerb.Kick, "Bob", "being rude")]
    [InlineData("/KICK bob", ChatVerb.Kick, "bob", "")]
    [InlineData("  /ban \"Big Bob\" twice  ", ChatVerb.Ban, "Big Bob", "twice")]
    [InlineData("/ban \"Big Bob", ChatVerb.Ban, "Big Bob", "")]
    [InlineData("/kick", ChatVerb.Kick, "", "")]
    [InlineData("/players", ChatVerb.Players, "", "")]
    [InlineData("/Help me", ChatVerb.Help, "me", "")]
    [InlineData("/dance now", ChatVerb.Unknown, "", "")]
    [InlineData("/", ChatVerb.Unknown, "", "")]
    [InlineData("hello", ChatVerb.Unknown, "", "")]
    public void Chat_commands_split_into_verb_target_and_reason(string line, ChatVerb verb, string target, string rest) =>
        Assert.Equal(new ChatCommand(verb, target, rest), ChatCommands.Parse(line));

    [Fact]
    public void A_chat_target_resolves_by_number_then_exact_name_then_unique_prefix()
    {
        RosterEntry[] players = [new(0, "Host", NetPlayerState.Host, 0, 0, 1), new(1, "Bob", NetPlayerState.Playing, 20, 1, 1),
            new(2, "Bobby", NetPlayerState.Playing, 30, 2, 1), new(3, "alice", NetPlayerState.Playing, 40, 3, 1)];
        Assert.Equal(3, ChatCommands.Resolve(players, "#3", out _)!.Player);
        Assert.Equal(1, ChatCommands.Resolve(players, "BOB", out _)!.Player);   // exact beats the prefix of Bobby
        Assert.Equal(3, ChatCommands.Resolve(players, "ali", out _)!.Player);
        Assert.Null(ChatCommands.Resolve(players, "bo", out string several));
        Assert.Equal("bo matches several players: Bob, Bobby.", several);
        Assert.Null(ChatCommands.Resolve(players, "#9", out string none));
        Assert.Equal("No player called #9.", none);
        Assert.Null(ChatCommands.Resolve(players, "zed", out none));
        Assert.Equal("No player called zed.", none);
    }
}
