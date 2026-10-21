using System.Numerics;

namespace Meitou.Simulation.Production;

/// <summary>
/// The HUD's Prospect button: the character stands where it is and prospects (the original's <c>Task_Prospecting</c>, order 150, docs/game/ai-tasks.md "Prospecting"). The host names the one nearest the
/// camera; a character that already prospects, is down, fighting, carried, caged or swimming ignores it.
/// </summary>
public sealed record ProspectOrder(CharacterId Character) : SimCommand;

/// <summary>One line of the results window: a resource or crop coverage and its value (1 = 100 %).</summary>
public readonly record struct ProspectLine(string Name, float Value)
{
    /// <summary>The number shown: the value times 100, cut to an int (<b>Verified</b>, <c>FUN_14049d850</c>).</summary>
    public int Percent => (int)(Value * 100);
}

/// <summary>
/// What a finished prospecting found (docs/game/ui-screens.md "Prospecting"): the spot, the prospector and the window's lines. Display only: the host reads <see cref="World.Prospected"/>
/// and opens the results window when <see cref="Serial"/> is new; nothing in the simulation reads it.
/// </summary>
/// <param name="Serial">1, 2, 3... one per finished prospecting of this world.</param>
/// <param name="Position">Where the prospector stood.</param>
/// <param name="Prospector">Its name.</param>
/// <param name="Skill">Its science stat, cut to an int (the window's "Prospector Skill").</param>
/// <param name="Zone">The BIOME_GROUP's name at the spot ("" without an areas map).</param>
/// <param name="Altitude">The height of the spot, cut to an int.</param>
/// <param name="AreaMetres">"Area Size": <c>int((science / 100 x 6000 + 2000) x 0.2)</c>.</param>
/// <param name="Environment">Green, Arid and Swamp, only those above 0 (empty: "Dead").</param>
/// <param name="Resources">Water, Fertility, Stone, Iron, Copper in the window's order.</param>
public sealed record ProspectReport(long Serial, Vector3 Position, string Prospector, int Skill, string Zone, int Altitude, int AreaMetres,
    IReadOnlyList<ProspectLine> Environment, IReadOnlyList<ProspectLine> Resources);
