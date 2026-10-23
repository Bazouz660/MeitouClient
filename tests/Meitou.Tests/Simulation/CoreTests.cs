using System.Numerics;
using Meitou.Simulation;

namespace Meitou.Tests.Simulation;

public class CoreTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void The_pool_runs_every_index_exactly_once(int threads)
    {
        using var pool = new WorkerPool(threads);
        for (int round = 0; round < 50; round++)
        {
            var hits = new int[100 + round];
            pool.ForEach(hits.Length, i => Interlocked.Increment(ref hits[i]));
            Assert.All(hits, h => Assert.Equal(1, h));
        }
        pool.ForEach(0, _ => throw new InvalidOperationException());
    }

    [Fact]
    public void Slots_are_reused_most_recent_first_with_a_higher_generation()
    {
        var t = new CharacterTable(2);
        var a = t.Spawn(new CharacterHot(), new CharacterCold(), 0);
        var b = t.Spawn(new CharacterHot(), new CharacterCold(), 0);
        var c = t.Spawn(new CharacterHot(), new CharacterCold(), 0);   // grows past the first capacity
        Assert.Equal([0, 1, 2], new[] { a.Slot, b.Slot, c.Slot });
        Assert.Equal(3, t.Count);
        Assert.True(t.Remove(a));
        Assert.True(t.Remove(c));
        Assert.False(t.Remove(a));
        Assert.False(t.IsAlive(a));
        var d = t.Spawn(new CharacterHot(), new CharacterCold(), 0);
        Assert.Equal(2, d.Slot);
        Assert.Equal(1, d.Generation);
        var e = t.Spawn(new CharacterHot(), new CharacterCold(), 0);
        Assert.Equal(0, e.Slot);
        Assert.Equal(1, e.Generation);
        // The old id names nothing, though its slot is alive again.
        Assert.False(t.IsAlive(a));
        Assert.True(t.IsAlive(e));
        Assert.Equal(3, t.HighWater);
    }

    [Fact]
    public void Effects_are_ordered_by_target_source_system_and_sequence_whatever_the_buffers()
    {
        // The same effects split over buffers in two different ways sort the same.
        var all = new List<(int Source, int Target)> { (5, 1), (2, 1), (2, 1), (9, 0), (3, 4) };
        List<Effect> Collect(int[] cut)
        {
            var buffers = new List<EffectBuffer>();
            int from = 0;
            foreach (int to in cut.Append(all.Count))
            {
                var b = new EffectBuffer();
                b.BeginSystem(0);
                for (int i = from; i < to; i++) b.Emit(all[i].Source, all[i].Target, kind: i);
                buffers.Add(b);
                from = to;
            }
            var list = buffers.SelectMany(b => b.Items).ToList();
            list.Sort(EffectBuffer.Order);
            return list;
        }
        // Sources emit together, so the cuts fall between sources: (5,1) | (2,1),(2,1) | rest, or all in one.
        var a = Collect([1, 3]);
        var b = Collect([]);
        Assert.Equal(a.Select(e => (e.Target, e.Source, e.Sequence)), b.Select(e => (e.Target, e.Source, e.Sequence)));
        Assert.Equal([(0, 9, 0), (1, 2, 0), (1, 2, 1), (1, 5, 0), (4, 3, 0)], a.Select(e => (e.Target, e.Source, e.Sequence)));
    }
}
