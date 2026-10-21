using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Meitou.Net.Transport.Steam;

/// <summary>
/// <see cref="ISteamP2P"/> over the <c>steam_api64.dll</c> of the player's Kenshi install (docs/net.md "Steam transport"): loaded at run time with
/// <see cref="NativeLibrary"/>, its flat exports called through function pointers, nothing of Valve's shipped or linked. The game runs as Kenshi
/// (app 233860): <c>SteamAppId</c>/<c>SteamGameId</c> are set in this process before the DLL loads, so no file is written into the install. The
/// interface versions are the ones Kenshi asks for, since each flat thunk calls through the vtable of the version it was built for. One instance
/// per process; frame thread only.
/// </summary>
public sealed unsafe class SteamNative : ISteamP2P, IDisposable
{
    /// <summary>Kenshi's Steam app id (its <c>steam_appid.txt</c>).</summary>
    public const uint KenshiAppId = 233860;
    public const string LibraryName = "steam_api64.dll";
    const string ClientVersion = "SteamClient017", UserVersion = "SteamUser019", FriendsVersion = "SteamFriends015", NetworkingVersion = "SteamNetworking005",
        UtilsVersion = "SteamUtils008";
    const int FriendFlagImmediate = 0x04;
    /// <summary>How long <see cref="CloseSession"/> waits, so a parting packet already queued still leaves.</summary>
    const long CloseDelayMs = 3000;

    static SteamNative? live;

    readonly nint library;
    readonly nint user, friends, networking, utils;
    readonly List<nint> callbacks = [];
    readonly Dictionary<ulong, long> closing = [];
    readonly Stopwatch time = Stopwatch.StartNew();
    bool disposed;

    // ---- Exports ----
    readonly delegate* unmanaged<void> shutdown;
    readonly delegate* unmanaged<void> runCallbacks;
    readonly delegate* unmanaged<nint, void> unregisterCallback;
    readonly delegate* unmanaged<nint, ulong, void*, uint, int, int, byte> sendP2P;
    readonly delegate* unmanaged<nint, uint*, int, byte> isP2PAvailable;
    readonly delegate* unmanaged<nint, void*, uint, uint*, ulong*, int, byte> readP2P;
    readonly delegate* unmanaged<nint, ulong, byte> acceptP2P;
    readonly delegate* unmanaged<nint, ulong, byte> closeP2P;
    readonly delegate* unmanaged<nint, ulong, SessionStateNative*, byte> sessionState;
    readonly delegate* unmanaged<nint, byte*> personaName;
    readonly delegate* unmanaged<nint, int, int> friendCount;
    readonly delegate* unmanaged<nint, int, int, ulong> friendByIndex;
    readonly delegate* unmanaged<nint, ulong, byte*> friendPersonaName;
    readonly delegate* unmanaged<nint, ulong, FriendGameInfoNative*, byte> friendGamePlayed;
    readonly delegate* unmanaged<nint, ulong, byte*, byte*> friendRichPresence;
    readonly delegate* unmanaged<nint, ulong, void> requestFriendRichPresence;
    readonly delegate* unmanaged<nint, byte*, byte*, byte> setRichPresence;
    readonly delegate* unmanaged<nint, void> clearRichPresence;

    /// <summary><c>P2PSessionState_t</c> (natural alignment, 20 bytes).</summary>
    [StructLayout(LayoutKind.Sequential)]
    struct SessionStateNative
    {
        public byte ConnectionActive, Connecting, SessionError, UsingRelay;
        public int BytesQueuedForSend, PacketsQueuedForSend;
        public uint RemoteIp;
        public ushort RemotePort;
    }

    /// <summary><c>FriendGameInfo_t</c>: game id (the app id in its low 24 bits), IP, ports, lobby (24 bytes).</summary>
    [StructLayout(LayoutKind.Sequential)]
    struct FriendGameInfoNative
    {
        public ulong GameId;
        public uint GameIp;
        public ushort GamePort, QueryPort;
        public ulong Lobby;
    }

    public ulong LocalId { get; }
    public string PersonaName => Text(personaName(friends));
    /// <summary>The app id Steam reports for this process (<c>ISteamUtils::GetAppID</c>): <see cref="KenshiAppId"/> when the environment took.</summary>
    public uint AppId { get; }

    public event Action<ulong>? SessionRequested;
    public event Action<ulong, byte>? SessionFailed;
    public event Action<ulong, string>? JoinRequested;

