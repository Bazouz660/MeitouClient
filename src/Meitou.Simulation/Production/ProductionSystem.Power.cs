using Meitou.Data.World;
using Meitou.Simulation.Construction;

namespace Meitou.Simulation.Production;

/// <summary>
/// The power grid (docs/game/buildings-production.md "Power", docs/simulation.md "Production"): each town's generators, batteries and consumers, shared as the original's
/// <c>Town::vfunc_87</c>. Runs after the operators, so a consumer's grant (from last tick's operators) scales its work the tick after.
/// </summary>
public sealed partial class ProductionSystem
{
    /// <summary>Per town, whether batteries covered a shortfall last tick (the original's town <c>+0x470</c>, read the frame after it is set; not saved).</summary>
    readonly Dictionary<int, bool> batteryMode = [];
    readonly Dictionary<int, List<Machine>> grids = [];
    /// <summary>Per town, whether its generators give power and its batteries can (the town object's <c>+0x474</c> and <c>+0x480</c> above 0 after <c>Town::vfunc_87</c>); what <see cref="HasPower"/> reads. Not saved.</summary>
    readonly Dictionary<int, (bool Generators, bool Batteries)> townSupply = [];
    /// <summary>Per town, the grid's totals after the last tick (the town panel's power and battery bars, <see cref="TownPower"/>). Not saved.</summary>
    readonly Dictionary<int, TownPower> townPower = [];

    float Rated(World world, Machine m) => m.Data.PowerOutput + world.Research.Upgrade(m.Data.Id).PowerIncrease;
    float Capacity(World world, Machine m) => m.Data.PowerCapacity + world.Research.Upgrade(m.Data.Id).CapacityIncrease;

    /// <summary>Whether a generator has fuel: no fuel sections (wind), or something left in one.</summary>
    static bool Fueled(Machine m) => m.Inputs.Length == 0 || Array.Exists(m.Inputs, i => i.Level > 0 || i.Count > 0);

    /// <summary>A generator's output now (<c>GeneratorBuilding::getPowerOutput</c>): health x (output + increase), 0 out of fuel; wind x <see cref="Machine.Wind"/>, nothing below 1 (<c>WindGeneratorBuilding::getPowerOutput</c>).</summary>
    float Output(World world, Machine m)
    {
        if (!m.PowerOn || !Fueled(m)) return 0;
        float output = Rated(world, m);
        if (m.Data.IsWind && (output *= m.Wind) < 1) return 0;
        return output;
    }

    /// <summary>The player outposts with their grids' totals, for the town panel (docs/game/ui-screens.md "Town panel").</summary>
    public IReadOnlyList<OutpostSnapshot> OutpostView(World world) => world.Buildings.Towns.Count == 0 ? []
        : [.. world.Buildings.Towns.Select(t => new OutpostSnapshot(t.Number, t.Name, t.Position, t.Radius, t.Owner, t.Public, townPower.GetValueOrDefault(t.Number)))];

    void PowerTick(World world, float dt)
    {
        foreach (var list in grids.Values) list.Clear();
        townPower.Clear();
        foreach (var m in world.Machines.All)
        {
            m.Power = 1;
            if (m.Data.IsWind) (m.Wind, m.WindRated) = (WindShare(world, m), Rated(world, m));
            if (!m.Data.IsConsumer && !m.Data.IsGenerator && !m.Data.IsBattery) continue;
            if (Site(world, m.Site) is not { Complete: true } site) continue;
            if (!grids.TryGetValue(site.Town, out var grid)) grids[site.Town] = grid = [];
            grid.Add(m);
        }
        foreach (var (town, grid) in grids)
            if (grid.Count > 0) Share(world, town, grid, dt);
        foreach (var m in world.Machines.All)
            if (m.Data.IsGenerator)
            {
                m.Status = (byte)(m.Demand > 0 ? 2 : 0);
                if (m.Inputs.Length > 0 && m.PowerOn) BurnFuel(m, dt);
            }
        foreach (var m in world.Machines.All)
        {
            var site = Site(world, m.Site);
            bool built = site is { Complete: true };
            m.Health = site is { Target: > 0 } s ? Math.Clamp(s.Progress / s.Target, 0, 1) : 1;
            m.ProductionMult = world.Research.Upgrade(m.Data.Id).ProductionMult;
            m.Efficiency = built ? Efficiency(world, m) : 0;
            m.HasPower = built && HasPower(world, m, Site(world, m.Site)!.Town);
            m.Rotation = !built ? 0 : m.Data.IsGenerator ? (m.PowerOn ? Output(world, m) * 0.01f : 0) : m.Efficiency;
        }
    }

