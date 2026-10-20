using System.Diagnostics;
using Meitou.Rendering.Gpu;

namespace Meitou.Rendering;

/// <summary>
/// The seam's composite pass: a scene's HDR colour through the game's exposure and tone map (<see cref="PostProcessShaders.Luminance"/>,
/// <see cref="PostProcessShaders.Adapt"/>, <see cref="PostProcessShaders.Composite"/>) into an LDR target. <see cref="PostProcess"/> itself
/// is still Vulkan-typed; this runs its final shaders from an <see cref="IGpuContext"/> so a second backend gets the same exposure band,
/// curve, grading and dither until the whole chain is widened. The luminance is measured at 256², its mip chain reduced to 1×1 and adapted
/// with the game's rate; a still picture uses the instant blend.
/// </summary>
sealed class PostComposite : IDisposable
{
    const int LuminanceSize = 256, LuminanceLevels = 9;
    const float AdaptationRate = 0.5f;

    readonly IGpuContext ctx;
    readonly LegacyProgram composite, luminance, adapt;
    readonly UniformHandle exposure, useAo, dither, auto, tone, toneMix, grade, saturation, contrast, night, balance, sceneSize;
    readonly UniformHandle lumCell, adaptLevel, adaptBlend, adaptBand;
    readonly SamplerSlot sceneSlot, aoSlot, adaptedSlot, lumSceneSlot, lumSlot, lastSlot;
    readonly SampledTexture sceneSampler, luminanceSampler, plainSampler;
    readonly Dictionary<(GpuProgram Program, InternalFormat Format), GraphicsPipelineDesc> pipelines = [];
    GpuTexture? luminanceTarget, adaptA, adaptB;
    long adaptClock;
    bool adaptedValid;

    /// <param name="flipY">The backend's render targets are not stored flipped (Metal's are not: the present flips the picture, where
    /// Vulkan's negative viewport does the flip into the target itself), so the fullscreen passes sample the scene bottom-up.</param>
    public PostComposite(IGpuContext ctx, bool flipY = false)
    {
        this.ctx = ctx;
        var vertex = flipY
            ? PostProcessShaders.Vertex.Replace("vUv = p * 0.5 + 0.5;", "vUv = vec2(p.x * 0.5 + 0.5, 0.5 - p.y * 0.5);")
            : PostProcessShaders.Vertex;
        composite = LegacyProgram.Create(ctx, vertex, PostProcessShaders.Composite, "post composite");
        luminance = LegacyProgram.Create(ctx, vertex, PostProcessShaders.Luminance, "post luminance");
        adapt = LegacyProgram.Create(ctx, vertex, PostProcessShaders.Adapt, "post exposure adapted");
        var c = composite;
        exposure = c.Uniform("uExposure");
        useAo = c.Uniform("uUseAo");
        dither = c.Uniform("uDither");
        auto = c.Uniform("uAuto");
        tone = c.Uniform("uTone");
        toneMix = c.Uniform("uToneMix");
        grade = c.Uniform("uGrade");
        saturation = c.Uniform("uSaturation");
        contrast = c.Uniform("uContrast");
        night = c.Uniform("uNight");
        balance = c.Uniform("uBalance");
        sceneSize = c.Uniform("uSceneSize");
        sceneSlot = c.Sampler("uScene");
        aoSlot = c.Sampler("uAo");
        adaptedSlot = c.Sampler("uAdapted");
        lumSceneSlot = luminance.Sampler("uScene");
        lumCell = luminance.Uniform("uCell");
        lumSlot = adapt.Sampler("uLuminance");
        lastSlot = adapt.Sampler("uLast");
        adaptLevel = adapt.Uniform("uLevel");
        adaptBlend = adapt.Uniform("uBlend");
        adaptBand = adapt.Uniform("uBand");
        sceneSampler = Sample(TextureMinFilter.Linear, TextureMagFilter.Linear);
        luminanceSampler = Sample(TextureMinFilter.LinearMipmapNearest, TextureMagFilter.Linear);
        plainSampler = Sample(TextureMinFilter.Linear, TextureMagFilter.Linear);
    }

    SampledTexture Sample(TextureMinFilter min, TextureMagFilter mag) =>
        new(ctx.Samplers.Get(SamplerDesc.FromGl(min, mag, TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge, TextureWrapMode.Repeat,
            false, DepthFunction.Lequal, false, 1, false, 0)), default);