    /// <summary>
    /// Starts Steam as Kenshi from the DLL in <paramref name="kenshiRoot"/>; null with the reason (for a notice) when it cannot: not Windows, no DLL,
    /// a missing export, Steam not running, <c>SteamAPI_Init</c> refused (not logged in, Kenshi not owned) or an interface missing.
    /// </summary>
    public static SteamNative? TryStart(string kenshiRoot, out string reason)
    {
        reason = "";
        if (live is not null) return live;
        if (!OperatingSystem.IsWindows()) { reason = "Kenshi ships Steam's library for Windows only"; return null; }
        string path = Path.Combine(kenshiRoot, LibraryName);
        if (!File.Exists(path)) { reason = $"{LibraryName} is not in the Kenshi folder"; return null; }
        // steam_api reads the app id from steam_appid.txt in the working directory or from these variables; set before it loads.
        Environment.SetEnvironmentVariable("SteamAppId", KenshiAppId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Environment.SetEnvironmentVariable("SteamGameId", KenshiAppId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (!NativeLibrary.TryLoad(path, out var library)) { reason = $"{LibraryName} could not be loaded"; return null; }
        try
        {
            var native = new SteamNative(library, out reason);
            if (reason.Length > 0) return null;
            live = native;
            return native;
        }
        catch (EntryPointNotFoundException e)
        {
            reason = $"{LibraryName} lacks {e.Message}";
            return null;
        }
    }

    static nint Export(nint library, string name) =>
        NativeLibrary.TryGetExport(library, name, out var address) ? address : throw new EntryPointNotFoundException(name);

    SteamNative(nint library, out string reason)
    {
        this.library = library;
        reason = "";
        var init = (delegate* unmanaged<byte>)Export(library, "SteamAPI_Init");
        var isRunning = (delegate* unmanaged<byte>)Export(library, "SteamAPI_IsSteamRunning");
        shutdown = (delegate* unmanaged<void>)Export(library, "SteamAPI_Shutdown");
        runCallbacks = (delegate* unmanaged<void>)Export(library, "SteamAPI_RunCallbacks");
        var registerCallback = (delegate* unmanaged<nint, int, void>)Export(library, "SteamAPI_RegisterCallback");
        unregisterCallback = (delegate* unmanaged<nint, void>)Export(library, "SteamAPI_UnregisterCallback");
        var createInterface = (delegate* unmanaged<byte*, nint>)Export(library, "SteamInternal_CreateInterface");
        var steamClient = (delegate* unmanaged<nint>)Export(library, "SteamClient");
        var getUser = (delegate* unmanaged<int>)Export(library, "SteamAPI_GetHSteamUser");
        var getPipe = (delegate* unmanaged<int>)Export(library, "SteamAPI_GetHSteamPipe");
        var clientUser = (delegate* unmanaged<nint, int, int, byte*, nint>)Export(library, "SteamAPI_ISteamClient_GetISteamUser");
        var clientFriends = (delegate* unmanaged<nint, int, int, byte*, nint>)Export(library, "SteamAPI_ISteamClient_GetISteamFriends");
        var clientNetworking = (delegate* unmanaged<nint, int, int, byte*, nint>)Export(library, "SteamAPI_ISteamClient_GetISteamNetworking");
        var clientUtils = (delegate* unmanaged<nint, int, byte*, nint>)Export(library, "SteamAPI_ISteamClient_GetISteamUtils");
        var steamId = (delegate* unmanaged<nint, ulong>)Export(library, "SteamAPI_ISteamUser_GetSteamID");
        var appId = (delegate* unmanaged<nint, uint>)Export(library, "SteamAPI_ISteamUtils_GetAppID");
        var allowRelay = (delegate* unmanaged<nint, byte, byte>)Export(library, "SteamAPI_ISteamNetworking_AllowP2PPacketRelay");
        sendP2P = (delegate* unmanaged<nint, ulong, void*, uint, int, int, byte>)Export(library, "SteamAPI_ISteamNetworking_SendP2PPacket");
        isP2PAvailable = (delegate* unmanaged<nint, uint*, int, byte>)Export(library, "SteamAPI_ISteamNetworking_IsP2PPacketAvailable");
        readP2P = (delegate* unmanaged<nint, void*, uint, uint*, ulong*, int, byte>)Export(library, "SteamAPI_ISteamNetworking_ReadP2PPacket");
        acceptP2P = (delegate* unmanaged<nint, ulong, byte>)Export(library, "SteamAPI_ISteamNetworking_AcceptP2PSessionWithUser");
        closeP2P = (delegate* unmanaged<nint, ulong, byte>)Export(library, "SteamAPI_ISteamNetworking_CloseP2PSessionWithUser");
        sessionState = (delegate* unmanaged<nint, ulong, SessionStateNative*, byte>)Export(library, "SteamAPI_ISteamNetworking_GetP2PSessionState");
        personaName = (delegate* unmanaged<nint, byte*>)Export(library, "SteamAPI_ISteamFriends_GetPersonaName");
        friendCount = (delegate* unmanaged<nint, int, int>)Export(library, "SteamAPI_ISteamFriends_GetFriendCount");
        friendByIndex = (delegate* unmanaged<nint, int, int, ulong>)Export(library, "SteamAPI_ISteamFriends_GetFriendByIndex");
        friendPersonaName = (delegate* unmanaged<nint, ulong, byte*>)Export(library, "SteamAPI_ISteamFriends_GetFriendPersonaName");
        friendGamePlayed = (delegate* unmanaged<nint, ulong, FriendGameInfoNative*, byte>)Export(library, "SteamAPI_ISteamFriends_GetFriendGamePlayed");
        friendRichPresence = (delegate* unmanaged<nint, ulong, byte*, byte*>)Export(library, "SteamAPI_ISteamFriends_GetFriendRichPresence");
        // RequestFriendRichPresence came later than the rest; optional.
        if (NativeLibrary.TryGetExport(library, "SteamAPI_ISteamFriends_RequestFriendRichPresence", out var request))
            requestFriendRichPresence = (delegate* unmanaged<nint, ulong, void>)request;
        setRichPresence = (delegate* unmanaged<nint, byte*, byte*, byte>)Export(library, "SteamAPI_ISteamFriends_SetRichPresence");
        clearRichPresence = (delegate* unmanaged<nint, void>)Export(library, "SteamAPI_ISteamFriends_ClearRichPresence");

        if (isRunning() == 0) { reason = "Steam is not running"; return; }
        if (init() == 0) { reason = "Steam refused to start (logged out, or Kenshi not in this account)"; return; }
        nint client;
        fixed (byte* version = Utf8(ClientVersion)) client = createInterface(version);
        if (client == 0) client = steamClient();
        int hUser = getUser(), hPipe = getPipe();
        if (client != 0)
        {
            fixed (byte* v = Utf8(UserVersion)) user = clientUser(client, hUser, hPipe, v);
            fixed (byte* v = Utf8(FriendsVersion)) friends = clientFriends(client, hUser, hPipe, v);
            fixed (byte* v = Utf8(NetworkingVersion)) networking = clientNetworking(client, hUser, hPipe, v);
            fixed (byte* v = Utf8(UtilsVersion)) utils = clientUtils(client, hPipe, v);
        }
        if (client == 0 || user == 0 || friends == 0 || networking == 0 || utils == 0)
        {
            shutdown();
            reason = "Steam lacks an interface Kenshi uses";
            return;
        }
        LocalId = steamId(user);
        AppId = appId(utils);
        allowRelay(networking, 1);
        SteamCallbacks.Handler = OnCallback;
        foreach (var (id, size) in new[] { (SteamCallbacks.SessionRequestId, SteamCallbacks.SessionRequestSize),
            (SteamCallbacks.SessionConnectFailId, SteamCallbacks.SessionConnectFailSize), (SteamCallbacks.RichPresenceJoinRequestedId, SteamCallbacks.RichPresenceJoinRequestedSize) })
        {
            var obj = SteamCallbacks.Create(id, size);
            registerCallback(obj, id);
            callbacks.Add(obj);
        }
    }

    void OnCallback(int id, nint param)
    {
        if (disposed) return;
        var p = (byte*)param;
        switch (id)
        {
            case SteamCallbacks.SessionRequestId:
                SessionRequested?.Invoke(*(ulong*)p);
                break;
            case SteamCallbacks.SessionConnectFailId:
                SessionFailed?.Invoke(*(ulong*)p, p[8]);
                break;
            case SteamCallbacks.RichPresenceJoinRequestedId:
                int length = new ReadOnlySpan<byte>(p + 8, 256).IndexOf((byte)0);
                JoinRequested?.Invoke(*(ulong*)p, Encoding.UTF8.GetString(p + 8, length < 0 ? 256 : length));
                break;
        }
    }

    static byte[] Utf8(string text)
    {
        var bytes = new byte[Encoding.UTF8.GetByteCount(text) + 1];
        Encoding.UTF8.GetBytes(text, bytes);
        return bytes;
    }

    static string Text(byte* text) => text == null ? "" : Marshal.PtrToStringUTF8((nint)text) ?? "";

    public void RunCallbacks()
    {
        if (disposed) return;
        runCallbacks();
        if (closing.Count == 0) return;
        long now = time.ElapsedMilliseconds;
        foreach (var (remote, at) in closing.ToArray())
            if (now >= at)
            {
                closing.Remove(remote);
                closeP2P(networking, remote);
            }
    }

    public bool Send(ulong remote, ReadOnlySpan<byte> data, SteamSendType type, int channel)
    {
        if (disposed) return false;
        closing.Remove(remote);
        fixed (byte* p = data) return sendP2P(networking, remote, p, (uint)data.Length, (int)type, channel) != 0;
    }

    public bool IsPacketAvailable(int channel, out int size)
    {
        size = 0;
        if (disposed) return false;
        uint n;
        if (isP2PAvailable(networking, &n, channel) == 0) return false;
        size = (int)Math.Min(n, int.MaxValue);
        return true;
    }

    public bool ReadPacket(int channel, Span<byte> buffer, out int size, out ulong remote)
    {
        size = 0;
        remote = 0;
        if (disposed) return false;
        uint n;
        ulong from;
        bool read;
        fixed (byte* p = buffer) read = readP2P(networking, p, (uint)buffer.Length, &n, &from, channel) != 0;
        if (!read) return false;
        (size, remote) = ((int)Math.Min(n, (uint)buffer.Length), from);
        return true;
    }

    public bool AcceptSession(ulong remote) => !disposed && acceptP2P(networking, remote) != 0;

    public void CloseSession(ulong remote)
    {
        if (!disposed) closing[remote] = time.ElapsedMilliseconds + CloseDelayMs;
    }

    public SteamSessionState? SessionState(ulong remote)
    {
        if (disposed) return null;
        SessionStateNative s;
        if (sessionState(networking, remote, &s) == 0) return null;
        return new SteamSessionState(s.ConnectionActive != 0, s.Connecting != 0, s.SessionError, s.UsingRelay != 0, s.BytesQueuedForSend, s.PacketsQueuedForSend);
    }

    public IReadOnlyList<ulong> Friends()
    {
        if (disposed) return [];
        int count = friendCount(friends, FriendFlagImmediate);
        var list = new List<ulong>(Math.Max(count, 0));
        for (int i = 0; i < count; i++) list.Add(friendByIndex(friends, i, FriendFlagImmediate));
        return list;
    }

    public string FriendName(ulong friend) => disposed ? "" : Text(friendPersonaName(friends, friend));

    public uint FriendAppId(ulong friend)
    {
        if (disposed) return 0;
        FriendGameInfoNative info;
        return friendGamePlayed(friends, friend, &info) != 0 ? (uint)(info.GameId & 0xFFFFFF) : 0;
    }

    public string FriendRichPresence(ulong friend, string key)
    {
        if (disposed) return "";
        fixed (byte* k = Utf8(key)) return Text(friendRichPresence(friends, friend, k));
    }

    public void RequestFriendRichPresence(ulong friend)
    {
        if (!disposed && requestFriendRichPresence != null) requestFriendRichPresence(friends, friend);
    }

    public bool SetRichPresence(string key, string? value)
    {
        if (disposed) return false;
        fixed (byte* k = Utf8(key))
        fixed (byte* v = Utf8(value ?? "")) return setRichPresence(friends, k, v) != 0;
    }

    public void ClearRichPresence()
    {
        if (!disposed) clearRichPresence(friends);
    }

    /// <summary>Unregisters the callbacks, clears the rich presence, closes the sessions still closing and shuts Steam down. The DLL stays loaded.</summary>
    public void Dispose()
    {
        if (disposed) return;
        clearRichPresence(friends);
        foreach (var remote in closing.Keys) closeP2P(networking, remote);
        closing.Clear();
        foreach (var obj in callbacks)
        {
            unregisterCallback(obj);
            SteamCallbacks.Free(obj);
        }
        callbacks.Clear();
        disposed = true;
        SteamCallbacks.Handler = null;
        shutdown();
        live = null;
        _ = library;
    }
}
