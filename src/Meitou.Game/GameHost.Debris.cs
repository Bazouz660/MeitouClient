using System.Numerics;
using Meitou.Data.World;
using Meitou.Rendering;
using Meitou.Rendering.Gpu;
using Meitou.Simulation;

namespace Meitou.Game;

/// <summary>
/// luckyren Mode's burst (docs/game/luckyren.md): an <see cref="BloodMarkKind.Explosion"/> throws the game's bone meshes and the race's severed limbs out of
/// the body, which fly, tumble and bounce on the ground and then lie there as long as a corpse would, and it splashes blood all round (with the
/// <c>Blood</c> setting on). Display only: the pieces move in game time (still while paused) and are not saved.
/// </summary>
sealed partial class GameHost
{
    /// <summary>The nest bones' meshes (<c>data/items/nests</c>; only <c>bone1</c> has an item record).</summary>
    static readonly string[] BoneMeshes = [.. Enumerable.Range(1, 14).Select(i => $"bone{i}.mesh")];

    const int BonesPerBurst = 12, MaxPieces = 400, BurstSplashes = 24;
    const float Gravity = 98, BurstHeight = 10, Bounce = 0.35f, Grip = 0.6f, BurstReach = 60, BoneLength = 2.5f;

    sealed class Piece
    {
        public required string Mesh;
        public Vector3 Position, Velocity, Spin;
        public Quaternion Rotation;
        public Vector3 Extent;
        public float Scale;
        public float Floor;
        public bool Resting, Landed;
        public long Born;
        public uint Colour;
    }

    readonly List<Piece> pieces = [];
    readonly Dictionary<string, string[]> limbMeshes = new(StringComparer.Ordinal);

    /// <summary>A burst: blood all round and the pieces thrown.</summary>
    void Burst(in BloodMark m, World world)
    {
        bool bleeds = m.Amount > 0;
        var blow = m.Direction.LengthSquared() > 1e-6f ? Vector2.Normalize(m.Direction) : Vector2.Zero;
        if (decalStore is { } store && bleeds && m.Position.Y <= gpu.Terrain.HeightAt(m.Position.X, m.Position.Z) + BloodAboveGround)
        {
            uint opaque = 0xFF000000 | Abgr(m.Colour);
            store.Add(TerrainDecalType.Pool, m.Position, RandomDirection(), 1.5f, opaque, TerrainDecalStore.NewLife, bloodRandom);
            for (int i = 0; i < BurstSplashes; i++)
            {
                var d = Vector2.Normalize(RandomDirection() + blow * 0.5f);
                float far = MathF.Sqrt(bloodRandom.NextSingle()) * BurstReach;
                store.Add(TerrainDecalType.Splash, m.Position + new Vector3(d.X, 0, d.Y) * far, d, 0.3f + bloodRandom.NextSingle() * 0.2f, opaque, TerrainDecalStore.NewLife, bloodRandom);
            }
        }
        if (gpu.Debris is not { } debris) return;
        float terrain = gpu.Terrain.HeightAt(m.Position.X, m.Position.Z);
        // On an upper floor the pieces land on its level; the terrain is beneath the building.
        float floor = m.Position.Y > terrain + BloodAboveGround ? m.Position.Y : float.NaN;
        var limbs = LimbMeshes(m.Race);
        var meshes = new List<string>(limbs);
        for (int i = 0; i < BonesPerBurst; i++) meshes.Add(BoneMeshes[bloodRandom.Next(BoneMeshes.Length)]);
        for (int i = 0; i < meshes.Count; i++)
        {
            string mesh = meshes[i];
            if (debris.Extent(mesh) is not { } size) continue;
            // The nest bones are a beast's (up to 3.4 m long): each made a human bone's length, BoneLength.
            float scale = i < limbs.Length ? 1 : BoneLength * (0.5f + bloodRandom.NextSingle()) / MathF.Max(size.X, MathF.Max(size.Y, size.Z));
            var extent = size * scale;
            var d = Vector2.Normalize(RandomDirection() + blow * 0.6f);
            float speed = 30 + bloodRandom.NextSingle() * 60;
            var spinAxis = Vector3.Normalize(new Vector3(bloodRandom.NextSingle() - 0.5f, bloodRandom.NextSingle() - 0.5f, bloodRandom.NextSingle() - 0.5f) + new Vector3(1e-3f));
            pieces.Add(new Piece
            {
                Mesh = mesh, Scale = scale, Extent = extent, Floor = floor, Born = world.Tick, Colour = bleeds ? 0xFF000000 | Abgr(m.Colour) : 0,
                Position = m.Position + new Vector3(0, BurstHeight, 0),
                Velocity = new Vector3(d.X * speed, 30 + bloodRandom.NextSingle() * 50, d.Y * speed),
                Rotation = Quaternion.Normalize(new Quaternion(bloodRandom.NextSingle() - 0.5f, bloodRandom.NextSingle() - 0.5f, bloodRandom.NextSingle() - 0.5f, bloodRandom.NextSingle() - 0.5f)),
                Spin = spinAxis * (4 + bloodRandom.NextSingle() * 10),
            });
        }
        if (pieces.Count > MaxPieces) pieces.RemoveRange(0, pieces.Count - MaxPieces);
    }

