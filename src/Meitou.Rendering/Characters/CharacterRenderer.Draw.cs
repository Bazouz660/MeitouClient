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

    internal sealed class Batch
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

        // 1. Cull, choose each part's level and group the instances by (mesh part, level, sidedness). The world's views from the update's eye take the parts it
        // placed and gather in parallel; a portrait (portraitLive, that one character, unculled) or another eye places its own.
        bool placed = portraitLive < 0 && partStamp == updateStamp && origin == partOrigin && eye == origin && (partMotion || !motionPass);
        int chunkCount = placed ? GatherPlaced(frustum) : 0;
        for (int i = portraitLive >= 0 ? portraitLive : 0, end = placed ? 0 : portraitLive >= 0 ? portraitLive + 1 : liveCount; i < end; i++)
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
        instances = Gpu.Frame.Constants.Allocate((ulong)total * CharInstance.Size, 16);
        if (placed) ScatterPlaced(chunkCount, (nint)instances.Pointer);
        else
        {
            var target = new Span<byte>(instances.Pointer, total * CharInstance.Size);
            foreach (var b in active)
                MemoryMarshal.AsBytes(b.Data.AsSpan(0, b.Count)).CopyTo(target[(b.Offset * CharInstance.Size)..]);
        }

        // 3. One draw per batch.
        var prog = motionPass ? motionProg : depthPass ? depthProg : colourProg;
        string label = motionPass ? "characters motion" : depthPass ? "characters depth" : "characters";
        var targets = Gpu.CurrentTargets();
        var state = Gpu.CurrentState() with { Cull = Vk.CullModeFlags.None };
        // The colour pass writes alpha 1 (the scene host masks it off for everything else): the SSAO character mask (PostProcess).
        if (!depthPass && !motionPass) state = state with { ColourMask = DrawState.Rgba };
        var job = drawJobs.Rent();
        (job.Owner, job.Targets, job.State, job.Layout, job.Count) = (this, targets, state, prog.P.Layout, 0);
        job.Frame = nativeFrame.Prepare(in view, [bones.Binding, materials.Binding, content.Morphs.Binding(Gpu.Frame), previousBones.Binding]);
        job.Cull = depthPass ? Vk.CullModeFlags.None : Vk.CullModeFlags.BackBit;
        for (int a = 0; a < 9; a++) job.Rows[a] = new BufferBinding(instances.Handle, instances.Offset + (ulong)(16 * a));
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
                Pipeline = Gpu.Pipelines.Get(state.Pipeline(prog.P, native.Layout!, Vk.PrimitiveTopology.TriangleList, targets.Formats, label)),
                Vertices = native.Vertices, Elements = new BufferBinding(native.Elements.Buffer, native.Elements.Offset + (ulong)part.Offset[b.Level] * 4),
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

    // ------------------------------------------------------------------ parts placed once a frame

    /// <summary>
    /// One part of a character in range as <see cref="Update"/> placed it, relative to its eye (<see cref="partOrigin"/>): the colour, motion and shadow views are all
    /// drawn from that eye, so they share the matrices and the distance the levels are chosen by.
    /// </summary>
    struct PartFrame
    {
        public Matrix4x4 Model, PreviousModel;
        /// <summary>The model's largest axis scale.</summary>
        public float Scale;
        /// <summary>The LOD values at bias 1 (<see cref="MeshLod.Value"/>): by the bounds radius (the generated levels) and by the file's radius (Faithful).</summary>
        public float Lod, FileLod;
        /// <summary>Shown with the character's weapon state (<see cref="AssetPart.ShowsWith"/>); the rest is not set when not.</summary>
        public bool Shows;
    }

    PartFrame[] partFrames = new PartFrame[1024];
    long partStamp = -1;
    Vector3 partOrigin;
    bool partMotion;

    /// <summary>Places <paramref name="l"/>'s parts relative to <paramref name="origin"/> (and last frame's too, for <paramref name="motion"/>). Any thread: writes its own entries only.</summary>
    void PlaceParts(in Live l, Vector3 origin, bool motion)
    {
        var world = Relative(l.World, origin);
        float worldScale = world.ScaleLength();
        var previousWorld = motion ? Relative(l.State.PreviousWorld, origin) : default;
        var parts = l.Asset.Parts;
        for (int p = 0; p < parts.Count; p++)
        {
            var part = parts[p];
            ref var f = ref partFrames[l.PartBase + p];
            f.Shows = part.ShowsWith(l.WeaponDrawn, l.RangedDrawn, l.Shots, l.BackDrawn);
            if (!f.Shows) continue;
            f.Model = part.Shared ? world : attach[l.AttachBase + part.AttachIndex] * world;
            f.Scale = part.Shared ? worldScale : f.Model.ScaleLength();
            var centre = Vector3.Transform(part.BoundsCentre, f.Model);
            f.Lod = MeshLod.Value(Vector3.Zero, centre, part.BoundsRadius * f.Scale);
            f.FileLod = MeshLod.Value(Vector3.Zero, centre, part.FileRadius * f.Scale);
            if (motion) f.PreviousModel = part.Shared ? previousWorld : l.State.PreviousAttach[part.AttachIndex] * previousWorld;
        }
    }

    /// <summary>A batch's instance as one chunk of characters gathered it: the batch, its place there (set in character order) and the instance.</summary>
    struct Gathered
    {
        public Batch Batch;
        public int Slot;
        public CharInstance Instance;
    }

    /// <summary>What one chunk of <see cref="ChunkCharacters"/> characters adds to a view, in order.</summary>
    sealed class Chunk
    {
        public Gathered[] Items = new Gathered[256];
        public int Count, Characters, Parts;

        public void Add(Batch batch, in CharInstance instance)
        {
            if (Count == Items.Length) Array.Resize(ref Items, Count * 2);
            ref var g = ref Items[Count++];
            (g.Batch, g.Instance) = (batch, instance);
        }
    }

    const int ChunkCharacters = 16;
    /// <summary>The mesh levels a part's batch cache holds (<see cref="AssetPart.Batches"/>); deeper ones are looked up.</summary>
    const int CachedLevels = 8;
    readonly List<Chunk> chunks = [];

    /// <summary>
    /// The view's instances from the placed parts (<see cref="PartFrame"/>): chunks of characters culled and their parts' levels chosen in parallel, then each instance
    /// given its place in its batch in character order (the order one thread adds them in). Returns the number of chunks.
    /// </summary>
    int GatherPlaced(Vector4[] frustum)
    {
        int count = (liveCount + ChunkCharacters - 1) / ChunkCharacters;
        while (chunks.Count < count) chunks.Add(new Chunk());
        Parallel.For(0, count, c => GatherChunk(chunks[c], c * ChunkCharacters, Math.Min(liveCount, (c + 1) * ChunkCharacters), frustum));
        for (int c = 0; c < count; c++)
        {
            var chunk = chunks[c];
            DrawnCharacters += chunk.Characters;
            DrawnParts += chunk.Parts;
            for (int k = 0; k < chunk.Count; k++)
            {
                ref var g = ref chunk.Items[k];
                if (g.Batch.Count == 0) active.Add(g.Batch);
                g.Slot = g.Batch.Count++;
            }
        }
        return count;
    }

    void GatherChunk(Chunk chunk, int from, int to, Vector4[] frustum)
    {
        chunk.Count = chunk.Characters = chunk.Parts = 0;
        float bias = depthPass ? LodBias * ShadowLodBias : LodBias;
        for (int i = from; i < to; i++)
        {
            ref var l = ref live[i];
            if (l.PortraitOnly || !FrustumTests.SphereVisible(frustum, l.Centre, l.Radius)) continue;
            if (!depthPass && FogCull?.Hidden(l.Centre, l.Radius, FogVolumes.CullKind.Characters) == true) continue;
            chunk.Characters++;
            var parts = l.Asset.Parts;
            for (int p = 0; p < parts.Count; p++)
            {
                ref readonly var f = ref partFrames[l.PartBase + p];
                if (!f.Shows) continue;
                var part = parts[p];
                if (depthPass && part.BoundsRadius * f.Scale < shadowMinRadius) continue;
                var mesh = part.Mesh;
                int level;
                if (!GeneratedLod) level = part.FileDistances is { } fileLevels && MeshLod.Select(fileLevels, f.FileLod) is int fl && fl > 0 ? part.FileFirst + fl - 1 : 0;
                else
                {
                    float value = f.Lod * bias;
                    if (depthPass) value = Math.Max(part.Material.Shading == CharacterShaders.ShadeHair && !l.Asset.Animal ? value * CharacterContent.HairLodScale : value, shadowLodFloor);
                    level = MeshLod.Select(mesh.Distances, value);
                    if (depthPass && part.ShadowDistances is { } shadowLevels && MeshLod.Select(shadowLevels, value) is int k && k > 0) level = part.ShadowFirst + k - 1;
                }
                bool twoSided = part.Material.DoubleSided;
                uint hidden = (part.IsBody && l.Asset.Appearance is { } look ? (uint)look.HiddenLimbs << 12 : 0u) | (uint)part.HideSide << 16;
                var instance = new CharInstance
                {
                    Model = f.Model, BoneBase = (uint)l.BoneBase, Material = (uint)part.MaterialSlot, Flags = (l.Interior ? 1u : 0u) | hidden,
                    MorphBase = part.Morphed ? l.Asset.MorphBase : 0, PreviousModel = motionPass ? f.PreviousModel : default,
                };
                chunk.Parts++;
                var gps = mesh.Parts;
                for (int g = 0; g < gps.Count; g++)
                {
                    var gp = gps[g];
                    int lv = Math.Min(level, gp.Count.Length - 1);
                    while (lv > 0 && gp.Count[lv] == 0) lv--;
                    if (gp.Count[lv] == 0) continue;
                    chunk.Add(BatchOf(part, g, gp, lv, twoSided), in instance);
                }
            }
        }
    }

    /// <summary>The batch of <paramref name="part"/>'s mesh part <paramref name="g"/> at level <paramref name="lv"/>: from the part's cache, else made or found under the lock (any thread).</summary>
    Batch BatchOf(AssetPart part, int g, GpuObjectPart gp, int lv, bool twoSided)
    {
        int slot = (g * CachedLevels + lv) * 2 + (twoSided ? 1 : 0);
        if (lv < CachedLevels && part.Batches is { } cached && slot < cached.Length && cached[slot] is { } known) return known;
        lock (batchMap)
        {
            var key = (gp, lv, twoSided);
            if (!batchMap.TryGetValue(key, out var batch)) batchMap[key] = batch = new Batch { Part = gp, Level = lv, DoubleSided = twoSided };
            if (lv < CachedLevels)
            {
                var cache = part.Batches;
                if (cache is null || slot >= cache.Length)
                {
                    var grown = new Batch?[part.Mesh.Parts.Count * CachedLevels * 2];
                    cache?.CopyTo(grown, 0);
                    part.Batches = cache = grown;
                }
                cache[slot] = batch;
            }
            return batch;
        }
    }

    /// <summary>Writes the gathered instances into the view's instance rows at <paramref name="rows"/>, each at its batch's offset plus its place (in parallel).</summary>
    void ScatterPlaced(int count, nint rows) => Parallel.For(0, count, c =>
    {
        var chunk = chunks[c];
        for (int k = 0; k < chunk.Count; k++)
        {
            ref var g = ref chunk.Items[k];
            *(CharInstance*)((byte*)rows + (long)(g.Batch.Offset + g.Slot) * CharInstance.Size) = g.Instance;
        }
    });

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
            attributes[CharacterShaders.InstanceLocation + a] = new LegacyProgram.Attribute(default, Vk.Format.R32G32B32A32Sfloat, CharInstance.Size, true);
        attributes[CharacterShaders.DataLocation] = new LegacyProgram.Attribute(default, Vk.Format.R32G32B32A32Uint, CharInstance.Size, true);
        for (int a = 0; a < 4; a++)
            attributes[CharacterShaders.PreviousLocation + a] = new LegacyProgram.Attribute(default, Vk.Format.R32G32B32A32Sfloat, CharInstance.Size, true);
        n.Layout = p.Layout(attributes);
        n.Vertices = p.Buffers(attributes, p.Own);
        n.Elements = new BufferBinding(part.Indices.Handle, 0, part.Indices.Size);
        n.SegA = n.SegB = 0;
        n.PipeA = n.PipeB = null;
    }

    readonly JobPool<DrawJob> drawJobs = new();

    /// <summary>One characters segment as prepared (<see cref="DrawView"/>): recorded on any thread.</summary>
    sealed class DrawJob : RecordJob
    {
        public struct Draw
        {
            public GraphicsPipeline Pipeline;
            public BufferBinding[] Vertices;
            public BufferBinding Elements;
            public uint IndexCount, Instances, FirstInstance;
            public bool TwoSided;
        }

        public CharacterRenderer Owner = null!;
        public PassTargets Targets = null!;
        public DrawState State = null!;
        public Vk.PipelineLayout Layout;
        public FrameBinding Frame;
        public Vk.CullModeFlags Cull;
        public readonly BufferBinding[] Rows = new BufferBinding[9];
        public int RowCount;
        public CharacterPush Push;
        public Draw[] Draws = new Draw[64];
        public int Count;
        public override int Size => Count;

        public override void Record(CommandList cmd)
        {
            var (targets, state, layout) = (Targets, State, Layout);
            cmd.SetViewport(targets.Viewport);
            cmd.SetScissor(targets.Scissor);
            cmd.SetDepth(state.DepthTest, state.DepthWrite, state.Compare);
            cmd.SetDepthBias(state.BiasEnable, state.BiasConstant, state.BiasSlope);
            NativeFrame.Record(cmd, layout, in Frame);
            cmd.BindVertexBuffers(CharacterShaders.InstanceLocation, Rows.AsSpan(0, RowCount));
            var push = Push;
            cmd.PushConstants(layout, Vk.ShaderStageFlags.VertexBit | Vk.ShaderStageFlags.FragmentBit, in push);
            bool? twoSided = null;
            for (int i = 0; i < Count; i++)
            {
                ref readonly var d = ref Draws[i];
                if (twoSided != d.TwoSided)
                {
                    cmd.SetRaster(d.TwoSided ? Vk.CullModeFlags.None : Cull, state.Front);
                    twoSided = d.TwoSided;
                }
                cmd.BindPipeline(d.Pipeline);
                cmd.BindVertexBuffers(0, d.Vertices);
                cmd.BindIndexBuffer(d.Elements, Vk.IndexType.Uint32);
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
