using Meitou.Data.Gameplay;
using Meitou.Simulation;

namespace Meitou.Net.Replication;

/// <summary>
/// One animation layer in wire form (docs/net.md, "Snapshot core"). A layer that names an <see cref="AnimationLibrary"/> record travels as its index and
/// the client takes the name from its own library (<c>AnimationSystem.Publish</c> sets <see cref="AnimationLayer.Name"/> from the definition); a layer
/// without one (the <see cref="AnimationLayers.For"/> fallback, or an index the library does not have) carries its name. The weight is a 16-bit fraction
/// when it lies in [0, 1] (so <c>CharacterSounds</c> keeps its dominant layer), else a raw float. The time is a 16-bit fraction of <see cref="Length"/>
/// when <c>0 &lt;= Time &lt;= Length</c> (footstep events fire at fractions of the length), else a raw float.
/// </summary>
/// <param name="Definition">The library index, or -1 for a named layer.</param>
/// <param name="Name">The layer name when <paramref name="Definition"/> is -1, else null.</param>
/// <param name="Weight">The decoded weight (what the client draws).</param>
/// <param name="Length">The clip length in seconds (0: unknown).</param>
/// <param name="TimeRaw">The time is <paramref name="Time"/>; else it is <paramref name="Phase"/> / 65535 of the length.</param>
/// <param name="Phase">The time as a fraction of the length in 1/65535 steps (when not raw).</param>
/// <param name="Time">The raw time in seconds (when raw).</param>
public readonly record struct AnimationLayerWire(int Definition, string? Name, float Weight, float Length, bool TimeRaw, ushort Phase, float Time)
{
    /// <summary>The state a layer is diffed against when the baseline has none at its index: it differs in its definition.</summary>
    internal static readonly AnimationLayerWire None = new(-2, null, 0f, 0f, false, 0, 0f);

    public static AnimationLayerWire Quantize(AnimationLayer layer, AnimationLibrary library)
    {
        int definition = layer.Definition;
        string? name = null;
        if (definition < 0 || definition >= library.Definitions.Count)
        {
            definition = -1;
            name = WireText.Limit(layer.Name, NetLimits.MaxName);
        }

        float weight = layer.Weight;
        weight = !float.IsFinite(weight) ? 0f : weight is >= 0f and <= 1f ? Quant.FromU16(Quant.U16(weight, 0f, 1f), 0f, 1f) : weight;

        float length = WireText.Finite(layer.Length);
        float time = layer.Time;
        if (length > 0f && time >= 0f && time <= length)
        {
            ushort phase = (ushort)Math.Round((double)time / length * ushort.MaxValue);
            return new AnimationLayerWire(definition, name, weight, length, false, phase, 0f);
        }
        return new AnimationLayerWire(definition, name, weight, length, true, 0, WireText.Finite(time));
    }

    public AnimationLayer ToLayer(AnimationLibrary library) =>
        new(Definition >= 0 ? library.Definitions[Definition].Name : Name ?? "", TimeRaw ? Time : (float)(Phase / (double)ushort.MaxValue * Length), Weight)
        { Length = Length, Definition = Definition };
}

/// <summary>
/// The animation group's codec (docs/net.md, "Snapshot core"): a layer count byte (at most <see cref="NetLimits.MaxAnimLayers"/>), then per layer a flags
/// byte (b0 definition differs from the baseline's layer at the same index, b1 weight differs, b2 length differs, b3 raw time, b4 raw weight), the
/// fields those bits announce, then the time. A steady layer costs 3 bytes.
/// </summary>
public static class AnimationWire
{
    const byte DefinitionBit = 1, WeightBit = 2, LengthBit = 4, TimeRawBit = 8, WeightRawBit = 16, Known = 31;

    /// <summary>The quantized layers of a snapshot (the first <see cref="NetLimits.MaxAnimLayers"/>).</summary>
    public static AnimationLayerWire[] Quantize(IReadOnlyList<AnimationLayer> layers, AnimationLibrary library)
    {
        int n = Math.Min(layers.Count, NetLimits.MaxAnimLayers);
        if (n == 0) return [];
        var wire = new AnimationLayerWire[n];
        for (int i = 0; i < n; i++) wire[i] = AnimationLayerWire.Quantize(layers[i], library);
        return wire;
    }

