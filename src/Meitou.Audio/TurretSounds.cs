using System.Numerics;

namespace Meitou.Audio;

/// <summary>
/// A turret part that turns to the target (a BUILDING_PART with <c>rotation function</c> ROTATION_TARGET): <paramref name="Parent"/> is the index of the part it hangs from (-1: the building),
/// <paramref name="Yaw"/> says whether it turns about its Y axis (<c>rotation axis</c> Y: the hinge) or pitches about X (the gun), <paramref name="Offset"/> is its place in the parent's frame
/// (<c>offset X/Y/Z</c>, before the building's scale).
/// </summary>
public readonly record struct TurretPart(int Parent, bool Yaw, Vector3 Offset);

/// <summary>A turret building as the aim needs it: where it stands and faces (0 along +Z, turning towards +X), its <c>scale</c>, its gun's <c>aim speed</c> and its turning parts in record order.</summary>
public sealed record TurretMount(Vector3 Position, float Yaw, float Scale, float AimSpeed, IReadOnlyList<TurretPart> Parts);

/// <summary>A manned turret this frame: the mount and the point it should aim at (null: keep the last).</summary>
public readonly record struct TurretAim(string Key, TurretMount Mount, Vector3? Target);

/// <summary>
/// The turrets' movement sounds (docs/audio.md "Other world-side events"). The exe turns a turret's parts toward the aim point every frame (<c>RotatingEnt::vfunc_2</c>) and
/// posts <c>Turret_Mvt_Start</c> / <c>Turret_Mvt_Stop</c> from <c>TurretBuilding::vfunc_28</c> by how far they still had to turn. This keeps each manned turret's turning parts
/// (display only: nothing draws them or reads them back), turns them by the exe's rule and posts the same events. Verified in the exe unless marked:
/// <list type="bullet">
/// <item>Each part, in order, takes the aim point into its own frame (its parent's orientation, then its own turn so far): a yaw part's error is <c>atan2(x, z)</c>, a pitch part's <c>atan2(-y, √(x² + z²))</c>,
/// and it turns by that error limited to ±<c>aim speed</c> × (building efficiency, 1 unharmed) × (game-speed-scaled frame time) radians; the next part sees the first one's new turn.</item>
/// <item>The largest error of the frame (before turning) counts as movement when over 0.1 degrees. With the counter at 0, movement posts <c>Turret_Mvt_Start</c> and sets the counter to 8; the counter then
/// counts down every frame and posts <c>Turret_Mvt_Stop</c> when it reaches 0. So a turret that keeps turning starts and stops again every 8 frames (neither action fades; the exe's frames).</item>
/// </list>
/// Meitou: the object is the turret's own, placed at the building (the exe registers the building's object when the gun first asks for it, so before a first shot it posts on object 0: <b>engine choice</b>);
/// a start more than <see cref="Range"/> from the eye is dropped (<b>engine choice</b>, as the doors). The wall bonus to the turning speed is not modelled (walls are not buildings here).
/// </summary>
public sealed class TurretSounds(WwisePlayer player)
{
    /// <summary>The first game object id of the turrets (above the doors').</summary>
    public const ulong FirstObject = 1UL << 41;
    /// <summary>Turrets farther than this from the eye start no movement sound (<b>engine choice</b>: the longest hearing range).</summary>
    public const float Range = 3000;
    /// <summary>The frames the exe lets a started movement sound run before it stops it.</summary>
    public const int RunFrames = 8;
    /// <summary>The movement over which the sound starts, degrees.</summary>
    public const float MovingDegrees = 0.1f;

    sealed class Turret(TurretMount mount)
    {
        public TurretMount Mount = mount;
        public float[] Angles = new float[mount.Parts.Count];
        public Vector3? Target;
        public int Counter;
        public ulong Object;
    }

    readonly Dictionary<string, Turret> turrets = new(StringComparer.Ordinal);
    ulong next = FirstObject;

    /// <summary>Events posted since the world was set (statistics).</summary>
    public int Posted { get; private set; }

