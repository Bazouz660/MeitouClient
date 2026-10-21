using System.Numerics;
using System.Text;
using Meitou.Data.World;
using Meitou.Engine.Time;
using Meitou.Net.Transport;

namespace Meitou.Net.Session;

/// <summary>Host to client: the challenge a connecting client answers (<see cref="Nonce"/> is 16 bytes).</summary>
public sealed record Challenge(byte[] Nonce, string HostName, bool NeedsPassword);

/// <summary>Client to host: who the client is. <see cref="Proof"/> is empty or 32 bytes (<see cref="Handshake.Proof"/>).</summary>
public sealed record Hello(ushort Protocol, string Build, Guid Player, string Name, byte[] Proof, DataSetIdentity Data, ulong AnimationHash);

/// <summary>Host to client: accepted as <see cref="Player"/>, with the lobby rules and the roster.</summary>
public sealed record Welcome(int Player, NetRules Rules, IReadOnlyList<RosterEntry> Roster);

/// <summary>Why a connection was refused or dropped; also the payload of a drop.</summary>
public sealed record Reject(RejectCode Code, string Reason);

/// <summary>Client to host: the world of <see cref="Epoch"/> is loaded; snapshots may start.</summary>
public sealed record Ready(ushort Epoch);

/// <summary>Host to client: a new world epoch. <see cref="Player"/> is the receiver's number (it can change with a world swap).</summary>
public sealed record WorldReset(ushort Epoch, int Player, ulong Seed, Vector3 Focus, ClockState Clock, SessionRules Rules, WeatherSync? Weather);

/// <summary>Host to client: the lobby and simulation rules changed.</summary>
public sealed record RulesUpdate(NetRules Net, SessionRules Session);

/// <summary>Client to host: one chat line (the host clamps it to <see cref="NetProtocol.MaxChatLength"/> characters).</summary>
public sealed record ChatRequest(string Text);

/// <summary>
/// The Control-channel protocol messages (docs/net.md, "Protocol and handshake"). <c>Write</c> puts the <see cref="MessageType"/> byte first; the caller
/// reads that byte (<see cref="ReadType"/>), dispatches, and the <c>ReadX</c> of the message reads the rest and demands its end. The <c>NetRules</c> and
/// <c>SessionRules</c> overloads write bodies only (they are parts of Welcome, WorldReset and Rules). Writers clamp texts to their reader limit (at a
/// character boundary) and throw <see cref="ArgumentException"/> for a fixed-size field of the wrong size or a non-finite clock or weather number, so a reader never refuses what they wrote.
/// </summary>
public static class ProtocolCodec
{
    /// <summary>Longest build string, in UTF-8 bytes.</summary>
    public const int MaxBuild = 64;

    /// <summary>
    /// Longest <see cref="Reject.Reason"/> written, in UTF-8 bytes: an encoded Reject (type byte, code byte, two length bytes, text) fits
    /// <see cref="INetTransport.MaxDropPayload"/>, as a drop or refusal payload must.
    /// </summary>
    public const int MaxRejectReason = INetTransport.MaxDropPayload - 4;

    /// <summary>Most files in a data set identity.</summary>
    public const int MaxDataFiles = 1024;

    /// <summary>Most roster entries in a message (players keep their number, so Away entries accumulate).</summary>
    public const int MaxRoster = 256;

    /// <summary>Most weather regions in a <see cref="WeatherSync"/>.</summary>
    public const int MaxWeatherRegions = 256;

    /// <summary>Reads the <see cref="MessageType"/> byte that starts every packet; a byte that is no message type is a format error.</summary>
    public static MessageType ReadType(ref NetReader r) => r.ReadEnum<MessageType>();

    // ---- Challenge, Hello, Welcome, Reject, Ready ----

    public static void Write(NetWriter w, Challenge m)
    {
        if (m.Nonce is not { Length: Handshake.NonceLength }) throw new ArgumentException($"A nonce is {Handshake.NonceLength} bytes.", nameof(m));
        w.WriteByte((byte)MessageType.Challenge);
        w.WriteBytes(m.Nonce);
        WriteText(w, m.HostName, NetLimits.MaxName);
        w.WriteBool(m.NeedsPassword);
    }

    public static Challenge ReadChallenge(ref NetReader r)
    {
        byte[] nonce = r.ReadBytes(Handshake.NonceLength).ToArray();
        var m = new Challenge(nonce, r.ReadUtf8(NetLimits.MaxName), r.ReadBool());
        r.ExpectEnd();
        return m;
    }

