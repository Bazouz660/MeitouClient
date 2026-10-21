using System.Numerics;

namespace Meitou.Net.Replication;

/// <summary>
/// What a client tells the host on the <see cref="NetChannel.Snapshots"/> channel (docs/net.md, "Interest, delta, replicator and mirror"): the snapshot
/// parts it received, redundantly for its newest <see cref="MaxAcks"/> Seqs (a 64-bit mask of part indices each), where its camera is, and the character
/// slots (and, as <c>-1 - id</c>, projectile ids) it could not decode and wants sent in full. <see cref="FrameSeq"/> grows by one per frame within an epoch.
/// </summary>
public sealed record ClientFrame(ushort Epoch, uint FrameSeq, IReadOnlyList<(uint Seq, ulong Parts)> Acks, ViewState View, IReadOnlyList<int> Resync)
{
    public const int MaxAcks = 8, MaxResync = 32;
}

/// <summary>
/// The <see cref="ClientFrame"/> message: <c>0x11 | Epoch u16 | FrameSeq | ack count, (Seq, part mask) varints | Eye, Focus as Q64 zigzag x3 |
/// DrawDistance u16 | resync count, zigzag ints</c>. The writer cuts the lists to their limits and clamps the draw distance, so the reader never refuses
/// what it produced.
/// </summary>
public static class ClientFrameCodec
{
    public static void Write(NetWriter w, ClientFrame frame)
    {
        w.WriteByte((byte)MessageType.ClientFrame);
        w.WriteUInt16(frame.Epoch);
        w.WriteVarUInt(frame.FrameSeq);
        int acks = Math.Min(frame.Acks.Count, ClientFrame.MaxAcks);
        w.WriteVarUInt((uint)acks);
        for (int i = 0; i < acks; i++)
        {
            w.WriteVarUInt(frame.Acks[i].Seq);
            w.WriteVarUInt(frame.Acks[i].Parts);
        }
        WritePoint(w, frame.View.Eye);
        WritePoint(w, frame.View.Focus);
        float draw = frame.View.DrawDistance;
        w.WriteUInt16(float.IsNaN(draw) ? (ushort)0 : (ushort)Math.Clamp(MathF.Round(draw), 0f, ushort.MaxValue));
        int resync = Math.Min(frame.Resync.Count, ClientFrame.MaxResync);
        w.WriteVarUInt((uint)resync);
        for (int i = 0; i < resync; i++) w.WriteVarInt(frame.Resync[i]);
    }

    /// <summary>Reads a whole message, from its type byte to the end of the packet.</summary>
    public static ClientFrame Read(ref NetReader r)
    {
        if (r.ReadByte() != (byte)MessageType.ClientFrame) throw new NetFormatException("Not a ClientFrame.");
        ushort epoch = r.ReadUInt16();
        uint frameSeq = r.ReadVarUInt32();
        int ackCount = r.ReadCount(ClientFrame.MaxAcks);
        var acks = new (uint Seq, ulong Parts)[ackCount];
        for (int i = 0; i < ackCount; i++) acks[i] = (r.ReadVarUInt32(), r.ReadVarUInt());
        var eye = ReadPoint(ref r);
        var focus = ReadPoint(ref r);
        float draw = r.ReadUInt16();
        int resyncCount = r.ReadCount(ClientFrame.MaxResync);
        var resync = new int[resyncCount];
        for (int i = 0; i < resyncCount; i++) resync[i] = r.ReadVarInt32();
        r.ExpectEnd();
        return new ClientFrame(epoch, frameSeq, acks, new ViewState(eye, focus, draw), resync);
    }

    static void WritePoint(NetWriter w, Vector3 v)
    {
        w.WriteVarInt(Quant.Q64(v.X));
        w.WriteVarInt(Quant.Q64(v.Y));
        w.WriteVarInt(Quant.Q64(v.Z));
    }

    static Vector3 ReadPoint(ref NetReader r) =>
        new(Quant.FromQ64(WireText.AddPosition(0, r.ReadVarInt())), Quant.FromQ64(WireText.AddPosition(0, r.ReadVarInt())), Quant.FromQ64(WireText.AddPosition(0, r.ReadVarInt())));
}
