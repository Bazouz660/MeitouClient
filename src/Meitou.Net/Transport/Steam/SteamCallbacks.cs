using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Meitou.Net.Transport.Steam;

/// <summary>
/// Stand-ins for the C++ <c>CCallbackBase</c> objects <c>SteamAPI_RegisterCallback</c> takes (docs/net.md "Steam transport"): this DLL has no
/// <c>Steam_BGetCallback</c> polling API, so callbacks need an object whose virtual <c>Run</c> Steam calls from <c>SteamAPI_RunCallbacks</c>.
/// Each object lives in unmanaged memory, laid out as MSVC x64 lays the base class out: the vtable pointer (0), <c>m_nCallbackFlags</c> (byte, 8),
/// <c>m_iCallback</c> (int, 12), then our own payload size (int, 16). The vtable has three slots, <c>Run(void*)</c>,
/// <c>Run(void*, bool, SteamAPICall_t)</c> and <c>GetCallbackSizeBytes()</c>; MSVC may put the two <c>Run</c> overloads in either order, so both
/// slots point at one handler that reads only <c>this</c> and the parameter pointer. Frame thread only (Steam calls back inside RunCallbacks).
/// </summary>
static unsafe class SteamCallbacks
{
    public const int SessionRequestId = 1202, SessionConnectFailId = 1203, RichPresenceJoinRequestedId = 337;

    /// <summary>Payload sizes of the three callbacks: <c>CSteamID</c>; <c>CSteamID</c> + <c>uint8</c> padded to 8; <c>CSteamID</c> + <c>char[256]</c>.</summary>
    public const int SessionRequestSize = 8, SessionConnectFailSize = 16, RichPresenceJoinRequestedSize = 8 + 256;

    const int ObjectSize = 24, OffsetFlags = 8, OffsetId = 12, OffsetSize = 16;

    static nint vtable;

    /// <summary>Called for each callback Steam runs: the callback id and its payload, valid only during the call.</summary>
    public static Action<int, nint>? Handler;

    /// <summary>A new callback object for <paramref name="id"/> (free it with <see cref="Free"/> after unregistering).</summary>
    public static nint Create(int id, int payloadSize)
    {
        if (vtable == 0)
        {
            vtable = (nint)NativeMemory.AllocZeroed(3, (nuint)sizeof(nint));
            var slots = (nint*)vtable;
            slots[0] = (nint)(delegate* unmanaged<nint, nint, byte, ulong, void>)&Run;
            slots[1] = (nint)(delegate* unmanaged<nint, nint, byte, ulong, void>)&Run;
            slots[2] = (nint)(delegate* unmanaged<nint, int>)&Size;
        }
        var obj = (byte*)NativeMemory.AllocZeroed(ObjectSize);
        *(nint*)obj = vtable;
        obj[OffsetFlags] = 0;
        *(int*)(obj + OffsetId) = id;
        *(int*)(obj + OffsetSize) = payloadSize;
        return (nint)obj;
    }

    /// <summary>Whether Steam marked the object registered (<c>k_ECallbackFlagsRegistered</c>).</summary>
    public static bool IsRegistered(nint obj) => (((byte*)obj)[OffsetFlags] & 1) != 0;

    public static void Free(nint obj)
    {
        if (obj != 0) NativeMemory.Free((void*)obj);
    }

    /// <summary>Both <c>Run</c> slots: the extra arguments of the three-argument overload are ignored (for the one-argument one they are whatever the registers hold).</summary>
    [UnmanagedCallersOnly]
    static void Run(nint self, nint param, byte ioFailure, ulong call)
    {
        try
        {
            if (self != 0 && param != 0) Handler?.Invoke(*(int*)((byte*)self + OffsetId), param);
        }
        catch (Exception e)
        {
            // Nothing may unwind into Steam's native frames.
            Console.Error.WriteLine($"steam     callback failed: {e.Message}");
        }
    }

    [UnmanagedCallersOnly]
    static int Size(nint self) => self == 0 ? 0 : *(int*)((byte*)self + OffsetSize);
}