    public static void Write(NetWriter w, Hello m)
    {
        if (m.Proof is not { Length: 0 or Handshake.ProofLength }) throw new ArgumentException($"A proof is empty or {Handshake.ProofLength} bytes.", nameof(m));
        w.WriteByte((byte)MessageType.Hello);
        w.WriteUInt16(m.Protocol);
        WriteText(w, m.Build, MaxBuild);
        w.WriteGuid(m.Player);
        WriteText(w, m.Name, NetLimits.MaxName);
        w.WriteByte((byte)m.Proof.Length);
        w.WriteBytes(m.Proof);
        WriteDataSet(w, m.Data);
        w.WriteUInt64(m.AnimationHash);
    }

    public static Hello ReadHello(ref NetReader r)
    {
        ushort protocol = r.ReadUInt16();
        string build = r.ReadUtf8(MaxBuild);
        var player = r.ReadGuid();
        string name = r.ReadUtf8(NetLimits.MaxName);
        int proofLength = r.ReadByte();
        if (proofLength is not (0 or Handshake.ProofLength)) throw new NetFormatException($"A proof of {proofLength} bytes.");
        byte[] proof = r.ReadBytes(proofLength).ToArray();
        var data = ReadDataSet(ref r);
        var m = new Hello(protocol, build, player, name, proof, data, r.ReadUInt64());
        r.ExpectEnd();
        return m;
    }

    public static void Write(NetWriter w, Welcome m)
    {
        w.WriteByte((byte)MessageType.Welcome);
        w.WriteVarInt(m.Player);
        Write(w, m.Rules);
        WriteRosterBody(w, m.Roster);
    }

    public static Welcome ReadWelcome(ref NetReader r)
    {
        int player = r.ReadVarInt32();
        var rules = ReadNetRules(ref r);
        var m = new Welcome(player, rules, ReadRosterBody(ref r));
        r.ExpectEnd();
        return m;
    }

    public static void Write(NetWriter w, Reject m)
    {
        w.WriteByte((byte)MessageType.Reject);
        w.WriteEnum(m.Code);
        WriteText(w, m.Reason, MaxRejectReason);
    }

    public static Reject ReadReject(ref NetReader r)
    {
        var m = new Reject(r.ReadEnum<RejectCode>(), r.ReadUtf8(NetLimits.MaxMessageText));
        r.ExpectEnd();
        return m;
    }

    public static void Write(NetWriter w, Ready m)
    {
        w.WriteByte((byte)MessageType.Ready);
        w.WriteUInt16(m.Epoch);
    }

    public static Ready ReadReady(ref NetReader r)
    {
        var m = new Ready(r.ReadUInt16());
        r.ExpectEnd();
        return m;
    }

    // ---- WorldReset, Rules, Chat ----

    public static void Write(NetWriter w, WorldReset m)
    {
        w.WriteByte((byte)MessageType.WorldReset);
        w.WriteUInt16(m.Epoch);
        w.WriteVarInt(m.Player);
        w.WriteUInt64(m.Seed);
        CommonCodec.WriteVector3(w, m.Focus);
        WriteClockBody(w, m.Clock);
        Write(w, m.Rules);
        w.WriteBool(m.Weather is not null);
        if (m.Weather is not null) WriteWeatherBody(w, m.Weather);
    }

    public static WorldReset ReadWorldReset(ref NetReader r)
    {
        ushort epoch = r.ReadUInt16();
        int player = r.ReadVarInt32();
        ulong seed = r.ReadUInt64();
        var focus = CommonCodec.ReadVector3(ref r);
        var clock = ReadClockBody(ref r);
        var rules = ReadSessionRules(ref r);
        var weather = r.ReadBool() ? ReadWeatherBody(ref r) : null;
        r.ExpectEnd();
        return new WorldReset(epoch, player, seed, focus, clock, rules, weather);
    }

    public static void Write(NetWriter w, RulesUpdate m)
    {
        w.WriteByte((byte)MessageType.Rules);
        Write(w, m.Net);
        Write(w, m.Session);
    }

    public static RulesUpdate ReadRulesUpdate(ref NetReader r)
    {
        var net = ReadNetRules(ref r);
        var m = new RulesUpdate(net, ReadSessionRules(ref r));
        r.ExpectEnd();
        return m;
    }

