using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.World;

namespace Meitou.Navigation;

/// <summary>Where a zone's mesh came from.</summary>
public enum NavMeshOrigin { Cache, Built }

/// <summary>What a worker reports for one finished zone.</summary>
public sealed record NavZoneReady(ZoneCoordinate Zone, NavMeshOrigin Origin, double Milliseconds, int Polygons);

/// <summary>
/// Builds zone meshes off the simulation thread and publishes them to a <see cref="NavmeshWalkability"/> (docs/simulation.md: path queries and navmesh
/// building run on their own threads). A finished mesh goes live at once, whenever that is: the walkability's world is swapped atomically, so a query
/// sees the old world or the new one, and the simulation can see a zone appear in the middle of a tick.
/// <see cref="Request"/> queues a zone; a worker takes it from the cache (keyed by the zone's building hash and the settings) or gathers, builds,
/// prunes and caches it. Several zones queued together are built one after another, each using all cores.
/// </summary>
internal sealed class NavMeshService : IDisposable
{
    readonly GameInstall install;
    readonly GameDatabase db;
    readonly WorldLevelData levels;
    readonly CollisionCache collisions;
    readonly NavRuntimeBuildings? runtime;
    // Bumped by Rebuild: a zone whose build started before the bump is queued again after it publishes (its gather may have missed the change).
    readonly ConcurrentDictionary<ZoneCoordinate, int> rebuilds = new();
    readonly BlockingCollection<ZoneCoordinate> queue = [];
    readonly ConcurrentDictionary<ZoneCoordinate, TaskCompletionSource<NavZoneReady>> pending = new();
    readonly Thread worker;
    readonly object publishLock = new();
    // The loaded zones' meshes with their pruned polygons, for joining kept regions across borders (worker thread and Unload, under publishLock).
    readonly Dictionary<ZoneCoordinate, ZoneNavMesh> full = [];
    volatile bool disposed;

    public NavMeshService(GameInstall install, GameDatabase db, WorldLevelData levels, NavmeshWalkability walkability, NavBuildSettings? settings = null, NavMeshCache? cache = null,
        NavRuntimeBuildings? runtime = null)
    {
        this.install = install;
        this.db = db;
        this.levels = levels;
        this.runtime = runtime;
        Walkability = walkability;
        Settings = settings ?? new NavBuildSettings();
        Cache = cache ?? new NavMeshCache();
        collisions = new CollisionCache(install);
        worker = new Thread(Work) { Name = "NavMeshBuilder", IsBackground = true, Priority = ThreadPriority.BelowNormal };
        worker.Start();
    }

    public NavmeshWalkability Walkability { get; }
    public NavBuildSettings Settings { get; }
    public NavMeshCache Cache { get; }

    /// <summary>Raised on the worker thread after a zone's mesh was published.</summary>
    public event Action<NavZoneReady>? ZoneReady;

    /// <summary>Queues a zone (once); the task completes when its mesh is published.</summary>
    public Task<NavZoneReady> Request(ZoneCoordinate zone)
    {
        if (Walkability.World.Find(zone) is { } loaded) return Task.FromResult(new NavZoneReady(zone, NavMeshOrigin.Cache, 0, loaded.PolygonCount));
        var tcs = new TaskCompletionSource<NavZoneReady>(TaskCreationOptions.RunContinuationsAsynchronously);
        var existing = pending.GetOrAdd(zone, tcs);
        if (ReferenceEquals(existing, tcs)) queue.Add(zone);
        return existing.Task;
    }

    /// <summary>
    /// Builds a zone again although its mesh is loaded (the buildings round it changed, <see cref="NavRuntimeBuildings"/>): the new mesh replaces the old one when it is
    /// published, which is when the task completes. A zone not loaded is not built (it is built with the overlay when it is asked for).
    /// </summary>
    public Task<NavZoneReady> Rebuild(ZoneCoordinate zone)
    {
        rebuilds.AddOrUpdate(zone, 1, (_, n) => n + 1);
        // A queued or running build sees the bump and builds again after it publishes.
        if (Walkability.World.Find(zone) is null) return pending.TryGetValue(zone, out var queued) ? queued.Task : Task.FromCanceled<NavZoneReady>(new CancellationToken(true));
        var tcs = new TaskCompletionSource<NavZoneReady>(TaskCreationOptions.RunContinuationsAsynchronously);
        var existing = pending.GetOrAdd(zone, tcs);
        if (ReferenceEquals(existing, tcs)) queue.Add(zone);
        return existing.Task;
    }

