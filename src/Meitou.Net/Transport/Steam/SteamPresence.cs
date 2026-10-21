using System.Globalization;

namespace Meitou.Net.Transport.Steam;

/// <summary>A friend hosting a Meitou game, as their rich presence announces it.</summary>
public readonly record struct SteamHostEntry(ulong Id, string Name, int Version, int Present, int Max)
{
    public bool Compatible => Version == NetProtocol.Version;
}

/// <summary>
/// The rich presence a Steam host sets and the friends list reads (docs/net.md "Steam transport"): <c>meitou</c> = <c>host &lt;protocol&gt; &lt;present&gt;/&lt;max&gt;</c>,
/// <c>status</c> = the line Steam shows, <c>connect</c> = <c>+meitou_join &lt;steamid64&gt;</c> (what Steam's "Join game" hands the friend).
/// </summary>
public static class SteamPresence
{
    public const string Key = "meitou", StatusKey = "status", ConnectKey = "connect", JoinCommand = "+meitou_join";
    /// <summary>The address prefix of a Steam host: <c>steam:&lt;steamid64&gt;</c> (<c>--join</c>, the address box, recent games).</summary>
    public const string AddressPrefix = "steam:";

    public static string HostValue(int present, int max) => string.Create(CultureInfo.InvariantCulture, $"host {NetProtocol.Version} {present}/{max}");

    public static string StatusValue(int present, int max) => string.Create(CultureInfo.InvariantCulture, $"Hosting Meitou co-op ({present}/{max})");

    public static string ConnectValue(ulong host) => string.Create(CultureInfo.InvariantCulture, $"{JoinCommand} {host}");

    /// <summary>Sets the three keys of a host with <paramref name="present"/> of <paramref name="max"/> players.</summary>
    public static void SetHosting(ISteamP2P steam, int present, int max)
    {
        steam.SetRichPresence(Key, HostValue(present, max));
        steam.SetRichPresence(StatusKey, StatusValue(present, max));
        steam.SetRichPresence(ConnectKey, ConnectValue(steam.LocalId));
    }

    /// <summary>Reads a <see cref="Key"/> value: <c>host</c>, the protocol version, then <c>present/max</c>; false for anything else.</summary>
    public static bool TryParseHost(string value, out int version, out int present, out int max)
    {
        version = present = max = 0;
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || parts[0] != "host" || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out version)) return false;
        int slash = parts[2].IndexOf('/');
        return slash > 0 && int.TryParse(parts[2].AsSpan(0, slash), NumberStyles.None, CultureInfo.InvariantCulture, out present)
            && int.TryParse(parts[2].AsSpan(slash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out max);
    }

    /// <summary>The host id of a connect string (<c>+meitou_join &lt;id&gt;</c>, also found among other launch arguments); false when there is none.</summary>
    public static bool TryParseConnect(string connect, out ulong host)
    {
        host = 0;
        var parts = connect.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int at = Array.IndexOf(parts, JoinCommand);
        return at >= 0 && at + 1 < parts.Length && TryParseId(parts[at + 1], out host);
    }

    /// <summary>A Steam host address: <c>steam:&lt;id&gt;</c> (prefix ignoring case) with a non-zero 64-bit id in digits.</summary>
    public static bool TryParseAddress(string text, out ulong host)
    {
        host = 0;
        var s = text.Trim();
        return s.StartsWith(AddressPrefix, StringComparison.OrdinalIgnoreCase) && TryParseId(s[AddressPrefix.Length..], out host);
    }

    public static string Address(ulong host) => string.Create(CultureInfo.InvariantCulture, $"{AddressPrefix}{host}");

    static bool TryParseId(string text, out ulong id) => ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id != 0;

    /// <summary>
    /// The friends whose <see cref="Key"/> announces a host, by name: only friends playing the same app are read (rich presence is per app).
    /// With <paramref name="request"/> their rich presence is asked for first (it arrives with a later call).
    /// </summary>
    public static List<SteamHostEntry> Hosts(ISteamP2P steam, uint appId, bool request)
    {
        var list = new List<SteamHostEntry>();
        foreach (var friend in steam.Friends())
        {
            if (steam.FriendAppId(friend) != appId) continue;
            if (request) steam.RequestFriendRichPresence(friend);
            if (TryParseHost(steam.FriendRichPresence(friend, Key), out int version, out int present, out int max))
                list.Add(new SteamHostEntry(friend, steam.FriendName(friend), version, present, max));
        }
        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return list;
    }
}
