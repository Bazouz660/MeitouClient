using System.Numerics;
using Meitou.Audio;
using Meitou.Data.World;

namespace Meitou.Game;

/// <summary>
/// The world's own sounds (docs/audio.md "Interiors" and "Point emitters"): the building under the camera's pivot (the interior ambience and the reverb switch) and the sound
/// emitters of buildings (campfires, torches, ovens, water tanks, …) that play while the camera eye is near. Driven each frame from <see cref="UpdateAudio"/>.
/// </summary>
sealed partial class GameHost
{
    AudioSystem? worldAudioFor;
    InteriorSounds? interiorSounds;
    WorldEmitters? worldEmitters;
    // The world the emitters were made for, the level's emitters (read on a worker: every building with sounds, positioned on the heightmap) and whether they were added.
    object? emittersFor;
    Task<List<PlacedEmitter>>? levelEmitters;
    bool levelEmittersAdded;
    // The player-built buildings the emitters follow: what each was when its emitter was decided.
    readonly Dictionary<string, (string Record, bool Complete, Vector3 Position)> runtimeEmitters = new(StringComparer.Ordinal);
    readonly HashSet<string> runtimeSeen = new(StringComparer.Ordinal);

    /// <summary>Once a frame, after the ambience: <paramref name="pivot"/> is the camera's target (the game tests the building under it), <paramref name="eye"/> the camera's position (the emitters' distance).</summary>
    void UpdateWorldAudio(AudioSystem sound, Vector3 pivot, Vector3 eye)
    {
        if (!ReferenceEquals(worldAudioFor, sound))
        {
            worldAudioFor = sound;
            interiorSounds = new InteriorSounds(sound.Player);
            worldEmitters = new WorldEmitters(sound.Player);
            ForgetEmitters();
        }

        // The building whose indoor volume lies under the pivot, whatever the pivot's height (a ray cast straight down from far above).
        var volume = ActiveObjects?.InteriorUnder(pivot.X, pivot.Z);
        interiorSounds!.Update(volume is null ? null : new InteriorHit(volume.PlacementId, volume.Ambience, volume.Integrity, volume.Cavernous));

        UpdateEmitters();
        worldEmitters!.Update(eye);
    }

    /// <summary>Keeps the emitters in step with the world's buildings: the level's once read, the player's as their sites change.</summary>
    void UpdateEmitters()
    {
        var emitters = worldEmitters!;
        if (!ReferenceEquals(emittersFor, session.World))
        {
            emitters.Clear();
            ForgetEmitters();
            emittersFor = session.World;
            levelEmitters = scene.Objects is { } objects ? Task.Run(objects.Emitters) : null;
        }
        if (!levelEmittersAdded && levelEmitters is { IsCompleted: true } read)
        {
            levelEmittersAdded = true;
            if (read.IsCompletedSuccessfully)
            {
                foreach (var emitter in read.Result) emitters.Add(emitter);
                Console.WriteLine($"audio     {read.Result.Count} sound emitters in the level's buildings");
            }
            else
                Console.WriteLine($"audio     no building emitters: {read.Exception?.GetBaseException().Message}");
        }
        if (scene.Database is not { } db) return;

        runtimeSeen.Clear();
        foreach (var site in session.CurrentSnapshot.Buildings)
        {
            runtimeSeen.Add(site.Id);
            var now = (site.Record, site.Complete, site.Position);
            if (runtimeEmitters.TryGetValue(site.Id, out var before) && before == now) continue;
            runtimeEmitters[site.Id] = now;
            emitters.Remove(site.Id);
            // A player-built building gets its emitter when it is complete (the game's completion step starts it).
            if (site.Complete && db.Find(site.Record) is { } record && BuildingEmitters.For(db, record, site.Id, site.Position, site.Rotation) is { } emitter)
                emitters.Add(emitter);
        }
        if (runtimeEmitters.Count > runtimeSeen.Count)
            foreach (string id in runtimeEmitters.Keys.Where(id => !runtimeSeen.Contains(id)).ToArray())
            {
                runtimeEmitters.Remove(id);
                emitters.Remove(id);
            }
    }

    void ForgetEmitters()
    {
        emittersFor = null;
        levelEmitters = null;
        levelEmittersAdded = false;
        runtimeEmitters.Clear();
    }

    /// <summary>The main menu (Meitou's choice, see <see cref="AudioMainMenu"/>): the interior ambience ends and the emitters stop.</summary>
    void ResetWorldAudio()
    {
        interiorSounds?.Silence();
        worldEmitters?.Clear();
        ForgetEmitters();
    }

    /// <summary>One statistics line: where the pivot is and how many emitters play; empty without audio.</summary>
    string WorldAudioStats() => interiorSounds is null || worldEmitters is null ? "" :
        $"{(interiorSounds.Playing ? $"indoors ({interiorSounds.Indoors})" : interiorSounds.Inside ? "in a building without an ambience" : "outdoors")}, {worldEmitters.Started} of {worldEmitters.Count} emitters playing" +
        (worldEmitters.NearestName is { } near ? $", nearest {near} {worldEmitters.NearestDistance:0} units from the eye" : "");
}