    /// <summary>
    /// A wind generator's share of its output (<c>PhysicsCollection::getWindRotationPower</c>, <b>Verified</b>): its first ROTATION_WIND_SPEED part's <see cref="RotorSpec.WindShare"/> of the
    /// wind speed of the weather region at the site; 0 without such a part or without weather. The original reads it from the part's render entity, so a generator without one loaded
    /// gives nothing; here every built generator has its wind (engine choice).
    /// </summary>
    static float WindShare(World world, Machine m)
    {
        m.WindSpeed = world.Buildings.Find(m.Site) is { } site && world.Weather?.At(site.Position.X, site.Position.Z) is { } here ? here.WindSpeed : 0;
        return m.Data.WindRotor is { } rotor ? RotorSpec.WindShare(m.WindSpeed, rotor.Min, rotor.Max) : 0;
    }

    /// <summary>
    /// The efficiency a machine's sound emitter hears (<c>FarmBuilding::vfunc_108</c>, <b>Observed</b>): the share of its operators (1 when the data has no operator count) x health x the
    /// granted share of its demand (1 with no demand; for a battery, below its capacity, the share of its rate it is charging, negative while it drains) x the yield x the BUILDING's raw
    /// output rate x the research production multiplier. A wind generator hears its blade's wind share (<c>WindGeneratorBuilding::getSoundIntensity</c>, <b>Verified</b>): <see cref="Machine.Wind"/>.
    /// </summary>
    float Efficiency(World world, Machine m)
    {
        if (m.Data.IsWind) return m.Wind;
        float share = m.Data.PowerOperators > 0 ? (float)m.Operators / m.Data.PowerOperators : 1;
        float saturation = m.Power;
        if (m.Data.IsBattery)
        {
            float rate = Rated(world, m);
            saturation = rate > 0 && m.Charge < Capacity(world, m) ? m.Granted / rate : 1;
        }
        return share * m.Health * saturation * m.Yield * m.Data.OutputRate * m.ProductionMult;
    }

    /// <summary>
    /// Whether a machine counts as powered for its sound emitter (<c>FUN_140299370</c>, <b>Observed</b>): a generator always (fuel and switch are not asked); a machine drawing nothing always; else
    /// it must be switched on and its town's generators give power, or its batteries do while the grid is in battery mode and the machine takes battery power.
    /// </summary>
    bool HasPower(World world, Machine m, int town)
    {
        if (m.Data.IsGenerator || Rated(world, m) == 0) return true;
        if (!m.PowerOn) return false;
        if (!townSupply.TryGetValue(town, out var supply)) return true;
        return supply.Generators || (m.GetsBattery && batteryMode.GetValueOrDefault(town) && supply.Batteries);
    }

