using System.Text;
using Nova4Me2.Core.Ntfs;
using Nova4Me2.Core.Recovery;
using Nova4Me2.Core.Util;

namespace Nova4Me2.Core.Forensics;

/// <summary>$Bitmap of the volume: which clusters are allocated.</summary>
public sealed class ClusterBitmap
{
    private readonly byte[] _bits;
    public long TotalClusters { get; }
    public long AllocatedClusters { get; }
    public long FreeClusters => TotalClusters - AllocatedClusters;

    private ClusterBitmap(byte[] bits, long total) { _bits = bits; TotalClusters = total; long a = 0; for (long i = 0; i < total; i++) if (IsAllocated(i)) a++; AllocatedClusters = a; }

    public static ClusterBitmap Load(NtfsVolume vol)
    {
        var rec = vol.GetRecord(6);
        var data = vol.FindAttribute(rec, AttrType.Data) ?? throw new NtfsException("$Bitmap has no data attribute.");
        using var s = vol.OpenAttribute(data);
        return new ClusterBitmap(NtfsVolume.ReadAll(s), vol.TotalClusters);
    }

    public bool IsAllocated(long lcn) => lcn >= 0 && lcn / 8 < _bits.Length && (_bits[lcn / 8] & (1 << (int)(lcn % 8))) != 0;

    public IEnumerable<(long Lcn, long Count)> UnallocatedRanges()
    {
        long start = -1;
        for (long i = 0; i < TotalClusters; i++)
        {
            bool free = !IsAllocated(i);
            if (free && start < 0) start = i;
            if (!free && start >= 0) { yield return (start, i - start); start = -1; }
        }
        if (start >= 0) yield return (start, TotalClusters - start);
    }
}

/// <summary>Reverse map cluster → owning MFT record/attribute (Sleuth Kit ifind -d).</summary>
public sealed class ClusterOwnerMap
{
    public sealed record Run(long Lcn, long Count, long Record, uint AttrType, string AttrName, bool Deleted);
    private readonly List<Run> _runs;
    private ClusterOwnerMap(List<Run> runs) { _runs = runs; }
    public int Count => _runs.Count;

    public static ClusterOwnerMap Build(NtfsVolume vol, IProgress<(long Done, long Total)>? progress = null, CancellationToken ct = default)
    {
        var runs = new List<Run>();
        long total = vol.MftRecordCount;
        for (long n = 0; n < total; n++)
        {
            ct.ThrowIfCancellationRequested();
            MftRecord rec;
            try { rec = vol.GetRecord(n); } catch { continue; }
            if (!MftRecord.HasFileSignature(rec.Raw)) continue;
            foreach (var a in rec.Attributes)
            {
                if (!a.NonResident) continue;
                foreach (var r in a.Runs) if (!r.IsSparse) runs.Add(new Run(r.Lcn, r.Count, rec.IsExtension ? rec.BaseRecord : n, a.Type, a.Name, !rec.InUse));
            }
            if (n % 2048 == 0) progress?.Report((n, total));
        }
        runs.Sort((x, y) => x.Lcn.CompareTo(y.Lcn));
        progress?.Report((total, total));
        return new ClusterOwnerMap(runs);
    }

    public Run? Find(long lcn)
    {
        int lo = 0, hi = _runs.Count - 1;
        Run? best = null;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            var r = _runs[mid];
            if (lcn < r.Lcn) hi = mid - 1;
            else if (lcn >= r.Lcn + r.Count) lo = mid + 1;
            else { best = r; break; }
        }
        if (best != null) return best;
        // Overlapping deleted runs may sit around; do a small local scan for allocated owners first.
        for (int i = Math.Max(0, lo - 4); i < Math.Min(_runs.Count, lo + 4); i++) if (lcn >= _runs[i].Lcn && lcn < _runs[i].Lcn + _runs[i].Count) return _runs[i];
        return null;
    }
}

