using Nova4Me2.Core.Util;

namespace Nova4Me2.Core.Ntfs;

/// <summary>Result of a full $MFT walk: every file by record number plus a parent → children map rebuilt from $FILE_NAME parents.</summary>
public sealed class MftIndex
{
    public const long OrphanRecord = -2;
    public Dictionary<long, NtfsEntry> ByRecord { get; } = new();
    public Dictionary<long, List<NtfsEntry>> Children { get; } = new();
    public List<NtfsEntry> Orphans { get; } = new();
    public long TotalRecords { get; set; }
    public long InUseRecords { get; set; }
    public long DeletedRecords { get; set; }
    public long BadRecords { get; set; }
    public long UnreadableRecords { get; set; }
    public long DirectoryCount { get; set; }
    public long FileCount { get; set; }
    public long TotalBytes { get; set; }
    public TimeSpan Elapsed { get; set; }

    public List<NtfsEntry> ListChildren(long parent)
    {
        if (parent == OrphanRecord) return Orphans;
        return Children.TryGetValue(parent, out var l) ? l : new List<NtfsEntry>();
    }

    public string PathOf(NtfsEntry e)
    {
        var parts = new List<string>();
        var cur = e;
        int guard = 0;
        while (cur != null && cur.Record != NtfsVolume.RootRecord && guard++ < 512)
        {
            parts.Add(cur.Name);
            ByRecord.TryGetValue(cur.Parent, out cur);
        }
        parts.Reverse();
        return string.Join("\\", parts);
    }
}

/// <summary>Walks the whole $MFT sequentially. Slower than the directory index, but it works even when directory indexes are damaged and it can list deleted files.</summary>
public static class MftScanner
{
    public static MftIndex Scan(NtfsVolume vol, bool includeDeleted = false, IProgress<(long Done, long Total)>? progress = null, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var idx = new MftIndex();
        long total = vol.MftRecordCount;
        idx.TotalRecords = total;
        int rs = vol.RecordSize;
        const int batch = 512;
        var buf = new byte[batch * rs];
        using var mft = vol.OpenAttribute(vol.MftDataAttribute);
        var links = new List<(NtfsEntry Entry, NtfsFileName Fn)>();
        for (long start = 0; start < total; start += batch)
        {
            ct.ThrowIfCancellationRequested();
            int count = (int)Math.Min(batch, total - start);
            mft.Position = start * rs;
            int got = 0;
            try { while (got < count * rs) { int r = mft.Read(buf.AsSpan(got, count * rs - got)); if (r <= 0) break; got += r; } }
            catch (Exception ex) { Log.Warn($"MFT records {start}..{start + count - 1} unreadable: {ex.Message}"); idx.UnreadableRecords += count; continue; }
            int readable = got / rs;
            idx.UnreadableRecords += count - readable;
            for (int i = 0; i < readable; i++)
            {
                long n = start + i;
                var raw = buf.AsSpan(i * rs, rs);
                if (!MftRecord.HasFileSignature(raw)) { if (MftRecord.HasBaadSignature(raw)) idx.BadRecords++; continue; }
                MftRecord rec;
                try { rec = MftRecord.Parse(raw.ToArray(), n, vol.Boot.BytesPerSector, vol.TotalClusters); }
                catch { idx.BadRecords++; continue; }
                if (rec.IsExtension) continue;
                if (rec.InUse) idx.InUseRecords++; else { idx.DeletedRecords++; if (!includeDeleted) continue; }
                var names = vol.GetFileNames(rec);
                if (names.Count == 0) continue;
                // Attribute-list files need extension records resolved for sizes; EntryFromRecord does that lazily.
                var best = NtfsVolume.BestName(names)!;
                NtfsEntry e;
                try { e = vol.EntryFromRecord(rec, best); } catch { continue; }
                idx.ByRecord[n] = e;
                if (e.IsDirectory) idx.DirectoryCount++; else { idx.FileCount++; idx.TotalBytes += e.Size; }
                // Hard links: one entry per distinct (parent, name), DOS-only names dropped when a long name exists.
                var seen = new HashSet<(long, string)>();
                foreach (var fn in names)
                {
                    if (fn.Namespace == 2 && names.Any(o => o != fn && o.Namespace != 2 && o.ParentRecord == fn.ParentRecord)) continue;
                    if (!seen.Add((fn.ParentRecord, fn.Name.ToUpperInvariant()))) continue;
                    var linkEntry = fn == best ? e : new NtfsEntry
                    {
                        Record = e.Record, Sequence = e.Sequence, Parent = fn.ParentRecord, Name = fn.Name, Namespace = fn.Namespace, IsDirectory = e.IsDirectory, Size = e.Size, AllocatedSize = e.AllocatedSize,
                        Created = e.Created, Modified = e.Modified, Accessed = e.Accessed, MftModified = e.MftModified, Attributes = e.Attributes, ReparseTag = e.ReparseTag, IsDeleted = e.IsDeleted
                    };
                    links.Add((linkEntry, fn));
                }
            }
            progress?.Report((Math.Min(total, start + count), total));
        }
        foreach (var (e, fn) in links)
        {
            long parent = fn.ParentRecord;
            bool parentOk = parent == NtfsVolume.RootRecord || (idx.ByRecord.TryGetValue(parent, out var p) && p.IsDirectory && (e.IsDeleted || !p.IsDeleted));
            if (!parentOk)
            {
                var orphan = Clone(e, MftIndex.OrphanRecord, true);
                idx.Orphans.Add(orphan);
                continue;
            }
            if (!idx.Children.TryGetValue(parent, out var l)) idx.Children[parent] = l = new();
            l.Add(e);
        }
        foreach (var l in idx.Children.Values) l.Sort(Cmp);
        idx.Orphans.Sort(Cmp);
        foreach (var e in idx.ByRecord.Values) e.Path = idx.PathOf(e);
        idx.Elapsed = sw.Elapsed;
        return idx;

        static int Cmp(NtfsEntry a, NtfsEntry b) => a.IsDirectory != b.IsDirectory ? (a.IsDirectory ? -1 : 1) : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        static NtfsEntry Clone(NtfsEntry e, long parent, bool orphan) => new()
        {
            Record = e.Record, Sequence = e.Sequence, Parent = parent, Name = e.Name, Namespace = e.Namespace, IsDirectory = e.IsDirectory, Size = e.Size, AllocatedSize = e.AllocatedSize,
            Created = e.Created, Modified = e.Modified, Accessed = e.Accessed, MftModified = e.MftModified, Attributes = e.Attributes, ReparseTag = e.ReparseTag, IsDeleted = e.IsDeleted, IsOrphan = orphan, Path = e.Path
        };
    }
}
