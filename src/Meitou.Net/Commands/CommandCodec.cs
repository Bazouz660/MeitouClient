using Meitou.Data.Gameplay.Ai;
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

/// <summary>
/// A <see cref="SimCommand"/> on the wire (docs/net.md, "Command codec"): a varint <see cref="CommandTags"/> tag, the constructor parameters in declaration order,
/// then the init-only properties in declaration order. <see cref="SimCommand.Tick"/> and <see cref="SimCommand.Player"/> are not sent: the reader's caller
/// supplies them (the host stamps a remote command with its own tick and the sender's number). The codec carries every field, including the trust fields
/// (<c>AttackOrder.Defensive</c>, <c>EditCharacter.Paid</c>); the command gate strips or checks them. Host-only tags are never accepted, debug tags only
/// when the host allows cheats.
/// </summary>
public static class CommandCodec
{
    /// <summary>
    /// Writes the tag and fields; <see cref="ArgumentException"/> for a type without a tag or an appearance beyond the <see cref="FcsRecordCodec"/> limits, in
    /// which case nothing stays written (the writer and the string table are rolled back). Every command type, host-only ones included, can be written.
    /// </summary>
    public static void Write(NetWriter w, StringTableWriter strings, SimCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ushort tag = CommandTags.TagOf(command.GetType());
        int start = w.Length;
        int mark = strings.Mark();
        try
        {
            WriteBody(w, strings, tag, command);
        }
        catch
        {
            w.Truncate(start);
            strings.Rollback(mark);
            throw;
        }
    }

