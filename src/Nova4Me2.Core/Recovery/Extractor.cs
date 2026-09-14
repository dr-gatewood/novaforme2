using System.Diagnostics;
using Nova4Me2.Core.Devices;
using Nova4Me2.Core.Ntfs;
using Nova4Me2.Core.Util;

namespace Nova4Me2.Core.Recovery;

public enum CollisionPolicy { Skip, Overwrite, Rename }

public sealed class CopyOptions
{
    public CollisionPolicy Collision { get; set; } = CollisionPolicy.Skip;
    public bool PreserveTimestamps { get; set; } = true;
    public bool PreserveAttributes { get; set; } = true;
    public bool CopyAlternateStreams { get; set; }
    public bool ContinueOnError { get; set; } = true;
    /// <summary>When a sector inside a file is unreadable, write zeros for it and keep going (partial recovery) instead of failing the file.</summary>
    public bool ZeroFillUnreadable { get; set; } = true;
    public bool SkipReparsePoints { get; set; } = true;
    public bool SkipMetaFiles { get; set; } = true;
    public bool VerifyAfterCopy { get; set; }
    public int BufferSize { get; set; } = 1024 * 1024;
}

public sealed record CopyError(string Path, string Message, bool Partial);

public sealed class CopyProgress
{
    public long FilesTotal, FilesDone, FilesSkipped, FilesFailed, FilesPartial, DirsCreated;
    public long BytesTotal, BytesDone;
    public string CurrentFile = "";
    public long CurrentFileSize, CurrentFileDone;
    public double BytesPerSecond;
    public TimeSpan Elapsed;
    public bool Enumerating;
    public List<CopyError> Errors = new();
    public double Fraction => BytesTotal > 0 ? Math.Min(1, (double)BytesDone / BytesTotal) : (FilesTotal > 0 ? (double)FilesDone / FilesTotal : 0);
    public TimeSpan? Eta => BytesPerSecond > 1 && BytesTotal > BytesDone ? TimeSpan.FromSeconds((BytesTotal - BytesDone) / BytesPerSecond) : null;
    public CopyProgress Snapshot() { var c = (CopyProgress)MemberwiseClone(); c.Errors = new List<CopyError>(Errors); return c; }
}

/// <summary>Copies files/folders out of an NTFS volume into a normal Windows (or Linux) directory.</summary>
public sealed class Extractor
{
    private readonly IDirectorySource _src;
    private readonly NtfsVolume _vol;

    public Extractor(IDirectorySource source)
    {
        _src = source;
        _vol = source.Volume;
    }

    public sealed record PlannedItem(NtfsEntry Entry, string RelativePath);

