using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Meitou.Content;
using Meitou.Engine;
using Meitou.Engine.Cameras;
using Meitou.Engine.Input;
using Meitou.Engine.Time;
using Meitou.Rendering;
using Meitou.Rendering.Characters;
using Meitou.Simulation;
using Meitou.Rendering.Gpu;
using Meitou.Rendering.Display;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using EngineKey = Meitou.Engine.Input.Key;
using EngineButton = Meitou.Engine.Input.MouseButton;
using SilkKey = Silk.NET.Input.Key;
using SilkButton = Silk.NET.Input.MouseButton;

namespace Meitou.Game;

/// <summary>The draw list from the snapshots, and markers for what has nothing to draw.</summary>
sealed partial class GameHost
{
    /// <summary>Tells the world where the camera looks, when it has moved enough to matter (the zones follow it).</summary>
    void SendFocus(Vector3? at = null)
    {
        if (IsDuel) return;   // the duel's worlds keep the focus on its spot (DuelSetup), wherever the camera flies
        var target = at ?? session.Camera.Current.Target;   // the cinema's camera while it holds the view
        if (sentFocus && Vector3.DistanceSquared(target, lastFocus) < 50 * 50) return;
        session.World.Commands.Enqueue(new Meitou.Simulation.FocusCommand(target) { Tick = session.World.Tick });
        lastFocus = target;
        sentFocus = true;
    }

    void IndexPrevious()
    {
        var previous = session.PreviousSnapshot;
        if (ReferenceEquals(indexedFor, previous)) return;
        previousById.Clear();
        foreach (var c in previous.Characters) previousById[c.Id] = c;
        indexedFor = previous;
    }

    /// <summary>Where a character is drawn: between the last two snapshots, by the motion alpha (a replica may extrapolate; a carried body on its carrier's left shoulder).</summary>
    Vector3 DrawnPosition(CharacterSnapshot c)
    {
        IndexPrevious();
        var at = previousById.TryGetValue(c.Id, out var before) ? Vector3.Lerp(before.Position, c.Position, session.MotionAlpha) : c.Position;
        return at + CarriedOffset(c, c.Yaw);
    }

    /// <summary>A carried body has its carrier's position; it is drawn at <see cref="Meitou.Simulation.Carrying.CarryPose"/> from it, where its ragdoll is posed too.</summary>
    static Vector3 CarriedOffset(CharacterSnapshot c, float yaw) => c.CarriedBy.IsNone ? Vector3.Zero : Meitou.Simulation.Carrying.CarryPose.Offset(yaw);

    /// <summary>Fills the renderer's list from the last two snapshots: positions and facings by the motion alpha, animation and ragdolls by the simulation's alpha.</summary>
    void FillDrawList(CharacterDrawList list)
    {
        long f0 = Stopwatch.GetTimestamp();
        try { FillDrawListCore(list); FillPortraits(); }
        finally { fillTotal += Stopwatch.GetElapsedTime(f0).TotalMilliseconds; fillCalls++; }
    }


    /// <summary>A character's key in the draw list: stable while it exists, new for every world (the renderer keeps its poses by it).</summary>
    long DrawKey(CharacterId id) => (((long)id.Slot << 32 | (uint)id.Generation) + 1) | (long)worldEpoch << 56;

    /// <summary>The portraits for the renderer to draw this frame: the squad bar's and the squads screen's members in their cells (docs/character-renderer.md "Portraits").</summary>
    void FillPortraits()
    {
        if (gpu.Characters is not { } characters) return;
        characters.PortraitRequests.Clear();
        if (player.TitleOpen) return;
        // The serial turns each portrait's camera.
        if (player.UiVisible)
        {
            var members = player.PortraitMembers();
            for (int i = 0; i < members.Count; i++)
                characters.PortraitRequests.Add(new PortraitRequest(DrawKey(members[i].Id), i, members[i].PortraitSerial));
        }
        if (player.DialoguePortraitCharacters() is var (npc, talker))
        {
            if (!npc.IsNone) characters.PortraitRequests.Add(new PortraitRequest(DrawKey(npc), PlayerInterface.DialogueNpcCell, player.PortraitSerial(npc)));
            if (!talker.IsNone) characters.PortraitRequests.Add(new PortraitRequest(DrawKey(talker), PlayerInterface.DialoguePlayerCell, player.PortraitSerial(talker)));
        }
    }

