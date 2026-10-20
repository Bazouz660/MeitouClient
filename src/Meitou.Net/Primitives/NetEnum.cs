using System.Collections.Frozen;
using System.Runtime.CompilerServices;

namespace Meitou.Net;

/// <summary>How an enum travels, chosen by its underlying type (docs/net.md, "Enums").</summary>
internal enum EnumWire : byte
{
    /// <summary><see cref="byte"/>-backed: one raw byte.</summary>
    Byte,

    /// <summary><see cref="sbyte"/>-backed: one raw byte, sign-extended on read.</summary>
    SByte,

    /// <summary><see cref="ushort"/>, <see cref="uint"/> or <see cref="ulong"/>-backed: an unsigned varint.</summary>
    Unsigned,

    /// <summary><see cref="short"/>, <see cref="int"/> or <see cref="long"/>-backed: a zigzag varint.</summary>
    Signed,
}

/// <summary>
/// The wire form and the set of valid values of one enum type, computed once. A value is valid when it is a defined member or, for a
/// <see cref="FlagsAttribute"/> enum, when it uses only bits some member uses. Values are handled as 64-bit patterns: unsigned zero-extended,
/// signed sign-extended.
/// </summary>
internal static class NetEnum<T> where T : struct, Enum
{
    public static readonly EnumWire Wire;
    public static readonly bool IsFlags;
    static readonly ulong mask;
    static readonly FrozenSet<ulong> defined;

    static NetEnum()
    {
        var underlying = Enum.GetUnderlyingType(typeof(T));
        Wire = underlying == typeof(byte) ? EnumWire.Byte
            : underlying == typeof(sbyte) ? EnumWire.SByte
            : underlying == typeof(ushort) || underlying == typeof(uint) || underlying == typeof(ulong) ? EnumWire.Unsigned
            : EnumWire.Signed;
        IsFlags = typeof(T).IsDefined(typeof(FlagsAttribute), false);
        var values = new HashSet<ulong>();
        foreach (var value in Enum.GetValues<T>())
        {
            ulong bits = ToBits(value);
            values.Add(bits);
            mask |= bits;
        }
        defined = values.ToFrozenSet();
    }

    public static bool IsValid(ulong bits) => IsFlags ? (bits & ~mask) == 0 : defined.Contains(bits);

    public static ulong ToBits(T value)
    {
        bool signed = Wire is EnumWire.SByte or EnumWire.Signed;
        return Unsafe.SizeOf<T>() switch
        {
            1 => signed ? (ulong)(long)Unsafe.BitCast<T, sbyte>(value) : Unsafe.BitCast<T, byte>(value),
            2 => signed ? (ulong)(long)Unsafe.BitCast<T, short>(value) : Unsafe.BitCast<T, ushort>(value),
            4 => signed ? (ulong)(long)Unsafe.BitCast<T, int>(value) : Unsafe.BitCast<T, uint>(value),
            _ => Unsafe.BitCast<T, ulong>(value),
        };
    }

    public static T FromBits(ulong bits) => Unsafe.SizeOf<T>() switch
    {
        1 => Unsafe.BitCast<byte, T>((byte)bits),
        2 => Unsafe.BitCast<ushort, T>((ushort)bits),
        4 => Unsafe.BitCast<uint, T>((uint)bits),
        _ => Unsafe.BitCast<ulong, T>(bits),
    };
}
