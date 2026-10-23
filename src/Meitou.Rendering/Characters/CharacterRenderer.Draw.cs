using InternalFormat = Meitou.Rendering.Gpu.InternalFormat;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Data;
using Meitou.Data.Ogre;
using Meitou.Rendering.Gpu;
using Vk = Silk.NET.Vulkan;

namespace Meitou.Rendering.Characters;

internal sealed unsafe partial class CharacterRenderer
{
    // ------------------------------------------------------------------ drawing

    sealed class Batch
    {
        public required GpuObjectPart Part;
        public int Level;
        public bool DoubleSided;
        public CharInstance[] Data = new CharInstance[16];
        public int Count, Offset;

        public void Add(in CharInstance i)
        {
            if (Count == Data.Length) Array.Resize(ref Data, Count * 2);
            Data[Count++] = i;
        }
    }

    readonly Dictionary<(GpuObjectPart, int, bool), Batch> batchMap = [];
    readonly List<Batch> active = [];
    Transient instances;
    bool depthPass, motionPass;
    float shadowLodFloor, shadowMinRadius;

    /// <summary>The fog cull (set once by the world frame; the colour and motion passes use it, not the shadow cascades): characters wholly hidden by the fog the eye is in are skipped.</summary>
    internal FogVolumes? FogCull { get; set; }

    /// <summary>Room masks used to give indoor characters the same indirect-light gain as their surroundings.</summary>
    internal WorldObjectRenderer? InteriorObjects { get; set; }

    /// <summary>Draws the characters seen from <paramref name="eye"/> through <paramref name="frustum"/> into the open pass.</summary>
    public void Draw(Matrix4x4 viewProjection, Vector3 eye, Vector4[] frustum, Vector3 light, Vector3 fogColour, float fogDistance) =>
        DrawView(new ViewConstants { ViewProjection = viewProjection, Eye = eye, LightDir = light, FogColour = fogColour, FogDistance = fogDistance }, eye, frustum);

    /// <summary>
    /// Draws the characters' depth for a shadow cascade (<see cref="CharacterShaders.DepthFragment"/>); levels chosen from <paramref name="lodEye"/>, never finer than
    /// <paramref name="lodFloor"/> (<see cref="ShadowLod.ObjectLodFloor"/> of a far cascade's texel; 0: the distance rule alone), as the objects. A part whose bounding
    /// radius is under <paramref name="minRadius"/> (a far cascade: half a texel, so it would cover less than one; 0: none) casts nothing there: hair, beards, small
    /// attachments and weapons leave the far maps, as the foliage's small meshes do (<c>ShadowPass.MinFoliageCasterTexels</c>).
    /// </summary>
    public void DrawDepth(Matrix4x4 worldToClip, Vector3 lodEye, Vector4[] frustum, float lodFloor = 0, float minRadius = 0)
    {
        (depthPass, shadowLodFloor, shadowMinRadius) = (true, lodFloor, minRadius);
        try { DrawView(new ViewConstants { ViewProjection = worldToClip, Eye = lodEye }, lodEye, frustum); }
        finally { (depthPass, shadowLodFloor, shadowMinRadius) = (false, 0, 0); }
    }

