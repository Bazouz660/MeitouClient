using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Data.Ogre;
using Meitou.Data.Textures;
using Meitou.Rendering.Gpu;
using Meitou.Rendering.Gpu.Shaders;

namespace Meitou.Rendering;

/// <summary>
/// One loose mesh in the world: its file, its size and where it is (rotation and translation, the mesh's middle at the translation); <paramref name="Stretch"/> lengthens
/// it along its own z (a bolt in flight).
/// </summary>
public readonly record struct DebrisPiece(string Mesh, float Scale, Quaternion Rotation, Vector3 Position, float Stretch = 1);

/// <summary>
/// Loose meshes thrown about by the host (luckyren Mode's bones and limbs, docs/game/luckyren.md): each a static mesh, centred on its bounds, textured with
/// its first resolved diffuse map and lit as the world's objects (<c>kenshiLight</c> under the game sky, the plain light otherwise, then the haze). The same
/// fragment runs on Vulkan and Metal: every atmosphere/shadow texture and the debris diffuse map come through the bindless table, so Metal's 16 direct
/// fragment samplers are no longer a constraint. Drawn in the scene pass of the near slice before the characters (colour only, leaving the alpha to them); they
/// cast no shadow. A mesh is loaded the first time its <see cref="Extent"/> is asked for.
/// </summary>
public sealed unsafe class DebrisRenderer : IDisposable
{
    const string Vertex = """
        #version 330 core
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec3 aNormal;
        layout(location = 2) in vec2 aUv;
        uniform mat4 uViewProjection;   // from the camera-relative world
        uniform mat4 uModel;            // to the camera-relative world
        uniform vec3 uEye;
        out vec3 vWorld;
        out vec3 vNormal;
        out vec2 vUv;
        void main()
        {
            vec4 p = uModel * vec4(aPos, 1.0);
            vWorld = p.xyz + uEye;
            vNormal = mat3(uModel) * aNormal;
            vUv = aUv;
            gl_Position = uViewProjection * p;
        }
        """;

    static readonly string Fragment = "#version 330 core\n" + AtmosphereShaders.Functions + """
        in vec3 vWorld;
        in vec3 vNormal;
        in vec2 vUv;
        out vec4 fragColour;
        uniform sampler2D uDiffuse;
        uniform int uTextured;
        uniform float uAlphaThreshold;   // > 0: the diffuse alpha is a cut-out, and the gloss fixed
        uniform vec3 uEye;
        uniform vec3 uLightDir;
        uniform vec3 uSunColour;
        uniform vec3 uAmbientSky;
        uniform vec3 uAmbientGround;
        void main()
        {
            vec4 base = uTextured != 0 ? texture(uDiffuse, vUv) : vec4(0.78, 0.72, 0.62, 0.3);
            if (uAlphaThreshold > 0.0 && base.a < uAlphaThreshold) discard;
            float gloss = uAlphaThreshold > 0.0 ? 0.3 : clamp(base.a, 0.0, 1.0);
            vec3 n = normalize(vNormal);
            if (!gl_FrontFacing) n = -n;
            vec3 v = normalize(uEye - vWorld);
            vec3 colour;
            if (uAtmoParams.x > 0.5) colour = kenshiLight(base.rgb, n, v, gloss, vWorld);
            else
            {
                vec3 ambient = mix(uAmbientGround, uAmbientSky, 0.5 + 0.5 * n.y);
                colour = base.rgb * (ambient + max(dot(n, normalize(uLightDir)), 0.0) * uSunColour);
            }
            fragColour = vec4(atmoApply(colour, uEye, vWorld), 1.0);
        }
        """;

    /// <summary>The native push block shared by the vertex and fragment stages (std430, 128 bytes): the model, the simple-sky fallback light, and the
    /// per-part bindless diffuse index and cut-out state.</summary>
    const string PushMembers = """
        mat4 model;
        vec3 sunColour;
        vec3 ambientSky;
        vec3 ambientGround;
        uint diffuse;
        int textured;
        float alphaThreshold;
        """;

