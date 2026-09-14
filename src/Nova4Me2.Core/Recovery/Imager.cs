using System.Diagnostics;
using System.Security.Cryptography;
using Nova4Me2.Core.Devices;
using Nova4Me2.Core.Util;

namespace Nova4Me2.Core.Recovery;

public sealed class ImageOptions
{
    /// <summary>Byte range of the source to copy (Length 0 = to the end).</summary>
    public long StartOffset { get; set; }
    public long Length { get; set; }
    /// <summary>Where in the target the data lands (0 for image files; a partition offset when cloning a partition into a disk).</summary>
    public long TargetOffset { get; set; }
    public int ChunkSize { get; set; } = 4 * 1024 * 1024;
    /// <summary>First pass skips whole chunks that fail (fast); the second pass retries them sector by sector.</summary>
    public bool TwoPass { get; set; } = true;
    public bool ComputeSha256 { get; set; } = true;
    public bool ComputeMd5 { get; set; }
    /// <summary>Verify the target after imaging by hashing it and comparing.</summary>
    public bool VerifyTarget { get; set; }
    /// <summary>Resume from a previous run's progress file if present.</summary>
    public bool Resume { get; set; }
    /// <summary>Path prefix for the progress/bad-sector log files (default: target path for files).</summary>
    public string? LogPrefix { get; set; }
    public int MapCells { get; set; } = 1024;
}

public sealed class ImageProgress
{
    public string Phase = "";
    public int Pass;
    public long BytesTotal, BytesDone, BytesBad, BytesSkipped;
    public long BadSectorCount;
    public double BytesPerSecond;
    public TimeSpan Elapsed;
    public long CurrentOffset;
    public BlockMap? Map;
    public string? Sha256, Md5, TargetSha256;
    public bool VerifyOk;
    public List<(long Offset, long Length)> BadRanges = new();
    public List<string> Messages = new();
    public double Fraction => BytesTotal > 0 ? Math.Min(1, (double)BytesDone / BytesTotal) : 0;
    public TimeSpan? Eta => BytesPerSecond > 1 && BytesTotal > BytesDone ? TimeSpan.FromSeconds((BytesTotal - BytesDone) / BytesPerSecond) : null;
    public ImageProgress Snapshot() { var c = (ImageProgress)MemberwiseClone(); c.BadRanges = new(BadRanges); c.Messages = new(Messages); c.Map = Map?.Clone(); return c; }
}

