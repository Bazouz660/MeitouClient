using System.Numerics;
using Meitou.Data.Characters;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.Save;
using Meitou.Net;
using Meitou.Net.Replication;
using Meitou.Simulation;
using Meitou.Simulation.Production;

namespace Meitou.Tests.Net;

/// <summary>The snapshot core codecs: <see cref="CharacterWire"/> and its group, animation, ragdoll and projectile encodings (docs/net.md, "Snapshot core").</summary>
public class CharacterWireTests
{
    static readonly AnimationLibrary Library = new(
        [.. Enumerable.Range(0, 5).Select(i => new AnimationDefinition { Id = "a" + i, Name = "anim " + i, Clip = "clip" + i })]);

    static BonePose Pose(Random random, int bones, float spread)
    {
        var pose = new BonePose(bones) { Weight = (float)random.NextDouble() };
        for (int i = 0; i < bones; i += 2)
        {
            pose.Set[i] = true;
            pose.Rotation[i] = RandomRotation(random);
            pose.Position[i] = new Vector3((float)(random.NextDouble() - 0.5) * spread, (float)(random.NextDouble() - 0.5) * spread, (float)(random.NextDouble() - 0.5) * spread);
        }
        return pose;
    }

    static Quaternion RandomRotation(Random random) =>
        Quaternion.Normalize(new Quaternion((float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f));

    /// <summary>A character with every one of the 13 groups filled with values that are not on a quantization step.</summary>
    static CharacterSnapshot Rich(Random random, float time = 0.37f) => new(new CharacterId(7, 3), null, new Vector3(1234.567f, -12.3456f, -98765.4321f), 2.7182f,
        [
            new AnimationLayer("anim 2", time, 0.6180339f) { Length = 1.7f, Definition = 2 },
            new AnimationLayer("walk lower", 5.5f, 1.25f) { Length = 0f, Definition = -1 },
            new AnimationLayer("anim 4", 9f, float.NaN) { Length = 2f, Definition = 4 },
        ])
    {
        WeaponDrawn = true, IsPlayer = true, Selected = true, InCombat = true, ShacklesLocked = true,
        Task = CharacterTask.Build, Orders = CharacterOrders.Block | CharacterOrders.Sneak, RunSpeed = SpeedMode.Run, PortraitSerial = 99_000,
        Body = new BodyStatus(0.123456f, -0.31415f, 2.71828f, false, false, true, CareKind.Resting, true, false, true),
        CarriedBy = new CharacterId(2, 1), Carrying = new CharacterId(9, 0), AttachBone = "Bip01 L UpperArm",
        Stealth = StealthExposure.Noticed, StealthKoChance = 41.5f,
        Watchers = [new WatcherView(new Vector3(1240.1f, -10f, -98760f), StealthExposure.Near), new WatcherView(new Vector3(1200f, 0f, -98800f), StealthExposure.Detected)],
        Ragdoll = Pose(random, 11, 3f),
        Scale = 0.8765f, Name = "Beep Ünï", Faction = 4, SquadId = 12, Owner = 2, SurgeryCost = -3, Activity = "Patrolling town",
        Prison = new PrisonView(true, 48.5f, 31.9, 250),
        Turret = new MannedTurret("town 1:3", "BUILDING_TURRET", new Vector3(4.2f, 5.3f, 6.4f), -1.2f, "GUN_X"),
        Jobs = [new PermaJob(TaskType.BUILD, "site-1"), new PermaJob(TaskType.PICKUP, null, new Hand(1, 2, 3, 4, 5)), PermaJob.Untargeted(TaskType.IDLE)],
    };

    static CharacterWire Wire(CharacterSnapshot c) => CharacterWire.Quantize(c, _ => 0u, Library);

    static CharacterWire RoundTrip(CharacterWire wire, CharacterWire? baseline, uint mask, out int bytes)
    {
        using var w = new NetWriter();
        var strings = new StringTableWriter(true);
        CharacterWireCodec.WriteGroups(w, strings, wire, baseline, mask);
        bytes = w.Length;
        var r = new NetReader(w.Written);
        var read = CharacterWireCodec.ReadGroups(ref r, new StringTableReader(), baseline, mask, Library);
        r.ExpectEnd();
        return read;
    }

    [Fact]
    public void Wires_round_trip_in_full_and_as_deltas_and_a_steady_character_costs_nothing()
    {
        var random = new Random(1);
        var first = Wire(Rich(random));
        var full = RoundTrip(first, null, CharacterWire.AllGroups, out int fullBytes);
        Assert.Equal(0u, first.DiffMask(full));
        Assert.True(fullBytes < 400, $"{fullBytes} bytes for a full record");

        // A delta moves only the groups that changed, against the acked wire.
        var moved = Rich(random, time: 0.52f) with { Position = new Vector3(1234.9f, -12.3456f, -98760f), Yaw = -2.5f, Selected = false };
        var next = Wire(moved);
        uint mask = next.DiffMask(first);
        Assert.Equal(CharacterWire.Position | CharacterWire.Yaw | CharacterWire.Animations | CharacterWire.Flags | CharacterWire.Ragdoll, mask);
        var delta = RoundTrip(next, first, mask, out int deltaBytes);
        Assert.Equal(0u, next.DiffMask(delta));
        Assert.True(deltaBytes < fullBytes);

        // A snapshot rebuilt from the wire keeps what the groups carry.
        var snapshot = delta.ToSnapshot(new CharacterId(7, 3), _ => null, Library);
        Assert.Equal("Beep Ünï", snapshot.Name);
        Assert.Equal(2, snapshot.Owner);
        Assert.Equal("anim 2", snapshot.Animations[0].Name);
        Assert.Equal(2, snapshot.Animations[0].Definition);
        Assert.Equal(0f, snapshot.Animations[2].Weight);
        Assert.Equal(1.25f, snapshot.Animations[1].Weight);
        Assert.Equal(CharacterTask.Build, snapshot.Task);
        Assert.Equal(new Hand(1, 2, 3, 4, 5), snapshot.Jobs[1].Saved);
        Assert.True(snapshot.Jobs[2].Saved.IsNull);
        Assert.Equal(StealthExposure.Detected, snapshot.Watchers[1].Level);
        Assert.Equal(6, snapshot.Ragdoll!.Set.Count(s => s));
        Assert.Equal(0u, next.DiffMask(Wire(snapshot)));

        // Nothing changed: no mask bits, so a record would carry no groups.
        Assert.Equal(0u, delta.DiffMask(RoundTrip(delta, delta, 0, out int none)));
        Assert.Equal(0, none);
    }

    [Fact]
    public void Projectiles_round_trip_and_writers_cut_what_a_reader_would_refuse()
    {
        var bolt = new ProjectileSnapshot(5, new Vector3(100.123f, 2.5f, -300.777f), Vector3.Normalize(new Vector3(0.3f, -0.2f, 0.9f)), 1.4f, false, "bolt.mesh");
        var wire = ProjectileWire.Quantize(bolt);
        using var w = new NetWriter();
        ProjectileWire.Write(w, new StringTableWriter(true), wire, null, wire.DiffMask(ProjectileWire.Default));
        var r = new NetReader(w.Written);
        var read = ProjectileWire.Read(ref r, new StringTableReader(), null);
        r.ExpectEnd();
        Assert.Equal(wire, read);
        Assert.Equal(wire, ProjectileWire.Quantize(read.ToSnapshot(5)));

        var stuck = ProjectileWire.Quantize(bolt with { Stuck = true, Stretch = 1f });
        using var w2 = new NetWriter();
        ProjectileWire.Write(w2, new StringTableWriter(true), stuck, wire, stuck.DiffMask(wire));
        var r2 = new NetReader(w2.Written);
        Assert.Equal(stuck, ProjectileWire.Read(ref r2, new StringTableReader(), wire));

        // Over-long text, non-finite floats and too many layers are cut on the host, so the reader accepts the result.
        var hostile = Rich(new Random(2)) with
        {
            Name = new string('x', 2000), Activity = new string('é', 900), Scale = float.PositiveInfinity, StealthKoChance = float.NaN,
            Animations = [.. Enumerable.Range(0, 20).Select(i => new AnimationLayer("l" + i, float.NaN, float.NegativeInfinity) { Length = float.NaN, Definition = 99 })],
            Watchers = [.. Enumerable.Range(0, 50).Select(_ => new WatcherView(Vector3.One, StealthExposure.Near))],
            Jobs = [.. Enumerable.Range(0, 100).Select(_ => PermaJob.Untargeted(TaskType.IDLE)), new PermaJob((TaskType)99999, null, Hand.Null)],
        };
        var cut = Wire(hostile);
        Assert.Equal(0u, cut.DiffMask(RoundTrip(cut, null, CharacterWire.AllGroups, out _)));
        Assert.Equal(NetLimits.MaxAnimLayers, cut.Layers.Length);
        Assert.Equal(CharacterWire.MaxWatchers, cut.Watchers.Length);
        Assert.Equal(CharacterWire.MaxJobs, cut.JobList.Length);
    }

    [Fact]
    public void Quantizing_a_decoded_snapshot_changes_nothing()
    {
        var random = new Random(3);
        for (int i = 0; i < 300; i++)
        {
            var c = Rich(random, time: (float)random.NextDouble() * 1.7f) with
            {
                Position = new Vector3((float)(random.NextDouble() - 0.5) * 294_000f, (float)random.NextDouble() * 500f, (float)(random.NextDouble() - 0.5) * 294_000f),
                Yaw = (float)(random.NextDouble() * 8 - 4),
                Ragdoll = Pose(random, 12, i % 7 == 0 ? 400f : 5f),
            };
            var wire = Wire(c);
            var snapshot = wire.ToSnapshot(c.Id, _ => null, Library);
            Assert.Equal(0u, wire.DiffMask(Wire(snapshot)));
            Assert.Equal(0u, wire.DiffMask(RoundTrip(wire, null, CharacterWire.AllGroups, out _)));

            var bolt = ProjectileWire.Quantize(new ProjectileSnapshot(i, c.Position, Vector3.Normalize(RandomRotation(random).AsVector3() + Vector3.UnitX), 1f, i % 2 == 0, "m"));
            Assert.Equal(bolt, ProjectileWire.Quantize(bolt.ToSnapshot(i)));
        }
    }

    [Fact]
    public void Quantization_stays_within_its_documented_precision()
    {
        var random = new Random(4);
        double worstAngle = 0, worstPosition = 0, worstPhase = 0, worstYaw = 0;
        for (int i = 0; i < 2000; i++)
        {
            var pose = Pose(random, 4, 20f);
            var wire = RagdollWire.Quantize(pose)!;
            var back = wire.ToPose();
            for (int b = 0; b < 4; b += 2)
            {
                double dot = Math.Min(1.0, Math.Abs(Quaternion.Dot(pose.Rotation[b], back.Rotation[b])));
                worstAngle = Math.Max(worstAngle, 2 * Math.Acos(dot));
                worstPosition = Math.Max(worstPosition, (pose.Position[b] - back.Position[b]).Length());
            }

            float length = 0.1f + (float)random.NextDouble() * 5f, time = (float)random.NextDouble() * length, yaw = (float)(random.NextDouble() * 6.2 - 3.1);
            var layer = AnimationLayerWire.Quantize(new AnimationLayer("anim 1", time, 1f) { Length = length, Definition = 1 }, Library).ToLayer(Library);
            worstPhase = Math.Max(worstPhase, Math.Abs(layer.Time / layer.Length - time / length));
            worstYaw = Math.Max(worstYaw, Math.Abs(Quant.FromYaw16(Quant.Yaw16(yaw)) - yaw));
        }
        Assert.True(worstAngle <= 0.005, $"smallest-three error {worstAngle} rad");
        Assert.True(worstPosition <= 0.0034, $"ragdoll position error {worstPosition}");   // half a 1/256 step per axis, so at most sqrt(3)/512
        Assert.True(worstPhase <= 1.0 / 65535, $"animation phase error {worstPhase}");
        Assert.True(worstYaw <= Math.PI / 32768, $"yaw error {worstYaw}");

        // Positions at the edge of the world stay within 1/128.
        foreach (float p in new[] { 147_456f, -147_456f, 0.01f, 12345.678f })
            Assert.True(Math.Abs(Quant.FromQ64(Quant.Q64(p)) - p) <= 1f / 128);
    }
}

static class QuaternionTestExtensions
{
    public static Vector3 AsVector3(this Quaternion q) => new(q.X, q.Y, q.Z);
}
