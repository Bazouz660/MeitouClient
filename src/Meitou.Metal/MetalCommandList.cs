using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Meitou.Rendering.Gpu;
using SharpMetal.Foundation;
using SharpMetal.Metal;
using SharpMetal.ObjectiveCCore;
using SharpMetal.QuartzCore;

namespace Meitou.Metal;

/// <summary>
/// A command list being recorded, on Metal (docs/renderer-native.md 2.7): the engine's words onto a Metal
/// command buffer's encoders. Sized to the sky pass: renderings, the draw state, a program's resources
/// (Metal's stages have their own binding tables, so a set's slots bind straight onto the encoder — no
/// argument buffer), labels, and the mip chain's blit. Everything else throws (docs/metal-backend.md).
/// </summary>
public sealed class MetalCommandList : IGpuCommandList
{
    readonly MetalContext ctx;
    readonly string name;
    MTLCommandBuffer command;
    MTLRenderCommandEncoder render;
    MTLBlitCommandEncoder blit;
    bool ownsBlit;
    bool depthAttachment;
    GpuPipeline lastPipeline;
    PrimitiveType topology;
    long epoch;

    /// <summary>Made by the context (the frame's) or an upload batch (its own command buffer, its blit shared).</summary>
    internal MetalCommandList(MetalContext ctx, string name, MTLCommandBuffer command, MTLBlitCommandEncoder? sharedBlit = null)
    {
        this.ctx = ctx;
        this.name = name;
        this.command = command;
        if (sharedBlit.HasValue) { blit = sharedBlit.Value; ownsBlit = false; }
    }

    public GpuStats Stats => ctx.Frame.Stats;
    public long Epoch => epoch;
    public object? BoundProgram { get; set; }

    /// <summary>Forgets everything the redundancy filters and callers believed was bound.</summary>
    public void Invalidate()
    {
        lastPipeline = default;
        BoundProgram = null;
        epoch++;
    }

    // ---- rendering ----

    public void BeginRendering(in RenderingDesc d, bool secondaries = false)
    {
        if (secondaries) throw new NotSupportedException("the Metal backend records no secondary command lists yet");
        EndBlit();
        var descriptor = new MTLRenderPassDescriptor(
            ObjectiveC.IntPtr_objc_msgSend(ObjectiveC.objc_getClass("MTLRenderPassDescriptor"), "renderPassDescriptor"));
        if (!d.Colour.IsNull)
        {
            // The attachments come back as struct copies, so a value-type property (the clear colour) has to be written back
            // into the array, or the pass clears to whatever the descriptor's memory held (docs/metal-backend.md).
            var attachments = descriptor.ColorAttachments;
            var attachment = attachments.Object(0);
            attachment.Texture = ctx.ViewTexture(d.Colour.View);
            attachment.LoadAction = d.Colour.Load switch { LoadOp.Clear => MTLLoadAction.Clear, LoadOp.DontCare => MTLLoadAction.DontCare, _ => MTLLoadAction.Load };
            attachment.StoreAction = MTLStoreAction.Store;
            attachment.ClearColor = new MTLClearColor { red = d.Colour.Clear.R, green = d.Colour.Clear.G, blue = d.Colour.Clear.B, alpha = d.Colour.Clear.A };
            attachments.SetObject(attachment, 0);
        }
        if (!d.Depth.IsNull)
        {
            var depth = descriptor.DepthAttachment;
            depth.Texture = ctx.ViewTexture(d.Depth.View);
            depth.LoadAction = d.Depth.Load switch { LoadOp.Clear => MTLLoadAction.Clear, LoadOp.DontCare => MTLLoadAction.DontCare, _ => MTLLoadAction.Load };
            depth.StoreAction = MTLStoreAction.Store;
            depth.ClearDepth = d.Depth.DepthClear;
        }
        render = command.RenderCommandEncoder(descriptor);
        depthAttachment = !d.Depth.IsNull;
        if (name.Length > 0) render.PushDebugGroup((NSString)name);
    }

    public void EndRendering()
    {
        if (render.NativePtr == IntPtr.Zero) throw new InvalidOperationException("EndRendering without BeginRendering");
        render.PopDebugGroup();
        render.EndEncoding();
        render = default;
    }