/// <summary>
/// Sector-level copy (disk → image file, disk → disk, partition → file/disk). Forensic style: never writes to the source,
/// hashes what it reads, records unreadable ranges, supports resume and a two-pass ddrescue-like strategy.
/// </summary>
public static class Imager
{
    public static ImageProgress Run(ResilientBlockDevice src, IBlockDevice dst, ImageOptions opt, IProgress<ImageProgress>? progress = null, CancellationToken ct = default)
    {
        long start = opt.StartOffset;
        long length = opt.Length > 0 ? opt.Length : src.Length - start;
        if (start < 0 || start + length > src.Length) throw new ArgumentOutOfRangeException(nameof(opt), "Range exceeds the source device.");
        if (!dst.CanWrite) throw new InvalidOperationException("Target is not writable.");
        if (opt.TargetOffset + length > dst.Length) throw new InvalidOperationException($"Target is too small: needs {Format.Bytes(opt.TargetOffset + length)}, has {Format.Bytes(dst.Length)}.");
        int ss = src.SectorSize;
        int chunk = (int)Bin.AlignUp(Math.Max(ss, opt.ChunkSize), ss);
        var p = new ImageProgress { BytesTotal = length, Map = new BlockMap(start, length, opt.MapCells), Phase = "Pass 1: copying", Pass = 1 };
        string logPrefix = opt.LogPrefix ?? (dst is FileBlockDevice f ? f.Path : Path.Combine(Path.GetTempPath(), "nova4me2-clone"));
        string progressFile = logPrefix + ".progress", badFile = logPrefix + ".badsectors.txt";
        var sw = Stopwatch.StartNew();
        var sha = opt.ComputeSha256 ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
        var md5 = opt.ComputeMd5 ? IncrementalHash.CreateHash(HashAlgorithmName.MD5) : null;
        long resumeFrom = 0;
        if (opt.Resume && File.Exists(progressFile) && long.TryParse(File.ReadAllText(progressFile).Trim(), out long saved) && saved > 0 && saved <= length)
        {
            resumeFrom = Bin.AlignDown(saved, chunk);
            p.Messages.Add($"Resuming at {Format.Bytes(resumeFrom)} (hashes will only cover the resumed part).");
            p.BytesDone = resumeFrom;
            p.Map!.Mark(start, resumeFrom, BlockState.Good);
        }
        var buf = new byte[chunk];
        var skipped = new List<(long Offset, int Length)>();
        var origSubdivide = src.Options.SubdivideOnError;
        var origZero = src.Options.ZeroFillBadSectors;
        long windowBytes = 0;
        var window = Stopwatch.StartNew();
        DateTime lastReport = DateTime.MinValue, lastSave = DateTime.MinValue;
        void Report(bool force = false)
        {
            if (!force && (DateTime.UtcNow - lastReport).TotalMilliseconds < 200) return;
            lastReport = DateTime.UtcNow;
            p.Elapsed = sw.Elapsed;
            if (window.ElapsedMilliseconds > 1500) { p.BytesPerSecond = windowBytes / window.Elapsed.TotalSeconds; window.Restart(); windowBytes = 0; }
            progress?.Report(p.Snapshot());
        }
        try
        {
            // ---- pass 1 ----
            src.Options.SubdivideOnError = !opt.TwoPass;
            src.Options.ZeroFillBadSectors = !opt.TwoPass; // single pass: zero-fill at sector level
            long badBefore = src.Stats.BadSectors;
            var badHandler = new EventHandler<BadSectorEventArgs>((_, e) => { lock (p) { p.BadRanges.Add((e.Offset, e.Length)); p.BadSectorCount++; p.BytesBad += e.Length; p.Map!.Mark(e.Offset, e.Length, BlockState.Bad); } });
            src.BadSector += badHandler;
            try
            {
                for (long off = resumeFrom; off < length; off += chunk)
                {
                    ct.ThrowIfCancellationRequested();
                    int n = (int)Math.Min(chunk, length - off);
                    long abs = start + off;
                    p.CurrentOffset = abs;
                    p.Map!.Mark(abs, n, BlockState.Reading);
                    var t0 = Stopwatch.GetTimestamp();
                    bool ok = true;
                    try { src.ReadExact(abs, buf.AsSpan(0, n)); }
                    catch (BadSectorException) when (opt.TwoPass)
                    {
                        ok = false;
                        Array.Clear(buf, 0, n);
                        skipped.Add((abs, n));
                        p.BytesSkipped += n;
                        p.Map.Mark(abs, n, BlockState.Skipped);
                    }
                    double secs = Stopwatch.GetElapsedTime(t0).TotalSeconds;
                    dst.WriteExact(opt.TargetOffset + off, buf.AsSpan(0, n));
                    sha?.AppendData(buf, 0, n);
                    md5?.AppendData(buf, 0, n);
                    if (ok) p.Map.Mark(abs, n, secs > 2.0 ? BlockState.Slow : BlockState.Good);
                    p.BytesDone = off + n;
                    windowBytes += n;
                    if ((DateTime.UtcNow - lastSave).TotalSeconds > 5) { TrySave(progressFile, p.BytesDone); lastSave = DateTime.UtcNow; }
                    Report();
                }
            }
            finally { src.BadSector -= badHandler; }
            // ---- pass 2: retry skipped chunks sector by sector ----
            if (opt.TwoPass && skipped.Count > 0)
            {
                p.Phase = $"Pass 2: retrying {skipped.Count} unreadable chunks sector by sector";
                p.Pass = 2;
                Report(true);
                src.Options.SubdivideOnError = true;
                src.Options.ZeroFillBadSectors = true;
                src.BadSector += badHandler;
                try
                {
                    foreach (var (abs, n) in skipped)
                    {
                        ct.ThrowIfCancellationRequested();
                        p.CurrentOffset = abs;
                        src.ReadExact(abs, buf.AsSpan(0, n));
                        dst.WriteExact(opt.TargetOffset + (abs - start), buf.AsSpan(0, n));
                        p.BytesSkipped -= n;
                        p.Map!.Mark(abs, n, BlockState.Good); // Bad sectors inside were marked Bad by the handler and outrank Good.
                        Report();
                    }
                }
                finally { src.BadSector -= badHandler; }
                p.Messages.Add("Pass 2 recovered the readable sectors inside the skipped chunks; hashes above were computed on pass 1 data and are not valid for those chunks.");
                sha = null; md5 = null;
            }
            dst.Flush();
            if (sha != null) p.Sha256 = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
            if (md5 != null) p.Md5 = Convert.ToHexString(md5.GetHashAndReset()).ToLowerInvariant();
            if (p.BadRanges.Count > 0) WriteBadSectorLog(badFile, src, p);
            if (opt.VerifyTarget)
            {
                p.Phase = "Verifying target";
                Report(true);
                using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                for (long off = 0; off < length; off += chunk)
                {
                    ct.ThrowIfCancellationRequested();
                    int n = (int)Math.Min(chunk, length - off);
                    dst.ReadExact(opt.TargetOffset + off, buf.AsSpan(0, n));
                    h.AppendData(buf, 0, n);
                    p.CurrentOffset = start + off;
                    Report();
                }
                p.TargetSha256 = Convert.ToHexString(h.GetHashAndReset()).ToLowerInvariant();
                p.VerifyOk = p.Sha256 == null || p.TargetSha256 == p.Sha256;
                if (p.Sha256 == null) p.Messages.Add("Target hashed; source hash unavailable for comparison (two-pass recovery ran).");
            }
            p.Phase = "Complete";
            TryDelete(progressFile);
            Report(true);
            return p;
        }
        catch (OperationCanceledException)
        {
            TrySave(progressFile, p.BytesDone);
            p.Phase = "Cancelled (resumable)";
            Report(true);
            throw;
        }
        finally
        {
            src.Options.SubdivideOnError = origSubdivide;
            src.Options.ZeroFillBadSectors = origZero;
        }
    }

    private static void WriteBadSectorLog(string path, IBlockDevice src, ImageProgress p)
    {
        try
        {
            using var w = new StreamWriter(path);
            w.WriteLine($"# Nova4Me2 unreadable sector log — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            w.WriteLine($"# Source: {src.Description}");
            w.WriteLine("# byte_offset\tlength\tLBA\tsectors");
            foreach (var (o, l) in p.BadRanges.OrderBy(r => r.Offset)) w.WriteLine($"{o}\t{l}\t{o / src.SectorSize}\t{(l + src.SectorSize - 1) / src.SectorSize}");
            p.Messages.Add($"Unreadable sector list written to {path}");
        }
        catch (Exception ex) { p.Messages.Add("Could not write bad sector log: " + ex.Message); }
    }

    private static void TrySave(string path, long value) { try { File.WriteAllText(path, value.ToString()); } catch { } }
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
}
