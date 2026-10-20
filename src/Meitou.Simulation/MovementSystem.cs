using System.Numerics;
using Meitou.Data.World;
using Meitou.Simulation.Bodies;

namespace Meitou.Simulation;

/// <summary>
/// Movement on the walkability stand-in (docs/game/pathfinding.md "Movement") and the first tasks: wander within the squad's home
/// radius, go to (move orders), follow the squad leader.
/// <list type="bullet">
/// <item>Speed: the stat S in decimetres (world units) per second, capped by the speed mode (walk speed, 55, none); the character
/// follows a path at that speed at once (the nav agent, Observed), speeds up by 15 units/s per second only in combat (<see cref="Acceleration"/>, the combat
/// steering), and stops at once on arrival.</item>
/// <item>Separation: the repulsion along self-other is nonzero only closer than R and proportional to 100 x (1 - d/R), over at most
/// 26 neighbours (Observed); R is twice the footprint radius 4 of a human (<see cref="SeparationRadius"/>), and the scale from that
/// proportionality to a speed is an engine choice (<see cref="SeparationStrength"/>).</item>
/// <item>Paths: a character that wants one is flagged in Think; the Schedule step of the next tick asks the <see cref="PathService"/>;
/// an answer is applied in a serial step, so a path is never read while it is replaced.</item>
/// </list>
/// Water states cap or replace the top speed (<see cref="WaterStateAt"/>, <see cref="Speed.InWater"/>). Not modelled yet (Unknown or later): slopes (the original has no slope penalty), the combat speed
/// multiplier, road routes for far trips, the formation slot rules (Unknown).
/// <item>Turning (engine choice; the original's rate is Unknown): the heading a path is followed along turns at most <see cref="TurnRate"/>, and the
/// speed is scaled by the cosine of what is left of the turn (none beyond 90 degrees), so a character turns about on the spot before it runs off.</item>
/// Queued orders (<see cref="CharacterCold.OrderQueue"/>) and the formation (<see cref="Formation"/>) exist.
/// </summary>
public sealed class MovementSystem(PathService paths) : ITickSystem, IDisposable
{
    /// <summary>Speed gained per second in combat (docs/game/pathfinding.md, the combat / follow steering: "ramps up by 15 per second").</summary>
    public const float Acceleration = 15;
    /// <summary>R of the separation steering: neighbour radius plus margin; a human's footprint radius is 4.</summary>
    public const float SeparationRadius = 8;
    /// <summary>Speed in units/s of a repulsion of magnitude 100 (engine choice).</summary>
    public const float SeparationStrength = 0.15f;
    public const int MaxSeparationContributions = 26;
    /// <summary>A character within this distance of a waypoint has reached it.</summary>
    public const float ArriveRadius = 10;
    /// <summary>The <see cref="CharacterCold.ArriveWithin"/> of a walk to a spot (engine choice: a barman stands at his node behind the counter, not a stride off it).</summary>
    public const float SpotArrive = 2;
    /// <summary>Formation offsets (3 units apart in the factory) are spread by this when followers stand beside the leader (the slot rules are Unknown).</summary>
    public const float FormationScale = 2.5f;

    /// <summary>The farthest a formation offset reaches: the factory's own layout (eight to a row, 3 apart) ends near 22 units, however many are in the squad.</summary>
    public const float MaxFormationOffset = 22;

