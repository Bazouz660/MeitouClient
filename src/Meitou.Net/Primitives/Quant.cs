using System.Numerics;

namespace Meitou.Net;

/// <summary>
/// Fixed-point forms of the simulation's floats (docs/net.md, "Quantization"). They are pure functions: the host compares <em>quantized</em> values, so
/// a change below one step is never sent, and the client draws exactly what it decodes. Every encoder maps NaN to the zero value and clamps, so a
/// bad float cannot throw or overflow.
/// </summary>
public static class Quant
{
    /// <summary>Steps per world unit of a position: 1/64 is a float32's step at the edge of the world (+-<c>WorldLayout.HalfWorldSize</c>).</summary>
    public const float PositionSteps = 64f;

    const int PositionLimit = 1 << 30;
    const float Sqrt2 = 1.41421356f;
    const float InvSqrt2 = 0.70710678f;

    /// <summary>A world coordinate in 1/64 units, clamped to +-2^30.</summary>
    public static int Q64(float v) => float.IsNaN(v) ? 0 : (int)Math.Clamp(Math.Round(v * (double)PositionSteps), -PositionLimit, PositionLimit);

    public static float FromQ64(int q) => q / PositionSteps;

    /// <summary>A yaw in radians as <c>round(yaw / pi * 32768)</c> wrapped to 16 bits, so it decodes into [-pi, pi).</summary>
    public static short Yaw16(float yaw) => float.IsFinite(yaw) ? unchecked((short)(long)Math.Round(yaw * (32768.0 / Math.PI))) : (short)0;

    public static float FromYaw16(short q) => (float)(q * (Math.PI / 32768.0));

    /// <summary>A value in [<paramref name="min"/>, <paramref name="max"/>] as a 16-bit step count, clamped.</summary>
    public static ushort U16(float v, float min, float max)
    {
        double t = ((double)v - min) / ((double)max - min);
        return float.IsNaN(v) ? (ushort)0 : (ushort)Math.Round(Math.Clamp(t, 0.0, 1.0) * ushort.MaxValue);
    }

    public static float FromU16(ushort q, float min, float max) => (float)(min + (double)q / ushort.MaxValue * ((double)max - min));

    /// <summary>A signed value in [-8, 8) as <c>round(v * 4096)</c>, clamped to 16 bits.</summary>
    public static short S12(float v) => float.IsNaN(v) ? (short)0 : (short)Math.Clamp(Math.Round(v * 4096.0), short.MinValue, short.MaxValue);

    public static float FromS12(short q) => q / 4096f;

    /// <summary>
    /// A rotation in 32 bits ("smallest three"): normalised, the largest component made positive and dropped (its index takes 2 bits, bits 30-31),
    /// the other three, in index order, as signed 10-bit steps of 1/(511*sqrt 2) (bits 20-29, 10-19, 0-9). The angle error is at most about
    /// 0.004 rad (0.23 degrees; 0.002 for a turn about one axis), and the identity encodes exactly.
    /// </summary>
    public static uint SmallestThree(Quaternion q)
    {
        Span<float> c = [q.X, q.Y, q.Z, q.W];
        float length = MathF.Sqrt(c[0] * c[0] + c[1] * c[1] + c[2] * c[2] + c[3] * c[3]);
        if (!float.IsFinite(length) || length < 1e-12f) c = [0f, 0f, 0f, 1f];
        else for (int i = 0; i < 4; i++) c[i] /= length;

        int largest = 0;
        for (int i = 1; i < 4; i++)
            if (MathF.Abs(c[i]) > MathF.Abs(c[largest])) largest = i;
        float sign = c[largest] < 0 ? -1f : 1f;

        uint packed = (uint)largest << 30;
        int shift = 20;
        for (int i = 0; i < 4; i++)
        {
            if (i == largest) continue;
            int step = (int)Math.Clamp(MathF.Round(c[i] * sign * Sqrt2 * 511f), -511, 511);
            packed |= ((uint)step & 0x3FF) << shift;
            shift -= 10;
        }
        return packed;
    }

    public static Quaternion FromSmallestThree(uint packed)
    {
        int largest = (int)(packed >> 30);
        Span<float> c = stackalloc float[4];
        float sum = 0;
        int shift = 20;
        for (int i = 0; i < 4; i++)
        {
            if (i == largest) continue;
            int step = (int)((packed >> shift) & 0x3FF);
            if (step >= 512) step -= 1024;
            c[i] = step * (InvSqrt2 / 511f);
            sum += c[i] * c[i];
            shift -= 10;
        }
        c[largest] = MathF.Sqrt(MathF.Max(0f, 1f - sum));
        return new Quaternion(c[0], c[1], c[2], c[3]);
    }

    /// <summary>One component of a unit direction as <c>round(c * 32767)</c>, clamped.</summary>
    public static short Dir16(float c) => float.IsNaN(c) ? (short)0 : (short)Math.Clamp(Math.Round(c * 32767.0), -32767, 32767);

    /// <summary>A unit direction from its three 16-bit components, renormalised (a zero vector stays zero).</summary>
    public static Vector3 FromDirection(short x, short y, short z)
    {
        var v = new Vector3(x, y, z);
        float length = v.Length();
        return length > 0f ? v / length : Vector3.Zero;
    }
}
