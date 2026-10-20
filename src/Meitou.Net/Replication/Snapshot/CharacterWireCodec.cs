using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.Save;
using Meitou.Simulation;
using Meitou.Simulation.Production;

namespace Meitou.Net.Replication;

/// <summary>
/// The 13 change-mask groups of a <see cref="CharacterWire"/> on the wire (docs/net.md, "Snapshot core"). The caller writes the record header (slot, length,
/// baseline reference, the mask varint); this writes the groups the mask names in bit order and reads them back onto a copy of the baseline (or of
/// <see cref="CharacterWire.Default"/> without one). Without a baseline the position is absolute; with one it is the zigzag difference. A reader failure
/// throws <see cref="NetFormatException"/>; a writer given a wire that breaks a limit throws <see cref="ArgumentException"/> (<see cref="CharacterWire.Quantize"/>
/// output never does).
/// </summary>
public static class CharacterWireCodec
{
    public static void WriteGroups(NetWriter w, StringTableWriter strings, CharacterWire now, CharacterWire? baseline, uint mask)
    {
        if ((mask & ~CharacterWire.AllGroups) != 0) throw new ArgumentException($"Mask {mask:X} has bits beyond the {CharacterWire.GroupCount} groups.", nameof(mask));
        if ((mask & CharacterWire.Position) != 0)
        {
            w.WriteVarInt(now.X - (long)(baseline?.X ?? 0));
            w.WriteVarInt(now.Y - (long)(baseline?.Y ?? 0));
            w.WriteVarInt(now.Z - (long)(baseline?.Z ?? 0));
        }
        if ((mask & CharacterWire.Yaw) != 0) w.WriteUInt16((ushort)now.YawSteps);
        if ((mask & CharacterWire.Animations) != 0) AnimationWire.Write(w, strings, now.Layers, (baseline ?? CharacterWire.Default).Layers);
        if ((mask & CharacterWire.Flags) != 0)
        {
            w.WriteVarUInt(now.FlagBits);
            w.WriteEnum(now.Task);
            w.WriteEnum(now.Orders);
            w.WriteByte(now.RunSpeed);
            w.WriteVarUInt(now.PortraitSerial);
        }
        if ((mask & CharacterWire.Body) != 0) WriteBody(w, now.BodyStatus);
        if ((mask & CharacterWire.Carry) != 0)
        {
            CommonCodec.WriteId(w, now.CarriedBy);
            CommonCodec.WriteId(w, now.Carrying);
            strings.Write(w, now.AttachBone);
        }
        if ((mask & CharacterWire.Stealth) != 0) WriteStealth(w, now);
        if ((mask & CharacterWire.Ragdoll) != 0)
        {
            w.WriteBool(now.RagdollPose is not null);
            now.RagdollPose?.Write(w);
        }
        if ((mask & CharacterWire.Look) != 0)
        {
            w.WriteVarUInt(now.LookId);
            w.WriteSingle(now.Scale);
        }
        if ((mask & CharacterWire.Identity) != 0)
        {
            strings.Write(w, now.Name);
            w.WriteVarInt(now.Faction);
            w.WriteVarInt(now.SquadId);
            w.WriteVarInt(now.Owner);
            CommonCodec.WriteNullableInt(w, now.SurgeryCost);
            strings.Write(w, now.Activity);
        }
        if ((mask & CharacterWire.Prison) != 0) WritePrison(w, now.PrisonView);
        if ((mask & CharacterWire.Turret) != 0) WriteTurret(w, strings, now.TurretView);
        if ((mask & CharacterWire.Jobs) != 0) WriteJobs(w, strings, now.JobList);
    }

