#if MEITOU_METAL
using System.Diagnostics;
using System.Numerics;
using Meitou.Metal;
using Meitou.Rendering;
using Meitou.Rendering.Gpu;

namespace Meitou.Game;

/// <summary>
/// Metal's slice of the shared post-chain seam: <see cref="PostComposite"/> (exposure, tone map, grade, fog volumes, SSAO, shafts,
/// FXAA, heat haze) and <see cref="PostTaa"/> (velocity, MetalFX or TAA). <see cref="WorldFrame.Draw"/> drives it like Vulkan's
/// <see cref="PostProcess"/>; the scene colour is the chain's own target, while Metal's frame owns the depth, albedo and backbuffer.
/// </summary>
sealed class MetalPostChain : IWorldPostChain, IDisposable
{
    readonly MetalContext ctx;
    readonly PostComposite composite;
    readonly PostTaa taa;
    readonly PostOptions options;
    readonly Func<bool> noTaa;
    readonly Func<bool> temporal;
    readonly Func<long, int, int, Vector2> jitter;
    readonly Stopwatch adaptationClock = Stopwatch.StartNew();

    GpuTexture? sceneColour;
    int displayWidth, displayHeight;
    long frame;
    double lastAdaptation;

    public MetalPostChain(MetalContext ctx, PostComposite composite, PostTaa taa, PostOptions options, Func<bool> noTaa, Func<bool> temporal,
        Func<long, int, int, Vector2> jitter)
    {
        this.ctx = ctx;
        this.composite = composite;
        this.taa = taa;
        this.options = options;
        this.noTaa = noTaa;
        this.temporal = temporal;
        this.jitter = jitter;
        CharactersDrawn = true;
    }

    public IGpuContext Context => ctx;
    public PostOptions Options => options;
    public GpuTexture? Target { get; set; }
    public PassTargets SceneTargets { get; private set; } = null!;
    public PassTargets SceneTargetsForParticles { get; private set; } = null!;
    public GpuTexture SceneColour => sceneColour ?? throw new InvalidOperationException("no Metal post frame is open");
    public GpuTexture SceneDepth => ctx.Depth;

    public int RenderWidth => sceneColour?.Desc.Width ?? 0;
    public int RenderHeight => sceneColour?.Desc.Height ?? 0;
    public Vector2 JitterPixels { get; private set; }
    public bool Temporal => temporal();
    public bool DepthSlices => false;
    public bool InstantAdaptation { get; set; } = true;
    public int GiResolve { get; set; }
    public (float Min, float Max)? AutoExposure { get; set; }
    public float? WaterHeight { get; set; }
    public bool CharactersDrawn { get; set; }
    public float HeatHazeAmount
    {
        get => composite.HeatHazeAmount;
        set => composite.HeatHazeAmount = value;
    }
    public double HeatHazeHours
    {
        get => composite.HeatHazeHours;
        set => composite.HeatHazeHours = value;
    }
    public float HeatHazeFarClip
    {
        get => composite.HeatHazeFarClip;
        set => composite.HeatHazeFarClip = value;
    }
    public bool LowResParticles => false;

    public void Begin(int width, int height)
    {
        displayWidth = Math.Max(width, 1);
        displayHeight = Math.Max(height, 1);
        int sceneWidth = Math.Max(ctx.SceneWidth, 1), sceneHeight = Math.Max(ctx.SceneHeight, 1);
        if (sceneColour is null || sceneColour.Desc.Width != sceneWidth || sceneColour.Desc.Height != sceneHeight)
        {
            sceneColour?.Dispose();
            sceneColour = ctx.CreateTexture(new TextureDesc(InternalFormat.Rgba16f, sceneWidth, sceneHeight,
                Use: TextureUse.ColourTarget | TextureUse.Sampled, Name: "scene colour"));
        }
        // The scene targets are Metal's own depth and albedo (the GI resolve is off, but the lit programs always write albedo).
        SceneTargets = PassTargets.Of(sceneColour, ctx.Depth, ctx.Albedo);
        // The full-size particles draw after the GI resolve and were built without the albedo attachment on Metal.
        SceneTargetsForParticles = PassTargets.Of(sceneColour, ctx.Depth);
        Target = ctx.Backbuffer;
        frame++;
        JitterPixels = jitter(frame, sceneWidth, displayWidth);
        // As the old Metal loop: mip bias only while a temporal reconstruction runs; spatial and native sample at full detail.
        ctx.LodBias = Temporal ? MathF.Log2(sceneWidth / (float)displayWidth) - 0.5f : 0f;
        AutoExposure = null;
        WaterHeight = null;
        CharactersDrawn = true;
    }