    /// <summary>
    /// The offset (in factory units, which <see cref="FormationScale"/> spreads) of a follower standing <paramref name="fromLeader"/> away from its leader when its squad is put back
    /// in the world (a save, a platoon coming into range). It keeps its place but not a stray: the offset is divided by the scale (else every reload would spread the
    /// squad 2.5 times wider) and cut to <see cref="MaxFormationOffset"/>, so followers that had wandered off close up again.
    /// </summary>
    public static Vector2 OffsetOfPlace(Vector2 fromLeader)
    {
        var offset = fromLeader / FormationScale;
        float length = offset.Length();
        return length > MaxFormationOffset ? offset * (MaxFormationOffset / length) : offset;
    }
    /// <summary>A follower farther than this from its slot asks for a path to it.</summary>
    public const float FollowSlack = 14;
    /// <summary>Seconds between a follower's path requests, and the wait after a failed one.</summary>
    public const float RepathSeconds = 0.5f, FailedPathWait = 2;
    /// <summary>A wanderer waits this many seconds (uniform) after reaching a place.</summary>
    public const float WaitMin = 3, WaitMax = 10;
    /// <summary>A follower farther than this from its slot runs at full speed to catch up.</summary>
    public const float CatchUpDistance = 60;
    /// <summary>
    /// Radians per second the heading of a path follower turns at most (engine choice: a Kenshi recording turns a standing character about 100 degrees in
    /// 0.15 s before it runs; docs/game/pathfinding.md "Turning").
    /// </summary>
    public const float TurnRate = 4 * MathF.PI;
    /// <summary>
    /// Seconds between the requests of a walker on the provisional part of a long route (<see cref="PathResult.Exact"/>), which ask for the path again as
    /// zones load ahead of it, and how many times running it may be stopped there by ground the route did not know before it gives up (engine choices).
    /// </summary>
    public const float ProvisionalRepathSeconds = 2;
    public const int ProvisionalBlockedLimit = 5;

    public PathService Paths { get; } = paths;

    /// <summary>The system owns its path service (the host hands it over at construction), so disposing the system stops the path thread.</summary>
    public void Dispose() => Paths.Dispose();

    public void Inputs(World world, IReadOnlyList<SimCommand> commands)
    {
        ApplyAnswers(world);
        var table = world.Characters;
        foreach (var command in commands)
        {
            if (command is RepathCommand)
            {
                for (int s = 0; s < table.Next.Length; s++)
                {
                    ref var c = ref table.Next[s];
                    if (!c.Alive || c.Task != (byte)CharacterTask.GoTo || (c.Flags & (ushort)(MoveFlags.Pending | MoveFlags.HasPath)) == 0) continue;
                    c.Flags = (ushort)((c.Flags & ~(ushort)(MoveFlags.Pending | MoveFlags.HasPath)) | (ushort)MoveFlags.NeedPath);
                    c.PathCursor = 0;
                    table.Cold(s)!.PathRequest = 0;
                }
                continue;
            }
            if (command is SandboxMovement sandbox)
            {
                if (table.TryResolveNext(sandbox.Character, out int target))
                {
                    var tc = table.Cold(target)!;
                    if (sandbox.Mode is { } mode) tc.ModeOverride = mode;
                    if (sandbox.Speed is { } fixedSpeed) tc.SpeedOverride = MathF.Max(fixedSpeed, 0);
                }
                continue;
            }
            if (command is not MoveOrder order) continue;
            // An empty list orders the issuer's selection (the UI sends orders for whatever is selected when the click happens).
            var who = world.Actors(order, order.Characters);
            var slots = new List<int>();
            foreach (var id in who)
            {
                if (!table.TryResolveNext(id, out int found) || table.Cold(found)!.IsImprisoned) continue;   // a prisoner stays in its cage
                if (table.Cold(found)!.InSomething == Locks.HeldState.Bed) world.Fixtures.LeaveBed(world, id);   // an order gets a sleeper up
                slots.Add(found);
            }
            var targets = Formation.Place([.. slots.Select(s => new Vector2(table.Next[s].Position.X, table.Next[s].Position.Z))], new Vector2(order.Target.X, order.Target.Z));
            for (int k = 0; k < slots.Count; k++)
            {
                int slot = slots[k];
                ref var n = ref table.Next[slot];
                var cold = table.Cold(slot)!;
                var target = targets[k];
                bool busy = n.Task == (byte)CharacterTask.GoTo && (n.Flags & (ushort)MoveFlags.AnyPath) != 0;
                if (order.Queued && busy)
                {
                    cold.OrderQueue.Add(target);   // after the order in hand (shift + right click)
                    continue;
                }
                GoTo(table, slot, target, cold.RunSpeed);
                (cold.GoalHeight, cold.GoalHeightAt) = (order.Target.Y, target);
            }
        }
    }