public sealed class TimelineRow
{
    public long Record { get; init; }
    public string Path { get; init; } = "";
    public bool IsDirectory { get; init; }
    public bool Deleted { get; init; }
    public long Size { get; init; }
    public DateTime Modified { get; init; }
    public DateTime Accessed { get; init; }
    public DateTime MftModified { get; init; }
    public DateTime Created { get; init; }
    public string? Md5 { get; set; }
}

/// <summary>Sleuth Kit-style helpers on top of the NTFS reader: fsstat, istat, fls/timeline, blkls, blkcat, slack.</summary>
public static class NtfsForensics
{
    // ---- fsstat ----
    public static string FsStat(NtfsVolume vol, ClusterBitmap? bitmap = null)
    {
        var b = vol.Boot;
        var sb = new StringBuilder();
        sb.AppendLine("FILE SYSTEM INFORMATION");
        sb.AppendLine("File System Type: NTFS");
        sb.AppendLine($"Volume Serial Number: {b.VolumeSerial:X16}");
        sb.AppendLine($"OEM Name: NTFS");
        sb.AppendLine($"Volume Name: {vol.Info.Label}");
        sb.AppendLine($"Version: Windows NTFS {vol.Info.Version}");
        sb.AppendLine($"Flags: {vol.Info.FlagsText}");
        sb.AppendLine();
        sb.AppendLine("METADATA INFORMATION");
        sb.AppendLine($"First Cluster of MFT: {b.MftLcn}");
        sb.AppendLine($"First Cluster of MFT Mirror: {b.MftMirrLcn}");
        sb.AppendLine($"Size of MFT Entries: {b.MftRecordSize} bytes");
        sb.AppendLine($"Size of Index Records: {b.IndexBlockSize} bytes");
        sb.AppendLine($"Range: 0 - {vol.MftRecordCount - 1}");
        sb.AppendLine($"Root Directory: 5");
        sb.AppendLine();
        sb.AppendLine("CONTENT INFORMATION");
        sb.AppendLine($"Sector Size: {b.BytesPerSector}");
        sb.AppendLine($"Cluster Size: {b.BytesPerCluster}");
        sb.AppendLine($"Total Cluster Range: 0 - {b.TotalClusters - 1}");
        sb.AppendLine($"Total Sector Range: 0 - {b.TotalSectors - 1}");
        if (bitmap != null) sb.AppendLine($"Allocated clusters: {bitmap.AllocatedClusters:N0} ({Format.Bytes(bitmap.AllocatedClusters * b.BytesPerCluster)}), free: {bitmap.FreeClusters:N0} ({Format.Bytes(bitmap.FreeClusters * b.BytesPerCluster)})");
        sb.AppendLine();
        sb.AppendLine("$AttrDef Attribute Values:");
        foreach (var t in new uint[] { 0x10, 0x20, 0x30, 0x40, 0x50, 0x60, 0x70, 0x80, 0x90, 0xA0, 0xB0, 0xC0, 0xD0, 0xE0, 0x100 }) sb.AppendLine($"  {AttrType.Name(t)} (0x{t:X})");
        return sb.ToString();
    }

