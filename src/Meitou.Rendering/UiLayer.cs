using Meitou.Rendering.Gpu;

namespace Meitou.Rendering;

/// <summary>
/// The interface's own picture: the overlays draw into <see cref="Texture"/> (transparent, display size, premultiplied alpha) instead of the
/// world's backbuffer, and <see cref="Composite"/> lays it over the backbuffer just before the present. The same colours come out as when the
/// overlays drew straight onto the backbuffer (a straight-alpha blend onto a cleared target, with the alpha channel accumulating
/// <c>a + dst.a * (1 - a)</c>, is the premultiplied "over" operator, and the composite is <c>ui + back * (1 - ui.a)</c>); the point is that
/// a later stage (frame generation) can take the world picture without the interface.
/// </summary>
public sealed class UiLayer : IDisposable
{
    const string Fragment = """
        #version 330 core
        in vec2 vUv;
        out vec4 fragColour;
        uniform sampler2D uUi;
        void main() { fragColour = texture(uUi, vUv); }
        """;

    readonly IGpuContext gpu;
    readonly LegacyProgram program;
    readonly SamplerSlot samplerUi;
    readonly SampledTexture pointSampler;
    GpuTexture? texture;

    public UiLayer(IGpuContext gpu)
    {
        this.gpu = gpu;
        program = LegacyProgram.Create(gpu, PostProcessShaders.Vertex, Fragment, "ui composite");
        samplerUi = program.Sampler("uUi");
        pointSampler = new(gpu.Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.Nearest, TextureMagFilter.Nearest, TextureWrapMode.ClampToEdge,
            TextureWrapMode.ClampToEdge, TextureWrapMode.Repeat, false, DepthFunction.Lequal, false, 1, false, 0)), default);
    }

    /// <summary>What the overlays draw into: valid after <see cref="Clear"/> of the frame.</summary>
    public GpuTexture? Texture => texture;

    /// <summary>Makes the texture again when the size changed, and clears it to transparent (the first thing of a frame, before any overlay draws).</summary>
    public GpuTexture Clear(int width, int height)
    {
        if (texture is null || texture.Desc.Width != width || texture.Desc.Height != height)
        {
            texture?.Dispose();   // released after the frames in flight
            texture = gpu.CreateTexture(new TextureDesc(InternalFormat.Rgba8, width, height, Use: TextureUse.ColourTarget | TextureUse.Sampled, Name: "ui layer"));
        }
        var cmd = gpu.BeginNative("ui layer clear");
        cmd.BeginRendering(new RenderingDesc(new RenderTarget(texture.Attachment(), LoadOp.Clear, default), default, width, height));
        cmd.EndRendering();
        gpu.EndNative(cmd);
        return texture;
    }

    /// <summary>Draws the layer over <paramref name="target"/> (the same size): <c>ui + target * (1 - ui.a)</c>.</summary>
    public void Composite(GpuTexture target)
    {
        if (texture is null) return;
        int width = target.Desc.Width, height = target.Desc.Height;
        var t = PassTargets.Of(target, null).At(new PixelViewport(0, 0, width, height, 0, 1));
        var cmd = gpu.BeginNative("ui composite");
        cmd.BeginRendering(t.Rendering);
        var state = DrawState.For(t.Formats, gpu.DepthClamp, blend: new BlendState(true, BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha));
        state.Record(cmd, t);
        cmd.BindPipeline(gpu.Pipelines.Get(state.Pipeline(program.Program, program.VertexLayout([]), PrimitiveType.Triangles, t.Formats, "ui composite")));
        program.Bind(samplerUi, pointSampler with { View = texture.View() });
        program.Flush(cmd);
        cmd.Draw(3);
        cmd.EndRendering();
        gpu.EndNative(cmd);
    }

    public void Dispose() => texture?.Dispose();
}
