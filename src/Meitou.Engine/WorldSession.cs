using System.Numerics;
using Meitou.Engine.Cameras;
using Meitou.Engine.Input;
using Meitou.Engine.Time;
using Meitou.Simulation;

namespace Meitou.Engine;

/// <summary>
/// What drives a <see cref="WorldSession"/>'s world: <see cref="Local"/> ticks it with the simulation clock (single-player, a net host); <see cref="Replica"/> is a net client's
/// world, which never ticks and shows the host's snapshots (<see cref="WorldSession.Present"/>; docs/simulation.md "Per-viewer snapshots").
/// </summary>
public enum SessionMode : byte { Local, Replica }

/// <summary>
/// A running world: what is loaded (the region around <see cref="Focus"/>), the two clocks, the game clock, the input and the
/// camera. The host feeds <see cref="Input"/> from its window and calls <see cref="Advance"/> once per displayed frame.
/// <list type="bullet">
/// <item>The <b>control tick</b> (<see cref="Ticks"/>, 30 Hz of real time, whatever the speed) consumes the input, handles pause and
/// speed and moves the camera, so the camera moves while the game is paused. The renderer draws <see cref="Camera"/> at <see cref="Alpha"/>.</item>
/// <item>The <b>simulation tick</b> (<see cref="Simulation"/>, 1/30 s of game time, 30/60/150 per real second at 1x/2x/5x, none while
/// paused) advances <see cref="Clock"/>. Within a frame all control ticks run first (they may change the speed), then the
/// simulation ticks the speed they left allows (docs/simulation.md "Time model").</item>
/// </list>
/// </summary>
public sealed class WorldSession : IDisposable
{
    public WorldSession(Vector3 focus, (double X0, double Z0, double X1, double Z1) region, Func<float, float, float> ground,
        double startHour = GameClock.DefaultStartHour, double tickRate = FixedStepClock.DefaultTickRate, InputBindings? bindings = null,
        GameClock? clock = null, WorldSettings? simulation = null, IEnumerable<ITickSystem>? systems = null,
        IWalkability? walkability = null)
    {
        // The game clock advances by the simulation clock's tick and the world by its own: they must be the same length.
        var settings = simulation ?? new WorldSettings();
        if (Math.Abs(settings.TickSeconds - Simulation.TickSeconds) > 1e-6)
            throw new ArgumentException($"the world ticks {settings.TickSeconds} s but the simulation clock {Simulation.TickSeconds} s", nameof(simulation));
        Focus = focus;
        Region = region;
        Ticks = new FixedStepClock(tickRate);
        Clock = clock ?? new GameClock(startHour);
        Bindings = bindings ?? new InputBindings();
        // The game keeps its cameras above the water surface too (docs/formats/camera.md): the lowest ground is the sea.
        Camera = new CameraRig((x, z) => MathF.Max(ground(x, z), Meitou.Data.World.WorldWater.Height));
        worldSettings = settings;
        worldWalkability = walkability ?? new OpenGroundWalkability(ground);
        World = new World(settings, worldWalkability, systems);
        World.Fixtures.Doors = worldWalkability as Meitou.Simulation.Locks.IDoorGate;
    }

    readonly WorldSettings worldSettings;
    readonly IWalkability worldWalkability;

