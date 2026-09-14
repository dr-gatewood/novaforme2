using System.Globalization;
using System.Text;
using Nova4Me2.Core.Devices;
using Nova4Me2.Core.Ntfs;
using Nova4Me2.Core.Partitions;
using Nova4Me2.Core.Util;

namespace Nova4Me2.Core.Forensics;

/// <summary>
/// A list of unreadable byte ranges, read from a Nova4Me2 <c>.badsectors.txt</c> log, a GNU ddrescue mapfile,
/// or a plain list of LBAs / byte ranges.
/// </summary>
public sealed class BadSectorLog
{
    public string Path { get; init; } = "";
    public string Format { get; private set; } = "";
    public string? SourceDescription { get; private set; }
    public DateTime? Created { get; private set; }
    public int SectorSize { get; private set; } = 512;
    public List<(long Offset, long Length)> Ranges { get; } = new();
    public List<string> Problems { get; } = new();

    public long TotalBytes => Ranges.Sum(r => r.Length);
    public long TotalSectors => (TotalBytes + SectorSize - 1) / SectorSize;
    public long FirstOffset => Ranges.Count > 0 ? Ranges[0].Offset : 0;
    public long LastOffset => Ranges.Count > 0 ? Ranges[^1].Offset + Ranges[^1].Length : 0;

    public static BadSectorLog Load(string path, int sectorSize = 512) => Parse(File.ReadAllText(path), path, sectorSize);

    public static BadSectorLog Parse(string text, string path = "", int sectorSize = 512)
    {
        var log = new BadSectorLog { Path = path, SectorSize = sectorSize };
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        bool ddrescue = lines.Any(l => l.StartsWith("# Mapfile", StringComparison.OrdinalIgnoreCase)) || lines.Any(l => l.Contains("ddrescue", StringComparison.OrdinalIgnoreCase));
        bool nova = lines.Any(l => l.StartsWith("# Nova4Me2", StringComparison.OrdinalIgnoreCase));
        log.Format = ddrescue ? "ddrescue mapfile" : nova ? "Nova4Me2 bad sector log" : "";
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line[0] == '#')
            {
                if (line.StartsWith("# Source:", StringComparison.OrdinalIgnoreCase)) log.SourceDescription = line[9..].Trim();
                else if (nova && line.Contains(" — ")) { var d = line[(line.LastIndexOf(" — ", StringComparison.Ordinal) + 3)..].Trim(); if (DateTime.TryParse(d, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dt)) log.Created = dt; }
                continue;
            }
            var cols = line.Split(new[] { '\t', ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            if (ddrescue)
            {
                // "0xPOS +" (current position/status) or "0xPOS 0xSIZE STATUS"; anything not '+' was not read successfully.
                if (cols.Length < 3) continue;
                if (!TryNum(cols[0], out long pos) || !TryNum(cols[1], out long size)) continue;
                char status = cols[2][0];
                if (status != '+' && size > 0) log.Ranges.Add((pos, size));
                continue;
            }
            var nums = new List<long>();
            foreach (var c in cols) { if (TryNum(c, out long v)) nums.Add(v); else break; }
            if (nums.Count == 0) { log.Problems.Add("ignored: " + line); continue; }
            if (nums.Count == 1) { log.Ranges.Add((nums[0] * sectorSize, sectorSize)); if (log.Format.Length == 0) log.Format = "LBA list"; }
            else { if (nums[1] <= 0) continue; log.Ranges.Add((nums[0], nums[1])); if (log.Format.Length == 0) log.Format = "byte ranges"; }
        }
        if (log.Format.Length == 0) log.Format = "empty";
        Coalesce(log.Ranges);
        return log;
    }

    private static bool TryNum(string s, out long v)
    {
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return long.TryParse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v);
        return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v);
    }

    internal static void Coalesce(List<(long Offset, long Length)> ranges)
    {
        ranges.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        int w = 0;
        for (int i = 0; i < ranges.Count; i++)
        {
            if (w > 0 && ranges[i].Offset <= ranges[w - 1].Offset + ranges[w - 1].Length)
            {
                long end = Math.Max(ranges[w - 1].Offset + ranges[w - 1].Length, ranges[i].Offset + ranges[i].Length);
                ranges[w - 1] = (ranges[w - 1].Offset, end - ranges[w - 1].Offset);
            }
            else ranges[w++] = ranges[i];
        }
        ranges.RemoveRange(w, ranges.Count - w);
    }
}

public enum BadSectorArea
{
    OutsideDevice,
    PartitionTable,
    Unpartitioned,
    NonNtfsPartition,
    NtfsUnreadable,
    NtfsUnallocated,
    NtfsUnowned,
    NtfsSlack,
    NtfsFile,
    NtfsDeletedFile,
    NtfsMetadata,
}

