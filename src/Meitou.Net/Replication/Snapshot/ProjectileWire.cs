using System.Numerics;
using Meitou.Simulation;

namespace Meitou.Net.Replication;

/// <summary>
/// A <see cref="ProjectileSnapshot"/> in wire form (docs/net.md, "Snapshot core"), keyed by its id with the same baselines as characters: the position in
/// 1/64 units, the direction as three 16-bit components (canonical: decoding and re-quantizing gives the same components), the stretch as a raw float, the
/// stuck bit and the mesh name. Immutable.
/// </summary>
public sealed class ProjectileWire : IEquatable<ProjectileWire>
{
    /// <summary>Mask bits of <see cref="DiffMask"/>.</summary>
    public const uint PositionBit = 1, DirectionBit = 2, StretchBit = 4, MeshBit = 8, StuckBit = 16;

    /// <summary>The baseline of a new projectile.</summary>
    public static readonly ProjectileWire Default = new() { Stretch = 1f };

    public int X { get; private init; }
    public int Y { get; private init; }
    public int Z { get; private init; }
    public short DirX { get; private init; }
    public short DirY { get; private init; }
    public short DirZ { get; private init; }
    public float Stretch { get; private init; } = 1f;
    public bool Stuck { get; private init; }
    public string Mesh { get; private init; } = "";

    public static ProjectileWire Quantize(ProjectileSnapshot p)
    {
        short x = Quant.Dir16(p.Direction.X), y = Quant.Dir16(p.Direction.Y), z = Quant.Dir16(p.Direction.Z);
        for (int i = 0; i < 4; i++)   // decoding renormalises, which can move a component by a step; settle on a fixed point
        {
            var d = Quant.FromDirection(x, y, z);
            short nx = Quant.Dir16(d.X), ny = Quant.Dir16(d.Y), nz = Quant.Dir16(d.Z);
            if (nx == x && ny == y && nz == z) break;
            (x, y, z) = (nx, ny, nz);
        }
        return new ProjectileWire
        {
            X = Quant.Q64(p.Position.X), Y = Quant.Q64(p.Position.Y), Z = Quant.Q64(p.Position.Z),
            DirX = x, DirY = y, DirZ = z,
            Stretch = WireText.Finite(p.Stretch, 1f), Stuck = p.Stuck, Mesh = WireText.Limit(p.Mesh, NetLimits.MaxName),
        };
    }

    public ProjectileSnapshot ToSnapshot(int id) =>
        new(id, new Vector3(Quant.FromQ64(X), Quant.FromQ64(Y), Quant.FromQ64(Z)), Quant.FromDirection(DirX, DirY, DirZ), Stretch, Stuck, Mesh);

    /// <summary>The fields that differ from <paramref name="other"/> (bits <see cref="PositionBit"/> to <see cref="StuckBit"/>); 0 when equal.</summary>
    public uint DiffMask(ProjectileWire other)
    {
        uint m = 0;
        if (X != other.X || Y != other.Y || Z != other.Z) m |= PositionBit;
        if (DirX != other.DirX || DirY != other.DirY || DirZ != other.DirZ) m |= DirectionBit;
        if (Stretch != other.Stretch) m |= StretchBit;
        if (!string.Equals(Mesh, other.Mesh, StringComparison.Ordinal)) m |= MeshBit;
        if (Stuck != other.Stuck) m |= StuckBit;
        return m;
    }

    public bool Equals(ProjectileWire? other) => other is not null && DiffMask(other) == 0;

    public override bool Equals(object? obj) => obj is ProjectileWire other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(X, Y, Z, DirX, Stuck);

    /// <summary>
    /// The record body (the caller writes the id and the baseline reference): a flags byte (bits 0-3 the fields of <paramref name="mask"/> that follow, bit 4
    /// the stuck value, always current), then the position as zigzag steps (from the baseline, or absolute without one), the three direction components
    /// as 16 bits, the stretch as a float and the mesh as an interned string.
    /// </summary>
    public static void Write(NetWriter w, StringTableWriter strings, ProjectileWire now, ProjectileWire? baseline, uint mask)
    {
        mask &= PositionBit | DirectionBit | StretchBit | MeshBit;
        w.WriteByte((byte)(mask | (now.Stuck ? StuckBit : 0)));
        if ((mask & PositionBit) != 0)
        {
            w.WriteVarInt(now.X - (long)(baseline?.X ?? 0));
            w.WriteVarInt(now.Y - (long)(baseline?.Y ?? 0));
            w.WriteVarInt(now.Z - (long)(baseline?.Z ?? 0));
        }
        if ((mask & DirectionBit) != 0)
        {
            w.WriteUInt16((ushort)now.DirX);
            w.WriteUInt16((ushort)now.DirY);
            w.WriteUInt16((ushort)now.DirZ);
        }
        if ((mask & StretchBit) != 0) w.WriteSingle(now.Stretch);
        if ((mask & MeshBit) != 0) strings.Write(w, now.Mesh);
    }

    public static ProjectileWire Read(ref NetReader r, StringTableReader strings, ProjectileWire? baseline)
    {
        var b = baseline ?? Default;
        byte flags = r.ReadByte();
        if ((flags & ~31) != 0) throw new NetFormatException($"Unknown projectile flags {flags}.");
        int x = b.X, y = b.Y, z = b.Z;
        if ((flags & PositionBit) != 0)
        {
            x = WireText.AddPosition(x, r.ReadVarInt());
            y = WireText.AddPosition(y, r.ReadVarInt());
            z = WireText.AddPosition(z, r.ReadVarInt());
        }
        short dx = b.DirX, dy = b.DirY, dz = b.DirZ;
        if ((flags & DirectionBit) != 0)
        {
            dx = (short)r.ReadUInt16();
            dy = (short)r.ReadUInt16();
            dz = (short)r.ReadUInt16();
        }
        float stretch = (flags & StretchBit) != 0 ? r.ReadFinite() : b.Stretch;
        string mesh = (flags & MeshBit) != 0 ? WireText.ReadRequired(ref r, strings, NetLimits.MaxName) : b.Mesh;
        return new ProjectileWire { X = x, Y = y, Z = z, DirX = dx, DirY = dy, DirZ = dz, Stretch = stretch, Stuck = (flags & StuckBit) != 0, Mesh = mesh };
    }
}