    /// <summary>
    /// Sends a character to a point at a speed mode, dropping its queue (serial phases only). A path it is following is kept until the new one
    /// arrives, so a new order given on the move (holding the right button) does not stop it for the ticks the path takes.
    /// </summary>
    public static void GoTo(CharacterTable table, int slot, Vector2 target, byte mode)
    {
        ref var n = ref table.Next[slot];
        var cold = table.Cold(slot)!;
        cold.OrderQueue.Clear();
        cold.ArriveWithin = 0;
        n.Task = (byte)CharacterTask.GoTo;
        n.Goal = target;
        n.Mode = mode;
        n.Flags = (ushort)((n.Flags & ~(ushort)MoveFlags.Pending) | (ushort)MoveFlags.NeedPath);
        cold.PathRequest = 0;
    }

    /// <summary>Drops a character's orders and path and makes it stand (serial phases only).</summary>
    public static void Stop(CharacterTable table, int slot)
    {
        ref var n = ref table.Next[slot];
        n.Flags = (ushort)(n.Flags & ~(ushort)MoveFlags.AnyPath);
        n.Task = (byte)CharacterTask.Idle;
        n.PathCursor = 0;
        var cold = table.Cold(slot)!;
        cold.OrderQueue.Clear();
        cold.PathRequest = 0;
    }

    public void Schedule(World world)
    {
        var table = world.Characters;
        var next = table.Next;
        for (int i = 0; i < next.Length; i++)
        {
            if (!next[i].Alive || (next[i].Flags & (ushort)MoveFlags.NeedPath) == 0) continue;
            var cold = table.Cold(i)!;
            // The goal's floor: the height the order was given at, else the character's own (a wander or follow goal on its level).
            float nearY = cold.GoalHeightAt == next[i].Goal && !float.IsNaN(cold.GoalHeight) ? cold.GoalHeight : next[i].Position.Y;
            var to = new Vector3(next[i].Goal.X, world.Walkability.GroundHeight(next[i].Goal.X, next[i].Goal.Y, nearY), next[i].Goal.Y);
            cold.PathRequest = Paths.Submit(new CharacterId(i, next[i].Generation), next[i].Position, to, cold.FootprintRadius, cold.WaterFactor);
            next[i].Flags = (ushort)((next[i].Flags & ~(ushort)MoveFlags.NeedPath) | (ushort)MoveFlags.Pending);
        }
        // The synchronous service has the answers already.
        if (Paths.IsSynchronous) ApplyAnswers(world);
    }

    void ApplyAnswers(World world)
    {
        var table = world.Characters;
        foreach (var answer in Paths.Drain())
        {
            if (!table.TryResolveNext(answer.Character, out int slot)) continue;
            var cold = table.Cold(slot)!;
            ref var n = ref table.Next[slot];
            if (cold.PathRequest != answer.Id)
            {
                // An older request: the character was given another goal since. Orders given again every tick (a held right button) are each replaced
                // before their path comes, so a found path newer than the one followed is still taken while the latest request is on its way.
                bool waiting = (n.Flags & (ushort)(MoveFlags.NeedPath | MoveFlags.Pending)) != 0;
                if (!answer.Path.Found || !waiting || n.Task != (byte)CharacterTask.GoTo || answer.Id <= cold.PathApplied) continue;
                cold.Path = [.. answer.Path.Points.Skip(1)];
                cold.PathExact = answer.Path.IsProvisional ? answer.Path.Exact - 1 : int.MaxValue;
                if (answer.Path.Exact > 1) cold.ProvisionalBlocked = 0;
                cold.PathApplied = answer.Id;
                n.PathCursor = 0;
                n.Flags |= (ushort)MoveFlags.HasPath;
                continue;
            }
            n.Flags = (ushort)(n.Flags & ~(ushort)MoveFlags.Pending);
            if (answer.Path.Found)
            {
                cold.Path = [.. answer.Path.Points.Skip(1)];
                cold.PathExact = answer.Path.IsProvisional ? answer.Path.Exact - 1 : int.MaxValue;
                if (answer.Path.Exact > 1) cold.ProvisionalBlocked = 0;
                n.PathCursor = 0;
                n.Flags |= (ushort)MoveFlags.HasPath;
            }
            else
            {
                // No way there: the old path (an order given on the move) is dropped too.
                n.Flags = (ushort)(n.Flags & ~(ushort)MoveFlags.HasPath);
                n.PathCursor = 0;
                n.TaskTime = FailedPathWait;
                if (n.Task == (byte)CharacterTask.GoTo) { n.Task = (byte)CharacterTask.Idle; cold.OrderQueue.Clear(); }
            }
        }
    }

