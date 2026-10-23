using System.Runtime.InteropServices;
using Meitou.Rendering.Gpu;

namespace Meitou.Rendering.Characters;

/// <summary>
/// The parts' material records, one growing storage buffer (binding 7 of the characters' frame) that keeps each <see cref="PartMaterial"/>'s record in a slot of
/// its own, uploaded only when the record changed. A changed record goes to a new slot (a frame in flight may still read the old one); a slot is used again
/// once the frames that could read it are done: after its material changed, or was not drawn for <see cref="IdleUpdates"/> updates. Render thread, in the
/// characters' update, before anything binds it this frame.
/// </summary>
internal sealed class CharacterMaterialTable : IDisposable
{
    readonly GpuContext gpu;
    DeviceBuffer buffer;
    CharacterMaterialRecord[] host = new CharacterMaterialRecord[1024];
    PartMaterial?[] owners = new PartMaterial?[1024];
    int used;
    readonly Stack<int> free = [];
    readonly Queue<(int Slot, long Frame)> retired = [];
    readonly List<int> dirty = [];
    readonly List<ulong> dirtyOffsets = [];
    CharacterMaterialRecord[] dirtyRecords = new CharacterMaterialRecord[64];

    /// <summary>Updates without a draw after which a material gives its slot back (it takes a new one when it is drawn again).</summary>
    public const int IdleUpdates = 600;

    public CharacterMaterialTable(GpuContext gpu)
    {
        this.gpu = gpu;
        buffer = DeviceBuffer.Create(gpu, (ulong)host.Length * CharacterMaterialRecord.Size, BufferUse.Storage, "character materials");
    }

    public int Slots => used - free.Count - retired.Count;
    public long Bytes => (long)host.Length * CharacterMaterialRecord.Size;

    /// <summary>Gives back the slots no frame in flight can read any more; first, each frame.</summary>
    public void BeginFrame()
    {
        long frame = gpu.Frame.Number;
        int inFlight = gpu.Device.FramesInFlight;
        while (retired.Count > 0 && frame - retired.Peek().Frame > inFlight) free.Push(retired.Dequeue().Slot);
    }

    /// <summary><paramref name="m"/>'s <see cref="PartMaterial.Record"/> into a new slot (its old one, if any, retired), uploaded at <see cref="Flush"/>.</summary>
    public void Place(PartMaterial m)
    {
        if (m.Slot >= 0) Retire(m.Slot);
        int slot = free.Count > 0 ? free.Pop() : Reserve();
        host[slot] = m.Record;
        owners[slot] = m;
        m.Slot = slot;
        dirty.Add(slot);
    }

    void Retire(int slot)
    {
        owners[slot] = null;
        retired.Enqueue((slot, gpu.Frame.Number));
    }

    /// <summary>Retires the slots of materials last drawn before <paramref name="before"/> (<see cref="PartMaterial.SlotUsed"/>).</summary>
    public void Sweep(long before)
    {
        for (int s = 0; s < used; s++)
            if (owners[s] is { } m && m.SlotUsed < before)
            {
                m.Slot = -1;
                Retire(s);
            }
    }

    int Reserve()
    {
        int slot = used++;
        if (used > host.Length)
        {
            Array.Resize(ref host, host.Length * 2);
            Array.Resize(ref owners, owners.Length * 2);
            buffer.Dispose();
            buffer = DeviceBuffer.Create(gpu, (ulong)host.Length * CharacterMaterialRecord.Size, BufferUse.Storage, "character materials");
            gpu.EnsureFrame();
            gpu.Uploads.Write(buffer, 0, MemoryMarshal.AsBytes(host.AsSpan(0, slot)));
        }
        return slot;
    }

    /// <summary>Uploads the records placed since the last call, in one copy.</summary>
    public void Flush()
    {
        if (dirty.Count == 0) return;
        if (dirtyRecords.Length < dirty.Count) dirtyRecords = new CharacterMaterialRecord[Math.Max(dirty.Count, dirtyRecords.Length * 2)];
        dirtyOffsets.Clear();
        for (int i = 0; i < dirty.Count; i++)
        {
            dirtyRecords[i] = host[dirty[i]];
            dirtyOffsets.Add((ulong)dirty[i] * CharacterMaterialRecord.Size);
        }
        gpu.EnsureFrame();
        gpu.Uploads.WriteScattered(buffer, MemoryMarshal.AsBytes(dirtyRecords.AsSpan(0, dirty.Count)), CharacterMaterialRecord.Size, CollectionsMarshal.AsSpan(dirtyOffsets));
        dirty.Clear();
    }

    public BufferBinding Binding(GpuFrame frame) => buffer.Binding(frame);

    public void Dispose() => buffer.Dispose();
}
