using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Data.Ogre;
using Meitou.Rendering.Gpu;

namespace Meitou.Rendering;

/// <summary>
/// The movement marker (docs/game/ui-input.md "Movement marker"): <c>position.mesh</c> scaled 8 at <see cref="At"/>, in the two passes of the
/// game's <c>debug_marker</c> material: the visible part lit as <c>Basic_Lit_VP</c> lights it (contrast 0.5) and alpha blended, then the part
/// behind the scene darker and fainter. The game interface sets <see cref="At"/> and <see cref="Strength"/>; nothing is drawn at strength 0.
/// </summary>
public sealed unsafe class MoveMarkerRenderer : IDisposable
{
    const float Scale = 8, Contrast = 0.5f;
    static readonly Vector4 Visible = new(0.612f, 0.694f, 0.529f, 1), Hidden = new(0.306f, 0.347f, 0.2645f, 0.6f);

    const string Vertex = """
        #version 330 core
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec3 aNormal;
        uniform mat4 uViewProjection;   // world-from-camera-relative included: the offset is relative to the eye
        uniform vec3 uOffset;
        uniform vec3 uSun;
        uniform vec4 uColour;
        out vec4 vColour;
        void main()
        {
            gl_Position = uViewProjection * vec4(uOffset + aPos, 1.0);
            vColour = vec4(uColour.rgb * (clamp(dot(aNormal, uSun), 0.0, 1.0) * 0.5 + 0.5), uColour.a);
        }
        """;

    // The scene's near-slice depth decides which pass a fragment belongs to (the game's depth tests, against the scene only).
    const string Fragment = """
        #version 330 core
        in vec4 vColour;
        out vec4 fragColour;
        uniform sampler2D uSceneDepth;
        uniform vec2 uDepthScale;
        uniform int uHidden;
        void main()
        {
            float scene = texelFetch(uSceneDepth, ivec2(gl_FragCoord.xy * uDepthScale), 0).r;
            if ((gl_FragCoord.z > scene) != (uHidden != 0)) discard;
            fragColour = vec4(vColour.rgb * vColour.a, vColour.a);   // premultiplied, over a clear target
        }
        """;

    // The resolved marker over the picture (a full-screen triangle).
    const string CompositeVertex = """
        #version 330 core
        void main() { gl_Position = vec4(vec2((gl_VertexID << 1) & 2, gl_VertexID & 2) * 2.0 - 1.0, 0.0, 1.0); }
        """;

    const string CompositeFragment = """
        #version 330 core
        out vec4 fragColour;
        uniform sampler2D uMarker;
        void main() { fragColour = texelFetch(uMarker, ivec2(gl_FragCoord.xy), 0); }
        """;

    /// <summary>Samples per pixel of the marker (anti-aliasing: the mesh has many triangles under a pixel; at most what the device allows).</summary>
    const int WantedSamples = 8;

    readonly GpuContext gpu;
    readonly LegacyProgram program;
    readonly NativeSegment segment;
    readonly DeviceBuffer vertices;
    readonly VertexArrayBindings vertexSource;
    readonly BufferBinding[] bindings = new BufferBinding[2];
    readonly UniformHandle viewProjection, offset, sun, colour, depthScale, hiddenPass;
    readonly SamplerSlot sceneDepth;
    readonly LegacyProgram composite;
    readonly NativeSegment compositeSegment;
    readonly SamplerSlot markerImage;
    // The marker drawn multisampled with a depth of its own, at the picture's size, and resolved.
    Texture? msColour, msDepth, resolved;
    int samples;
    readonly int count;

    /// <summary>Where the marker stands (the ground point of the move order).</summary>
    public Vector3 At { get; set; }
    /// <summary>1 when the order is given, down to 0 (gone).</summary>
    public float Strength { get; set; }

