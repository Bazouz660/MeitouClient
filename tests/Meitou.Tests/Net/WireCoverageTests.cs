using System.Reflection;
using Meitou.Net.Replication;
using Meitou.Simulation;

namespace Meitou.Tests.Net;

/// <summary>
/// Every public property of <see cref="WorldSnapshot"/> and <see cref="CharacterSnapshot"/> has a carrier on the wire (docs/net.md, "Interest, delta,
/// replicator and mirror"): a snapshot part (header or records), a <see cref="CharacterWire"/> group or a <see cref="ViewKind"/>. The table below is the
/// explicit claim; a property added to either snapshot fails this test until it is wired and listed.
/// </summary>
public class WireCoverageTests
{
    /// <summary>Where a member travels: a part (header or record list), a character group bit, or a view kind.</summary>
    readonly record struct Carrier(string? Part = null, uint Group = 0, ViewKind? View = null)
    {
        public static Carrier InPart(string what) => new(Part: what);
        public static Carrier InGroup(uint bit) => new(Group: bit);
        public static Carrier InView(ViewKind kind) => new(View: kind);
    }

    static readonly Dictionary<string, Carrier> World = new()
    {
        [nameof(WorldSnapshot.Tick)] = Carrier.InPart("header Tick"),
        [nameof(WorldSnapshot.Characters)] = Carrier.InPart("character records"),
        [nameof(WorldSnapshot.Projectiles)] = Carrier.InPart("projectile records"),
        [nameof(WorldSnapshot.Trade)] = Carrier.InView(ViewKind.Trade),
        [nameof(WorldSnapshot.Conversation)] = Carrier.InView(ViewKind.Conversation),
        [nameof(WorldSnapshot.Speech)] = Carrier.InView(ViewKind.Speech),
        [nameof(WorldSnapshot.DialogueRequest)] = Carrier.InView(ViewKind.DialogueRequest),
        [nameof(WorldSnapshot.Towns)] = Carrier.InView(ViewKind.Towns),
        [nameof(WorldSnapshot.Camps)] = Carrier.InView(ViewKind.Camps),
        [nameof(WorldSnapshot.SquadAi)] = Carrier.InView(ViewKind.Header),
        [nameof(WorldSnapshot.PlayerSquads)] = Carrier.InView(ViewKind.Squads),
        [nameof(WorldSnapshot.ActiveSquad)] = Carrier.InView(ViewKind.Header),
        [nameof(WorldSnapshot.Viewer)] = Carrier.InView(ViewKind.Header),
        [nameof(WorldSnapshot.PlayerFaction)] = Carrier.InView(ViewKind.Header),
        [nameof(WorldSnapshot.Money)] = Carrier.InView(ViewKind.Header),
        [nameof(WorldSnapshot.Relations)] = Carrier.InView(ViewKind.Relations),
        [nameof(WorldSnapshot.RelationsRevision)] = Carrier.InView(ViewKind.Relations),
        [nameof(WorldSnapshot.Talkable)] = Carrier.InView(ViewKind.Talkable),
        [nameof(WorldSnapshot.Prospected)] = Carrier.InView(ViewKind.Prospected),
        [nameof(WorldSnapshot.InCreation)] = Carrier.InView(ViewKind.Header),
        [nameof(WorldSnapshot.CreationRaces)] = Carrier.InView(ViewKind.Header),
        [nameof(WorldSnapshot.Buildings)] = Carrier.InView(ViewKind.Buildings),
        [nameof(WorldSnapshot.BuildingsRevision)] = Carrier.InView(ViewKind.Buildings),
        [nameof(WorldSnapshot.GroundItems)] = Carrier.InView(ViewKind.GroundItems),
        [nameof(WorldSnapshot.Containers)] = Carrier.InView(ViewKind.Containers),
        [nameof(WorldSnapshot.DoorPicks)] = Carrier.InView(ViewKind.DoorPicks),
        [nameof(WorldSnapshot.GroundItemsRevision)] = Carrier.InView(ViewKind.GroundItems),
        [nameof(WorldSnapshot.UnlockedBuildings)] = Carrier.InView(ViewKind.Unlocked),
        [nameof(WorldSnapshot.Doors)] = Carrier.InView(ViewKind.Doors),
        [nameof(WorldSnapshot.InspectedBuilding)] = Carrier.InView(ViewKind.Inspected),
        [nameof(WorldSnapshot.Machines)] = Carrier.InView(ViewKind.Machines),
        [nameof(WorldSnapshot.MachinesVersion)] = Carrier.InView(ViewKind.Machines),
        [nameof(WorldSnapshot.Research)] = Carrier.InView(ViewKind.Research),
        [nameof(WorldSnapshot.Crafts)] = Carrier.InView(ViewKind.Crafts),
    };

