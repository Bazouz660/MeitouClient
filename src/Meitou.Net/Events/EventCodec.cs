using System.Numerics;
using System.Text;
using Meitou.Simulation;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Carrying;
using Meitou.Simulation.Combat;

namespace Meitou.Net.Events;

/// <summary>
/// <see cref="EventBatch"/> as the <see cref="MessageType.EventBatch"/> message on the Events channel (docs/net.md, "Events"). Writing clamps what the
/// reader would refuse: texts are cut to their byte limit on a character boundary, non-finite numbers become 0, an undefined enum value throws <see cref="ArgumentException"/> and a batch is cut to <see cref="MaxEvents"/> events.
/// </summary>
public static class EventCodec
{
    /// <summary>Most events in one batch.</summary>
    public const int MaxEvents = 1024;

    const byte KindSound = 1, KindBlood = 2, KindFloater = 3, KindMessage = 4, KindKnockout = 5, KindChat = 7, KindNotice = 8;

    /// <summary>Type byte, epoch u16, tick (zigzag varint), count, then each event as a kind byte and its fields. Events beyond <see cref="MaxEvents"/> are dropped.</summary>
    public static void Write(NetWriter w, EventBatch batch)
    {
        w.WriteByte((byte)MessageType.EventBatch);
        w.WriteUInt16(batch.Epoch);
        w.WriteVarInt(batch.Tick);
        int count = Math.Min(batch.Events.Count, MaxEvents);
        w.WriteVarUInt((uint)count);
        for (int i = 0; i < count; i++) WriteEvent(w, batch.Events[i]);
    }

    public static EventBatch Read(ref NetReader r)
    {
        byte type = r.ReadByte();
        if (type != (byte)MessageType.EventBatch) throw new NetFormatException($"Message type {type} is not an event batch.");
        ushort epoch = r.ReadUInt16();
        long tick = r.ReadVarInt();
        int count = r.ReadCount(MaxEvents);
        var events = new NetEvent[count];
        for (int i = 0; i < count; i++) events[i] = ReadEvent(ref r);
        return new EventBatch(epoch, tick, events);
    }

    static void WriteEvent(NetWriter w, NetEvent e)
    {
        switch (e)
        {
            case SoundNet { Sound: var s }:
                w.WriteByte(KindSound);
                WriteKind(w, s.Kind);
                CommonCodec.WriteId(w, s.Subject);
                CommonCodec.WriteId(w, s.Other);
                WritePosition(w, s.Position);
                w.WriteByte((byte)((s.Heavy ? 1 : 0) | (s.AttackerUnarmed ? 2 : 0) | (s.AttackerRobot ? 4 : 0) | (s.Armoured ? 8 : 0) | (s.Fatal ? 16 : 0) | (s.Item is not null ? 32 : 0)));
                if (s.Item is not null) WriteText(w, s.Item, NetLimits.MaxName);
                break;
            case BloodNet { Mark: var m }:
                w.WriteByte(KindBlood);
                WriteKind(w, m.Kind);
                CommonCodec.WriteId(w, m.Who);
                WritePosition(w, m.Position);
                WriteHalf(w, m.Direction.X);
                WriteHalf(w, m.Direction.Y);
                WriteHalf(w, m.Amount);
                w.WriteByte((byte)(m.Colour >> 16));
                w.WriteByte((byte)(m.Colour >> 8));
                w.WriteByte((byte)m.Colour);
                w.WriteBool(m.Race is not null);
                if (m.Race is not null) WriteText(w, m.Race, NetLimits.MaxName);
                break;
            case FloaterNet { Floater: var f }:
                w.WriteByte(KindFloater);
                WriteKind(w, f.Kind);
                CommonCodec.WriteId(w, f.Target);
                WritePosition(w, f.Position);
                w.WriteByte((byte)((f.TargetIsPlayer ? 1 : 0) | (f.SourceIsPlayer ? 2 : 0)));
                WriteText(w, f.Part, NetLimits.MaxName);
                WriteFinite(w, f.Damage.Cut);
                WriteFinite(w, f.Damage.Blunt);
                WriteFinite(w, f.Damage.Pierce);
                WriteFinite(w, f.Damage.ExtraStun);
                WriteFinite(w, f.Damage.Scale);
                WriteFinite(w, f.Absorbed.Cut);
                WriteFinite(w, f.Absorbed.Blunt);
                WriteFinite(w, f.Absorbed.Pierce);
                WriteFinite(w, f.Healed);
                break;
            case MessageNet m:
                w.WriteByte(KindMessage);
                WriteText(w, m.Text, NetLimits.MaxMessageText);
                break;
            case KnockoutNet { Entry: var k }:
                w.WriteByte(KindKnockout);
                w.WriteVarInt(k.Tick);
                CommonCodec.WriteId(w, k.Attacker);
                CommonCodec.WriteId(w, k.Target);
                WriteFinite(w, k.Chance);
                w.WriteBool(k.Success);
                break;
            case ChatNet c:
                w.WriteByte(KindChat);
                w.WriteVarInt(c.From);
                WriteText(w, c.Name, NetLimits.MaxName);
                WriteText(w, c.Text, NetLimits.MaxChat);
                break;
            case NoticeNet n:
                w.WriteByte(KindNotice);
                WriteKind(w, n.Kind);
                w.WriteVarInt(n.Player);
                WriteText(w, n.Name, NetLimits.MaxName);
                WriteText(w, n.Detail, NetLimits.MaxMessageText);
                break;
            default:
                throw new ArgumentException($"{e.GetType().Name} has no wire kind.", nameof(e));
        }
    }