    void FillDrawListCore(CharacterDrawList list)
    {
        var current = session.CurrentSnapshot;
        IndexPrevious();
        float alpha = session.SimulationAlpha, motion = session.MotionAlpha;
        // A replica's snapshots can be several ticks apart, so a looping clip that wrapped between them is unwrapped rather than shown at its new time.
        bool unwrap = session.Mode == SessionMode.Replica;
        // The character editor is a scene of its own (GameHost.Editor.cs): only the character being edited is posed and drawn.
        var editing = player.EditorFocus?.Character;
        foreach (var c in current.Characters)
        {
            if (c.Appearance is null || editing is { } edited && c.Id != edited) continue;
            var position = c.Position;
            float yaw = c.Yaw;
            CharacterSnapshot? before = null;
            if (previousById.TryGetValue(c.Id, out var found))
            {
                before = found;
                position = Vector3.Lerp(found.Position, c.Position, motion);
                yaw = Meitou.Engine.Time.Interp.LerpAngle(found.Yaw, c.Yaw, motion);
            }
            position += CarriedOffset(c, yaw);
            var poses = new CharacterPose[c.Animations.Count];
            for (int i = 0; i < poses.Length; i++)
            {
                var layer = c.Animations[i];
                float time = layer.Time, weight = layer.Weight;
                // Between ticks the clip time and weight move on from the last snapshot (a wrap of a looping clip just shows the new time, but on a replica it runs on through the end).
                if (before is not null)
                    foreach (var old in before.Animations)
                        if (old.Name == layer.Name)
                        {
                            if (layer.Time >= old.Time) time = old.Time + (layer.Time - old.Time) * alpha;
                            else if (unwrap && layer.Length > 0)
                            {
                                time = old.Time + (layer.Time + layer.Length - old.Time) * alpha;
                                if (time >= layer.Length) time -= layer.Length;
                            }
                            weight = old.Weight + (layer.Weight - old.Weight) * alpha;
                            break;
                        }
                poses[i] = new CharacterPose(layer.Name, time, weight);
            }
            var look = player.EditorPreview(c.Id, c.Appearance) ?? c.Appearance;
            var glide = player.EditorBlend(c.Id);   // after the preview, which may just have swapped the look
            list.Add(new CharacterInstance(DrawKey(c.Id), look, position, yaw, poses)
            {
                WeaponDrawn = c.WeaponDrawn, Scale = c.Scale, Hidden = aboveFloor.Contains(c.Id), FullRateAnimation = c.IsPlayer,
                Bones = Blend(before?.Ragdoll, c.Ragdoll, alpha),
                BlendFrom = glide?.From, Blend = glide?.Blend ?? 0,
            });
            // The editor's next preview look, listed hidden so the renderer prepares it while the last one is still shown.
            if (player.EditorPending(c.Id) is { } pending)
                list.Add(new CharacterInstance(DrawKey(c.Id) ^ (1L << 55), pending, position, yaw, poses) { Hidden = true });
        }
    }

    /// <summary>A ragdoll's bones between the last two ticks: rotations by nlerp, root positions and the get-up weight linearly; a bone only in the newer one as it is.</summary>
    static Meitou.Data.Characters.BonePose? Blend(Meitou.Data.Characters.BonePose? from, Meitou.Data.Characters.BonePose? to, float alpha)
    {
        if (to is null || from is null || from.Set.Length != to.Set.Length) return to;
        var pose = new Meitou.Data.Characters.BonePose(to.Set.Length) { Weight = from.Weight + (to.Weight - from.Weight) * alpha };
        for (int h = 0; h < to.Set.Length; h++)
        {
            if (!to.Set[h]) continue;
            pose.Set[h] = true;
            if (!from.Set[h]) { (pose.Rotation[h], pose.Position[h]) = (to.Rotation[h], to.Position[h]); continue; }
            var b = to.Rotation[h];
            if (Quaternion.Dot(from.Rotation[h], b) < 0) b = -b;
            pose.Rotation[h] = Quaternion.Normalize(Quaternion.Lerp(from.Rotation[h], b, alpha));
            pose.Position[h] = Vector3.Lerp(from.Position[h], to.Position[h], alpha);
        }
        return pose;
    }

    /// <summary>Squares over the characters that have no drawing (a failed build, a world without looks), coloured by faction.</summary>
    void DrawMarkers(DebugOverlay overlay, int width, int height)
    {
        var view = camera.View * camera.Projection(width / (float)Math.Max(height, 1), camera.Near, camera.ViewDistance);
        var current = session.CurrentSnapshot;
        foreach (var c in current.Characters)
        {
            if (c.Appearance is not null) continue;
            var clip = Vector4.Transform(new Vector4(c.Position + new Vector3(0, 10, 0), 1), view);
            if (clip.W <= 0.1f) continue;
            float x = (clip.X / clip.W * 0.5f + 0.5f) * width, y = (0.5f - clip.Y / clip.W * 0.5f) * height;
            if (x < -10 || y < -10 || x > width + 10 || y > height + 10) continue;
            float hue = c.Faction < 0 ? 0 : (c.Faction * 0.618034f) % 1f;
            var colour = new Vector4(0.5f + 0.5f * MathF.Sin(hue * MathF.Tau), 0.5f + 0.5f * MathF.Sin(hue * MathF.Tau + 2.1f), 0.5f + 0.5f * MathF.Sin(hue * MathF.Tau + 4.2f), 1);
            overlay.Rect(x - 3, y - 3, x + 3, y + 3, colour);
        }
        overlay.Flush(width, height);
    }
}