    public static void Write(NetWriter w, ChatRequest m)
    {
        w.WriteByte((byte)MessageType.ChatRequest);
        WriteText(w, m.Text, NetLimits.MaxChat);
    }

    public static ChatRequest ReadChatRequest(ref NetReader r)
    {
        var m = new ChatRequest(r.ReadUtf8(NetLimits.MaxChat));
        r.ExpectEnd();
        return m;
    }

    // ---- Clock ----

    public static void Write(NetWriter w, ClockRequest m)
    {
        w.WriteByte((byte)MessageType.ClockRequest);
        w.WriteEnum(m.Kind);
        WriteFiniteDouble(w, m.Speed);
    }

    public static ClockRequest ReadClockRequest(ref NetReader r)
    {
        var kind = r.ReadEnum<ClockRequestKind>();
        var m = new ClockRequest(kind, ReadFiniteDouble(ref r));
        r.ExpectEnd();
        return m;
    }

    public static void Write(NetWriter w, ClockState m)
    {
        w.WriteByte((byte)MessageType.ClockState);
        WriteClockBody(w, m);
    }

    public static ClockState ReadClockState(ref NetReader r)
    {
        var m = ReadClockBody(ref r);
        r.ExpectEnd();
        return m;
    }

    static void WriteClockBody(NetWriter w, ClockState m)
    {
        w.WriteUInt16(m.Epoch);
        WriteFiniteDouble(w, m.Speed);
        WriteFiniteDouble(w, m.LastSpeed);
        w.WriteBool(m.HostBusy);
        WriteFiniteDouble(w, m.AchievedSpeed);
        w.WriteVarInt(m.AnchorTick);
        WriteFiniteDouble(w, m.AnchorHours);
        w.WriteVarInt(m.Change.Player);
        w.WriteEnum(m.Change.Kind);
        WriteFiniteDouble(w, m.Change.Speed);
    }

    static ClockState ReadClockBody(ref NetReader r)
    {
        ushort epoch = r.ReadUInt16();
        double speed = ReadFiniteDouble(ref r), lastSpeed = ReadFiniteDouble(ref r);
        bool busy = r.ReadBool();
        double achieved = ReadFiniteDouble(ref r);
        long anchorTick = r.ReadVarInt();
        double anchorHours = ReadFiniteDouble(ref r);
        int player = r.ReadVarInt32();
        var kind = r.ReadEnum<ClockChangeKind>();
        return new ClockState(epoch, speed, lastSpeed, busy, achieved, anchorTick, anchorHours, new ClockChange(player, kind, ReadFiniteDouble(ref r)));
    }

    // ---- Rules ----

    /// <summary>The body of <see cref="NetRules"/>: send rate, clock policy, max players, a flags byte (cheats, password), port.</summary>
    public static void Write(NetWriter w, NetRules m)
    {
        w.WriteVarInt(m.SendRate);
        w.WriteEnum(m.ClockPolicy);
        w.WriteVarInt(m.MaxPlayers);
        w.WriteByte((byte)((m.Cheats ? 1 : 0) | (m.HasPassword ? 2 : 0)));
        w.WriteVarInt(m.Port);
    }

    public static NetRules ReadNetRules(ref NetReader r)
    {
        int sendRate = r.ReadVarInt32();
        var policy = r.ReadEnum<ClockPolicy>();
        int maxPlayers = r.ReadVarInt32();
        byte flags = r.ReadByte();
        if ((flags & ~3) != 0) throw new NetFormatException($"Unknown rule flags {flags}.");
        return new NetRules(sendRate, policy, maxPlayers, (flags & 1) != 0, (flags & 2) != 0, r.ReadVarInt32());
    }

    /// <summary>The body of <see cref="SessionRules"/>: attack slots, then a flags byte with the eight switches in declaration order.</summary>
    public static void Write(NetWriter w, SessionRules m)
    {
        w.WriteVarInt(m.AttackSlots);
        int flags = (m.LateDefences ? 1 : 0) | (m.DoorsCloseBehind ? 2 : 0) | (m.KeepRoamers ? 4 : 0) | (m.OffscreenFights ? 8 : 0)
            | (m.OffscreenHealing ? 16 : 0) | (m.CampaignAncestorsOnce ? 32 : 0) | (m.KeepWild ? 64 : 0) | (m.LuckyrenMode ? 128 : 0);
        w.WriteByte((byte)flags);
    }

