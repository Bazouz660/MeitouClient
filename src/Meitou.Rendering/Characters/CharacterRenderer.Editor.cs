using System.Numerics;
using Meitou.Rendering.Gpu;
using Silk.NET.Vulkan;

namespace Meitou.Rendering.Characters;

/// <summary>
/// The character editor's view of the character being edited (<see cref="CharacterRenderer.StageRequest"/>): the character by its draw-list key, the size of the picture
/// and the camera as the editor's orbit sets it. <paramref name="Yaw"/> turns the camera round the character (0: in front of it) and <paramref name="Elevation"/> lifts it,
/// both in radians; <paramref name="Distance"/> is from the pivot, which is the body's middle or, with <paramref name="Head"/>, the head;
/// <paramref name="ShiftX"/> moves the pivot sideways on the picture (normalised device units, negative: left).
/// </summary>
public readonly record struct EditorStageRequest(long Key, int Width, int Height, float Yaw, float Elevation, float Distance, bool Head, float FieldOfView, float ShiftX);

/// <summary>
/// The character editor's stage (docs/characters.md "Character editor"): the original draws the edited character into a workspace of its own and puts the plain
/// ground texture behind it; here the character is drawn alone into a window-sized picture, transparent round it, which the interface shows over the ground texture.
/// </summary>
internal sealed unsafe partial class CharacterRenderer
{
    /// <summary>The character to draw into <see cref="StageTexture"/> this frame (the host sets it before <see cref="Update"/>, which also poses the character out of range); null: none.</summary>
    public EditorStageRequest? StageRequest { get; set; }

    Texture? stageColour, stageDepth;
    SampledTexture? stageSampled;

    /// <summary>The stage picture (sRGB, transparent where no character is, upside down as the renderer's targets are), made by <see cref="DrawStage"/>; null before the first.</summary>
    public (Texture Texture, SampledTexture Sampled)? StageTexture => stageColour is null ? null : (stageColour, stageSampled!.Value);

    /// <summary>
    /// Draws <see cref="StageRequest"/>'s character with its live pose (after <see cref="Update"/>, outside any pass). The light is the key light of the portraits, from the camera's
    /// side, above and to the left, plus a hemispheric ambient (the shader's stage branch, a fog distance of -2) (the original's trivial lighting, <c>rtt.hlsl</c>, has one directional light and the environment; its convention is <b>Unknown</b>).
    /// </summary>
    public void DrawStage()
    {
        if (StageRequest is not { } r || r.Width <= 0 || r.Height <= 0) return;
        if (stageColour is null || stageColour.Desc.Width != r.Width || stageColour.Desc.Height != r.Height) MakeStage(r.Width, r.Height);
        int index = -1;
        for (int i = 0; i < liveCount; i++)
            if (live[i].Key == r.Key) { index = i; break; }
        var cmd = Gpu.BeginNative("character stage");
        var target = PassTargets.Of(stageColour, stageDepth);
        cmd.BeginRendering(new RenderingDesc(target.Colour, target.Depth with { Load = AttachmentLoadOp.Clear, Clear = new ClearValue(depthStencil: new ClearDepthStencilValue(1f, 0)) }, target.Width, target.Height));
        Gpu.BeginHostPass(cmd, target, DrawState.Scene(target.Formats));
        var rect = new PixelRect(0, 0, (uint)r.Width, (uint)r.Height);
        Gpu.Clear(cmd, true, default, true, 1, rect);
        Gpu.SetPassViewport(new PixelViewport(0, 0, r.Width, r.Height, 0, 1), rect);
        if (index >= 0)
        {
            ref var l = ref live[index];
            var (eye, pivot) = StageCamera(in l, in r);
            var view = Matrix4x4.CreateLookAt(eye, pivot, Vector3.UnitY);
            var projection = Matrix4x4.CreatePerspectiveFieldOfView(r.FieldOfView, r.Width / (float)r.Height, 0.5f, 400) * Matrix4x4.CreateTranslation(r.ShiftX, 0, 0);
            var toEye = Vector3.Normalize(eye - pivot);
            var left = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, toEye));
            var light = Vector3.Normalize(toEye + 0.7f * Vector3.UnitY + 0.4f * left);
            portraitLive = index;
            try { DrawView(new ViewConstants { ViewProjection = view * projection, Eye = eye, LightDir = light, FogDistance = -2 }, eye, []); }
            finally { portraitLive = -1; }
        }
        Gpu.EndHostPass(cmd);
        cmd.EndRendering();
        Gpu.EndNative(cmd);
    }

    /// <summary>The eye and the pivot it looks at, in the world: the pivot is the middle of the body, or the head bone moved by the race's <c>portrait offset</c> (as the portrait camera does).</summary>
    (Vector3 Eye, Vector3 Pivot) StageCamera(in Live l, in EditorStageRequest r)
    {
        var asset = l.Asset;
        float scale = Math.Max(l.World.ScaleLength(), 1e-3f);
        Vector3 pivot;
        if (r.Head)
        {
            var race = asset.Appearance.Race;
            var offset = new Vector3(race.GetFloat("portrait offset x"), race.GetFloat("portrait offset y"), race.GetFloat("portrait offset z"));
            pivot = Vector3.Transform(PosePortrait(in l) + offset * scale, l.World);
        }
        else pivot = Vector3.Transform(new Vector3(0, asset.Height * 0.5f, 0), l.World);
        var local = Vector3.Transform(new Vector3(0, 0, r.Distance), Quaternion.CreateFromAxisAngle(Vector3.UnitY, r.Yaw) * Quaternion.CreateFromAxisAngle(Vector3.UnitX, -r.Elevation));
        return (pivot + Vector3.TransformNormal(local, l.World) / scale, pivot);
    }

    void MakeStage(int width, int height)
    {
        DisposeStage();
        stageColour = Texture.Create(Gpu, new TextureDesc(Format.R8G8B8A8Srgb, width, height, Use: TextureUse.Sampled | TextureUse.ColourTarget, Name: "character stage"));
        stageDepth = Texture.Create(Gpu, new TextureDesc(Format.D32Sfloat, width, height, Use: TextureUse.DepthTarget, Name: "character stage depth"));
        stageSampled = new SampledTexture(Gpu.Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.Linear, TextureMagFilter.Linear,
            TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge, false, DepthFunction.Lequal, false, 1, false, 0)), stageColour.View(), stageColour.Image);
    }

    void DisposeStage()
    {
        stageColour?.Dispose();
        stageDepth?.Dispose();
        (stageColour, stageDepth, stageSampled) = (null, null, null);
    }
}
