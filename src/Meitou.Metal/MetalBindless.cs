using Meitou.Rendering.Gpu;
using SharpMetal.Metal;

namespace Meitou.Metal;

/// <summary>
/// The engine's bindless table on Metal (docs/metal-backend.md, "The bindless table"): the bindless set is one Metal 3
/// tier-2 argument buffer, one shared buffer per frame slot (the CPU writes it, unified memory). An entry is a texture's
/// <c>gpuResourceID</c> at the members' sequential offsets; the float kinds' entries carry their sampler's beside them (a
/// sampler needs <see cref="SharpMetal.Metal.MTLSamplerDescriptor.SupportArgumentBuffers"/>), the integer kinds' samplers are
/// dropped in SPIR-V (the fetches never read them). The journal contract is the Vulkan table's
/// (<see cref="BindlessTable"/>): registrations and updates are replayed into a slot's buffer when its frame begins and
/// into the open frame's buffer before it is submitted, so an index registered while a frame is open is valid in it; a
/// freed index is handed out again once the frames that could read it have finished. Render thread only.
/// </summary>
internal sealed unsafe class MetalBindlessTable : IGpuBindlessTable
{
    readonly MetalContext ctx;
    readonly MTLBuffer[] buffers = new MTLBuffer[MetalContext.FramesInFlight];
    readonly byte*[] pointers = new byte*[MetalContext.FramesInFlight];
    readonly long[] applied = new long[MetalContext.FramesInFlight];
    readonly List<(BindlessKind Kind, uint Index, SampledTexture Texture)> journal = [];
    long journalBase;
    readonly Stack<uint>[] free = new Stack<uint>[BindlessTable.ArrayCount];
    readonly uint[] next = new uint[BindlessTable.ArrayCount];
    readonly List<(long Frame, BindlessKind Kind, uint Index)> deferred = [];
    /// <summary>The textures the argument buffer's entries hold (their ids alone do not make them resident: the encoder is told).</summary>
    readonly MTLTexture[] live = new MTLTexture[LiveSlots()];
    int currentSlot;
    bool open;

    /// <summary>The argument buffer's bytes: every declared member's id, an entry each (the MSL's <c>[[id]]</c> order).</summary>
    static readonly uint size = Size();

    static uint Size()
    {
        uint last = 0;
        for (int k = 0; k < BindlessTable.ArrayCount; k++)
        {
            last = Math.Max(last, MetalShaders.TableTextureId(k) + MetalShaders.TableCapacity[k]);
            if (MetalShaders.HasSamplers(k)) last = Math.Max(last, MetalShaders.TableSamplerId(k) + MetalShaders.TableCapacity[k]);
        }
        return last * 8;
    }

    static int LiveSlots()
    {
        int n = 0;
        for (int k = 0; k < BindlessTable.ArrayCount; k++) n = Math.Max(n, (int)MetalShaders.TableTextureId(k) + (int)MetalShaders.TableCapacity[k]);
        return n;
    }

    public MetalBindlessTable(MetalContext ctx)
    {
        this.ctx = ctx;
        for (int i = 0; i < free.Length; i++) free[i] = new Stack<uint>();
        for (int i = 0; i < buffers.Length; i++)
        {
            var shared = ctx.NewSharedBuffer(size, $"bindless table {i}");
            buffers[i] = shared.Buffer;
            pointers[i] = shared.Pointer;
            new Span<byte>(shared.Pointer, (int)size).Clear();
        }
    }

    public uint[] Capacity => MetalShaders.TableCapacity;

    public int Registered
    {
        get
        {
            int n = 0;
            for (int i = 0; i < next.Length; i++) n += (int)next[i] - free[i].Count;
            return n;
        }
    }

    /// <summary>The table is the argument buffer, not a set: the renderers bind it through <c>BindBindlessTable</c>, so the
    /// index is never resolved into a set.</summary>
    public int SetIndex => 0;

    public uint Register(BindlessKind kind, in SampledTexture texture)
    {
        int k = (int)kind;
        uint index = free[k].Count > 0 ? free[k].Pop() : next[k] < Capacity[k] ? next[k]++ : throw new InvalidOperationException($"bindless {kind} array is full ({Capacity[k]})");
        journal.Add((kind, index, texture));
        return index;
    }