    /// <summary>
    /// One town's frame (<b>Observed</b>, <c>Town::vfunc_87</c>): generator output is shared among the consumers in ascending demand, each taking at most an equal share of what is
    /// left (<c>FUN_140927350</c> sorts); when batteries were in use last tick, consumers left short that get battery power share the batteries' output. With nothing left over the
    /// batteries drain by what was drawn; with a surplus they charge from it (each at most its rate) and the grid leaves battery mode.
    /// </summary>
    void Share(World world, int town, List<Machine> grid, float dt)
    {
        bool mode = batteryMode.GetValueOrDefault(town);
        float supply = 0, batteries = 0, demandTotal = 0;
        var consumers = new List<Machine>();
        foreach (var m in grid)
        {
            m.Granted = 0;
            if (m.Data.IsGenerator) supply += m.Demand = Output(world, m);
            else if (m.Data.IsBattery) { m.Demand = m.PowerOn && m.Charge > 0 ? Rated(world, m) : 0; batteries += m.Demand; }
            else
            {
                float share = m.Data.PowerOperators > 0 ? (float)m.LastOperators / m.Data.PowerOperators : 1;
                m.Demand = m.PowerOn ? MathF.Abs(Rated(world, m)) * share : 0;
                demandTotal += m.Demand;
                consumers.Add(m);
            }
        }
        float generated = supply;
        townSupply[town] = (generated > 0, batteries > 0);
        consumers.Sort((a, b) => a.Demand != b.Demand ? a.Demand.CompareTo(b.Demand) : string.CompareOrdinal(a.Site, b.Site));
        int left = consumers.Count, short_ = 0;
        foreach (var c in consumers)
        {
            float share = supply / Math.Max(1, left--);
            float give = MathF.Min(share, c.Demand);
            supply -= give;
            c.Granted = give;
            if (give < c.Demand && c.GetsBattery) short_++;
        }
        float batteryLeft = batteries;
        if (mode && short_ > 0)
            foreach (var c in consumers)
            {
                if (!c.GetsBattery || c.Granted >= c.Demand) continue;
                float give = MathF.Min(c.Demand - c.Granted, batteryLeft / Math.Max(1, short_--));
                batteryLeft -= give;
                c.Granted += give;
            }
        float charged = 0, drained = 0;
        if (supply <= 0)
        {
            float used = drained = batteries - batteryLeft;
            foreach (var b in grid)
                if (b.Data.IsBattery) b.Granted = used > 0 && batteries > 0 ? -used * b.Demand / batteries : 0;
            batteryMode[town] = true;
        }
        else
        {
            batteryMode[town] = false;
            var cells = grid.Where(b => b.Data.IsBattery && b.PowerOn && b.Charge < Capacity(world, b)).ToList();
            int n = cells.Count;
            foreach (var b in cells)
            {
                float give = MathF.Min(MathF.Abs(Rated(world, b)), supply / Math.Max(1, n--));
                supply -= give;
                charged += give;
                b.Granted = give;
            }
        }
        float charge = 0, capacity = 0;
        foreach (var m in grid)
        {
            if (m.Data.IsConsumer) m.Power = m.Demand > 0 ? m.Granted / m.Demand : 1;
            else if (m.Data.IsGenerator)
            {
                // The load (GeneratorBuilding::vfunc_167): output / rated x (charging + consumer demand) / all generators' output, at most 1.
                float rated = Rated(world, m);
                m.Usage = generated > 0 && rated > 0 ? MathF.Min(1, m.Demand / rated * (charged + demandTotal) / generated) : 0;
            }
            else if (m.Data.IsBattery)
            {
                // The store moves by flow / rate x dt seconds, between 0 and the capacity (FarmBuilding::vfunc_27 on batteries).
                float rate = Rated(world, m);
                m.Store = Capacity(world, m);
                if (rate > 0) m.Charge = Math.Clamp(m.Charge + m.Granted / rate * dt, 0, Capacity(world, m));
                (charge, capacity) = (charge + m.Charge, capacity + m.Store);
            }
        }
        townPower[town] = new(generated, demandTotal, charged, drained, charge, capacity);
    }

    /// <summary>A generator burns fuel as a producer draws inputs (<c>vfunc_183</c>): <c>rate x dt x P x option</c>, times its load.</summary>
    void BurnFuel(Machine m, float dt)
    {
        float amount = m.Data.Rate * dt * constants.ProductionSpeed * Settings.ProductionOption * m.Usage;
        if (amount <= 0) return;
        foreach (var input in m.Inputs)
        {
            float need = input.Rate * amount;
            if (need <= 0) continue;
            while (input.Level < need && input.Count > 0) { input.Count--; input.Level += 1; }
            input.Level = MathF.Max(0, input.Level - need);
        }
    }

    /// <summary>The factor a machine's work is scaled by: its health, its power share and its research production multiplier.</summary>
    float WorkFactor(World world, Machine m) => m.Health * m.Power * world.Research.Upgrade(m.Data.Id).ProductionMult;
}
