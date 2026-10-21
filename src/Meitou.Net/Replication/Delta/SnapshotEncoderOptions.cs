using Meitou.Data;
using Meitou.Data.Gameplay;
using Meitou.Simulation;

namespace Meitou.Net.Replication;

/// <summary>
/// How a <see cref="ClientReplicator"/> paces and bounds one client's snapshot stream (docs/net.md, "Interest, delta, replicator and mirror"):
/// <see cref="BytesPerSecond"/> / <see cref="SendHz"/> is the byte budget of one send (doubled for the first 2 s after a reset, so a joiner fills its
/// view quickly); a delta uses an acked baseline at most <see cref="BaselineWindow"/> sends old; <see cref="History"/> sent parts are kept for acks;
/// a header-only part goes out at <see cref="KeepAliveHz"/> when nothing changed.
/// </summary>
public sealed record SnapshotEncoderOptions
{
    public int SendHz { get; init; } = 20;
    public int BytesPerSecond { get; init; } = 262_144;
    public int BaselineWindow { get; init; } = 60;
    public int History { get; init; } = 128;
    public int KeepAliveHz { get; init; } = 5;
}

/// <summary>What a replica needs to rebuild the host's snapshots: the shared data, its animation library (same indices as the host's) and a look source.</summary>
public sealed record ReplicaCatalog(GameDatabase Database, AnimationLibrary Animations, IAppearanceSource Appearances);