    // ---- istat ----
    public static string IStat(NtfsVolume vol, long number, MftIndex? index = null)
    {
        var rec = vol.GetRecord(number);
        var sb = new StringBuilder();
        sb.AppendLine($"MFT Entry Header Values:");
        sb.AppendLine($"Entry: {rec.Number}        Sequence: {rec.Sequence}");
        sb.AppendLine($"$LogFile Sequence Number: {rec.LogSequenceNumber}");
        sb.AppendLine($"{(rec.InUse ? "Allocated" : "Not Allocated")} {(rec.IsDirectory ? "Directory" : "File")}");
        sb.AppendLine($"Links: {rec.LinkCount}");
        if (rec.IsExtension) sb.AppendLine($"Extension record of base entry {rec.BaseRecord}");
        if (rec.Problems.Count > 0) sb.AppendLine("Problems: " + string.Join("; ", rec.Problems));
        var si = vol.GetStandardInfo(rec);
        if (si != null)
        {
            sb.AppendLine();
            sb.AppendLine("$STANDARD_INFORMATION Attribute Values:");
            sb.AppendLine($"Flags: {si.Flags}");
            sb.AppendLine($"Owner ID: {si.OwnerId}   Security ID: {si.SecurityId}   USN: {si.Usn}");
            sb.AppendLine($"Created:\t{si.Created:yyyy-MM-dd HH:mm:ss.fffffff} (UTC)");
            sb.AppendLine($"File Modified:\t{si.Modified:yyyy-MM-dd HH:mm:ss.fffffff} (UTC)");
            sb.AppendLine($"MFT Modified:\t{si.MftModified:yyyy-MM-dd HH:mm:ss.fffffff} (UTC)");
            sb.AppendLine($"Accessed:\t{si.Accessed:yyyy-MM-dd HH:mm:ss.fffffff} (UTC)");
        }
        foreach (var fn in vol.GetFileNames(rec))
        {
            sb.AppendLine();
            sb.AppendLine("$FILE_NAME Attribute Values:");
            sb.AppendLine($"Name: {fn.Name}   (namespace {fn.Namespace switch { 0 => "POSIX", 1 => "Win32", 2 => "DOS", 3 => "Win32+DOS", _ => "?" }})");
            sb.AppendLine($"Parent MFT Entry: {fn.ParentRecord} \tSequence: {fn.ParentSequence}");
            sb.AppendLine($"Allocated Size: {fn.AllocatedSize}   \tActual Size: {fn.RealSize}");
            sb.AppendLine($"Flags: {fn.Flags}");
            sb.AppendLine($"Created:\t{fn.Created:yyyy-MM-dd HH:mm:ss.fffffff}   File Modified:\t{fn.Modified:yyyy-MM-dd HH:mm:ss.fffffff}   MFT Modified:\t{fn.MftModified:yyyy-MM-dd HH:mm:ss.fffffff}   Accessed:\t{fn.Accessed:yyyy-MM-dd HH:mm:ss.fffffff}");
        }
        try
        {
            var e = index != null && index.ByRecord.TryGetValue(number, out var ie) ? ie : vol.EntryFromRecord(rec);
            string path = index != null ? index.PathOf(e) : e.Path;
            if (path.Length > 0) sb.AppendLine($"Full path: \\{path}");
        }
        catch { }
        sb.AppendLine();
        sb.AppendLine("Attributes:");
        foreach (var a in vol.GetLogicalAttributes(rec))
        {
            sb.AppendLine($"Type: {a.TypeName} (0x{a.Type:X}-{a.Id})   Name: {(a.Name.Length > 0 ? a.Name : "N/A")}   {(a.NonResident ? "Non-Resident" : "Resident")}{(a.IsCompressed ? ", Compressed" : "")}{(a.IsSparse ? ", Sparse" : "")}{(a.IsEncrypted ? ", Encrypted" : "")}   size: {a.Length}{(a.NonResident ? $"  init_size: {a.InitializedSize}  alloc: {a.AllocatedSize}" : "")}");
            if (a.NonResident)
            {
                var runs = string.Join(" ", a.Runs.Select(r => r.IsSparse ? $"[sparse {r.Count}]" : r.Count == 1 ? $"{r.Lcn}" : $"{r.Lcn}-{r.Lcn + r.Count - 1}"));
                sb.AppendLine("  Clusters: " + (runs.Length > 2000 ? runs[..2000] + " …" : runs));
            }
            else if (a.Type == AttrType.Data && a.ResidentValue.Length > 0)
                sb.AppendLine("  Resident data: " + Format.Hex(a.ResidentValue, 48));
        }
        return sb.ToString();
    }

