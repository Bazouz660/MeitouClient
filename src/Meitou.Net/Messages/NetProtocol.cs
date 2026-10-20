namespace Meitou.Net;

/// <summary>Protocol constants shared by host and client (docs/net.md, "Protocol").</summary>
public static class NetProtocol
{
    /// <summary>Bumped on any layout change in any codec; a client with another version is refused at connect time.</summary>
    public const ushort Version = 1;

    public const int DefaultPort = 40120;

    /// <summary>Most players in a session, the host included; the palette has this many colours.</summary>
    public const int MaxPlayers = 8;

    public const int MaxNameLength = 24;

    /// <summary>Longest chat line, in UTF-16 characters (the host clamps; the wire limit is <see cref="NetLimits.MaxChat"/> bytes).</summary>
    public const int MaxChatLength = 200;

    public const int MaxPasswordLength = 32;

    /// <summary>The connect request payload is <see cref="Magic"/> followed by a <see cref="ushort"/> <see cref="Version"/>.</summary>
    public static ReadOnlySpan<byte> Magic => "MEITOU"u8;
}
