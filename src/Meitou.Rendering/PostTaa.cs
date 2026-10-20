using System.Numerics;
using Meitou.Rendering.Gpu;

namespace Meitou.Rendering;

/// <summary>
/// The seam's temporal anti-aliasing (the first slice of the post chain's widening): the game's velocity and TAA shaders
/// (<see cref="UpscaleShaders.Velocity"/>, <see cref="UpscaleShaders.Taa"/>) at the scene's own size. The camera's projection
/// is jittered by <see cref="Jitter"/> (the caller applies the same offset to the scene's projection), the velocity pass writes
/// the reprojection into an RGBA16F motion target, and the TAA pass blends the jittered frame into a display-size history.
/// No render-scale upscaler yet; the vendor upscalers and the full <see cref="PostProcess"/> chain stay Vulkan-typed.
/// </summary>
sealed class PostTaa : IDisposable
{
    const float Blend = 0.1f;

    readonly IGpuContext ctx;
    readonly LegacyProgram velocity, taa;
    readonly UniformHandle nearToPrevious, farToPrevious, nearPlanes, farPlanes, fullPlanes, jitterNdc, hasFar, mode, tan, right, up, back, eyeY, waterY, waterReactive, sun, sunMotion;
    readonly SamplerSlot nearDepth, farDepth;
    readonly UniformHandle renderSize, displaySize, jitter, blend, reset;
    readonly SamplerSlot colour, motionSlot, history;
    readonly Dictionary<(GpuProgram Program, InternalFormat Format), GraphicsPipelineDesc> pipelines = [];
    readonly SampledTexture pointSampler, linearSampler;

    GpuTexture? motion, historyA, historyB;
    int width, height;
    bool historyValid, previousValid;
    Vector3 previousEye;
    Matrix4x4 previousRotation;
    float previousFov, previousAspect;

    public PostTaa(IGpuContext ctx)
    {
        this.ctx = ctx;
        // The TAA and velocity passes are engine textures in the engine's own orientation: no flip (the composite flips at the present).
        velocity = LegacyProgram.Create(ctx, PostProcessShaders.Vertex, UpscaleShaders.Velocity, "post velocity");
        taa = LegacyProgram.Create(ctx, PostProcessShaders.Vertex, UpscaleShaders.Taa, "post taa");
        var v = velocity;
        nearDepth = v.Sampler("uNearDepth");
        farDepth = v.Sampler("uFarDepth");
        nearToPrevious = v.Uniform("uNearToPrev");
        farToPrevious = v.Uniform("uFarToPrev");
        nearPlanes = v.Uniform("uNearPlanes");
        farPlanes = v.Uniform("uFarPlanes");
        fullPlanes = v.Uniform("uFullPlanes");
        jitterNdc = v.Uniform("uJitterNdc");
        hasFar = v.Uniform("uHasFar");
        mode = v.Uniform("uMode");
        tan = v.Uniform("uTan");
        right = v.Uniform("uRight");
        up = v.Uniform("uUp");
        back = v.Uniform("uBack");
        eyeY = v.Uniform("uEyeY");
        waterY = v.Uniform("uWaterY");
        waterReactive = v.Uniform("uWaterReactive");
        sun = v.Uniform("uSun");
        sunMotion = v.Uniform("uSunMotion");
        var t = taa;
        colour = t.Sampler("uColour");
        motionSlot = t.Sampler("uMotion");
        history = t.Sampler("uHistory");
        renderSize = t.Uniform("uRenderSize");
        displaySize = t.Uniform("uDisplaySize");
        jitter = t.Uniform("uJitter");
        blend = t.Uniform("uBlend");
        reset = t.Uniform("uReset");
        pointSampler = new SampledTexture(Sampled(TextureMinFilter.Nearest, TextureMagFilter.Nearest), default);
        linearSampler = new SampledTexture(Sampled(TextureMinFilter.Linear, TextureMagFilter.Linear), default);
    }

    GpuSampler Sampled(TextureMinFilter min, TextureMagFilter mag) =>
        ctx.Samplers.Get(SamplerDesc.FromGl(min, mag, TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge, TextureWrapMode.Repeat,
            false, DepthFunction.Lequal, false, 1, false, 0));

    /// <summary>The offset frame <paramref name="frame"/> moves the projection by, in render pixels.</summary>
    public static Vector2 Offset(long frame, int renderWidth) => Jitter.Offset(frame, Jitter.PhaseCount(renderWidth, renderWidth));

