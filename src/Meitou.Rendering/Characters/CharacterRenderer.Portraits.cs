using InternalFormat = Meitou.Rendering.Gpu.InternalFormat;
using Meitou.Data.Characters;
using System.Numerics;
using Meitou.Rendering.Gpu;
using Silk.NET.Vulkan;

namespace Meitou.Rendering.Characters;

/// <summary>A portrait the host wants this frame: the character by its draw-list key, the atlas cell to draw it in, and the number that turns its camera.</summary>
public readonly record struct PortraitRequest(long Key, int Cell, uint Serial);

/// <summary>
/// The squad portraits (docs/character-renderer.md "Portraits"): each requested character drawn into its cell of one atlas, from the camera the original
/// places by the race's <c>portrait distance</c> and <c>portrait offset</c> round the head bone, lit by the shader's plain model (no fog, sky or shadows).
/// </summary>
internal sealed unsafe partial class CharacterRenderer
{
    /// <summary>A cell's size in pixels and the atlas's cells across and down (the original's atlas: 128 px cells).</summary>
    public const int PortraitSize = 128, PortraitColumns = 16, PortraitRows = 2;
    /// <summary>The vertical field of view of the portrait camera.</summary>
    const float PortraitFieldOfView = 25 * MathF.PI / 180;

    /// <summary>The portraits to draw this frame (the host fills it before <see cref="Update"/>, which also poses these characters when they are out of range).</summary>
    public List<PortraitRequest> PortraitRequests { get; } = [];
    readonly HashSet<long> portraitKeys = [];
    readonly Dictionary<long, int> liveByKey = [];
    int portraitLive = -1;
    Texture? portraitColour, portraitDepth;
    SampledTexture? portraitSampled;