    /// <summary>The point the world was loaded around.</summary>
    public Vector3 Focus { get; }
    /// <summary>The loaded region in world X/Z (the detail streams with the camera beyond it).</summary>
    public (double X0, double Z0, double X1, double Z1) Region { get; }
    /// <summary>The real-time control tick (input actions, camera, UI).</summary>
    public FixedStepClock Ticks { get; }
    /// <summary>The game-time simulation tick.</summary>
    public SimulationClock Simulation { get; } = new();
    public GameClock Clock { get; }
    /// <summary>The simulated world; it ticks with the simulation clock. Replaced as a whole by <see cref="ReplaceWorld"/> (a loaded save); read it again after a frame, do not keep it.</summary>
    public World World { get; private set; }
    /// <summary>The snapshot the last simulation tick published and the one before it, for interpolating by <see cref="SimulationAlpha"/>.</summary>
    public WorldSnapshot CurrentSnapshot => World.Snapshot;
    public WorldSnapshot PreviousSnapshot => World.PreviousSnapshot;
    /// <summary>
    /// Where the frame is between the previous and the current snapshot, 0 to 1 (the snapshots' alpha, not the camera's): the simulation clock's in <see cref="SessionMode.Local"/>,
    /// the presented one (<see cref="Present"/>) on a replica. Animation, weights and ragdolls interpolate by it.
    /// </summary>
    public float SimulationAlpha => Mode == SessionMode.Replica ? presentedAlpha : Simulation.Alpha;
    /// <summary>
    /// The alpha positions and facings interpolate by: <see cref="SimulationAlpha"/> in <see cref="SessionMode.Local"/>; on a replica the presented one, up to 2 when the client
    /// extrapolates past the newest snapshot.
    /// </summary>
    public float MotionAlpha => Mode == SessionMode.Replica ? presentedMotion : Simulation.Alpha;
    /// <summary>Whether the world ticks here (<see cref="SessionMode.Local"/>) or shows a host's snapshots (<see cref="SessionMode.Replica"/>); set by <see cref="ReplaceWorld"/>.</summary>
    public SessionMode Mode { get; private set; }
    float presentedAlpha, presentedMotion;
    public InputState Input { get; } = new();
    public InputBindings Bindings { get; }
    public CameraRig Camera { get; }
    /// <summary>Where the frame is between the previous and the current control tick (camera interpolation).</summary>
    public float Alpha => (float)Ticks.Alpha;
    /// <summary>The speed: 0 while paused by the player, else 1, 2 or 5.</summary>
    public double TimeScale => Simulation.Speed;

    /// <summary>
    /// Who decides the clock in a net session (docs/net.md); null in single-player. While set, <see cref="RequestClock"/> hands every request to it instead of
    /// applying it, and modal screens stop pausing the world (<see cref="ModalsPause"/>).
    /// </summary>
    public IClockAuthority? ClockAuthority { get; set; }
    /// <summary>Whether modal screens (menus, settings, windows) pause the world: only in single-player, where nobody else is playing.</summary>
    public bool ModalsPause => ClockAuthority is null;

    /// <summary>Called for each control tick's actions after the session's own handling (pause, speed, camera); the host's actions (quit, screenshot...).</summary>
    public event Action<ActionState>? Ticked;
    /// <summary>Raised after each simulation tick (the world ran its phases and the game clock moved), on the frame thread.</summary>
    public event Action<World>? SimulationTicked;
    /// <summary>Raised by <see cref="ReplaceWorld"/> once the new world is in place and the old one discarded; the argument is the new world.</summary>
    public event Action<World>? WorldReplaced;

    /// <summary>
    /// A pause or speed input (key or HUD button): the <see cref="ClockAuthority"/> decides it when one is set, else it applies to <see cref="Simulation"/> at
    /// once, as single-player always did.
    /// </summary>
    public void RequestClock(ClockRequest request)
    {
        if (ClockAuthority is { } authority) authority.Request(request);
        else request.ApplyTo(Simulation);
    }

    /// <summary>Runs one frame's worth of real time: the control ticks it holds, then (<see cref="SessionMode.Local"/> only) the simulation ticks; returns the control ticks.</summary>
    public int Advance(double realSeconds)
    {
        int n = Ticks.Advance(realSeconds);
        for (int i = 0; i < n; i++) Tick();
        if (Mode == SessionMode.Local) AdvanceSimulation(realSeconds);
        return n;
    }

    /// <summary>Feeds real time to the simulation clock only and runs the ticks it owes; returns how many ran. A replica throws: its world never ticks.</summary>
    public int AdvanceSimulation(double realSeconds)
    {
        if (Mode == SessionMode.Replica) throw new InvalidOperationException("a replica world never ticks");
        int n = Simulation.Advance(realSeconds);
        for (int i = 0; i < n; i++) SimulationTick();
        if (Simulation.IsPaused) World.ApplyPausedInterfaceCommands();
        return n;
    }

    /// <summary>One simulation tick: the world runs its phases, the game clock moves by one tick of game time.</summary>
    void SimulationTick()
    {
        World.GameHours = Clock.TotalHours;
        World.RunTick();
        Clock.Advance(Simulation.TickSeconds);
        SimulationTicked?.Invoke(World);
    }

