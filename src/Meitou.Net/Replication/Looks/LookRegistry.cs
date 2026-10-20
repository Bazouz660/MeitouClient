using System.Runtime.CompilerServices;
using Meitou.Data.Characters;

namespace Meitou.Net.Replication;

/// <summary>
/// The host's look ids (docs/net.md, "Looks"): 0 is no look; every other look object gets one id the first time a snapshot names it, kept while the object lives
/// and never reused within the registry (one per <c>NetHost</c>), so a client's <see cref="LookCache"/> stays valid for the whole connection and a species' shared
/// animal look is sent once. The recipe is taken at that first time: the recorded call (<see cref="RecordingAppearances.RecipeOf"/>), else
/// <see cref="AppearanceRecipe.FromLook"/>, with the look's <see cref="CharacterAppearance.Shape"/> when set. Which ids a client already has is the replicator's.
/// Frame thread only.
/// </summary>
public sealed class LookRegistry(RecordingAppearances recording)
{
    sealed class Entry(uint id) { public readonly uint Id = id; }

    readonly ConditionalWeakTable<CharacterAppearance, Entry> ids = [];
    // By id: the look (weak, so a look that left play is dropped at the next sweep) and its recipe (null: none, the client shows a marker).
    readonly Dictionary<uint, (WeakReference<CharacterAppearance> Look, AppearanceRecipe? Recipe)> byId = [];
    uint next = 1;
    int sweepAt = MinSweep;
    const int MinSweep = 1024;

    /// <summary>Ids whose look may still be in play.</summary>
    public int Count => byId.Count;

    /// <summary>The look's id; 0 for null.</summary>
    public uint IdOf(CharacterAppearance? look)
    {
        if (look is null) return 0;
        if (ids.TryGetValue(look, out var known)) return known.Id;
        var entry = new Entry(next++);
        ids.Add(look, entry);
        var recipe = recording.RecipeOf(look) ?? AppearanceRecipe.FromLook(look);
        if (recipe is not null && look.Shape is { } shape && look.Animal is null) recipe = recipe with { Shape = shape };
        byId[entry.Id] = (new WeakReference<CharacterAppearance>(look), recipe);
        if (byId.Count >= sweepAt) Sweep();
        return entry.Id;
    }

    /// <summary>The recipe of an id <see cref="IdOf"/> gave; null for 0, an unknown id, a look without one, or a look that has left play.</summary>
    public AppearanceRecipe? RecipeOf(uint id) => byId.TryGetValue(id, out var e) && e.Look.TryGetTarget(out _) ? e.Recipe : null;

    void Sweep()
    {
        foreach (var (id, e) in byId)
            if (!e.Look.TryGetTarget(out _)) byId.Remove(id);
        sweepAt = Math.Max(MinSweep, byId.Count * 2);
    }
}