    /// <summary>The atlas (sRGB, transparent where no character is), made on first use. Render thread.</summary>
    public (Texture Texture, SampledTexture Sampled) PortraitAtlas
    {
        get
        {
            if (portraitColour is null)
            {
                int w = PortraitSize * PortraitColumns, h = PortraitSize * PortraitRows;
                portraitColour = Texture.Create(Gpu, new TextureDesc(InternalFormat.Srgb8Alpha8, w, h, Use: TextureUse.Sampled | TextureUse.ColourTarget, Name: "portraits"));
                portraitDepth = Texture.Create(Gpu, new TextureDesc(InternalFormat.DepthComponent24, w, h, Use: TextureUse.DepthTarget, Name: "portraits depth"));
                portraitSampled = new SampledTexture(Gpu.Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.Linear, TextureMagFilter.Linear,
                    TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge, false, DepthFunction.Lequal, false, 1, false, 0)), portraitColour.View(), portraitColour.Image);
                cleared = false;
                portraitShown.Clear();
            }
            return (portraitColour, portraitSampled!.Value);
        }
    }

    bool cleared;

    /// <summary>The keys <see cref="Update"/> poses even out of range.</summary>
    void GatherPortraitKeys()
    {
        portraitKeys.Clear();
        foreach (var r in PortraitRequests) portraitKeys.Add(r.Key);
        if (StageRequest is { } stage) portraitKeys.Add(stage.Key);
    }

    sealed record ShownPortrait(long Key, uint Serial, CharacterAsset Asset, float Radius,
        (Texture? Image, bool Swizzled)[] Textures);
    /// <summary>The character, appearance and streamed texture images each cell last showed.</summary>
    readonly Dictionary<int, ShownPortrait> portraitShown = [];
    Matrix4x4[] portraitBones = [], portraitAttach = [];
    readonly List<ActiveLayer> portraitLayers = [];

    /// <summary>
    /// Draws the requested portraits into their cells (after <see cref="Update"/>, outside any pass). Like the original's, a portrait is a still: the character in its
    /// idle stance at its start and its posture layers, not its live animation. A cell is posed and drawn anew when its character or appearance (equipment) changes,
    /// or its size, camera serial or streamed textures change; a
    /// character not posed this frame (not in the list) keeps what its cell last showed.
    /// </summary>
    public void DrawPortraits()
    {
        if (PortraitRequests.Count == 0) return;
        var (atlas, _) = PortraitAtlas;
        liveByKey.Clear();
        for (int i = 0; i < liveCount; i++) liveByKey[live[i].Key] = i;
        bool any = !cleared;
        foreach (var request in PortraitRequests)
            if (Due(request, out _)) { any = true; break; }
        if (!any) return;
        var cmd = Gpu.BeginNative("portraits");
        var target = PassTargets.Of(atlas, portraitDepth);
        cmd.BeginRendering(new RenderingDesc(target.Colour, target.Depth with { Load = LoadOp.Clear, DepthClear = 1f },
            target.Width, target.Height));
        Gpu.BeginHostPass(cmd, target, DrawState.Scene(target.Formats));
        if (!cleared) { Gpu.Clear(cmd, true, default, false, 1, new PixelRect(0, 0, (uint)target.Width, (uint)target.Height)); cleared = true; }
        foreach (var request in PortraitRequests)
        {
            if (!Due(request, out int index)) continue;
            ref var l = ref live[index];
            var head = PosePortrait(in l);
            portraitShown[request.Cell] = new ShownPortrait(request.Key, request.Serial, l.Asset, l.Radius,
                l.Asset.Parts.SelectMany(p => p.Material.Tex).Select(t => (t?.Native, t?.Swizzled ?? false)).ToArray());
            int x = request.Cell % PortraitColumns * PortraitSize, y = request.Cell / PortraitColumns * PortraitSize;
            var rect = new PixelRect(x, y, PortraitSize, PortraitSize);
            Gpu.Clear(cmd, true, default, true, 1, rect);
            Gpu.SetPassViewport(new PixelViewport(x, y, PortraitSize, PortraitSize, 0, 1), rect);
            var (eye, look) = PortraitCamera(in l, head, request.Serial);
            var view = Matrix4x4.CreateLookAt(eye, look, Vector3.UnitY);
            var projection = Matrix4x4.CreatePerspectiveFieldOfView(PortraitFieldOfView, 1, 0.5f, 400);
            // The key light from the camera's side, above and a little to the left.
            var toEye = Vector3.Normalize(eye - look);
            var left = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, toEye));
            var light = Vector3.Normalize(toEye + 0.7f * Vector3.UnitY + 0.4f * left);
            // The still pose's palette in place of the live one: this view's own bone memory and attachments past the frame's, weapon sheathed.
            var (liveBones, livePrevious) = (bones, previousBones);
            var (boneBase, attachBase, weapon) = (l.BoneBase, l.AttachBase, l.WeaponDrawn);
            int bonesCount = l.Asset.Rig.BoneCount;
            bones = previousBones = Gpu.Frame.Constants.Allocate((ulong)Math.Max(bonesCount, 1) * 64, 256);
            portraitBones.AsSpan(0, bonesCount).CopyTo(new Span<Matrix4x4>(bones.Pointer, bonesCount));
            if (attach.Length < attachCount + l.Asset.AttachCount) Array.Resize(ref attach, attachCount + l.Asset.AttachCount);
            portraitAttach.AsSpan(0, l.Asset.AttachCount).CopyTo(attach.AsSpan(attachCount));
            (l.BoneBase, l.AttachBase, l.WeaponDrawn) = (0, attachCount, false);
            portraitLive = index;
            try { DrawView(new ViewConstants { ViewProjection = view * projection, Eye = eye, LightDir = light }, eye, []); }
            finally { portraitLive = -1; (bones, previousBones) = (liveBones, livePrevious); (l.BoneBase, l.AttachBase, l.WeaponDrawn) = (boneBase, attachBase, weapon); }
        }
        Gpu.EndHostPass(cmd);
        cmd.EndRendering();
        Gpu.EndNative(cmd);
    }

    /// <summary>Whether <paramref name="request"/>'s cell needs drawing this frame: its character is in the list and the cell shows someone else or another
    /// appearance, size, camera serial or streamed texture images.</summary>
    bool Due(PortraitRequest request, out int index)
    {
        index = -1;
        if (request.Cell < 0 || request.Cell >= PortraitColumns * PortraitRows || !liveByKey.TryGetValue(request.Key, out index)) return false;
        ref var l = ref live[index];
        if (!portraitShown.TryGetValue(request.Cell, out var shown) || shown.Key != request.Key ||
            shown.Serial != request.Serial || shown.Asset != l.Asset || shown.Radius != l.Radius) return true;
        int texture = 0;
        foreach (var part in l.Asset.Parts)
            foreach (var t in part.Material.Tex)
            {
                if (texture >= shown.Textures.Length || shown.Textures[texture] != (t?.Native, t?.Swizzled ?? false)) return true;
                texture++;
            }
        return texture != shown.Textures.Length;
    }

    /// <summary>Poses <paramref name="l"/> still for its portrait (its idle stance at time 0 and its posture layers) into <see cref="portraitBones"/> and
    /// <see cref="portraitAttach"/>; returns the head bone's position in the model's space.</summary>
    Vector3 PosePortrait(in Live l)
    {
        var asset = l.Asset;
        var rig = asset.Rig;
        portraitLayers.Clear();
        if (rig.Layer(content.Database, asset.Idle, asset.Animal) is { } idle) portraitLayers.Add(new ActiveLayer(idle, 0, 1));
        foreach (var (def, time) in asset.Fixed) portraitLayers.Add(new ActiveLayer(def, time, 1));
        if (portraitBones.Length < rig.BoneCount) portraitBones = new Matrix4x4[rig.BoneCount];
        if (portraitAttach.Length < asset.AttachCount) portraitAttach = new Matrix4x4[asset.AttachCount];
        var scratch = rig.Rent();
        try
        {
            scratch.Pose(asset.Shape, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(portraitLayers), portraitBones);
            foreach (var part in asset.Parts)
                if (part.AttachIndex >= 0) portraitAttach[part.AttachIndex] = part.Offset * scratch.BoneTransform(part.Bone);
            return asset.HeadBone >= 0 ? scratch.BoneTransform(asset.HeadBone).Translation : new Vector3(0, asset.Height * 0.85f, 0);
        }
        finally { rig.Return(scratch); }
    }

    /// <summary>
    /// The original's portrait camera (docs/character-renderer.md "Portraits"): it looks at the head bone moved by the race's <c>portrait offset</c> (in the model's
    /// axes, times the character's size) from <c>portrait distance</c> along the model's front, turned about the vertical by up to 45 degrees either way and about
    /// the side axis by a few degrees, both by the character's serial.
    /// </summary>
    static (Vector3 Eye, Vector3 Target) PortraitCamera(in Live l, Vector3 head, uint serial)
    {
        var race = l.Asset.Appearance.Race;
        float scale = l.World.ScaleLength();
        var offset = new Vector3(race.GetFloat("portrait offset x"), race.GetFloat("portrait offset y"), race.GetFloat("portrait offset z"));
        var target = Vector3.Transform(head, l.World) + Vector3.TransformNormal(offset, l.World);
        float yaw = (serial % 500 / 1000f - 0.25f) * MathF.PI, pitch = (serial % 252 / 1000f - 0.083f) * MathF.PI;
        var local = Vector3.Transform(new Vector3(0, 0, race.GetFloat("portrait distance", 10)), Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw) * Quaternion.CreateFromAxisAngle(Vector3.UnitX, pitch));
        var direction = Vector3.TransformNormal(local, l.World) / Math.Max(scale, 1e-3f);
        return (target + direction, target);
    }

    void DisposePortraits()
    {
        portraitColour?.Dispose();
        portraitDepth?.Dispose();
        (portraitColour, portraitDepth, portraitSampled) = (null, null, null);
    }
}
