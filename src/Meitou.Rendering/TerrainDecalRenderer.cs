using System.Numerics;
using Meitou.Data.Textures;
using Meitou.Data.World;
using Meitou.Rendering.Gpu;

namespace Meitou.Rendering;

/// <summary>
/// The blood on the ground (docs/game/blood-decals.md): the decal cells nearest the eye within the game's <c>Decal Range</c>, up to <see cref="Slots"/>, each drawn into
/// a layer of a <c>Decal Resolution</c>² texture array (the game's <c>Kenshi_Decal_Texture_n</c>), which the terrain's colour shader lays over its albedo
/// (<see cref="Apply"/>). A layer is drawn again only when its cell changes or a new cell takes it. Each decal is its texture × its colour, alpha blended into the
/// colour and added into the alpha, as the game's <c>Quad2DMaterial</c> draws it.
/// </summary>
public sealed unsafe class TerrainDecalRenderer : IDisposable
{
    /// <summary>The cells drawn at once (the game's eight decal textures).</summary>
    public const int Slots = 8;

    const string Vertex = """
        #version 330 core
        uniform vec2 uCentre;
        uniform vec2 uAxisW;
        uniform vec2 uAxisH;
        out vec2 vUv;
        void main()
        {
            vec2 q = vec2(gl_VertexID & 1, gl_VertexID >> 1);
            vUv = q;
            gl_Position = vec4(uCentre + (q.x - 0.5) * uAxisW + (q.y - 0.5) * uAxisH, 0.0, 1.0);
        }
        """;

    const string Fragment = """
        #version 330 core
        in vec2 vUv;
        out vec4 fragColour;
        uniform sampler2D uSprite;
        uniform vec4 uColour;
        void main() { fragColour = texture(uSprite, vUv) * uColour; }
        """;

    readonly GpuContext gpu;
    readonly LegacyProgram program;
    readonly NativeSegment segment;
    readonly UniformHandle centre, axisW, axisH, colour;
    readonly SamplerSlot sprite;
    readonly SampledImage?[][] sprites;
    // Each layer's cell, the cell version it shows, the rewrite it was cleared at and how many of the cell's appended decals it holds since (TerrainDecalStore.CellRewrite).
    readonly (uint Key, int Version, int Rewrite, int Appended)[] slots = new (uint, int, int, int)[Slots];
    readonly List<(float Distance, uint Key)> near = [];
    readonly HashSet<uint> drawn = [];
    readonly Vector4[] cells = new Vector4[Slots];
    Texture? target;
    (Texture Texture, uint Index)? bindless;
    int count;

    TerrainDecalRenderer(GpuContext gpu, SampledImage?[][] sprites)
    {
        this.gpu = gpu;
        this.sprites = sprites;
        program = LegacyProgram.Create(gpu, Vertex, Fragment, "terrain decals");
        segment = new NativeSegment(gpu, program, Silk.NET.Vulkan.PrimitiveTopology.TriangleStrip, "terrain decals");
        centre = program.Uniform("uCentre");
        axisW = program.Uniform("uAxisW");
        axisH = program.Uniform("uAxisH");
        colour = program.Uniform("uColour");
        sprite = program.Sampler("uSprite");
    }

