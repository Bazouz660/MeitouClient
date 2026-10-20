using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Meitou.Simulation;
using Meitou.Data.Gameplay;

namespace Meitou.Net.Session;

/// <summary>
/// The pure checks of the join handshake, shared by host and client (docs/net.md, "Protocol and handshake"). Check order: protocol, full, banned address
/// (<see cref="CheckRequest"/>); then protocol again, banned id, same id as the host, build, data set, animation library, password
/// (<see cref="CheckHello"/>); the host's slot assignment (<see cref="RejectCode.NoSlot"/>) comes last and is the host's own.
/// </summary>
public static class Handshake
{
    /// <summary>Length of the challenge nonce.</summary>
    public const int NonceLength = 16;

    /// <summary>Length of a non-empty proof (HMAC-SHA256).</summary>
    public const int ProofLength = 32;

    /// <summary>
    /// The password proof: empty without a password, else HMAC-SHA256 keyed with SHA-256 of the UTF-8 password over the nonce followed by the player id's
    /// bytes (<see cref="Guid.ToByteArray()"/>), so a proof is good for one challenge and one player only.
    /// </summary>
    public static byte[] Proof(string? password, ReadOnlySpan<byte> nonce, Guid player)
    {
        if (string.IsNullOrEmpty(password)) return [];
        byte[] key = SHA256.HashData(Encoding.UTF8.GetBytes(password));
        byte[] data = new byte[nonce.Length + 16];
        nonce.CopyTo(data);
        player.ToByteArray().CopyTo(data, nonce.Length);
        return HMACSHA256.HashData(key, data);
    }

    /// <summary>
    /// The check at connect time. <paramref name="connectData"/> is <see cref="NetProtocol.Magic"/> and a little-endian ushort version;
    /// <paramref name="connected"/> counts the remote players already in the game (the requester and the host excluded), so the game is full when
    /// <c>connected + 1 &gt;= maxPlayers</c> (<paramref name="maxPlayers"/> counts the host). Null: accept.
    /// </summary>
    public static Reject? CheckRequest(ReadOnlySpan<byte> connectData, int connected, int maxPlayers, bool addressBanned)
    {
        int magic = NetProtocol.Magic.Length;
        if (connectData.Length != magic + 2 || !connectData[..magic].SequenceEqual(NetProtocol.Magic))
            return new Reject(RejectCode.Protocol, "Not a Meitou connection. Update Meitou.");
        int version = BinaryPrimitives.ReadUInt16LittleEndian(connectData[magic..]);
        if (version != NetProtocol.Version) return ProtocolMismatch(version);
        if (connected + 1 >= maxPlayers) return new Reject(RejectCode.Full, $"The game is full ({maxPlayers} players).");
        if (addressBanned) return Banned;
        return null;
    }

    /// <summary>
    /// The check of a <see cref="Hello"/>. <paramref name="password"/> null or empty: none needed. <paramref name="allowBuildMismatch"/> skips the build
    /// comparison only (the data set and animation library still must match). <paramref name="banned"/>: the player id is banned. Null: accept.
    /// </summary>
    public static Reject? CheckHello(Hello hello, NetIdentity host, ulong hostAnimationHash, string? password, ReadOnlySpan<byte> nonce, bool allowBuildMismatch, bool banned)
    {
        ArgumentNullException.ThrowIfNull(hello);
        ArgumentNullException.ThrowIfNull(host);
        if (hello.Protocol != NetProtocol.Version) return ProtocolMismatch(hello.Protocol);
        if (banned) return Banned;
        if (hello.Player == host.PlayerId) return new Reject(RejectCode.Duplicate, "Your player id is already in this game.");
        if (!allowBuildMismatch && !string.Equals(hello.Build, host.Build, StringComparison.Ordinal))
            return new Reject(RejectCode.Build, $"Version mismatch: you run {hello.Build}, the host runs {host.Build}.");
        string? difference = DataSetIdentity.Difference(host.Data, hello.Data);
        if (difference is not null) return new Reject(RejectCode.Data, $"Your game data differs from the host's. {difference}");
        if (hello.AnimationHash != hostAnimationHash)
            return new Reject(RejectCode.Data, "Your game data differs from the host's. The animation library is different.");
        if (!string.IsNullOrEmpty(password))
        {
            if (hello.Proof.Length == 0) return new Reject(RejectCode.Password, "This game needs a password.");
            if (!CryptographicOperations.FixedTimeEquals(Proof(password, nonce, hello.Player), hello.Proof))
                return new Reject(RejectCode.Password, "Wrong password.");
        }
        return null;
    }

    static readonly Reject Banned = new(RejectCode.Banned, "You are banned from this game.");

    static Reject ProtocolMismatch(int theirs) =>
        new(RejectCode.Protocol, $"Incompatible network protocol (yours {theirs}, host's {NetProtocol.Version}). Update Meitou.");

    /// <summary>
    /// A display name the host accepts: control characters dropped, trimmed, at most <see cref="NetProtocol.MaxNameLength"/> characters (never splitting a
    /// surrogate pair); empty becomes <c>Player {number}</c>.
    /// </summary>
    public static string SanitizeName(string raw, int number)
    {
        var sb = new StringBuilder(raw?.Length ?? 0);
        foreach (char c in raw ?? "")
            if (!char.IsControl(c)) sb.Append(c);
        string name = sb.ToString().Trim();
        if (name.Length > NetProtocol.MaxNameLength)
        {
            int cut = NetProtocol.MaxNameLength;
            if (char.IsHighSurrogate(name[cut - 1])) cut--;
            name = name[..cut].TrimEnd();
        }
        return name.Length == 0 ? $"Player {number}" : name;
    }

    /// <summary>
    /// A hash of the animation library host and client must share, as the wire carries library indices: <see cref="Rng.StableHash"/> of each definition id
    /// in order, folded with the count. Two builds of the same data give the same value (the library sorts each group by id).
    /// </summary>
    public static ulong AnimationHash(AnimationLibrary library)
    {
        ArgumentNullException.ThrowIfNull(library);
        ulong h = 0xCBF29CE484222325UL;
        foreach (var definition in library.Definitions) h = (h ^ Rng.StableHash(definition.Id)) * 0x100000001B3UL;
        return (h ^ (ulong)library.Definitions.Count) * 0x100000001B3UL;
    }
}