    /// <summary>
    /// Keeps the region of a loaded zone's pruned polygons that <paramref name="at"/> stands on, and what it reaches across the loaded borders, and publishes
    /// it: a walker on ground its zone's own seeds do not reach, whose way to a seed leads through a zone that is not loaded (a save loaded in the middle of
    /// a zone, docs/game/pathfinding.md "Zone borders"). Not cached: the cache holds the zone's own pruning. True when the kept set grew.
    /// </summary>
    public bool KeepAt(Vector3 at)
    {
        var zone = WorldLayout.ZoneOf(at.X, at.Z);
        lock (publishLock)
        {
            if (!full.TryGetValue(zone, out var mesh) || Walkability.World.Find(zone) is null || !SeedPruner.Extend(mesh, [at], Settings)) return false;
            var world = Walkability.World;
            foreach (var other in ZoneKeptClosure.Spread(full, zone, Settings))
                if (other != zone && world.Find(other) is not null) world = world.With(full[other].WithoutPruned());
            Walkability.SetWorld(world.With(mesh.WithoutPruned()));
            return true;
        }
    }

    /// <summary>
    /// Drops a zone's mesh from the loaded world (the cache file stays). A queued build of it is skipped and a running one is cached but not published;
    /// their task is cancelled.
    /// </summary>
    public void Unload(ZoneCoordinate zone)
    {
        TaskCompletionSource<NavZoneReady>? tcs;
        lock (publishLock)
        {
            Walkability.SetWorld(Walkability.World.Without(zone));
            full.Remove(zone);
            pending.TryRemove(zone, out tcs);
        }
        tcs?.TrySetCanceled();
    }

    void Work()
    {
        using var gatherer = new ZoneGeometryGatherer(install, db, levels, collisions, runtime);
        uint settingsHash = NavMeshCache.SettingsHash(Settings) * 16777619 ^ NavMeshCache.ContentHash(install, db);
        foreach (var zone in queue.GetConsumingEnumerable())
        {
            if (disposed) break;
            // Unloaded while it waited, or published by an earlier entry.
            if (!pending.ContainsKey(zone)) continue;
            var watch = Stopwatch.StartNew();
            try
            {
                int rebuildVersion = rebuilds.GetValueOrDefault(zone);
                uint hash = gatherer.BuildingHash(zone);
                var mesh = Cache.TryLoad(zone.X, zone.Y, hash, settingsHash);
                var origin = NavMeshOrigin.Cache;
                if (mesh is null)
                {
                    var geometry = gatherer.Gather(zone, buildingHash: hash);
                    // Pruned by the zone's own seeds only, so the cached mesh does not depend on which neighbours were loaded (ZoneKeptClosure joins them).
                    mesh = NavMeshPipeline.BuildZone(gatherer, geometry, Settings, out _);
                    origin = NavMeshOrigin.Built;
                    try { Cache.Save(mesh, hash, settingsHash); }
                    catch (IOException) { /* a read-only or full disk only costs the next build */ }
                }
                mesh.Terrain = gatherer.ZoneHeights(zone);
                int polygons;
                TaskCompletionSource<NavZoneReady>? tcs;
                lock (publishLock)
                {
                    // Unloaded while it was built: only the cache keeps it (in `full` it would spread kept regions into its neighbours).
                    if (!pending.TryRemove(zone, out tcs)) continue;
                    full[zone] = mesh;
                    var world = Walkability.World;
                    // Neighbours whose kept regions grew from this zone's border are published again (With replaces them and relinks their borders).
                    foreach (var other in ZoneKeptClosure.Spread(full, zone, Settings))
                        if (other != zone && world.Find(other) is not null) world = world.With(full[other].WithoutPruned());
                    var own = mesh.WithoutPruned();
                    polygons = own.PolygonCount;
                    Walkability.SetWorld(world.With(own));
                }
                var ready = new NavZoneReady(zone, origin, watch.Elapsed.TotalMilliseconds, polygons);
                tcs.TrySetResult(ready);
                ZoneReady?.Invoke(ready);
                // The buildings changed while this one was built: it may have missed it.
                if (rebuilds.GetValueOrDefault(zone) != rebuildVersion) _ = Rebuild(zone);
            }
            catch (Exception e)
            {
                if (pending.TryRemove(zone, out var tcs)) tcs.TrySetException(e);
            }
        }
    }

    public void Dispose()
    {
        disposed = true;
        queue.CompleteAdding();
        worker.Join(TimeSpan.FromSeconds(30));
        foreach (var tcs in pending.Values) tcs.TrySetCanceled();
    }
}
