namespace Nova4Me2.Core.Ntfs;

/// <summary>LZNT1 decompressor (NTFS "compressed" attribute, 4 KiB chunks inside a compression unit).</summary>
public static class Lznt1
{
    public const int ChunkSize = 4096;

    /// <summary>Decompress <paramref name="src"/> into <paramref name="dst"/>; returns the number of bytes produced. Never throws on malformed input; stops early instead.</summary>
    public static int Decompress(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        int si = 0, di = 0;
        while (si + 2 <= src.Length && di < dst.Length)
        {
            int hdr = src[si] | (src[si + 1] << 8);
            si += 2;
            if (hdr == 0) break;
            int size = (hdr & 0xFFF) + 1;
            bool compressed = (hdr & 0x8000) != 0;
            int chunkEnd = Math.Min(si + size, src.Length);
            int outStart = di;
            int outEnd = Math.Min(di + ChunkSize, dst.Length);
            if (!compressed)
            {
                int n = Math.Min(chunkEnd - si, outEnd - di);
                src.Slice(si, n).CopyTo(dst.Slice(di, n));
                di += n;
            }
            else
            {
                while (si < chunkEnd && di < outEnd)
                {
                    byte flags = src[si++];
                    for (int bit = 0; bit < 8 && si < chunkEnd && di < outEnd; bit++)
                    {
                        if ((flags & (1 << bit)) == 0)
                        {
                            dst[di++] = src[si++];
                            continue;
                        }
                        if (si + 2 > chunkEnd) { si = chunkEnd; break; }
                        int v = src[si] | (src[si + 1] << 8);
                        si += 2;
                        int p = di - outStart;
                        int lg = 0;
                        for (int u = p - 1; u >= 0x10; u >>= 1) lg++;
                        int lmask = 0xFFF >> lg;
                        int dshift = 12 - lg;
                        int len = (v & lmask) + 3;
                        int disp = (v >> dshift) + 1;
                        if (disp > p) return di; // corrupt: back-reference before chunk start
                        for (int k = 0; k < len && di < outEnd; k++) { dst[di] = dst[di - disp]; di++; }
                    }
                }
            }
            si = chunkEnd;
            // Every chunk but the last expands to exactly ChunkSize; pad short chunks so the next one lands on its boundary.
            if (si < src.Length && di < outStart + ChunkSize && outStart + ChunkSize <= dst.Length)
            {
                dst.Slice(di, outStart + ChunkSize - di).Clear();
                di = outStart + ChunkSize;
            }
        }
        return di;
    }
}
