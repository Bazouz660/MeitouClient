using System.Numerics;
using Meitou.Data.Characters;

namespace Meitou.Net.Replication;

/// <summary>
/// A ragdoll's <see cref="BonePose"/> in wire form (docs/net.md, "Snapshot core"): only the bones the ragdoll sets, each as a smallest-three rotation and a
/// position, and the blend weight as a 16-bit fraction. Positions are 16-bit steps of 1/256 unit; a pose with any position at or beyond 127 units (a large
/// animal) is <see cref="Wide"/> and sends raw floats instead. Immutable; equal when every field is, so a frozen ragdoll costs nothing once acked.
/// </summary>
public sealed class RagdollWire : IEquatable<RagdollWire>
{
    const float PositionSteps = 256f;
    const int WideLimit = 127 * 256;

    readonly int[] bones;
    readonly uint[] rotations;
    readonly Vector3[] positions;
    BonePose? pose;

    RagdollWire(int boneCount, float weight, bool wide, int[] bones, uint[] rotations, Vector3[] positions)
    {
        BoneCount = boneCount;
        Weight = weight;
        Wide = wide;
        this.bones = bones;
        this.rotations = rotations;
        this.positions = positions;
    }

    /// <summary>The bones of the rig the pose was made for (at most <see cref="NetLimits.MaxBones"/>).</summary>
    public int BoneCount { get; }

    /// <summary>The decoded blend weight from the animated pose (0) to the ragdoll (1).</summary>
    public float Weight { get; }

    /// <summary>Positions travel as raw floats.</summary>
    public bool Wide { get; }

    /// <summary>The pose quantized, or null for no pose or one with more than <see cref="NetLimits.MaxBones"/> bones.</summary>
    public static RagdollWire? Quantize(BonePose? p)
    {
        if (p is null) return null;
        int count = Math.Min(p.Set.Length, Math.Min(p.Rotation.Length, p.Position.Length));
        if (count > NetLimits.MaxBones) return null;

        var list = new List<int>();
        for (int i = 0; i < count; i++)
            if (p.Set[i]) list.Add(i);
        var rot = new uint[list.Count];
        var pos = new Vector3[list.Count];
        bool wide = false;
        for (int i = 0; i < list.Count; i++)
        {
            rot[i] = Quant.SmallestThree(p.Rotation[list[i]]);
            var v = p.Position[list[i]];
            pos[i] = new Vector3(WireText.Finite(v.X), WireText.Finite(v.Y), WireText.Finite(v.Z));
            wide |= Step(pos[i].X) >= WideLimit || Step(pos[i].Y) >= WideLimit || Step(pos[i].Z) >= WideLimit;
        }
        if (!wide)
            for (int i = 0; i < pos.Length; i++)
                pos[i] = new Vector3(Signed(pos[i].X) / PositionSteps, Signed(pos[i].Y) / PositionSteps, Signed(pos[i].Z) / PositionSteps);
        float weight = float.IsFinite(p.Weight) ? Quant.FromU16(Quant.U16(p.Weight, 0f, 1f), 0f, 1f) : 0f;
        return new RagdollWire(count, weight, wide, [.. list], rot, pos);
    }

    /// <summary>
    /// <see cref="Quant.FromSmallestThree"/> with the dropped component kept strictly the largest. When it comes out level with a quantized one (two
    /// components near-equal) packing the result again could pick the other index and give other bits; the margin (about 1e-5, far below a step) makes
    /// <c>SmallestThree(Decode(p)) == p</c> hold for every packed value <see cref="Quant.SmallestThree"/> produces.
    /// </summary>
    static Quaternion Decode(uint packed)
    {
        var q = Quant.FromSmallestThree(packed);
        int largest = (int)(packed >> 30);
        Span<float> c = [q.X, q.Y, q.Z, q.W];
        float other = 0f;
        for (int i = 0; i < 4; i++)
            if (i != largest) other = MathF.Max(other, MathF.Abs(c[i]));
        c[largest] = MathF.Max(c[largest], other * 1.00001f);
        return new Quaternion(c[0], c[1], c[2], c[3]);
    }