    /// <summary>The uniforms whose native form is not in <see cref="NativeShaders.Map"/>: the model and the simple-sky fallback.</summary>
    static readonly Dictionary<string, string> NativeOwn = new()
    {
        ["uModel"] = "pc.model",
        ["uTextured"] = "pc.textured",
        ["uSunColour"] = "pc.sunColour",
        ["uAmbientSky"] = "pc.ambientSky",
        ["uAmbientGround"] = "pc.ambientGround",
    };

    /// <summary>A loaded diffuse texture and its stable bindless entry. The sampler can change with the upscaler's LOD bias; the index
    /// stays, so a draw recorded earlier in the frame keeps its view (the table's update-after-bind rules).</summary>
    sealed class DebrisTexture : IDisposable
    {
        readonly IGpuContext gpu;
        readonly SampledImage image;
        BindlessHandle handle;
        SampledTexture registered;
        bool hasEntry;

        public DebrisTexture(IGpuContext gpu, SampledImage image)
        {
            this.gpu = gpu;
            this.image = image;
        }

        /// <summary>The bindless index for <see cref="SampledImage.Sampled"/> now, registering or updating the entry.</summary>
        public uint Index
        {
            get
            {
                var sampled = image.Sampled();
                if (!hasEntry)
                {
                    handle = new BindlessHandle(BindlessKind.Texture2D, gpu.Bindless.Register(BindlessKind.Texture2D, sampled));
                    hasEntry = true;
                }
                else if (!sampled.Equals(registered))
                {
                    gpu.Bindless.Update(handle, sampled);
                }
                registered = sampled;
                CachedIndex = handle.Index;
                return CachedIndex;
            }
        }

        /// <summary>The index <see cref="Index"/> last registered; safe to use after the frame's bindless table has been bound.</summary>
        public uint CachedIndex { get; private set; }

        public void Dispose()
        {
            if (hasEntry) gpu.Bindless.Free(handle);
            image.Dispose();
        }
    }

    /// <summary>A loaded mesh: one buffer of vertices and one of indices, a draw per part.</summary>
    sealed class Mesh(DeviceBuffer vertices, DeviceBuffer indices, (uint First, uint Count, int VertexOffset, DebrisTexture? Diffuse, float AlphaThreshold)[] parts, Vector3 extent)
    {
        public readonly DeviceBuffer Vertices = vertices, Indices = indices;
        public readonly (uint First, uint Count, int VertexOffset, DebrisTexture? Diffuse, float AlphaThreshold)[] Parts = parts;
        public readonly Vector3 Extent = extent;
    }

    readonly IGpuContext gpu;
    readonly MaterialResolver? resolver;
    readonly Func<string, string?> find;
    readonly NativeFrame frame;
    readonly NativeProg program;
    readonly VertexLayout layout;
    readonly GpuBufferBinding[] bindings = new GpuBufferBinding[3];
    readonly Dictionary<string, Mesh?> meshes = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, DebrisTexture?> textures = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="resolver">Finds a mesh's textures (null: every mesh untextured).</param>
    /// <param name="find">A file name to its path in the install (the configured resource locations).</param>
    public DebrisRenderer(IGpuContext gpu, MaterialResolver? resolver, Func<string, string?> find)
    {
        this.gpu = gpu;
        this.resolver = resolver;
        this.find = find;
        frame = new NativeFrame(gpu);
        string vertex = NativeShaders.Port(Vertex, NativeShaders.Map(NativeOwn), PushMembers);
        string fragment = NativeShaders.Port(Fragment, NativeShaders.Map(NativeOwn), PushMembers);
        program = new NativeProg(gpu, frame, vertex, fragment, "debris");
        uint stride = (uint)Rendering.Vertex.Size;
        layout = program.Layout(
        [
            new LegacyProgram.Attribute(default, InternalFormat.Rgb32f, stride, false),
            new LegacyProgram.Attribute(default, InternalFormat.Rgb32f, stride, false),
            new LegacyProgram.Attribute(default, InternalFormat.RG32f, stride, false),
        ]);
        // The native model's bindless table is what keeps the full atmosphere inside Metal's direct-sampler budget.
        if (program.P.Info.FragmentReflection is { } reflection)
            SamplerBudget.Require("debris", reflection.Samplers, NativeShaders.BindlessSet);
    }

