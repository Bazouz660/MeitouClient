using System.Numerics;
using Meitou.Rendering.Gpu;

namespace Meitou.Rendering;

/// <summary>
/// The seam's temporal anti-aliasing (the first slice of the post chain's widening): the game's velocity and TAA shaders
/// (<see cref="UpscaleShaders.Velocity"/>, <see cref="UpscaleShaders.Taa"/>). The camera's projection is jittered by
/// <see cref="Jitter"/> (the caller applies the same offset to the scene's projection), the velocity pass writes the reprojection
/// into a render-size RGBA16F motion target, and the TAA pass reconstructs the jittered scene into a display-size history pair.
/// The vendor upscalers and the full <see cref="PostProcess"/> chain stay Vulkan-typed.
/// </summary>
sealed class PostTaa : IDisposable
{
    const float Blend = 0.1f;

    readonly IGpuContext ctx;
    readonly LegacyProgram velocity, taa;
    readonly UniformHandle nearToPrevious, farToPrevious, nearPlanes, farPlanes, fullPlanes, jitterNdc, hasFar, mode,
        clipDepthZeroToOne, flipDepthY, tan, right, up, back, eyeY, waterY, waterReactive, sun, sunMotion;
    readonly SamplerSlot nearDepth, farDepth;
    readonly UniformHandle renderSize, displaySize, jitter, blend, reset;
    readonly SamplerSlot colour, motionSlot, history;
    readonly Dictionary<(GpuProgram Program, InternalFormat Format), GraphicsPipelineDesc> pipelines = [];
    readonly SampledTexture pointSampler, linearSampler;
    readonly SunMotionTracker sunTracker = new();

    GpuTexture? motion, reactive, historyA, historyB, externalOutput;
    int width, height, displayWidth, displayHeight;
    bool historyValid, previousValid, externalValid;
    IUpscaler? external;
    Vector3 previousEye;
    Matrix4x4 previousRotation;
    float previousFov, previousAspect;

    /// <summary>The frame's particles, whose coverage is added to the upscalers' reactive mask (see <see cref="ParticleCoverage"/>).</summary>
    public ParticleRenderer? Particles { get; set; }

    /// <summary>
    /// Draws the frame's full-size particles and then the low-resolution accumulations into <c>target</c>, whose mask is
    /// <c>mask</c>; <c>clear</c> zeroes the target first (the reactive mask when there is no water value to keep).
    /// <see cref="MetalPostChain"/> supplies this, because it owns the particle renderer and the low-resolution targets.
    /// </summary>
    public Action<IGpuCommandList, GpuTexture, ColorMask, bool>? ParticleCoverage { get; set; }

    /// <summary>
    /// An upscaler outside the TAA (the Metal host's MetalFX): when set, the velocity pass still runs and the external
    /// scaler receives the scene, depth and motion in <see cref="UpscaleInputs"/>; TAA runs when it is null or fails.
    /// </summary>
    /// <summary>Moving geometry's own motion over the camera's, called after the velocity pass.</summary>
    public Action<MotionTargets>? ObjectMotion { get; set; }

    public IUpscaler? External
    {
        get => external;
        set
        {
            if (ReferenceEquals(external, value)) return;
            external = value;
            externalValid = false;
            historyValid = false;
        }
    }

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
        clipDepthZeroToOne = v.Uniform("uClipDepthZeroToOne");
        flipDepthY = v.Uniform("uFlipDepthY");
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

    /// <summary>The offset frame <paramref name="frame"/> moves the projection by, in render pixels (the phase count covers every
    /// display pixel at the render scale).</summary>
    public static Vector2 Offset(long frame, int renderWidth, int displayWidth) =>
        Jitter.Offset(frame, Jitter.PhaseCount(renderWidth, displayWidth));