    static double Step(float v) => Math.Abs(Math.Round((double)v * PositionSteps));

    static short Signed(float v) => (short)Math.Clamp(Math.Round((double)v * PositionSteps), -WideLimit, WideLimit);

    /// <summary>The pose the renderer blends in. Built once and shared: <see cref="BonePose"/> is immutable once published.</summary>
    public BonePose ToPose()
    {
        if (pose is { } cached) return cached;
        var p = new BonePose(BoneCount) { Weight = Weight };
        for (int i = 0; i < bones.Length; i++)
        {
            p.Set[bones[i]] = true;
            p.Rotation[bones[i]] = Decode(rotations[i]);
            p.Position[bones[i]] = positions[i];
        }
        return pose = p;
    }

    /// <summary>The bone count, the set bitset, the weight, a pose flags byte (b0 wide), then per set bone its rotation and position.</summary>
    public void Write(NetWriter w)
    {
        w.WriteVarUInt((uint)BoneCount);
        Span<byte> set = stackalloc byte[(NetLimits.MaxBones + 7) / 8];
        set = set[..((BoneCount + 7) / 8)];
        set.Clear();
        foreach (int bone in bones) set[bone >> 3] |= (byte)(1 << (bone & 7));
        w.WriteBytes(set);
        w.WriteUInt16(Quant.U16(Weight, 0f, 1f));
        w.WriteByte(Wide ? (byte)1 : (byte)0);
        for (int i = 0; i < bones.Length; i++)
        {
            w.WriteUInt32(rotations[i]);
            var v = positions[i];
            if (Wide) CommonCodec.WriteVector3(w, v);
            else
            {
                w.WriteUInt16((ushort)Signed(v.X));
                w.WriteUInt16((ushort)Signed(v.Y));
                w.WriteUInt16((ushort)Signed(v.Z));
            }
        }
    }

    public static RagdollWire Read(ref NetReader r)
    {
        int count = r.ReadCount(NetLimits.MaxBones);
        var set = r.ReadBytes((count + 7) / 8);
        var list = new List<int>();
        for (int i = 0; i < count; i++)
            if ((set[i >> 3] & (1 << (i & 7))) != 0) list.Add(i);
        if (count % 8 != 0 && (set[^1] >> (count % 8)) != 0) throw new NetFormatException("The ragdoll bone set has bits beyond the bone count.");
        float weight = Quant.FromU16(r.ReadUInt16(), 0f, 1f);
        byte flags = r.ReadByte();
        if ((flags & ~1) != 0) throw new NetFormatException($"Unknown ragdoll pose flags {flags}.");
        bool wide = (flags & 1) != 0;
        var rot = new uint[list.Count];
        var pos = new Vector3[list.Count];
        for (int i = 0; i < list.Count; i++)
        {
            rot[i] = r.ReadUInt32();
            pos[i] = wide
                ? CommonCodec.ReadVector3(ref r)
                : new Vector3((short)r.ReadUInt16() / PositionSteps, (short)r.ReadUInt16() / PositionSteps, (short)r.ReadUInt16() / PositionSteps);
        }
        return new RagdollWire(count, weight, wide, [.. list], rot, pos);
    }

    public bool Equals(RagdollWire? other) =>
        other is not null && BoneCount == other.BoneCount && Weight == other.Weight && Wide == other.Wide
        && bones.AsSpan().SequenceEqual(other.bones) && rotations.AsSpan().SequenceEqual(other.rotations) && positions.AsSpan().SequenceEqual(other.positions);

    public override bool Equals(object? obj) => obj is RagdollWire other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(BoneCount, Weight, Wide, bones.Length, rotations.Length > 0 ? rotations[0] : 0u);
}