    void DrawView(in ViewConstants worldView, Vector3 eye, Vector4[] frustum)
    {
        var cpu = Stopwatch.StartNew();
        // Drawn relative to the view's eye (the vertex program adds it back for the shading): skinned in world floats, an animated vertex far from
        // the origin (the Hub, tens of thousands of units out) rounds to a different step every frame and the character visibly jiggles.
        var origin = worldView.Eye;
        var view = worldView;
        view.ViewProjection = FromOrigin(origin, worldView.ViewProjection);
        view.PreviousViewProjection = FromOrigin(origin, worldView.PreviousViewProjection);
        DrawnCharacters = DrawnParts = DrawCalls = 0;
        DrawnTriangles = 0;
        Array.Clear(LevelParts);
        Array.Clear(LevelTriangles);
        foreach (var b in batchMap.Values) b.Count = 0;
        active.Clear();

        // 1. Cull, choose each part's level and group the instances by (mesh part, level, sidedness).
        // A portrait (portraitLive) draws that one character, unculled.
        for (int i = portraitLive >= 0 ? portraitLive : 0, end = portraitLive >= 0 ? portraitLive + 1 : liveCount; i < end; i++)
        {
            ref var l = ref live[i];
            if (portraitLive < 0 && (l.PortraitOnly || !FrustumTests.SphereVisible(frustum, l.Centre, l.Radius))) continue;
            if (!depthPass && portraitLive < 0 && FogCull?.Hidden(l.Centre, l.Radius, FogVolumes.CullKind.Characters) == true) continue;
            DrawnCharacters++;
            bool interior = portraitLive < 0 && l.Interior;
            var world = Relative(l.World, origin);
            float worldScale = world.ScaleLength();
            foreach (var part in l.Asset.Parts)
            {
                if (!part.ShowsWith(l.WeaponDrawn, l.RangedDrawn, l.Shots, l.BackDrawn)) continue;
                Matrix4x4 model = world;
                if (!part.Shared) model = attach[l.AttachBase + part.AttachIndex] * world;
                float modelScale = part.Shared ? worldScale : model.ScaleLength();
                var mesh = part.Mesh;
                if (depthPass && part.BoundsRadius * modelScale < shadowMinRadius) continue;
                int level;
                if (!GeneratedLod)
                {
                    // Faithful: the file's own levels by the original's distance_sphere value (its stored radius, bias 1), the shadow passes as the picture
                    // (docs/characters.md "LOD of character meshes").
                    float own = MeshLod.Value(eye - origin, Vector3.Transform(part.BoundsCentre, model), part.FileRadius * modelScale);
                    level = part.FileDistances is { } fileLevels && MeshLod.Select(fileLevels, own) is int f && f > 0 ? part.FileFirst + f - 1 : 0;
                }
                else
                {
                    // The model is relative to the view's eye (above), so the LOD eye is too: with the world eye every part far from the origin took its last level.
                    float value = MeshLod.Value(eye - origin, Vector3.Transform(part.BoundsCentre, model), part.BoundsRadius * modelScale, depthPass ? LodBias * ShadowLodBias : LodBias);
                    // In a shadow map hair is a silhouette, so it takes the rule's own distances there (its levels start CharacterContent.HairLodScale times farther for the picture).
                    if (depthPass) value = Math.Max(part.Material.Shading == CharacterShaders.ShadeHair && !l.Asset.Animal ? value * CharacterContent.HairLodScale : value, shadowLodFloor);
                    level = MeshLod.Select(mesh.Distances, value);
                    if (depthPass && part.ShadowDistances is { } shadowLevels && MeshLod.Select(shadowLevels, value) is int k && k > 0) level = part.ShadowFirst + k - 1;   // the body's shadow-only levels
                }
                bool twoSided = part.Material.DoubleSided;
                uint hidden = (part.IsBody && l.Asset.Appearance is { } look ? (uint)look.HiddenLimbs << 12 : 0u) | (uint)part.HideSide << 16;
                var instance = new CharInstance { Model = model, BoneBase = (uint)l.BoneBase, Material = (uint)part.MaterialSlot, Flags = (interior ? 1u : 0u) | hidden, MorphBase = part.Morphed ? l.Asset.MorphBase : 0 };
                if (motionPass)
                {
                    var previousWorld = Relative(l.State.PreviousWorld, origin);
                    instance.PreviousModel = part.Shared ? previousWorld : l.State.PreviousAttach[part.AttachIndex] * previousWorld;
                }
                DrawnParts++;
                foreach (var gp in mesh.Parts)
                {
                    int lv = Math.Min(level, gp.Count.Length - 1);
                    while (lv > 0 && gp.Count[lv] == 0) lv--;
                    if (gp.Count[lv] == 0) continue;
                    var key = (gp, lv, twoSided);
                    if (!batchMap.TryGetValue(key, out var batch)) batchMap[key] = batch = new Batch { Part = gp, Level = lv, DoubleSided = twoSided };
                    if (batch.Count == 0) active.Add(batch);
                    batch.Add(in instance);
                }
            }
        }
        if (active.Count == 0) { LastDrawCpuMs = cpu.Elapsed.TotalMilliseconds; return; }
        // Single-sided first, then the double-sided (the cull mode changes once).
        active.Sort((a, b) => a.DoubleSided.CompareTo(b.DoubleSided));

        // 2. The instances: each batch's contiguous in this view's constants (the draws reach a batch by firstInstance).
        int total = 0;
        foreach (var b in active) { b.Offset = total; total += b.Count; }
        instances = AllocateConstants((ulong)total * CharInstance.Size, 16);
        var target = new Span<byte>(instances.Pointer, total * CharInstance.Size);
        foreach (var b in active)
            MemoryMarshal.AsBytes(b.Data.AsSpan(0, b.Count)).CopyTo(target[(b.Offset * CharInstance.Size)..]);

        // 3. One draw per batch.
        var prog = motionPass ? motionProg : depthPass ? depthProg : colourProg;
        string label = motionPass ? "characters motion" : depthPass ? "characters depth" : "characters";
        var targets = Gpu.CurrentTargets();
        var state = Gpu.CurrentState() with { Cull = CullMode.None };
        // The colour pass writes alpha 1 (the scene host masks it off for everything else): the SSAO character mask (PostProcess).
        if (!depthPass && !motionPass) state = state with { ColourMask = DrawState.Rgba };
        var job = drawJobs.Rent();
        (job.Owner, job.Targets, job.State, job.Program, job.Count) = (this, targets, state, prog.P, 0);
        job.Frame = nativeFrame.Prepare(in view, [bones.Binding, materials.Binding, content.Morphs.Binding(Gpu.Frame), previousBones.Binding]);
        job.Cull = depthPass ? CullMode.None : CullMode.Back;
        for (int a = 0; a < 9; a++) job.Rows[a] = new GpuBufferBinding(instances.Handle, instances.Offset + (ulong)(16 * a));
        job.Push = new CharacterPush { DepthIndex = motionPass ? motionDepth : 0, Fill = new(Fill, 0), SkinLut = SkinShading ? skinScattering.Index : 0 };
        job.RowCount = motionPass ? 9 : 5;
        if (job.Draws.Length < active.Count) job.Draws = new DrawJob.Draw[Math.Max(active.Count, job.Draws.Length * 2)];
        int n = 0;
        foreach (var b in active)
        {
            var part = b.Part;
            ref var native = ref (motionPass ? ref MotionNative(part) : ref depthPass ? ref part.DepthNative : ref part.ColourNative);
            Current(ref native, part, prog);
            job.Draws[n++] = new DrawJob.Draw
            {
                Pipeline = Gpu.Pipelines.Get(state.Pipeline(prog.P, native.Layout!, PrimitiveType.Triangles, targets.Formats, label)),
                Vertices = native.Vertices, Elements = new GpuBufferBinding(native.Elements.Buffer, native.Elements.Offset + (ulong)part.Offset[b.Level] * 4),
                IndexCount = (uint)part.Count[b.Level], Instances = (uint)b.Count, FirstInstance = (uint)b.Offset, TwoSided = b.DoubleSided,
            };
            DrawCalls++;
            DrawnTriangles += (long)part.Count[b.Level] / 3 * b.Count;
            LevelParts[Math.Min(b.Level, 7)] += b.Count;
            LevelTriangles[Math.Min(b.Level, 7)] += (long)part.Count[b.Level] / 3 * b.Count;
        }
        job.Count = n;
        var timer = depthPass ? depthTimer : colourTimer;
        if (portraitLive < 0) timer.Begin();
        Gpu.Record(label, job);
        if (portraitLive < 0) timer.End();
        LastDrawCpuMs = cpu.Elapsed.TotalMilliseconds;
        if (!depthPass && portraitLive < 0) drawCpu.Add(LastDrawCpuMs);
    }

