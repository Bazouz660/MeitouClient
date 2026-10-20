namespace Meitou.Net;

/// <summary>Where one peer's packets go: the replicator and the message senders write to it, the transport (or a test) implements it.</summary>
public interface IPacketSink
{
    /// <summary>Largest payload that fits one packet on the <see cref="NetChannel.Snapshots"/> channel; a snapshot part never exceeds it.</summary>
    int MaxUnreliablePayload { get; }

    /// <summary>Sends <paramref name="packet"/> (its first byte is the <see cref="MessageType"/>) on <paramref name="channel"/>. The sink copies it before returning.</summary>
    void Send(NetChannel channel, ReadOnlySpan<byte> packet);
}