    static readonly Dictionary<string, Carrier> Character = new()
    {
        [nameof(CharacterSnapshot.Id)] = Carrier.InPart("record slot and generation"),
        [nameof(CharacterSnapshot.Appearance)] = Carrier.InGroup(CharacterWire.Look),
        [nameof(CharacterSnapshot.Position)] = Carrier.InGroup(CharacterWire.Position),
        [nameof(CharacterSnapshot.Yaw)] = Carrier.InGroup(CharacterWire.Yaw),
        [nameof(CharacterSnapshot.Animations)] = Carrier.InGroup(CharacterWire.Animations),
        [nameof(CharacterSnapshot.Faction)] = Carrier.InGroup(CharacterWire.Identity),
        [nameof(CharacterSnapshot.WeaponDrawn)] = Carrier.InGroup(CharacterWire.Flags),
        [nameof(CharacterSnapshot.Name)] = Carrier.InGroup(CharacterWire.Identity),
        [nameof(CharacterSnapshot.SquadId)] = Carrier.InGroup(CharacterWire.Identity),
        [nameof(CharacterSnapshot.IsPlayer)] = Carrier.InGroup(CharacterWire.Flags),
        [nameof(CharacterSnapshot.Owner)] = Carrier.InGroup(CharacterWire.Identity),
        [nameof(CharacterSnapshot.IsTrader)] = Carrier.InGroup(CharacterWire.Flags),
        [nameof(CharacterSnapshot.SurgeryCost)] = Carrier.InGroup(CharacterWire.Identity),
        [nameof(CharacterSnapshot.Selected)] = Carrier.InGroup(CharacterWire.Flags),
        [nameof(CharacterSnapshot.Inspected)] = Carrier.InGroup(CharacterWire.Flags),
        [nameof(CharacterSnapshot.Orders)] = Carrier.InGroup(CharacterWire.Flags),
        [nameof(CharacterSnapshot.RunSpeed)] = Carrier.InGroup(CharacterWire.Flags),
        [nameof(CharacterSnapshot.Jobs)] = Carrier.InGroup(CharacterWire.Jobs),
        [nameof(CharacterSnapshot.Path)] = Carrier.InView(ViewKind.Path),
        [nameof(CharacterSnapshot.Body)] = Carrier.InGroup(CharacterWire.Body),
        [nameof(CharacterSnapshot.Inventory)] = Carrier.InView(ViewKind.Inventory),
        [nameof(CharacterSnapshot.Bounties)] = Carrier.InView(ViewKind.Stats),
        [nameof(CharacterSnapshot.Skills)] = Carrier.InView(ViewKind.Stats),
        [nameof(CharacterSnapshot.AllSkills)] = Carrier.InView(ViewKind.Stats),
        [nameof(CharacterSnapshot.SkillXpMultipliers)] = Carrier.InView(ViewKind.Stats),
        [nameof(CharacterSnapshot.HealthParts)] = Carrier.InView(ViewKind.Health),
        [nameof(CharacterSnapshot.Hud)] = Carrier.InView(ViewKind.Stats),
        [nameof(CharacterSnapshot.ItemGrid)] = Carrier.InView(ViewKind.Inventory),
        [nameof(CharacterSnapshot.CarriedBy)] = Carrier.InGroup(CharacterWire.Carry),
        [nameof(CharacterSnapshot.AttachBone)] = Carrier.InGroup(CharacterWire.Carry),
        [nameof(CharacterSnapshot.Scale)] = Carrier.InGroup(CharacterWire.Look),
        [nameof(CharacterSnapshot.Task)] = Carrier.InGroup(CharacterWire.Flags),
        [nameof(CharacterSnapshot.PickingLock)] = Carrier.InGroup(CharacterWire.Flags),
        [nameof(CharacterSnapshot.WorkingShackles)] = Carrier.InGroup(CharacterWire.Flags),
        [nameof(CharacterSnapshot.InBed)] = Carrier.InGroup(CharacterWire.Flags),
        [nameof(CharacterSnapshot.InCombat)] = Carrier.InGroup(CharacterWire.Flags),
        [nameof(CharacterSnapshot.Shackled)] = Carrier.InGroup(CharacterWire.Flags),
        [nameof(CharacterSnapshot.ShacklesLocked)] = Carrier.InGroup(CharacterWire.Flags),
        [nameof(CharacterSnapshot.CanBeShackled)] = Carrier.InGroup(CharacterWire.Flags),
        [nameof(CharacterSnapshot.CarriesShackles)] = Carrier.InGroup(CharacterWire.Flags),
        [nameof(CharacterSnapshot.PortraitSerial)] = Carrier.InGroup(CharacterWire.Flags),
        [nameof(CharacterSnapshot.Carrying)] = Carrier.InGroup(CharacterWire.Carry),
        [nameof(CharacterSnapshot.Activity)] = Carrier.InGroup(CharacterWire.Identity),
        [nameof(CharacterSnapshot.Stealth)] = Carrier.InGroup(CharacterWire.Stealth),
        [nameof(CharacterSnapshot.StealthKoChance)] = Carrier.InGroup(CharacterWire.Stealth),
        [nameof(CharacterSnapshot.Prison)] = Carrier.InGroup(CharacterWire.Prison),
        [nameof(CharacterSnapshot.Turret)] = Carrier.InGroup(CharacterWire.Turret),
        [nameof(CharacterSnapshot.Watchers)] = Carrier.InGroup(CharacterWire.Stealth),
        [nameof(CharacterSnapshot.Ragdoll)] = Carrier.InGroup(CharacterWire.Ragdoll),
    };