    static void WriteBody(NetWriter w, StringTableWriter strings, ushort tag, SimCommand command)
    {
        w.WriteVarUInt(tag);
        var e = new CommandWriter(w, strings);
        switch (command)
        {
            case MoveOrder c: e.Ids(c.Characters); e.Vec3(c.Target); e.Bool(c.Queued); break;
            case SandboxMovement c: e.Id(c.Character); e.Byte(c.Mode); e.Float(c.Speed); break;
            case FocusCommand c: e.Vec3(c.Position); break;
            case SelectCommand c: e.Ids(c.Characters); e.Bool(c.Additive); break;
            case InspectCommand c: e.Id(c.Target); break;
            case InspectBuildingCommand c: e.KeyOrNull(c.Instance); break;
            case SeenDiscoveriesCommand: break;
            case StopCommand c: e.Ids(c.Characters); break;
            case RepathCommand: break;

            case OrdersCommand c: e.Ids(c.Characters); e.Value(c.Order); e.Bool(c.On); break;
            case RunSpeedCommand c: e.Ids(c.Characters); e.Int(c.Step); break;
            case SquadAiCommand c: e.Value(c.Option); e.Bool(c.On); break;

            case NewSquadCommand c: e.Vec3(c.At); break;
            case RenameSquadCommand c: e.Int(c.Squad); e.Text(c.Name); break;
            case RemoveSquadCommand c: e.Int(c.Squad); break;
            case MoveToSquadCommand c: e.Id(c.Character); e.Int(c.Squad); e.Int(c.Index); e.Bool(c.Swap); break;
            case ReorderSquadCommand c: e.Int(c.Squad); e.Int(c.Index); break;
            case ActiveSquadCommand c: e.Int(c.Squad); break;
            case DismissCommand c: e.Id(c.Character); break;

            case AttackOrder c: e.Ids(c.Attackers); e.Id(c.Target); e.Bool(c.Defensive); break;
            case PickUpOrder c: e.Ids(c.Carriers); e.Id(c.Target); break;
            case PutDownOrder c: e.Ids(c.Carriers); break;
            case LootOrder c: e.Ids(c.Looters); e.Id(c.Target); break;
            case StealthKoOrder c: e.Ids(c.Attackers); e.Id(c.Target); break;
            case RescueJobCommand c: e.Ids(c.Characters); e.Bool(c.Job); break;

            case TreatOrder c: e.Ids(c.Medics); e.Id(c.Patient); break;
            case SplintOrder c: e.Ids(c.Medics); e.Id(c.Patient); break;
            case RestOrder c: e.Ids(c.Characters); break;
            case MedicJobCommand c: e.Ids(c.Characters); e.Bool(c.Job); break;

            case TalkOrder c: e.Id(c.Talker); e.Id(c.Target); break;
            case DialogueChoice c: e.Int(c.Index); break;
            case EndConversation: break;
            case EditCharacter c: e.Id(c.Character); e.Fcs(c.Appearance); e.Text(c.Name); e.Value(c.Kind); e.Id(c.Surgeon); e.Bool(c.Paid); break;
            case FinishCreation: break;

            case OpenTrade c: e.Id(c.Customer); e.Id(c.Trader); break;
            case CloseTrade: break;
            case BuyItem c: e.Token(c.Item); e.Int(c.Quantity); e.KeyOrNull(c.Section); e.Int(c.X); e.Int(c.Y); e.Bool(c.Equip); break;
            case SellItem c: e.Token(c.Item); e.Int(c.Quantity); e.Int(c.X); e.Int(c.Y); break;
            case ArrangeStock: break;

            case MoveInventoryItem c: e.Id(c.Character); e.Token(c.Item); e.Key(c.Section); e.Int(c.X); e.Int(c.Y); break;
            case ArrangeInventory c: e.Id(c.Character); break;
            case ReadMapItem c: e.Id(c.Character); e.Token(c.Item); break;
            case PickUpItemOrder c: e.Ids(c.Characters); e.Int(c.Item); break;
            case OpenContainerOrder c: e.Ids(c.Characters); e.Key(c.Fixture); break;
            case CloseContainer: break;
            case PickLockOrder c: e.Ids(c.Characters); e.Id(c.Prisoner); break;
            case GetOutOrder c: e.Ids(c.Characters); break;
            case ToggleDoorsOrder c: e.Key(c.Building); break;
            case ToggleDoorLocksOrder c: e.Key(c.Building); break;
            case DoorOrder c: e.Ids(c.Characters); e.Key(c.Door); e.Bool(c.PickLock); break;
            case PutShacklesOrder c: e.Ids(c.Characters); e.Id(c.Wearer); break;
            case ShackleOrder c: e.Ids(c.Characters); e.Id(c.Wearer); e.Value(c.Work); break;
            case PayBailOrder c: e.Id(c.Prisoner); break;
            case PutInCageOrder c: e.Ids(c.Carriers); break;

            case OperateOrder c: e.Ids(c.Characters); e.Key(c.Site); break;
            case AddJob c: e.Ids(c.Characters); e.Value(c.Type); e.Key(c.Site); break;
            case RemoveJob c: e.Id(c.Character); e.Int(c.Index); break;
            case MoveJob c: e.Id(c.Character); e.Int(c.From); e.Int(c.To); break;
            case PutInMachine c: e.Id(c.Character); e.Token(c.Item); e.Key(c.Site); e.Int(c.Slot); e.Int(c.X); e.Int(c.Y); break;
            case TakeFromMachine c: e.Id(c.Character); e.Key(c.Site); e.Int(c.Slot); e.Int(c.Index); e.Key(c.Section); break;
            case QueueResearch c: e.Key(c.Research); break;
            case CancelResearch c: e.Key(c.Research); break;
            case QueueCraft c: e.Key(c.Site); e.Key(c.Option); e.Int(c.Count); break;
            case CancelCraft c: e.Key(c.Site); e.Int(c.Index); break;
            case RepeatCrafts c: e.Key(c.Site); e.Bool(c.Repeat); break;
            case ProspectOrder c: e.Id(c.Character); break;
            case PlaceBuildings c: e.Plans(c.Plans); break;
            case BuildOrder c: e.Ids(c.Characters); e.Key(c.Site); e.Bool(c.Repair); break;
            case DismantleOrder c: e.Ids(c.Characters); e.Key(c.Site); break;
            case CancelSite c: e.Key(c.Site); break;
            case PauseSite c: e.Key(c.Site); e.Bool(c.Paused); break;

            case DebugMoney c: e.Long(c.Delta); break;
            case DebugTeleport c: e.Ids(c.Characters); e.Vec3(c.Target); break;
            case DebugHeal c: e.Ids(c.Characters); break;
            case DebugMaxSkills c: e.Ids(c.Characters); break;
            case DebugDamage c: e.Ids(c.Characters); e.Float(c.Cut); break;
            case DebugGiveItem c: e.Id(c.Character); e.Key(c.Record); e.Int(c.Quantity); break;
            case DebugSpawn c: e.Key(c.Record); e.Vec2(c.At); break;

            case StartContract c: e.Int(c.SquadId); e.Key(c.PackageId); e.Float(c.Hours); break;
            case JoinPlayer c: e.KeyOrNull(c.StartId); e.Vec3(c.Near); break;
            case TransferSquad c: e.Int(c.Squad); e.Int(c.Owner); break;

            default: throw new InvalidOperationException($"{command.GetType().Name} is tagged but has no writer.");
        }
    }

