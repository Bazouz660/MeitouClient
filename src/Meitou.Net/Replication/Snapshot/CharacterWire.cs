using System.Numerics;
using Meitou.Data.Characters;
using Meitou.Data.Gameplay;
using Meitou.Data.Save;
using Meitou.Simulation;
using Meitou.Simulation.Production;

namespace Meitou.Net.Replication;

/// <summary>A prisoner's cage and sentence in wire form: the remaining sentence is whole minutes, so it is not resent every tick.</summary>
public readonly record struct PrisonWire(bool Locked, float Sentence, long Minutes);

/// <summary>A watcher of a sneaking character in wire form: the looker's position in 1/64 units, absolute (the codec sends it relative to the character).</summary>
public readonly record struct WatcherWire(int X, int Y, int Z, StealthExposure Level);

/// <summary>A manned turret in wire form: the position in 1/64 units and the yaw as 16 bits.</summary>
public sealed record TurretWire(string Key, string BuildingId, string GunId, int X, int Y, int Z, short Yaw);

/// <summary>
/// The quantized fields of one <see cref="CharacterSnapshot"/> (docs/net.md, "Snapshot core"), in the 13 change-mask groups of <see cref="CharacterWireCodec"/>,
/// with value equality per group. The host quantizes each per-viewer snapshot (<see cref="Quantize"/>) and diffs the result against the last acked one
/// (<see cref="DiffMask"/>); the client keeps wires and rebuilds snapshots from them (<see cref="ToSnapshot"/>). Every stored value is what the client
/// decodes, so <c>Quantize(ToSnapshot(w))</c> equals <c>w</c> in every group. Instances are treated as immutable once built; the setters are internal only
/// for the codec. The detail fields (skills, HUD, inventory, path, bounties, health parts, item grid) travel on the reliable channel and are not here.
/// </summary>
public sealed class CharacterWire : IEquatable<CharacterWire>
{
    public const int GroupCount = 13;

    /// <summary>The mask with every group set.</summary>
    public const uint AllGroups = (1u << GroupCount) - 1;

    /// <summary>Group bits of the change mask, hottest first.</summary>
    public const uint Position = 1 << 0, Yaw = 1 << 1, Animations = 1 << 2, Flags = 1 << 3, Body = 1 << 4, Carry = 1 << 5, Stealth = 1 << 6,
        Ragdoll = 1 << 7, Look = 1 << 8, Identity = 1 << 9, Prison = 1 << 10, Turret = 1 << 11, Jobs = 1 << 12;

    /// <summary>Most watchers and jobs one character sends.</summary>
    public const int MaxWatchers = 32, MaxJobs = 64;

    /// <summary>The bits of <see cref="FlagBits"/>, in wire order.</summary>
    public const uint WeaponDrawnFlag = 1 << 0, IsPlayerFlag = 1 << 1, IsTraderFlag = 1 << 2, SelectedFlag = 1 << 3, InspectedFlag = 1 << 4,
        PickingLockFlag = 1 << 5, WorkingShacklesFlag = 1 << 6, InBedFlag = 1 << 7, InCombatFlag = 1 << 8, ShackledFlag = 1 << 9,
        ShacklesLockedFlag = 1 << 10, CanBeShackledFlag = 1 << 11, CarriesShacklesFlag = 1 << 12, AllFlags = (1 << 13) - 1;

    /// <summary>The wire of a new entity (the baseline of a first record): Faction, SquadId and Owner -1, run speed Free, scale 1, ids none, no layers.</summary>
    public static readonly CharacterWire Default = new()
    {
        RunSpeed = SpeedMode.Free, Scale = 1f, Faction = -1, SquadId = -1, Owner = -1,
        CarriedBy = CharacterId.None, Carrying = CharacterId.None,
    };

