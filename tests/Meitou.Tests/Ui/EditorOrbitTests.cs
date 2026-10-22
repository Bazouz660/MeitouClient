using Meitou.Game;

namespace Meitou.Tests.Ui;

/// <summary>The character editor's camera (<see cref="EditorOrbit"/>, docs/characters.md "Character editor", Preview): start values, drag direction and the limits.</summary>
public class EditorOrbitTests
{
    [Fact]
    public void Reset_starts_a_body_far_and_a_head_close()
    {
        var orbit = new EditorOrbit();
        orbit.Drag(100, 50);
        orbit.Reset(head: true);
        Assert.True(orbit.Head);
        Assert.Equal((EditorOrbit.StartYaw, EditorOrbit.HeadStartElevation, EditorOrbit.HeadStart), (orbit.Yaw, orbit.Elevation, orbit.Distance));
        orbit.Reset(head: false);
        Assert.Equal((EditorOrbit.StartYaw, EditorOrbit.StartElevation, EditorOrbit.BodyStart), (orbit.Yaw, orbit.Elevation, orbit.Distance));
    }

    [Fact]
    public void Drag_right_turns_and_down_lifts_within_the_elevation_limits()
    {
        var orbit = new EditorOrbit();
        orbit.Drag(10, 0);
        Assert.True(orbit.Yaw < EditorOrbit.StartYaw);
        orbit.Drag(0, 10_000);
        Assert.Equal(EditorOrbit.MaxElevation, orbit.Elevation);
        orbit.Drag(0, -10_000);
        Assert.Equal(EditorOrbit.MinElevation, orbit.Elevation);
    }

    [Theory]
    [InlineData(false, EditorOrbit.BodyNear, EditorOrbit.BodyFar)]
    [InlineData(true, EditorOrbit.HeadNear, EditorOrbit.HeadFar)]
    public void Wheel_zooms_within_the_limits_of_the_mode(bool head, float near, float far)
    {
        var orbit = new EditorOrbit();
        orbit.Reset(head);
        float start = orbit.Distance;
        orbit.Zoom(1);
        Assert.True(orbit.Distance < start);
        orbit.Zoom(1000);
        Assert.Equal(near, orbit.Distance);
        orbit.Zoom(-1000);
        Assert.Equal(far, orbit.Distance);
    }
}