    /// <summary>Clears a depth area (a shadow tile, the depth between slices) — comes with the shadow passes.</summary>
    public void ClearDepth(float value, PixelRect rect) =>
        throw new NotSupportedException("the Metal backend has no partial depth clear yet (the sky pass needs none)");

    /// <summary>Clears the colour and / or the depth attachment over an area — comes with the shadow passes.</summary>
    public void Clear(bool colour, ClearColour colourValue, bool depth, float depthValue, PixelRect rect) =>
        throw new NotSupportedException("the Metal backend has no in-pass clear yet (the sky pass needs none)");

    // ---- state ----

    public void BindPipeline(GpuPipeline pipeline)
    {
        if (pipeline.Desc == lastPipeline.Desc) return;
        if (render.NativePtr == IntPtr.Zero) throw new InvalidOperationException("BindPipeline outside a rendering");
        render.SetRenderPipelineState(ctx.Pipelines.Backend(pipeline));
        ctx.Pipelines.BindDepthClipMode(render, pipeline.Desc.DepthClamp);
        topology = pipeline.Desc.Topology;
        lastPipeline = pipeline;
        Stats.PipelinesBound++;
    }

    /// <summary>Binds a compute pipeline — comes with the compute passes.</summary>
    public void BindPipeline(GpuComputePipeline pipeline) =>
        throw new NotSupportedException("the Metal backend has no compute pipelines yet (the sky pass needs none)");

    public void SetViewport(in PixelViewport v) =>
        render.SetViewport(new MTLViewport { originX = v.X, originY = v.Y, width = v.Width, height = v.Height, znear = v.MinDepth, zfar = v.MaxDepth });

    public void SetScissor(in PixelRect r) =>
        render.SetScissorRect(new MTLScissorRect { x = (ulong)r.X, y = (ulong)r.Y, width = r.Width, height = r.Height });

    public void SetRaster(CullMode cull, FrontFaceDirection front)
    {
        render.SetCullMode(MetalConventions.Cull(cull));
        render.SetFrontFacingWinding(MetalConventions.Winding(front));
    }

    /// <summary>The front face alone (a draw that turns the winding round, the cull mode left as set).</summary>
    public void SetFrontFace(FrontFaceDirection front) => render.SetFrontFacingWinding(MetalConventions.Winding(front));

    public void SetDepth(bool test, bool write, DepthFunction op)
    {
        // With no depth attachment the state would be read nowhere; Metal's validation objects to setting it.
        if (!depthAttachment) return;
        render.SetDepthStencilState(ctx.DepthState(test, write, op));
    }

    public void SetDepthBias(bool enable, float constant, float slope) => render.SetDepthBias(enable ? constant : 0, enable ? slope : 0, 0);

    // ---- resources ----

    /// <summary>Binds the vertex buffers of the locations from <paramref name="first"/>: the pipeline's slots, Vulkan's own numbers
    /// (the MSL's indices below <see cref="MetalShaders.VertexBlockBase"/>, where the blocks go).</summary>
    public void BindVertexBuffers(uint first, ReadOnlySpan<GpuBufferBinding> bindings)
    {
        if (render.NativePtr == IntPtr.Zero) throw new InvalidOperationException("BindVertexBuffers outside a rendering");
        if (first + (uint)bindings.Length > MetalShaders.VertexBlockBase)
            throw new NotSupportedException($"the Metal backend keeps the vertex buffers in the slots below {MetalShaders.VertexBlockBase} (docs/metal-backend.md), {first}..{first + bindings.Length - 1} asked");
        for (int i = 0; i < bindings.Length; i++)
        {
            var (buffer, offset) = ctx.Buffer(bindings[i]);
            render.SetVertexBuffer(buffer, offset, first + (uint)i);
        }
    }

    /// <summary>Binds the index buffer, held until the next indexed draw.</summary>
    public void BindIndexBuffer(GpuBufferBinding binding, IndexKind type)
    {
        (indexBuffer, indexOffset) = ctx.Buffer(binding);
        indexType = MetalConventions.IndexType(type);
    }
    MTLBuffer indexBuffer;
    ulong indexOffset;
    MTLIndexType indexType;