    // Position
    public int X { get; internal set; }
    public int Y { get; internal set; }
    public int Z { get; internal set; }
    // Yaw
    public short YawSteps { get; internal set; }
    // Animations
    public AnimationLayerWire[] Layers { get; internal set; } = [];
    // Flags
    public uint FlagBits { get; internal set; }
    public CharacterTask Task { get; internal set; }
    public CharacterOrders Orders { get; internal set; }
    public byte RunSpeed { get; internal set; }
    public uint PortraitSerial { get; internal set; }
    // Body
    public BodyStatus? BodyStatus { get; internal set; }
    // Carry
    public CharacterId CarriedBy { get; internal set; }
    public CharacterId Carrying { get; internal set; }
    public string? AttachBone { get; internal set; }
    // Stealth
    public StealthExposure? StealthLevel { get; internal set; }
    public float? StealthKoChance { get; internal set; }
    public WatcherWire[] Watchers { get; internal set; } = [];
    // Ragdoll
    public RagdollWire? RagdollPose { get; internal set; }
    // Look
    public uint LookId { get; internal set; }
    public float Scale { get; internal set; }
    // Identity
    public string Name { get; internal set; } = "";
    public int Faction { get; internal set; }
    public int SquadId { get; internal set; }
    public int Owner { get; internal set; }
    public string? Activity { get; internal set; }
    // Prison
    public PrisonWire? PrisonView { get; internal set; }
    // Turret
    public TurretWire? TurretView { get; internal set; }
    // Jobs
    public PermaJob[] JobList { get; internal set; } = [];

    internal CharacterWire Clone() => (CharacterWire)MemberwiseClone();

    /// <summary>
    /// The host side: quantizes one snapshot. <paramref name="lookIdOf"/> maps the appearance to its wire look id (0 for none, as the
    /// <c>LookRegistry</c> does); <paramref name="animations"/> is the library whose indices the layers use. Text is cut to its wire limits, non-finite
    /// floats become 0, lists are cut to their maxima, so a reader never refuses what this produces.
    /// </summary>
    public static CharacterWire Quantize(CharacterSnapshot c, Func<CharacterAppearance?, uint> lookIdOf, AnimationLibrary animations)
    {
        uint flags = 0;
        void Set(bool on, uint bit) => flags |= on ? bit : 0;
        Set(c.WeaponDrawn, WeaponDrawnFlag); Set(c.IsPlayer, IsPlayerFlag); Set(c.IsTrader, IsTraderFlag); Set(c.Selected, SelectedFlag);
        Set(c.Inspected, InspectedFlag); Set(c.PickingLock, PickingLockFlag); Set(c.WorkingShackles, WorkingShacklesFlag); Set(c.InBed, InBedFlag);
        Set(c.InCombat, InCombatFlag); Set(c.Shackled, ShackledFlag); Set(c.ShacklesLocked, ShacklesLockedFlag); Set(c.CanBeShackled, CanBeShackledFlag);
        Set(c.CarriesShackles, CarriesShacklesFlag);

        int x = Quant.Q64(c.Position.X), y = Quant.Q64(c.Position.Y), z = Quant.Q64(c.Position.Z);
        return new CharacterWire
        {
            X = x, Y = y, Z = z,
            YawSteps = Quant.Yaw16(c.Yaw),
            Layers = AnimationWire.Quantize(c.Animations, animations),
            FlagBits = flags,
            Task = NetEnum<CharacterTask>.IsValid(NetEnum<CharacterTask>.ToBits(c.Task)) ? c.Task : CharacterTask.Idle,
            Orders = c.Orders & (CharacterOrders)0xFF,
            RunSpeed = c.RunSpeed,
            PortraitSerial = c.PortraitSerial,
            BodyStatus = c.Body is { } b ? QuantizeBody(b) : null,
            CarriedBy = c.CarriedBy,
            Carrying = c.Carrying,
            AttachBone = WireText.LimitOrNull(c.AttachBone, NetLimits.MaxName),
            StealthLevel = c.Stealth is { } level && NetEnum<StealthExposure>.IsValid(NetEnum<StealthExposure>.ToBits(level)) ? level : null,
            StealthKoChance = c.StealthKoChance is { } ko && float.IsFinite(ko) ? ko : null,
            Watchers = QuantizeWatchers(c.Watchers),
            RagdollPose = RagdollWire.Quantize(c.Ragdoll),
            LookId = lookIdOf(c.Appearance),
            Scale = WireText.Finite(c.Scale, 1f),
            Name = WireText.Limit(c.Name, NetLimits.MaxName),
            Faction = c.Faction,
            SquadId = c.SquadId,
            Owner = c.Owner,
            Activity = WireText.LimitOrNull(c.Activity, NetLimits.MaxName),
            PrisonView = c.Prison is { } p ? QuantizePrison(p) : null,
            TurretView = c.Turret is { } t ? QuantizeTurret(t) : null,
            JobList = QuantizeJobs(c.Jobs),
        };
    }

