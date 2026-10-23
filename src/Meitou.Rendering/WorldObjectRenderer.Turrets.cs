using System.Numerics;
using Meitou.Data.World;

namespace Meitou.Rendering;

/// <summary>
/// How far a turret's turning parts have turned (<see cref="WorldObjectRenderer.Turrets"/>): <see cref="PartIds"/> are the BUILDING_PART records of its joints (<see cref="TurnJoint.PartId"/>), <see cref="Angles"/>
/// the turn of each in radians (the simulation's <c>TurretRig</c> angles, same order). The host updates the angles in place and raises <see cref="Version"/> when they change.
/// </summary>
public sealed class TurretPose(string[] partIds)
{
    public readonly string[] PartIds = partIds;
    public readonly float[] Angles = new float[partIds.Length];
    /// <summary>Starts at 1; instances posed at another version are posed again.</summary>
    public int Version = 1;

    /// <summary>The turn of the joint of part <paramref name="partId"/>; 0 for a part this turret does not turn.</summary>
    public float AngleOf(string partId)
    {
        int k = Array.IndexOf(PartIds, partId);
        return k < 0 ? 0 : Angles[k];
    }
}

public sealed partial class WorldObjectRenderer
{
    /// <summary>
    /// The turn of each turret that has turned, by placement id (a town building's key, a player site's id): the parts of its meshes with <see cref="PlacedMesh.Joints"/> are drawn turned about their joints
    /// (docs/render-objects.md "Turrets"). A turret missing is drawn as placed. Read on the render thread; the host replaces or edits it.
    /// </summary>
    public IReadOnlyDictionary<string, TurretPose>? Turrets { get; set; }

    /// <summary>Turns a turret part to its pose: its placement, then a turn about each joint above it and its own, the innermost first (the joints are stored outermost first).</summary>
    void PoseTurret(ObjectStreamer.Instance inst, GpuObjectMesh gpu)
    {
        var pose = Turrets is { } turrets && turrets.TryGetValue(inst.Placed.PlacementId, out var found) ? found : null;
        int version = pose?.Version ?? 0;
        if (version == inst.TurretStamp) return;
        inst.TurretStamp = version;
        var m = inst.Placed.Transform;
        if (pose is not null)
        {
            var joints = inst.Placed.Joints!;
            for (int k = joints.Count - 1; k >= 0; k--)
            {
                float angle = pose.AngleOf(joints[k].PartId);
                if (angle == 0) continue;
                m *= Matrix4x4.CreateTranslation(-joints[k].Pivot) * Matrix4x4.CreateFromAxisAngle(joints[k].Axis, angle) * Matrix4x4.CreateTranslation(joints[k].Pivot);
            }
        }
        inst.Transform = m;
        inst.Centre = Vector3.Transform(gpu.Centre, m);
    }
}