    public static CharacterWire ReadGroups(ref NetReader r, StringTableReader strings, CharacterWire? baseline, uint mask, AnimationLibrary animations)
    {
        if ((mask & ~CharacterWire.AllGroups) != 0) throw new NetFormatException($"Character change mask {mask:X} has unknown groups.");
        var b = baseline ?? CharacterWire.Default;
        var c = b.Clone();
        if ((mask & CharacterWire.Position) != 0)
        {
            c.X = WireText.AddPosition(b.X, r.ReadVarInt());
            c.Y = WireText.AddPosition(b.Y, r.ReadVarInt());
            c.Z = WireText.AddPosition(b.Z, r.ReadVarInt());
        }
        if ((mask & CharacterWire.Yaw) != 0) c.YawSteps = (short)r.ReadUInt16();
        if ((mask & CharacterWire.Animations) != 0) c.Layers = AnimationWire.Read(ref r, strings, b.Layers, animations);
        if ((mask & CharacterWire.Flags) != 0)
        {
            uint flags = r.ReadVarUInt32();
            if ((flags & ~CharacterWire.AllFlags) != 0) throw new NetFormatException($"Unknown character flags {flags:X}.");
            c.FlagBits = flags;
            c.Task = r.ReadEnum<CharacterTask>();
            c.Orders = r.ReadEnum<CharacterOrders>();
            c.RunSpeed = r.ReadByte();
            c.PortraitSerial = r.ReadVarUInt32();
        }
        if ((mask & CharacterWire.Body) != 0) c.BodyStatus = ReadBody(ref r);
        if ((mask & CharacterWire.Carry) != 0)
        {
            c.CarriedBy = CommonCodec.ReadId(ref r);
            c.Carrying = CommonCodec.ReadId(ref r);
            c.AttachBone = strings.Read(ref r, NetLimits.MaxName);
        }
        if ((mask & CharacterWire.Stealth) != 0) ReadStealth(ref r, c);
        if ((mask & CharacterWire.Ragdoll) != 0) c.RagdollPose = r.ReadBool() ? RagdollWire.Read(ref r) : null;
        if ((mask & CharacterWire.Look) != 0)
        {
            c.LookId = r.ReadVarUInt32();
            c.Scale = r.ReadFinite();
        }
        if ((mask & CharacterWire.Identity) != 0)
        {
            c.Name = WireText.ReadRequired(ref r, strings, NetLimits.MaxName);
            c.Faction = r.ReadVarInt32();
            c.SquadId = r.ReadVarInt32();
            c.Owner = r.ReadVarInt32();
            c.SurgeryCost = CommonCodec.ReadNullableInt(ref r);
            c.Activity = strings.Read(ref r, NetLimits.MaxName);
        }
        if ((mask & CharacterWire.Prison) != 0) c.PrisonView = ReadPrison(ref r);
        if ((mask & CharacterWire.Turret) != 0) c.TurretView = ReadTurret(ref r, strings);
        if ((mask & CharacterWire.Jobs) != 0) c.JobList = ReadJobs(ref r, strings);
        return c;
    }

    // Body: a presence byte, blood U16[0,1], worst part S12, hunger U16[0,4], then one varint of the flags
    // (b0 Unconscious, b1 Dead, b2 NeedsAid, b3-4 Care, b5 MedicJob, b6 RescueJob, b7 NeedsSplint).
    static void WriteBody(NetWriter w, BodyStatus? body)
    {
        w.WriteBool(body.HasValue);
        if (body is not { } b) return;
        w.WriteUInt16(Quant.U16(b.Blood, 0f, 1f));
        w.WriteUInt16((ushort)Quant.S12(b.WorstPart));
        w.WriteUInt16(Quant.U16(b.Hunger, 0f, 4f));
        w.WriteVarUInt((uint)((b.Unconscious ? 1 : 0) | (b.Dead ? 2 : 0) | (b.NeedsAid ? 4 : 0) | ((int)b.Care << 3) | (b.MedicJob ? 32 : 0)
            | (b.RescueJob ? 64 : 0) | (b.NeedsSplint ? 128 : 0)));
    }

    static BodyStatus? ReadBody(ref NetReader r)
    {
        if (!r.ReadBool()) return null;
        float blood = Quant.FromU16(r.ReadUInt16(), 0f, 1f);
        float worst = Quant.FromS12((short)r.ReadUInt16());
        float hunger = Quant.FromU16(r.ReadUInt16(), 0f, 4f);
        uint bits = r.ReadVarUInt32();
        int care = (int)((bits >> 3) & 3);
        if (bits > 255 || care > (int)CareKind.Resting) throw new NetFormatException($"Bad body status flags {bits}.");
        return new BodyStatus(blood, worst, hunger, (bits & 1) != 0, (bits & 2) != 0, (bits & 4) != 0, (CareKind)care, (bits & 32) != 0, (bits & 64) != 0, (bits & 128) != 0);
    }

    // Stealth: 0 for none else 1 + level; the KO chance as a nullable float; the watchers (count, then each position relative to the character in 1/64
    // units as zigzag, and a level byte).
    static void WriteStealth(NetWriter w, CharacterWire c)
    {
        w.WriteByte(c.StealthLevel is { } level ? (byte)(1 + (byte)level) : (byte)0);
        CommonCodec.WriteNullableSingle(w, c.StealthKoChance);
        if (c.Watchers.Length > CharacterWire.MaxWatchers) throw new ArgumentException($"{c.Watchers.Length} watchers exceed the limit of {CharacterWire.MaxWatchers}.", nameof(c));
        w.WriteVarUInt((uint)c.Watchers.Length);
        foreach (var watcher in c.Watchers)
        {
            w.WriteVarInt(watcher.X - (long)c.X);
            w.WriteVarInt(watcher.Y - (long)c.Y);
            w.WriteVarInt(watcher.Z - (long)c.Z);
            w.WriteEnum(watcher.Level);
        }
    }