    static BodyStatus QuantizeBody(BodyStatus b) => b with
    {
        Blood = Quant.FromU16(Quant.U16(b.Blood, 0f, 1f), 0f, 1f),
        WorstPart = Quant.FromS12(Quant.S12(b.WorstPart)),
        Hunger = Quant.FromU16(Quant.U16(b.Hunger, 0f, 4f), 0f, 4f),
        Care = b.Care is CareKind.None or CareKind.Treating or CareKind.Resting ? b.Care : CareKind.None,
    };

    static WatcherWire[] QuantizeWatchers(IReadOnlyList<WatcherView> watchers)
    {
        int n = Math.Min(watchers.Count, MaxWatchers);
        if (n == 0) return [];
        var wire = new WatcherWire[n];
        for (int i = 0; i < n; i++)
        {
            var w = watchers[i];
            wire[i] = new WatcherWire(Quant.Q64(w.Position.X), Quant.Q64(w.Position.Y), Quant.Q64(w.Position.Z),
                NetEnum<StealthExposure>.IsValid(NetEnum<StealthExposure>.ToBits(w.Level)) ? w.Level : StealthExposure.Unobserved);
        }
        return wire;
    }

    static PrisonWire QuantizePrison(PrisonView p)
    {
        double minutes = double.IsFinite(p.HoursLeft) ? Math.Round(p.HoursLeft * 60.0) : 0.0;
        return new PrisonWire(p.Locked, WireText.Finite(p.Sentence), (long)Math.Clamp(minutes, -1e12, 1e12));
    }

    static TurretWire QuantizeTurret(MannedTurret t) =>
        new(WireText.Limit(t.Key, NetLimits.MaxName), WireText.Limit(t.BuildingId, NetLimits.MaxName), WireText.Limit(t.GunId, NetLimits.MaxName),
            Quant.Q64(t.Position.X), Quant.Q64(t.Position.Y), Quant.Q64(t.Position.Z), Quant.Yaw16(t.Yaw));

    static PermaJob[] QuantizeJobs(IReadOnlyList<PermaJob> jobs)
    {
        if (jobs.Count == 0) return [];
        var list = new List<PermaJob>(Math.Min(jobs.Count, MaxJobs));
        foreach (var job in jobs)
        {
            if (list.Count == MaxJobs) break;
            if (!NetEnum<Meitou.Data.Gameplay.Ai.TaskType>.IsValid(NetEnum<Meitou.Data.Gameplay.Ai.TaskType>.ToBits(job.Type))) continue;
            list.Add(job with { Site = WireText.LimitOrNull(job.Site, NetLimits.MaxName), Saved = job.Saved.IsNull ? Hand.Null : job.Saved });
        }
        return [.. list];
    }

