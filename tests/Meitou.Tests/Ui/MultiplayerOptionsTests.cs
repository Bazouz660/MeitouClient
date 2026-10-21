using Meitou.Game;

namespace Meitou.Tests.Ui;

/// <summary>The multiplayer command line (docs/engine.md "Multiplayer"): <c>--host [port]</c>, <c>--join</c>, <c>--name</c>, <c>--password</c>, <c>--config</c> and what they refuse to go with.</summary>
public class MultiplayerOptionsTests
{
    static (GameOptions Options, string[] Others) Split(params string[] args) => GameOptions.Split(args);

    [Fact]
    public void Host_takes_an_optional_port_and_join_an_address()
    {
        var (host, rest) = Split("--host", "--new-game", "--name", "Ana", "--windowed");
        Assert.Equal(0, host.Host);
        Assert.True(host.NewGame);
        Assert.Equal("Ana", host.Name);
        Assert.Equal(["--windowed"], rest);
        Assert.Null(host.NetProblem(screenshot: false));

        Assert.Equal(40200, Split("--host", "40200").Options.Host);
        Assert.Equal(-1, Split("--host", "99999999999").Options.Host);
        Assert.NotNull(Split("--host", "99999999999").Options.NetProblem(false));

        var (join, _) = Split("--join", "[::1]:40121", "--password", "pw", "--config", "meitou.guest.json");
        Assert.Equal("[::1]:40121", join.Join);
        Assert.Equal(("pw", "meitou.guest.json"), (join.Password, join.ConfigFile));
        Assert.Null(join.NetProblem(screenshot: false));
    }

    [Theory]
    [InlineData("--host and --join do not go together.", "--host", "--join", "localhost")]
    [InlineData("--join plays the host's game: drop --new-game, --load.", "--join", "localhost", "--new-game", "--load", "save")]
    [InlineData("--join plays the host's game: drop --screenshot.", "--join", "localhost", "--screenshot-run")]
    [InlineData("--host shares an interactive game: drop --sandbox, --duel.", "--host", "--sandbox", "--duel")]
    [InlineData("--join a b: not an address (host, host:port, [v6]:port).", "--join", "a b")]
    [InlineData("--name: 1 to 24 characters, no control characters.", "--host", "--name", "   ")]
    [InlineData("--name: 1 to 24 characters, no control characters.", "--join", "h", "--name", "a name that is far too long")]
    [InlineData("--password: at most 32 characters.", "--host", "--password", "0123456789012345678901234567890123")]
    [InlineData("--name and --password need --host or --join.", "--name", "Ana")]
    public void Clashing_options_are_refused_with_a_reason(string reason, params string[] args)
    {
        bool screenshot = args.Contains("--screenshot-run");
        var (options, _) = Split([.. args.Where(a => a != "--screenshot-run")]);
        Assert.Equal(reason, options.NetProblem(screenshot));
    }

    [Fact]
    public void A_host_may_start_its_game_from_the_command_line()
    {
        Assert.Null(Split("--host", "--load", "save").Options.NetProblem(false));
        Assert.Null(Split("--host", "40120", "--new-game", "Wanderer").Options.NetProblem(false));
        Assert.Equal("--host shares an interactive game: drop --screenshot.", Split("--host").Options.NetProblem(true));
    }
}