    static void ReadStealth(ref NetReader r, CharacterWire c)
    {
        byte level = r.ReadByte();
        if (level == 0) c.StealthLevel = null;
        else if (NetEnum<StealthExposure>.IsValid((ulong)(level - 1))) c.StealthLevel = (StealthExposure)(level - 1);
        else throw new NetFormatException($"Unknown stealth level {level - 1}.");
        c.StealthKoChance = CommonCodec.ReadNullableSingle(ref r);
        int count = r.ReadCount(CharacterWire.MaxWatchers);
        var watchers = count == 0 ? [] : new WatcherWire[count];
        for (int i = 0; i < count; i++)
        {
            int x = WireText.AddPosition(c.X, r.ReadVarInt()), y = WireText.AddPosition(c.Y, r.ReadVarInt()), z = WireText.AddPosition(c.Z, r.ReadVarInt());
            watchers[i] = new WatcherWire(x, y, z, r.ReadEnum<StealthExposure>());
        }
        c.Watchers = watchers;
    }

    // Prison: a presence byte, Locked, Sentence (float), HoursLeft as zigzag minutes, Bail (nullable int).
    static void WritePrison(NetWriter w, PrisonWire? prison)
    {
        w.WriteBool(prison.HasValue);
        if (prison is not { } p) return;
        w.WriteBool(p.Locked);
        w.WriteSingle(p.Sentence);
        w.WriteVarInt(p.Minutes);
        CommonCodec.WriteNullableInt(w, p.Bail);
    }

    static PrisonWire? ReadPrison(ref NetReader r)
    {
        if (!r.ReadBool()) return null;
        bool locked = r.ReadBool();
        float sentence = r.ReadFinite();
        long minutes = r.ReadVarInt();
        return new PrisonWire(locked, sentence, minutes, CommonCodec.ReadNullableInt(ref r));
    }

    // Turret: a presence byte, Key, BuildingId, GunId (interned), the position as zigzag 1/64 units, the yaw as 16 bits.
    static void WriteTurret(NetWriter w, StringTableWriter strings, TurretWire? turret)
    {
        w.WriteBool(turret is not null);
        if (turret is null) return;
        strings.Write(w, turret.Key);
        strings.Write(w, turret.BuildingId);
        strings.Write(w, turret.GunId);
        w.WriteVarInt(turret.X);
        w.WriteVarInt(turret.Y);
        w.WriteVarInt(turret.Z);
        w.WriteUInt16((ushort)turret.Yaw);
    }

    static TurretWire? ReadTurret(ref NetReader r, StringTableReader strings)
    {
        if (!r.ReadBool()) return null;
        string key = WireText.ReadRequired(ref r, strings, NetLimits.MaxName);
        string building = WireText.ReadRequired(ref r, strings, NetLimits.MaxName);
        string gun = WireText.ReadRequired(ref r, strings, NetLimits.MaxName);
        int x = WireText.AddPosition(0, r.ReadVarInt()), y = WireText.AddPosition(0, r.ReadVarInt()), z = WireText.AddPosition(0, r.ReadVarInt());
        return new TurretWire(key, building, gun, x, y, z, (short)r.ReadUInt16());
    }

    // Jobs: a count (at most MaxJobs), then Type (zigzag), Site (interned, nullable), Saved (presence byte, then five zigzag ints; absent for Hand.Null).
    static void WriteJobs(NetWriter w, StringTableWriter strings, PermaJob[] jobs)
    {
        if (jobs.Length > CharacterWire.MaxJobs) throw new ArgumentException($"{jobs.Length} jobs exceed the limit of {CharacterWire.MaxJobs}.", nameof(jobs));
        w.WriteVarUInt((uint)jobs.Length);
        foreach (var job in jobs)
        {
            w.WriteEnum(job.Type);
            strings.Write(w, job.Site);
            w.WriteBool(!job.Saved.IsNull);
            if (job.Saved.IsNull) continue;
            w.WriteVarInt(job.Saved.Type);
            w.WriteVarInt(job.Saved.C);
            w.WriteVarInt(job.Saved.CS);
            w.WriteVarInt(job.Saved.I);
            w.WriteVarInt(job.Saved.S);
        }
    }

    static PermaJob[] ReadJobs(ref NetReader r, StringTableReader strings)
    {
        int count = r.ReadCount(CharacterWire.MaxJobs);
        if (count == 0) return [];
        var jobs = new PermaJob[count];
        for (int i = 0; i < count; i++)
        {
            var type = r.ReadEnum<TaskType>();
            string? site = strings.Read(ref r, NetLimits.MaxName);
            var saved = Hand.Null;
            if (r.ReadBool())
            {
                var hand = new Hand(r.ReadVarInt32(), r.ReadVarInt32(), r.ReadVarInt32(), r.ReadVarInt32(), r.ReadVarInt32());
                if (!hand.IsNull) saved = hand;
            }
            jobs[i] = new PermaJob(type, site, saved);
        }
        return jobs;
    }
}
