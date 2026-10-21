using System.Diagnostics;
using Meitou.Net.Session;
using Meitou.Net.Transport.Steam;

namespace Meitou.Game;

/// <summary>
/// Steam (docs/net.md "Steam transport"): started once for an interactive run from the Kenshi install's own <c>steam_api64.dll</c>, unless the settings
/// turn it off. A host also listens on Steam and announces itself in rich presence; the Multiplayer window lists the friends hosting; a friend's "Join
/// game" accepted in Steam joins from the title. Without Steam everything works over UDP as before.
/// </summary>
sealed partial class GameHost
{
    SteamNative? steam;
    /// <summary>Why Steam is off ("" while on or not started yet).</summary>
    string steamOff = "";
    /// <summary>The rich presence value last set while hosting (null: none set).</summary>
    string? presenceShown;
    long presenceAt;
    /// <summary>A Steam "Join game" waiting for the frame's end: the friend and the connect string.</summary>
    (ulong Friend, string Connect)? steamJoin;

    /// <summary>The friends list the Multiplayer window reads.</summary>
    sealed class SteamDirectory(GameHost game) : ISteamDirectory
    {
        IReadOnlyList<SteamHostEntry> hosts = [];

        public string? Off => game.steam is null ? game.steamOff : null;

        public IReadOnlyList<SteamHostEntry> Hosts => game.steam is null ? [] : hosts;

        public void Refresh(bool request)
        {
            if (game.steam is { } s) hosts = SteamPresence.Hosts(s, s.AppId, request);
        }
    }

    /// <summary>Once, before the frame loop of an interactive run (after the window and device are up).</summary>
    void StartSteam()
    {
        if (!config.Steam) steamOff = "turned off in the settings";
        else if ((steam = SteamNative.TryStart(install.Root, out steamOff)) is { } s)
        {
            Console.WriteLine($"steam     on as {s.PersonaName} ({s.LocalId}), app {s.AppId}");
            s.JoinRequested += (friend, connect) => steamJoin = (friend, connect);
        }
        if (steam is null) Console.WriteLine($"steam     off: {steamOff}");
        player.Steam = new SteamDirectory(this);
    }

    /// <summary>The notice that goes with hosting: how friends find the game, or why Steam is not part of it.</summary>
    string SteamHostingNotice() => steam is not null ? "Steam: friends can join you from their Multiplayer window"
        : config.Steam ? $"Steam is off ({steamOff}): friends join by address" : "";

    /// <summary>Every frame at its end: the host's rich presence kept current (cleared once not hosting) and a Steam "Join game" started.</summary>
    void SteamFrame()
    {
        if (steam is not { } s) return;
        if (netHost is { } host)
        {
            long now = Stopwatch.GetTimestamp();
            if (presenceShown is null || Stopwatch.GetElapsedTime(presenceAt, now).TotalSeconds >= 2)
            {
                presenceAt = now;
                int present = host.Roster.Count(e => e.State != NetPlayerState.Away), max = host.Rules.MaxPlayers;
                string value = SteamPresence.HostValue(present, max);
                if (value != presenceShown)
                {
                    SteamPresence.SetHosting(s, present, max);
                    presenceShown = value;
                }
            }
        }
        else if (presenceShown is not null)
        {
            s.ClearRichPresence();
            presenceShown = null;
        }
        if (steamJoin is { } join)
        {
            steamJoin = null;
            string friend = s.FriendName(join.Friend);
            if (!SteamPresence.TryParseConnect(join.Connect, out ulong hostId)) Console.WriteLine($"steam     join request without a Meitou host: '{join.Connect}'");
            else if (role != NetRole.None || !player.TitleOpen || Covered) player.Notice($"Steam: return to the main menu to join {friend}.", true);
            else
            {
                Console.WriteLine($"steam     joining {friend} ({hostId}) from Steam");
                OnMultiplayerRequest(new JoinGame(SteamPresence.Address(hostId), 0, ""));
            }
        }
    }

    void StopSteam()
    {
        steam?.Dispose();
        steam = null;
        presenceShown = null;
    }
}