    MoveMarkerRenderer(GpuContext gpu, float[] data)
    {
        this.gpu = gpu;
        count = data.Length / 6;
        program = LegacyProgram.Create(gpu, Vertex, Fragment, "move marker");
        segment = new NativeSegment(gpu, program, PrimitiveType.Triangles, "move marker");
        viewProjection = program.Uniform("uViewProjection");
        offset = program.Uniform("uOffset");
        sun = program.Uniform("uSun");
        colour = program.Uniform("uColour");
        depthScale = program.Uniform("uDepthScale");
        hiddenPass = program.Uniform("uHidden");
        sceneDepth = program.Sampler("uSceneDepth");
        composite = LegacyProgram.Create(gpu, CompositeVertex, CompositeFragment, "move marker composite");
        compositeSegment = new NativeSegment(gpu, composite, PrimitiveType.Triangles, "move marker composite");
        markerImage = composite.Sampler("uMarker");
        var l = gpu.Device.Limits;
        uint both = (uint)(l.FramebufferColorSampleCounts & l.FramebufferDepthSampleCounts);
        samples = 1;
        while (samples < WantedSamples && (both & (uint)(samples * 2)) != 0) samples *= 2;
        vertices = DeviceBuffer.Create(gpu, sizeof(float) * (ulong)data.Length, BufferUse.Vertex, "move marker");
        using (var batch = gpu.Uploads.Begin()) batch.Write(vertices, 0, MemoryMarshal.AsBytes(data.AsSpan()));
        vertexSource = new VertexArrayBindings(
        [
            new LegacyProgram.Attribute(default, InternalFormat.Rgb32f, 24, false),
            new LegacyProgram.Attribute(default, InternalFormat.Rgb32f, 24, false),
        ], default);
    }