    /// <summary>The renderer for the decal textures <paramref name="files"/> (per <see cref="TerrainDecalType"/>, its variants in order); a file that cannot be read draws nothing.</summary>
    public static TerrainDecalRenderer Create(GpuContext gpu, IReadOnlyList<IReadOnlyList<string>> files)
    {
        var sprites = new SampledImage?[4][];
        for (int t = 0; t < 4; t++)
        {
            var list = t < files.Count ? files[t] : [];
            sprites[t] = new SampledImage?[list.Count];
            for (int v = 0; v < list.Count; v++)
            {
                try { sprites[t][v] = SampledImage.FromDds(gpu, DdsReader.ReadFile(list[v]), repeat: false, $"blood {(TerrainDecalType)t} {v}"); }
                catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException) { Console.Error.WriteLine($"blood texture skipped: {list[v]}: {e.Message}"); }
            }
        }
        return new TerrainDecalRenderer(gpu, sprites);
    }

    /// <summary>The game's <c>Decal Resolution</c>: the side of each cell's texture (512, 1024 or 2048); a change draws every cell again.</summary>
    public int Resolution { get; set; } = 1024;

    /// <summary>The game's <c>Decal Range</c>: how far from the eye (X/Z, to the cell's square) cells are drawn.</summary>
    public float Range { get; set; } = 1000;

    /// <summary>Whether <paramref name="key"/>'s cell is on a layer now (its decals then do not expire: <see cref="TerrainDecalStore.Age"/>).</summary>
    public bool Drawn(uint key) => drawn.Contains(key);

    /// <summary>Picks the cells near <paramref name="eye"/> and draws the layers whose cell changed. Before the scene (outside a pass).</summary>
    public void Record(TerrainDecalStore store, Vector3 eye)
    {
        near.Clear();
        float range = MathF.Max(Range, 0);
        foreach (uint key in store.Cells)
        {
            var (x0, z0) = TerrainDecalCells.Origin(key);
            float dx = MathF.Max(MathF.Max(x0 - eye.X, eye.X - (x0 + TerrainDecalCells.Size)), 0);
            float dz = MathF.Max(MathF.Max(z0 - eye.Z, eye.Z - (z0 + TerrainDecalCells.Size)), 0);
            float d = MathF.Sqrt(dx * dx + dz * dz);
            if (d <= range) near.Add((d, key));
        }
        near.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        if (near.Count > Slots) near.RemoveRange(Slots, near.Count - Slots);

        int resolution = Math.Clamp(Resolution, 64, 4096);
        if (near.Count > 0 && (target is null || target.Desc.Width != resolution))
        {
            if (bindless is { } old) gpu.Bindless.Free(BindlessKind.Texture2DArray, old.Index);
            bindless = null;
            target?.Dispose();
            target = Texture.Create(gpu, new TextureDesc(Silk.NET.Vulkan.Format.R8G8B8A8Unorm, resolution, resolution, Layers: Slots, Kind: TextureKind.Texture2DArray,
                Use: TextureUse.ColourTarget | TextureUse.Sampled, Name: "terrain decals"));
            Array.Clear(slots);
        }

        // A cell keeps its layer while it stays near; the others go to the layers left free.
        drawn.Clear();
        Span<bool> taken = stackalloc bool[Slots];
        Span<int> layerOf = stackalloc int[Slots];
        for (int i = 0; i < near.Count; i++)
        {
            layerOf[i] = -1;
            for (int s = 0; s < Slots; s++)
                if (!taken[s] && slots[s].Version != 0 && slots[s].Key == near[i].Key) { layerOf[i] = s; taken[s] = true; break; }
        }
        for (int i = 0; i < near.Count; i++)
        {
            if (layerOf[i] >= 0) continue;
            for (int s = 0; s < Slots; s++)
                if (!taken[s]) { layerOf[i] = s; taken[s] = true; slots[s] = (near[i].Key, 0, 0, 0); break; }
        }

        CommandList? cmd = null;
        count = 0;
        for (int i = 0; i < near.Count; i++)
        {
            uint key = near[i].Key;
            int layer = layerOf[i];
            drawn.Add(key);
            var (column, row) = TerrainDecalCells.Cell(key);
            cells[count++] = new Vector4(column, row, layer, 0);
            int version = store.CellVersion(key);
            if (slots[layer].Version == version) continue;
            cmd ??= gpu.BeginNative("terrain decals");
            // Only decals added since the layer was drawn: those drawn over it (a battle's cells change every few ticks); any gone: the layer cleared and drawn again.
            int rewrite = store.CellRewrite(key);
            var appended = store.Appended(key);
            var slot = slots[layer];
            if (slot.Version != 0 && slot.Rewrite == rewrite && slot.Appended <= appended.Length) DrawLayer(cmd, layer, appended[slot.Appended..], clear: false);
            else DrawLayer(cmd, layer, store.In(key), clear: true);
            slots[layer] = (key, version, rewrite, appended.Length);
        }
        if (cmd is not null)
        {
            cmd.Barrier(BarrierBatch.Full);
            gpu.EndNative(cmd);
        }
        if (count > 0 && target is not null && (bindless is not { } known || known.Texture != target))
            bindless = (target, gpu.Bindless.Register(BindlessKind.Texture2DArray, new SampledTexture(Linear(), target.View(), target.Image)));
    }

    /// <summary>
    /// Clears <paramref name="layer"/> when <paramref name="clear"/> and draws <paramref name="decals"/> into it (<c>FUN_1408dec40</c>): a quad centred on the decal's
    /// place in the cell, its sides the half sizes / 10240 in clip space along the rotation, the texture × the colour (its alpha at most the life). The colour is alpha
    /// blended (red, green, blue only), then the alpha added (alpha only), the game's separate blend.
    /// </summary>
    void DrawLayer(CommandList cmd, int layer, ReadOnlySpan<TerrainDecal> decals, bool clear)
    {
        if (!clear && decals.IsEmpty) return;
        var t = PassTargets.Of(target, null);
        var colourTarget = new RenderTarget(target!.Attachment(0, layer), clear ? Silk.NET.Vulkan.AttachmentLoadOp.Clear : Silk.NET.Vulkan.AttachmentLoadOp.Load,
            new Silk.NET.Vulkan.ClearValue(new Silk.NET.Vulkan.ClearColorValue(0f, 0f, 0f, 0f)), target.Image);
        t = t with { Colour = colourTarget };
        cmd.BeginRendering(t.Rendering);
        var blendColour = new BlendState(true, Silk.NET.Vulkan.BlendFactor.SrcAlpha, Silk.NET.Vulkan.BlendFactor.OneMinusSrcAlpha);
        var addAlpha = new BlendState(true, Silk.NET.Vulkan.BlendFactor.One, Silk.NET.Vulkan.BlendFactor.One);
        for (int pass = 0; pass < 2; pass++)
        {
            var state = pass == 0
                ? DrawState.For(t.Formats, gpu.Device.DepthClamp, blend: blendColour, mask: DrawState.Rgb)
                : DrawState.For(t.Formats, gpu.Device.DepthClamp, blend: addAlpha, mask: Silk.NET.Vulkan.ColorComponentFlags.ABit);
            state.Record(cmd, t);
            cmd.BindPipeline(segment.Get(state, t.Formats, null));
            foreach (var d in decals)
            {
                var images = sprites[(int)d.Type];
                if (d.Variant >= images.Length || images[d.Variant] is not { } image) continue;
                float angle = d.Rotation / 1000f, s = MathF.Sin(angle), c = MathF.Cos(angle);
                uint abgr = d.Colour;
                float alpha = MathF.Min((abgr >> 24) / 255f, d.Life);
                program.Set(centre, d.LocalX * 2 / 1000f - 1, d.LocalZ * 2 / 1000f - 1);
                program.Set(axisW, d.HalfWidth / 10240f * s, d.HalfWidth / 10240f * c);
                program.Set(axisH, d.HalfHeight / 10240f * c, -d.HalfHeight / 10240f * s);
                program.Set(colour, (abgr & 0xFF) / 255f, (abgr >> 8 & 0xFF) / 255f, (abgr >> 16 & 0xFF) / 255f, alpha);
                program.Bind(sprite, image.Sampled());
                program.Flush(cmd);
                cmd.Draw(4);
            }
        }
        cmd.EndRendering();
    }

    /// <summary>The cells for the terrain's colour shader (<c>uBlood</c>, <c>uBloodCount</c>, <c>uBloodCellSize</c>, <c>uBloodCells</c>): none until a cell is drawn.</summary>
    internal void Apply(ref TerrainConstants c)
    {
        if (count == 0 || bindless is not { } b) return;
        c.Blood = b.Index;
        c.BloodCount = count;
        c.BloodCellSize = TerrainDecalCells.Size;
        for (int i = 0; i < count; i++) c.BloodCells[i] = cells[i];
    }

    Silk.NET.Vulkan.Sampler Linear() => gpu.Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.Linear, TextureMagFilter.Linear, TextureWrapMode.ClampToEdge,
        TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge, false, DepthFunction.Lequal, false, 1, false, 0));

    public void Dispose()
    {
        if (bindless is { } b) gpu.Bindless.Free(BindlessKind.Texture2DArray, b.Index);
        target?.Dispose();
        foreach (var list in sprites)
            foreach (var image in list) image?.Dispose();
        program.Dispose();
    }
}
