using Meitou.Engine;
using Meitou.Engine.Input;
using Meitou.Engine.Time;

namespace Meitou.Tests.Engine;

public class SimulationClockTests
{
    [Fact]
    public void Ticks_beyond_the_budget_are_dropped_and_the_achieved_speed_drops()
    {
        var clock = new SimulationClock(maxTicksPerFrame: 6);
        // A 1 s stall at 1x owes 30 ticks; 6 run.
        Assert.Equal(6, clock.Advance(1));
        Assert.Equal(24, clock.DroppedTicks);
        Assert.Equal(0.2, clock.AchievedSpeed, 9);
        // The budget scales with the speed: 5x runs up to 30 per frame.
        clock.SetSpeed(5);
        Assert.Equal(30, clock.Advance(1));   // owes 150
        Assert.Equal(1.0, clock.AchievedSpeed, 9);   // 5 x 30 / 150
        // A normal frame is back at full speed.
        clock.SetSpeed(1);
        Assert.Equal(1, clock.Advance(1 / 30.0));
        Assert.Equal(1, clock.AchievedSpeed, 9);
        clock.TogglePause();
        clock.Advance(1);
        Assert.Equal(0, clock.AchievedSpeed);
    }
}