    public void BindProgramResources(GpuProgram program, uint set, ReadOnlySpan<GpuBinding> bindings)
    {
        if (set != 0) throw new NotSupportedException($"the Metal backend binds set {set} only through CreateSet and BindSets");
        if (render.NativePtr == IntPtr.Zero) throw new InvalidOperationException("BindProgramResources outside a rendering");
        var slots = program.NeutralSets[set].Slots;
        foreach (var binding in bindings)
        {
            foreach (var slot in slots)
            {
                if (slot.Binding != binding.Slot) continue;
                if (slot.Stages.HasFlag(GpuStage.Vertex)) BindSlot(isVertex: true, set, slot, binding);
                if (slot.Stages.HasFlag(GpuStage.Fragment)) BindSlot(isVertex: false, set, slot, binding);
            }
        }
        Stats.DescriptorPushes++;
    }

    void BindSlot(bool isVertex, uint set, in GpuSlot slot, in GpuBinding binding)
    {
        // Metal's one binding table per stage: the slot's flattened index (MetalShaders.Flatten, the MSL's own numbers).
        uint index = MetalShaders.Flatten(slot.Kind, slot.Stages, set, slot.Binding);
        switch (binding.Kind)
        {
            case GpuBindingKind.Sampled:
                var (texture, sampler) = ctx.Sampled(binding.Texture);
                float bias = MetalSamplers.LodBias(binding.Texture.Sampler);
                if (isVertex)
                {
                    render.SetVertexTexture(texture, index);
                    render.SetVertexSamplerState(sampler, bias, 1000 + bias, index);
                }
                else
                {
                    render.SetFragmentTexture(texture, index);
                    render.SetFragmentSamplerState(sampler, bias, 1000 + bias, index);
                }
                break;
            case GpuBindingKind.UniformBuffer or GpuBindingKind.StorageBuffer:
                var (buffer, offset) = ctx.Buffer(binding.Buffer);
                if (isVertex) render.SetVertexBuffer(buffer, offset, index);
                else render.SetFragmentBuffer(buffer, offset, index);
                break;
            default:
                throw new NotSupportedException($"the Metal backend binds no storage images yet ({binding.Kind} at set {set}, binding {binding.Slot})");
        }
    }

    public void BindSets(GpuProgram program, uint first, ReadOnlySpan<GpuSet> sets, ReadOnlySpan<uint> dynamicOffsets, GpuBindPoint point = GpuBindPoint.Graphics)
    {
        if (point != GpuBindPoint.Graphics) throw new NotSupportedException("the Metal backend binds no compute sets yet");
        if (render.NativePtr == IntPtr.Zero) throw new InvalidOperationException("BindSets outside a rendering");
        int used = 0;
        for (int i = 0; i < sets.Length; i++)
        {
            var set = ctx.SetAt(sets[i]);
            uint index = first + (uint)i;
            int next = 0;
            foreach (var slot in set.Layout.Slots)
            {
                var binding = set.Bindings[next++].Buffer;
                // A slot whose offset comes at bind time takes the bind's dynamic offset (the default blocks' slices).
                if (slot.OffsetAtBind) binding = new GpuBufferBinding(binding.Buffer, dynamicOffsets.IsEmpty ? 0 : dynamicOffsets[used++], binding.Size);
                var (buffer, offset) = ctx.Buffer(binding);
                // Metal's one binding table per stage: the slot's flattened index (MetalShaders.Flatten, the MSL's own numbers).
                if (slot.Stages.HasFlag(GpuStage.Vertex))
                    render.SetVertexBuffer(buffer, offset, MetalShaders.Flatten(slot.Kind, GpuStage.Vertex, index, slot.Binding));
                if (slot.Stages.HasFlag(GpuStage.Fragment))
                    render.SetFragmentBuffer(buffer, offset, MetalShaders.Flatten(slot.Kind, GpuStage.Fragment, index, slot.Binding));
            }
        }
        Stats.DescriptorPushes++;
    }