    /// <summary>
    /// Half the size of <paramref name="mesh"/>'s bounds along its own axes, loading it (null when it is missing or unreadable). Textured as
    /// <paramref name="materialOf"/> when given (a mesh no record names, sharing another's material), else as itself; the first load decides.
    /// </summary>
    public Vector3? Extent(string mesh, string? materialOf = null) => Load(mesh, materialOf)?.Extent;

    /// <summary>
    /// Draws <paramref name="pieces"/> in the open scene pass (a guest): depth tested and written, back faces culled, colour only.
    /// <paramref name="sliceViewProjection"/> is the slice's (absolute world to clip), <paramref name="frustum"/> its planes.
    /// </summary>
    public void Draw(ReadOnlySpan<DebrisPiece> pieces, Matrix4x4 sliceViewProjection, Vector3 eye, Vector4[] frustum, in WorldLighting light)
    {
        if (pieces.IsEmpty) return;
        var cmd = gpu.BeginGuest("debris");
        var targets = gpu.CurrentTargets();
        var state = gpu.CurrentState() with
        {
            Cull = CullMode.Back, Front = FrontFaceDirection.Ccw, DepthTest = true, DepthWrite = true,
            Blend = BlendState.Off, ColourMask = DrawState.Rgb,
        };
        cmd.SetViewport(targets.Viewport);
        cmd.SetScissor(targets.Scissor);
        cmd.SetRaster(state.Cull, state.Front);
        cmd.SetDepth(state.DepthTest, state.DepthWrite, state.Compare);
        cmd.SetDepthBias(false, 0, 0);
        cmd.BindPipeline(gpu.Pipelines.Get(state.Pipeline(program.P, layout, PrimitiveType.Triangles, targets.Formats, "debris")));

        // The bindless table is bound once per segment: register/update the debris textures before that bind. On Metal the
        // argument buffer must already name them when the encoder takes it.
        var standIn = gpu.StandIn2D;
        foreach (var piece in pieces)
            if (meshes.TryGetValue(piece.Mesh, out var known) && known is not null)
                foreach (var part in known.Parts) _ = part.Diffuse?.Index;

        // The view block carries the camera-relative projection/eye and the light direction; the frame block carries the atmosphere.
        var view = new ViewConstants
        {
            // The model matrices are camera-relative (as the legacy path set them), so the view projection is too.
            ViewProjection = Matrix4x4.CreateTranslation(eye) * sliceViewProjection,
            Eye = eye,
            LightDir = light.SunDirection,
            FogColour = light.FogColour,
            FogDistance = light.FogDistance,
        };
        frame.Bind(cmd, program.P, in view);

        var basePush = new DebrisPush
        {
            SunColour = light.SunColour,
            AmbientSky = light.AmbientSky,
            AmbientGround = light.AmbientGround,
        };
        foreach (var piece in pieces)
        {
            // Only meshes loaded before (Extent): nothing is uploaded inside the pass.
            if (!meshes.TryGetValue(piece.Mesh, out var mesh) || mesh is null || !FrustumTests.SphereVisible(frustum, piece.Position, mesh.Extent.Length() * piece.Scale * MathF.Max(piece.Stretch, 1))) continue;
            var m = Matrix4x4.CreateScale(piece.Scale, piece.Scale, piece.Scale * piece.Stretch) * Matrix4x4.CreateFromQuaternion(piece.Rotation) * Matrix4x4.CreateTranslation(piece.Position - eye);
            for (int k = 0; k < 3; k++) bindings[k] = new GpuBufferBinding(mesh.Vertices.Handle, (ulong)(k * 12));
            cmd.BindVertexBuffers(0, bindings);
            cmd.BindIndexBuffer(new GpuBufferBinding(mesh.Indices.Handle, 0), IndexKind.U32);
            foreach (var part in mesh.Parts)
            {
                var push = basePush;
                push.Model = m;
                push.Diffuse = part.Diffuse is { } diffuse ? diffuse.CachedIndex : standIn;
                push.Textured = part.Diffuse is null ? 0 : 1;
                push.AlphaThreshold = part.AlphaThreshold;
                cmd.PushConstants(program.P, GpuStage.Vertex | GpuStage.Fragment, in push);
                cmd.DrawIndexed(part.Count, 1, part.First, part.VertexOffset);
            }
        }
        gpu.EndGuest(cmd);
    }