    /// <summary>The marker from <c>position.mesh</c> (null when the file is missing or unreadable).</summary>
    public static MoveMarkerRenderer? Create(GpuContext gpu, Func<string, string?> find)
    {
        if (find("position.mesh") is not { } path) return null;
        try
        {
            var mesh = OgreMeshReader.ReadFile(path);
            var data = new List<float>();
            foreach (var sub in mesh.SubMeshes)
            {
                if (sub.Operation != OgreOperationType.TriangleList || (sub.VertexData ?? mesh.SharedVertexData) is not { } v || v.ReadPositions() is not { } positions) continue;
                float[]? normals = v.Find(OgreVertexSemantic.Normal) is { } n ? v.ReadFloats(n) : null;
                var indices = sub.Indices.Indices;
                for (int t = 0; t + 2 < indices.Length; t += 3)
                {
                    // Without normals in the file, the face's own.
                    var face = Vector3.Normalize(Vector3.Cross(positions[indices[t + 1]] - positions[indices[t]], positions[indices[t + 2]] - positions[indices[t]]));
                    for (int k = 0; k < 3; k++)
                    {
                        uint i = indices[t + k];
                        var p = positions[i] * Scale;
                        var normal = normals is null ? face : Vector3.Normalize(new Vector3(normals[i * 3], normals[i * 3 + 1], normals[i * 3 + 2]));
                        data.AddRange([p.X, p.Y, p.Z, normal.X, normal.Y, normal.Z]);
                    }
                }
            }
            return data.Count == 0 ? null : new MoveMarkerRenderer(gpu, [.. data]);
        }
        catch (Exception e) when (e is IOException or InvalidDataException)
        {
            Console.Error.WriteLine($"move marker skipped: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Draws over the finished <paramref name="picture"/> (after the tone map, so the colours come out as the material gives them, whatever the
    /// exposure): the visible part, then the part behind the scene, each fragment sorted by the near slice's <paramref name="nearDepth"/>
    /// (<paramref name="renderWidth"/> × <paramref name="renderHeight"/> of it used). The marker is drawn multisampled into targets of its own (its
    /// own depth hides its far side), resolved, and blended over the picture. Depth is clamped, so a marker beyond the near slice is drawn whole.
    /// </summary>
    public void Record(Texture picture, int width, int height, Texture nearDepth, int renderWidth, int renderHeight, Matrix4x4 nearViewProjection, Vector3 eye, Vector3 sunDirection)
    {
        if (Strength <= 0 || width <= 0 || height <= 0) return;
        if (msColour is null || msColour.Desc.Width != width || msColour.Desc.Height != height)
        {
            msColour?.Dispose(); msDepth?.Dispose(); resolved?.Dispose();
            var sampled = samples > 1 ? TextureUse.TransferSrc : TextureUse.Sampled;
            msColour = Texture.Create(gpu, new TextureDesc(InternalFormat.Rgba8, width, height, Samples: samples, Use: TextureUse.ColourTarget | sampled, Name: "move marker colour"));
            msDepth = Texture.Create(gpu, new TextureDesc(InternalFormat.DepthComponent24, width, height, Samples: samples, Use: TextureUse.DepthTarget, Name: "move marker depth"));
            resolved = samples > 1 ? Texture.Create(gpu, new TextureDesc(InternalFormat.Rgba8, width, height, Use: TextureUse.Sampled | TextureUse.TransferDst, Name: "move marker resolved")) : msColour;
        }
        var cmd = gpu.BeginNative("move marker");
        var viewport = new PixelViewport(0, 0, width, height, 0, 1);
        var t = PassTargets.Of(msColour, msDepth).At(viewport);
        t = t with
        {
            Colour = t.Colour with { Load = LoadOp.Clear, Clear = new ClearColour(0f, 0f, 0f, 0f) },
            Depth = t.Depth with { Load = LoadOp.Clear, DepthClear = 1 },
        };
        cmd.BeginRendering(t.Rendering);
        var vp = Matrix4x4.CreateTranslation(eye) * nearViewProjection;
        program.Set(viewProjection, in vp);
        program.Set(offset, At - eye);
        program.Set(sun, sunDirection);
        program.Set(depthScale, renderWidth / (float)width, renderHeight / (float)height);
        program.Bind(sceneDepth, new SampledTexture(Nearest(), nearDepth.View(), nearDepth.Image));
        var premultiplied = new BlendState(true, BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);
        for (int pass = 0; pass < 2; pass++)
        {
            bool hidden = pass == 1;
            var state = DrawState.For(t.Formats, gpu.Device.DepthClamp, cull: CullMode.Back, depthTest: true, depthWrite: !hidden,
                compare: DepthFunction.Lequal, blend: premultiplied, depthClamp: gpu.Device.DepthClamp, front: FrontFaceDirection.Ccw);
            var c = hidden ? Hidden : Visible;
            program.Set(colour, c.X, c.Y, c.Z, c.W * Strength);
            program.Set(hiddenPass, hidden ? 1 : 0);
            state.Record(cmd, t);
            cmd.BindPipeline(segment.Get(state, t.Formats, vertexSource));
            bindings[0] = new BufferBinding(vertices.Handle, 0);
            bindings[1] = new BufferBinding(vertices.Handle, 12);
            cmd.BindVertexBuffers(0, bindings);
            program.Flush(cmd);
            cmd.Draw((uint)count, 1, 0, 0);
        }
        cmd.EndRendering();
        cmd.Barrier(BarrierBatch.Full);
        if (samples > 1)
        {
            cmd.Resolve(msColour, resolved!);
            cmd.Barrier(BarrierBatch.Full);
        }
        var over = PassTargets.Of(picture, null).At(viewport);
        cmd.BeginRendering(over.Rendering);
        composite.Bind(markerImage, new SampledTexture(Nearest(), resolved!.View(), resolved.Image));
        var overState = DrawState.For(over.Formats, gpu.Device.DepthClamp, blend: premultiplied);
        overState.Record(cmd, over);
        cmd.BindPipeline(compositeSegment.Get(overState, over.Formats, null));
        composite.Flush(cmd);
        cmd.Draw(3);
        cmd.EndRendering();
        gpu.EndNative(cmd);
    }

    Silk.NET.Vulkan.Sampler Nearest() => gpu.Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.Nearest, TextureMagFilter.Nearest, TextureWrapMode.ClampToEdge,
        TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge, false, DepthFunction.Lequal, false, 1, false, 0));

    public void Dispose()
    {
        vertices.Dispose();
        if (resolved != msColour) resolved?.Dispose();
        msColour?.Dispose();
        msDepth?.Dispose();
    }
}