    static NetEvent ReadEvent(ref NetReader r)
    {
        byte kind = r.ReadByte();
        switch (kind)
        {
            case KindSound:
            {
                var k = r.ReadEnum<SoundKind>();
                var subject = CommonCodec.ReadId(ref r);
                var other = CommonCodec.ReadId(ref r);
                var at = ReadPosition(ref r);
                byte flags = r.ReadByte();
                if ((flags & ~63) != 0) throw new NetFormatException($"Sound flags {flags} use unknown bits.");
                string? item = (flags & 32) != 0 ? r.ReadUtf8(NetLimits.MaxName) : null;
                return new SoundNet(new SoundEvent(k, subject, other, at, (flags & 1) != 0, (flags & 2) != 0, (flags & 4) != 0, (flags & 8) != 0, (flags & 16) != 0, item));
            }
            case KindBlood:
            {
                var k = r.ReadEnum<BloodMarkKind>();
                var who = CommonCodec.ReadId(ref r);
                var at = ReadPosition(ref r);
                float dx = ReadHalfFinite(ref r), dy = ReadHalfFinite(ref r), amount = ReadHalfFinite(ref r);
                uint colour = ((uint)r.ReadByte() << 16) | ((uint)r.ReadByte() << 8) | r.ReadByte();
                string? race = r.ReadBool() ? r.ReadUtf8(NetLimits.MaxName) : null;
                return new BloodNet(new BloodMark(k, who, at, new Vector2(dx, dy), amount, colour, race));
            }
            case KindFloater:
            {
                var k = r.ReadEnum<FloaterKind>();
                var target = CommonCodec.ReadId(ref r);
                var at = ReadPosition(ref r);
                byte flags = r.ReadByte();
                if ((flags & ~3) != 0) throw new NetFormatException($"Floater flags {flags} use unknown bits.");
                string part = r.ReadUtf8(NetLimits.MaxName);
                var damage = new HitDamage(r.ReadFinite(), r.ReadFinite(), r.ReadFinite(), r.ReadFinite(), r.ReadFinite());
                var absorbed = new ArmourAbsorbed(r.ReadFinite(), r.ReadFinite(), r.ReadFinite());
                return new FloaterNet(new Floater(k, target, at, (flags & 1) != 0, (flags & 2) != 0, part, damage, absorbed, r.ReadFinite()));
            }
            case KindMessage:
                return new MessageNet(r.ReadUtf8(NetLimits.MaxMessageText));
            case KindKnockout:
            {
                long tick = r.ReadVarInt();
                var attacker = CommonCodec.ReadId(ref r);
                var target = CommonCodec.ReadId(ref r);
                float chance = r.ReadFinite();
                return new KnockoutNet(new StealthKoEntry(tick, attacker, target, chance, r.ReadBool()));
            }
            case KindChat:
            {
                int from = r.ReadVarInt32();
                string name = r.ReadUtf8(NetLimits.MaxName);
                return new ChatNet(from, name, r.ReadUtf8(NetLimits.MaxChat));
            }
            case KindNotice:
            {
                var k = r.ReadEnum<NoticeKind>();
                int player = r.ReadVarInt32();
                string name = r.ReadUtf8(NetLimits.MaxName);
                return new NoticeNet(k, player, name, r.ReadUtf8(NetLimits.MaxMessageText));
            }
            default:
                throw new NetFormatException($"Unknown event kind {kind}.");
        }
    }

    static void WritePosition(NetWriter w, Vector3 v)
    {
        w.WriteVarInt(Quant.Q64(v.X));
        w.WriteVarInt(Quant.Q64(v.Y));
        w.WriteVarInt(Quant.Q64(v.Z));
    }

    static Vector3 ReadPosition(ref NetReader r) =>
        new(Quant.FromQ64(r.ReadVarInt32()), Quant.FromQ64(r.ReadVarInt32()), Quant.FromQ64(r.ReadVarInt32()));

    /// <summary>An enum value that is a defined member, so the reader's check never refuses it; else <see cref="ArgumentException"/>.</summary>
    static void WriteKind<T>(NetWriter w, T value) where T : struct, Enum
    {
        if (!Enum.IsDefined(value)) throw new ArgumentException($"{value} is not a defined {typeof(T).Name}.");
        w.WriteEnum(value);
    }

    static void WriteFinite(NetWriter w, float v) => w.WriteSingle(float.IsFinite(v) ? v : 0);

    /// <summary>A half; out-of-range values saturate at the largest half and NaN becomes 0, so the reader's finite check never refuses it.</summary>
    static void WriteHalf(NetWriter w, float v)
    {
        float c = float.IsNaN(v) ? 0 : Math.Clamp(v, (float)Half.MinValue, (float)Half.MaxValue);
        w.WriteHalf((Half)c);
    }

    static float ReadHalfFinite(ref NetReader r)
    {
        var h = r.ReadHalf();
        if (!Half.IsFinite(h)) throw new NetFormatException("A half-precision value is not finite.");
        return (float)h;
    }

    static void WriteText(NetWriter w, string? text, int maxBytes) => w.WriteUtf8(Clamp(text ?? "", maxBytes));

    /// <summary>A string cut to at most <paramref name="maxBytes"/> UTF-8 bytes without splitting a character, so the reader never refuses it.</summary>
    internal static string Clamp(string text, int maxBytes)
    {
        if (text.Length <= maxBytes / 3 || Encoding.UTF8.GetByteCount(text) <= maxBytes) return text;
        int bytes = 0, end = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            bytes += rune.Utf8SequenceLength;
            if (bytes > maxBytes) break;
            end += rune.Utf16SequenceLength;
        }
        return text[..end];
    }
}