    /// <summary>
    /// Resolves the scene into the history: <paramref name="scene"/> and <paramref name="depth"/> are the frame's render-size targets,
    /// <paramref name="jitterPixels"/> is the offset the scene's projection used. Returns the TAA picture (at the scene's size).
    /// </summary>
    public GpuTexture Draw(IGpuCommandList cmd, GpuTexture scene, GpuTexture depth, Vector3 eye, Matrix4x4 view, float fov, float aspect,
        float near, float far, Vector2 jitterPixels)
    {
        Ensure(scene.Desc.Width, scene.Desc.Height);
        var rotation = view with { M41 = 0, M42 = 0, M43 = 0 };
        var projection = Jitter.Apply(Matrix4x4.CreatePerspectiveFieldOfView(fov, aspect, near, far), jitterPixels, width, height);
        var previousProjection = previousValid ? Matrix4x4.CreatePerspectiveFieldOfView(previousFov, previousAspect, near, far) : projection;
        var toPrevious = previousValid
            ? Reprojection.ClipToPrevious(rotation, eye, projection, previousRotation, previousEye, previousProjection)
            : Matrix4x4.Identity;

        // The motion: the near slice's depth carried into the previous frame (the Metal path has one depth slice).
        var v = velocity;
        v.Bind(nearDepth, pointSampler with { View = depth.View() });
        v.Bind(farDepth, ctx.Dummy(v.SamplerInfo(farDepth)));
        v.Set(nearToPrevious, toPrevious);
        v.Set(farToPrevious, Matrix4x4.Identity);
        v.Set(nearPlanes, near, far);
        v.Set(farPlanes, near, far);
        v.Set(fullPlanes, 1f, 1_000_000f);
        v.Set(jitterNdc, 2 * jitterPixels.X / width, 2 * jitterPixels.Y / height);
        v.Set(hasFar, 0);
        v.Set(mode, 0);
        float tanY = MathF.Tan(fov * 0.5f);
        v.Set(tan, tanY * aspect, tanY);
        v.Set(right, rotation.M11, rotation.M21, rotation.M31);
        v.Set(up, rotation.M12, rotation.M22, rotation.M32);
        v.Set(back, rotation.M13, rotation.M23, rotation.M33);
        v.Set(eyeY, eye.Y);
        v.Set(waterY, float.MinValue);
        v.Set(waterReactive, 0f);
        v.Set(sun, 0f, 0f, 0f, 0f);       // the sky's sun motion is off in this slice
        v.Set(sunMotion, 0f, 0f, 0f, 0f);
        Fullscreen(cmd, velocity, motion!, width, height);

        // The TAA: this frame's scene and motion against the previous history, into the other history target.
        var previous = historyA!;
        var output = historyB!;
        var t = taa;
        t.Bind(colour, linearSampler with { View = scene.View() });
        t.Bind(motionSlot, pointSampler with { View = motion!.View() });
        t.Bind(history, linearSampler with { View = previous.View() });
        t.Set(renderSize, (float)width, (float)height);
        t.Set(displaySize, (float)width, (float)height);
        t.Set(jitter, jitterPixels.X, jitterPixels.Y);
        t.Set(blend, Blend);
        t.Set(reset, historyValid && previousValid ? 0 : 1);
        Fullscreen(cmd, taa, output, width, height);

        historyA = output;
        historyB = previous;
        historyValid = true;
        previousEye = eye;
        previousRotation = rotation;
        previousFov = fov;
        previousAspect = aspect;
        previousValid = true;
        return output;
    }

    void Ensure(int w, int h)
    {
        if (motion is not null && width == w && height == h) return;
        DisposeTargets();
        width = w;
        height = h;
        motion = ctx.CreateTexture(new TextureDesc(InternalFormat.Rgba16f, w, h,
            Use: TextureUse.ColourTarget | TextureUse.Sampled, Name: "post taa motion"));
        historyA = ctx.CreateTexture(new TextureDesc(InternalFormat.Rgba16f, w, h,
            Use: TextureUse.ColourTarget | TextureUse.Sampled, Name: "post taa history"));
        historyB = ctx.CreateTexture(new TextureDesc(InternalFormat.Rgba16f, w, h,
            Use: TextureUse.ColourTarget | TextureUse.Sampled, Name: "post taa history"));
        historyValid = false;
        previousValid = false;
    }

    /// <summary>One fullscreen triangle of <paramref name="program"/> into <paramref name="target"/>.</summary>
    void Fullscreen(IGpuCommandList cmd, LegacyProgram program, GpuTexture target, int w, int h)
    {
        var formats = new AttachmentFormats(target.Desc.Format, InternalFormat.None);
        if (!pipelines.TryGetValue((program.Program, formats.Colour), out var desc))
        {
            var state = DrawState.For(formats, false);
            pipelines[(program.Program, formats.Colour)] = desc = state.Pipeline(program.Program, program.VertexLayout([]), PrimitiveType.Triangles,
                formats, program.Name);
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

    void DisposeTargets()
    {
        motion?.Dispose();
        historyA?.Dispose();
        historyB?.Dispose();
        motion = historyA = historyB = null;
    }

    public void Dispose()
    {
        DisposeTargets();
        velocity.Dispose();
        taa.Dispose();
    }
}
