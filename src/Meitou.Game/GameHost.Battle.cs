using System.Numerics;
using Meitou.Simulation;
using Meitou.Simulation.Combat;

namespace Meitou.Game;

/// <summary>
/// The battle (<c>--battle n unit m unit</c>, docs/simulation.md "Battle"): two groups of NPCs, each of one CHARACTER or ANIMAL_CHARACTER record, in two blocks facing
/// each other on the map (the plateau, the duel's mesa or x,z) or the sandbox's floor. No towns and no aggro: the host gives every standing fighter without a target the nearest
/// standing enemy. Backspace starts the same fight again (a new world made the same way). Nothing here runs in the normal game.
/// </summary>
sealed partial class GameHost
{
    /// <summary>The two fronts start this far apart along Z, each block in ranks of up to <see cref="BattleRankWidth"/> fighters <see cref="BattleSpacing"/> apart.</summary>
    const float BattleGap = 40, BattleSpacing = 6;
    const int BattleRankWidth = 25;
    /// <summary>No orders before this tick: the blocks stand a moment before they charge.</summary>
    const int BattleOrderTick = 30;

    /// <summary>The default spot (the owner's pick, 2026-10-23): a plateau about 1380 high, from camera code 01C69590C7EAFBAB44A4CE5146EAC4B2BFD203D43E755F7D44.</summary>
    public static readonly Vector2 BattleSpot = new(-74028, 13428);

    bool IsBattle => g.Battle is not null;
    /// <summary>The floor is the sandbox's: the animation sandbox's, or the battle's with <c>--battle-map sandbox</c>.</summary>
    bool IsFlat => IsSandbox || IsBattle && g.BattleFlat;

    Vector3 battleSpot;
    readonly List<(string Record, string Name, int Count, int? Faction)> battleSides = [];
    readonly List<CharacterId>[] battleFighters = [[], []];
    long battleOrderedAt = -1;
    bool battleRestartRequested, battleOver;

    /// <summary>Finds the two records and their factions and fills the session's world; returns where the camera starts.</summary>
    Vector3 BattleStart()
    {
        var db = scene.Database!;
        foreach (var (count, unit) in g.Battle!)
        {
            var record = FindCharacter(db, unit) ?? throw new ArgumentException($"--battle: no CHARACTER or ANIMAL_CHARACTER named '{unit}'");
            // The record's own faction for its looks (an animal has none: the first faction hostile to the player's).
            string? factionId = record.GetReferences("faction").Select(r => r.TargetStringId).FirstOrDefault();
            int faction = popData!.Factions.Select((f, i) => (f, i)).FirstOrDefault(p => p.f.Id == factionId, (null!, -1)).Item2;
            battleSides.Add((record.StringId, record.Name, count, faction >= 0 ? faction : null));
        }
        var spot = g.BattleFlat ? Vector2.Zero : g.BattleAt is { } at ? new Vector2(at.X, at.Z) : BattleSpot;
        battleSpot = new Vector3(spot.X, session.World.Walkability.GroundHeight(spot.X, spot.Y), spot.Y);
        BattleSetup(session.World, population!);
        session.Ticked += _ => BattleTick();
        var wet = battleFighters.SelectMany(f => f).Count(id => !simWalkability!.IsWalkable(session.World.Characters.Previous[id.Slot].Position.X, session.World.Characters.Previous[id.Slot].Position.Z));
        if (wet > 0) Console.Error.WriteLine($"battle    {wet} fighter(s) start under water: pick another spot with --battle-map x,z");
        Console.WriteLine($"battle    {BattleSide(0)} vs {BattleSide(1)} at {spot.X:0}, {spot.Y:0}{(g.BattleFlat ? " (sandbox floor)" : "")}; Backspace restarts");
        return battleSpot;
    }

    string BattleSide(int side) => $"{battleSides[side].Count} x {battleSides[side].Name}";

    /// <summary>Fills a new world with the two blocks: side 0 south (−Z) facing +Z, side 1 north facing −Z, each centred on the spot's X.</summary>
    void BattleSetup(World world, PopulationSystem population)
    {
        for (int side = 0; side < 2; side++)
        {
            var (record, _, count, faction) = battleSides[side];
            var fighters = battleFighters[side];
            fighters.Clear();
            float sign = side == 0 ? -1 : 1;
            for (int i = 0; i < count; i++)
            {
                int rank = i / BattleRankWidth, inRank = Math.Min(BattleRankWidth, count - rank * BattleRankWidth);
                float x = (i % BattleRankWidth - (inRank - 1) / 2f) * BattleSpacing, z = sign * (BattleGap / 2 + rank * BattleSpacing);
                // Facing +Z is yaw 0; a look of its own per fighter.
                fighters.Add(population.SpawnSandbox(world, record, new Vector2(battleSpot.X + x, battleSpot.Z + z), player: false,
                    yaw: side == 0 ? 0 : MathF.PI, variant: side * 1000 + i + 1, faction: faction));
            }
        }
        battleOrderedAt = -1;
        battleOver = false;
    }

    /// <summary>
    /// Each control tick, once per world tick: a standing fighter without a target is ordered at the nearest standing enemy (one who wakes up joins in again).
    /// The first tick a side has nobody standing the battle is over and the orders stop.
    /// </summary>
    void BattleTick()
    {
        var world = session.World;
        if (battleOver || world.Tick < BattleOrderTick || world.Tick == battleOrderedAt) return;
        battleOrderedAt = world.Tick;
        // The world and its combat system are new after each restart.
        if (world.Systems.OfType<CombatSystem>().FirstOrDefault() is not { } combat) return;
        var table = world.Characters;
        bool Standing(CharacterId id) => table.TryResolveNext(id, out int slot) && table.Cold(slot)?.Medical is { Incapacitated: false };
        Vector3 At(CharacterId id) => table.Previous[id.Slot].Position;
        var standing = battleFighters.Select(f => f.Where(Standing).ToList()).ToArray();
        if (standing[0].Count == 0 || standing[1].Count == 0)
        {
            battleOver = true;
            int winner = standing[0].Count > 0 ? 0 : standing[1].Count > 0 ? 1 : -1;
            string result = winner < 0 ? "nobody left standing" : $"{battleSides[winner].Name} win, {standing[winner].Count} of {battleSides[winner].Count} standing";
            Console.WriteLine($"battle    {BattleSide(0)} vs {BattleSide(1)}: {result} after {world.Tick * world.TickSeconds:0.0} s");
            player.Notice($"Battle over: {result} (Backspace restarts)");
            return;
        }
        for (int side = 0; side < 2; side++)
            foreach (var id in standing[side])
            {
                if (combat.StateOf(id).TargetSlot >= 0) continue;
                var at = At(id);
                var foe = standing[1 - side].MinBy(e => Vector2.DistanceSquared(new Vector2(at.X, at.Z), new Vector2(At(e).X, At(e).Z)));
                world.Commands.Enqueue(new AttackOrder([id], foe) { Tick = world.Tick });
            }
    }

    /// <summary>Once per frame, between ticks: Backspace starts the battle again in a new world made the same way.</summary>
    void BattleFrame()
    {
        if (!IsBattle || !battleRestartRequested) return;
        battleRestartRequested = false;
        var built = BuildSystems(interactive: true);
        session.ReplaceWorld(built.Systems, world => BattleSetup(world, built.Population!));
        population = built.Population;
        aggro = built.Aggro;
        worldEpoch++;
        indexedFor = null;
        previousById.Clear();
        player.Reset();
        player.Notice("Battle restarted (Backspace)");
    }
}
