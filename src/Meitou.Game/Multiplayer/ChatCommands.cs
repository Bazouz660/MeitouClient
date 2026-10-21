using System.Globalization;
using Meitou.Net.Session;

namespace Meitou.Game;

/// <summary>What a chat line starting with <c>/</c> asks for.</summary>
public enum ChatVerb { Help, Players, Kick, Ban, Unknown }

/// <summary>A parsed chat command: <paramref name="Target"/> is the first argument (a player token), <paramref name="Rest"/> the text after it (a kick's reason).</summary>
public readonly record struct ChatCommand(ChatVerb Verb, string Target, string Rest);

/// <summary>
/// The chat line's slash commands (docs/meitou-ui.md "Multiplayer", Chat): pure parsing and player lookup, so the host's /kick, /ban, /players and /help can be
/// tested without a window. Engine choice; the game has no chat.
/// </summary>
public static class ChatCommands
{
    /// <summary>
    /// "/kick Bob being rude" gives (Kick, "Bob", "being rude"); a target in double quotes may hold spaces ("/ban "Big Bob""); the verb ignores case. A line that is not
    /// a command, or a verb that is not known, gives <see cref="ChatVerb.Unknown"/> with no arguments.
    /// </summary>
    public static ChatCommand Parse(string line)
    {
        string text = line.Trim();
        if (text.Length == 0 || text[0] != '/') return new(ChatVerb.Unknown, "", "");
        int space = text.IndexOf(' ');
        string word = (space < 0 ? text[1..] : text[1..space]).ToLowerInvariant();
        string args = space < 0 ? "" : text[(space + 1)..].TrimStart();
        var verb = word switch { "help" => ChatVerb.Help, "players" => ChatVerb.Players, "kick" => ChatVerb.Kick, "ban" => ChatVerb.Ban, _ => ChatVerb.Unknown };
        if (verb == ChatVerb.Unknown) return new(verb, "", "");
        string target, rest;
        if (args.Length > 0 && args[0] == '"')
        {
            int close = args.IndexOf('"', 1);
            target = close < 0 ? args[1..] : args[1..close];
            rest = close < 0 ? "" : args[(close + 1)..];
        }
        else
        {
            int cut = args.IndexOf(' ');
            target = cut < 0 ? args : args[..cut];
            rest = cut < 0 ? "" : args[cut..];
        }
        return new(verb, target.Trim(), rest.Trim());
    }

    /// <summary>
    /// "#3" is player number 3; else the exact name (ignoring case); else the one name that starts with the token. Null with <paramref name="error"/>: "No player called X."
    /// or "X matches several players: A, B.". The caller passes the players to choose from (those not away).
    /// </summary>
    public static RosterEntry? Resolve(IReadOnlyList<RosterEntry> players, string token, out string error)
    {
        error = "";
        if (token.Length > 1 && token[0] == '#' && int.TryParse(token.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int number))
        {
            if (players.FirstOrDefault(p => p.Player == number) is { } byNumber) return byNumber;
        }
        else if (token.Length > 0)
        {
            if (players.FirstOrDefault(p => string.Equals(p.Name, token, StringComparison.OrdinalIgnoreCase)) is { } exact) return exact;
            var prefixed = players.Where(p => p.Name.StartsWith(token, StringComparison.OrdinalIgnoreCase)).ToList();
            if (prefixed.Count == 1) return prefixed[0];
            if (prefixed.Count > 1)
            {
                error = $"{token} matches several players: {string.Join(", ", prefixed.Select(p => p.Name))}.";
                return null;
            }
        }
        error = $"No player called {token}.";
        return null;
    }
}
