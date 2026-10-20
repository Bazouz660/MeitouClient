using System.Text;

namespace Meitou.Net;

/// <summary>
/// The sending half of a string table: repeated strings (record ids, keys, names) cross the wire once and are then a one- or two-byte index
/// (docs/net.md, "String tables"). There is one table per channel and direction, kept for the whole connection, because reliable channels are
/// ordered only within themselves.
/// <para>
/// The wire value is a varint <c>h</c>: 0 is null, 1 a literal (<see cref="NetWriter.WriteUtf8"/>, not stored), an even <c>h</c> a reference to
/// index <c>h/2-1</c>, an odd <c>h &gt;= 3</c> a definition of index <c>(h-3)/2</c> followed by its UTF-8 text. A full table
/// (<see cref="NetLimits.MaxStringTable"/>) sends literals.
/// </para>
/// <para>
/// On a reliable channel (<paramref name="confirmOnSend"/>) an index is known to the reader once it was written, so later messages reference it. On the
/// snapshot channel an index counts as known only after <see cref="Confirm"/> (a part that defined it was acked): until then every part that uses it
/// defines it again, which the reader accepts as idempotent. Such a writer calls <see cref="BeginPart"/> before each part.
/// </para>
/// </summary>
public sealed class StringTableWriter(bool confirmOnSend)
{
    sealed class Entry(string text, int allocatedIn)
    {
        public readonly string Text = text;
        public readonly int AllocatedIn = allocatedIn;
        public bool Confirmed;
        public int DefinedIn;
    }

    readonly Dictionary<string, int> lookup = new(StringComparer.Ordinal);
    readonly List<Entry> entries = [];
    bool literalOnly;
    List<int> definitions = [];
    int part = 1;

    /// <summary>A writer that sends every string as a literal and keeps no state: the canonical form change detection compares (<see cref="KeyedViewSync{TKey, TItem}"/>).</summary>
    internal static StringTableWriter CreateLiteralOnly() => new(false) { literalOnly = true };

    /// <summary>Strings in the table.</summary>
    public int Count => entries.Count;

    /// <summary>
    /// The indices the current part defined (or redefined), in order. A snapshot-channel sender keeps it with the part and passes it to
    /// <see cref="Confirm"/> when the part is acked. <see cref="BeginPart"/> starts a new list; an earlier one stays valid.
    /// </summary>
    public IReadOnlyList<int> PartDefinitions => definitions;

    /// <param name="intern">False for text that rarely repeats (messages, chat, dialogue): it is written as a literal.</param>
    public void Write(NetWriter w, string? s, bool intern = true)
    {
        if (s is null)
        {
            w.WriteVarUInt(0);
            return;
        }
        if (!intern || literalOnly)
        {
            WriteLiteral(w, s);
            return;
        }
        if (!lookup.TryGetValue(s, out int index))
        {
            if (entries.Count >= NetLimits.MaxStringTable)
            {
                WriteLiteral(w, s);
                return;
            }
            index = entries.Count;
            entries.Add(new Entry(s, part));
            lookup.Add(s, index);
        }
        var entry = entries[index];
        if (entry.Confirmed || entry.DefinedIn == part)
        {
            w.WriteVarUInt((ulong)(index + 1) * 2);
            return;
        }
        w.WriteVarUInt((ulong)index * 2 + 3);
        w.WriteUtf8(s);
        entry.DefinedIn = part;
        definitions.Add(index);
        if (confirmOnSend) entry.Confirmed = true;
    }

    static void WriteLiteral(NetWriter w, string s)
    {
        w.WriteVarUInt(1);
        w.WriteUtf8(s);
    }

    /// <summary>Starts a new snapshot part: strings defined by earlier parts but not yet confirmed are defined again.</summary>
    public void BeginPart()
    {
        part++;
        definitions = [];
    }

    /// <summary>A rollback point: pair it with a <see cref="NetWriter.Length"/> taken at the same time.</summary>
    public int Mark() => definitions.Count;

    /// <summary>
    /// Forgets what was written since <paramref name="mark"/> (the caller truncates the writer to the length it took with the mark): the
    /// definitions are withdrawn, and indices first allocated since then are freed again, because the reader never saw them.
    /// </summary>
    public void Rollback(int mark)
    {
        if ((uint)mark > (uint)definitions.Count) throw new ArgumentOutOfRangeException(nameof(mark), mark, "Not a mark of this part.");
        for (int i = definitions.Count - 1; i >= mark; i--)
        {
            int index = definitions[i];
            var entry = entries[index];
            entry.DefinedIn = 0;
            if (entry.AllocatedIn != part) continue;
            // Allocated by this part after the mark, so it is the last entry.
            entries.RemoveAt(index);
            lookup.Remove(entry.Text);
        }
        definitions.RemoveRange(mark, definitions.Count - mark);
    }

    /// <summary>The reader is known to hold these indices (a part that defined them was acked): later parts reference them.</summary>
    public void Confirm(IEnumerable<int> indices)
    {
        foreach (int index in indices)
            if ((uint)index < (uint)entries.Count)
                entries[index].Confirmed = true;
    }
}

/// <summary>The receiving half of a string table (see <see cref="StringTableWriter"/> for the wire form). Indices may arrive in any order and more than once.</summary>
public sealed class StringTableReader
{
    string?[] table = new string?[64];

    /// <summary>Reads one string value. <paramref name="maxBytes"/> bounds literals, definitions and the strings a reference resolves to.</summary>
    public string? Read(ref NetReader r, int maxBytes)
    {
        uint h = r.ReadVarUInt32();
        switch (h)
        {
            case 0:
                return null;
            case 1:
                return r.ReadUtf8(maxBytes);
        }
        if ((h & 1) == 0)
        {
            uint index = h / 2 - 1;
            string? known = index < table.Length ? table[index] : null;
            if (known is null) throw new NetFormatException($"String {index} was never defined.");
            if (known.Length > maxBytes || (known.Length * 3L > maxBytes && Encoding.UTF8.GetByteCount(known) > maxBytes))
                throw new NetFormatException($"String {index} exceeds this field's limit of {maxBytes} bytes.");
            return known;
        }
        else
        {
            uint index = (h - 3) / 2;
            if (index >= NetLimits.MaxStringTable) throw new NetFormatException($"String index {index} is beyond the table.");
            string text = r.ReadUtf8(maxBytes);
            if (index >= table.Length) Array.Resize(ref table, (int)Math.Min(NetLimits.MaxStringTable, Math.Max(index + 1, table.Length * 2L)));
            if (table[index] is { } existing && !string.Equals(existing, text, StringComparison.Ordinal))
                throw new NetFormatException($"String {index} is defined twice with different text.");
            table[index] = text;
            return text;
        }
    }
}