    /// <summary>
    /// Reads one command. <paramref name="tick"/> and <paramref name="player"/> are set on it. A host-only tag always throws <see cref="NetFormatException"/>, a
    /// debug tag unless <paramref name="allowDebug"/>; so does an unknown tag, a non-finite float, an undefined enum value or an over-limit count.
    /// </summary>
    public static SimCommand Read(ref NetReader r, StringTableReader strings, long tick, int player, bool allowDebug)
    {
        var d = new CommandReader(r, strings);
        var command = ReadBody(ref d, tick, player, allowDebug, allowHostOnly: false);
        r = d.Reader;
        return command;
    }

    /// <summary>Reads any tagged command, host-only ones included: for tests of the layouts the gate keeps off the wire.</summary>
    internal static SimCommand ReadUnchecked(ref NetReader r, StringTableReader strings, long tick, int player)
    {
        var d = new CommandReader(r, strings);
        var command = ReadBody(ref d, tick, player, allowDebug: true, allowHostOnly: true);
        r = d.Reader;
        return command;
    }

    static SimCommand ReadBody(ref CommandReader d, long tick, int player, bool allowDebug, bool allowHostOnly)
    {
        ushort tag = d.Tag();
        var (type, origin) = CommandTags.Of(tag);
        if (origin == CommandOrigin.HostOnly && !allowHostOnly) throw new NetFormatException($"{type.Name} (tag {tag}) is host-only.");
        if (origin == CommandOrigin.Debug && !allowDebug) throw new NetFormatException($"{type.Name} (tag {tag}) is a debug command and cheats are off.");
        switch (tag)
        {
            case 1: return new MoveOrder(d.Ids(), d.Vec3()) { Tick = tick, Player = player, Queued = d.Bool() };
            case 2: return new SandboxMovement(d.Id(), d.Byte(), d.NullableFloat()) { Tick = tick, Player = player };
            case 3: return new FocusCommand(d.Vec3()) { Tick = tick, Player = player };
            case 4: return new SelectCommand(d.Ids(), d.Bool()) { Tick = tick, Player = player };
            case 5: return new InspectCommand(d.Id()) { Tick = tick, Player = player };
            case 6: return new InspectBuildingCommand(d.KeyOrNull()) { Tick = tick, Player = player };
            case 7: return new SeenDiscoveriesCommand { Tick = tick, Player = player };
            case 8: return new StopCommand(d.Ids()) { Tick = tick, Player = player };
            case 9: return new RepathCommand { Tick = tick, Player = player };

            case 20: return new OrdersCommand(d.Ids(), d.Value<CharacterOrders>(), d.NullableBool()) { Tick = tick, Player = player };
            case 21: return new RunSpeedCommand(d.Ids(), d.Int()) { Tick = tick, Player = player };
            case 22: return new SquadAiCommand(d.Value<SquadAi>(), d.Bool()) { Tick = tick, Player = player };

            case 30: return new NewSquadCommand(d.Vec3()) { Tick = tick, Player = player };
            case 31: return new RenameSquadCommand(d.Int(), d.Text()) { Tick = tick, Player = player };
            case 32: return new RemoveSquadCommand(d.Int()) { Tick = tick, Player = player };
            case 33: return new MoveToSquadCommand(d.Id(), d.Int(), d.Int(), d.Bool()) { Tick = tick, Player = player };
            case 34: return new ReorderSquadCommand(d.Int(), d.Int()) { Tick = tick, Player = player };
            case 35: return new ActiveSquadCommand(d.Int()) { Tick = tick, Player = player };
            case 36: return new DismissCommand(d.Id()) { Tick = tick, Player = player };

            case 40: return new AttackOrder(d.Ids(), d.Id()) { Tick = tick, Player = player, Defensive = d.Bool() };
            case 41: return new PickUpOrder(d.Ids(), d.Id()) { Tick = tick, Player = player };
            case 42: return new PutDownOrder(d.Ids()) { Tick = tick, Player = player };
            case 43: return new LootOrder(d.Ids(), d.Id()) { Tick = tick, Player = player };
            case 44: return new StealthKoOrder(d.Ids(), d.Id()) { Tick = tick, Player = player };
            case 45: return new RescueJobCommand(d.Ids(), d.Bool()) { Tick = tick, Player = player };

            case 50: return new TreatOrder(d.Ids(), d.Id()) { Tick = tick, Player = player };
            case 51: return new SplintOrder(d.Ids(), d.Id()) { Tick = tick, Player = player };
            case 52: return new RestOrder(d.Ids()) { Tick = tick, Player = player };
            case 53: return new MedicJobCommand(d.Ids(), d.Bool()) { Tick = tick, Player = player };

            case 60: return new TalkOrder(d.Id(), d.Id()) { Tick = tick, Player = player };
            case 61: return new DialogueChoice(d.Int()) { Tick = tick, Player = player };
            case 62: return new EndConversation { Tick = tick, Player = player };
            case 63: return new EditCharacter(d.Id(), d.Fcs(), d.Text(), d.Value<EditKind>()) { Tick = tick, Player = player, Surgeon = d.Id(), Paid = d.Bool() };
            case 64: return new FinishCreation { Tick = tick, Player = player };

            case 70: return new OpenTrade(d.Id(), d.Id()) { Tick = tick, Player = player };
            case 71: return new CloseTrade { Tick = tick, Player = player };
            case 72: return new BuyItem(d.Token(), d.Int()) { Tick = tick, Player = player, Section = d.KeyOrNull(), X = d.Int(), Y = d.Int(), Equip = d.Bool() };
            case 73: return new SellItem(d.Token(), d.Int()) { Tick = tick, Player = player, X = d.NullableInt(), Y = d.Int() };
            case 74: return new ArrangeStock { Tick = tick, Player = player };

            case 80: return new MoveInventoryItem(d.Id(), d.Token(), d.Key(), d.Int(), d.Int()) { Tick = tick, Player = player };
            case 81: return new ArrangeInventory(d.Id()) { Tick = tick, Player = player };
            case 82: return new ReadMapItem(d.Id(), d.Token()) { Tick = tick, Player = player };
            case 83: return new PickUpItemOrder(d.Ids(), d.Int()) { Tick = tick, Player = player };
            case 84: return new OpenContainerOrder(d.Ids(), d.Key()) { Tick = tick, Player = player };
            case 85: return new CloseContainer { Tick = tick, Player = player };
            case 86: return new PickLockOrder(d.Ids(), d.Id()) { Tick = tick, Player = player };
            case 87: return new GetOutOrder(d.Ids()) { Tick = tick, Player = player };
            case 88: return new ToggleDoorsOrder(d.Key()) { Tick = tick, Player = player };
            case 89: return new ToggleDoorLocksOrder(d.Key()) { Tick = tick, Player = player };
            case 90: return new DoorOrder(d.Ids(), d.Key(), d.Bool()) { Tick = tick, Player = player };
            case 91: return new PutShacklesOrder(d.Ids(), d.Id()) { Tick = tick, Player = player };
            case 92: return new ShackleOrder(d.Ids(), d.Id(), d.Value<ShackleWork>()) { Tick = tick, Player = player };
            case 93: return new PayBailOrder(d.Id()) { Tick = tick, Player = player };
            case 94: return new PutInCageOrder(d.Ids()) { Tick = tick, Player = player };

            case 100: return new OperateOrder(d.Ids(), d.Key()) { Tick = tick, Player = player };
            case 101: return new AddJob(d.Ids(), d.Value<TaskType>(), d.Key()) { Tick = tick, Player = player };
            case 102: return new RemoveJob(d.Id(), d.Int()) { Tick = tick, Player = player };
            case 103: return new MoveJob(d.Id(), d.Int(), d.Int()) { Tick = tick, Player = player };
            case 104: return new PutInMachine(d.Id(), d.Token(), d.Key(), d.Int(), d.Int(), d.Int()) { Tick = tick, Player = player };
            case 105: return new TakeFromMachine(d.Id(), d.Key(), d.Int(), d.Int(), d.Key()) { Tick = tick, Player = player };
            case 106: return new QueueResearch(d.Key()) { Tick = tick, Player = player };
            case 107: return new CancelResearch(d.Key()) { Tick = tick, Player = player };
            case 108: return new QueueCraft(d.Key(), d.Key(), d.Int()) { Tick = tick, Player = player };
            case 109: return new CancelCraft(d.Key(), d.Int()) { Tick = tick, Player = player };
            case 110: return new RepeatCrafts(d.Key(), d.Bool()) { Tick = tick, Player = player };
            case 111: return new ProspectOrder(d.Id()) { Tick = tick, Player = player };
            case 112: return new PlaceBuildings(d.Plans()) { Tick = tick, Player = player };
            case 113: return new BuildOrder(d.Ids(), d.Key(), d.Bool()) { Tick = tick, Player = player };
            case 114: return new DismantleOrder(d.Ids(), d.Key()) { Tick = tick, Player = player };
            case 115: return new CancelSite(d.Key()) { Tick = tick, Player = player };
            case 116: return new PauseSite(d.Key(), d.Bool()) { Tick = tick, Player = player };

            case 200: return new DebugMoney(d.Long()) { Tick = tick, Player = player };
            case 201: return new DebugTeleport(d.Ids(), d.Vec3()) { Tick = tick, Player = player };
            case 202: return new DebugHeal(d.Ids()) { Tick = tick, Player = player };
            case 203: return new DebugMaxSkills(d.Ids()) { Tick = tick, Player = player };
            case 204: return new DebugDamage(d.Ids(), d.Float()) { Tick = tick, Player = player };
            case 205: return new DebugGiveItem(d.Id(), d.Key(), d.Int()) { Tick = tick, Player = player };
            case 206: return new DebugSpawn(d.Key(), d.Vec2()) { Tick = tick, Player = player };

            case 240: return new StartContract(d.Int(), d.Key(), d.Float()) { Tick = tick, Player = player };
            case 241: return new JoinPlayer(d.KeyOrNull(), d.Vec3()) { Tick = tick, Player = player };
            case 242: return new TransferSquad(d.Int(), d.Int()) { Tick = tick, Player = player };

            default: throw new InvalidOperationException($"{type.Name} (tag {tag}) is accepted but has no reader.");
        }
    }
}