    /// <summary>Turrets with a state (ever manned in this world).</summary>
    public int Count => turrets.Count;

    /// <summary>
    /// One frame. <paramref name="manned"/> are the turrets with a gunner now (a target assigns the aim point, which stays when the gunner goes); every turret that has a state turns toward its aim point
    /// by <paramref name="seconds"/> of game time (the frame time times the game speed: 0 while paused) and counts its sound. <paramref name="eye"/> is the camera eye.
    /// </summary>
    public void Update(IEnumerable<TurretAim> manned, float seconds, Vector3 eye)
    {
        foreach (var m in manned)
        {
            if (!turrets.TryGetValue(m.Key, out var t)) turrets[m.Key] = t = new Turret(m.Mount);
            else if (!ReferenceEquals(t.Mount, m.Mount)) (t.Mount, t.Angles) = (m.Mount, new float[m.Mount.Parts.Count]);
            if (m.Target is { } target) t.Target = target;
        }
        foreach (var t in turrets.Values)
        {
            float worst = t.Target is { } aim ? Turn(t, aim, t.Mount.AimSpeed * seconds) : -99;
            if (t.Counter == 0 && worst > MovingDegrees)
            {
                Start(t, eye);
                t.Counter = RunFrames;
            }
            if (t.Counter > 0 && --t.Counter == 0) Stop(t);
        }
    }

    /// <summary>
    /// Turns every part toward <paramref name="aim"/> by at most <paramref name="step"/> radians (<c>RotatingEnt::vfunc_2</c>, with the helpers <c>FUN_1409b2fc0</c> and <c>FUN_1409b3090</c> for the errors).
    /// Returns the largest error in degrees before turning, or -99 for a mount without parts.
    /// </summary>
    static float Turn(Turret t, Vector3 aim, float step)
    {
        float worst = -99;
        var parts = t.Mount.Parts;
        for (int k = 0; k < parts.Count; k++)
        {
            var (position, orientation) = Derived(t, k);
            var local = Vector3.Transform(aim - position, Quaternion.Inverse(orientation));
            float error = parts[k].Yaw ? MathF.Atan2(local.X, local.Z) : MathF.Atan2(-local.Y, MathF.Sqrt(local.X * local.X + local.Z * local.Z));
            worst = MathF.Max(worst, MathF.Abs(error) * (180 / MathF.PI));
            t.Angles[k] += Math.Clamp(error, -step, step);
        }
        return worst;
    }

    /// <summary>The world place and orientation of part <paramref name="k"/>'s node: the building's, then each part down the chain (its offset in the parent's frame, scaled, and its own turn about its axis).</summary>
    static (Vector3 Position, Quaternion Orientation) Derived(Turret t, int k)
    {
        var part = t.Mount.Parts[k];
        var (position, orientation) = part.Parent < 0 ? (t.Mount.Position, Quaternion.CreateFromAxisAngle(Vector3.UnitY, t.Mount.Yaw)) : Derived(t, part.Parent);
        return (position + Vector3.Transform(part.Offset * t.Mount.Scale, orientation),
            orientation * Quaternion.CreateFromAxisAngle(part.Yaw ? Vector3.UnitY : Vector3.UnitX, t.Angles[k]));
    }

    void Start(Turret t, Vector3 eye)
    {
        if (Vector3.DistanceSquared(t.Mount.Position, eye) > Range * Range) return;
        if (t.Object == 0)
        {
            t.Object = next++;
            player.RegisterObject(t.Object);
            player.SetPosition(t.Object, t.Mount.Position);
        }
        player.Post("Turret_Mvt_Start", t.Object);
        Posted++;
    }

    void Stop(Turret t)
    {
        if (t.Object == 0) return;
        player.Post("Turret_Mvt_Stop", t.Object);
        Posted++;
    }

    /// <summary>A new world or the main menu: every object is let go.</summary>
    public void Clear()
    {
        foreach (var t in turrets.Values)
            if (t.Object != 0) player.UnregisterObject(t.Object);
        turrets.Clear();
        Posted = 0;
    }
}
