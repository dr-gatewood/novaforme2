using System.Diagnostics;
using System.Security.Cryptography;
using Nova4Me2.Core.Devices;
using Nova4Me2.Core.Ntfs;
using Nova4Me2.Core.Util;

namespace Nova4Me2.Core.Forensics;

/// <summary>Anything the carver can read: a device, a partition/volume window, an NTFS file stream, a local file.</summary>
public interface ICarveSource : IDisposable
{
    long Length { get; }
    string Name { get; }
    void Read(long offset, Span<byte> buffer);
}

public sealed class DeviceCarveSource(IBlockDevice dev, long offset = 0, long? length = null, string? name = null) : ICarveSource
{
    public long Length { get; } = length ?? dev.Length - offset;
    public string Name => name ?? dev.Description;
    public void Read(long off, Span<byte> buffer) => dev.ReadExact(offset + off, buffer);
    public void Dispose() { }
}

public sealed class StreamCarveSource(Stream stream, string name, bool ownsStream = true) : ICarveSource
{
    private readonly object _lock = new();
    public long Length => stream.Length;
    public string Name => name;
    public void Read(long off, Span<byte> buffer)
    {
        lock (_lock)
        {
            stream.Position = off;
            int t = 0;
            while (t < buffer.Length) { int n = stream.Read(buffer[t..]); if (n <= 0) throw new EndOfStreamException(); t += n; }
        }
    }
    public void Dispose() { if (ownsStream) stream.Dispose(); }
}

public sealed class CarveOptions
{
    /// <summary>Signature names to include (null = all).</summary>
    public HashSet<string>? Types { get; set; }
    /// <summary>Only accept headers at multiples of this (512 for disk / unallocated scans, 1 when looking inside a file).</summary>
    public int Alignment { get; set; } = 512;
    /// <summary>Report files that lie completely inside an earlier carved file (embedded files).</summary>
    public bool IncludeNested { get; set; } = false;
    public int ChunkSize { get; set; } = 8 * 1024 * 1024;
    public int MaxResults { get; set; } = 200000;
}

public sealed class CarvedFile
{
    public long Offset { get; init; }
    public long Length { get; init; }
    public FileSignature Signature { get; init; } = null!;
    public CarveMethod Method { get; init; }
    public bool Truncated { get; init; }
    public bool Nested { get; init; }
    public string? ExtractedPath { get; set; }
    public string? Sha256 { get; set; }
    public string SuggestedName => $"{Offset:X10}.{Signature.Extension}";
    public string Confidence => Method switch { CarveMethod.HeaderAndLength => "high", CarveMethod.HeaderAndFooter => "medium", _ => "low (size capped)" };
    public override string ToString() => $"{Signature.Name} @ {Offset} ({Format.Bytes(Length)}, {Confidence})";
}

public sealed class CarveProgress
{
    public long BytesTotal, BytesDone;
    public int Found;
    public string Phase = "";
    public double BytesPerSecond;
    public TimeSpan Elapsed;
    public double Fraction => BytesTotal > 0 ? Math.Min(1, (double)BytesDone / BytesTotal) : 0;
}

/// <summary>Signature-based file carving with exact-length parsing where the format allows it.</summary>
public static class FileCarver
{
    public static List<(long Offset, long Length)> WholeSource(ICarveSource s) => new() { (0, s.Length) };