    public void Think(World world, Partition part)
    {
        var table = world.Characters;
        var prev = table.Previous;
        var next = table.Next;
        float dt = world.TickSeconds;
        for (int i = part.Start; i < part.End; i++)
        {
            if (!prev[i].Alive) continue;
            ref var n = ref next[i];
            var task = (CharacterTask)n.Task;
            var flags = (MoveFlags)n.Flags;
            var cold = table.Cold(i)!;
            if (cold.Medical is { Incapacitated: true }) continue;
            bool busy = (flags & MoveFlags.AnyPath) != 0;
            if (task == CharacterTask.Follow)
            {
                var squad = cold.SquadId >= 0 ? world.Squads.Find(cold.SquadId) : null;
                var leader = squad?.Leader ?? CharacterId.None;
                if (leader.IsNone || leader.Slot == i || !table.IsAlive(leader))
                {
                    n.Task = (byte)CharacterTask.Wander;
                    continue;
                }
                if (table.Cold(leader.Slot)?.CarriedBy == table.IdOf(i)) continue;   // it carries its leader: the squad follows it, it does not follow itself
                var lead = prev[leader.Slot];
                var slotPos = new Vector2(lead.Position.X, lead.Position.Z) + cold.FormationOffset * FormationScale;
                var here = new Vector2(n.Position.X, n.Position.Z);
                float away = Vector2.Distance(here, slotPos);
                n.TaskTime -= dt;
                n.Mode = away > CatchUpDistance ? SpeedMode.Free : lead.Mode;
                if (away > FollowSlack && (flags & (MoveFlags.NeedPath | MoveFlags.Pending)) == 0 && n.TaskTime <= 0
                    && (!busy || Vector2.DistanceSquared(slotPos, n.Goal) > 20 * 20))
                {
                    n.Goal = slotPos;
                    n.Flags |= (ushort)MoveFlags.NeedPath;
                    n.TaskTime = RepathSeconds;
                }
            }
            else if (task == CharacterTask.Wander)
            {
                n.Mode = SpeedMode.Walk;
                if (busy) continue;
                n.TaskTime -= dt;
                if (n.TaskTime > 0) continue;
                var squad = cold.SquadId >= 0 ? world.Squads.Find(cold.SquadId) : null;
                var centre = squad is not null ? squad.HomeCentre : new Vector2(n.Position.X, n.Position.Z);
                float radius = squad is not null ? squad.HomeRadius : 100;
                if (cold.Ai is { WanderRadius: > 0 } mind) (centre, radius) = (mind.WanderCentre, mind.WanderRadius);   // milling about its post (Ai.AiBehaviour.Mill)
                ulong roll = Rng.Hash(world.Seed, Rng.Key(table.IdOf(i)), RngPurpose.Wander, (ulong)world.Tick);
                n.Goal = Rng.PointInDisc(roll, centre, radius);
                n.Flags |= (ushort)MoveFlags.NeedPath;
            }
        }
    }

