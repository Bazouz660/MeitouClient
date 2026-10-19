using InternalFormat = Meitou.Rendering.Gpu.InternalFormat;
using System.Numerics;
using Meitou.Rendering.Gpu;
using Silk.NET.Vulkan;

namespace Meitou.Rendering.Characters;

/// <summary>The character under the mouse drawn alone into a mask of its own, for the hover outline (<see cref="HoverOutlineRenderer"/>, docs/game/ui-input.md "Hover outline").</summary>
internal sealed unsafe partial class CharacterRenderer
{
    Texture? hoverColour, hoverDepth;

    /// <summary>
    /// This frame's mask (null when nothing is outlined): its colour target (alpha 1 where the character is), its depth from the projection
    /// <c>Near</c>..<c>Far</c>, and the character's bounding sphere.
    /// </summary>
    public (Texture Colour, Texture Depth, float Near, float Far, Vector3 Centre, float Radius)? HoverMask { get; private set; }

    /// <summary>
    /// Draws the character <paramref name="key"/> (null: none) alone, in its live pose and the picture's levels, through the colour pass (it writes alpha 1) into a
    /// <paramref name="width"/> × <paramref name="height"/> mask seen from <paramref name="eye"/> along <paramref name="view"/> (unjittered). The projection's planes hug
    /// the character, so its depth stays precise. Nothing for a character not drawn in the world (beyond the draw distance, or hidden by the interior view).
    /// Called after <see cref="Update"/> and before the world's colour pass, whose statistics then stand.
    /// </summary>
    public void DrawHoverMask(long? key, int width, int height, Matrix4x4 view, float fieldOfView, Vector3 eye)
    {
        HoverMask = null;
        if (key is not { } wanted || width <= 0 || height <= 0) return;
        int index = -1;
        for (int i = 0; i < liveCount; i++)
            if (live[i].Key == wanted) { index = i; break; }
        if (index < 0 || live[index].PortraitOnly) return;
        ref var l = ref live[index];
        if (hoverColour is null || hoverColour.Desc.Width != width || hoverColour.Desc.Height != height)
        {
            hoverColour?.Dispose(); hoverDepth?.Dispose();
            // The portraits' formats, so the mask shares their pipelines.
            hoverColour = Texture.Create(Gpu, new TextureDesc(InternalFormat.Srgb8Alpha8, width, height, Use: TextureUse.Sampled | TextureUse.ColourTarget, Name: "hover mask"));
            hoverDepth = Texture.Create(Gpu, new TextureDesc(InternalFormat.DepthComponent24, width, height, Use: TextureUse.Sampled | TextureUse.DepthTarget, Name: "hover mask depth"));
        }
        float distance = Vector3.Distance(eye, l.Centre);
        float near = Math.Max(0.25f, distance - l.Radius * 2), far = distance + l.Radius * 2;
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(fieldOfView, width / (float)height, near, far);
        var cmd = Gpu.BeginNative("hover mask");
        var target = PassTargets.Of(hoverColour, hoverDepth);
        cmd.BeginRendering(new RenderingDesc(
            target.Colour with { Load = AttachmentLoadOp.Clear, Clear = new ClearValue(new ClearColorValue(0f, 0f, 0f, 0f)) },
            target.Depth with { Load = AttachmentLoadOp.Clear, Clear = new ClearValue(depthStencil: new ClearDepthStencilValue(1f, 0)) }, target.Width, target.Height));
        Gpu.BeginHostPass(cmd, target, DrawState.Scene(target.Formats));
        portraitLive = index;
        try { DrawView(new ViewConstants { ViewProjection = view * projection, Eye = eye, LightDir = Vector3.UnitY }, eye, []); }
        finally { portraitLive = -1; }
        Gpu.EndHostPass(cmd);
        cmd.EndRendering();
        Gpu.EndNative(cmd);
        HoverMask = (hoverColour, hoverDepth!, near, far, l.Centre, l.Radius);
    }

    void DisposeHover()
    {
        hoverColour?.Dispose();
        hoverDepth?.Dispose();
        (hoverColour, hoverDepth, HoverMask) = (null, null, null);
    }
}