    /// <summary>Walk the selection recursively producing (entry, relative destination path) pairs.</summary>
    public IEnumerable<PlannedItem> Plan(IEnumerable<NtfsEntry> roots, CopyOptions opt, CancellationToken ct = default)
    {
        foreach (var root in roots)
        {
            var stack = new Stack<(NtfsEntry E, string Rel)>();
            stack.Push((root, SafeName(root.Name.Length == 0 ? "root" : root.Name)));
            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var (e, rel) = stack.Pop();
                if (opt.SkipMetaFiles && e.IsMetaFile) continue;
                if (e.IsDirectory)
                {
                    yield return new PlannedItem(e, rel);
                    if (e.IsReparsePoint && opt.SkipReparsePoints) continue;
                    List<NtfsEntry> children;
                    try { children = _src.List(e); }
                    catch (Exception ex) { Log.Warn($"Cannot list {e.Path}: {ex.Message}"); continue; }
                    for (int i = children.Count - 1; i >= 0; i--)
                        stack.Push((children[i], System.IO.Path.Combine(rel, SafeName(children[i].Name))));
                }
                else yield return new PlannedItem(e, rel);
            }
        }
    }

    public CopyProgress Copy(IEnumerable<NtfsEntry> roots, string destDir, CopyOptions opt, IProgress<CopyProgress>? progress = null, CancellationToken ct = default)
    {
        var p = new CopyProgress { Enumerating = true };
        var sw = Stopwatch.StartNew();
        var items = new List<PlannedItem>();
        DateTime lastReport = DateTime.MinValue;
        foreach (var it in Plan(roots, opt, ct))
        {
            items.Add(it);
            if (!it.Entry.IsDirectory) { p.FilesTotal++; p.BytesTotal += it.Entry.Size; }
            if ((DateTime.UtcNow - lastReport).TotalMilliseconds > 200) { p.Elapsed = sw.Elapsed; progress?.Report(p.Snapshot()); lastReport = DateTime.UtcNow; }
        }
        p.Enumerating = false;
        var buf = new byte[opt.BufferSize];
        long windowBytes = 0;
        var window = Stopwatch.StartNew();
        foreach (var it in items)
        {
            ct.ThrowIfCancellationRequested();
            string dest = System.IO.Path.Combine(destDir, it.RelativePath);
            var e = it.Entry;
            try
            {
                if (e.IsDirectory)
                {
                    Directory.CreateDirectory(LongPath(dest));
                    p.DirsCreated++;
                    if (opt.PreserveTimestamps) TrySetTimes(dest, e, true);
                    continue;
                }
                p.CurrentFile = e.Path.Length > 0 ? e.Path : e.Name;
                p.CurrentFileSize = e.Size;
                p.CurrentFileDone = 0;
                if (e.IsReparsePoint && opt.SkipReparsePoints && !e.IsWofCompressed) { p.FilesSkipped++; p.FilesDone++; continue; }
                string target = LongPath(dest);
                if (File.Exists(target))
                {
                    if (opt.Collision == CollisionPolicy.Skip) { p.FilesSkipped++; p.FilesDone++; p.BytesDone += e.Size; continue; }
                    if (opt.Collision == CollisionPolicy.Rename) { dest = UniqueName(dest); target = LongPath(dest); }
                }
                Directory.CreateDirectory(LongPath(System.IO.Path.GetDirectoryName(dest)!));
                bool partial = CopyFileData(e, "", target, opt, buf, p, progress, sw, ref windowBytes, window, ct);
                if (opt.CopyAlternateStreams)
                {
                    foreach (var (name, size, _) in _vol.ListStreams(e.Record))
                    {
                        if (name.Length == 0) continue;
                        string adsTarget = OperatingSystem.IsWindows() ? target + ":" + name : target + "~" + SafeName(name) + ".ads";
                        try { CopyFileData(e, name, adsTarget, opt, buf, p, progress, sw, ref windowBytes, window, ct); }
                        catch (Exception ex) { p.Errors.Add(new CopyError(e.Path + ":" + name, ex.Message, false)); }
                    }
                }
                if (opt.PreserveTimestamps) TrySetTimes(dest, e, false);
                if (opt.PreserveAttributes) TrySetAttributes(dest, e);
                if (e.IsEncrypted) p.Errors.Add(new CopyError(e.Path, "File is EFS-encrypted; copied bytes are ciphertext (needs the original user's certificate to decrypt).", true));
                if (e.IsWofCompressed) p.Errors.Add(new CopyError(e.Path, "File is WOF/CompactOS compressed; main stream copied as stored (may be empty). Enable alternate streams to also get WofCompressedData.", true));
                if (partial) p.FilesPartial++;
                p.FilesDone++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                p.FilesFailed++;
                p.FilesDone++;
                p.Errors.Add(new CopyError(e.Path.Length > 0 ? e.Path : e.Name, ex.Message, false));
                Log.Error($"Copy failed: {e.Path}: {ex.Message}");
                if (!opt.ContinueOnError || ex is DeviceLostException) throw;
            }
            p.Elapsed = sw.Elapsed;
            progress?.Report(p.Snapshot());
        }
        p.Elapsed = sw.Elapsed;
        p.CurrentFile = "";
        progress?.Report(p.Snapshot());
        return p;
    }

    /// <summary>Copy one stream to a file. Returns true if some sectors were unreadable and zero-filled.</summary>
    private bool CopyFileData(NtfsEntry e, string stream, string target, CopyOptions opt, byte[] buf, CopyProgress p, IProgress<CopyProgress>? progress, Stopwatch sw, ref long windowBytes, Stopwatch window, CancellationToken ct)
    {
        bool partial = false;
        using var src = _vol.OpenFile(e, stream);
        using var dst = new FileStream(target, FileMode.Create, opt.VerifyAfterCopy ? FileAccess.ReadWrite : FileAccess.Write, FileShare.None, 1 << 16, FileOptions.SequentialScan);
        long remaining = src.Length;
        DateTime lastReport = DateTime.MinValue;
        while (remaining > 0)
        {
            ct.ThrowIfCancellationRequested();
            int want = (int)Math.Min(buf.Length, remaining);
            int n;
            try { n = src.Read(buf, 0, want); }
            catch (BadSectorException) when (opt.ZeroFillUnreadable)
            {
                // Retry this window sector by sector, zero-filling what stays unreadable.
                n = 0;
                long basePos = src.Position;
                int ss = _vol.Boot.BytesPerSector;
                while (n < want)
                {
                    int piece = Math.Min(ss, want - n);
                    src.Position = basePos + n;
                    try { int g = src.Read(buf, n, piece); if (g <= 0) { Array.Clear(buf, n, piece); g = piece; } n += g; }
                    catch (BadSectorException) { Array.Clear(buf, n, piece); n += piece; partial = true; }
                }
                src.Position = basePos + n;
            }
            if (n <= 0) break;
            dst.Write(buf, 0, n);
            remaining -= n;
            p.BytesDone += n;
            p.CurrentFileDone += n;
            windowBytes += n;
            if (window.ElapsedMilliseconds > 1000) { p.BytesPerSecond = windowBytes / window.Elapsed.TotalSeconds; window.Restart(); windowBytes = 0; }
            if ((DateTime.UtcNow - lastReport).TotalMilliseconds > 150) { p.Elapsed = sw.Elapsed; progress?.Report(p.Snapshot()); lastReport = DateTime.UtcNow; }
        }
        if (partial) p.Errors.Add(new CopyError(e.Path + (stream.Length > 0 ? ":" + stream : ""), "Some sectors were unreadable and were filled with zeros.", true));
        if (opt.VerifyAfterCopy && !partial)
        {
            dst.Flush();
            dst.Position = 0;
            src.Position = 0;
            if (!StreamsEqual(src, dst, buf)) throw new IOException("Verification failed: destination differs from source.");
        }
        return partial;
    }

    private static bool StreamsEqual(Stream a, Stream b, byte[] buf)
    {
        var buf2 = new byte[buf.Length];
        while (true)
        {
            int n1 = ReadFull(a, buf), n2 = ReadFull(b, buf2);
            if (n1 != n2) return false;
            if (n1 == 0) return true;
            if (!buf.AsSpan(0, n1).SequenceEqual(buf2.AsSpan(0, n2))) return false;
        }
    }

    private static int ReadFull(Stream s, byte[] b) { int t = 0; while (t < b.Length) { int n = s.Read(b, t, b.Length - t); if (n <= 0) break; t += n; } return t; }

    public static string SafeName(string name)
    {
        if (name.Length == 0) return "_";
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var chars = name.ToCharArray();
        for (int i = 0; i < chars.Length; i++) if (Array.IndexOf(invalid, chars[i]) >= 0) chars[i] = '_';
        string s = new string(chars).TrimEnd(' ', '.');
        if (s.Length == 0) s = "_";
        if (OperatingSystem.IsWindows())
        {
            string stem = s.Split('.')[0].ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsDigit(stem[3]))) s = "_" + s;
        }
        return s;
    }

    public static string LongPath(string path)
    {
        if (!OperatingSystem.IsWindows()) return path;
        if (path.StartsWith(@"\\?\")) return path;
        string full = System.IO.Path.GetFullPath(path);
        return full.StartsWith(@"\\") ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
    }

    private static string UniqueName(string path)
    {
        string dir = System.IO.Path.GetDirectoryName(path)!, stem = System.IO.Path.GetFileNameWithoutExtension(path), ext = System.IO.Path.GetExtension(path);
        for (int i = 1; ; i++)
        {
            string c = System.IO.Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(LongPath(c))) return c;
        }
    }

    private static void TrySetTimes(string path, NtfsEntry e, bool dir)
    {
        try
        {
            string p = LongPath(path);
            if (dir)
            {
                if (e.Created > DateTime.MinValue) Directory.SetCreationTimeUtc(p, e.Created);
                if (e.Modified > DateTime.MinValue) Directory.SetLastWriteTimeUtc(p, e.Modified);
                if (e.Accessed > DateTime.MinValue) Directory.SetLastAccessTimeUtc(p, e.Accessed);
            }
            else
            {
                if (e.Created > DateTime.MinValue) File.SetCreationTimeUtc(p, e.Created);
                if (e.Modified > DateTime.MinValue) File.SetLastWriteTimeUtc(p, e.Modified);
                if (e.Accessed > DateTime.MinValue) File.SetLastAccessTimeUtc(p, e.Accessed);
            }
        }
        catch { }
    }

    private static void TrySetAttributes(string path, NtfsEntry e)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var a = FileAttributes.Normal;
            if ((e.Attributes & NtfsFileAttributes.ReadOnly) != 0) a |= FileAttributes.ReadOnly;
            if ((e.Attributes & NtfsFileAttributes.Hidden) != 0) a |= FileAttributes.Hidden;
            if ((e.Attributes & NtfsFileAttributes.System) != 0) a |= FileAttributes.System;
            if ((e.Attributes & NtfsFileAttributes.Archive) != 0) a |= FileAttributes.Archive;
            if (a != FileAttributes.Normal) File.SetAttributes(LongPath(path), a & ~FileAttributes.Normal);
        }
        catch { }
    }
}
