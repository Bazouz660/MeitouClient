using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Data.Ogre;
using Meitou.Data.Textures;
using Meitou.Rendering.Gpu;

namespace Meitou.Rendering;

/// <summary>One loose mesh in the world: its file, its size and where it is (rotation and translation, the mesh's middle at the translation).</summary>
public readonly record struct DebrisPiece(string Mesh, float Scale, Quaternion Rotation, Vector3 Position);

/// <summary>
/// Loose meshes thrown about by the host (luckyren Mode's bones and limbs, docs/game/luckyren.md): each a static mesh, centred on its bounds, textured with
/// its first resolved diffuse map and lit as the world's objects (<c>kenshiLight</c> under the game sky, the plain light otherwise, then the haze). Drawn
/// in the scene pass of the near slice before the characters (colour only, leaving the alpha to them); they cast no shadow. A mesh is loaded the first time
/// its <see cref="Extent"/> is asked for.
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

    /// <summary>A loaded mesh: one buffer of vertices and one of indices, a draw per part.</summary>
    sealed class Mesh(DeviceBuffer vertices, DeviceBuffer indices, (uint First, uint Count, int VertexOffset, SampledImage? Diffuse, float AlphaThreshold)[] parts, Vector3 extent)
    {
        public readonly DeviceBuffer Vertices = vertices, Indices = indices;
        public readonly (uint First, uint Count, int VertexOffset, SampledImage? Diffuse, float AlphaThreshold)[] Parts = parts;
        public readonly Vector3 Extent = extent;
    }

    readonly GpuContext gpu;
    readonly MaterialResolver? resolver;
    readonly Func<string, string?> find;
    readonly LegacyProgram program;
    readonly NativeSegment segment;
    readonly VertexArrayBindings vertexSource;
    readonly GpuBufferBinding[] bindings = new GpuBufferBinding[3];
    readonly UniformHandle viewProjection, model, eyeU, textured, alphaThreshold, lightDir, sunColour, ambientSky, ambientGround;
    readonly SamplerSlot diffuse;
    readonly Dictionary<string, Mesh?> meshes = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, SampledImage?> textures = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="resolver">Finds a mesh's textures (null: every mesh untextured).</param>
    /// <param name="find">A file name to its path in the install (the configured resource locations).</param>
    public DebrisRenderer(GpuContext gpu, MaterialResolver? resolver, Func<string, string?> find)
    {
        this.gpu = gpu;
        this.resolver = resolver;
        this.find = find;
        program = LegacyProgram.Create(gpu, Vertex, Fragment, "debris");
        segment = new NativeSegment(gpu, program, PrimitiveType.Triangles, "debris");
        viewProjection = program.Uniform("uViewProjection");
        model = program.Uniform("uModel");
        eyeU = program.Uniform("uEye");
        textured = program.Uniform("uTextured");
        alphaThreshold = program.Uniform("uAlphaThreshold");
        lightDir = program.Uniform("uLightDir");
        sunColour = program.Uniform("uSunColour");
        ambientSky = program.Uniform("uAmbientSky");
        ambientGround = program.Uniform("uAmbientGround");
        diffuse = program.Sampler("uDiffuse");
        uint stride = (uint)Rendering.Vertex.Size;
        vertexSource = new VertexArrayBindings(
        [
            new LegacyProgram.Attribute(default, InternalFormat.Rgb32f, stride, false),
            new LegacyProgram.Attribute(default, InternalFormat.Rgb32f, stride, false),
            new LegacyProgram.Attribute(default, InternalFormat.RG32f, stride, false),
        ], default);
    }

    /// <summary>Half the size of <paramref name="mesh"/>'s bounds along its own axes, loading it (null when it is missing or unreadable).</summary>
    public Vector3? Extent(string mesh) => Load(mesh)?.Extent;

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
        cmd.BindPipeline(segment.Get(state, targets.Formats, vertexSource));
        program.ApplyGlobals();
        var vp = Matrix4x4.CreateTranslation(eye) * sliceViewProjection;
        program.Set(viewProjection, in vp);
        program.Set(eyeU, eye);
        program.Set(lightDir, light.SunDirection);
        program.Set(sunColour, light.SunColour);
        program.Set(ambientSky, light.AmbientSky);
        program.Set(ambientGround, light.AmbientGround);
        foreach (var piece in pieces)
        {
            // Only meshes loaded before (Extent): nothing is uploaded inside the pass.
            if (!meshes.TryGetValue(piece.Mesh, out var mesh) || mesh is null || !FrustumTests.SphereVisible(frustum, piece.Position, mesh.Extent.Length() * piece.Scale)) continue;
            var m = Matrix4x4.CreateScale(piece.Scale) * Matrix4x4.CreateFromQuaternion(piece.Rotation) * Matrix4x4.CreateTranslation(piece.Position - eye);
            program.Set(this.model, in m);
            for (int k = 0; k < 3; k++) bindings[k] = new GpuBufferBinding(mesh.Vertices.Handle, (ulong)(k * 12));
            cmd.BindVertexBuffers(0, bindings);
            cmd.BindIndexBuffer(new GpuBufferBinding(mesh.Indices.Handle, 0), IndexKind.U32);
            foreach (var part in mesh.Parts)
            {
                program.Set(textured, part.Diffuse is null ? 0 : 1);
                program.Set(alphaThreshold, part.AlphaThreshold);
                if (part.Diffuse is { } image) program.Bind(diffuse, image.Sampled());
                program.Flush(cmd);
                cmd.DrawIndexed(part.Count, 1, part.First, part.VertexOffset);
            }
        }
        gpu.EndGuest(cmd);
    }

    Mesh? Load(string file)
    {
        if (meshes.TryGetValue(file, out var known)) return known;
        Mesh? mesh = null;
        try
        {
            if (find(Path.GetFileName(file.Replace('\\', '/'))) is { } path) mesh = Build(file, OgreMeshReader.ReadFile(path));
        }
        catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException)
        {
            Console.Error.WriteLine($"debris mesh skipped: {file}: {e.Message}");
        }
        meshes[file] = mesh;
        return mesh;
    }

    Mesh? Build(string file, OgreMesh ogre)
    {
        var built = Model.Build(ogre, null);
        if (built.Parts.Count == 0) return null;
        var centre = built.Center;
        var vertices = new List<Vertex>();
        var indices = new List<uint>();
        var parts = new List<(uint, uint, int, SampledImage?, float)>();
        foreach (var part in built.Parts)
        {
            var material = resolver?.Candidates(file, new OgreSubMesh { MaterialName = part.MaterialName }).FirstOrDefault();
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

    SampledImage? Texture(string? name)
    {
        if (name is null) return null;
        if (textures.TryGetValue(name, out var known)) return known;
        SampledImage? image = null;
        try
        {
            if (find(name) is { } path) image = SampledImage.FromDds(gpu, DdsReader.ReadFile(path), repeat: true, "debris " + name);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException)
        {
            Console.Error.WriteLine($"debris texture skipped: {name}: {e.Message}");
        }
        textures[name] = image;
        return image;
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
    }
}
