using InternalFormat = Meitou.Rendering.Gpu.InternalFormat;
using System.Collections.Concurrent;
using System.Numerics;
using Meitou.Data;
using Meitou.Data.Gameplay;
using Meitou.Rendering.Gpu;
using Silk.NET.Vulkan;

namespace Meitou.Rendering.Characters;

/// <summary>An inventory icon wanted: the item's record, its <c>material sid</c> ("" none) and the COLOR_DATA it is dyed with ("" undyed), the ids the game's saved icons are named by.</summary>
public readonly record struct IconKey(string Record, string Material, string Colour = "");

/// <summary>
/// The inventory icons the game would draw from an item's mesh (docs/game/ui-screens.md "Item icons"), made here when the game's saved picture is not at hand. An icon is the item's
/// mesh drawn through the item shader into a texture of its own, from the camera <see cref="ItemIconStyle"/> describes and lit from the camera. It is drawn each frame until the
/// textures it uses are resident (they stream in), then kept.
/// </summary>
internal sealed unsafe partial class CharacterRenderer
{
    /// <summary>Icons being drawn at once; the others wait their turn.</summary>
    const int IconsPerFrame = 4;
    /// <summary>Frames an icon is drawn waiting for its textures before it is shown with what it has.</summary>
    const int IconGiveUpFrames = 300;

    sealed class IconEntry
    {
        public required IconKey Key;
        public CharacterAsset? Asset;
        public ItemIconStyle Style = null!;
        public GpuTexture? Colour, Depth;
        public SampledTexture Sampled;
        /// <summary>The asset could not be made (no mesh): the cell keeps the name.</summary>
        public bool Failed;
        /// <summary>The textures were all resident when this frame's materials were written.</summary>
        public bool TexturesReady;
        /// <summary>Drawn once with everything resident: it is kept as is.</summary>
        public bool Settled;
        /// <summary>The image holds a picture (published to the UI thread).</summary>
        public volatile bool Drawn;
        public int Frames;
    }

    readonly ConcurrentDictionary<IconKey, IconEntry> icons = new();
    readonly ConcurrentQueue<IconEntry> iconRequests = new();
    readonly List<IconEntry> iconsUnsettled = [];
    readonly Dictionary<CharacterAsset, IconEntry> iconByAsset = new(ReferenceEqualityComparer.Instance);
    int iconAssetsBuilt;

    /// <summary>
    /// The drawn icon of <paramref name="key"/> as a texture (drawn upside down, as the renderer's targets are: show it flipped), once it exists; asking starts it being made. Any thread.
    /// </summary>
    public bool TryGetIcon(IconKey key, out (GpuTexture Texture, SampledTexture Sampled) icon)
    {
        if (!icons.TryGetValue(key, out var entry))
        {
            entry = icons.GetOrAdd(key, k => new IconEntry { Key = k });
            iconRequests.Enqueue(entry);
        }
        icon = entry.Drawn ? (entry.Colour!, entry.Sampled) : default;
        return entry.Drawn;
    }

    /// <summary>Icons asked for and not yet final: waiting for their turn or for textures to stream in.</summary>
    public int PendingIcons => iconRequests.Count + iconsUnsettled.Count;

    /// <summary>The icon texture of <paramref name="key"/> if it has been drawn (no request made).</summary>
    public bool HasIcon(IconKey key) => icons.TryGetValue(key, out var e) && e.Drawn;

    /// <summary>
    /// Adds the icons being made to this frame's list (after the characters: they are drawn only by <see cref="DrawIcons"/>, so the world and the shadows skip them), building the
    /// asset of a new one (one a frame). Their node's matrix goes to <paramref name="attach"/> later (<see cref="PoseIcons"/>). Called in <see cref="Update"/>.
    /// </summary>
    void GatherIcons(Vector3 eye, ref int attachCount)
    {
        while (iconRequests.TryDequeue(out var requested)) iconsUnsettled.Add(requested);
        iconAssetsBuilt = 0;
        int gathered = 0;
        for (int i = 0; i < iconsUnsettled.Count && gathered < IconsPerFrame; i++)
        {
            var entry = iconsUnsettled[i];
            if (entry.Asset is null && !entry.Failed)
            {
                if (iconAssetsBuilt++ > 0) continue;
                BuildIconAsset(entry);
            }
            if (entry.Failed) { iconsUnsettled.RemoveAt(i--); continue; }
            if (entry.Asset is not { } asset) continue;
            entry.TexturesReady = asset.Parts[0].Material.Tex.All(t => t is null || t.Native is not null || t.State == WorldTexture.Residency.Missing);
            if (liveCount == live.Length) Array.Resize(ref live, liveCount * 2);
            ref var l = ref live[liveCount++];
            l = default;
            l.Asset = asset;
            l.World = Matrix4x4.Identity;
            l.Centre = eye;
            l.Radius = 1;
            l.PortraitOnly = true;
            l.AttachBase = attachCount;
            attachCount += 1;
            gathered++;
        }
    }

