using Nova4Me2.Core.Devices;
using Nova4Me2.Core.Partitions;
using Nova4Me2.Core.Util;

namespace Nova4Me2.Core.Ntfs;

/// <summary>
/// Read-only NTFS implementation that talks straight to sectors. It does not need Windows to recognise the
/// volume (that is the whole point when Windows reports the drive as RAW).
/// </summary>
public sealed class NtfsVolume : IDisposable
{
    public const long RootRecord = 5;
    private readonly Dictionary<long, MftRecord> _cache = new();
    private readonly LinkedList<long> _lru = new();
    private readonly object _lock = new();
    private const int CacheSize = 8192;
    private NtfsAttribute _mftData = null!;
    private NtfsStream? _mftStream;

    public IBlockDevice Device { get; }
    public long Offset { get; }
    public BootSector Boot { get; }
    public int ClusterSize => Boot.BytesPerCluster;
    public int RecordSize => Boot.MftRecordSize;
    public int IndexBlockSize => Boot.IndexBlockSize;
    public long TotalClusters => Boot.TotalClusters;
    public long Length => Boot.VolumeBytes;
    public NtfsVolumeInfo Info { get; private set; } = new();
    public List<string> Problems { get; } = new();
    public bool MftLoadedFromMirror { get; private set; }
    public long MftRecordCount => _mftData.RealSize / RecordSize;
    public NtfsAttribute MftDataAttribute => _mftData;
    public NtfsVolumeCandidate? Candidate { get; }

    private NtfsVolume(IBlockDevice dev, long offset, BootSector boot, NtfsVolumeCandidate? cand)
    {
        Device = dev;
        Offset = offset;
        Boot = boot;
        Candidate = cand;
    }

    public static NtfsVolume Open(IBlockDevice dev, NtfsVolumeCandidate c)
    {
        var v = Open(dev, c.StartOffset, c.BootSector, c);
        foreach (var n in c.Notes) v.Problems.Add(n);
        if (c.Label.Length == 0) c.Label = v.Info.Label;
        return v;
    }

    public static NtfsVolume Open(IBlockDevice dev, long offset, BootSector? boot = null, NtfsVolumeCandidate? cand = null)
    {
        if (boot == null)
        {
            var c = VolumeLocator.Probe(dev, offset, dev.Length - offset, null) ?? throw new NtfsException($"No NTFS boot sector at offset {offset} (nor a backup).");
            boot = c.BootSector;
            cand ??= c;
        }
        var vol = new NtfsVolume(dev, offset, boot, cand);
        vol.LoadMft();
        vol.LoadVolumeInfo();
        return vol;
    }

    private void LoadMft()
    {
        MftRecord? rec0 = null;
        Exception? primaryError = null;
        try
        {
            var raw = Device.ReadBytes(Offset + Boot.MftLcn * ClusterSize, RecordSize);
            rec0 = MftRecord.Parse(raw, 0, Boot.BytesPerSector, TotalClusters);
            if (rec0.First(AttrType.Data) == null) throw new NtfsException("$MFT record 0 has no $DATA attribute (" + string.Join("; ", rec0.Problems) + ")");
        }
        catch (Exception ex)
        {
            primaryError = ex;
            rec0 = null;
        }
        if (rec0 == null)
        {
            try
            {
                var raw = Device.ReadBytes(Offset + Boot.MftMirrLcn * ClusterSize, RecordSize);
                var mirror = MftRecord.Parse(raw, 0, Boot.BytesPerSector, TotalClusters);
                if (mirror.First(AttrType.Data) == null) throw new NtfsException("$MFTMirr record 0 unusable");
                rec0 = mirror;
                MftLoadedFromMirror = true;
                Problems.Add($"$MFT record 0 is damaged ({primaryError?.Message}); using the copy in $MFTMirr (fixable).");
            }
            catch (Exception ex2)
            {
                throw new NtfsException($"Cannot read the $MFT: primary failed ({primaryError?.Message}), mirror failed ({ex2.Message}).", primaryError);
            }
        }
        foreach (var p in rec0.Problems) Problems.Add("$MFT record 0: " + p);
        _mftData = rec0.First(AttrType.Data)!;
        _mftStream = new NtfsStream(this, _mftData);
        // Seed the cache with record 0 so attribute-list resolution for a huge $MFT can bootstrap.
        Put(rec0);
        var attrList = rec0.First(AttrType.AttributeList);
        if (attrList != null)
        {
            try
            {
                var logical = GetLogicalAttributes(rec0);
                var full = logical.FirstOrDefault(a => a.Type == AttrType.Data && a.Name.Length == 0);
                if (full != null && full.Runs.Count > _mftData.Runs.Count) { _mftData = full; _mftStream = new NtfsStream(this, _mftData); }
            }
            catch (Exception ex) { Problems.Add("$MFT attribute list could not be resolved fully: " + ex.Message); }
        }
    }