    Vector2 RandomDirection()
    {
        float a = bloodRandom.NextSingle() * MathF.Tau;
        return new Vector2(MathF.Sin(a), MathF.Cos(a));
    }

    /// <summary>The meshes of a race's <c>severed limbs</c> items (their <c>ground mesh</c>, else <c>mesh</c>).</summary>
    string[] LimbMeshes(string? race)
    {
        if (race is null || scene.Database is not { } db) return [];
        if (limbMeshes.TryGetValue(race, out var known)) return known;
        var list = new List<string>();
        if (db.Find(race) is { } record)
            foreach (var limb in record.GetReferences("severed limbs"))
                if (db.Find(limb.TargetStringId) is { } item && (item.GetPath("ground mesh") is { Length: > 0 } ground ? ground : item.GetPath("mesh")) is { Length: > 0 } mesh)
                    list.Add(mesh);
        return limbMeshes[race] = [.. list];
    }

    /// <summary>Moves the pieces by <paramref name="dt"/> seconds of game time and hands the renderer this frame's list; those older than a corpse go.</summary>
    void UpdateDebris(World world, float dt)
    {
        pieces.RemoveAll(p => world.Tick - p.Born >= BodySystem.CorpseTicks);
        gpu.DebrisPieces.Clear();
        if (pieces.Count == 0) return;
        int steps = Math.Clamp((int)MathF.Ceiling(dt * 60), 1, 8);
        float h = dt / steps;
        foreach (var p in pieces)
        {
            for (int s = 0; s < steps && !p.Resting && h > 0; s++) Step(p, h);
            gpu.DebrisPieces.Add(new DebrisPiece(p.Mesh, p.Scale, p.Rotation, p.Position));
        }
    }

    void Step(Piece p, float h)
    {
        p.Velocity.Y -= Gravity * h;
        p.Position += p.Velocity * h;
        float spin = p.Spin.Length();
        if (spin > 0) p.Rotation = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(p.Spin / spin, spin * h) * p.Rotation);
        float floor = float.IsNaN(p.Floor) ? gpu.Terrain.HeightAt(p.Position.X, p.Position.Z) : p.Floor;
        float thin = MathF.Min(p.Extent.X, MathF.Min(p.Extent.Y, p.Extent.Z));
        if (p.Position.Y - thin > floor) return;
        p.Position.Y = floor + thin;
        if (!p.Landed && p.Colour != 0 && decalStore is { } store)
            store.Add(TerrainDecalType.Splash, p.Position, new Vector2(p.Velocity.X, p.Velocity.Z), 0.12f, p.Colour, TerrainDecalStore.NewLife, bloodRandom);
        p.Landed = true;
        var flat = new Vector2(p.Velocity.X, p.Velocity.Z);
        if (MathF.Abs(p.Velocity.Y) < 15 && flat.Length() < 12)
        {
            p.Resting = true;
            p.Velocity = p.Spin = Vector3.Zero;
            p.Rotation = LieFlat(p.Rotation, p.Extent);
            return;
        }
        p.Velocity = new Vector3(p.Velocity.X * Grip, MathF.Abs(p.Velocity.Y) * Bounce, p.Velocity.Z * Grip);
        p.Spin *= Grip;
    }

    /// <summary>The rotation that lays a piece on its thinnest side, keeping where its long axis pointed on the ground.</summary>
    static Quaternion LieFlat(Quaternion rotation, Vector3 extent)
    {
        var axis = extent.X <= extent.Y && extent.X <= extent.Z ? Vector3.UnitX : extent.Y <= extent.Z ? Vector3.UnitY : Vector3.UnitZ;
        var up = Vector3.Transform(axis, rotation);
        if (up.Y < 0) up = -up;
        var tilt = Vector3.Cross(up, Vector3.UnitY);
        float s = tilt.Length();
        if (s < 1e-5f) return rotation;
        return Quaternion.Normalize(Quaternion.CreateFromAxisAngle(tilt / s, MathF.Atan2(s, up.Y)) * rotation);
    }
}
