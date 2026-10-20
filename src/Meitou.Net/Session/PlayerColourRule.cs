namespace Meitou.Net.Session;

/// <summary>Which of the 8 palette colours a player gets (docs/net.md, "Protocol and handshake"); the palette itself is in the game's UI.</summary>
public static class PlayerColourRule
{
    /// <summary>Colours in the palette (<see cref="NetProtocol.MaxPlayers"/>).</summary>
    public const int Count = NetProtocol.MaxPlayers;

    /// <summary>
    /// The colour for a joining player: <paramref name="preferred"/> (callers pass the player number, the host being 0, so the host gets colour 0 when
    /// <paramref name="taken"/> is empty) reduced modulo 8 when free, else the lowest free colour; -1 when all 8 are taken.
    /// </summary>
    public static int Assign(IReadOnlyCollection<int> taken, int preferred)
    {
        ArgumentNullException.ThrowIfNull(taken);
        int wanted = ((preferred % Count) + Count) % Count;
        if (!taken.Contains(wanted)) return wanted;
        for (int colour = 0; colour < Count; colour++)
            if (!taken.Contains(colour)) return colour;
        return -1;
    }
}
