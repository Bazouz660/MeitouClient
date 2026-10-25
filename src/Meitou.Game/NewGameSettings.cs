using Meitou.Data.Save;
using Meitou.Simulation;

namespace Meitou.Game;

/// <summary>
/// Per-world difficulty options. The seven sliders are saved under Meitou keys (missing ones use neutral values); the two ticks under the original's CAMERA bools
/// (<c>GameplayOptions</c> load <c>FUN_1403eef20</c>, docs/game/character-stats.md "New-game advanced options").
/// </summary>
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
    /// <summary>A tick under the sliders: CAMERA bool key, label, the loader's default, help line (-1: the original sets none).</summary>
    internal sealed record Tick(string Key, string Label, bool Default, int HelpLine);
    public static readonly Tick[] Ticks =
    [
        new("bl", "Bandits loot the player", true, 564),
        // Nothing in the original reads it (docs/game/character-stats.md); kept and saved as it is.
        new("ep", "Easy prospecting", false, -1),
    ];
    readonly float[] values;
    readonly bool[] ticks;
    public NewGameSettings() : this(Enumerable.Repeat(1f, Options.Length).ToArray(), Ticks.Select(t => t.Default).ToArray()) { }
    NewGameSettings(float[] values, bool[] ticks) { this.values = values; this.ticks = ticks; }
    public float this[int index] => values[index];
    public bool Ticked(int index) => ticks[index];
    public bool BanditsLoot => ticks[0];
    public NewGameSettings Set(int index, float value)
    {
        var copy = (float[])values.Clone();
        copy[index] = float.IsFinite(value) ? Math.Clamp(value, Options[index].Min, Options[index].Max) : 1;
        return new(copy, ticks);
    }
    public NewGameSettings SetTick(int index, bool value)
    {
        var copy = (bool[])ticks.Clone();
        copy[index] = value;
        return new(values, copy);
    }
    /// <summary>The body options of a new world: these and the Gameplay settings' <paramref name="dismemberment"/>.</summary>
    public PopulationData Apply(PopulationData data, Meitou.Simulation.Bodies.Dismemberment dismemberment) => data.WithBodyOptions(data.BodyOptions with
    {
        HungerTime = this[0], ChanceOfDeath = this[1], GlobalDamageMultiplier = this[2], Dismemberment = dismemberment,
    });
    /// <summary>Writes the options into a save. <c>bl</c> is the world's too (<c>PlayerState.BanditsLoot</c>, which the capture writes): both hold the same value, and an import needs it here.</summary>
    public void Write(SaveCamera camera)
    {
        for (int i = 0; i < Options.Length; i++) camera.Record.Floats["meitou difficulty " + Options[i].Key] = this[i];
        for (int i = 0; i < Ticks.Length; i++) camera.Record.Bools[Ticks[i].Key] = ticks[i];
    }
    public static NewGameSettings Read(SaveCamera camera)
    {
        var settings = new NewGameSettings();
        for (int i = 0; i < Options.Length; i++) settings = settings.Set(i, camera.Record.Floats.GetValueOrDefault("meitou difficulty " + Options[i].Key, 1));
        for (int i = 0; i < Ticks.Length; i++) settings = settings.SetTick(i, camera.Record.Bools.GetValueOrDefault(Ticks[i].Key, Ticks[i].Default));
        return settings;
    }
}