    /// <summary>
    /// The group speed (<see cref="SpeedMode.Group"/>): the lowest top speed among the squad's members walking a path in that mode (last tick's, so the order the
    /// partitions run in does not matter). Which characters the original counts as the group (the squad or the selection) is Unknown (docs/game/pathfinding.md).
    /// </summary>
    static float SlowestOfSquad(World world, CharacterCold cold)
    {
        var table = world.Characters;
        float slowest = float.MaxValue;
        if (cold.SquadId < 0 || world.Squads.Find(cold.SquadId) is not { } squad) return slowest;
        foreach (var member in squad.Members)
        {
            if (!table.IsAlive(member)) continue;
            ref readonly var p = ref table.Previous[member.Slot];
            if (p.Mode != SpeedMode.Group || ((MoveFlags)p.Flags & MoveFlags.HasPath) == 0 || table.Cold(member.Slot)?.Medical is { Incapacitated: true }) continue;
            slowest = MathF.Min(slowest, p.MaxSpeed);
        }
        return slowest;
    }

    public void Move(World world, Partition part)
    {
        var table = world.Characters;
        var prev = table.Previous;
        var next = table.Next;
        var walk = world.Walkability;
        float dt = world.TickSeconds;
        for (int i = part.Start; i < part.End; i++)
        {
            if (!prev[i].Alive) continue;
            ref readonly var p = ref prev[i];
            ref var n = ref next[i];
            var cold = table.Cold(i)!;
            if (cold.Medical is { Incapacitated: true })
            {
                // Lying down (knocked out or dead): no steering, no pushing; the body system has already dropped the orders.
                n.Velocity = Vector3.Zero;
                n.Animation = 0;
                continue;
            }
            if (cold.IsImprisoned)
            {
                // Held in its cage (put there by the fixture registry): no path, no steering, not pushed.
                n.Velocity = Vector3.Zero;
                n.Position = p.Position;
                n.Flags = (ushort)(n.Flags & ~(ushort)MoveFlags.AnyPath);
                continue;
            }
            float speed = MathF.Sqrt(p.Velocity.X * p.Velocity.X + p.Velocity.Z * p.Velocity.Z);
            var dir = Vector2.Zero;
            float desired = 0;
            bool finished = false, closing = false;
            int water = WaterStateAt(walk, p.Position, cold);
            cold.WaterState = (byte)water;

            if (((MoveFlags)n.Flags & MoveFlags.HasPath) != 0 && n.PathCursor < cold.Path.Length)
            {
                var waypoint = cold.Path[n.PathCursor];
                var to = new Vector2(waypoint.X - p.Position.X, waypoint.Z - p.Position.Z);
                float dist = to.Length();
                bool last = n.PathCursor == cold.Path.Length - 1;
                closing = last && cold.ArriveWithin > 0 && dist <= ArriveRadius;
                if (dist <= (last ? cold.ArriveWithin > 0 ? cold.ArriveWithin : ArriveRadius : ArriveRadius * 0.5f))
                {
                    n.PathCursor++;
                    if (last) finished = true;
                }
                else
                {
                    dir = to / dist;
                    byte mode = cold.ModeOverride ?? n.Mode;
                    // A carrier in walk mode is raised to run mode (the original's speed selector, Observed; docs/game/carry-loot.md "Speed and weight").
                    if (mode == SpeedMode.Walk && !cold.Carrying.IsNone) mode = SpeedMode.Run;
                    float top = water > 0 && cold.Race is { } race ? Speed.InWater(water, n.MaxSpeed, race, cold.SwimSpeed) : n.MaxSpeed;
                    desired = cold.SpeedOverride > 0 ? cold.SpeedOverride : MathF.Min(top, SpeedMode.Cap(mode, n.WalkSpeed));
                    if (mode == SpeedMode.Group && cold.SpeedOverride <= 0) desired = MathF.Min(desired, SlowestOfSquad(world, cold));
                    if ((cold.Orders & CharacterOrders.Sneak) != 0 && cold.SpeedOverride <= 0) desired = MathF.Min(desired, Orders.SneakSpeed(desired, Orders.Stealth(cold)));
                }
                // On the provisional part of a long route: ask again (the zones ahead may have loaded), following this one until the answer comes.
                if (n.PathCursor >= cold.PathExact) Reroute(world, ref n, cold);
            }
            else if (((MoveFlags)n.Flags & MoveFlags.HasPath) != 0)
            {
                finished = true;
            }

            if (finished)
            {
                n.Flags = (ushort)(n.Flags & ~(ushort)MoveFlags.HasPath);
                n.PathCursor = 0;
                speed = 0;
                // The end of an old path while the new one is on its way (an order given on the move) leaves the task to the new path.
                bool replaced = ((MoveFlags)n.Flags & (MoveFlags.NeedPath | MoveFlags.Pending)) != 0;
                if (!replaced && n.Task == (byte)CharacterTask.GoTo)
                {
                    if (cold.OrderQueue.Count > 0)
                    {
                        // The next queued order (shift + right click): a path to it is asked for next tick.
                        n.Goal = cold.OrderQueue[0];
                        cold.OrderQueue.RemoveAt(0);
                        n.Flags |= (ushort)MoveFlags.NeedPath;
                    }
                    else n.Task = (byte)CharacterTask.Idle;
                }
                else if (!replaced)
                {
                    ulong roll = Rng.Hash(world.Seed, Rng.Key(table.IdOf(i)), RngPurpose.Think, (ulong)world.Tick);
                    n.TaskTime = WaitMin + Rng.Float(roll) * (WaitMax - WaitMin);
                }
            }
            else
            {
                // A path is followed at the top speed at once (the nav agent sets its velocity to top speed x direction); the 15 / s ramp is the combat steering's.
                speed = cold.InCombat && desired > speed ? MathF.Min(speed + Acceleration * dt, desired) : desired;
                if (dir != Vector2.Zero)
                {
                    (dir, speed) = Turn(p.Yaw, dir, speed, dt);
                    n.Yaw = MathF.Atan2(dir.X, dir.Y);   // also while it turns on the spot
                }
            }

            // Separation from the neighbours of the last tick, visited in the grid's fixed order.
            var push = new SeparationState { Table = table, Self = i };
            world.Grid.Query(p.Position.X, p.Position.Z, SeparationRadius, ref push, static (int other, ref SeparationState s) =>
                {
                    if (other == s.Self || s.Count >= MaxSeparationContributions) return;
                    if (s.Table.Cold(other) is { CarriedBy.IsNone: false }) return;   // a carried body is on its carrier's shoulder, not in anyone's way
                    var all = s.Table.Previous;
                    var d = new Vector2(all[s.Self].Position.X - all[other].Position.X, all[s.Self].Position.Z - all[other].Position.Z);
                    float len = d.Length();
                    if (len >= SeparationRadius) return;
                    s.Count++;
                    // Exactly on top of each other: the lower slot is pushed one way, the other the opposite.
                    s.Sum += (len > 1e-4f ? d / len : (s.Self < other ? Vector2.UnitX : -Vector2.UnitX)) * (100 * (1 - len / SeparationRadius));
                });

            // Closing on its spot it is not held off by those standing round it (they are pushed aside as it arrives).
            if (closing) push.Sum = Vector2.Zero;
            var step = (dir * speed + push.Sum * SeparationStrength) * dt;
            float x = p.Position.X + step.X, z = p.Position.Z + step.Y;
            // Pushed by its neighbours where it cannot stand (over a hole furniture cuts in a floor): its own step alone.
            if (push.Sum != Vector2.Zero && !walk.IsWalkable(x, z, p.Position.Y))
            {
                step = dir * speed * dt;
                (x, z) = (p.Position.X + step.X, p.Position.Z + step.Y);
            }
            if (step != Vector2.Zero)
            {
                // One already off every surface near its height (a stale height) is not held there: it steps down onto what is below.
                if (walk.IsWalkable(x, z, p.Position.Y) || !walk.IsWalkable(p.Position.X, p.Position.Z, p.Position.Y) && walk.IsWalkable(x, z))
                {
                    n.Position = new Vector3(x, walk.GroundHeight(x, z, p.Position.Y), z);
                    if (speed > 0.1f) n.Yaw = MathF.Atan2(dir.X, dir.Y);
                }
                else
                {
                    // Blocked on the provisional part of a long route (the terrain-only route met a wall or a building): stand and ask again.
                    if (n.PathCursor >= cold.PathExact && ++cold.ProvisionalBlocked <= ProvisionalBlockedLimit)
                    {
                        speed = 0;
                        dir = Vector2.Zero;
                        Reroute(world, ref n, cold, now: true);
                        goto moved;
                    }
                    // Blocked (no surface to walk on there): drop the path and wait before trying again.
                    speed = 0;
                    n.Flags = (ushort)(n.Flags & ~(ushort)MoveFlags.HasPath);
                    n.TaskTime = FailedPathWait;
                    if (n.Task == (byte)CharacterTask.GoTo) { n.Task = (byte)CharacterTask.Idle; cold.OrderQueue.Clear(); }
                }
            }
        moved:
            n.Velocity = new Vector3(dir.X * speed, 0, dir.Y * speed);
            n.Animation = (ushort)(speed < 1 ? 0 : speed <= n.WalkSpeed + 1 ? 1 : 2);
            n.AnimationTime = n.Animation == p.Animation ? p.AnimationTime + dt : 0;
        }
    }