/// <summary>One unreadable piece (never crosses a cluster boundary inside an NTFS volume) and what lives there.</summary>
public sealed class BadSectorHit
{
    public long Offset { get; init; }
    public long Length { get; init; }
    public long Lba { get; init; }
    public BadSectorArea Area { get; set; }
    public string AreaText { get; set; } = "";
    public string Partition { get; set; } = "";
    public long? Lcn { get; set; }
    public bool? Allocated { get; set; }
    public long? Record { get; set; }
    public string Path { get; set; } = "";
    public string Attribute { get; set; } = "";
    public bool Deleted { get; set; }
    public long? FileOffset { get; set; }
    public long? FileSize { get; set; }
    /// <summary>Bytes of real file content lost in this hit (0 when the hit lies beyond the initialized data).</summary>
    public long BytesLost { get; set; }
    public string Note { get; set; } = "";

    public bool IsData => Area is BadSectorArea.NtfsFile or BadSectorArea.NtfsDeletedFile or BadSectorArea.NtfsMetadata;
    public string Owner => Path.Length > 0 ? Path + (Attribute.Length > 0 && Attribute != "$DATA" ? " (" + Attribute + ")" : "") : AreaText;
}

/// <summary>Per file/stream summary of every hit it received.</summary>
public sealed class AffectedFile
{
    public long Record { get; init; }
    public string Path { get; init; } = "";
    public string Attribute { get; init; } = "";
    public string Partition { get; init; } = "";
    public bool Metadata { get; init; }
    public bool Deleted { get; init; }
    public bool IsDirectory { get; init; }
    public long FileSize { get; init; }
    public long BytesLost { get; set; }
    public long BytesInSlack { get; set; }
    public long FirstOffset { get; set; } = long.MaxValue;
    public long LastOffset { get; set; }
    public int Hits { get; set; }
    public List<(long Offset, long Length)> Ranges { get; } = new();
    public string Impact { get; set; } = "";
    /// <summary>For $MFT hits: the records (and their names) that sit inside the unreadable sectors.</summary>
    public List<(long Record, string Name)> DamagedRecords { get; } = new();

    public double PercentLost => FileSize > 0 ? BytesLost * 100.0 / FileSize : 0;
    public string Display => Path.Length > 0 ? Path : $"MFT entry {Record}";
    public string Kind => Metadata ? "file-system metadata" : Deleted ? "deleted file" : IsDirectory ? "directory index" : "file";
    public string WhereText => BytesLost == 0 && BytesInSlack > 0 ? "past the end of the data (slack)" : FirstOffset == long.MaxValue ? "" : $"{FirstOffset:N0} – {LastOffset:N0} of {FileSize:N0}";
}

public sealed class BadSectorReport
{
    public string LogPath { get; init; } = "";
    public string LogFormat { get; init; } = "";
    public string Source { get; init; } = "";
    public string Device { get; init; } = "";
    public DateTime Created { get; init; } = DateTime.Now;
    public int RangeCount { get; init; }
    public long TotalBytes { get; init; }
    public long TotalSectors { get; init; }
    public long SpanStart { get; init; }
    public long SpanEnd { get; init; }
    public List<BadSectorHit> Hits { get; } = new();
    public List<AffectedFile> Files { get; } = new();
    public Dictionary<BadSectorArea, long> BytesByArea { get; } = new();
    public List<string> Notes { get; } = new();
    public string Verdict { get; set; } = "";
    public string Headline { get; set; } = "";

    public long UserFileBytesLost => Files.Where(f => !f.Metadata && !f.Deleted).Sum(f => f.BytesLost);
    public int UserFilesAffected => Files.Count(f => !f.Metadata && !f.Deleted && f.BytesLost > 0);
    public long Bytes(BadSectorArea a) => BytesByArea.TryGetValue(a, out var v) ? v : 0;