    public static List<CarvedFile> Scan(ICarveSource src, IEnumerable<(long Offset, long Length)> regions, CarveOptions opt, IProgress<CarveProgress>? progress = null, CancellationToken ct = default)
    {
        var sigs = Signatures.All.Where(s => opt.Types == null || opt.Types.Contains(s.Name)).ToList();
        int maxSpan = sigs.Max(s => s.HeaderOffset + s.Header.Length);
        var regionList = regions.ToList();
        var p = new CarveProgress { BytesTotal = regionList.Sum(r => r.Length), Phase = "Scanning" };
        var results = new List<CarvedFile>();
        var seen = new HashSet<(long, string)>();
        var sw = Stopwatch.StartNew();
        long windowBytes = 0; var window = Stopwatch.StartNew();
        DateTime lastReport = DateTime.MinValue;
        var buf = new byte[opt.ChunkSize + maxSpan];
        byte[] Reader(long absOff, int count)
        {
            int n = (int)Math.Min(count, src.Length - absOff);
            if (n <= 0) return Array.Empty<byte>();
            var b = new byte[n];
            src.Read(absOff, b);
            return b;
        }
        foreach (var (rOff, rLen) in regionList)
        {
            long pos = rOff, end = rOff + rLen;
            int carry = 0;
            while (pos < end)
            {
                ct.ThrowIfCancellationRequested();
                int n = (int)Math.Min(opt.ChunkSize, end - pos);
                src.Read(pos, buf.AsSpan(carry, n));
                int total = carry + n;
                long bufStart = pos - carry;
                var span = buf.AsSpan(0, total);
                foreach (var sig in sigs)
                {
                    int from = 0;
                    while (from < total)
                    {
                        int idx = span[from..].IndexOf(sig.Header);
                        if (idx < 0) break;
                        int hit = from + idx;
                        from = hit + 1;
                        long fileStart = bufStart + hit - sig.HeaderOffset;
                        if (fileStart < rOff || fileStart >= end) continue;
                        if (opt.Alignment > 1 && fileStart % opt.Alignment != 0) continue;
                        if (hit - sig.HeaderOffset < carry - maxSpan && carry > 0 && pos != rOff) continue; // already examined in the previous chunk
                        if (!seen.Add((fileStart, sig.Name))) continue;
                        var cf = Measure(src, sig, fileStart, Math.Min(end, src.Length), Reader);
                        if (cf == null) continue;
                        bool nested = results.Any(r => r.Offset <= cf.Offset && r.Offset + r.Length >= cf.Offset + cf.Length && r != cf);
                        if (nested && !opt.IncludeNested) continue;
                        results.Add(new CarvedFile { Offset = cf.Offset, Length = cf.Length, Signature = cf.Signature, Method = cf.Method, Truncated = cf.Truncated, Nested = nested });
                        p.Found = results.Count;
                        if (results.Count >= opt.MaxResults) { p.Phase = "Stopped: result limit"; progress?.Report(p); return results; }
                    }
                }
                // keep the tail so headers straddling the chunk boundary are found next round
                carry = Math.Min(maxSpan, total);
                Array.Copy(buf, total - carry, buf, 0, carry);
                pos += n;
                p.BytesDone += n;
                windowBytes += n;
                if (window.ElapsedMilliseconds > 1000) { p.BytesPerSecond = windowBytes / window.Elapsed.TotalSeconds; window.Restart(); windowBytes = 0; }
                if ((DateTime.UtcNow - lastReport).TotalMilliseconds > 200) { p.Elapsed = sw.Elapsed; progress?.Report(p); lastReport = DateTime.UtcNow; }
            }
        }
        p.Phase = "Complete"; p.Elapsed = sw.Elapsed;
        progress?.Report(p);
        return results.OrderBy(r => r.Offset).ToList();
    }

    /// <summary>Validate a candidate at <paramref name="start"/> and determine its length. Public so the stego analyser can measure a file's logical end.</summary>
    public static CarvedFile? Measure(ICarveSource src, FileSignature sig, long start, long limit, Func<long, int, byte[]>? reader = null)
    {
        reader ??= (o, c) => { int n = (int)Math.Min(c, src.Length - o); var b = new byte[Math.Max(0, n)]; if (n > 0) src.Read(o, b); return b; };
        long avail = limit - start;
        if (avail <= sig.HeaderOffset + sig.Header.Length) return null;
        int probe = (int)Math.Min(sig.ProbeBytes, avail);
        var head = reader(start, probe);
        if (head.Length < sig.HeaderOffset + sig.Header.Length) return null;
        if (!head.AsSpan(sig.HeaderOffset, sig.Header.Length).SequenceEqual(sig.Header)) return null;
        if (sig.Validate != null && !sig.Validate(head)) return null;
        long cap = Math.Min(sig.MaxLength, avail);
        if (sig.LengthParser != null)
        {
            long? len = null;
            try { len = sig.LengthParser(head, (o, c) => reader(start + o, (int)Math.Min(c, avail))); } catch { }
            if (len is { } l && l > sig.HeaderOffset + sig.Header.Length && l <= cap) return new CarvedFile { Offset = start, Length = l, Signature = sig, Method = CarveMethod.HeaderAndLength };
            if (len is { } l2 && l2 > cap) return new CarvedFile { Offset = start, Length = cap, Signature = sig, Method = CarveMethod.HeaderAndLength, Truncated = true };
        }
        if (sig.Footer != null)
        {
            long searchFrom = sig.Name.StartsWith("JPEG") ? JpegScanStart(head) : sig.Header.Length;
            long found = FindFooter(reader, start, searchFrom, cap, sig.Footer, sig.Name.StartsWith("JPEG"));
            if (found >= 0)
            {
                long len = found + sig.Footer.Length;
                if (sig.Name.StartsWith("PDF")) len = Math.Min(cap, len + TrailingNewlines(reader(start + len, 4)));
                return new CarvedFile { Offset = start, Length = len, Signature = sig, Method = CarveMethod.HeaderAndFooter };
            }
        }
        long capped = Math.Min(cap, Math.Min(sig.MaxLength, 16L << 20));
        return new CarvedFile { Offset = start, Length = capped, Signature = sig, Method = CarveMethod.HeaderAndCap, Truncated = true };
    }

