using System.Collections;
using System.Numerics;
using System.Reflection;
using Meitou.Data.Fcs;
using Meitou.Net;
using Meitou.Net.Commands;
using Meitou.Simulation;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Editing;
using Meitou.Simulation.Items;

namespace Meitou.Tests.Net;

/// <summary>
/// <see cref="CommandCodec"/>: every concrete <see cref="SimCommand"/> has a tag and survives the wire with every field (constructor parameters and init-only
/// properties) set to a non-default value; host-only and debug tags are refused (docs/net.md, "Command codec").
/// </summary>
public class CommandCodecTests
{
    const long SampleTick = 77;
    const int SamplePlayer = 3;

    static IEnumerable<Type> ConcreteCommands() =>
        typeof(SimCommand).Assembly.GetTypes().Where(t => t.IsSubclassOf(typeof(SimCommand)) && !t.IsAbstract).OrderBy(t => t.Name, StringComparer.Ordinal);

    static FcsRecord SampleRecord()
    {
        var record = new FcsRecord { ByteSize = 99, Type = 41, Id = -5, Name = "Üñï 名前", StringId = "12-test.mod", Flags = 0xFFFFFFF3 };
        record.Bools["a"] = true;
        record.Bools["b"] = false;
        record.Floats["f"] = 1.25f;
        record.Ints["i"] = -17;
        record.Vector3s["v3"] = new Vector3(1, -2, 3);
        record.Vector4s["v4"] = new Vector4(0.1f, 0.2f, 0.3f, 1);
        record.Strings["s"] = "text";
        record.Filenames["m"] = "body/male.mesh";
        record.References["conditions"] = [new("2-test.mod", 1, 0, -7)];
        var placed = new FcsInstance { Id = "i1", Target = "5-test.mod", Position = new Vector3(10, 20, 30), Rotation = new Quaternion(0.1f, 0.2f, 0.3f, 0.9f) };
        placed.States.Add("open");
        record.Instances.Add(placed);
        return record;
    }

    /// <summary>A non-default value of <paramref name="type"/>; a type missing here fails the test by name so a new field kind cannot slip through.</summary>
    static object Sample(Type type, string what)
    {
        Type? nullable = Nullable.GetUnderlyingType(type);
        if (nullable is not null) type = nullable;
        if (type == typeof(int)) return 1234;
        if (type == typeof(long)) return 5_000_000_123L;
        if (type == typeof(bool)) return true;
        if (type == typeof(byte)) return (byte)201;
        if (type == typeof(float)) return 2.5f;
        if (type == typeof(string)) return "sample-" + what;
        if (type == typeof(Vector2)) return new Vector2(1.5f, -2.5f);
        if (type == typeof(Vector3)) return new Vector3(1.5f, -2.5f, 3.5f);
        if (type == typeof(CharacterId)) return new CharacterId(12, 34);
        if (type == typeof(IReadOnlyList<CharacterId>)) return new[] { new CharacterId(1, 2), new CharacterId(300, 4), new CharacterId(5, 600) };
        if (type == typeof(InventoryItemToken)) return new InventoryItemToken(3, "item-record", "backpack_content", 4, 5, 6, 0.1f);
        if (type == typeof(IReadOnlyList<PlannedBuilding>))
            return new[]
            {
                new PlannedBuilding("hut", new Vector3(1, 2, 3), new Quaternion(0.1f, 0.2f, 0.3f, 0.9f), 2, "parent-instance"),
                new PlannedBuilding("wall", new Vector3(-4, 5, -6), Quaternion.Identity, 0, null),
            };
        if (type == typeof(FcsRecord)) return SampleRecord();
        if (type.IsEnum)
        {
            var values = Enum.GetValues(type).Cast<object>().Select(v => Convert.ToInt64(v)).ToArray();
            long bits = type.IsDefined(typeof(FlagsAttribute), false) ? values.Aggregate(0L, (a, b) => a | b) : values.Max();
            return Enum.ToObject(type, bits);
        }
        throw new InvalidOperationException($"The sample table has no value for {type} ({what}); add one.");
    }

    static SimCommand MakeSample(Type type)
    {
        var constructor = Assert.Single(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        var parameters = constructor.GetParameters();
        var command = (SimCommand)constructor.Invoke(parameters.Select(p => Sample(p.ParameterType, $"{type.Name}.{p.Name}")).ToArray());
        var positional = parameters.Select(p => p.Name!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.Name is nameof(SimCommand.Tick) or nameof(SimCommand.Player) || positional.Contains(property.Name) || property.SetMethod is null) continue;
            property.SetValue(command, Sample(property.PropertyType, $"{type.Name}.{property.Name}"));
        }
        return command with { Tick = 5, Player = 6 };
    }