    void BuildIconAsset(IconEntry entry)
    {
        var item = content.Database.Find(entry.Key.Record);
        if (item is null) { entry.Failed = true; return; }
        var spec = content.IconMaterialOf(item, entry.Key.Material);
        entry.Style = ItemIconStyle.Of(item, spec);
        entry.Asset = content.BuildIcon(item, spec, entry.Key.Colour.Length > 0 ? content.Database.Find(entry.Key.Colour) : null);
        if (entry.Asset is null) { entry.Failed = true; return; }
        iconByAsset[entry.Asset] = entry;
    }

    /// <summary>Sets each icon's node matrix in the attachment table (the part is not skinned: its model matrix is the node's). Called in <see cref="Update"/> after the poses.</summary>
    void PoseIcons()
    {
        for (int i = 0; i < liveCount; i++)
            if (live[i].Asset.IsIcon) attach[live[i].AttachBase] = iconByAsset[live[i].Asset].Style.Matrices().Model;
    }

    /// <summary>Draws the icons gathered this frame, each into its own texture (after <see cref="Update"/>, outside any pass).</summary>
    public void DrawIcons()
    {
        for (int i = 0; i < liveCount; i++)
        {
            if (!live[i].Asset.IsIcon) continue;
            var entry = iconByAsset[live[i].Asset];
            DrawIcon(entry, i);
            // Shown once its textures were all resident (before that it would show stand-ins), or after a while if they never are (the video memory guard keeps them out).
            if (entry.TexturesReady || ++entry.Frames > IconGiveUpFrames) entry.Drawn = true;
            if (entry.TexturesReady)
            {
                // Final: the depth buffer is only needed while drawing.
                entry.Settled = true;
                iconsUnsettled.Remove(entry);
                entry.Depth?.Dispose();
                entry.Depth = null;
            }
        }
    }

    void DrawIcon(IconEntry entry, int index)
    {
        var (w, h) = entry.Style.TextureSize;
        if (entry.Colour is null)
        {
            entry.Colour = Gpu.CreateTexture(new TextureDesc(InternalFormat.Rgba8, w, h, Use: TextureUse.Sampled | TextureUse.ColourTarget | TextureUse.TransferSrc, Name: $"icon {entry.Key.Record}"));
            entry.Depth = Gpu.CreateTexture(new TextureDesc(InternalFormat.DepthComponent24, w, h, Use: TextureUse.DepthTarget, Name: $"icon depth {entry.Key.Record}"));
            entry.Sampled = new SampledTexture(Gpu.Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.Linear, TextureMagFilter.Linear,
                TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge, false, DepthFunction.Lequal, false, 1, false, 0)), entry.Colour.View());
        }
        var cmd = Gpu.BeginNative("item icon");
        var target = PassTargets.Of(entry.Colour, entry.Depth);
        cmd.BeginRendering(new RenderingDesc(target.Colour, target.Depth with { Load = LoadOp.Clear, DepthClear = 1f }, target.Width, target.Height));
        Gpu.BeginHostPass(cmd, target, DrawState.Scene(target.Formats));
        var rect = new PixelRect(0, 0, (uint)w, (uint)h);
        cmd.Clear(true, default, true, 1, rect);
        Gpu.SetPassViewport(new PixelViewport(0, 0, w, h, 0, 1), rect);
        var (_, view, projection) = entry.Style.Matrices();
        var eye = new Vector3(0, 0, entry.Style.Distance);
        portraitLive = index;
        try { DrawView(new ViewConstants { ViewProjection = view * projection, Eye = eye, LightDir = Vector3.UnitZ, FogDistance = -1 }, eye, []); }
        finally { portraitLive = -1; }
        Gpu.EndHostPass(cmd);
        cmd.EndRendering();
        Gpu.EndNative(cmd);
    }

    void DisposeIcons()
    {
        foreach (var entry in icons.Values)
        {
            entry.Colour?.Dispose();
            entry.Depth?.Dispose();
        }
        icons.Clear();
    }
}