    public static string AreaName(BadSectorArea a) => a switch
    {
        BadSectorArea.OutsideDevice => "beyond the end of the device",
        BadSectorArea.PartitionTable => "partition table (MBR/GPT)",
        BadSectorArea.Unpartitioned => "unpartitioned gap",
        BadSectorArea.NonNtfsPartition => "non-NTFS partition",
        BadSectorArea.NtfsUnreadable => "NTFS volume (structure unreadable)",
        BadSectorArea.NtfsUnallocated => "free space (unallocated clusters)",
        BadSectorArea.NtfsUnowned => "allocated but not owned by any file",
        BadSectorArea.NtfsSlack => "file slack (past the end of the data)",
        BadSectorArea.NtfsFile => "file contents",
        BadSectorArea.NtfsDeletedFile => "deleted file contents",
        BadSectorArea.NtfsMetadata => "file-system metadata",
        _ => a.ToString(),
    };

    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"BAD SECTOR ANALYSIS — {Created:yyyy-MM-dd HH:mm}");
        sb.AppendLine($"Log:     {LogPath} ({LogFormat})");
        if (Source.Length > 0) sb.AppendLine($"Source:  {Source}");
        sb.AppendLine($"Device:  {Device}");
        sb.AppendLine($"Unreadable: {TotalSectors:N0} sectors, {Format.Bytes(TotalBytes)} in {RangeCount:N0} range(s), between {Format.Bytes(SpanStart)} and {Format.Bytes(SpanEnd)} (span {Format.Bytes(Math.Max(0, SpanEnd - SpanStart))})");
        sb.AppendLine();
        sb.AppendLine("VERDICT: " + Headline);
        foreach (var l in Verdict.Split('\n')) if (l.Trim().Length > 0) sb.AppendLine("  " + l.Trim());
        sb.AppendLine();
        sb.AppendLine("BY AREA");
        foreach (var kv in BytesByArea.OrderByDescending(k => k.Value)) sb.AppendLine($"  {AreaName(kv.Key),-46} {Format.Bytes(kv.Value),10}");
        if (Files.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("AFFECTED FILES");
            foreach (var f in Files.OrderByDescending(f => f.BytesLost))
            {
                sb.AppendLine($"  {f.Display}{(f.Attribute.Length > 0 && f.Attribute != "$DATA" ? " [" + f.Attribute + "]" : "")}  ({f.Kind}{(f.Partition.Length > 0 ? ", " + f.Partition : "")})");
                sb.AppendLine($"      size {f.FileSize:N0} B, lost {f.BytesLost:N0} B ({f.PercentLost:0.###}%){(f.BytesInSlack > 0 ? $", {f.BytesInSlack:N0} B in slack (no loss)" : "")}, {f.Hits} hit(s){(f.WhereText.Length > 0 ? ", file offsets " + f.WhereText : "")}");
                sb.AppendLine($"      {f.Impact}");
                foreach (var (r, n) in f.DamagedRecords.Take(50)) sb.AppendLine($"        MFT record {r}: {n}");
                if (f.DamagedRecords.Count > 50) sb.AppendLine($"        … {f.DamagedRecords.Count - 50} more");
            }
        }
        if (Notes.Count > 0) { sb.AppendLine(); sb.AppendLine("NOTES"); foreach (var n in Notes) sb.AppendLine("  " + n); }
        sb.AppendLine();
        sb.AppendLine("DETAIL");
        sb.AppendLine($"  {"byte_offset",14} {"LBA",12} {"len",6}  area / owner");
        foreach (var h in Hits)
            sb.AppendLine($"  {h.Offset,14:N0} {h.Lba,12} {h.Length,6}  {h.Owner}{(h.FileOffset is { } fo ? $" @ file offset {fo:N0}" : "")}{(h.Lcn is { } l ? $" [LCN {l}]" : "")}{(h.Note.Length > 0 ? " — " + h.Note : "")}");
        return sb.ToString();
    }

    public void WriteText(string path) => File.WriteAllText(path, ToText());

    public void WriteCsv(string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("byte_offset,lba,length,area,partition,lcn,allocated,record,path,attribute,deleted,file_offset,file_size,bytes_lost,note");
        foreach (var h in Hits)
            sb.AppendLine(string.Join(",", h.Offset, h.Lba, h.Length, Csv(AreaName(h.Area)), Csv(h.Partition), h.Lcn?.ToString() ?? "", h.Allocated is { } al ? (al ? "1" : "0") : "",
                h.Record?.ToString() ?? "", Csv(h.Path), Csv(h.Attribute), h.Deleted ? "1" : "0", h.FileOffset?.ToString() ?? "", h.FileSize?.ToString() ?? "", h.BytesLost, Csv(h.Note)));
        File.WriteAllText(path, sb.ToString());
    }

    private static string Csv(string s) => s.Contains(',') || s.Contains('"') || s.Contains('\n') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}

/// <summary>Progress of a bad-sector analysis: the current phase and, when known, how far through it we are.</summary>
public sealed record BadSectorProgress(string Phase, long Done, long Total)
{
    public bool Determinate => Total > 0;
    public double Fraction => Total > 0 ? Math.Clamp((double)Done / Total, 0, 1) : 0;
    public int Percent => (int)Math.Round(Fraction * 100);
    public override string ToString() => Determinate ? $"{Phase} — {Percent}%" : Phase;
}

