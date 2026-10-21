using System.Text.Json.Nodes;
using Meitou.Game;

namespace Meitou.Tests.Gameplay;

/// <summary><see cref="UserConfig"/> loading (docs/launcher.md "Graphics"): one file shared by every installed version, so a release
/// must keep what it cannot read aside and what it does not know as it was.</summary>
public class UserConfigTests : IDisposable
{
    readonly string dir = Directory.CreateTempSubdirectory("meitou-config").FullName;
    string File => Path.Combine(dir, UserConfig.FileName);

    public void Dispose() => Directory.Delete(dir, recursive: true);

    // Loads the text, saves it back, returns the config, the saved JSON and the backups beside it.
    (UserConfig Config, JsonObject Saved, string[] Backups) RoundTrip(string json)
    {
        System.IO.File.WriteAllText(File, json);
        var config = UserConfig.Load(File);
        config.Save();
        return (config, (JsonObject)JsonNode.Parse(System.IO.File.ReadAllText(File))!, Directory.GetFiles(dir, "meitou.user.*.bak.json"));
    }

    [Fact]
    public void A_file_from_before_versions_keeps_its_values_and_is_saved_as_version_1()
    {
        var (config, saved, backups) = RoundTrip("""{"fpsLimit": 90, "graphics": {"Object draw distance": 30000}}""");

        Assert.Equal(90, config.FpsLimit);
        Assert.Equal(30000, config.Graphics["Object draw distance"]);
        Assert.Equal(1, (int)saved["version"]!);
        Assert.Empty(backups);
    }

    [Fact]
    public void A_value_of_the_wrong_type_takes_its_default_and_the_rest_stay_with_a_copy_kept_aside()
    {
        const string json = """{"fpsLimit": "fast", "vSync": 3, "fullScreen": false, "graphics": {"Object draw distance": "far", "Grass density x": 1.5}}""";
        var (config, _, backups) = RoundTrip(json);

        Assert.Equal(new UserConfig().FpsLimit, config.FpsLimit);
        Assert.False(config.VSync);
        Assert.False(config.FullScreen);
        Assert.Equal(1.5f, config.Graphics["Grass density x"]);
        Assert.False(config.Graphics.ContainsKey("Object draw distance"));
        Assert.Equal(json, System.IO.File.ReadAllText(Assert.Single(backups)));
    }

    [Fact]
    public void A_newer_release_file_keeps_its_version_and_unknown_keys()
    {
        var (config, saved, backups) = RoundTrip("""{"version": 3, "fpsLimit": 60, "brandNewKey": {"a": [1, 2]}}""");

        Assert.Equal(60, config.FpsLimit);
        Assert.Equal(3, (int)saved["version"]!);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"a": [1, 2]}"""), saved["brandNewKey"]));
        Assert.Empty(backups);
    }

    [Fact]
    public void Unparsable_json_gives_the_defaults_with_a_copy_kept_aside()
    {
        const string json = """{"fpsLimit": 60,""";
        var (config, _, backups) = RoundTrip(json);

        Assert.Equal(new UserConfig().FpsLimit, config.FpsLimit);
        Assert.Equal(json, System.IO.File.ReadAllText(Assert.Single(backups)));
    }
}
