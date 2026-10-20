using System.Numerics;

namespace Meitou.Simulation;

public sealed partial class World
{
    /// <summary>Prepares nearby population at a frame boundary and publishes it without running AI, movement, combat or game time. A replica has nothing to load: true.</summary>
    public bool PreparePopulation(Vector3 cameraFocus)
    {
        if (IsReplica) return true;
        if (Characters.Phase != TablePhase.Outside) throw new InvalidOperationException("population loading requires a tick boundary");
        var population = systems.OfType<PopulationSystem>().FirstOrDefault();
        if (population is null) return true;
        stateStamp++;
        bool ready;
        Characters.BeginTick();
        Characters.Phase = TablePhase.SlowWorld;
        try { ready = population.PrepareLoading(this, cameraFocus); }
        finally
        {
            Characters.Phase = TablePhase.Outside;
            Characters.EndTick();
        }
        if (Settings.PublishSnapshots)
        {
            Publish();
            PreviousSnapshot = Snapshot;
        }
        return ready;
    }
}