    /// <summary><paramref name="world"/> with <paramref name="origin"/> taken off its translation.</summary>
    static Matrix4x4 Relative(Matrix4x4 world, Vector3 origin)
    {
        world.Translation -= origin;
        return world;
    }

    /// <summary><paramref name="m"/> (world to clip) for positions relative to <paramref name="origin"/>: translation(origin) × m, in double so the
    /// origin's large coordinates cancel exactly.</summary>
    static Matrix4x4 FromOrigin(Vector3 origin, in Matrix4x4 m)
    {
        double x = origin.X, y = origin.Y, z = origin.Z;
        var r = m;
        r.M41 = (float)(x * m.M11 + y * m.M21 + z * m.M31 + m.M41);
        r.M42 = (float)(x * m.M12 + y * m.M22 + z * m.M32 + m.M42);
        r.M43 = (float)(x * m.M13 + y * m.M23 + z * m.M33 + m.M43);
        r.M44 = (float)(x * m.M14 + y * m.M24 + z * m.M34 + m.M44);
        return r;
    }

    readonly Dictionary<GpuObjectPart, MotionHolder> motionNatives = [];
    sealed class MotionHolder { public ObjectNativeMesh Native; }
    ref ObjectNativeMesh MotionNative(GpuObjectPart part)
    {
        if (!motionNatives.TryGetValue(part, out var h)) motionNatives[part] = h = new MotionHolder();
        return ref h.Native;
    }

