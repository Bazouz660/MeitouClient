using System.Diagnostics;

namespace Meitou.Simulation;

/// <summary>
/// Wall time of the world's ticks per phase and per system, summed until the host takes it (the profiler's sim rows). Off by default: a tick then
/// pays one null check per measured span. A system's time is every span it ran in (its serial calls, its parallel phases up to the barrier, its effects' apply).
/// </summary>
public sealed class TickProfile
{
    public static readonly string[] PhaseNames = ["inputs", "grid", "sight", "schedule", "think", "move", "act", "commit", "slow world", "publish"];
    public const int Inputs = 0, Grid = 1, Sight = 2, Schedule = 3, Think = 4, Move = 5, Act = 6, Commit = 7, SlowWorld = 8, Publish = 9;

    long last;

    /// <summary>Measure the ticks (set by the host between ticks).</summary>
    public bool Enabled { get; set; }
    /// <summary>Ticks measured since the last <see cref="Clear"/>.</summary>
    public int Ticks { get; private set; }
    /// <summary>Ms per phase (<see cref="PhaseNames"/>) since the last <see cref="Clear"/>.</summary>
    public double[] PhaseMs { get; } = new double[PhaseNames.Length];
    /// <summary>Ms per system, by its index in <see cref="World.Systems"/>, since the last <see cref="Clear"/>.</summary>
    public double[] SystemMs { get; private set; } = [];

    public void Clear()
    {
        Ticks = 0;
        Array.Clear(PhaseMs);
        Array.Clear(SystemMs);
    }

    internal void Begin(int systems)
    {
        if (SystemMs.Length != systems) SystemMs = new double[systems];
        Ticks++;
        last = Stopwatch.GetTimestamp();
    }

    /// <summary>The time since the last lap goes to <paramref name="phase"/> and, given one, to <paramref name="system"/>.</summary>
    internal void Lap(int phase, int system = -1)
    {
        long now = Stopwatch.GetTimestamp();
        double ms = (now - last) * 1000.0 / Stopwatch.Frequency;
        PhaseMs[phase] += ms;
        if (system >= 0) SystemMs[system] += ms;
        last = now;
    }
}
