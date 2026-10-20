using System.Text;
using Meitou.Simulation;

namespace Meitou.Net.Commands;

/// <summary>
/// Up to <see cref="NetLimits.MaxCommandBatch"/> commands a client sends in one reliable message (docs/net.md, "Command codec"). Sequence numbers are consecutive
/// per client and per connection: command <c>i</c> of the batch is <c>FirstSeq + i</c>, which is what a <see cref="CommandResult"/> names.
/// </summary>
public sealed record CommandBatch(ushort Epoch, uint FirstSeq, IReadOnlyList<SimCommand> Commands);

/// <summary>The host's refusal of command <paramref name="Seq"/> (sent only for refusals, at most twice a second per peer); <paramref name="Reason"/> is the gate's text.</summary>
public sealed record CommandResult(uint Seq, string Reason);

/// <summary>
/// The bodies of the <c>CommandBatch</c> (0x20) and <c>CommandResult</c> (0x21) messages, without the leading <see cref="MessageType"/> byte, which the
/// caller writes and reads. Layout: batch = epoch u16, first seq varint, count (at most 64), the commands (<see cref="CommandCodec"/>); result = seq varint,
/// reason (UTF-8 text, at most <see cref="NetLimits.MaxMessageText"/> bytes).
/// </summary>
public static class CommandBatchCodec
{
    /// <summary>
    /// Writes the batch. <see cref="ArgumentException"/> when it holds more than <see cref="NetLimits.MaxCommandBatch"/> commands or would exceed
    /// <see cref="NetLimits.MaxReliableMessage"/> bytes with the <see cref="MessageType"/> byte the caller adds (the sender splits it); the writer and the string table are then left as they were.
    /// </summary>
    public static void Write(NetWriter w, StringTableWriter strings, CommandBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Commands.Count > NetLimits.MaxCommandBatch)
            throw new ArgumentException($"A batch of {batch.Commands.Count} commands exceeds the limit of {NetLimits.MaxCommandBatch}.", nameof(batch));
        int start = w.Length;
        int mark = strings.Mark();
        try
        {
            w.WriteUInt16(batch.Epoch);
            w.WriteVarUInt(batch.FirstSeq);
            w.WriteVarUInt((uint)batch.Commands.Count);
            foreach (var command in batch.Commands) CommandCodec.Write(w, strings, command);
            if (w.Length - start + 1 > NetLimits.MaxReliableMessage)
                throw new ArgumentException($"A batch of {w.Length - start + 1} bytes exceeds the limit of {NetLimits.MaxReliableMessage}.", nameof(batch));
        }
        catch
        {
            w.Truncate(start);
            strings.Rollback(mark);
            throw;
        }
    }

    /// <summary>Reads a batch; every command gets <paramref name="tick"/> and <paramref name="player"/>. Any bad command fails the whole batch with <see cref="NetFormatException"/>.</summary>
    public static CommandBatch Read(ref NetReader r, StringTableReader strings, long tick, int player, bool allowDebug)
    {
        ushort epoch = r.ReadUInt16();
        uint firstSeq = r.ReadVarUInt32();
        int count = r.ReadCount(NetLimits.MaxCommandBatch);
        var commands = new SimCommand[count];
        for (int i = 0; i < count; i++) commands[i] = CommandCodec.Read(ref r, strings, tick, player, allowDebug);
        return new CommandBatch(epoch, firstSeq, commands);
    }

    /// <summary>Writes a result; a reason longer than <see cref="NetLimits.MaxMessageText"/> bytes is cut on a character boundary.</summary>
    public static void Write(NetWriter w, CommandResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        w.WriteVarUInt(result.Seq);
        w.WriteUtf8(Clamp(result.Reason ?? "", NetLimits.MaxMessageText));
    }

    public static CommandResult ReadResult(ref NetReader r) => new(r.ReadVarUInt32(), r.ReadUtf8(NetLimits.MaxMessageText));

    static string Clamp(string s, int maxBytes)
    {
        if (s.Length * 3L <= maxBytes || Encoding.UTF8.GetByteCount(s) <= maxBytes) return s;
        int bytes = 0, chars = 0;
        foreach (var rune in s.EnumerateRunes())
        {
            bytes += rune.Utf8SequenceLength;
            if (bytes > maxBytes) break;
            chars += rune.Utf16SequenceLength;
        }
        return s[..chars];
    }
}