    /// <summary>A part's native state for <paramref name="p"/>, made on first use: the vertex layout (the per-instance rows at 7 to 10 and the data at 11 are bound once per segment).</summary>
    static void Current(ref ObjectNativeMesh n, GpuObjectPart part, ReflectedProgram p)
    {
        if (n.Layout is not null) return;
        Span<LegacyProgram.Attribute?> attributes = stackalloc LegacyProgram.Attribute?[16];
        part.Attributes.AsSpan().CopyTo(attributes);
        for (int a = 0; a < 4; a++)
            attributes[CharacterShaders.InstanceLocation + a] = new LegacyProgram.Attribute(default, InternalFormat.Rgba32f, CharInstance.Size, true);
        attributes[CharacterShaders.DataLocation] = new LegacyProgram.Attribute(default, InternalFormat.Rgba32ui, CharInstance.Size, true);
        for (int a = 0; a < 4; a++)
            attributes[CharacterShaders.PreviousLocation + a] = new LegacyProgram.Attribute(default, InternalFormat.Rgba32f, CharInstance.Size, true);
        n.Layout = p.Layout(attributes);
        n.Vertices = p.Buffers(attributes, p.Own);
        n.Elements = new GpuBufferBinding(part.Indices.Handle, 0, part.Indices.Size);
        n.SegA = n.SegB = 0;
        n.PipeA = n.PipeB = null;
    }

    readonly JobPool<DrawJob> drawJobs = new();

    /// <summary>One characters segment as prepared (<see cref="DrawView"/>): recorded on any thread.</summary>
    sealed class DrawJob : RecordJob
    {
        public struct Draw
        {
            public GpuPipeline Pipeline;
            public GpuBufferBinding[] Vertices;
            public GpuBufferBinding Elements;
            public uint IndexCount, Instances, FirstInstance;
            public bool TwoSided;
        }

        public CharacterRenderer Owner = null!;
        public PassTargets Targets = null!;
        public DrawState State = null!;
        public GpuProgram Program = default;
        public FrameBinding Frame;
        public CullMode Cull;
        public readonly GpuBufferBinding[] Rows = new GpuBufferBinding[9];
        public int RowCount;
        public CharacterPush Push;
        public Draw[] Draws = new Draw[64];
        public int Count;
        public override int Size => Count;

        public override void Record(IGpuCommandList cmd)
        {
            var (targets, state) = (Targets, State);
            cmd.SetViewport(targets.Viewport);
            cmd.SetScissor(targets.Scissor);
            cmd.SetDepth(state.DepthTest, state.DepthWrite, state.Compare);
            cmd.SetDepthBias(state.BiasEnable, state.BiasConstant, state.BiasSlope);
            NativeFrame.Record(cmd, Program, in Frame);
            cmd.BindVertexBuffers(CharacterShaders.InstanceLocation, Rows.AsSpan(0, RowCount));
            var push = Push;
            cmd.PushConstants(Program, GpuStage.Vertex | GpuStage.Fragment, in push);
            bool? twoSided = null;
            for (int i = 0; i < Count; i++)
            {
                ref readonly var d = ref Draws[i];
                if (twoSided != d.TwoSided)
                {
                    cmd.SetRaster(d.TwoSided ? CullMode.None : Cull, state.Front);
                    twoSided = d.TwoSided;
                }
                cmd.BindPipeline(d.Pipeline);
                cmd.BindVertexBuffers(0, d.Vertices);
                cmd.BindIndexBuffer(d.Elements, IndexKind.U32);
                cmd.DrawIndexed(d.IndexCount, d.Instances, 0, 0, d.FirstInstance);
            }
        }

        public override void Release()
        {
            Array.Clear(Draws, 0, Count);
            Count = 0;
            Owner.drawJobs.Return(this);
        }
    }
}

static class MatrixExtensions
{
    /// <summary>The largest axis scale of a transform's upper 3x3 (a bounding radius through it).</summary>
    public static float ScaleLength(this in Matrix4x4 m) =>
        MathF.Sqrt(Math.Max(new Vector3(m.M11, m.M12, m.M13).LengthSquared(), Math.Max(new Vector3(m.M21, m.M22, m.M23).LengthSquared(), new Vector3(m.M31, m.M32, m.M33).LengthSquared())));
}
