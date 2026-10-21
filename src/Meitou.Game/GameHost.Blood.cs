using System.Collections.Concurrent;
using Meitou.Audio;
using Meitou.Data.Fcs;
using Meitou.Data.Ogre;
using Meitou.Data.Save;
using Meitou.Data.World;
using Meitou.Rendering;
using Meitou.Rendering.Gpu;
using Meitou.Simulation;

namespace Meitou.Game;

/// <summary>
/// The blood on the ground (docs/game/blood-decals.md): with the game's <c>Blood</c> setting on, the simulation's <see cref="World.BloodMarks"/> become terrain decals in
/// a <see cref="TerrainDecalStore"/> that ages with game time, drawn by the world frame's <see cref="TerrainDecalRenderer"/> within <c>Decal Range</c> at
/// <c>Decal Resolution</c> (the install's <c>settings.cfg</c>). Display only: the simulation is not affected.
/// </summary>
sealed partial class GameHost
{
    /// <summary>The <c>Blood</c> resource group's folders by decal type (<see cref="TerrainDecalType"/>), the game's names for them.</summary>
    static readonly string[] BloodFolders = ["line", "splash", "pool", "smear"];

    /// <summary>How far above the ground a mark is still left on it (an engine rule: a character on an upper floor of a building bleeds onto the floor, which has no decals).</summary>
    const float BloodAboveGround = 20;

    TerrainDecalStore? decalStore;
    World? bloodFor;
    long bloodTick;
    readonly ConcurrentQueue<BloodMark> bloodQueue = new();
    readonly Random bloodRandom = new();

    /// <summary>Makes the store and the renderer when the install's <c>Blood</c> setting is on (missing: on).</summary>
    void SetUpBlood(GpuContext context)
    {
        gpu.Debris ??= new DebrisRenderer(context, gpu.Objects?.Resolver, assets.Find);   // made now: its shaders compile here, not at the first burst
        if (gpu.Decals is not null || AudioSystem.Setting(install, "Blood", 1) == 0) return;
        var files = BloodTextureFiles(assets.Configured);
        decalStore = new TerrainDecalStore([.. files.Select(list => (IReadOnlyList<(int, int)>)[.. list.Select(DdsSize)])]);
        gpu.Decals = TerrainDecalRenderer.Create(context, files);
        gpu.Decals.Resolution = (int)AudioSystem.Setting(install, "Decal Resolution", 1024, 512, 2048);
        gpu.Decals.Range = AudioSystem.Setting(install, "Decal Range", 1000, 0, 5000);
        gpu.DecalStore = decalStore;
    }

    /// <summary>
    /// Once a frame: luckyren Mode handed to the world, the marks of the ticks since the last frame into the store (bursts thrown), and the decals aged and the
    /// pieces moved by the game time they took.
    /// </summary>
    void UpdateBlood()
    {
        var world = session.World;
        // A client follows the host's luckyren Mode (its replica world runs no combat, the setting picks what the marks do here).
        bool luckyren = role == NetRole.Client ? netClient?.SessionRules?.LuckyrenMode == true : config.LuckyrenMode;
        world.Luckyren = luckyren;
        if (decalStore is null && !luckyren && pieces.Count == 0)
        {
            world.BloodMarks = null;
            return;
        }
        if (!ReferenceEquals(bloodFor, world)) BloodForNewWorld(null);
        world.BloodMarks = bloodQueue;
        float tick = world.TickSeconds;
        while (bloodQueue.TryDequeue(out var m))
        {
            if (m.Kind == BloodMarkKind.Explosion)
            {
                Burst(m, world);
                continue;
            }
            if (decalStore is not { } store) continue;
            if (m.Position.Y > gpu.Terrain.HeightAt(m.Position.X, m.Position.Z) + BloodAboveGround) continue;
            uint opaque = 0xFF000000 | Abgr(m.Colour);
            long who = (long)(uint)m.Who.Slot << 32 | (uint)m.Who.Generation;
            switch (m.Kind)
            {
                case BloodMarkKind.Pool: store.Bleed(who, m.Position, m.Amount, tick, opaque, bloodRandom); break;
                case BloodMarkKind.Smear:
                    store.Add(TerrainDecalType.Smear, m.Position, m.Direction, 0.2f, (uint)(Math.Clamp(m.Amount, 0, 1) * 255) << 24 | Abgr(m.Colour), TerrainDecalStore.NewLife, bloodRandom);
                    break;
                case BloodMarkKind.Drip: store.Add(TerrainDecalType.Pool, m.Position, m.Direction, m.Amount, opaque, TerrainDecalStore.NewLife, bloodRandom); break;
                case BloodMarkKind.Splash: store.Add(TerrainDecalType.Splash, m.Position, m.Direction, m.Amount, opaque, TerrainDecalStore.NewLife, bloodRandom); break;
            }
        }
        long ticks = world.Tick - bloodTick;
        bloodTick = world.Tick;
        if (ticks > 0 && decalStore is { } aging && gpu.Decals is { } decals) aging.Age(ticks * tick, decals.Drawn);
        UpdateDebris(world, Math.Max(ticks, 0) * tick);
    }

