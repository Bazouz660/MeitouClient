using InternalFormat = Meitou.Rendering.Gpu.InternalFormat;
using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Data.World;
using Meitou.Rendering.Gpu;
using Silk.NET.Vulkan;

namespace Meitou.Rendering;

/// <summary>
/// The game's point and spot lights on the world (docs/render-lights.md): the lamps of every placed building (<see cref="WorldLights"/>,
/// loaded on a worker), binned each frame into a grid of <see cref="Cells"/>² cells of <see cref="CellSize"/> around the eye and uploaded as three
/// textures, which <c>kenshiLight</c> (<see cref="LampShaders"/>) and the probe trace read. Interior lamps are clipped to their building's
/// mask; the probe trace also shadows their bounce contribution. Render thread only.
/// </summary>
internal sealed class WorldLamps : IDisposable
{
    public const int Cells = 64, IndexWidth = 1024, IndexRows = 16, MaxLamps = 1024;
    public const float CellSize = 128;
    internal const int MaxRoomEdges = 32, DataWidth = 5 + MaxRoomEdges;
    // Allow for the small offset between collision masks and visible wall/floor surfaces.
    const float RoomSurfaceMargin = 0.5f;

    readonly GpuContext ctx;
    readonly Task<WorldLight[]> loading;
    WorldLight[]? lamps;
    readonly GpuTexture[] cells, index, data;
    readonly GpuSampler nearest;
    readonly float[] cellValues = new float[Cells * Cells * 2];
    readonly float[] indexValues = new float[IndexWidth * IndexRows];
    readonly Vector4[] dataValues = new Vector4[MaxLamps * DataWidth];
    readonly List<(int Lamp, InteriorVolume? Room)> near = [];
    readonly Dictionary<string, uint> emitters = new(StringComparer.Ordinal);
    readonly int[] counts = new int[Cells * Cells];
    int slot;
    Vector4 grid;
    readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>Whether the lamps light the world (the bench's <c>lights</c> switch).</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>The lamps of the world, once loaded; and those in this frame's grid.</summary>
    public int Count => lamps?.Length ?? 0;
    public int InGrid { get; private set; }

    public WorldLamps(GpuContext ctx, Func<IReadOnlyList<WorldLight>> load)
    {
        this.ctx = ctx;
        loading = Task.Run(() => load().Where(l => l.Intensity > 0 && l.Radius > 0).ToArray());
        int frames = ctx.Device.Frames.Count;
        cells = new GpuTexture[frames];
        index = new GpuTexture[frames];
        data = new GpuTexture[frames];
        const TextureUse use = TextureUse.Sampled | TextureUse.TransferDst;
        for (int i = 0; i < frames; i++)
        {
            cells[i] = ctx.CreateTexture(new TextureDesc(InternalFormat.RG32f, Cells, Cells, Use: use, Name: "lamp cells"));
            index[i] = ctx.CreateTexture(new TextureDesc(InternalFormat.R32f, IndexWidth, IndexRows, Use: use, Name: "lamp index"));
            data[i] = ctx.CreateTexture(new TextureDesc(InternalFormat.Rgba32f, DataWidth, MaxLamps, Use: use, Name: "lamp data"));
        }
        nearest = ctx.Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.Nearest, TextureMagFilter.Nearest, TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge,
            TextureWrapMode.ClampToEdge, false, DepthFunction.Lequal, false, 1, false, 0));
        var g = ctx.Globals;
        g.PublishUniform("uLightGrid", () => grid);
        g.Publish("uLightCells", () => Sampled(cells[slot]));
        g.Publish("uLightIndex", () => Sampled(index[slot]));
        g.Publish("uLightData", () => Sampled(data[slot]));
    }

    SampledTexture Sampled(GpuTexture t) => new(nearest, t.View());

    /// <summary>The textures of this frame's slot, for a compute pass that binds them itself (the probe trace).</summary>
    internal (GpuTexture Cells, GpuTexture Index, GpuTexture Data) Current => (cells[slot], index[slot], data[slot]);
    internal GpuSampler NearestSampler => nearest;
    /// <summary>This frame's <c>uLightGrid</c> (w 0: no lamps).</summary>
    internal Vector4 Grid => grid;
    internal uint Emitter(string placementId) => emitters.GetValueOrDefault(placementId);

    /// <summary>Bins the lamps around <paramref name="eye"/> for this frame (the pulsing and shimmering ones at their power now).</summary>
    public void Update(Vector3 eye, WorldObjectRenderer? objects = null)
    {
        slot = ctx.Frame.Slot;
        grid = default;
        InGrid = 0;
        emitters.Clear();
        if (!Enabled) return;
        if (lamps is null)
        {
            if (!loading.IsCompleted) return;
            if (loading.IsFaulted) { Console.Error.WriteLine($"lamps     not loaded: {loading.Exception?.InnerException?.Message}"); lamps = []; return; }
            lamps = loading.Result;
            Console.WriteLine($"lamps     {lamps.Length} lights ({lamps.Count(l => l.Interior)} interior)");
        }
        if (lamps.Length == 0) return;

        float x0 = MathF.Floor(eye.X / CellSize) * CellSize - Cells / 2 * CellSize, z0 = MathF.Floor(eye.Z / CellSize) * CellSize - Cells / 2 * CellSize;
        float x1 = x0 + Cells * CellSize, z1 = z0 + Cells * CellSize;
        near.Clear();
        for (int i = 0; i < lamps.Length && near.Count < MaxLamps; i++)
        {
            var l = lamps[i];
            if (l.Position.X + l.Radius < x0 || l.Position.X - l.Radius > x1 || l.Position.Z + l.Radius < z0 || l.Position.Z - l.Radius > z1) continue;
            var room = l.Interior ? objects?.InteriorFor(l.PlacementId) : null;
            // An unloaded or unrepresentable room must not turn its lights into unbounded outdoor lamps.
            if (l.Interior && (room is null || room.Footprint.Length is < 3 or > MaxRoomEdges)) continue;
            near.Add((i, room));
            if (room is not null && !emitters.ContainsKey(l.PlacementId)) emitters.Add(l.PlacementId, (uint)emitters.Count + 1);
        }
        if (near.Count == 0) return;

        // The lamps' rows: position and radius; radiance (the game's power, its effect now) and type; spot direction, cos of the half cones, falloff.
        float t = (float)(clock.Elapsed.TotalSeconds * 60);
        for (int k = 0; k < near.Count; k++)
        {
            var (lamp, room) = near[k];
            var l = lamps[lamp];
            float power = l.Intensity * WorldLights.EffectFactor(l.Effect, t);
            float inner = MathF.Min(l.InnerAngle, l.OuterAngle), outer = MathF.Max(l.InnerAngle, l.OuterAngle);
            int row = k * DataWidth;
            dataValues[row] = new Vector4(l.Position, l.Radius);
            dataValues[row + 1] = new Vector4(l.Colour * power, l.Type == WorldLightType.Spot ? 1 : 0);
            dataValues[row + 2] = new Vector4(l.Direction, MathF.Cos(outer * 0.5f));
            dataValues[row + 3] = new Vector4(MathF.Cos(inner * 0.5f), l.Falloff > 0 ? l.Falloff : 1, room?.Footprint.Length ?? 0, Emitter(l.PlacementId));
            if (room is null) continue;
            dataValues[row + 4] = new Vector4(room.YMin - RoomSurfaceMargin, room.YMax + RoomSurfaceMargin, 0, 0);
            for (int e = 0; e < room.Footprint.Length; e++)
            {
                var at = room.Footprint[e];
                var edge = room.Footprint[(e + 1) % room.Footprint.Length] - at;
                var inward = Vector2.Normalize(new Vector2(-edge.Y, edge.X));
                dataValues[row + 5 + e] = new Vector4(inward, -Vector2.Dot(inward, at) + RoomSurfaceMargin, 0);
            }
        }

        // Each cell lists the lamps whose reach overlaps it (counted, then placed).
        Array.Clear(counts);
        for (int pass = 0; pass < 2; pass++)
        {
            int total = 0;
            if (pass == 1)
            {
                for (int c = 0; c < counts.Length; c++)
                {
                    cellValues[c * 2] = total;
                    cellValues[c * 2 + 1] = 0;
                    total += counts[c];
                }
                if (total > IndexWidth * IndexRows) { grid = default; return; }
            }
            for (int k = 0; k < near.Count; k++)
            {
                var l = lamps[near[k].Lamp];
                int cx0 = Math.Max((int)MathF.Floor((l.Position.X - l.Radius - x0) / CellSize), 0), cx1 = Math.Min((int)MathF.Floor((l.Position.X + l.Radius - x0) / CellSize), Cells - 1);
                int cz0 = Math.Max((int)MathF.Floor((l.Position.Z - l.Radius - z0) / CellSize), 0), cz1 = Math.Min((int)MathF.Floor((l.Position.Z + l.Radius - z0) / CellSize), Cells - 1);
                for (int cz = cz0; cz <= cz1; cz++)
                    for (int cx = cx0; cx <= cx1; cx++)
                    {
                        int c = cz * Cells + cx;
                        if (pass == 0) { counts[c]++; continue; }
                        int at = (int)cellValues[c * 2] + (int)cellValues[c * 2 + 1];
                        indexValues[at] = k;
                        cellValues[c * 2 + 1]++;
                    }
            }
        }

        var full = new PixelRect(0, 0, Cells, Cells);
        ctx.Uploads.Write(cells[slot], 0, 0, full, MemoryMarshal.AsBytes(cellValues.AsSpan()));
        ctx.Uploads.Write(index[slot], 0, 0, new PixelRect(0, 0, IndexWidth, IndexRows), MemoryMarshal.AsBytes(indexValues.AsSpan()));
        ctx.Uploads.Write(data[slot], 0, 0, new PixelRect(0, 0, DataWidth, (uint)near.Count), MemoryMarshal.AsBytes(dataValues.AsSpan(0, near.Count * DataWidth)));
        grid = new Vector4(x0, z0, CellSize, Cells);
        InGrid = near.Count;
    }

    public string Describe() => lamps is null ? "loading" : $"{lamps.Length} lamps, {InGrid} around the eye" + (Enabled ? "" : ", off");

    public void Dispose()
    {
        foreach (var t in cells.Concat(index).Concat(data)) t.Dispose();
    }
}