/// <summary>Maps unreadable sectors to partitions, NTFS clusters and the files that own them.</summary>
public static class BadSectorAnalyzer
{
    /// <summary>Find the bad-sector log the imager wrote next to an image (also handles a .vhd made from a .img).</summary>
    public static string? FindLogFor(string imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath)) return null;
        var cands = new List<string> { imagePath + ".badsectors.txt" };
        string noExt = System.IO.Path.ChangeExtension(imagePath, null);
        foreach (var ext in new[] { ".img", ".dd", ".raw", ".bin", ".vhd" }) cands.Add(noExt + ext + ".badsectors.txt");
        cands.Add(noExt + ".badsectors.txt");
        try { return cands.FirstOrDefault(File.Exists); } catch { return null; }
    }

    public static BadSectorReport Analyze(IBlockDevice dev, BadSectorLog log, PartitionTableInfo? table = null, IReadOnlyList<NtfsVolumeCandidate>? volumes = null,
        IProgress<BadSectorProgress>? progress = null, CancellationToken ct = default)
    {
        progress?.Report(new BadSectorProgress("Reading the partition table", 0, 0));
        table ??= PartitionTable.Read(dev);
        if (volumes == null) { progress?.Report(new BadSectorProgress("Locating NTFS volumes", 0, 0)); volumes = VolumeLocator.Find(dev, table, true); }
        int ss = Math.Max(512, dev.SectorSize);
        var report = new BadSectorReport
        {
            LogPath = log.Path, LogFormat = log.Format, Source = log.SourceDescription ?? "", Device = dev.Description,
            RangeCount = log.Ranges.Count, TotalBytes = log.TotalBytes, TotalSectors = (log.TotalBytes + ss - 1) / ss, SpanStart = log.FirstOffset, SpanEnd = log.LastOffset,
        };
        foreach (var p in log.Problems) report.Notes.Add(p);

        // Group the ranges by NTFS volume so each volume is opened (and its cluster map built) once.
        var vols = volumes.OrderBy(v => v.StartOffset).ToList();
        var byVolume = new Dictionary<NtfsVolumeCandidate, List<(long, long)>>();
        var loose = new List<(long Offset, long Length)>();
        foreach (var r in log.Ranges)
        {
            long off = r.Offset, end = r.Offset + r.Length;
            while (off < end)
            {
                var v = vols.FirstOrDefault(v => off >= v.StartOffset && off < v.StartOffset + v.Length);
                if (v == null)
                {
                    long next = vols.Where(v => v.StartOffset > off).Select(v => v.StartOffset).DefaultIfEmpty(long.MaxValue).Min();
                    long stop = Math.Min(end, next);
                    loose.Add((off, stop - off)); off = stop;
                }
                else
                {
                    long stop = Math.Min(end, v.StartOffset + v.Length);
                    if (!byVolume.TryGetValue(v, out var l)) byVolume[v] = l = new List<(long, long)>();
                    l.Add((off, stop - off)); off = stop;
                }
            }
        }

        foreach (var (off, len) in loose) report.Hits.Add(ClassifyLoose(dev, table, off, len, ss));

        foreach (var (cand, ranges) in byVolume)
        {
            ct.ThrowIfCancellationRequested();
            string partText = cand.Partition != null ? $"Partition {cand.Partition.Index}{(cand.Label.Length > 0 ? $" \"{cand.Label}\"" : "")}" : $"NTFS volume @ {Format.Bytes(cand.StartOffset)}{(cand.Label.Length > 0 ? $" \"{cand.Label}\"" : "")}";
            NtfsVolume vol;
            try { vol = NtfsVolume.Open(dev, cand); }
            catch (Exception ex)
            {
                report.Notes.Add($"{partText}: NTFS structures could not be read ({ex.Message}); its {ranges.Count} range(s) are reported by position only.");
                foreach (var (o, l) in ranges) report.Hits.Add(new BadSectorHit { Offset = o, Length = l, Lba = o / ss, Area = BadSectorArea.NtfsUnreadable, AreaText = partText + " (NTFS, unreadable)", Partition = partText });
                continue;
            }
            progress?.Report(new BadSectorProgress($"{partText}: reading $Bitmap", 0, 0));
            ClusterBitmap? bitmap = null;
            try { bitmap = ClusterBitmap.Load(vol); } catch (Exception ex) { report.Notes.Add($"{partText}: $Bitmap unreadable ({ex.Message}); allocation state unknown."); }
            long mftTotal = Math.Max(1, vol.MftRecordCount);
            progress?.Report(new BadSectorProgress($"{partText}: indexing {mftTotal:N0} MFT records to find which file owns each cluster", 0, mftTotal));
            ClusterOwnerMap owners = ClusterOwnerMap.Build(vol, new Progress<(long Done, long Total)>(x => progress?.Report(new BadSectorProgress($"{partText}: indexing MFT records ({x.Done:N0} of {x.Total:N0})", x.Done, x.Total))), ct);
            var ctx = new VolumeContext(vol, bitmap, owners, partText);
            int done = 0;
            foreach (var (o, l) in ranges)
            {
                ct.ThrowIfCancellationRequested();
                if (++done % 16 == 0 || done == ranges.Count) progress?.Report(new BadSectorProgress($"{partText}: mapping unreadable ranges to files ({done:N0} of {ranges.Count:N0})", done, ranges.Count));
                // Split at cluster boundaries so every hit maps to exactly one cluster (and therefore one owner).
                long off = o, end = o + l;
                while (off < end)
                {
                    long rel = off - vol.Offset;
                    long lcn = rel / vol.ClusterSize;
                    long clusterEnd = vol.Offset + (lcn + 1) * (long)vol.ClusterSize;
                    long stop = Math.Min(end, clusterEnd);
                    report.Hits.Add(ClassifyInVolume(ctx, off, stop - off, ss));
                    off = stop;
                }
            }
            ctx.FinishFiles(report);
        }

        progress?.Report(new BadSectorProgress("Summarising", 0, 0));
        report.Hits.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        foreach (var h in report.Hits) report.BytesByArea[h.Area] = report.Bytes(h.Area) + h.Length;
        Summarize(report);
        progress?.Report(new BadSectorProgress("Done", 1, 1));
        return report;
    }

    private static BadSectorHit ClassifyLoose(IBlockDevice dev, PartitionTableInfo table, long off, long len, int ss)
    {
        var h = new BadSectorHit { Offset = off, Length = len, Lba = off / ss };
        if (off >= dev.Length) { h.Area = BadSectorArea.OutsideDevice; h.AreaText = "beyond the end of the device"; return h; }
        long lba = off / ss, lastLba = dev.Length / ss - 1;
        var part = table.Partitions.FirstOrDefault(p => off >= p.StartOffset && off < p.EndOffset);
        if (part != null)
        {
            h.Area = BadSectorArea.NonNtfsPartition;
            h.Partition = $"Partition {part.Index}";
            h.AreaText = $"Partition {part.Index} ({part.TypeName}{(part.Name.Length > 0 ? " \"" + part.Name + "\"" : "")}) — not NTFS, contents not analysed";
            return h;
        }
        if (lba == 0) { h.Area = BadSectorArea.PartitionTable; h.AreaText = table.Scheme == PartitionScheme.Gpt ? "protective MBR (LBA 0)" : "master boot record (LBA 0)"; h.Note = "partition table sector; Repair can rebuild it from the backup GPT"; return h; }
        if (table.Scheme == PartitionScheme.Gpt && lba >= 1 && lba <= 33) { h.Area = BadSectorArea.PartitionTable; h.AreaText = lba == 1 ? "primary GPT header (LBA 1)" : $"primary GPT partition entries (LBA {lba})"; h.Note = "a backup copy lives at the end of the disk; Repair → Rebuild GPT from backup"; return h; }
        if (table.Scheme == PartitionScheme.Gpt && lba >= lastLba - 33) { h.Area = BadSectorArea.PartitionTable; h.AreaText = lba == lastLba ? "backup GPT header (last sector)" : $"backup GPT partition entries (LBA {lba})"; h.Note = "backup copy only; the primary table is intact"; return h; }
        h.Area = BadSectorArea.Unpartitioned;
        h.AreaText = off < (table.Partitions.Count > 0 ? table.Partitions.Min(p => p.StartOffset) : long.MaxValue) ? "gap before the first partition (alignment space)" : "unpartitioned gap between/after partitions";
        return h;
    }

    private sealed class VolumeContext
    {
        public readonly NtfsVolume Vol; public readonly ClusterBitmap? Bitmap; public readonly ClusterOwnerMap Owners; public readonly string Partition;
        private readonly Dictionary<long, string> _paths = new();
        private readonly Dictionary<(long, uint, string), AffectedFile> _files = new();
        private readonly Dictionary<(long, uint, string), NtfsAttribute?> _attrs = new();
        public VolumeContext(NtfsVolume vol, ClusterBitmap? bitmap, ClusterOwnerMap owners, string partition) { Vol = vol; Bitmap = bitmap; Owners = owners; Partition = partition; }

        public string PathOf(long record)
        {
            if (_paths.TryGetValue(record, out var p)) return p;
            string result;
            try
            {
                var parts = new List<string>(); long cur = record; int guard = 0;
                while (cur != NtfsVolume.RootRecord && guard++ < 256)
                {
                    var e = Vol.EntryFromRecord(Vol.GetRecord(cur));
                    parts.Add(e.Name.Length > 0 ? e.Name : $"<record {cur}>");
                    if (e.Parent == cur || e.Parent < 0) break;
                    cur = e.Parent;
                }
                parts.Reverse();
                result = "\\" + string.Join("\\", parts);
            }
            catch { result = $"MFT entry {record}"; }
            return _paths[record] = result;
        }

        public NtfsAttribute? Attr(long record, uint type, string name)
        {
            var key = (record, type, name);
            if (_attrs.TryGetValue(key, out var a)) return a;
            try { a = Vol.GetLogicalAttributes(Vol.GetRecord(record)).FirstOrDefault(x => x.Type == type && x.Name == name && x.NonResident); }
            catch { a = null; }
            return _attrs[key] = a;
        }

        public AffectedFile File(BadSectorHit h, ClusterOwnerMap.Run run, bool metadata, bool isDir)
        {
            var key = (run.Record, run.AttrType, run.AttrName);
            if (!_files.TryGetValue(key, out var f))
                _files[key] = f = new AffectedFile { Record = run.Record, Path = h.Path, Attribute = h.Attribute, Partition = Partition, Metadata = metadata, Deleted = run.Deleted, IsDirectory = isDir, FileSize = h.FileSize ?? 0 };
            f.Hits++;
            f.BytesLost += h.BytesLost;
            f.BytesInSlack += h.Length - h.BytesLost;
            if (h.FileOffset is { } fo && h.BytesLost > 0) { f.FirstOffset = Math.Min(f.FirstOffset, fo); f.LastOffset = Math.Max(f.LastOffset, fo + h.BytesLost); f.Ranges.Add((fo, h.BytesLost)); }
            return f;
        }

        public void FinishFiles(BadSectorReport report)
        {
            foreach (var f in _files.Values)
            {
                BadSectorLog.Coalesce(f.Ranges);
                f.Impact = DescribeImpact(f);
                report.Files.Add(f);
            }
        }

        private string DescribeImpact(AffectedFile f)
        {
            string name = f.Path.Length > 0 ? f.Path.TrimStart('\\') : $"MFT entry {f.Record}";
            if (f.Metadata)
            {
                switch (f.Record)
                {
                    case 0:
                        if (f.Attribute == "$DATA")
                        {
                            foreach (var (fo, len) in f.Ranges)
                                for (long r = fo / Vol.RecordSize; r <= (fo + len - 1) / Vol.RecordSize; r++)
                                    if (!f.DamagedRecords.Any(d => d.Record == r)) f.DamagedRecords.Add((r, RecordName(r)));
                            return $"$MFT records are unreadable: {f.DamagedRecords.Count} file record(s) damaged. Their data clusters are untouched but the name/size/location entry is; $MFTMirr covers records 0-3 only. An MFT scan (Browse → include deleted) lists the files whose names survive, and carving recovers the rest by content.";
                        }
                        return "$MFT bitmap/attribute: which records are in use is unreliable for part of the table; chkdsk /f rebuilds it.";
                    case 1: return "$MFTMirr: the backup copy of the first MFT records. No loss while $MFT itself is intact.";
                    case 2: return "$LogFile: NTFS transaction log. No user data; chkdsk resets it.";
                    case 3: return "$Volume: volume name/version/flags. Trivial to rebuild.";
                    case 4: return "$AttrDef: attribute definitions; a standard table that chkdsk restores.";
                    case 5: return "Root directory index: the top-level listing may be incomplete, files themselves are intact (an MFT scan recovers the listing).";
                    case 6: return "$Bitmap: free-space accounting for the affected cluster ranges is unreliable; chkdsk /f rebuilds it. No user data.";
                    case 7: return "$Boot: the boot sector / bootstrap code. The backup boot sector at the end of the volume can restore it (Repair view).";
                    case 8: return "$BadClus: NTFS's own bad-cluster list. No user data.";
                    case 9: return "$Secure: security descriptors; affected files may lose their ACLs, not their contents.";
                    case 10: return "$UpCase: uppercase table; standard content that chkdsk restores.";
                    case 11: return "$Extend directory index: metadata directory listing; no user data.";
                }
                if (f.Path.Contains("$UsnJrnl", StringComparison.OrdinalIgnoreCase)) return "$UsnJrnl change journal: history of recent changes only; no user data.";
                if (f.Path.Contains("$Extend", StringComparison.OrdinalIgnoreCase)) return "NTFS metadata under $Extend (quota/object-id/reparse index); no user data.";
                return "File-system metadata; no user file contents in these sectors.";
            }
            if (f.IsDirectory) return $"Directory index block of {name}: its listing may miss entries or fail to open in Windows; the files inside are intact and an MFT scan (Browse → include deleted) still finds them.";
            if (f.Deleted) return $"Belongs to a deleted file; only matters if you intend to undelete {name}.";
            if (f.BytesLost == 0) return $"All hits lie past the end of the file's data (slack). {name} is intact.";
            string range = f.Ranges.Count == 1 ? $"bytes {f.Ranges[0].Offset:N0}-{f.Ranges[0].Offset + f.Ranges[0].Length - 1:N0}" : $"{f.Ranges.Count} places between bytes {f.FirstOffset:N0} and {f.LastOffset - 1:N0}";
            string sev = f.PercentLost >= 50 ? "most of the file is gone" : f.PercentLost >= 5 ? "a substantial part of the file is gone" : "a small hole";
            return $"{name}: {f.BytesLost:N0} of {f.FileSize:N0} bytes ({f.PercentLost:0.###}%) replaced by zeros at {range} — {sev}. Whether the file still opens depends on its format: archives/databases/executables usually fail, media and documents often open with a glitch.";
        }

        private string RecordName(long r)
        {
            try
            {
                var rec = Vol.GetRecord(r);
                if (!MftRecord.HasFileSignature(rec.Raw)) return "(unused)";
                var e = Vol.EntryFromRecord(rec);
                return (rec.InUse ? "" : "(deleted) ") + PathOf(r) + (e.IsDirectory ? "\\" : "");
            }
            catch (Exception ex) { return "(unreadable: " + ex.Message + ")"; }
        }
    }

    private static BadSectorHit ClassifyInVolume(VolumeContext c, long off, long len, int ss)
    {
        var vol = c.Vol;
        long rel = off - vol.Offset;
        long lcn = rel / vol.ClusterSize;
        var h = new BadSectorHit { Offset = off, Length = len, Lba = off / ss, Partition = c.Partition, Lcn = lcn };
        if (lcn >= vol.TotalClusters)
        {
            h.Area = BadSectorArea.NtfsMetadata; h.AreaText = c.Partition + " — backup boot sector area (past the last cluster)"; h.Path = "\\$Boot (backup)"; h.Attribute = "$DATA";
            h.Note = "backup boot sector; only needed if the primary boot sector is damaged";
            return h;
        }
        h.Allocated = c.Bitmap?.IsAllocated(lcn);
        var run = c.Owners.Find(lcn);
        if (run == null)
        {
            if (h.Allocated == false) { h.Area = BadSectorArea.NtfsUnallocated; h.AreaText = c.Partition + " — free space (unallocated cluster)"; }
            else if (h.Allocated == true) { h.Area = BadSectorArea.NtfsUnowned; h.AreaText = c.Partition + " — cluster marked allocated but no file references it (lost cluster)"; h.Note = "chkdsk would return it to free space"; }
            else { h.Area = BadSectorArea.NtfsUnallocated; h.AreaText = c.Partition + " — not referenced by any file ($Bitmap unreadable)"; }
            return h;
        }
        string path = c.PathOf(run.Record);
        bool metadata = run.Record < 16 || path.StartsWith("\\$Extend\\", StringComparison.OrdinalIgnoreCase) || (path.StartsWith("\\$", StringComparison.Ordinal) && run.Record < 64);
        h.Record = run.Record;
        h.Path = run.Record == NtfsVolume.RootRecord ? "\\" : c.PathOf(run.Record);
        h.Attribute = AttrType.Name(run.AttrType) + (run.AttrName.Length > 0 ? ":" + run.AttrName : "");
        h.Deleted = run.Deleted;
        var attr = c.Attr(run.Record, run.AttrType, run.AttrName);
        long fileOffset = (run.Vcn + (lcn - run.Lcn)) * (long)vol.ClusterSize + (rel - lcn * (long)vol.ClusterSize);
        h.FileOffset = fileOffset;
        h.FileSize = attr?.RealSize;
        long valid = attr != null ? Math.Min(attr.InitializedSize > 0 ? attr.InitializedSize : attr.RealSize, attr.RealSize) : fileOffset + len;
        h.BytesLost = Math.Max(0, Math.Min(fileOffset + len, valid) - fileOffset);
        bool isDir = run.AttrType == AttrType.IndexAllocation;
        if (metadata) { h.Area = BadSectorArea.NtfsMetadata; h.AreaText = c.Partition + " — NTFS metadata " + h.Path; }
        else if (run.Deleted) { h.Area = BadSectorArea.NtfsDeletedFile; h.AreaText = c.Partition + " — deleted file " + h.Path; }
        else if (h.BytesLost == 0 && attr != null) { h.Area = BadSectorArea.NtfsSlack; h.AreaText = c.Partition + " — slack of " + h.Path; h.Note = "past the end of the file's data; nothing lost"; }
        else { h.Area = BadSectorArea.NtfsFile; h.AreaText = c.Partition + " — " + h.Path; }
        if (isDir && h.Area == BadSectorArea.NtfsFile) h.Note = "directory index block";
        c.File(h, run, metadata, isDir);
        return h;
    }

    private static void Summarize(BadSectorReport r)
    {
        var user = r.Files.Where(f => !f.Metadata && !f.Deleted && !f.IsDirectory && f.BytesLost > 0).ToList();
        var dirs = r.Files.Where(f => !f.Metadata && f.IsDirectory && f.BytesLost > 0).ToList();
        var meta = r.Files.Where(f => f.Metadata).ToList();
        var deleted = r.Files.Where(f => f.Deleted && !f.Metadata).ToList();
        var sb = new StringBuilder();
        if (r.Hits.Count == 0) { r.Headline = "The log lists no unreadable sectors."; r.Verdict = ""; return; }
        if (user.Count == 0 && dirs.Count == 0 && meta.Count == 0)
        {
            r.Headline = "No files affected.";
            sb.AppendLine($"All {r.TotalSectors:N0} unreadable sectors ({Format.Bytes(r.TotalBytes)}) lie outside any live file: {Describe(r)}.");
            sb.AppendLine("Every file on the volume was copied intact. The zero-filled sectors only matter for carving deleted data from free space.");
        }
        else
        {
            var parts = new List<string>();
            if (user.Count > 0) parts.Add($"{user.Count} file(s) lost {Format.Bytes(user.Sum(f => f.BytesLost))} of content");
            if (dirs.Count > 0) parts.Add($"{dirs.Count} directory listing(s) damaged (files inside intact)");
            if (meta.Count > 0) parts.Add($"{meta.Count} metadata structure(s) hit" + (meta.Any(m => m.Record == 0 && m.Attribute == "$DATA") ? $" incl. {meta.Where(m => m.Record == 0).Sum(m => m.DamagedRecords.Count)} MFT record(s)" : ""));
            r.Headline = string.Join("; ", parts) + ".";
            if (user.Count > 0)
            {
                sb.AppendLine("Files with holes (zeros where the unreadable sectors were):");
                foreach (var f in user.OrderByDescending(f => f.BytesLost).Take(25)) sb.AppendLine($"  {f.Display}: {Format.Bytes(f.BytesLost)} of {Format.Bytes(f.FileSize)} ({f.PercentLost:0.###}%)");
                if (user.Count > 25) sb.AppendLine($"  … {user.Count - 25} more (see AFFECTED FILES)");
            }
            foreach (var d in dirs) sb.AppendLine($"Directory {d.Display}: index damaged; use Browse with 'include deleted' (MFT scan) to list its contents.");
            foreach (var m in meta) sb.AppendLine($"{m.Display}: {m.Impact}");
            long other = r.TotalBytes - r.Files.Sum(f => f.BytesLost);
            if (other > 0) sb.AppendLine($"The remaining {Format.Bytes(other)} lie in {Describe(r)}.");
        }
        if (deleted.Count > 0) sb.AppendLine($"{deleted.Count} deleted file(s) also overlap the bad sectors; irrelevant unless you undelete them.");
        if (r.Bytes(BadSectorArea.PartitionTable) > 0) sb.AppendLine("Partition-table sectors are affected: check Repair → Rebuild GPT / boot sector if the disk or image does not show its partitions.");
        if (r.Bytes(BadSectorArea.NonNtfsPartition) > 0) sb.AppendLine("Some sectors fall in a non-NTFS partition (EFI/recovery/other); those contents were not analysed.");
        r.Verdict = sb.ToString().TrimEnd();
    }

    private static string Describe(BadSectorReport r)
    {
        var l = new List<string>();
        foreach (var kv in r.BytesByArea.OrderByDescending(k => k.Value))
            if (kv.Key is not (BadSectorArea.NtfsFile or BadSectorArea.NtfsMetadata))
                l.Add($"{BadSectorReport.AreaName(kv.Key)} ({Format.Bytes(kv.Value)})");
        return l.Count > 0 ? string.Join(", ", l) : "no other areas";
    }
}