    /// <summary>
    /// The store emptied for the session's world and hooked to its marks, then given the decals of the save's TERRAIN_DECALS record <paramref name="saved"/> when the
    /// world was loaded from one (the load calls it before the new world's first frame).
    /// </summary>
    void BloodForNewWorld(FcsRecord? saved)
    {
        var world = session.World;
        pieces.Clear();
        gpu.DebrisPieces.Clear();
        bloodQueue.Clear();
        bloodFor = world;
        bloodTick = world.Tick;
        if (decalStore is not { } store) return;
        store.Clear();
        world.BloodMarks = bloodQueue;
        if (saved is not null) store.Load(SaveDecals.Read(saved));
    }

    /// <summary>The store's decals into the save's TERRAIN_DECALS record (with <c>Blood</c> off the record keeps what the loaded save had).</summary>
    void WriteDecals(SaveGame game)
    {
        if (decalStore is { } store && game.Decals is { } record) SaveDecals.Write(record, store.All);
    }

    /// <summary>0xRRGGBB as the decal's ABGR colour bytes (alpha 0).</summary>
    static uint Abgr(uint rgb) => (rgb >> 16 & 0xFF) | (rgb & 0xFF00) | (rgb & 0xFF) << 16;

    /// <summary>The <c>.dds</c> files of the <c>Blood</c> group's folders, by <see cref="BloodFolders"/> type, in registration order and by name within a folder.</summary>
    static List<IReadOnlyList<string>> BloodTextureFiles(OgreScriptResources resources)
    {
        var files = BloodFolders.Select(_ => new List<string>()).ToList();
        foreach (var location in resources.Locations)
        {
            if (!location.Group.Equals("Blood", StringComparison.Ordinal) || !Directory.Exists(location.Path)) continue;
            int type = Array.FindIndex(BloodFolders, f => f.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(location.Path)), StringComparison.OrdinalIgnoreCase));
            if (type < 0) continue;
            files[type].AddRange(Directory.EnumerateFiles(location.Path, "*.dds").Order(StringComparer.OrdinalIgnoreCase));
        }
        return [.. files.Cast<IReadOnlyList<string>>()];
    }

    /// <summary>A DDS file's size in texels from its header (0 × 0 when unreadable).</summary>
    static (int Width, int Height) DdsSize(string path)
    {
        try
        {
            Span<byte> header = stackalloc byte[20];
            using var file = File.OpenRead(path);
            file.ReadExactly(header);
            return (BitConverter.ToInt32(header[16..]), BitConverter.ToInt32(header[12..]));
        }
        catch (IOException) { return (0, 0); }
    }
}