    static string[] Properties(Type type) =>
        [.. type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name).Order(StringComparer.Ordinal)];

    [Fact]
    public void Every_snapshot_property_has_a_carrier_and_every_carrier_is_used()
    {
        Assert.Equal(Properties(typeof(WorldSnapshot)), World.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(Properties(typeof(CharacterSnapshot)), Character.Keys.Order(StringComparer.Ordinal));

        var all = World.Values.Concat(Character.Values).ToList();
        foreach (var c in all)
        {
            int kinds = (c.Part is null ? 0 : 1) + (c.Group == 0 ? 0 : 1) + (c.View is null ? 0 : 1);
            Assert.Equal(1, kinds);
            if (c.Group != 0) Assert.True(c.Group == (c.Group & CharacterWire.AllGroups) && (c.Group & (c.Group - 1)) == 0, $"group {c.Group:X}");
            if (c.View is { } view) Assert.True(Enum.IsDefined(view), $"view {view}");
        }
        // No carrier is idle: every group of the character record and every view kind carries something.
        for (int bit = 0; bit < CharacterWire.GroupCount; bit++) Assert.Contains(all, c => c.Group == 1u << bit);
        foreach (var kind in Enum.GetValues<ViewKind>()) Assert.Contains(all, c => c.View == kind);

        // The view codecs' own claims agree with the table.
        foreach (string name in ViewCodecs.PlayerCovered) Assert.NotNull(World[name].View);
        foreach (string name in ViewCodecs.DetailCovered) Assert.NotNull(Character[name].View);
    }
}