    static IEnumerable<PropertyInfo> Fields(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.Name is not (nameof(SimCommand.Tick) or nameof(SimCommand.Player)));

    static bool IsDefault(object? value) => value switch
    {
        null => true,
        string s => s.Length == 0,
        IEnumerable e => !e.Cast<object>().Any(),
        _ => value.GetType().IsValueType && value.Equals(Activator.CreateInstance(value.GetType())),
    };

    static void AssertSame(object? expected, object? actual, string path)
    {
        switch (expected)
        {
            case FcsRecord record:
                var other = Assert.IsType<FcsRecord>(actual);
                Assert.Equal((record.ByteSize, record.Type, record.Id, record.Name, record.StringId, record.Flags), (other.ByteSize, other.Type, other.Id, other.Name, other.StringId, other.Flags));
                Assert.Equal(record.Bools.ToArray(), other.Bools.ToArray());
                Assert.Equal(record.Floats.ToArray(), other.Floats.ToArray());
                Assert.Equal(record.Ints.ToArray(), other.Ints.ToArray());
                Assert.Equal(record.Vector3s.ToArray(), other.Vector3s.ToArray());
                Assert.Equal(record.Vector4s.ToArray(), other.Vector4s.ToArray());
                Assert.Equal(record.Strings.ToArray(), other.Strings.ToArray());
                Assert.Equal(record.Filenames.ToArray(), other.Filenames.ToArray());
                Assert.Equal(record.References.Keys, other.References.Keys);
                foreach (var (category, references) in record.References) Assert.Equal(references, other.References[category]);
                Assert.Equal(record.Instances.Count, other.Instances.Count);
                for (int i = 0; i < record.Instances.Count; i++)
                {
                    var a = record.Instances[i];
                    var b = other.Instances[i];
                    Assert.Equal((a.Id, a.Target, a.Position, a.Rotation), (b.Id, b.Target, b.Position, b.Rotation));
                    Assert.Equal(a.States, b.States);
                }
                break;
            case string:
                Assert.True(Equals(expected, actual), $"{path}: expected \"{expected}\", got \"{actual}\".");
                break;
            case IEnumerable list:
                var items = list.Cast<object>().ToArray();
                var actualItems = Assert.IsAssignableFrom<IEnumerable>(actual).Cast<object>().ToArray();
                Assert.True(items.Length == actualItems.Length, $"{path}: expected {items.Length} elements, got {actualItems.Length}.");
                for (int i = 0; i < items.Length; i++) AssertSame(items[i], actualItems[i], $"{path}[{i}]");
                break;
            default:
                Assert.True(Equals(expected, actual), $"{path}: expected {expected}, got {actual}.");
                break;
        }
    }

    static byte[] Encode(SimCommand command, StringTableWriter? table = null)
    {
        using var w = new NetWriter();
        CommandCodec.Write(w, table ?? new StringTableWriter(confirmOnSend: true), command);
        return w.Written.ToArray();
    }

    [Fact]
    public void Every_concrete_command_has_exactly_one_tag_and_abstract_ones_none()
    {
        var concrete = ConcreteCommands().ToArray();
        var tagged = CommandTags.All.Select(e => e.Type).ToArray();
        Assert.Empty(concrete.Except(tagged).Select(t => t.Name));
        Assert.Empty(tagged.Except(concrete).Select(t => t.Name));
        Assert.Equal(tagged.Length, tagged.Distinct().Count());
        Assert.Equal(CommandTags.All.Count, CommandTags.All.Select(e => e.Tag).Distinct().Count());
        Assert.DoesNotContain(CommandTags.All, e => e.Type.IsAbstract || e.Tag == 0);
        Assert.Equal(CommandTags.All.OrderBy(e => e.Tag).Select(e => e.Tag), CommandTags.All.Select(e => e.Tag));
        foreach (var (tag, type, origin) in CommandTags.All)
        {
            Assert.Equal(tag, CommandTags.TagOf(type));
            Assert.Equal((type, origin), CommandTags.Of(tag));
        }
        Assert.Equal(241, CommandTags.TagOf(typeof(JoinPlayer)));
        Assert.Equal(CommandOrigin.HostOnly, CommandTags.Of(241).Origin);
        Assert.Equal(CommandOrigin.HostOnly, CommandTags.Of(242).Origin);
        Assert.Throws<ArgumentException>(() => CommandTags.TagOf(typeof(SimCommand)));
        Assert.Throws<ArgumentException>(() => CommandTags.TagOf(typeof(DebugCommand)));
        Assert.Throws<ArgumentException>(() => CommandTags.TagOf(typeof(SquadCommand)));
        Assert.Throws<NetFormatException>(() => CommandTags.Of(9999));
    }