    /// <summary>
    /// Resolves the scene into the history: <paramref name="scene"/> and <paramref name="depth"/> are the frame's render-size targets,
    /// <paramref name="jitterPixels"/> is the offset the scene's projection used. Returns the TAA picture (at the scene's size).
    /// </summary>
    public GpuTexture Draw(IGpuCommandList cmd, GpuTexture scene, GpuTexture depth, Vector3 eye, Matrix4x4 view, float fov, float aspect,
        float near, float far, Vector2 jitterPixels, int outputWidth, int outputHeight, float deltaSeconds = 1f / 60f, float? waterHeight = null)
    {
        Ensure(scene.Desc.Width, scene.Desc.Height, outputWidth, outputHeight);
        EnsureExternalOutput();
        var rotation = view with { M41 = 0, M42 = 0, M43 = 0 };
        bool cut = previousValid && Vector3.Distance(eye, previousEye) > 5000;
        if (cut) sunTracker.Reset();
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
        v.Set(clipDepthZeroToOne, ctx.ClipDepthZeroToOne ? 1 : 0);
        v.Set(flipDepthY, ctx.RenderTargetsBottomUp ? 0 : 1);
        float tanY = MathF.Tan(fov * 0.5f);
        v.Set(tan, tanY * aspect, tanY);
        v.Set(right, rotation.M11, rotation.M21, rotation.M31);
        v.Set(up, rotation.M12, rotation.M22, rotation.M32);
        v.Set(back, rotation.M13, rotation.M23, rotation.M33);
        v.Set(eyeY, eye.Y);
        v.Set(waterY, waterHeight ?? float.MinValue);
        v.Set(waterReactive, 0.5f);
        var sunData = sunTracker.Prepare(ctx.Globals, true, previousValid && !cut, rotation, fov, aspect);
        v.Set(sun, sunData.Sun);
        v.Set(sunMotion, sunData.Motion);
        Fullscreen(cmd, velocity, motion!, width, height);
        if (External is not null && reactive is not null)
        {
            v.Set(waterY, waterHeight ?? float.MinValue);
            v.Set(waterReactive, 0.5f);
            v.Set(mode, 2);
            Fullscreen(cmd, velocity, reactive, width, height);
        }
        if (ObjectMotion is { } objectMotion)
        {
            var targets = PassTargets.Of(motion, null);
            cmd.BeginRendering(targets.Rendering);
            ctx.BeginHostPass(cmd, targets, DrawState.For(targets.Formats, false, mask: ColorMask.R | ColorMask.G));
            objectMotion(new MotionTargets(new SampledTexture(pointSampler.Sampler, depth.View()), new Vector2(near, far),
                new Vector2(2 * jitterPixels.X / width, 2 * jitterPixels.Y / height), height / (2 * MathF.Tan(fov * 0.5f))));
            ctx.EndHostPass(cmd);
            cmd.EndRendering();
            cmd.Order();
        }

        // Weather particles are thin and fast: without their own coverage in the reactive mask the temporal
        // history keeps about 90% of what was behind them and rain/dust almost disappears. The full-size quads
        // write themselves; the low-resolution accumulations are upsampled by PostComposite, as on Vulkan.
        bool covered = Particles is { HasCoverage: true } && ParticleCoverage is not null;
        if (covered && External is not null)
            ParticleCoverage!(cmd, reactive!, ColorMask.R, waterHeight is null);

        if (External is { } upscaler && externalOutput is { } externalTarget)
        {
            var fullProjection = Matrix4x4.CreatePerspectiveFieldOfView(fov, aspect, near, far);
            var previousFullProjection = previousValid
                ? Matrix4x4.CreatePerspectiveFieldOfView(previousFov, previousAspect, near, far)
                : fullProjection;
            bool resetExternal = cut || !externalValid || !previousValid;
            bool done = upscaler.Dispatch(new UpscaleInputs
            {
                Colour = scene, Depth = depth, Motion = motion!, Output = externalTarget,
                Reactive = reactive,
                RenderWidth = width, RenderHeight = height, DisplayWidth = displayWidth, DisplayHeight = displayHeight,
                JitterPixels = jitterPixels, Near = near, Far = far, FieldOfView = fov,
                DeltaSeconds = Math.Clamp(deltaSeconds, 0.001f, 0.25f), Sharpness = 0, Reset = resetExternal,
                ViewToClip = fullProjection,
                ClipToPreviousClip = previousValid
                    ? Reprojection.ClipToPrevious(rotation, eye, fullProjection, previousRotation, previousEye, previousFullProjection)
                    : Matrix4x4.Identity,
                Eye = eye, Right = new Vector3(rotation.M11, rotation.M21, rotation.M31),
                Up = new Vector3(rotation.M12, rotation.M22, rotation.M32), Forward = -new Vector3(rotation.M13, rotation.M23, rotation.M33),
                Aspect = aspect,
            });
            if (done)
            {
                externalValid = true;
                historyValid = false;   // returning to TAA starts a fresh history
                previousEye = eye;
                previousRotation = rotation;
                previousFov = fov;
                previousAspect = aspect;
                previousValid = true;
                return externalTarget;
            }
            Console.WriteLine($"upscaler  {upscaler.Name} failed; using TAA");
            upscaler.Dispose();
            External = null;
        }

        // The TAA: this frame's scene and motion against the previous history, into the other history target.
        if (covered)
            ParticleCoverage!(cmd, motion!, ColorMask.A, false);   // the TAA reads the motion target's alpha
        var previous = historyA!;
        var output = historyB!;
        var t = taa;
        t.Bind(colour, linearSampler with { View = scene.View() });
        t.Bind(motionSlot, pointSampler with { View = motion!.View() });
        t.Bind(history, linearSampler with { View = previous.View() });
        t.Set(renderSize, (float)width, (float)height);
        t.Set(displaySize, (float)displayWidth, (float)displayHeight);
        t.Set(jitter, jitterPixels.X, jitterPixels.Y);
        t.Set(blend, Blend);
        t.Set(reset, cut || !historyValid || !previousValid ? 1 : 0);
        Fullscreen(cmd, taa, output, displayWidth, displayHeight);

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

    void Ensure(int w, int h, int dw, int dh)
    {
        if (motion is not null && width == w && height == h && displayWidth == dw && displayHeight == dh) return;
        DisposeTargets();
        width = w;
        height = h;
        displayWidth = dw;
        displayHeight = dh;
        motion = ctx.CreateTexture(new TextureDesc(InternalFormat.Rgba16f, w, h,
            Use: TextureUse.ColourTarget | TextureUse.Sampled, Name: "post taa motion"));
        reactive = ctx.CreateTexture(new TextureDesc(InternalFormat.R32f, w, h,
            Use: TextureUse.ColourTarget | TextureUse.Sampled, Name: "post taa reactive"));
        historyA = ctx.CreateTexture(new TextureDesc(InternalFormat.Rgba16f, dw, dh,
            Use: TextureUse.ColourTarget | TextureUse.Sampled, Name: "post taa history"));
        historyB = ctx.CreateTexture(new TextureDesc(InternalFormat.Rgba16f, dw, dh,
            Use: TextureUse.ColourTarget | TextureUse.Sampled, Name: "post taa history"));
        historyValid = false;
        externalValid = false;
        previousValid = false;
        sunTracker.Reset();
    }

    void EnsureExternalOutput()
    {
        if (External is null) return;
        if (externalOutput is not null && externalOutput.Desc.Width == displayWidth && externalOutput.Desc.Height == displayHeight) return;
        externalOutput?.Dispose();
        externalOutput = ctx.CreateTexture(new TextureDesc(InternalFormat.Rgba16f, displayWidth, displayHeight,
            Use: TextureUse.ColourTarget | TextureUse.Sampled | TextureUse.Storage, Name: "post external upscale", Private: true));
        externalValid = false;
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
        reactive?.Dispose();
        historyA?.Dispose();
        historyB?.Dispose();
        externalOutput?.Dispose();
        motion = reactive = historyA = historyB = externalOutput = null;
    }

    public void Dispose()
    {
        external?.Dispose();
        external = null;
        DisposeTargets();
        velocity.Dispose();
        taa.Dispose();
    }
}
