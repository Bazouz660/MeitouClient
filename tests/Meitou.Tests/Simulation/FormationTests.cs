using System.Numerics;
using Meitou.Simulation;

namespace Meitou.Tests.Simulation;

public class FormationTests
{
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
