using Meitou.Rendering.Gpu;
using SharpMetal.Foundation;
using SharpMetal.Metal;
using SharpMetal.ObjectiveCCore;
using SharpMetal.QuartzCore;

namespace Meitou.Metal;

/// <summary>
/// The frame being recorded, on Metal (docs/resource-layer.md): a slot ring over the display's drawables —
/// one drawable, one command buffer and one constants buffer per slot. The constants ring is a shared buffer
/// the CPU writes (unified memory); the frame's commands are a <see cref="MetalCommandList"/>. Begun and ended
/// by the context's frame loop (<see cref="MetalContext.BeginFrame"/>, <see cref="MetalContext.EndFrame"/>).
/// </summary>
public sealed unsafe class MetalFrame : IGpuFrame
{
    readonly MetalContext ctx;
    readonly GpuBuffer constantsHandle;
    MTLBuffer constants;
    byte* constantsPointer;
    ulong constantsSize, constantsUsed;

    internal MetalFrame(MetalContext ctx, int slot)
    {
        this.ctx = ctx;
        Slot = slot;
        // The frame's own constants ring: grown when a frame's writes fill it (at least a default block's few slices a frame).
        var shared = ctx.NewSharedBuffer(1 << 20, $"frame constants {slot}");
        constants = shared.Buffer;
        constantsPointer = shared.Pointer;
        constantsSize = shared.Size;
        constantsHandle = ctx.RegisterBuffer(constants, $"frame constants {slot}");
        Commands = new MetalCommandList(ctx, $"frame {slot}", default);
        States = new ResourceStates();
        Stats = new GpuStats();
    }

    public long Number { get; internal set; }
    public int Slot { get; }
    public bool Open { get; internal set; }
    public MetalCommandList Commands { get; }
    IGpuCommandList IGpuFrame.Commands => Commands;
    /// <summary>Metal records uploads and bakes into the frame's own list, before its passes (the seam's pre-frame).</summary>
    IGpuCommandList IGpuFrame.PreFrame => Commands;
    public ResourceStates States { get; }
    public GpuStats Stats { get; }
    public IGpuTimestamps Timestamps => ctx.Timestamps;

    /// <summary>The command buffer this frame's commands are recorded into (the present pass continues it at <see cref="MetalContext.EndFrame"/>).</summary>
    internal MTLCommandBuffer Command { get; private set; }

    /// <summary>The drawable this frame draws into (the context's <see cref="MetalDisplay.TryBeginFrame"/> gave it).</summary>
    public CAMetalDrawable Drawable { get; internal set; }

    /// <summary>Writes bytes into the frame's constant ring (the default blocks' slices), aligned; the slice lives for the frame.</summary>
    public unsafe Transient WriteConstants(ReadOnlySpan<byte> data, ulong alignment = 16)
    {
        ulong offset = (constantsUsed + alignment - 1) / alignment * alignment;
        if (constantsSize - offset < (ulong)data.Length)
        {
            Grow((ulong)data.Length);
            offset = 0;
        }
        var pointer = constantsPointer + (nint)offset;
        data.CopyTo(new Span<byte>(pointer, data.Length));
        constantsUsed = offset + (ulong)data.Length;
        // Shared mode needs no flush (Managed's DidModifyRange); the GPU reads the memory as it is.
        return new Transient(constantsHandle, offset, pointer, (ulong)data.Length);
    }

    void Grow(ulong needed)
    {
        var shared = ctx.NewSharedBuffer(Math.Max(needed, constantsSize * 2), "frame constants");
        constants.Dispose();
        constants = shared.Buffer;
        constantsPointer = shared.Pointer;
        constantsSize = shared.Size;
        constantsUsed = 0;
        ctx.RebindBuffer(constantsHandle, constants);
    }

    internal void Begin(long number, MTLCommandBuffer command, CAMetalDrawable drawable)
    {
        Number = number;
        Command = command;
        Commands.Rebind(command);
        Drawable = drawable;
        constantsUsed = 0;
        Commands.Invalidate();
        States.AssumeFullBarrier();
        Stats.Reset();
        Open = true;
    }

    internal void End() => Open = false;
}
