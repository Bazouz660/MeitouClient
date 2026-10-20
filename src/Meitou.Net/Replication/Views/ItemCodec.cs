namespace Meitou.Net.Replication;

/// <summary>Writes one view item; strings go through <paramref name="strings"/>.</summary>
public delegate void ItemWriter<T>(NetWriter w, StringTableWriter strings, T item);

/// <summary>Reads one view item written by the matching <see cref="ItemWriter{T}"/>; every failure is a <see cref="NetFormatException"/>.</summary>
public delegate T ItemReader<T>(ref NetReader r, StringTableReader strings);

/// <summary>The two halves of an item's wire form. <see cref="KeyedViewSync{TKey, TItem}"/> and <see cref="KeyedViewDecoder{TItem}"/> take one.</summary>
public readonly record struct ItemCodec<T>(ItemWriter<T> Write, ItemReader<T> Read);