    /// <summary>Binds the bindless table to a program — comes with the terrain (the table as a Metal 3 argument buffer, docs/metal-backend.md).</summary>
    public void BindBindlessTable(GpuProgram program, uint set, GpuBindPoint point = GpuBindPoint.Graphics) =>
        throw new NotSupportedException("the Metal backend has no bindless table yet (the sky pass needs none)");

    /// <summary>Pushes a block of constants: the bytes both stages' encoders take inline at the stage's top index
    /// (<see cref="MslTranslator.PushConstantBinding"/>, the push blocks' own <c>[[buffer(n)]]</c>).</summary>
    public unsafe void PushConstants<T>(GpuProgram program, GpuStage stages, in T value, uint offset = 0) where T : unmanaged
    {
        if (offset != 0) throw new NotSupportedException("the Metal backend pushes constants whole (offset 0)");
        var bytes = (nint)Unsafe.AsPointer(ref Unsafe.AsRef(in value));
        ulong size = (ulong)sizeof(T);
        if (stages.HasFlag(GpuStage.Vertex)) render.SetVertexBytes(bytes, size, MslTranslator.PushConstantBinding);
        if (stages.HasFlag(GpuStage.Fragment)) render.SetFragmentBytes(bytes, size, MslTranslator.PushConstantBinding);
    }

    /// <summary>Writes a timestamp into a query slot (the frame's timings). Metal records none yet (<see cref="MetalTimestamps"/>).</summary>
    public void Timestamp(IGpuTimestamps arena, in QuerySlot slot, GpuStage stage = GpuStage.All) { }

    // ---- draws ----

    public void Draw(uint vertices, uint instances = 1, uint firstVertex = 0, uint firstInstance = 0)
    {
        if (firstInstance != 0 || firstVertex != 0)
            throw new NotSupportedException("the Metal backend draws from the first vertex and instance only (the sky pass draws 3)");
        render.DrawPrimitives(MetalConventions.Primitive(topology), 0, vertices, instances);
        Stats.Draws++;
    }

    /// <summary>Draws indexed vertices out of the bound index buffer.</summary>
    public void DrawIndexed(uint indices, uint instances = 1, uint firstIndex = 0, int vertexOffset = 0, uint firstInstance = 0)
    {
        if (indexBuffer.NativePtr == IntPtr.Zero) throw new InvalidOperationException("DrawIndexed with no index buffer bound");
        render.DrawIndexedPrimitives(MetalConventions.Primitive(topology), indices, indexType, indexBuffer,
            indexOffset + (ulong)firstIndex * MetalConventions.IndexBytes(indexType), instances, vertexOffset, firstInstance);
        Stats.Draws++;
    }

    /// <summary>Draws indexed vertices, the arguments in a buffer — comes with the terrain and the objects.</summary>
    public void DrawIndexedIndirect(GpuBuffer args, ulong offset, uint count, uint stride = 20) =>
        throw new NotSupportedException("the Metal backend has no indirect draws yet");

    /// <summary>Draws indexed vertices, the arguments in a buffer and the count in another — comes with the foliage.</summary>
    public void DrawIndexedIndirectCount(GpuBuffer args, ulong offset, GpuBuffer count, ulong countOffset, uint maxCount, uint stride = 20) =>
        throw new NotSupportedException("the Metal backend has no indirect draws yet");

    /// <summary>Draws indirect, the count in a buffer — comes with the foliage.</summary>
    public void DrawIndirectCount(GpuBuffer args, ulong offset, GpuBuffer countBuffer, ulong countOffset, uint maxCount, uint stride = 16) =>
        throw new NotSupportedException("the Metal backend has no indirect draws yet");

    /// <summary>Dispatches a compute pass — comes with the compute passes.</summary>
    public void Dispatch(uint x, uint y = 1, uint z = 1) =>
        throw new NotSupportedException("the Metal backend has no compute dispatches yet (the sky pass needs none)");

    /// <summary>A new texture: the backend's own image, and its transition recorded into this command list — comes with the render targets' passes.</summary>
    public GpuTexture CreateTexture(TextureDesc desc) =>
        throw new NotSupportedException("the Metal backend makes no textures inside a command list yet");

