namespace Meitou.Rendering;

/// <summary>
/// A height map recovered from a tiling normal map (docs/render-terrain.md, "Sand parallax"): the normal's slopes integrated in the frequency domain
/// (Frankot–Chellappa), exact for a periodic texture up to a constant. The slopes follow the terrain shader's tangent frame (tangent along world -X,
/// binormal along +Z, texture u along +X and v along +Z): dh/du = x / z, dh/dv = -y / z. The lowest frequencies (under two cycles a tile) are dropped,
/// which keeps compression noise from integrating into large bumps. The result is scaled to 0..255, 255 the top.
/// </summary>
public static class NormalHeight
{
    /// <summary>An R8 height (rows top first) from an RGBA8 normal map of <paramref name="size"/>² texels (a power of two).</summary>
    public static byte[] FromNormals(byte[] rgba, int size)
    {
        int count = size * size;
        float[] pRe = new float[count], pIm = new float[count], qRe = new float[count], qIm = new float[count];
        for (int i = 0; i < count; i++)
        {
            float x = rgba[i * 4] / 127.5f - 1, y = rgba[i * 4 + 1] / 127.5f - 1;
            float z = MathF.Sqrt(MathF.Max(1 - x * x - y * y, 0.04f));
            pRe[i] = Math.Clamp(x / z, -5f, 5f);
            qRe[i] = Math.Clamp(-y / z, -5f, 5f);
        }
        Fft2(pRe, pIm, size, false);
        Fft2(qRe, qIm, size, false);
        // H = (-i kx P - i ky Q) / (kx² + ky²); -i (a + ib) = b - ia.
        for (int row = 0; row < size; row++)
        {
            int fy = row <= size / 2 ? row : row - size;
            for (int col = 0; col < size; col++)
            {
                int fx = col <= size / 2 ? col : col - size, i = row * size + col;
                if (fx * fx + fy * fy < 4) { pRe[i] = pIm[i] = 0; continue; }
                float kx = 2 * MathF.PI * fx / size, ky = 2 * MathF.PI * fy / size, k2 = kx * kx + ky * ky;
                float re = (kx * pIm[i] + ky * qIm[i]) / k2, im = -(kx * pRe[i] + ky * qRe[i]) / k2;
                (pRe[i], pIm[i]) = (re, im);
            }
        }
        Fft2(pRe, pIm, size, true);
        // Scaled by the 1st and 99th percentiles, so a few deep pits do not flatten the rest.
        var sorted = (float[])pRe.Clone();
        Array.Sort(sorted);
        float lo = sorted[count / 100], hi = sorted[count - 1 - count / 100], scale = hi > lo ? 255 / (hi - lo) : 0;
        var height = new byte[count];
        for (int i = 0; i < count; i++) height[i] = (byte)Math.Clamp((pRe[i] - lo) * scale, 0, 255);
        return height;
    }

    /// <summary>In-place 2D FFT of a square power-of-two grid: rows, then columns (the inverse scaled by 1 / n²).</summary>
    static void Fft2(float[] re, float[] im, int n, bool inverse)
    {
        Parallel.For(0, n, row => Fft(re.AsSpan(row * n, n), im.AsSpan(row * n, n), inverse));
        Parallel.For(0, n, () => (new float[n], new float[n]), (col, _, buffers) =>
        {
            var (r, i) = buffers;
            for (int k = 0; k < n; k++) (r[k], i[k]) = (re[k * n + col], im[k * n + col]);
            Fft(r, i, inverse);
            for (int k = 0; k < n; k++) (re[k * n + col], im[k * n + col]) = (r[k], i[k]);
            return buffers;
        }, _ => { });
        if (!inverse) return;
        float s = 1f / ((float)n * n);
        for (int k = 0; k < re.Length; k++) { re[k] *= s; im[k] *= s; }
    }

    /// <summary>Iterative radix-2 FFT (unscaled).</summary>
    static void Fft(Span<float> re, Span<float> im, bool inverse)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double angle = (inverse ? 2 : -2) * Math.PI / len;
            float wRe = (float)Math.Cos(angle), wIm = (float)Math.Sin(angle);
            for (int i = 0; i < n; i += len)
            {
                float cRe = 1, cIm = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = a + len / 2;
                    float tRe = re[b] * cRe - im[b] * cIm, tIm = re[b] * cIm + im[b] * cRe;
                    (re[b], im[b]) = (re[a] - tRe, im[a] - tIm);
                    re[a] += tRe; im[a] += tIm;
                    (cRe, cIm) = (cRe * wRe - cIm * wIm, cRe * wIm + cIm * wRe);
                }
            }
        }
    }
}
