using System.Collections.Concurrent;
using Meitou.Data;
using Meitou.Data.Characters;
using Meitou.Simulation;

namespace Meitou.Net.Replication;

/// <summary>Turns the look ids of snapshot parts into looks on a client (docs/net.md, "Looks").</summary>
public interface ILookResolver
{
    /// <summary>The look of <paramref name="lookId"/>; null for 0, an id not defined yet, a look still being built, or a recipe that gave none.</summary>
    CharacterAppearance? Resolve(uint lookId);

    /// <summary>Takes a <c>LookDefinition</c>. Ids are never reused within a connection, so a repeated id keeps its first recipe.</summary>
    void Define(uint lookId, AppearanceRecipe recipe);
}

/// <summary>
/// The client's looks, kept for the whole connection (look ids are never reused within a <c>NetHost</c>, so nothing is evicted). <see cref="Define"/> starts the
/// build: on the thread pool when <paramref name="background"/> (the default; <see cref="AppearanceRecipe.Build"/> reads body and physics files, and
/// <see cref="GeneratedAppearances"/> is safe to call from several threads), else at once on the caller's thread for a source that is not thread-safe.
/// <see cref="Resolve"/> gives null until the build is done; the mirror keeps the character's last look meanwhile. A build that throws gives a null look (a marker),
/// like a recipe naming a record this client's data lacks.
/// </summary>
public sealed class LookCache(IAppearanceSource source, GameDatabase db, bool background = true) : ILookResolver
{
    sealed class Entry(AppearanceRecipe recipe)
    {
        public readonly AppearanceRecipe Recipe = recipe;
        public volatile CharacterAppearance? Look;
        public volatile bool Ready;
        // Set before the entry is published, so a WhenBuilt on another thread never sees a placeholder.
        public Task Built = Task.CompletedTask;
    }

    readonly ConcurrentDictionary<uint, Entry> entries = [];

    /// <summary>Ids defined so far.</summary>
    public int Count => entries.Count;

    public void Define(uint lookId, AppearanceRecipe recipe)
    {
        if (lookId == 0) throw new NetFormatException("Look 0 cannot be defined.");
        var entry = new Entry(recipe);
        var build = background ? new Task(() => Build(entry)) : null;
        if (build is not null) entry.Built = build;
        if (!entries.TryAdd(lookId, entry)) return;
        if (build is not null) build.Start(TaskScheduler.Default);
        else Build(entry);
    }

    public CharacterAppearance? Resolve(uint lookId) => entries.TryGetValue(lookId, out var e) && e.Ready ? e.Look : null;

    /// <summary>Whether <paramref name="lookId"/> is defined and built (its look may still be null: a marker).</summary>
    public bool IsReady(uint lookId) => entries.TryGetValue(lookId, out var e) && e.Ready;

    /// <summary>The recipe <paramref name="lookId"/> was defined with; null when it was not.</summary>
    public AppearanceRecipe? RecipeOf(uint lookId) => entries.TryGetValue(lookId, out var e) ? e.Recipe : null;

    /// <summary>Completes when <paramref name="lookId"/>'s build has finished (at once for an unknown id or a synchronous cache).</summary>
    public Task WhenBuilt(uint lookId) => entries.TryGetValue(lookId, out var e) ? e.Built : Task.CompletedTask;

    void Build(Entry entry)
    {
        try { entry.Look = entry.Recipe.Build(source, db); }
        catch (Exception) { entry.Look = null; }   // host data a client cannot build from is a marker, never a crash on a pool thread
        entry.Ready = true;
    }
}