    public static SessionRules ReadSessionRules(ref NetReader r)
    {
        int slots = r.ReadVarInt32();
        byte f = r.ReadByte();
        return new SessionRules(slots, (f & 1) != 0, (f & 2) != 0, (f & 4) != 0, (f & 8) != 0, (f & 16) != 0, (f & 32) != 0, (f & 64) != 0, (f & 128) != 0);
    }

    // ---- Roster ----

    /// <summary>The Roster message: a count (at most <see cref="MaxRoster"/>; a longer list is cut) and the entries.</summary>
    public static void Write(NetWriter w, IReadOnlyList<RosterEntry> roster)
    {
        w.WriteByte((byte)MessageType.Roster);
        WriteRosterBody(w, roster);
    }

    public static IReadOnlyList<RosterEntry> ReadRoster(ref NetReader r)
    {
        var roster = ReadRosterBody(ref r);
        r.ExpectEnd();
        return roster;
    }

    static void WriteRosterBody(NetWriter w, IReadOnlyList<RosterEntry> roster)
    {
        int count = Math.Min(roster.Count, MaxRoster);
        w.WriteVarUInt((uint)count);
        for (int i = 0; i < count; i++)
        {
            var e = roster[i];
            w.WriteVarInt(e.Player);
            WriteText(w, e.Name, NetLimits.MaxName);
            w.WriteEnum(e.State);
            w.WriteVarInt(e.RoundTripMs);
            w.WriteVarInt(e.Colour);
            w.WriteVarInt(e.Squads);
        }
    }

    static List<RosterEntry> ReadRosterBody(ref NetReader r)
    {
        int count = r.ReadCount(MaxRoster);
        var roster = new List<RosterEntry>(count);
        for (int i = 0; i < count; i++)
            roster.Add(new RosterEntry(r.ReadVarInt32(), r.ReadUtf8(NetLimits.MaxName), r.ReadEnum<NetPlayerState>(), r.ReadVarInt32(), r.ReadVarInt32(), r.ReadVarInt32()));
        return roster;
    }

    // ---- Weather ----

    /// <summary>The Weather message: the revision, forced weather, day, hour and every region's state (a <see cref="WeatherSync"/>).</summary>
    public static void Write(NetWriter w, WeatherSync m)
    {
        w.WriteByte((byte)MessageType.Weather);
        WriteWeatherBody(w, m);
    }

    public static WeatherSync ReadWeather(ref NetReader r)
    {
        var m = ReadWeatherBody(ref r);
        r.ExpectEnd();
        return m;
    }

    static void WriteWeatherBody(NetWriter w, WeatherSync m)
    {
        w.WriteVarInt(m.Revision);
        w.WriteBool(m.Forced is not null);
        if (m.Forced is not null) WriteText(w, m.Forced, NetLimits.MaxName);
        w.WriteVarInt(m.Day);
        WriteFiniteDouble(w, m.Hour);
        int count = Math.Min(m.Regions.Count, MaxWeatherRegions);
        w.WriteVarUInt((uint)count);
        for (int i = 0; i < count; i++)
        {
            var g = m.Regions[i];
            WriteText(w, g.RegionId, NetLimits.MaxName);
            WriteText(w, g.SeasonId, NetLimits.MaxName);
            w.WriteVarInt(g.SeasonIndex);
            w.WriteVarInt(g.SeasonEndDay);
            WriteText(w, g.WeatherId, NetLimits.MaxName);
            w.WriteVarInt(g.WeatherEntryIndex);
            WriteFiniteDouble(w, g.WeatherStartMinute);
            WriteFiniteDouble(w, g.WeatherEndMinute);
            WriteFiniteSingle(w, g.Strength);
            WriteFiniteDouble(w, g.Wind.StartMinute);
            WriteFiniteDouble(w, g.Wind.DurationMinutes);
            WriteFiniteSingle(w, g.Wind.Speed0);
            WriteFiniteSingle(w, g.Wind.Heading0);
            WriteFiniteSingle(w, g.Wind.Speed1);
            WriteFiniteSingle(w, g.Wind.Heading1);
            WriteFiniteDouble(w, g.NextWindUpdateMinute);
            WriteFiniteSingle(w, g.FogFrom.Enabled);
            CommonCodec.WriteVector3(w, g.FogFrom.Colour);
            WriteFiniteSingle(w, g.FogFrom.Distance);
            WriteFiniteDouble(w, g.FogFadeMinutes);
        }
    }

