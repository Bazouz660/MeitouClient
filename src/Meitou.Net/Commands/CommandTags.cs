using Meitou.Simulation;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Carrying;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Crime;
using Meitou.Simulation.Dialogue;
using Meitou.Simulation.Editing;
using Meitou.Simulation.FirstAid;
using Meitou.Simulation.Items;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Production;
using Meitou.Simulation.Trading;

namespace Meitou.Net.Commands;

/// <summary>Who may send a command (docs/net.md, "Command codec"): any client, a client only when the host allows debug commands (cheats), or nobody (the host's own commands).</summary>
public enum CommandOrigin : byte { Client, Debug, HostOnly }

/// <summary>
/// The explicit, never reordered tag of every concrete <see cref="SimCommand"/> on the wire (docs/net.md, "Command codec"). A new command gets a new tag;
/// a removed one leaves a gap. The abstract <see cref="SimCommand"/>, <see cref="DebugCommand"/> and <see cref="SquadCommand"/> have none. The table is
/// the only list: the codec's switches are checked against it by the completeness test.
/// </summary>
public static class CommandTags
{
    static readonly (ushort Tag, Type Type, CommandOrigin Origin)[] Table =
    [
        (1, typeof(MoveOrder), CommandOrigin.Client),
        (2, typeof(SandboxMovement), CommandOrigin.HostOnly),
        (3, typeof(FocusCommand), CommandOrigin.Client),
        (4, typeof(SelectCommand), CommandOrigin.Client),
        (5, typeof(InspectCommand), CommandOrigin.Client),
        (6, typeof(InspectBuildingCommand), CommandOrigin.Client),
        (7, typeof(SeenDiscoveriesCommand), CommandOrigin.Client),
        (8, typeof(StopCommand), CommandOrigin.Client),
        (9, typeof(RepathCommand), CommandOrigin.HostOnly),

        (20, typeof(OrdersCommand), CommandOrigin.Client),
        (21, typeof(RunSpeedCommand), CommandOrigin.Client),
        (22, typeof(SquadAiCommand), CommandOrigin.Client),

        (30, typeof(NewSquadCommand), CommandOrigin.Client),
        (31, typeof(RenameSquadCommand), CommandOrigin.Client),
        (32, typeof(RemoveSquadCommand), CommandOrigin.Client),
        (33, typeof(MoveToSquadCommand), CommandOrigin.Client),
        (34, typeof(ReorderSquadCommand), CommandOrigin.Client),
        (35, typeof(ActiveSquadCommand), CommandOrigin.Client),
        (36, typeof(DismissCommand), CommandOrigin.Client),

        (40, typeof(AttackOrder), CommandOrigin.Client),
        (41, typeof(PickUpOrder), CommandOrigin.Client),
        (42, typeof(PutDownOrder), CommandOrigin.Client),
        (43, typeof(LootOrder), CommandOrigin.Client),
        (44, typeof(StealthKoOrder), CommandOrigin.Client),
        (45, typeof(RescueJobCommand), CommandOrigin.Client),

        (50, typeof(TreatOrder), CommandOrigin.Client),
        (51, typeof(SplintOrder), CommandOrigin.Client),
        (52, typeof(RestOrder), CommandOrigin.Client),
        (53, typeof(MedicJobCommand), CommandOrigin.Client),

        (60, typeof(TalkOrder), CommandOrigin.Client),
        (61, typeof(DialogueChoice), CommandOrigin.Client),
        (62, typeof(EndConversation), CommandOrigin.Client),
        (63, typeof(EditCharacter), CommandOrigin.Client),
        (64, typeof(FinishCreation), CommandOrigin.Client),

        (70, typeof(OpenTrade), CommandOrigin.Client),
        (71, typeof(CloseTrade), CommandOrigin.Client),
        (72, typeof(BuyItem), CommandOrigin.Client),
        (73, typeof(SellItem), CommandOrigin.Client),
        (74, typeof(ArrangeStock), CommandOrigin.Client),

        (80, typeof(MoveInventoryItem), CommandOrigin.Client),
        (81, typeof(ArrangeInventory), CommandOrigin.Client),
        (82, typeof(ReadMapItem), CommandOrigin.Client),
        (83, typeof(PickUpItemOrder), CommandOrigin.Client),
        (84, typeof(OpenContainerOrder), CommandOrigin.Client),
        (85, typeof(CloseContainer), CommandOrigin.Client),
        (86, typeof(PickLockOrder), CommandOrigin.Client),
        (87, typeof(GetOutOrder), CommandOrigin.Client),
        (88, typeof(ToggleDoorsOrder), CommandOrigin.Client),
        (89, typeof(ToggleDoorLocksOrder), CommandOrigin.Client),
        (90, typeof(DoorOrder), CommandOrigin.Client),
        (91, typeof(PutShacklesOrder), CommandOrigin.Client),
        (92, typeof(ShackleOrder), CommandOrigin.Client),
        (93, typeof(PayBailOrder), CommandOrigin.Client),
        (94, typeof(PutInCageOrder), CommandOrigin.Client),

        (100, typeof(OperateOrder), CommandOrigin.Client),
        (101, typeof(AddJob), CommandOrigin.Client),
        (102, typeof(RemoveJob), CommandOrigin.Client),
        (103, typeof(MoveJob), CommandOrigin.Client),
        (104, typeof(PutInMachine), CommandOrigin.Client),
        (105, typeof(TakeFromMachine), CommandOrigin.Client),
        (106, typeof(QueueResearch), CommandOrigin.Client),
        (107, typeof(CancelResearch), CommandOrigin.Client),
        (108, typeof(QueueCraft), CommandOrigin.Client),
        (109, typeof(CancelCraft), CommandOrigin.Client),
        (110, typeof(RepeatCrafts), CommandOrigin.Client),
        (111, typeof(ProspectOrder), CommandOrigin.Client),
        (112, typeof(PlaceBuildings), CommandOrigin.Client),
        (113, typeof(BuildOrder), CommandOrigin.Client),
        (114, typeof(DismantleOrder), CommandOrigin.Client),
        (115, typeof(CancelSite), CommandOrigin.Client),
        (116, typeof(PauseSite), CommandOrigin.Client),

        (200, typeof(DebugMoney), CommandOrigin.Debug),
        (201, typeof(DebugTeleport), CommandOrigin.Debug),
        (202, typeof(DebugHeal), CommandOrigin.Debug),
        (203, typeof(DebugMaxSkills), CommandOrigin.Debug),
        (204, typeof(DebugDamage), CommandOrigin.Debug),
        (205, typeof(DebugGiveItem), CommandOrigin.Debug),
        (206, typeof(DebugSpawn), CommandOrigin.Debug),

        (240, typeof(StartContract), CommandOrigin.HostOnly),
        (241, typeof(JoinPlayer), CommandOrigin.HostOnly),
        (242, typeof(TransferSquad), CommandOrigin.HostOnly),
    ];

    static readonly Dictionary<Type, ushort> ByType = Table.ToDictionary(e => e.Type, e => e.Tag);
    static readonly Dictionary<ushort, (Type Type, CommandOrigin Origin)> ByTag = Table.ToDictionary(e => e.Tag, e => (e.Type, e.Origin));

    /// <summary>Every tagged command type, ascending by tag.</summary>
    public static IReadOnlyList<(ushort Tag, Type Type, CommandOrigin Origin)> All => Table;

    /// <summary>The tag of a command type; <see cref="ArgumentException"/> when it has none.</summary>
    public static ushort TagOf(Type type) =>
        ByType.TryGetValue(type, out ushort tag) ? tag : throw new ArgumentException($"{type.Name} has no wire tag.", nameof(type));

    /// <summary>The type and origin of a tag; <see cref="NetFormatException"/> for an unknown one (the sender is not speaking this protocol).</summary>
    public static (Type Type, CommandOrigin Origin) Of(ushort tag) =>
        ByTag.TryGetValue(tag, out var entry) ? entry : throw new NetFormatException($"Unknown command tag {tag}.");
}
