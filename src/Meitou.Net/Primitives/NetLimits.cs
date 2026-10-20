namespace Meitou.Net;

/// <summary>
/// Hard limits every reader enforces on what the other end sends (docs/net.md, "Limits"). They are protocol constants: a writer that exceeds one
/// produces a message the reader refuses, so the codecs check them when writing too.
/// </summary>
public static class NetLimits
{
    /// <summary>Longest character-id list (selections, squad members).</summary>
    public const int MaxIdList = 1024;

    /// <summary>Longest string, in UTF-8 bytes, unless a field names a tighter limit.</summary>
    public const int MaxString = 4096;

    /// <summary>Longest name or key (record keys, character and squad names), in UTF-8 bytes.</summary>
    public const int MaxName = 256;

    /// <summary>Longest notice or message text, in UTF-8 bytes.</summary>
    public const int MaxMessageText = 1024;

    /// <summary>Longest chat line on the wire, in UTF-8 bytes (the host clamps the text to <see cref="NetProtocol.MaxChatLength"/> characters).</summary>
    public const int MaxChat = 1024;

    /// <summary>Longest list inside one item or command (inventory items, plans' parts, instance states).</summary>
    public const int MaxListItems = 4096;

    /// <summary>Most items in one keyed view (<see cref="KeyedViewSync{TKey, TItem}"/>); a longer source is cut to this.</summary>
    public const int MaxViewItems = 16384;

    /// <summary>Most planned buildings in one <c>PlaceBuildings</c>.</summary>
    public const int MaxPlans = 256;

    /// <summary>Most entries in one <c>FcsRecord</c> map (and most reference categories).</summary>
    public const int MaxFcsMapEntries = 4096;

    /// <summary>Most commands in one <c>CommandBatch</c>.</summary>
    public const int MaxCommandBatch = 64;

    /// <summary>Largest message on a reliable channel, in bytes (LiteNetLib fragments it).</summary>
    public const int MaxReliableMessage = 65536;

    /// <summary>Soft size of one view update body, in bytes: a keyed view that would exceed it is split over several updates.</summary>
    public const int MaxViewBody = 16384;

    /// <summary>Most self-contained parts of one snapshot.</summary>
    public const int MaxParts = 64;

    /// <summary>Most animation layers on one character.</summary>
    public const int MaxAnimLayers = 8;

    /// <summary>Most bones in a ragdoll pose.</summary>
    public const int MaxBones = 512;

    /// <summary>Most entries in one string table; once it is full the writer sends literals.</summary>
    public const int MaxStringTable = 16384;
}
