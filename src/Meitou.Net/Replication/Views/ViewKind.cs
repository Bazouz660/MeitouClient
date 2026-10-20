namespace Meitou.Net.Replication;

/// <summary>
/// The replicated views: the parts of a <c>WorldSnapshot</c> that change rarely or are large, sent as reliable <c>ViewUpdate</c> messages instead of
/// inside snapshot parts (docs/net.md, "Views"). Values are append-only within a <see cref="NetProtocol.Version"/>.
/// </summary>
public enum ViewKind : byte
{
    /// <summary>SquadAi, ActiveSquad, Viewer, PlayerFaction, Money, InCreation, CreationRaces.</summary>
    Header = 1,
    Towns = 2,
    Camps = 3,
    Squads = 4,
    Buildings = 5,
    GroundItems = 6,
    Containers = 7,
    DoorPicks = 8,
    Doors = 9,
    Unlocked = 10,
    Inspected = 11,
    Machines = 12,
    Research = 13,
    Crafts = 14,
    Trade = 15,
    Conversation = 16,
    DialogueRequest = 17,
    Speech = 18,

    /// <summary>Per character: Skills, AllSkills, SkillXpMultipliers, Hud, Bounties.</summary>
    Stats = 19,

    /// <summary>Per character: HealthParts.</summary>
    Health = 20,

    /// <summary>Per character: ItemGrid, Inventory.</summary>
    Inventory = 21,

    /// <summary>Per character: Path.</summary>
    Path = 22,

    /// <summary>Relations and RelationsRevision (structural).</summary>
    Relations = 23,

    /// <summary>The characters the viewer can talk to.</summary>
    Talkable = 24,

    /// <summary>The viewer's last <c>ProspectReport</c>, if any.</summary>
    Prospected = 25,
}