    private void LoadVolumeInfo()
    {
        try
        {
            var v = GetRecord(3);
            string label = "";
            var vn = v.First(AttrType.VolumeName);
            if (vn is { NonResident: false }) label = Bin.Utf16(vn.ResidentValue, 0, vn.ResidentValue.Length / 2).TrimEnd('\0');
            var vi = v.First(AttrType.VolumeInformation);
            byte maj = 0, min = 0; ushort flags = 0;
            if (vi is { NonResident: false } && vi.ResidentValue.Length >= 12) { maj = vi.ResidentValue[8]; min = vi.ResidentValue[9]; flags = Bin.U16(vi.ResidentValue, 10); }
            Info = new NtfsVolumeInfo { Label = label, MajorVersion = maj, MinorVersion = min, Flags = flags };
        }
        catch (Exception ex) { Problems.Add("$Volume record unreadable: " + ex.Message); }
    }

    // ---- raw access helpers -------------------------------------------------------------------

    public void ReadBytesAtCluster(long lcn, int offsetInCluster, Span<byte> dst)
    {
        if (lcn < 0 || lcn >= TotalClusters + 1) throw new NtfsException($"Cluster {lcn} outside the volume.");
        Device.ReadExact(Offset + lcn * ClusterSize + offsetInCluster, dst);
    }

    public byte[] ReadClusters(long lcn, long count)
    {
        var b = new byte[count * ClusterSize];
        ReadBytesAtCluster(lcn, 0, b);
        return b;
    }

    /// <summary>Raw bytes of MFT record <paramref name="n"/> (no fixups applied).</summary>
    public byte[] ReadRawRecord(long n)
    {
        if (n < 0 || n >= MftRecordCount) throw new NtfsException($"MFT record {n} is beyond the end of the $MFT ({MftRecordCount} records).");
        var b = new byte[RecordSize];
        lock (_lock)
        {
            _mftStream!.Position = n * RecordSize;
            int got = 0;
            while (got < RecordSize) { int r = _mftStream.Read(b.AsSpan(got)); if (r <= 0) break; got += r; }
            if (got < RecordSize) throw new NtfsException($"Short read of MFT record {n}.");
        }
        return b;
    }