    [Fact]
    public void Every_command_round_trips_with_every_field_non_default_and_tick_and_player_set()
    {
        foreach (var type in ConcreteCommands())
        {
            var sample = MakeSample(type);
            foreach (var property in Fields(type))
                Assert.False(IsDefault(property.GetValue(sample)), $"{type.Name}.{property.Name} is default in the sample, so a missing field would go unnoticed.");

            var bytes = Encode(sample);
            var r = new NetReader(bytes);
            var decoded = CommandCodec.ReadUnchecked(ref r, new StringTableReader(), SampleTick, SamplePlayer);
            r.ExpectEnd();

            Assert.Equal(type, decoded.GetType());
            Assert.Equal(SampleTick, decoded.Tick);
            Assert.Equal(SamplePlayer, decoded.Player);
            foreach (var property in Fields(type))
            {
                var value = property.GetValue(decoded);
                Assert.False(IsDefault(value), $"{type.Name}.{property.Name} decoded to its default.");
                AssertSame(property.GetValue(sample), value, $"{type.Name}.{property.Name}");
            }
        }
    }

    [Fact]
    public void Host_only_tags_are_always_refused_and_debug_tags_unless_allowed()
    {
        foreach (var (_, type, origin) in CommandTags.All)
        {
            var bytes = Encode(MakeSample(type));
            foreach (bool allowDebug in new[] { false, true })
            {
                bool refused = origin == CommandOrigin.HostOnly || (origin == CommandOrigin.Debug && !allowDebug);
                var r = new NetReader(bytes);
                if (refused)
                {
                    bool thrown = false;
                    try { CommandCodec.Read(ref r, new StringTableReader(), 1, 1, allowDebug); }
                    catch (NetFormatException) { thrown = true; }
                    Assert.True(thrown, $"{type.Name} ({origin}, allowDebug {allowDebug}) should be refused.");
                }
                else
                {
                    var command = CommandCodec.Read(ref r, new StringTableReader(), 1, 1, allowDebug);
                    Assert.Equal(type, command.GetType());
                }
            }
        }

        byte[] unknownTag = [0xE7, 0x07]; // varint 999
        Assert.Throws<NetFormatException>(() =>
        {
            var u = new NetReader(unknownTag);
            CommandCodec.Read(ref u, new StringTableReader(), 1, 1, true);
        });
    }

    [Fact]
    public void A_batch_and_a_result_round_trip_through_shared_string_tables()
    {
        var plan = new PlannedBuilding("hut", Vector3.One, Quaternion.Identity);
        SimCommand[] commands =
        [
            MakeSample(typeof(MoveOrder)), MakeSample(typeof(EditCharacter)), MakeSample(typeof(PlaceBuildings)) with { Tick = 1 },
            MakeSample(typeof(DebugMoney)), new CloseContainer { Tick = 9 }, new PlaceBuildings([plan]) { Tick = 2 },
        ];
        var table = new StringTableWriter(confirmOnSend: true);
        var strings = new StringTableReader();
        using var w = new NetWriter();

        CommandBatchCodec.Write(w, table, new CommandBatch(7, 1_000_000u, commands));
        int first = w.Length;
        CommandBatchCodec.Write(w, table, new CommandBatch(8, 1_000_006u, commands));
        Assert.True(w.Length - first < first, "The second batch should reuse the string table.");
        CommandBatchCodec.Write(w, new CommandResult(1_000_003u, "Not your character."));

        var r = new NetReader(w.Written);
        var batch = CommandBatchCodec.Read(ref r, strings, 40, 2, allowDebug: true);
        Assert.Equal((ushort)7, batch.Epoch);
        Assert.Equal(1_000_000u, batch.FirstSeq);
        Assert.Equal(commands.Length, batch.Commands.Count);
        for (int i = 0; i < commands.Length; i++)
        {
            Assert.Equal(commands[i].GetType(), batch.Commands[i].GetType());
            Assert.Equal((40L, 2), (batch.Commands[i].Tick, batch.Commands[i].Player));
            foreach (var property in Fields(commands[i].GetType()))
                AssertSame(property.GetValue(commands[i]), property.GetValue(batch.Commands[i]), $"{commands[i].GetType().Name}.{property.Name}");
        }
        var second = CommandBatchCodec.Read(ref r, strings, 41, 2, allowDebug: true);
        Assert.Equal(((ushort)8, 1_000_006u), (second.Epoch, second.FirstSeq));
        Assert.Equal(new CommandResult(1_000_003u, "Not your character."), CommandBatchCodec.ReadResult(ref r));
        r.ExpectEnd();

        byte[] bytes = w.Written.ToArray();
        Assert.Throws<NetFormatException>(() =>
        {
            var denied = new NetReader(bytes);
            CommandBatchCodec.Read(ref denied, new StringTableReader(), 40, 2, allowDebug: false);
        });
    }
}