    Mesh? Load(string file, string? materialOf)
    {
        if (meshes.TryGetValue(file, out var known)) return known;
        Mesh? mesh = null;
        try
        {
            if (find(Path.GetFileName(file.Replace('\\', '/'))) is { } path) mesh = Build(file, materialOf ?? file, OgreMeshReader.ReadFile(path));
        }
        catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException)
        {
            Console.Error.WriteLine($"debris mesh skipped: {file}: {e.Message}");
        }
        meshes[file] = mesh;
        return mesh;
    }

    Mesh? Build(string file, string materialOf, OgreMesh ogre)
    {
        var built = Model.Build(ogre, null);
        if (built.Parts.Count == 0) return null;
        var centre = built.Center;
        var vertices = new List<Vertex>();
        var indices = new List<uint>();
        var parts = new List<(uint, uint, int, DebrisTexture?, float)>();
        foreach (var part in built.Parts)
        {
            var material = resolver?.Candidates(materialOf, new OgreSubMesh { MaterialName = part.MaterialName }).FirstOrDefault();
            float threshold = material is { Alpha: AlphaSource.DiffuseAlpha } ? MathF.Max(material.AlphaThreshold, 0.5f) : 0;
            parts.Add(((uint)indices.Count, (uint)part.Indices.Length, vertices.Count, Texture(material?.Diffuse), threshold));
            foreach (var v in part.Vertices) vertices.Add(v with { Position = v.Position - centre });
            indices.AddRange(part.Indices);
        }
        var vb = DeviceBuffer.Create(gpu, (ulong)(vertices.Count * Rendering.Vertex.Size), BufferUse.Vertex, "debris " + file);
        var ib = DeviceBuffer.Create(gpu, (ulong)indices.Count * sizeof(uint), BufferUse.Index, "debris " + file);
        using (var batch = gpu.Uploads.Begin())
        {
            batch.Write(vb, 0, MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(vertices)));
            batch.Write(ib, 0, MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(indices)));
        }
        return new Mesh(vb, ib, [.. parts], (built.Max - built.Min) / 2);
    }

    DebrisTexture? Texture(string? name)
    {
        if (name is null) return null;
        if (textures.TryGetValue(name, out var known)) return known;
        DebrisTexture? texture = null;
        try
        {
            if (find(name) is { } path) texture = new DebrisTexture(gpu, SampledImage.FromDds(gpu, DdsReader.ReadFile(path), repeat: true, "debris " + name));
        }
        catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException)
        {
            Console.Error.WriteLine($"debris texture skipped: {name}: {e.Message}");
        }
        textures[name] = texture;
        return texture;
    }

    public void Dispose()
    {
        foreach (var mesh in meshes.Values)
        {
            mesh?.Vertices.Dispose();
            mesh?.Indices.Dispose();
        }
        foreach (var image in textures.Values) image?.Dispose();
        program.Dispose();
        frame.Dispose();
    }
}

/// <summary>The C# side of <see cref="DebrisRenderer.PushMembers"/> (std430 push constants, 128 bytes).</summary>
[StructLayout(LayoutKind.Explicit, Size = 128)]
struct DebrisPush
{
    [FieldOffset(0)] public Matrix4x4 Model;
    [FieldOffset(64)] public Vector3 SunColour;
    [FieldOffset(76)] public Vector3 AmbientSky;
    [FieldOffset(88)] public Vector3 AmbientGround;
    [FieldOffset(100)] public uint Diffuse;
    [FieldOffset(104)] public int Textured;
    [FieldOffset(108)] public float AlphaThreshold;
}
