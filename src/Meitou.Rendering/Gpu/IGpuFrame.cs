namespace Meitou.Rendering.Gpu;

/// <summary>
/// The frame's neutral surface (docs/resource-layer.md): what a renderer may ask of the frame regardless of
/// the backend. The Vulkan frame implements it; the Metal one has its own (a slot ring over shared buffers).
/// The backend-only parts (the descriptor pools, the parallel secondaries, the host-visible allocators)
/// stay on the concrete frame.
/// </summary>
public interface IGpuFrame
{
    /// <summary>The frame number (FrameRing.FrameNumber); 0 before the first.</summary>
    long Number { get; }

    /// <summary>The slot the frame rotates through (the frames-in-flight index).</summary>
    int Slot { get; }

    /// <summary>Whether the frame is open for recording (between the context's BeginFrame and EndFrame).</summary>
    bool Open { get; }

    /// <summary>The frame's primary command list.</summary>
    IGpuCommandList Commands { get; }

    /// <summary>The frame's resource states (the neutral sync model's bookkeeping).</summary>
    ResourceStates States { get; }

    /// <summary>The frame's statistics (the passes', draws' and dispatches' counters).</summary>
    GpuStats Stats { get; }
}
