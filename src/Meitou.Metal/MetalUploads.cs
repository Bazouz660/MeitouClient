using System.Runtime.InteropServices;
using Meitou.Rendering.Gpu;
using SharpMetal.Foundation;
using SharpMetal.Metal;

namespace Meitou.Metal;

/// <summary>
/// The uploads: each <see cref="IGpuUploads.Begin"/> batch records its copies into its own command buffer (a
/// blit encoder over a shared staging ring), which is committed when the batch is disposed. The queue executes
/// its command buffers in commit order, so a batch disposed before the frame's own commands are committed is
/// ordered ahead of them. The staging buffers are shared mode — on Apple silicon the whole memory is unified,
/// and the copy is the GPU's own from it (no separate staging allocation).
/// </summary>
public sealed class MetalUploads : IGpuUploads
{
    readonly MetalContext ctx;

    public MetalUploads(MetalContext ctx) => this.ctx = ctx;

    public IGpuUploadBatch Begin() => new MetalUploadBatch(ctx);

    internal sealed unsafe class MetalUploadBatch : IGpuUploadBatch
    {
        readonly MetalContext ctx;
        readonly MTLCommandBuffer command;
        readonly MetalCommandList list;
        MTLBuffer staging;
        ulong stagingSize, stagingUsed;

        public MetalUploadBatch(MetalContext ctx)
        {
            this.ctx = ctx;
            command = ctx.Queue.CommandBuffer();
            list = new MetalCommandList(ctx, "uploads", command);
        }

        public IGpuCommandList Commands => list;

        public GpuTexture Create(TextureDesc desc) => ctx.CreateTexture(desc);

        public void Write(GpuTexture target, int level, int layer, in PixelRect region, ReadOnlySpan<byte> data)
        {
            var desc = target.Desc;
            int block = MetalConventions.BlockWidth(desc.Format);
            int texel = MetalConventions.TexelBytes(desc.Format);
            // A block-compressed row is whole blocks (a tail mip's 1x1 level still strides one block).
            ulong bytesPerRow = (ulong)(((region.Width + block - 1) / block) * texel);
            ulong blockRows = (ulong)((region.Height + block - 1) / block);
            ulong bytesPerImage = bytesPerRow * blockRows;
            if ((ulong)data.Length < bytesPerImage) throw new ArgumentException($"the upload has {data.Length} bytes, the region needs {bytesPerImage}");
            // Metal's blit takes the source row stride as the destination level's whole row, also for a sub-rectangle (the terrain's
            // overlay and colour-map tiles): repack the rows with that stride and the tile's x offset.
            ulong levelWidth = (ulong)Math.Max(desc.Width >> level, 1), neededRow = ((levelWidth + (ulong)block - 1) / (ulong)block) * (ulong)texel;
            var blit = list.OpenBlit();
            if (bytesPerRow != neededRow || region.X != 0)
            {
                ulong neededImage = neededRow * blockRows, xBytes = (ulong)(region.X / block) * (ulong)texel;
                var staged = Reserve(checked((int)neededImage), out ulong stagedOffset);
                var dst = (byte*)staged.Contents + stagedOffset;
                if (staged.Contents == IntPtr.Zero || dst is null) throw new InvalidOperationException($"no staging for {desc.Name} L{level}: {neededImage} bytes");
                new Span<byte>(dst, (int)neededImage).Clear();
                fixed (byte* src = data)
                {
                    if (src is null) throw new InvalidOperationException($"no source for {desc.Name} L{level}: {data.Length} bytes");
                    for (ulong r = 0; r < blockRows; r++)
                        Buffer.MemoryCopy(src + r * bytesPerRow, dst + (nint)(r * neededRow + xBytes), (long)neededRow - (long)xBytes, (long)bytesPerRow);
                }
                blit.CopyFromBuffer(staged, stagedOffset, neededRow, neededImage,
                    new MTLSize { width = (ulong)region.Width, height = (ulong)region.Height, depth = 1 },
                    ctx.TextureAt(target), (ulong)layer, (ulong)level,
                    new MTLOrigin { x = (ulong)region.X, y = (ulong)region.Y, z = 0 });
            }
            else
            {
                var buffer = Reserve(data.Length, out ulong offset);
                data.CopyTo(new Span<byte>((void*)(buffer.Contents + (nint)offset), data.Length));
                blit.CopyFromBuffer(buffer, offset, bytesPerRow, bytesPerImage,
                    new MTLSize { width = (ulong)region.Width, height = (ulong)region.Height, depth = 1 },
                    ctx.TextureAt(target), (ulong)layer, (ulong)level,
                    new MTLOrigin { x = (ulong)region.X, y = (ulong)region.Y, z = 0 });
            }
            ctx.CountUpload(data.Length);
        }

        public void Write(DeviceBuffer target, ulong offset, ReadOnlySpan<byte> data)
        {
            var buffer = Reserve(data.Length, out ulong stagingOffset);
            data.CopyTo(new Span<byte>((void*)(buffer.Contents + (nint)stagingOffset), data.Length));
            var blit = list.OpenBlit();
            blit.CopyFromBuffer(buffer, stagingOffset, ctx.BufferAt(target.Handle), offset, (ulong)data.Length);
            ctx.CountUpload(data.Length);
        }

        /// <summary>The staging room for <paramref name="bytes"/> more (a grown buffer when the current is full).</summary>
        MTLBuffer Reserve(int bytes, out ulong offset)
        {
            if (stagingSize - stagingUsed < (ulong)bytes)
            {
                if (staging.NativePtr != IntPtr.Zero) staging.Dispose();
                stagingSize = Math.Max((ulong)bytes, stagingSize == 0 ? 1 << 20 : stagingSize * 2);
                staging = ctx.Device.NewBuffer(stagingSize, MTLResourceOptions.ResourceStorageModeShared);
                stagingUsed = 0;
            }
            offset = stagingUsed;
            stagingUsed += (ulong)bytes;
            return staging;
        }

        public void Dispose()
        {
            list.EndBlit();
            command.Commit();
            if (staging.NativePtr != IntPtr.Zero) staging.Dispose();
        }
    }
}