    /// <summary>
    /// Shows a host's snapshots on a replica (<see cref="SessionMode.Replica"/>): <see cref="World.Present"/>, then the frame's alphas, <paramref name="alpha"/> clamped to [0, 1]
    /// (<see cref="SimulationAlpha"/>) and <paramref name="motionAlpha"/> to [0, 2] (<see cref="MotionAlpha"/>). Between frames, on the frame thread.
    /// </summary>
    public void Present(WorldSnapshot previous, WorldSnapshot current, float alpha, float motionAlpha)
    {
        if (Mode != SessionMode.Replica) throw new InvalidOperationException("only a replica session is presented");
        World.Present(previous, current);
        presentedAlpha = float.IsNaN(alpha) ? 0 : Math.Clamp(alpha, 0, 1);
        presentedMotion = float.IsNaN(motionAlpha) ? 0 : Math.Clamp(motionAlpha, 0, 2);
    }

    /// <summary>
    /// Swaps in a new world made from <paramref name="systems"/> (same settings and walkability) at a tick boundary, for a loaded save. <paramref name="setup"/>
    /// fills the new world (before its first tick, as <c>SaveLoader.Load</c> needs); when it throws the old world stays and the new one and its systems are disposed.
    /// The old world and its systems are disposed after the swap. The control tick, the camera, the input and the simulation clock carry on; the game clock is the
    /// caller's to set. Call it between frames or from a control tick's handler, never from inside a world tick. <see cref="WorldReplaced"/> is raised last.
    /// <paramref name="mode"/> becomes <see cref="Mode"/> and the presented alphas restart at 0: a <see cref="SessionMode.Replica"/> takes no systems and a
    /// <paramref name="setup"/> that calls <see cref="World.MakeReplica"/> (and a local world must not be one), else <see cref="ArgumentException"/> with the old world kept.
    /// </summary>
    public World ReplaceWorld(IEnumerable<ITickSystem> systems, Action<World>? setup = null, SessionMode mode = SessionMode.Local)
    {
        var list = systems.ToList();
        var next = new World(worldSettings, worldWalkability, list);
        next.Floaters = World.Floaters;   // the host keeps drawing the floaters across the swap
        try
        {
            if (mode == SessionMode.Replica && list.Count != 0) throw new ArgumentException("a replica world has no systems", nameof(systems));
            setup?.Invoke(next);
            if (next.IsReplica != (mode == SessionMode.Replica))
                throw new ArgumentException(mode == SessionMode.Replica ? "a replica session's setup makes its world a replica (World.MakeReplica)" : "a local session's world is not a replica", nameof(setup));
        }
        catch
        {
            Discard(next);
            throw;
        }
        var old = World;
        World = next;
        Mode = mode;
        presentedAlpha = presentedMotion = 0;
        Discard(old);
        // The navmesh door table follows the new world: the old one's locked doors are forgotten, the new one's told.
        if (worldWalkability is Meitou.Simulation.Locks.IDoorGate gate) { gate.ResetDoors(); next.Fixtures.Doors = gate; }
        WorldReplaced?.Invoke(next);
        return next;
    }

    static void Discard(World world)
    {
        world.Dispose();
        foreach (var s in world.Systems) (s as IDisposable)?.Dispose();
    }

    /// <summary>Called when the session is disposed, for the systems that own threads.</summary>
    public void Dispose() => Discard(World);

    /// <summary>One control tick: the actions from the input (pause, speed), then the camera.</summary>
    public ActionState Tick()
    {
        var actions = Bindings.Resolve(Input.Consume());
        if (actions.Pressed(InputAction.Pause)) RequestClock(ClockRequest.TogglePause);
        if (actions.Pressed(InputAction.Speed1)) RequestClock(ClockRequest.Set(1));
        if (actions.Pressed(InputAction.Speed2)) RequestClock(ClockRequest.Set(2));
        if (actions.Pressed(InputAction.Speed3)) RequestClock(ClockRequest.Set(5));
        if (actions.Pressed(InputAction.TimeFaster)) RequestClock(ClockRequest.Faster);
        if (actions.Pressed(InputAction.TimeSlower)) RequestClock(ClockRequest.Slower);
        Camera.Update((float)Ticks.TickSeconds, actions);
        Ticked?.Invoke(actions);
        return actions;
    }

    /// <summary>The camera to draw this frame.</summary>
    public CameraState CameraAt() => Camera.At(Alpha);
}