    private static int TrailingNewlines(byte[] b) { int n = 0; while (n < b.Length && (b[n] == '\r' || b[n] == '\n')) n++; return n; }

    /// <summary>Offset of the Start-Of-Scan segment: FFD9 markers before it belong to embedded thumbnails.</summary>
    private static long JpegScanStart(byte[] b)
    {
        int i = 2;
        while (i + 4 <= b.Length)
        {
            if (b[i] != 0xFF) break;
            byte m = b[i + 1];
            if (m == 0xDA) return i;
            if (m == 0xD8 || (m >= 0xD0 && m <= 0xD7) || m == 0x01 || m == 0xFF) { i += (m == 0xFF ? 1 : 2); continue; }
            int len = (b[i + 2] << 8) | b[i + 3];
            if (len < 2) break;
            i += 2 + len;
        }
        return 2;
    }

    private static long FindFooter(Func<long, int, byte[]> reader, long start, long from, long cap, byte[] footer, bool jpeg)
    {
        const int win = 4 * 1024 * 1024;
        long pos = from;
        while (pos < cap)
        {
            int n = (int)Math.Min(win + footer.Length, cap - pos);
            var b = reader(start + pos, n);
            if (b.Length == 0) return -1;
            int idx = 0;
            while (true)
            {
                int hit = b.AsSpan(idx).IndexOf(footer);
                if (hit < 0) break;
                long abs = pos + idx + hit;
                if (jpeg)
                {
                    // Accept the EOI only if what follows is not more JPEG entropy data / a restart marker (guards against FFD9 inside scan data of a multi-part file).
                    var after = reader(start + abs + 2, 2);
                    if (after.Length == 2 && after[0] == 0xFF && after[1] >= 0xD0 && after[1] <= 0xD7) { idx += hit + 1; continue; }
                }
                return abs;
            }
            if (b.Length < n) return -1;
            pos += win;
        }
        return -1;
    }

    /// <summary>Write a carved file to disk and return its path (sets Sha256 and ExtractedPath).</summary>
    public static string Extract(ICarveSource src, CarvedFile cf, string destDir, string? fileName = null)
    {
        Directory.CreateDirectory(destDir);
        string path = ForensicProject.UniquePath(destDir, fileName ?? cf.SuggestedName);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buf = new byte[1 << 20];
        long remaining = cf.Length, pos = cf.Offset;
        while (remaining > 0)
        {
            int n = (int)Math.Min(buf.Length, remaining);
            src.Read(pos, buf.AsSpan(0, n));
            fs.Write(buf, 0, n);
            sha.AppendData(buf, 0, n);
            pos += n; remaining -= n;
        }
        cf.Sha256 = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
        cf.ExtractedPath = path;
        return path;
    }

    /// <summary>Signature match at the very start of a buffer (used to label ADS / payload contents).</summary>
    public static FileSignature? Identify(ReadOnlySpan<byte> head)
    {
        foreach (var s in Signatures.All)
        {
            if (head.Length < s.HeaderOffset + s.Header.Length) continue;
            if (!head.Slice(s.HeaderOffset, s.Header.Length).SequenceEqual(s.Header)) continue;
            if (s.Validate != null && !s.Validate(head)) continue;
            return s;
        }
        return null;
    }

    public static string DescribeContent(ReadOnlySpan<byte> head)
    {
        var sig = Identify(head);
        if (sig != null) return sig.Name;
        if (head.Length == 0) return "empty";
        int printable = 0;
        foreach (var b in head) if (b == 9 || b == 10 || b == 13 || (b >= 32 && b < 127)) printable++;
        if (printable > head.Length * 0.95) return "text";
        if (head.Length >= 2 && head[0] == 0xFF && head[1] == 0xFE) return "UTF-16 text";
        double e = Entropy.Shannon(head);
        return e > 7.5 ? "binary (high entropy: compressed/encrypted?)" : "binary";
    }
}
