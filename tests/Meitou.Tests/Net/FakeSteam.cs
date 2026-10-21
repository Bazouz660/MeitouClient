using Meitou.Net.Transport;
using Meitou.Net.Transport.Steam;

namespace Meitou.Tests.Net;

/// <summary>
/// Steam users in memory for the Steam transport's tests (docs/net.md "Steam transport"), with Steam's session rules as the transport relies on them:
/// a user's first packets to someone who never sent to them wait, unseen, until the receiver accepts the session from <see cref="ISteamP2P.SessionRequested"/>
/// (raised in its next <see cref="ISteamP2P.RunCallbacks"/>); sending to a user opens the session for their replies; each channel is FIFO; every
/// packet is due <see cref="LatencyMs"/> after it was sent by <see cref="Clock"/>.
/// </summary>
sealed class FakeSteamNetwork(INetClock clock)
{
    public INetClock Clock { get; } = clock;
    public int LatencyMs { get; set; }
    /// <summary>While set, every packet sent is lost.</summary>
    public bool Severed { get; set; }
    /// <summary>While set, unreliable packets wait in <see cref="FakeSteam"/>'s held list until <see cref="FakeSteam.ReleaseUnreliable"/>.</summary>
    public bool HoldUnreliable { get; set; }
    readonly Dictionary<ulong, FakeSteam> users = [];

    public FakeSteam Add(ulong id, string name, uint appId = SteamNative.KenshiAppId)
    {
        var user = new FakeSteam(this, id, name, appId);
        users.Add(id, user);
        return user;
    }

    public FakeSteam? User(ulong id) => users.GetValueOrDefault(id);

    internal void Deliver(ulong from, ulong to, byte[] data, SteamSendType type, int channel)
    {
        if (Severed || !users.TryGetValue(to, out var target)) return;
        target.Arrive(from, data, type, channel, Clock.NowMicroseconds + LatencyMs * 1000L);
    }
}

sealed class FakeSteam(FakeSteamNetwork network, ulong id, string name, uint appId) : ISteamP2P
{
    public readonly record struct Packet(ulong From, byte[] Data, long Due);

    readonly List<Packet>[] inbox = [[], [], [], []];
    readonly HashSet<ulong> open = [];
    readonly Dictionary<ulong, List<(Packet Packet, int Channel)>> waiting = [];
    readonly List<(Packet Packet, int Channel)> heldUnreliable = [];
    readonly Queue<Action> callbacks = [];
    public Dictionary<string, string> Presence { get; } = [];
    public List<ulong> FriendIds { get; } = [];
    public List<ulong> Accepted { get; } = [];
    public uint AppId { get; } = appId;

    public ulong LocalId { get; } = id;
    public string PersonaName { get; } = name;
    public event Action<ulong>? SessionRequested;
    public event Action<ulong, byte>? SessionFailed;
    public event Action<ulong, string>? JoinRequested;

    internal void Arrive(ulong from, byte[] data, SteamSendType type, int channel, long due)
    {
        var packet = new Packet(from, data, due);
        if (type is SteamSendType.Unreliable or SteamSendType.UnreliableNoDelay && network.HoldUnreliable)
        {
            heldUnreliable.Add((packet, channel));
            return;
        }
        if (open.Contains(from))
        {
            inbox[channel].Add(packet);
            return;
        }
        if (!waiting.TryGetValue(from, out var list))
        {
            waiting[from] = list = [];
            callbacks.Enqueue(() => SessionRequested?.Invoke(from));
        }
        list.Add((packet, channel));
    }

    /// <summary>Puts the held unreliable packets in the inbox, newest first when <paramref name="reversed"/>.</summary>
    public void ReleaseUnreliable(bool reversed)
    {
        var held = reversed ? Enumerable.Reverse(heldUnreliable).ToList() : [.. heldUnreliable];
        heldUnreliable.Clear();
        foreach (var (packet, channel) in held) inbox[channel].Add(packet with { Due = 0 });
    }

    public void Fail(ulong remote, byte error) => callbacks.Enqueue(() => SessionFailed?.Invoke(remote, error));

    public void RequestJoin(ulong friend, string connect) => callbacks.Enqueue(() => JoinRequested?.Invoke(friend, connect));

    public void RunCallbacks()
    {
        while (callbacks.TryDequeue(out var callback)) callback();
    }

    public bool Send(ulong remote, ReadOnlySpan<byte> data, SteamSendType type, int channel)
    {
        if (remote == 0 || channel is < 0 or > 3) return false;
        if (type is SteamSendType.Unreliable or SteamSendType.UnreliableNoDelay ? data.Length > 1200 : data.Length > 1024 * 1024) return false;
        open.Add(remote);
        network.Deliver(LocalId, remote, data.ToArray(), type, channel);
        return true;
    }

    bool Due(int channel) => inbox[channel].Count > 0 && inbox[channel][0].Due <= network.Clock.NowMicroseconds;

    public bool IsPacketAvailable(int channel, out int size)
    {
        size = Due(channel) ? inbox[channel][0].Data.Length : 0;
        return Due(channel);
    }

    public bool ReadPacket(int channel, Span<byte> buffer, out int size, out ulong remote)
    {
        (size, remote) = (0, 0);
        if (!Due(channel)) return false;
        var packet = inbox[channel][0];
        inbox[channel].RemoveAt(0);
        packet.Data.CopyTo(buffer);
        (size, remote) = (packet.Data.Length, packet.From);
        return true;
    }

    public bool AcceptSession(ulong remote)
    {
        Accepted.Add(remote);
        open.Add(remote);
        if (waiting.Remove(remote, out var list))
            foreach (var (packet, channel) in list) inbox[channel].Add(packet);
        return true;
    }

    public void CloseSession(ulong remote)
    {
        open.Remove(remote);
        waiting.Remove(remote);
    }

    public SteamSessionState? SessionState(ulong remote) => open.Contains(remote) ? new SteamSessionState(true, false, 0, false, 0, 0) : null;

    public IReadOnlyList<ulong> Friends() => FriendIds;

    public string FriendName(ulong friend) => network.User(friend)?.PersonaName ?? "";

    public uint FriendAppId(ulong friend) => network.User(friend)?.AppId ?? 0;

    public string FriendRichPresence(ulong friend, string key) => network.User(friend)?.Presence.GetValueOrDefault(key) ?? "";

    public void RequestFriendRichPresence(ulong friend) { }

    public bool SetRichPresence(string key, string? value)
    {
        if (string.IsNullOrEmpty(value)) Presence.Remove(key);
        else Presence[key] = value;
        return true;
    }

    public void ClearRichPresence() => Presence.Clear();
}