    public void SetCamera(Vector3 eye, Matrix4x4 view, float fieldOfView, float aspect)
    {
        this.eye = eye;
        this.view = view;
        fov = fieldOfView;
        this.aspect = aspect;
    }

    public void SetNearSlice(float near, float far, float fieldOfView, float aspect)
    {
        nearPlane = near;
        farPlane = far;
        fov = fieldOfView;
        this.aspect = aspect;
    }

    public void BeginFarSlice(float near, float far)
    {
        nearPlane = near;
        farPlane = far;
    }

    public void BeginNearSlice(float near, float far)
    {
        nearPlane = near;
        farPlane = far;
    }

    public PassTargets WithShadingRate(PassTargets targets) => targets;

    public OcclusionView OcclusionFor(Vector3 eye) => default;

    public void RunGiResolve() { }

    public void RunLightShafts(float darkening, float far, float groundY, float airByTime = 1)
    {
        if (sceneColour is null) return;
        var cmd = (MetalCommandList)ctx.BeginNative("post shafts");
        composite.RunLightShafts(cmd, sceneColour, ctx.Depth, Inputs, Options, darkening, far, groundY, airByTime, Temporal);
        ctx.EndNative(cmd);
    }

    public void RunFogVolumes(bool volumes) => volumesPending = volumes;

    public void BuildShadingRate(Vector3 eye) { }

    public void ApplySsao()
    {
        if (sceneColour is null || (!volumesPending && !Options.Ssao)) return;
        var cmd = (MetalCommandList)ctx.BeginNative("post scene effects");
        composite.ApplyScene(cmd, sceneColour, ctx.Depth, Inputs, volumesPending, Options.Ssao);
        ctx.EndNative(cmd);
        volumesPending = false;
    }

    public float ParticlePixelsPerUnit(float projectionM22) => 0f;
    public PassTargets ParticleTargets(int divisor) => SceneTargets;
    public void BeginParticlesLow(int divisor) { }
    public void CompositeParticlesLow(int divisor) { }
    public void MarkParticleDraws() { }
    public void MarkParticles() { }

    public void End()
    {
        if (sceneColour is null) throw new InvalidOperationException("no Metal post frame is open");
        var target = Target ?? ctx.Backbuffer;
        GpuTexture resolved = sceneColour;
        if (!noTaa())
        {
            var taaCmd = (MetalCommandList)ctx.BeginNative("post taa");
            resolved = taa.Draw(taaCmd, sceneColour, ctx.Depth, eye, view, fov, aspect, nearPlane, farPlane, JitterPixels,
                displayWidth, displayHeight, DeltaSeconds());
            ctx.EndNative(taaCmd);
        }
        var cmd = (MetalCommandList)ctx.BeginNative("post composite");
        composite.Draw(cmd, resolved, ctx.Depth, target, Options, AutoExposure ?? (0f, 0f), Inputs, temporal: !noTaa());
        ctx.EndNative(cmd);
    }

    PostComposite.SceneInputs Inputs => new(eye, view, fov, aspect, nearPlane, farPlane, CharactersDrawn, WaterHeight);

    float DeltaSeconds()
    {
        double now = adaptationClock.Elapsed.TotalSeconds;
        float dt = (float)(now - lastAdaptation);
        lastAdaptation = now;
        return dt;
    }

    public void Dispose() => sceneColour?.Dispose();

    Vector3 eye;
    Matrix4x4 view;
    float fov = 50 * MathF.PI / 180, aspect = 1, nearPlane = 1, farPlane = 1000;
    bool volumesPending;
}
#endif
