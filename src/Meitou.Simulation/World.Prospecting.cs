using Meitou.Simulation.Production;

namespace Meitou.Simulation;

public sealed partial class World
{
    /// <summary>The last prospecting player 0's characters finished (null: none yet; <see cref="PlayerSession.Prospected"/>). Read by the host's window; never part of the state hash.</summary>
    public ProspectReport? Prospected => Player.Local.Prospected;
}