    // ---- transfers ----

    /// <summary>Fills a range of a buffer with a value — comes with its passes.</summary>
    public void FillBuffer(GpuBuffer b, ulong offset, ulong size, uint value) =>
        throw new NotSupportedException("the Metal backend has no buffer fills yet");

    /// <summary>Copies a range of a buffer into another — comes with its passes.</summary>
    public void CopyBuffer(GpuBuffer src, GpuBuffer dst, in GpuBufferCopy region) =>
        throw new NotSupportedException("the Metal backend has no buffer copies yet");

    /// <summary>Copies a range of a buffer into a texture's level and layer — the uploads' own commands do it.</summary>
    public void CopyBufferToImage(GpuBuffer src, ulong offset, GpuTexture dst, int level, int layer) =>
        throw new NotSupportedException("the Metal backend copies into textures through its upload batches only");

    /// <summary>Resolves a multisample texture into a single-sample one — comes with the reflection's passes.</summary>
    public void Resolve(GpuTexture src, GpuTexture dst) =>
        throw new NotSupportedException("the Metal backend has no resolves yet");

    /// <summary>Copies a texture into another — comes with its passes.</summary>
    public void Blit(GpuTexture src, GpuTexture dst, TextureMinFilter filter) =>
        throw new NotSupportedException("the Metal backend has no texture blits yet");

    /// <summary>Blits one level of a texture into another — comes with its passes.</summary>
    public void BlitLevel(GpuTexture texture, int srcLevel, int dstLevel, TextureMinFilter filter) =>
        throw new NotSupportedException("the Metal backend has no texture blits yet");

    /// <summary>Generates a texture's mip chain (as GL's GenerateMipmap made it: a blit's mip generation, filter linear).</summary>
    public void GenerateMips(GpuTexture t)
    {
        var b = OpenBlit();
        b.GenerateMipmaps(ctx.TextureAt(t));
    }

    // ---- sync ----

    /// <summary>Records the dependencies the engine's words describe: nothing — Apple silicon tracks the hazards itself
    /// (docs/metal-backend.md "Barriers between passes").</summary>
    public void Barrier(ReadOnlySpan<GpuDependency> dependencies) { }

    /// <summary>Orders everything recorded before against everything after it: nothing, as <see cref="Barrier"/>.</summary>
    public void Order() { }

    // ---- labels ----

    public void BeginLabel(string name)
    {
        if (render.NativePtr != IntPtr.Zero) render.PushDebugGroup((NSString)name);
        else if (blit.NativePtr != IntPtr.Zero) blit.PushDebugGroup((NSString)name);
        else command.PushDebugGroup((NSString)name);
    }

    public void EndLabel()
    {
        if (render.NativePtr != IntPtr.Zero) render.PopDebugGroup();
        else if (blit.NativePtr != IntPtr.Zero) blit.PopDebugGroup();
        else command.PopDebugGroup();
    }

    // ---- the blit encoder (the mip chain's and the uploads') ----

    /// <summary>The blit encoder of this list's command buffer, opened when the first transfer needs it.</summary>
    internal MTLBlitCommandEncoder OpenBlit()
    {
        if (blit.NativePtr != IntPtr.Zero) return blit;
        if (render.NativePtr != IntPtr.Zero) throw new InvalidOperationException("a transfer inside a rendering");
        blit = command.BlitCommandEncoder();
        ownsBlit = true;
        return blit;
    }

    internal void EndBlit()
    {
        if (blit.NativePtr == IntPtr.Zero) return;
        if (ownsBlit) blit.EndEncoding();
        blit = default;
        ownsBlit = false;
    }

    /// <summary>The frame's list is reused every frame of its slot: the next frame's command buffer comes in here.</summary>
    internal void Rebind(MTLCommandBuffer next) => command = next;

    /// <summary>Closes what is open (the upload batch's own encoder is the caller's).</summary>
    internal void Finish()
    {
        EndBlit();
        if (render.NativePtr != IntPtr.Zero) EndRendering();
    }
}
