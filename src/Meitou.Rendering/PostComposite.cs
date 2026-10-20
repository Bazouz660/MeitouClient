using Meitou.Rendering.Gpu;

namespace Meitou.Rendering;

/// <summary>
/// The seam's composite pass: a scene's HDR colour through the game's tone map (<see cref="PostProcessShaders.Composite"/>) into an LDR
/// target. <see cref="PostProcess"/> itself is still Vulkan-typed; this runs its final shader from an <see cref="IGpuContext"/> so a second
/// backend gets the same curve, grading and dither until the whole chain is widened. No auto exposure yet: uAuto stays 0 and
/// <see cref="PostOptions.Exposure"/> is the exposure (the luminance reduction's passes come with the chain).
/// </summary>
sealed class PostComposite : IDisposable
{
    readonly IGpuContext ctx;
    readonly LegacyProgram program;
    readonly UniformHandle exposure, useAo, dither, auto, tone, toneMix, grade, saturation, contrast, night, balance, sceneSize;
    readonly SamplerSlot sceneSlot, aoSlot, adaptedSlot;
    SampledTexture sceneSampler;
    GraphicsPipelineDesc? cachedPipeline;
    ImageFormatsPair cachedFormats;

    /// <summary>The formats a cached pipeline was made for (the target's and the lack of a depth attachment).</summary>
    readonly record struct ImageFormatsPair(InternalFormat Colour, InternalFormat Depth, int Samples);

    /// <param name="flipY">The backend's render targets are not stored flipped (Metal's are not: the present flips the picture, where
    /// Vulkan's negative viewport does the flip into the target itself), so the fullscreen pass samples the scene bottom-up.</param>
    public PostComposite(IGpuContext ctx, bool flipY = false)
    {
        this.ctx = ctx;
        var vertex = flipY
            ? PostProcessShaders.Vertex.Replace("vUv = p * 0.5 + 0.5;", "vUv = vec2(p.x * 0.5 + 0.5, 0.5 - p.y * 0.5);")
            : PostProcessShaders.Vertex;
        program = LegacyProgram.Create(ctx, vertex, PostProcessShaders.Composite, "post composite");
        var p = program;
        exposure = p.Uniform("uExposure");
        useAo = p.Uniform("uUseAo");
        dither = p.Uniform("uDither");
        auto = p.Uniform("uAuto");
        tone = p.Uniform("uTone");
        toneMix = p.Uniform("uToneMix");
        grade = p.Uniform("uGrade");
        saturation = p.Uniform("uSaturation");
        contrast = p.Uniform("uContrast");
        night = p.Uniform("uNight");
        balance = p.Uniform("uBalance");
        sceneSize = p.Uniform("uSceneSize");
        sceneSlot = p.Sampler("uScene");
        aoSlot = p.Sampler("uAo");
        adaptedSlot = p.Sampler("uAdapted");
        sceneSampler = new SampledTexture(ctx.Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.Linear, TextureMagFilter.Linear,
            TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge, TextureWrapMode.Repeat, false, DepthFunction.Lequal, false, 1, false, 0)),
            default);
    }

    /// <summary>Draws the scene into <paramref name="target"/> at its own size (the target is the display's picture).</summary>
    public void Draw(IGpuCommandList cmd, GpuTexture scene, GpuTexture target, PostOptions o)
    {
        var p = program;
        sceneSampler = new SampledTexture(sceneSampler.Sampler, scene.View());
        p.Bind(sceneSlot, sceneSampler);
        p.Bind(aoSlot, ctx.Dummy(p.SamplerInfo(aoSlot)));
        p.Bind(adaptedSlot, ctx.Dummy(p.SamplerInfo(adaptedSlot)));
        p.Set(exposure, o.Exposure);
        p.Set(useAo, 0);
        p.Set(dither, o.Dither ? 1 : 0);
        p.Set(auto, 0);
        p.Set(tone, (int)o.ToneMap);
        p.Set(toneMix, Math.Clamp(o.ToneMix, 0, 1));
        p.Set(grade, o.Grade ? 1 : 0);
        p.Set(saturation, PostOptions.Saturation);
        p.Set(contrast, PostOptions.Contrast);
        p.Set(night, 0f);
        p.Set(balance, o.Balance);
        p.Set(sceneSize, 0f, 0f);

        int w = target.Desc.Width, h = target.Desc.Height;
        var formats = new AttachmentFormats(target.Desc.Format, InternalFormat.None);
        var key = new ImageFormatsPair(formats.Colour, formats.Depth, formats.Samples);
        if (cachedPipeline is not { } desc || cachedFormats != key)
        {
            var state = DrawState.For(formats, false);
            cachedPipeline = state.Pipeline(p.Program, p.VertexLayout([]), PrimitiveType.Triangles, formats, "post composite");
            cachedFormats = key;
        }
        var pipeline = ctx.Pipelines.Get(cachedPipeline.Value);
        cmd.BeginRendering(new RenderingDesc(new RenderTarget(target.Attachment(), LoadOp.DontCare, default), default, w, h));
        cmd.SetViewport(new PixelViewport(0, 0, w, h, 0, 1));
        cmd.SetScissor(new PixelRect(0, 0, (uint)w, (uint)h));
        cmd.SetRaster(CullMode.None, FrontFaceDirection.Ccw);
        cmd.SetDepth(false, false, DepthFunction.Always);
        cmd.SetDepthBias(false, 0, 0);
        cmd.BindPipeline(pipeline);
        p.Flush(cmd);
        cmd.Draw(3);
        cmd.EndRendering();
        cmd.Order();
    }

    public void Dispose() => program.Dispose();
}