    public BindlessHandle Register(GpuTexture texture, GpuSampler sampler, bool shadow = false)
    {
        var kind = BindlessTable.KindFor(texture.Desc.Format, texture.Desc.Kind, shadow);
        return new BindlessHandle(kind, Register(kind, new SampledTexture(sampler, texture.View())));
    }

    public void Update(BindlessKind kind, uint index, in SampledTexture texture) => journal.Add((kind, index, texture));

    public void Update(BindlessHandle handle, in SampledTexture texture) => Update(handle.Kind, handle.Index, texture);

    public void Free(BindlessKind kind, uint index)
    {
        journal.Add((kind, index, default));   // a tombstone (null view)
        deferred.Add((ctx.FrameNumber, kind, index));
    }

    public void Free(BindlessHandle handle) => Free(handle.Kind, handle.Index);

    public void BeginFrame(int slot)
    {
        currentSlot = slot;
        // The frames that could have read an index freed when frame f was open are done once the ring wraps past it.
        for (int i = 0; i < deferred.Count;)
        {
            if (deferred[i].Frame + MetalContext.FramesInFlight > ctx.FrameNumber) { i++; continue; }
            free[(int)deferred[i].Kind].Push(deferred[i].Index);
            deferred.RemoveAt(i);
        }
        Apply(slot, endOfFrame: false);
        open = true;
    }

    public void EndFrame()
    {
        if (!open) return;
        open = false;
        Apply(currentSlot, endOfFrame: true);
    }

    /// <summary>The slot's argument buffer (the bytes the shader reads).</summary>
    public MTLBuffer BufferAt(int slot) => buffers[slot];

    /// <summary>Tells the encoder which textures the argument buffer's entries hold: a resource the encoder does not know about
    /// is not made resident and reads as nothing (the manual argument buffer's price).</summary>
    public void UseResources(MetalCommandList list)
    {
        foreach (var texture in live)
            if (texture.NativePtr != IntPtr.Zero) list.UseResource(texture);
    }

    readonly HashSet<(BindlessKind, uint)> written = [];

    /// <summary>
    /// The journal's new entries the slot has not seen, newest first, each (array, index) once — the Vulkan table's rule
    /// (an entry overwritten later is skipped; at a frame's begin a freed one is skipped too, at its end it is kept: the
    /// frame's draws may have read it, and its view lives until the frame has finished).
    /// </summary>
    void Apply(int slot, bool endOfFrame)
    {
        long end = journalBase + journal.Count;
        long from = Math.Max(applied[slot], journalBase);
        if (from < end)
        {
            byte* p = pointers[slot];
            written.Clear();
            for (long j = end - 1; j >= from; j--)
            {
                var (kind, index, t) = journal[(int)(j - journalBase)];
                if (t.IsNull)
                {
                    if (!endOfFrame) { written.Add((kind, index)); live[MetalShaders.TableTextureId((int)kind) + (int)index] = default; }
                    continue;
                }
                if (!written.Add((kind, index))) continue;
                var img = ctx.ViewTexture(t.View);
                live[MetalShaders.TableTextureId((int)kind) + index] = img;
                *(ulong*)(p + (MetalShaders.TableTextureId((int)kind) + index) * 8) = img.GpuResourceID._impl;
                if (MetalShaders.HasSamplers((int)kind))
                    *(ulong*)(p + (MetalShaders.TableSamplerId((int)kind) + index) * 8) = ctx.Samplers.Backend(t.Sampler).GpuResourceID._impl;
            }
        }
        applied[slot] = end;
        // Drop what every slot has seen.
        long seen = applied[0];
        for (int i = 1; i < applied.Length; i++) seen = Math.Min(seen, applied[i]);
        int drop = (int)(seen - journalBase);
        if (drop > 0) { journal.RemoveRange(0, drop); journalBase = seen; }
    }

    public void Dispose()
    {
        foreach (var b in buffers) b.Dispose();
    }
}
