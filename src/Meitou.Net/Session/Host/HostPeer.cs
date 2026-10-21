using Meitou.Net.Events;
using Meitou.Net.Replication;
using Meitou.Net.Transport;

namespace Meitou.Net.Session;

/// <summary>Where a connection is in the host's eyes; <see cref="Away"/> players keep only their roster entry.</summary>
enum HostPeerState : byte { Challenged, Loading, Playing }

/// <summary>One connected client of a <see cref="NetHost"/> (docs/net.md, "Host"): its handshake state, player, replicator, string table and rate limits.</summary>
sealed class HostPeer(NetConnectionId id, string address, long connectedUs)
{
    public readonly NetConnectionId Id = id;
    public readonly string Address = address;
    public HostPeerState State = HostPeerState.Challenged;
    /// <summary>When the current state began (Hello and load timeouts).</summary>
    public long SinceUs = connectedUs;
    public byte[] Nonce = [];
    public int Player = -1;
    public Guid Guid;
    public string Name = "";
    public int Colour = -1;
    public ClientReplicator? Replicator;
    public PeerSink? Sink;
    /// <summary>The client-to-host Control string table (command batches).</summary>
    public StringTableReader Strings = new();
    public long NextSendUs;
    /// <summary>Targeted events (messages, chat, notices) waiting for this frame's batch.</summary>
    public readonly List<NetEvent> Pending = [];
    /// <summary>A <c>WorldReset</c> owed after a world swap, sent at the next <see cref="NetHost.EndFrame"/>.</summary>
    public bool ResetOwed;

    // Token buckets (tokens, last refill in microseconds).
    public double CommandTokens = NetHost.CommandBurst;
    public long CommandRefillUs = connectedUs;
    public double ChatTokens = NetHost.ChatLines;
    public long ChatRefillUs = connectedUs;
    public long LastRefusalUs = long.MinValue / 2, LastThrottleNoticeUs = long.MinValue / 2;
    public int RefusalsThisSecond;

    // Byte rates from the transport's counters.
    public long StatsAtUs = connectedUs, BytesSentAt, BytesReceivedAt;
    public int InPerSecond, OutPerSecond;

    public bool InGame => State is HostPeerState.Loading or HostPeerState.Playing;

    /// <summary>Refills a token bucket of <paramref name="capacity"/> at <paramref name="perSecond"/> and takes one token; false when empty.</summary>
    public static bool Take(ref double tokens, ref long refillUs, long nowUs, double perSecond, double capacity)
    {
        if (nowUs > refillUs)
        {
            tokens = Math.Min(capacity, tokens + (nowUs - refillUs) / 1e6 * perSecond);
            refillUs = nowUs;
        }
        if (tokens < 1) return false;
        tokens -= 1;
        return true;
    }
}
