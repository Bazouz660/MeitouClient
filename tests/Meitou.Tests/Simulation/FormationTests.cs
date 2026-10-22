using System.Numerics;
using Meitou.Simulation;

namespace Meitou.Tests.Simulation;

public class FormationTests
{
    [Fact]
    public void Followers_stand_in_a_block_behind_the_leader_that_turns_with_its_heading()
    {
        // Facing +Z, eight followers: three rows of three, two and three behind the leader, centred, never closer than the spacing.
        var slots = Enumerable.Range(0, 8).Select(k => Formation.Slot(k, 8, 0)).ToArray();
        Assert.All(slots, s => Assert.True(s.Y <= -Formation.Spacing + 0.01f, $"{s} is behind the leader"));
        Assert.Equal(3, slots.Select(s => MathF.Round(s.Y)).Distinct().Count());
        Assert.Equal(0, slots.Take(3).Sum(s => s.X), 3);
        for (int i = 0; i < 8; i++)
            for (int j = i + 1; j < 8; j++)
                Assert.True(Vector2.Distance(slots[i], slots[j]) >= Formation.Spacing - 0.01f);
        // Any other heading is the same block turned with it.
        const float yaw = 2.1f;
        var forward = new Vector2(MathF.Sin(yaw), MathF.Cos(yaw));
        var right = new Vector2(forward.Y, -forward.X);
        for (int k = 0; k < 8; k++)
        {
            var turned = Formation.Slot(k, 8, yaw);
            Assert.Equal(slots[k].Y, Vector2.Dot(turned, forward), 3);
            Assert.Equal(slots[k].X, Vector2.Dot(turned, right), 3);
        }
    }

    [Fact]
    public void Characters_keep_their_left_to_right_order_and_the_ones_nearest_the_target_take_the_front_row()
    {
        // Heading +Z (north on the map, towards the click): a character on the +X side stays on that side, the one further north leads.
        var target = new Vector2(0, 1000);
        var positions = new[] { new Vector2(-20, 0), new Vector2(20, 0), new Vector2(0, 30), new Vector2(0, -30) };
        var slots = Formation.Place(positions, target);
        float Along(Vector2 p) => p.Y;
        Assert.True(Along(slots[2]) >= Along(slots[0]) && Along(slots[2]) >= Along(slots[1]) && Along(slots[2]) > Along(slots[3]), "the most advanced character is in the front row");
        Assert.True(Along(slots[3]) <= Along(slots[0]) && Along(slots[3]) <= Along(slots[1]), "the one furthest back is in the back row");
        // Within a row the order across the line of travel is kept, so paths do not cross.
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
                if (i != j && MathF.Abs(slots[i].Y - slots[j].Y) < 0.01f && positions[i].X != positions[j].X)
                    Assert.Equal(MathF.Sign(positions[i].X - positions[j].X), MathF.Sign(slots[i].X - slots[j].X));
    }
}