    /// <summary>
    /// The client side: the snapshot the wire stands for. <paramref name="resolveLook"/> maps a look id to its appearance (null for 0 or an id not yet
    /// defined); the detail fields keep their defaults and are merged in by the caller.
    /// </summary>
    public CharacterSnapshot ToSnapshot(CharacterId id, Func<uint, CharacterAppearance?> resolveLook, AnimationLibrary animations)
    {
        var layers = new AnimationLayer[Layers.Length];
        for (int i = 0; i < layers.Length; i++) layers[i] = Layers[i].ToLayer(animations);

        var watchers = new WatcherView[Watchers.Length];
        for (int i = 0; i < watchers.Length; i++)
        {
            var w = Watchers[i];
            watchers[i] = new WatcherView(new Vector3(Quant.FromQ64(w.X), Quant.FromQ64(w.Y), Quant.FromQ64(w.Z)), w.Level);
        }

        return new CharacterSnapshot(id, LookId == 0 ? null : resolveLook(LookId), new Vector3(Quant.FromQ64(X), Quant.FromQ64(Y), Quant.FromQ64(Z)),
            Quant.FromYaw16(YawSteps), layers)
        {
            Faction = Faction,
            WeaponDrawn = (FlagBits & WeaponDrawnFlag) != 0,
            Name = Name,
            SquadId = SquadId,
            IsPlayer = (FlagBits & IsPlayerFlag) != 0,
            Owner = Owner,
            IsTrader = (FlagBits & IsTraderFlag) != 0,
            Selected = (FlagBits & SelectedFlag) != 0,
            Inspected = (FlagBits & InspectedFlag) != 0,
            Orders = Orders,
            RunSpeed = RunSpeed,
            Jobs = JobList.Length == 0 ? [] : JobList,
            Body = BodyStatus,
            CarriedBy = CarriedBy,
            AttachBone = AttachBone,
            Scale = Scale,
            Task = Task,
            PickingLock = (FlagBits & PickingLockFlag) != 0,
            WorkingShackles = (FlagBits & WorkingShacklesFlag) != 0,
            InBed = (FlagBits & InBedFlag) != 0,
            InCombat = (FlagBits & InCombatFlag) != 0,
            Shackled = (FlagBits & ShackledFlag) != 0,
            ShacklesLocked = (FlagBits & ShacklesLockedFlag) != 0,
            CanBeShackled = (FlagBits & CanBeShackledFlag) != 0,
            CarriesShackles = (FlagBits & CarriesShacklesFlag) != 0,
            PortraitSerial = PortraitSerial,
            Carrying = Carrying,
            Activity = Activity,
            Stealth = StealthLevel,
            StealthKoChance = StealthKoChance,
            Prison = PrisonView is { } p ? new PrisonView(p.Locked, p.Sentence, p.Minutes / 60.0) : null,
            Turret = TurretView is { } t
                ? new MannedTurret(t.Key, t.BuildingId, new Vector3(Quant.FromQ64(t.X), Quant.FromQ64(t.Y), Quant.FromQ64(t.Z)), Quant.FromYaw16(t.Yaw), t.GunId)
                : null,
            Watchers = watchers,
            Ragdoll = RagdollPose?.ToPose(),
        };
    }

    /// <summary>The groups (bits of <see cref="Position"/> to <see cref="Jobs"/>) in which this wire differs from <paramref name="other"/>; 0 when equal.</summary>
    public uint DiffMask(CharacterWire other)
    {
        uint m = 0;
        if (X != other.X || Y != other.Y || Z != other.Z) m |= Position;
        if (YawSteps != other.YawSteps) m |= Yaw;
        if (!Layers.AsSpan().SequenceEqual(other.Layers)) m |= Animations;
        if (FlagBits != other.FlagBits || Task != other.Task || Orders != other.Orders || RunSpeed != other.RunSpeed || PortraitSerial != other.PortraitSerial) m |= Flags;
        if (BodyStatus != other.BodyStatus) m |= Body;
        if (CarriedBy != other.CarriedBy || Carrying != other.Carrying || !string.Equals(AttachBone, other.AttachBone, StringComparison.Ordinal)) m |= Carry;
        if (StealthLevel != other.StealthLevel || StealthKoChance != other.StealthKoChance || !Watchers.AsSpan().SequenceEqual(other.Watchers)) m |= Stealth;
        if (!Equals(RagdollPose, other.RagdollPose)) m |= Ragdoll;
        if (LookId != other.LookId || Scale != other.Scale) m |= Look;
        if (!string.Equals(Name, other.Name, StringComparison.Ordinal) || Faction != other.Faction || SquadId != other.SquadId || Owner != other.Owner
            || !string.Equals(Activity, other.Activity, StringComparison.Ordinal)) m |= Identity;
        if (PrisonView != other.PrisonView) m |= Prison;
        if (!Equals(TurretView, other.TurretView)) m |= Turret;
        if (!JobList.AsSpan().SequenceEqual(other.JobList)) m |= Jobs;
        return m;
    }

    public bool Equals(CharacterWire? other) => other is not null && DiffMask(other) == 0;

    public override bool Equals(object? obj) => obj is CharacterWire other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(X, Y, Z, YawSteps, FlagBits, Name, Layers.Length);
}
