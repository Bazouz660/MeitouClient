using System.Globalization;
using Meitou.Data.Fcs;

namespace Meitou.Simulation.Saving;

/// <summary>
/// The multiplayer roster and squad owners in a save (docs/formats/save.md "Multiplayer roster and owners"; docs/simulation.md "Multiplayer (host-authoritative co-op)"):
/// Meitou-only CAMERA keys <c>meitou players</c> and <c>meitou player&lt;k&gt; id/name/number</c>, and each player PLATOON's <c>meitou owner</c> (its owner's GUID). Written
/// only while some session has a GUID, so a single-player save holds none of them; the original reads its keys by name and ignores these.
/// </summary>
public static class RosterSave
{
    public const string CountKey = "meitou players", OwnerKey = "meitou owner";
    const string EntryPrefix = "meitou player";

    /// <summary>The highest player number in a save's roster (engine guard against a damaged one: a bigger number is neither written nor read, so a remap or a later number never overflows).</summary>
    public const int MaxNumber = ushort.MaxValue;

    static string IdKey(int k) => $"{EntryPrefix}{k} id";
    static string NameKey(int k) => $"{EntryPrefix}{k} name";
    static string NumberKey(int k) => $"{EntryPrefix}{k} number";

    /// <summary>
    /// Removes any roster in <paramref name="camera"/>, then writes every session with a GUID in number order when there is one. Returns each such session's GUID by player
    /// number (for <see cref="WriteOwner"/>), or null when no session has one (a single-player save: nothing written).
    /// </summary>
    public static IReadOnlyDictionary<int, Guid>? Write(FcsRecord camera, PlayerState player)
    {
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(player);
        foreach (var key in camera.Ints.Keys.Where(k => k.StartsWith(EntryPrefix, StringComparison.Ordinal)).ToList()) camera.Ints.Remove(key);
        foreach (var key in camera.Strings.Keys.Where(k => k.StartsWith(EntryPrefix, StringComparison.Ordinal)).ToList()) camera.Strings.Remove(key);
        if (!player.Sessions.Any(s => s.Guid != Guid.Empty && s.Number <= MaxNumber)) return null;
        var owners = new Dictionary<int, Guid>();
        int n = 0;
        foreach (var s in player.Sessions)
        {
            if (s.Guid == Guid.Empty || s.Number > MaxNumber) continue;   // what Read skips is not written
            camera.Strings[IdKey(n)] = s.Guid.ToString("D", CultureInfo.InvariantCulture);
            camera.Strings[NameKey(n)] = s.Name;
            camera.Ints[NumberKey(n)] = s.Number;
            owners[s.Number] = s.Guid;
            n++;
        }
        camera.Ints[CountKey] = n;
        return owners;
    }

    /// <summary>A player platoon's <c>meitou owner</c>: its owner's GUID from <paramref name="owners"/> (<see cref="Write"/>); removed when there is no roster or the owner has no GUID.</summary>
    public static void WriteOwner(FcsRecord platoon, int owner, IReadOnlyDictionary<int, Guid>? owners)
    {
        ArgumentNullException.ThrowIfNull(platoon);
        if (owners is not null && owners.TryGetValue(owner, out var guid)) platoon.Strings[OwnerKey] = guid.ToString("D", CultureInfo.InvariantCulture);
        else platoon.Strings.Remove(OwnerKey);
    }

    /// <summary>
    /// Reads the roster into <paramref name="world"/>'s sessions for a load where <paramref name="local"/> plays player 0 (skipping entries with a bad or repeated GUID or number):
    /// the local GUID becomes 0; whoever held 0 takes the local entry's old number, or one past the highest when the local player is not in the roster; the others keep theirs.
    /// Each other entry is <see cref="PlayerState.Join"/>ed and player 0 takes the local GUID (and its entry's name). Returns the player number of each roster GUID.
    /// </summary>
    public static Dictionary<Guid, int> Read(FcsRecord camera, World world, Guid local)
    {
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(world);
        var entries = new List<(Guid Guid, string Name, int Number)>();
        // Each entry needs its id string, so a damaged count never runs past the record's strings.
        int count = Math.Min(camera.Ints.GetValueOrDefault(CountKey), camera.Strings.Count);
        for (int k = 0; k < count; k++)
        {
            if (!Guid.TryParseExact(camera.Strings.GetValueOrDefault(IdKey(k), ""), "D", out var guid) || guid == Guid.Empty) continue;
            if (!camera.Ints.TryGetValue(NumberKey(k), out int number) || number is < 0 or > MaxNumber) continue;
            if (entries.Any(e => e.Guid == guid || e.Number == number)) continue;
            entries.Add((guid, camera.Strings.GetValueOrDefault(NameKey(k), ""), number));
        }
        int localAt = entries.FindIndex(e => e.Guid == local);
        int zeroAt = entries.FindIndex(e => e.Number == 0);
        if (zeroAt >= 0 && zeroAt != localAt)
            entries[zeroAt] = entries[zeroAt] with { Number = localAt >= 0 ? entries[localAt].Number : entries.Max(e => e.Number) + 1 };
        if (localAt >= 0) entries[localAt] = entries[localAt] with { Number = 0 };

        var numbers = new Dictionary<Guid, int> { [local] = 0 };
        var me = world.Player.Local;
        me.Guid = local;
        if (localAt >= 0) me.Name = entries[localAt].Name;
        foreach (var (guid, name, number) in entries)
        {
            numbers[guid] = number;
            if (number != 0) world.Player.Join(number, guid, name);
        }
        return numbers;
    }

    /// <summary>
    /// The owner of a player platoon on load: its <c>meitou owner</c>'s number in <paramref name="roster"/> (<see cref="Read"/>); 0 without a roster (a single-player load) or
    /// without the key, and 0 with a note for a GUID the roster does not hold.
    /// </summary>
    public static int OwnerOf(FcsRecord platoon, string platoonName, Dictionary<Guid, int>? roster, Action<string> note)
    {
        ArgumentNullException.ThrowIfNull(platoon);
        ArgumentNullException.ThrowIfNull(note);
        if (roster is null || platoon.Strings.GetValueOrDefault(OwnerKey) is not { Length: > 0 } text) return 0;
        if (Guid.TryParseExact(text, "D", out var guid) && roster.TryGetValue(guid, out int owner)) return owner;
        note($"{platoonName}: its owner {text} is not in the save's player list; the squad goes to the host.");
        return 0;
    }
}