    public MftRecord GetRecord(long n)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(n, out var hit)) { _lru.Remove(n); _lru.AddFirst(n); return hit; }
        }
        var rec = MftRecord.Parse(ReadRawRecord(n), n, Boot.BytesPerSector, TotalClusters);
        if (!MftRecord.HasFileSignature(rec.Raw) && n < 16 && !MftLoadedFromMirror)
        {
            // System records 0..3 also live in $MFTMirr.
            try
            {
                var mirr = Device.ReadBytes(Offset + Boot.MftMirrLcn * ClusterSize + n * RecordSize, RecordSize);
                if (MftRecord.HasFileSignature(mirr)) { rec = MftRecord.Parse(mirr, n, Boot.BytesPerSector, TotalClusters); rec.Problems.Add("read from $MFTMirr"); }
            }
            catch { }
        }
        Put(rec);
        return rec;
    }

    public MftRecord? TryGetRecord(long n)
    {
        try { return GetRecord(n); } catch { return null; }
    }

    private void Put(MftRecord rec)
    {
        lock (_lock)
        {
            _cache[rec.Number] = rec;
            _lru.Remove(rec.Number);
            _lru.AddFirst(rec.Number);
            while (_lru.Count > CacheSize) { var last = _lru.Last!.Value; _lru.RemoveLast(); _cache.Remove(last); }
        }
    }

    public void ClearCache() { lock (_lock) { _cache.Clear(); _lru.Clear(); } }

    // ---- attributes --------------------------------------------------------------------------

    /// <summary>All attributes of a file, following $ATTRIBUTE_LIST into extension records and merging non-resident fragments.</summary>
    public List<NtfsAttribute> GetLogicalAttributes(MftRecord rec)
    {
        var list = rec.First(AttrType.AttributeList);
        if (list == null) return new List<NtfsAttribute>(rec.Attributes);
        byte[] bytes;
        if (list.NonResident) { using var s = new NtfsStream(this, list); bytes = ReadAll(s); }
        else bytes = list.ResidentValue;
        var entries = AttributeListEntry.Parse(bytes);
        var result = new List<NtfsAttribute>();
        var groups = new Dictionary<(uint, string), List<NtfsAttribute>>();
        var order = new List<(uint, string)>();
        var seen = new HashSet<NtfsAttribute>();
        foreach (var e in entries)
        {
            MftRecord? r = e.RecordNumber == rec.Number ? rec : TryGetRecord(e.RecordNumber);
            if (r == null) { rec.Problems.Add($"extension record {e.RecordNumber} unreadable"); continue; }
            if (r != rec && (r.BaseRecord != rec.Number || !r.InUse)) continue; // stale entry
            var a = r.Attributes.FirstOrDefault(x => x.Type == e.Type && x.Id == e.AttributeId)
                    ?? r.Attributes.FirstOrDefault(x => x.Type == e.Type && string.Equals(x.Name, e.Name, StringComparison.OrdinalIgnoreCase) && x.StartVcn == e.StartVcn);
            if (a == null || !seen.Add(a)) continue;
            if (!a.NonResident) { result.Add(a); continue; }
            var key = (a.Type, a.Name.ToUpperInvariant());
            if (!groups.TryGetValue(key, out var g)) { groups[key] = g = new(); order.Add(key); }
            g.Add(a);
        }
        foreach (var key in order)
        {
            var frags = groups[key].OrderBy(f => f.StartVcn).ToList();
            if (frags.Count == 1) { result.Add(frags[0]); continue; }
            var first = frags[0];
            var merged = new NtfsAttribute
            {
                Type = first.Type, Name = first.Name, Id = first.Id, Flags = first.Flags, NonResident = true, StartVcn = 0, LastVcn = frags[^1].LastVcn,
                AllocatedSize = first.AllocatedSize, RealSize = first.RealSize, InitializedSize = first.InitializedSize, CompressionUnit = first.CompressionUnit, OwnerRecord = rec.Number
            };
            foreach (var f in frags) merged.Runs.AddRange(f.Runs);
            result.Add(merged);
        }
        // Attributes that live in the base record but were not listed (should not happen, but be forgiving).
        foreach (var a in rec.Attributes) if (a.Type != AttrType.AttributeList && !seen.Contains(a) && !result.Any(x => x.Type == a.Type && x.Name == a.Name && x.NonResident == a.NonResident && x.Id == a.Id)) result.Add(a);
        return result;
    }

    public NtfsAttribute? FindAttribute(MftRecord rec, uint type, string name = "")
        => GetLogicalAttributes(rec).FirstOrDefault(a => a.Type == type && string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

    public NtfsStream OpenAttribute(NtfsAttribute a) => new(this, a);

    /// <summary>$FILE_NAME attributes of a record, following the attribute list when the base record has none.</summary>
    public List<NtfsFileName> GetFileNames(MftRecord rec)
    {
        var names = rec.FileNames().ToList();
        if (names.Count == 0 && rec.First(AttrType.AttributeList) != null)
        {
            try { names = GetLogicalAttributes(rec).Where(a => a.Type == AttrType.FileName && !a.NonResident).Select(a => NtfsFileName.Parse(a.ResidentValue)).Where(f => f != null).ToList()!; }
            catch { }
        }
        return names;
    }

    public NtfsStandardInfo? GetStandardInfo(MftRecord rec)
    {
        var si = rec.StandardInfo();
        if (si == null && rec.First(AttrType.AttributeList) != null)
        {
            try { var a = GetLogicalAttributes(rec).FirstOrDefault(x => x.Type == AttrType.StandardInformation && !x.NonResident); if (a != null) si = NtfsStandardInfo.Parse(a.ResidentValue); }
            catch { }
        }
        return si;
    }

    public static byte[] ReadAll(Stream s)
    {
        var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    // ---- files & directories -------------------------------------------------------------------

    public MftRecord Root => GetRecord(RootRecord);

    /// <summary>Open the (unnamed or named) $DATA stream of a file.</summary>
    public NtfsStream OpenFile(NtfsEntry e, string streamName = "")
    {
        var rec = GetRecord(e.Record);
        var data = FindAttribute(rec, AttrType.Data, streamName);
        if (data == null)
        {
            if (streamName.Length == 0) return new NtfsStream(this, new NtfsAttribute { Type = AttrType.Data, OwnerRecord = e.Record });
            throw new NtfsException($"Stream '{streamName}' not found on {e.Name}.");
        }
        return new NtfsStream(this, data);
    }

    public NtfsStream OpenFile(long record, string streamName = "")
    {
        var rec = GetRecord(record);
        var data = FindAttribute(rec, AttrType.Data, streamName) ?? throw new NtfsException($"Record {record} has no $DATA stream '{streamName}'.");
        return new NtfsStream(this, data);
    }

    /// <summary>Named and unnamed $DATA streams of a file.</summary>
    public List<(string Name, long Size, NtfsAttribute Attr)> ListStreams(long record)
    {
        var rec = GetRecord(record);
        return GetLogicalAttributes(rec).Where(a => a.Type == AttrType.Data).Select(a => (a.Name, a.Length, a)).ToList();
    }

    /// <summary>Build an entry from the MFT record itself (used by the MFT scanner and for path lookups).</summary>
    public NtfsEntry EntryFromRecord(MftRecord rec, NtfsFileName? fn = null, string? path = null)
    {
        fn ??= BestName(GetFileNames(rec));
        var si = GetStandardInfo(rec);
        long size = 0, alloc = 0;
        NtfsFileAttributes attrs = si?.Flags ?? fn?.Flags ?? 0;
        if (rec.IsDirectory) attrs |= NtfsFileAttributes.Directory;
        uint reparse = fn?.ReparseTag ?? 0;
        if (!rec.IsDirectory)
        {
            try
            {
                var data = FindAttribute(rec, AttrType.Data);
                if (data != null)
                {
                    size = data.Length; alloc = data.NonResident ? data.AllocatedSize : data.ResidentValue.Length;
                    if (data.IsCompressed) attrs |= NtfsFileAttributes.Compressed;
                    if (data.IsSparse) attrs |= NtfsFileAttributes.SparseFile;
                    if (data.IsEncrypted) attrs |= NtfsFileAttributes.Encrypted;
                }
            }
            catch { }
        }
        if (reparse == 0 && (attrs & NtfsFileAttributes.ReparsePoint) != 0)
        {
            var rp = rec.First(AttrType.ReparsePoint);
            if (rp is { NonResident: false } && rp.ResidentValue.Length >= 4) reparse = Bin.U32(rp.ResidentValue, 0);
        }
        return new NtfsEntry
        {
            Record = rec.Number, Sequence = rec.Sequence, Parent = fn?.ParentRecord ?? -1, Name = fn?.Name ?? $"<record {rec.Number}>", Namespace = fn?.Namespace ?? 1,
            IsDirectory = rec.IsDirectory, Size = size, AllocatedSize = alloc, Created = si?.Created ?? fn?.Created ?? DateTime.MinValue, Modified = si?.Modified ?? fn?.Modified ?? DateTime.MinValue,
            Accessed = si?.Accessed ?? fn?.Accessed ?? DateTime.MinValue, MftModified = si?.MftModified ?? DateTime.MinValue, Attributes = attrs, ReparseTag = reparse, IsDeleted = !rec.InUse,
            Path = path ?? ""
        };
    }

    public static NtfsFileName? BestName(IEnumerable<NtfsFileName> names)
    {
        NtfsFileName? best = null;
        foreach (var n in names)
        {
            if (best == null || Rank(n.Namespace) > Rank(best.Namespace)) best = n;
        }
        return best;
        static int Rank(byte ns) => ns switch { 1 => 4, 3 => 3, 0 => 2, 2 => 1, _ => 0 };
    }

    /// <summary>List a directory using its $I30 index (fast, exact, but needs healthy index blocks).</summary>
    public List<NtfsEntry> ListDirectory(long dirRecord, string? dirPath = null, bool includeSlack = false)
    {
        var rec = GetRecord(dirRecord);
        var attrs = GetLogicalAttributes(rec);
        var root = attrs.FirstOrDefault(a => a.Type == AttrType.IndexRoot && a.Name.Equals("$I30", StringComparison.OrdinalIgnoreCase));
        if (root == null) return new List<NtfsEntry>();
        var alloc = attrs.FirstOrDefault(a => a.Type == AttrType.IndexAllocation && a.Name.Equals("$I30", StringComparison.OrdinalIgnoreCase));
        var bitmap = attrs.FirstOrDefault(a => a.Type == AttrType.Bitmap && a.Name.Equals("$I30", StringComparison.OrdinalIgnoreCase));
        var raw = new List<(ulong Ref, NtfsFileName Fn)>();
        var rv = root.ResidentValue;
        int blockSize = IndexBlockSize;
        if (rv.Length >= 32)
        {
            int ibs = (int)Bin.U32(rv, 8);
            if (ibs >= 512 && ibs <= 65536 && (ibs & (ibs - 1)) == 0) blockSize = ibs;
            int entriesOff = (int)Bin.U32(rv, 16), total = (int)Bin.U32(rv, 20);
            if (entriesOff >= 16 && total <= rv.Length - 16 && total > entriesOff) ParseIndexEntries(rv.AsSpan(16 + entriesOff, total - entriesOff), raw);
            else Problems.Add($"Directory {dirRecord}: $INDEX_ROOT header invalid.");
        }
        if (alloc != null)
        {
            byte[]? bits = null;
            if (bitmap != null)
            {
                try { bits = bitmap.NonResident ? ReadAll(OpenAttribute(bitmap)) : bitmap.ResidentValue; } catch { bits = null; }
            }
            using var s = OpenAttribute(alloc);
            long blocks = (alloc.RealSize + blockSize - 1) / blockSize;
            var buf = new byte[blockSize];
            for (long i = 0; i < blocks; i++)
            {
                bool inUse = bits == null || (i / 8 < bits.Length && (bits[i / 8] & (1 << (int)(i % 8))) != 0);
                if (!inUse && !includeSlack) continue;
                s.Position = i * blockSize;
                int got = 0;
                try { while (got < blockSize) { int r = s.Read(buf.AsSpan(got)); if (r <= 0) break; got += r; } }
                catch (Exception ex) { Problems.Add($"Directory {dirRecord}: index block {i} unreadable ({ex.Message})."); continue; }
                if (got < blockSize) continue;
                if (!(buf[0] == 'I' && buf[1] == 'N' && buf[2] == 'D' && buf[3] == 'X')) { if (inUse) Problems.Add($"Directory {dirRecord}: index block {i} has no INDX signature."); continue; }
                var span = buf.AsSpan();
                var probs = new List<string>();
                MftRecord.ApplyFixups(span, Boot.BytesPerSector, probs);
                int entriesOff = (int)Bin.U32(span, 0x18), total = (int)Bin.U32(span, 0x1C);
                if (entriesOff < 0 || total < entriesOff || 0x18 + total > blockSize) { Problems.Add($"Directory {dirRecord}: index block {i} header invalid."); continue; }
                ParseIndexEntries(span.Slice(0x18 + entriesOff, total - entriesOff), raw);
            }
        }
        return Dedupe(raw, dirRecord, dirPath);
    }

    private static void ParseIndexEntries(ReadOnlySpan<byte> b, List<(ulong, NtfsFileName)> into)
    {
        int o = 0;
        while (o + 16 <= b.Length)
        {
            ulong fref = Bin.U64(b, o);
            int entryLen = Bin.U16(b, o + 8), keyLen = Bin.U16(b, o + 10), flags = Bin.U16(b, o + 12);
            if (entryLen < 16 || o + entryLen > b.Length) break;
            if ((flags & 2) != 0) break; // last entry (no key)
            if (keyLen >= 0x42 && 16 + keyLen <= entryLen)
            {
                var fn = NtfsFileName.Parse(b.Slice(o + 16, keyLen));
                if (fn != null) into.Add((fref, fn));
            }
            o += entryLen;
        }
    }

    private List<NtfsEntry> Dedupe(List<(ulong Ref, NtfsFileName Fn)> raw, long parent, string? dirPath)
    {
        var byRec = new Dictionary<long, List<(ulong Ref, NtfsFileName Fn)>>();
        foreach (var r in raw)
        {
            long n = (long)(r.Ref & 0xFFFFFFFFFFFFUL);
            if (!byRec.TryGetValue(n, out var l)) byRec[n] = l = new();
            l.Add(r);
        }
        var result = new List<NtfsEntry>();
        foreach (var (n, list) in byRec)
        {
            var nonDos = list.Where(x => x.Fn.Namespace != 2).ToList();
            var keep = nonDos.Count > 0 ? nonDos : list;
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (fref, fn) in keep)
            {
                if (!seenNames.Add(fn.Name)) continue;
                bool dir = (fn.Flags & NtfsFileAttributes.DirectoryIndex) != 0 || (fn.Flags & NtfsFileAttributes.Directory) != 0;
                var attrs = fn.Flags & ~NtfsFileAttributes.DirectoryIndex;
                if (dir) attrs |= NtfsFileAttributes.Directory;
                result.Add(new NtfsEntry
                {
                    Record = n, Sequence = (ushort)(fref >> 48), Parent = parent, Name = fn.Name, Namespace = fn.Namespace, IsDirectory = dir, Size = fn.RealSize, AllocatedSize = fn.AllocatedSize,
                    Created = fn.Created, Modified = fn.Modified, Accessed = fn.Accessed, MftModified = fn.MftModified, Attributes = attrs, ReparseTag = fn.ReparseTag,
                    Path = dirPath == null ? "" : (dirPath.Length == 0 ? fn.Name : dirPath.TrimEnd('\\') + "\\" + fn.Name)
                });
            }
        }
        result.Sort((a, b) => a.IsDirectory != b.IsDirectory ? (a.IsDirectory ? -1 : 1) : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    /// <summary>Resolve a backslash/slash separated path from the root (case-insensitive). Returns null if not found.</summary>
    public NtfsEntry? Resolve(string path)
    {
        var parts = path.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        var cur = EntryFromRecord(Root, new NtfsFileName { Name = "", ParentRecord = RootRecord, Namespace = 1 }, "");
        cur = new NtfsEntry { Record = RootRecord, Sequence = cur.Sequence, Parent = RootRecord, Name = "", IsDirectory = true, Attributes = NtfsFileAttributes.Directory, Path = "" };
        string curPath = "";
        foreach (var p in parts)
        {
            if (!cur.IsDirectory) return null;
            var next = ListDirectory(cur.Record, curPath).FirstOrDefault(e => string.Equals(e.Name, p, StringComparison.OrdinalIgnoreCase));
            if (next == null) return null;
            cur = next;
            curPath = next.Path;
        }
        return cur;
    }

    public NtfsEntry RootEntry => new() { Record = RootRecord, Parent = RootRecord, Name = "", IsDirectory = true, Attributes = NtfsFileAttributes.Directory, Path = "" };

    public void Dispose() { }
}