    static WeatherSync ReadWeatherBody(ref NetReader r)
    {
        int revision = r.ReadVarInt32();
        string? forced = r.ReadBool() ? r.ReadUtf8(NetLimits.MaxName) : null;
        int day = r.ReadVarInt32();
        double hour = ReadFiniteDouble(ref r);
        int count = r.ReadCount(MaxWeatherRegions);
        var regions = new List<WeatherRegionSnapshot>(count);
        for (int i = 0; i < count; i++)
        {
            string regionId = r.ReadUtf8(NetLimits.MaxName), seasonId = r.ReadUtf8(NetLimits.MaxName);
            int seasonIndex = r.ReadVarInt32(), seasonEndDay = r.ReadVarInt32();
            string weatherId = r.ReadUtf8(NetLimits.MaxName);
            int entry = r.ReadVarInt32();
            double start = ReadFiniteDouble(ref r), end = ReadFiniteDouble(ref r);
            float strength = r.ReadFinite();
            var wind = new WindInterpolation(ReadFiniteDouble(ref r), ReadFiniteDouble(ref r), r.ReadFinite(), r.ReadFinite(), r.ReadFinite(), r.ReadFinite());
            double nextWind = ReadFiniteDouble(ref r);
            float enabled = r.ReadFinite();
            var colour = CommonCodec.ReadVector3(ref r);
            var fog = new RegionFog(enabled, colour, r.ReadFinite());
            regions.Add(new WeatherRegionSnapshot(regionId, seasonId, seasonIndex, seasonEndDay, weatherId, entry, start, end, strength, wind, nextWind, fog, ReadFiniteDouble(ref r)));
        }
        return new WeatherSync(revision, forced, day, hour, regions);
    }

    // ---- Data set ----

    static void WriteDataSet(NetWriter w, DataSetIdentity data)
    {
        int count = data.Files.Count;
        if (count > MaxDataFiles) throw new ArgumentException($"A data set of {count} files exceeds the limit of {MaxDataFiles}.", nameof(data));
        w.WriteVarUInt((uint)count);
        foreach (var file in data.Files)
        {
            WriteText(w, file.Name, NetLimits.MaxName);
            w.WriteUInt64(file.Hash);
            w.WriteVarInt(file.Length);
        }
    }

    static DataSetIdentity ReadDataSet(ref NetReader r)
    {
        int count = r.ReadCount(MaxDataFiles);
        var files = new List<DataSetEntry>(count);
        for (int i = 0; i < count; i++) files.Add(new DataSetEntry(r.ReadUtf8(NetLimits.MaxName), r.ReadUInt64(), r.ReadVarInt()));
        return new DataSetIdentity(files);
    }

    // ---- Helpers ----

    static void WriteFiniteDouble(NetWriter w, double v)
    {
        if (!double.IsFinite(v)) throw new ArgumentException("A clock or weather number is not finite.");
        w.WriteDouble(v);
    }

    static void WriteFiniteSingle(NetWriter w, float v)
    {
        if (!float.IsFinite(v)) throw new ArgumentException("A weather number is not finite.");
        w.WriteSingle(v);
    }

    static double ReadFiniteDouble(ref NetReader r)
    {
        double v = r.ReadDouble();
        if (!double.IsFinite(v)) throw new NetFormatException("A non-finite double.");
        return v;
    }

    /// <summary>Writes <paramref name="text"/> cut to at most <paramref name="maxBytes"/> UTF-8 bytes, never inside a character or surrogate pair.</summary>
    static void WriteText(NetWriter w, string? text, int maxBytes)
    {
        text ??= "";
        if (Encoding.UTF8.GetByteCount(text) <= maxBytes)
        {
            w.WriteUtf8(text);
            return;
        }
        int bytes = 0, end = 0;
        while (end < text.Length)
        {
            int units = char.IsHighSurrogate(text[end]) && end + 1 < text.Length && char.IsLowSurrogate(text[end + 1]) ? 2 : 1;
            int size = Encoding.UTF8.GetByteCount(text.AsSpan(end, units));
            if (bytes + size > maxBytes) break;
            bytes += size;
            end += units;
        }
        w.WriteUtf8(text[..end]);
    }
}
