using System.Numerics;
using Meitou.Audio;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Simulation;

namespace Meitou.Game;

/// <summary>
/// The turrets' movement sounds (docs/audio.md "Other world-side events"): a town turret a character stands at the gun of (<see cref="CharacterSnapshot.Turret"/>) aims at the nearest hostile character
/// in its gun's range and its parts turn by the exe's rule in <see cref="TurretSounds"/>, which posts <c>Turret_Mvt_Start</c> / <c>Turret_Mvt_Stop</c>. Display only: nothing here is read by the simulation, and nothing draws the turn
/// (the renderer has no rotating parts). Driven each frame from <see cref="UpdateAudio"/>.
/// </summary>
sealed partial class GameHost
{
    /// <summary>Turrets farther than this from the eye are not looked after (the movement sound is dropped beyond <see cref="TurretSounds.Range"/> anyway).</summary>
    const float TurretAudioRadius = 4000;

    AudioSystem? turretAudioFor;
    TurretSounds? turretSounds;
    World? turretSoundsWorld;
    // Each turret's mount and its gun's range, by the turret's key (null: its records are missing).
    readonly Dictionary<string, (TurretMount Mount, float Range)?> turretMounts = new(StringComparer.Ordinal);
    readonly List<TurretAim> mannedTurrets = [];

    void UpdateTurretAudio(AudioSystem sound, Vector3 eye, double realSeconds)
    {
        if (!ReferenceEquals(turretAudioFor, sound))
        {
            turretAudioFor = sound;
            turretSounds = new TurretSounds(sound.Player);
            turretSoundsWorld = null;
        }
        var sounds = turretSounds!;
        if (!ReferenceEquals(turretSoundsWorld, session.World))
        {
            sounds.Clear();
            turretMounts.Clear();
            turretSoundsWorld = session.World;
        }
        if (scene.Database is not { } db) return;
        mannedTurrets.Clear();
        var characters = session.CurrentSnapshot.Characters;
        foreach (var gunner in characters)
        {
            if (gunner.Turret is not { } post || Vector3.DistanceSquared(post.Position, eye) > TurretAudioRadius * TurretAudioRadius) continue;
            if (!turretMounts.TryGetValue(post.Key, out var made)) turretMounts[post.Key] = made = MountOf(db, post);
            if (made is not { } known) continue;
            mannedTurrets.Add(new TurretAim(post.Key, known.Mount, TargetOf(gunner, post.Position, known.Range, characters)));
        }
        // The turning is by game time: the frame time times the game speed (the exe's frame time is scaled by it; 0 while paused).
        sounds.Update(mannedTurrets, (float)(realSeconds * session.Simulation.Speed), eye);
    }

    /// <summary>
    /// The point the gunner aims at: the nearest living, free hostile character within the gun's range, 5 units above its place (the ranged controller aims 5 up at its target; <c>FUN_140438c70</c>).
    /// Which target the exe chooses (<c>FUN_140437f40</c>) is not traced: the nearest is an <b>engine choice</b>. Null: none (the turret keeps its last aim).
    /// </summary>
    Vector3? TargetOf(CharacterSnapshot gunner, Vector3 turret, float range, IReadOnlyList<CharacterSnapshot> characters)
    {
        if (popData is not { } data || !data.Relations.Contains(gunner.Faction)) return null;
        CharacterSnapshot? nearest = null;
        float best = range * range;
        foreach (var c in characters)
        {
            if (c.Id == gunner.Id || !data.Relations.Contains(c.Faction) || !data.Relations.IsHostile(gunner.Faction, c.Faction)) continue;
            if (c.Body is { Dead: true } or { Unconscious: true } || !c.CarriedBy.IsNone) continue;
            float d2 = Vector3.DistanceSquared(c.Position, turret);
            if (d2 > best) continue;
            best = d2;
            nearest = c;
        }
        return nearest is null ? null : nearest.Position + new Vector3(0, 5, 0);
    }

    /// <summary>The mount of a turret from its records: the building's <c>scale</c>, its rotating parts, the gun's <c>aim speed</c> and <c>range</c> (fcs.def default 1000).</summary>
    static (TurretMount, float)? MountOf(GameDatabase db, MannedTurret post)
    {
        if (db.Find(post.BuildingId) is not { } building || db.Find(post.GunId) is not { } gun) return null;
        float scale = building.GetFloat("scale", 1);
        var parts = new List<TurretPart>();
        CollectParts(db, building, -1, Vector3.Zero, parts, 0);
        return (new TurretMount(post.Position, post.Yaw, scale > 0 ? scale : 1, gun.GetFloat("aim speed", 1), parts), gun.GetInt("range", 1000));
    }

    /// <summary>
    /// The parts that turn to the target (<c>rotation function</c> ROTATION_TARGET, 5), depth first like the exe's part list: each hangs from the nearest such ancestor, with the offsets of the parts between
    /// carried along. A part whose <c>rotation axis</c> is Y yaws; any other pitches (<c>RotatingEnt::vfunc_2</c>).
    /// </summary>
    static void CollectParts(GameDatabase db, GameRecord holder, int parent, Vector3 carried, List<TurretPart> parts, int depth)
    {
        if (depth > 8) return;
        foreach (var reference in holder.GetReferences("parts"))
        {
            if (db.Find(reference.TargetStringId) is not { Type: FcsRecordType.BUILDING_PART } part) continue;
            var offset = carried + (part.GetBool("is for position marker") ? Vector3.Zero : new Vector3(part.GetFloat("offset X"), part.GetFloat("offset Y"), part.GetFloat("offset Z")));
            if (part.GetInt("rotation function") == 5)
            {
                parts.Add(new TurretPart(parent, part.GetInt("rotation axis", 2) == 1, offset));
                CollectParts(db, part, parts.Count - 1, Vector3.Zero, parts, depth + 1);
            }
            else CollectParts(db, part, parent, offset, parts, depth + 1);
        }
    }

    /// <summary>The main menu: the turrets' objects end.</summary>
    void ResetTurretAudio()
    {
        turretSounds?.Clear();
        turretMounts.Clear();
        turretSoundsWorld = null;
    }
}