    /// <summary>Draws the scene into <paramref name="target"/> at its own size; <paramref name="band"/> is the game's exposure clamp.</summary>
    public void Draw(IGpuCommandList cmd, GpuTexture scene, GpuTexture target, PostOptions o, (float Min, float Max) band)
    {
        MakeTargets();
        // The luminance: 16 taps of the scene into 256², then the mip chain down to 1×1.
        luminance.Bind(lumSceneSlot, sceneSampler with { View = scene.View() });
        luminance.Set(lumCell, 1f / LuminanceSize, 1f / LuminanceSize);
        Fullscreen(cmd, luminance, luminanceTarget!, LuminanceSize, LuminanceSize);
        cmd.GenerateMips(luminanceTarget!);
        cmd.Order();

        // The adaptation: this frame's mean against the last, blended at the game's rate (instant for a still) inside the band.
        var previous = adaptA;
        (adaptA, adaptB) = (adaptB, adaptA);
        long now = Stopwatch.GetTimestamp();
        float dt = adaptClock == 0 ? 0 : (float)Stopwatch.GetElapsedTime(adaptClock, now).TotalSeconds;
        adaptClock = now;
        float blend = !adaptedValid ? 1 : 1 - MathF.Exp(-dt * AdaptationRate);
        adapt.Bind(lumSlot, luminanceSampler with { View = luminanceTarget!.View() });
        adapt.Bind(lastSlot, previous is { } last ? plainSampler with { View = last.View() } : ctx.Dummy(adapt.SamplerInfo(lastSlot)));
        adapt.Set(adaptLevel, MathF.Log2(LuminanceSize));
        adapt.Set(adaptBlend, blend);
        adapt.Set(adaptBand, band.Min, band.Max);
        Fullscreen(cmd, adapt, adaptB!, 1, 1);
        adaptedValid = true;

        // The composite: auto exposure (0.55 / adapted), the game's tone map, grade and dither.
        var p = composite;
        p.Bind(sceneSlot, sceneSampler with { View = scene.View() });
        p.Bind(aoSlot, ctx.Dummy(p.SamplerInfo(aoSlot)));
        p.Bind(adaptedSlot, plainSampler with { View = adaptB!.View() });
        p.Set(exposure, o.Exposure);
        p.Set(useAo, 0);
        p.Set(dither, o.Dither ? 1 : 0);
        p.Set(auto, 1);
        p.Set(tone, (int)o.ToneMap);
        p.Set(toneMix, Math.Clamp(o.ToneMix, 0, 1));
        p.Set(grade, o.Grade ? 1 : 0);
        p.Set(saturation, PostOptions.Saturation);
        p.Set(contrast, PostOptions.Contrast);
        p.Set(night, Math.Clamp(o.NightGradeStrength, 0, 1));
        p.Set(balance, o.Balance);
        p.Set(sceneSize, 0f, 0f);
        Fullscreen(cmd, composite, target, target.Desc.Width, target.Desc.Height);
    }

    void MakeTargets()
    {
        if (luminanceTarget is not null) return;
        luminanceTarget = ctx.CreateTexture(new TextureDesc(InternalFormat.R32f, LuminanceSize, LuminanceSize, LuminanceLevels,
            Use: TextureUse.ColourTarget | TextureUse.Sampled | TextureUse.TransferSrc | TextureUse.TransferDst, Name: "post luminance"));
        adaptA = ctx.CreateTexture(new TextureDesc(InternalFormat.RG32f, 1, 1, Use: TextureUse.ColourTarget | TextureUse.Sampled | TextureUse.TransferSrc, Name: "post exposure adapted"));
        adaptB = ctx.CreateTexture(new TextureDesc(InternalFormat.RG32f, 1, 1, Use: TextureUse.ColourTarget | TextureUse.Sampled | TextureUse.TransferSrc, Name: "post exposure adapted"));
    }

    /// <summary>One fullscreen triangle of <paramref name="program"/> into <paramref name="target"/> (its uniforms and samplers set before).</summary>
    void Fullscreen(IGpuCommandList cmd, LegacyProgram program, GpuTexture target, int w, int h)
    {
        var formats = new AttachmentFormats(target.Desc.Format, InternalFormat.None);
        if (!pipelines.TryGetValue((program.Program, formats.Colour), out var desc))
        {
            var state = DrawState.For(formats, false);
            pipelines[(program.Program, formats.Colour)] = desc = state.Pipeline(program.Program, program.VertexLayout([]), PrimitiveType.Triangles, formats, program.Name);
        }
        cmd.BeginRendering(new RenderingDesc(new RenderTarget(target.Attachment(), LoadOp.DontCare, default), default, w, h));
        cmd.SetViewport(new PixelViewport(0, 0, w, h, 0, 1));
        cmd.SetScissor(new PixelRect(0, 0, (uint)w, (uint)h));
        cmd.SetRaster(CullMode.None, FrontFaceDirection.Ccw);
        cmd.SetDepth(false, false, DepthFunction.Always);
        cmd.SetDepthBias(false, 0, 0);
        cmd.BindPipeline(ctx.Pipelines.Get(desc));
        program.Flush(cmd);
        cmd.Draw(3);
        cmd.EndRendering();
        cmd.Order();
    }

    public void Dispose()
    {
        luminanceTarget?.Dispose();
        adaptA?.Dispose();
        adaptB?.Dispose();
        composite.Dispose();
        luminance.Dispose();
        adapt.Dispose();
    }
}