    // ---- fls / timeline ----
    public static List<TimelineRow> Fls(IDirectorySource src, bool includeDeleted, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var rows = new List<TimelineRow>();
        var stack = new Stack<NtfsEntry>();
        stack.Push(src.Root);
        var visited = new HashSet<long>();
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var d = stack.Pop();
            if (!visited.Add(d.Record)) continue;
            List<NtfsEntry> kids;
            try { kids = src.List(d); } catch { continue; }
            foreach (var e in kids)
            {
                if (e.IsDeleted && !includeDeleted) continue;
                rows.Add(new TimelineRow { Record = e.Record, Path = e.Path, IsDirectory = e.IsDirectory, Deleted = e.IsDeleted, Size = e.Size, Modified = e.Modified, Accessed = e.Accessed, MftModified = e.MftModified, Created = e.Created });
                if (e.IsDirectory && !e.IsReparsePoint && e.Record != MftIndex.OrphanRecord) stack.Push(e);
                if (rows.Count % 1000 == 0) progress?.Report(rows.Count);
            }
        }
        return rows;
    }

    public static void WriteBodyFile(IEnumerable<TimelineRow> rows, string path)
    {
        // TSK body file v3: MD5|name|inode|mode_as_string|UID|GID|size|atime|mtime|ctime|crtime (unix seconds)
        using var w = new StreamWriter(path);
        foreach (var r in rows)
        {
            string name = r.Path.Replace("|", "_") + (r.Deleted ? " (deleted)" : "");
            w.WriteLine($"{r.Md5 ?? "0"}|{name}|{r.Record}|{(r.IsDirectory ? "d/drwxrwxrwx" : "r/rrwxrwxrwx")}|0|0|{r.Size}|{Unix(r.Accessed)}|{Unix(r.Modified)}|{Unix(r.MftModified)}|{Unix(r.Created)}");
        }
    }

    public static void WriteCsv(IEnumerable<TimelineRow> rows, string path)
    {
        using var w = new StreamWriter(path);
        w.WriteLine("record,path,type,deleted,size,created_utc,modified_utc,mft_modified_utc,accessed_utc,md5");
        foreach (var r in rows) w.WriteLine($"{r.Record},\"{r.Path.Replace("\"", "\"\"")}\",{(r.IsDirectory ? "dir" : "file")},{(r.Deleted ? 1 : 0)},{r.Size},{r.Created:o},{r.Modified:o},{r.MftModified:o},{r.Accessed:o},{r.Md5}");
    }

    /// <summary>mactime-style event list: one line per timestamp, sorted, with MACB flags.</summary>
    public static IEnumerable<(DateTime Time, string Macb, TimelineRow Row)> Timeline(IEnumerable<TimelineRow> rows, DateTime? from = null, DateTime? to = null)
    {
        var events = new List<(DateTime, string, TimelineRow)>();
        foreach (var r in rows)
        {
            var groups = new Dictionary<DateTime, char[]>();
            void Add(DateTime t, int idx, char c) { if (t <= DateTime.MinValue) return; if (!groups.TryGetValue(t, out var f)) groups[t] = f = new[] { '.', '.', '.', '.' }; f[idx] = c; }
            Add(r.Modified, 0, 'm'); Add(r.Accessed, 1, 'a'); Add(r.MftModified, 2, 'c'); Add(r.Created, 3, 'b');
            foreach (var (t, f) in groups)
            {
                if (from != null && t < from) continue;
                if (to != null && t > to) continue;
                events.Add((t, new string(f), r));
            }
        }
        return events.OrderBy(e => e.Item1);
    }

    private static long Unix(DateTime t) => t <= DateTime.MinValue ? 0 : (long)(t - DateTime.UnixEpoch).TotalSeconds;

    // ---- blkls / blkcat / blkstat ----
    public static long Blkls(NtfsVolume vol, ClusterBitmap bitmap, string destPath, IProgress<(long Done, long Total)>? progress = null, CancellationToken ct = default)
    {
        long total = bitmap.FreeClusters, done = 0;
        using var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        using var map = new StreamWriter(destPath + ".map.txt");
        map.WriteLine("# unallocated cluster ranges written to " + Path.GetFileName(destPath) + " (lcn_start\tcount\toutput_offset)");
        long outOff = 0;
        int cs = vol.ClusterSize;
        var buf = new byte[Math.Max(cs, 4 << 20)];
        foreach (var (lcn, count) in bitmap.UnallocatedRanges())
        {
            ct.ThrowIfCancellationRequested();
            map.WriteLine($"{lcn}\t{count}\t{outOff}");
            long remaining = count, cur = lcn;
            while (remaining > 0)
            {
                long n = Math.Min(remaining, buf.Length / cs);
                try { vol.ReadBytesAtCluster(cur, 0, buf.AsSpan(0, (int)(n * cs))); }
                catch { Array.Clear(buf, 0, (int)(n * cs)); }
                fs.Write(buf, 0, (int)(n * cs));
                outOff += n * cs; cur += n; remaining -= n; done += n;
                progress?.Report((done, total));
            }
        }
        return outOff;
    }

    public static byte[] BlkCat(NtfsVolume vol, long lcn, int count = 1) => vol.ReadClusters(lcn, count);

    public static string BlkStat(NtfsVolume vol, ClusterBitmap bitmap, ClusterOwnerMap? owners, long lcn, MftIndex? index = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Cluster: {lcn}  (byte offset {lcn * (long)vol.ClusterSize:N0} in the volume, {vol.Offset + lcn * (long)vol.ClusterSize:N0} on the device)");
        sb.AppendLine(bitmap.IsAllocated(lcn) ? "Allocated" : "Not Allocated");
        var owner = owners?.Find(lcn);
        if (owner != null)
        {
            string path = index != null && index.ByRecord.TryGetValue(owner.Record, out var e) ? "\\" + index.PathOf(e) : $"MFT entry {owner.Record}";
            sb.AppendLine($"Owner: {path} ({AttrType.Name(owner.AttrType)}{(owner.AttrName.Length > 0 ? ":" + owner.AttrName : "")}){(owner.Deleted ? " — DELETED file" : "")}");
        }
        else if (owners != null) sb.AppendLine("Owner: none (not referenced by any MFT record)");
        return sb.ToString();
    }

    public static string HexDump(ReadOnlySpan<byte> data, long baseOffset = 0, int maxBytes = 4096)
    {
        var sb = new StringBuilder();
        int n = Math.Min(data.Length, maxBytes);
        for (int i = 0; i < n; i += 16)
        {
            sb.Append($"{baseOffset + i:X10}  ");
            for (int j = 0; j < 16; j++) sb.Append(i + j < n ? $"{data[i + j]:x2} " : "   ");
            sb.Append(' ');
            for (int j = 0; j < 16 && i + j < n; j++) { byte c = data[i + j]; sb.Append(c >= 32 && c < 127 ? (char)c : '.'); }
            sb.AppendLine();
        }
        if (data.Length > maxBytes) sb.AppendLine($"… {data.Length - maxBytes:N0} more bytes");
        return sb.ToString();
    }

    // ---- slack ----
    public sealed class SlackEntry
    {
        public NtfsEntry File { get; init; } = null!;
        public long ClusterOffsetInVolume { get; init; }
        public int Length { get; init; }
        public double Entropy { get; init; }
        public string Preview { get; init; } = "";
        public string Content { get; init; } = "";
    }

    /// <summary>Bytes between a file's logical end and the end of its last cluster (file slack) that are not zero.</summary>
    public static List<SlackEntry> ScanSlack(IDirectorySource src, IProgress<(int Files, int Found)>? progress = null, CancellationToken ct = default, int maxFiles = int.MaxValue)
    {
        var vol = src.Volume;
        var res = new List<SlackEntry>();
        var stack = new Stack<NtfsEntry>();
        stack.Push(src.Root);
        var visited = new HashSet<long>();
        int files = 0;
        int cs = vol.ClusterSize;
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var d = stack.Pop();
            if (!visited.Add(d.Record)) continue;
            List<NtfsEntry> kids;
            try { kids = src.List(d); } catch { continue; }
            foreach (var e in kids)
            {
                if (e.IsDirectory) { if (!e.IsReparsePoint && e.Record != MftIndex.OrphanRecord) stack.Push(e); continue; }
                if (e.IsMetaFile || e.IsDeleted) continue;
                if (++files > maxFiles) return res;
                try
                {
                    var rec = vol.GetRecord(e.Record);
                    var data = vol.FindAttribute(rec, AttrType.Data);
                    if (data == null || !data.NonResident || data.IsCompressed || data.IsSparse || data.RealSize == 0) continue;
                    long inLast = data.RealSize % cs;
                    if (inLast == 0) continue;
                    long lastVcn = data.RealSize / cs;
                    var run = data.Runs.FirstOrDefault(r => lastVcn >= r.Vcn && lastVcn < r.Vcn + r.Count);
                    if (run == null || run.IsSparse) continue;
                    long lcn = run.Lcn + (lastVcn - run.Vcn);
                    int slackLen = (int)(cs - inLast);
                    var buf = new byte[slackLen];
                    vol.ReadBytesAtCluster(lcn, (int)inLast, buf);
                    if (buf.All(b => b == 0)) continue;
                    var preview = new string(buf.Take(64).Select(b => b >= 32 && b < 127 ? (char)b : '.').ToArray());
                    res.Add(new SlackEntry { File = e, ClusterOffsetInVolume = lcn * (long)cs + inLast, Length = slackLen, Entropy = Entropy.Shannon(buf), Preview = preview, Content = FileCarver.DescribeContent(buf) });
                }
                catch { }
                if (files % 500 == 0) progress?.Report((files, res.Count));
            }
        }
        progress?.Report((files, res.Count));
        return res;
    }

    public static string ExtractSlack(NtfsVolume vol, SlackEntry s, string destDir)
    {
        Directory.CreateDirectory(destDir);
        string path = ForensicProject.UniquePath(destDir, Extractor.SafeName(s.File.Path.Replace('\\', '_')) + ".slack");
        var buf = new byte[s.Length];
        vol.Device.ReadExact(vol.Offset + s.ClusterOffsetInVolume, buf);
        File.WriteAllBytes(path, buf);
        return path;
    }

    /// <summary>icat: copy a record's (named) data stream to a file.</summary>
    public static string ICat(NtfsVolume vol, long record, string stream, string destPath)
    {
        using var s = vol.OpenFile(record, stream);
        using var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        s.CopyTo(fs);
        return destPath;
    }

    /// <summary>ils: list MFT entries with allocation state (optionally only unallocated ones).</summary>
    public static IEnumerable<(long Record, bool InUse, bool IsDir, string Name, long Size)> Ils(NtfsVolume vol, bool onlyDeleted, CancellationToken ct = default)
    {
        for (long n = 0; n < vol.MftRecordCount; n++)
        {
            ct.ThrowIfCancellationRequested();
            MftRecord rec;
            try { rec = vol.GetRecord(n); } catch { continue; }
            if (!MftRecord.HasFileSignature(rec.Raw) || rec.IsExtension) continue;
            if (onlyDeleted && rec.InUse) continue;
            var fn = NtfsVolume.BestName(vol.GetFileNames(rec));
            long size = 0;
            try { size = vol.FindAttribute(rec, AttrType.Data)?.Length ?? 0; } catch { }
            yield return (n, rec.InUse, rec.IsDirectory, fn?.Name ?? "", size);
        }
    }
}
