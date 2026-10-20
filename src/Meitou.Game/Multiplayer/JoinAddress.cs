using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Meitou.Game;

/// <summary>The address a player types to join (the Multiplayer window, <c>--join</c>, <see cref="RecentGame.Address"/>; docs/meitou-ui.md "Multiplayer").</summary>
public static class JoinAddress
{
    /// <summary>
    /// Reads <c>host</c>, <c>host:port</c>, <c>[v6]</c>, <c>[v6]:port</c> or a bare IPv6 address (several colons: the host alone). A host is a DNS name or an
    /// IPv4 address (<see cref="Uri.CheckHostName"/>), an IPv6 one comes back without brackets; the port is 1-65535 in digits, else <paramref name="defaultPort"/>.
    /// Surrounding spaces are ignored. False (host "", port 0) for anything else.
    /// </summary>
    public static bool TryParse(string text, int defaultPort, out string host, out int port)
    {
        host = ""; port = 0;
        var s = text.Trim();
        string name, portText = "";
        if (s.StartsWith('['))
        {
            int close = s.IndexOf(']');
            if (close < 0) return false;
            name = s[1..close];
            var rest = s[(close + 1)..];
            if (rest.Length > 0 && (rest[0] != ':' || rest.Length == 1)) return false;
            if (rest.Length > 0) portText = rest[1..];
            if (!IsV6(name)) return false;
        }
        else if (s.Count(c => c == ':') > 1)
        {
            if (!IsV6(s)) return false;
            name = s;
        }
        else
        {
            int colon = s.IndexOf(':');
            name = colon < 0 ? s : s[..colon];
            if (colon >= 0 && (portText = s[(colon + 1)..]).Length == 0) return false;
            if (Uri.CheckHostName(name) is not (UriHostNameType.Dns or UriHostNameType.IPv4)) return false;
        }
        int value = defaultPort;
        if (portText.Length > 0 && !int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out value)) return false;
        if (value is < 1 or > 65535) return false;
        (host, port) = (name, value);
        return true;
    }

    static bool IsV6(string text) => IPAddress.TryParse(text, out var address) && address.AddressFamily == AddressFamily.InterNetworkV6;
}