    /// <summary>
    /// The water state of a character (<see cref="Speed.WaterState"/>, a race's own characters only, not a carried body): the original reads the height it stands at, which in water is the
    /// seabed, while here a character on the water keeps the navmesh's flat water surface for its height, so the depth is taken from the terrain below while it is at the surface
    /// (<see cref="Speed.WaterSlack"/> over the water level; a deck or floor higher up counts as dry). The race's size scale is taken as 1 (what humans have, <b>Observed</b>).
    /// </summary>
    static int WaterStateAt(IWalkability walk, Vector3 at, CharacterCold cold)
    {
        if (cold.Race is not { } race || !cold.CarriedBy.IsNone) return 0;
        float standY = at.Y <= WorldWater.Height + Speed.WaterSlack ? MathF.Min(at.Y, walk.TerrainHeight(at.X, at.Z)) : at.Y;
        return Speed.WaterState(standY, 1, race.Swims);
    }

    /// <summary>Asks for the path to the goal again (partition-safe: the character's own state), at most every <see cref="ProvisionalRepathSeconds"/> unless <paramref name="now"/>.</summary>
    static void Reroute(World world, ref CharacterHot n, CharacterCold cold, bool now = false)
    {
        if ((n.Flags & (ushort)(MoveFlags.NeedPath | MoveFlags.Pending)) != 0 || !now && world.Tick < cold.ProvisionalRepathTick) return;
        n.Flags |= (ushort)MoveFlags.NeedPath;
        cold.ProvisionalRepathTick = world.Tick + (long)MathF.Ceiling(ProvisionalRepathSeconds / world.TickSeconds);
    }

    /// <summary>
    /// The heading turned from <paramref name="yaw"/> towards <paramref name="wanted"/> by at most <see cref="TurnRate"/> x <paramref name="dt"/>,
    /// and the speed scaled by the cosine of the turn still to make (zero beyond 90 degrees: the character turns where it stands).
    /// </summary>
    public static (Vector2 Dir, float Speed) Turn(float yaw, Vector2 wanted, float speed, float dt)
    {
        float target = MathF.Atan2(wanted.X, wanted.Y);
        float delta = MathF.IEEERemainder(target - yaw, MathF.Tau);
        float max = TurnRate * dt;
        float heading = MathF.Abs(delta) <= max ? target : yaw + MathF.CopySign(max, delta);
        float left = MathF.IEEERemainder(target - heading, MathF.Tau);
        return (new Vector2(MathF.Sin(heading), MathF.Cos(heading)), speed * MathF.Max(0, MathF.Cos(left)));
    }

    struct SeparationState
    {
        public CharacterTable Table;
        public int Self;
        public int Count;
        public Vector2 Sum;
    }
}