    public static void Write(NetWriter w, StringTableWriter strings, AnimationLayerWire[] now, AnimationLayerWire[] baseline)
    {
        if (now.Length > NetLimits.MaxAnimLayers) throw new ArgumentException($"{now.Length} animation layers exceed the limit of {NetLimits.MaxAnimLayers}.", nameof(now));
        w.WriteByte((byte)now.Length);
        for (int i = 0; i < now.Length; i++)
        {
            ref readonly var layer = ref now[i];
            var b = i < baseline.Length ? baseline[i] : AnimationLayerWire.None;
            bool definition = layer.Definition != b.Definition || (layer.Definition < 0 && !string.Equals(layer.Name, b.Name, StringComparison.Ordinal));
            bool weight = layer.Weight != b.Weight, length = layer.Length != b.Length;
            bool weightRaw = weight && (layer.Weight < 0f || layer.Weight > 1f);
            w.WriteByte((byte)((definition ? DefinitionBit : 0) | (weight ? WeightBit : 0) | (length ? LengthBit : 0) | (layer.TimeRaw ? TimeRawBit : 0) | (weightRaw ? WeightRawBit : 0)));
            if (definition)
            {
                w.WriteVarUInt((uint)(layer.Definition + 1));
                if (layer.Definition < 0) strings.Write(w, layer.Name);
            }
            if (weight)
            {
                if (weightRaw) w.WriteSingle(layer.Weight);
                else w.WriteUInt16(Quant.U16(layer.Weight, 0f, 1f));
            }
            if (length) w.WriteSingle(layer.Length);
            if (layer.TimeRaw) w.WriteSingle(layer.Time);
            else w.WriteUInt16(layer.Phase);
        }
    }

    public static AnimationLayerWire[] Read(ref NetReader r, StringTableReader strings, AnimationLayerWire[] baseline, AnimationLibrary library)
    {
        int count = r.ReadCount(NetLimits.MaxAnimLayers);
        if (count == 0) return [];
        var layers = new AnimationLayerWire[count];
        for (int i = 0; i < count; i++)
        {
            byte flags = r.ReadByte();
            if ((flags & ~Known) != 0 || ((flags & WeightRawBit) != 0 && (flags & WeightBit) == 0)) throw new NetFormatException($"Bad animation layer flags {flags}.");
            var b = i < baseline.Length ? baseline[i] : AnimationLayerWire.None;
            int definition = b.Definition;
            string? name = b.Name;
            if ((flags & DefinitionBit) != 0)
            {
                uint v = r.ReadVarUInt32();
                if (v == 0)
                {
                    definition = -1;
                    name = WireText.ReadRequired(ref r, strings, NetLimits.MaxName);
                }
                else
                {
                    if (v - 1 >= (uint)library.Definitions.Count) throw new NetFormatException($"Animation definition {v - 1} is not in the library.");
                    definition = (int)(v - 1);
                    name = null;
                }
            }
            else if (definition < -1) throw new NetFormatException("An animation layer has no definition to inherit.");

            float weight = b.Weight;
            if ((flags & WeightBit) != 0) weight = (flags & WeightRawBit) != 0 ? r.ReadFinite() : Quant.FromU16(r.ReadUInt16(), 0f, 1f);
            float length = (flags & LengthBit) != 0 ? r.ReadFinite() : b.Length;
            if ((flags & TimeRawBit) != 0) layers[i] = new AnimationLayerWire(definition, name, weight, length, true, 0, r.ReadFinite());
            else
            {
                if (!(length > 0f)) throw new NetFormatException("A fractional animation time needs a positive length.");
                layers[i] = new AnimationLayerWire(definition, name, weight, length, false, r.ReadUInt16(), 0f);
            }
        }
        return layers;
    }
}
