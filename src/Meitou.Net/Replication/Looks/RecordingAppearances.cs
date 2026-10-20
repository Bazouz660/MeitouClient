using System.Runtime.CompilerServices;
using Meitou.Data;
using Meitou.Data.Characters;
using Meitou.Data.Fcs;
using Meitou.Simulation;

namespace Meitou.Net.Replication;

/// <summary>
/// An <see cref="IAppearanceSource"/> that forwards every call to <paramref name="inner"/> and remembers, per look it returns, the call that made it as an
/// <see cref="AppearanceRecipe"/> (docs/net.md, "Looks"). It never changes a look or a result: every member, the default-bodied ones included, is the inner
/// source's. Recipes are held weakly by the look, so a look that leaves play takes its recipe with it. Thread-safe (population and save loading call it from
/// workers).
/// </summary>
public sealed class RecordingAppearances(IAppearanceSource inner) : IAppearanceSource
{
    readonly ConditionalWeakTable<CharacterAppearance, AppearanceRecipe> recipes = [];

    /// <summary>The wrapped source.</summary>
    public IAppearanceSource Inner => inner;

    public CharacterAppearance? Create(string recordId, string? factionId, int seed) =>
        Record(inner.Create(recordId, factionId, seed), () => new AppearanceRecipe.Rolled(recordId, factionId, seed));

    public CharacterAppearance? Create(string recordId, Loadout loadout) =>
        Record(inner.Create(recordId, loadout), () => new AppearanceRecipe.Kit(recordId, LoadoutWire.From(loadout)));

    public string? RandomName(bool female, ulong roll) => inner.RandomName(female, roll);

    public FcsRecord? Sanitize(FcsRecord proposed, GameRecord race) => inner.Sanitize(proposed, race);

    /// <summary>The recipe of a look this source made; null for one it never returned.</summary>
    public AppearanceRecipe? RecipeOf(CharacterAppearance look) => recipes.TryGetValue(look, out var recipe) ? recipe : null;

    CharacterAppearance? Record(CharacterAppearance? look, Func<AppearanceRecipe> recipe)
    {
        // An animal's look is its species' shared instance whatever the arguments (GeneratedAppearances): one recipe names the species.
        if (look is not null) recipes.AddOrUpdate(look, look.Animal is { } animal ? new AppearanceRecipe.Rolled(animal.StringId, null, 0) : recipe());
        return look;
    }
}
