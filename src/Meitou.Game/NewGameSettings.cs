using Meitou.Data.Save;
using Meitou.Simulation;

namespace Meitou.Game;

/// <summary>Per-world difficulty options. Missing save keys use neutral values; these keys are Meitou extensions.</summary>
internal sealed record NewGameSettings
{
    internal sealed record Option(string Key, string Label, float Min, float Max, int HelpLine);
    public static readonly Option[] Options =
    [
        new("hunger", "Hunger time", .25f, 8, 557),
        new("death", "Chance of death", .5f, 4, 558),
        new("damage", "Global damage multiplier", .5f, 4, 559),
        new("production", "Production speed", .5f, 2, 560),
        new("research", "Research speed", .5f, 2, 561),
        new("building", "Building speed", .5f, 2, 562),
        new("nests", "Number of nests multiplier", .5f, 4, 563),
    ];
    readonly float[] values;
    public NewGameSettings() => values = Enumerable.Repeat(1f, Options.Length).ToArray();
    NewGameSettings(float[] values) => this.values = values;
    public float this[int index] => values[index];
    public NewGameSettings Set(int index, float value)
    {
        var copy = (float[])values.Clone();
        copy[index] = float.IsFinite(value) ? Math.Clamp(value, Options[index].Min, Options[index].Max) : 1;
        return new(copy);
    }
    /// <summary>The body options of a new world: these and the Gameplay settings' <paramref name="dismemberment"/>.</summary>
    public PopulationData Apply(PopulationData data, Meitou.Simulation.Bodies.Dismemberment dismemberment) => data.WithBodyOptions(data.BodyOptions with
    {
        HungerTime = this[0], ChanceOfDeath = this[1], GlobalDamageMultiplier = this[2], Dismemberment = dismemberment,
    });
    public void Write(SaveCamera camera)
    {
        for (int i = 0; i < Options.Length; i++) camera.Record.Floats["meitou difficulty " + Options[i].Key] = this[i];
    }
    public static NewGameSettings Read(SaveCamera camera)
    {
        var settings = new NewGameSettings();
        for (int i = 0; i < Options.Length; i++) settings = settings.Set(i, camera.Record.Floats.GetValueOrDefault("meitou difficulty " + Options[i].Key, 1));
        return settings;
    }
}
