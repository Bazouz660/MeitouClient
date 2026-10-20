namespace Meitou.Net;

/// <summary>
/// The only exception a <see cref="NetReader"/> (and the codecs written on it) throws for bytes it cannot accept: an overrun, an overlong varint,
/// a bad bool, invalid UTF-8, a count over its limit, an undefined enum value, a non-finite float, trailing bytes (docs/net.md, "Reader rules").
/// A failure on a reliable channel loses the stream and string-table state, so the connection is dropped; a bad snapshot part is just discarded.
/// </summary>
public sealed class NetFormatException(string message) : Exception(message);
