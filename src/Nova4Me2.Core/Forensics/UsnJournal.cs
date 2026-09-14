using Nova4Me2.Core.Ntfs;
using Nova4Me2.Core.Util;

namespace Nova4Me2.Core.Forensics;

public sealed class UsnRecord
{
    public long Usn { get; init; }
    public DateTime Time { get; init; }
    public long FileRecord { get; init; }
    public ushort FileSequence { get; init; }
    public long ParentRecord { get; init; }
    public uint Reason { get; init; }
    public uint Attributes { get; init; }
    public string FileName { get; init; } = "";
    public string Path { get; set; } = "";
    public string ReasonText => UsnJournal.Reasons(Reason);
}

/// <summary>Reader for the NTFS change journal ($Extend\$UsnJrnl:$J): which files were created/changed/deleted recently.</summary>
public static class UsnJournal
{
    private static readonly (uint Bit, string Name)[] ReasonNames =
    {
        (0x1, "DATA_OVERWRITE"), (0x2, "DATA_EXTEND"), (0x4, "DATA_TRUNCATION"), (0x10, "NAMED_DATA_OVERWRITE"), (0x20, "NAMED_DATA_EXTEND"), (0x40, "NAMED_DATA_TRUNCATION"),
        (0x100, "FILE_CREATE"), (0x200, "FILE_DELETE"), (0x400, "EA_CHANGE"), (0x800, "SECURITY_CHANGE"), (0x1000, "RENAME_OLD_NAME"), (0x2000, "RENAME_NEW_NAME"), (0x4000, "INDEXABLE_CHANGE"),
        (0x8000, "BASIC_INFO_CHANGE"), (0x10000, "HARD_LINK_CHANGE"), (0x20000, "COMPRESSION_CHANGE"), (0x40000, "ENCRYPTION_CHANGE"), (0x80000, "OBJECT_ID_CHANGE"), (0x100000, "REPARSE_POINT_CHANGE"),
        (0x200000, "STREAM_CHANGE"), (0x400000, "TRANSACTED_CHANGE"), (0x800000, "INTEGRITY_CHANGE"), (0x80000000, "CLOSE")
    };

    public static string Reasons(uint r) => string.Join("|", ReasonNames.Where(x => (r & x.Bit) != 0).Select(x => x.Name));

    /// <summary>Read all records (newest last). Returns an empty list if the volume has no journal.</summary>
    public static List<UsnRecord> Read(NtfsVolume vol, IProgress<string>? progress = null, CancellationToken ct = default, int maxRecords = 2_000_000)
    {
        var list = new List<UsnRecord>();
        var extend = vol.Resolve(@"$Extend\$UsnJrnl");
        if (extend == null) return list;
        var rec = vol.GetRecord(extend.Record);
        var j = vol.FindAttribute(rec, AttrType.Data, "$J");
        if (j == null) return list;
        int cs = vol.ClusterSize;
        var runs = j.Runs.Where(r => !r.IsSparse).OrderBy(r => r.Vcn).ToList();
        progress?.Report($"$UsnJrnl:$J has {runs.Count} data runs, {Format.Bytes(j.RealSize)} logical size");
        var carry = Array.Empty<byte>();
        long carryPos = 0;
        foreach (var run in runs)
        {
            ct.ThrowIfCancellationRequested();
            long remaining = run.Count, cur = run.Lcn, vcn = run.Vcn;
            while (remaining > 0)
            {
                long n = Math.Min(remaining, (4 << 20) / cs);
                var buf = new byte[n * cs + carry.Length];
                carry.CopyTo(buf, 0);
                try { vol.ReadBytesAtCluster(cur, 0, buf.AsSpan(carry.Length)); } catch { Array.Clear(buf, carry.Length, (int)(n * cs)); }
                long bufPos = carry.Length > 0 ? carryPos : vcn * cs;
                int consumed = Parse(buf, bufPos, list, maxRecords);
                if (list.Count >= maxRecords) return list;
                carry = buf.AsSpan(consumed).ToArray();
                carryPos = bufPos + consumed;
                cur += n; vcn += n; remaining -= n;
            }
        }
        ResolvePaths(vol, list);
        return list;
    }

    private static int Parse(byte[] b, long basePos, List<UsnRecord> list, int max)
    {
        int o = 0;
        while (o + 8 <= b.Length)
        {
            uint len = Bin.U32(b, o);
            if (len == 0) { // padding to the next 4 KiB/page boundary
                int next = (int)Bin.AlignUp(o + 1, 8);
                int skip = o;
                while (skip < b.Length && b[skip] == 0) skip++;
                if (skip >= b.Length) return b.Length;
                o = skip & ~7;
                if (o == skip - (skip % 8) && Bin.U32(b, o) == 0) o = skip; 
                continue;
            }
            if (len < 60 || len > 4096 || (len & 7) != 0) { o += 8; continue; }
            if (o + len > b.Length) return o;
            ushort major = Bin.U16(b, o + 4);
            if (major is not (2 or 3)) { o += 8; continue; }
            int off = major == 2 ? 0 : 16; // v3 uses 128-bit file references
            ulong fref = Bin.U64(b, o + 8), pref = Bin.U64(b, o + 16 + off);
            int nameLen = Bin.U16(b, o + 56 + off * 2), nameOff = Bin.U16(b, o + 58 + off * 2);
            if (nameOff + nameLen > len) { o += (int)len; continue; }
            list.Add(new UsnRecord
            {
                Usn = Bin.I64(b, o + 24 + off * 2), Time = Format.FromFileTime(Bin.U64(b, o + 32 + off * 2)), FileRecord = (long)(fref & 0xFFFFFFFFFFFFUL), FileSequence = (ushort)(fref >> 48),
                ParentRecord = (long)(pref & 0xFFFFFFFFFFFFUL), Reason = Bin.U32(b, o + 40 + off * 2), Attributes = Bin.U32(b, o + 52 + off * 2), FileName = Bin.Utf16(b, o + nameOff, nameLen / 2)
            });
            if (list.Count >= max) return b.Length;
            o += (int)len;
        }
        return o;
    }

    private static void ResolvePaths(NtfsVolume vol, List<UsnRecord> list)
    {
        var cache = new Dictionary<long, string>();
        string PathOf(long record, int depth)
        {
            if (record == NtfsVolume.RootRecord) return "";
            if (depth > 64) return "?";
            if (cache.TryGetValue(record, out var p)) return p;
            string result;
            try
            {
                var rec = vol.GetRecord(record);
                var fn = NtfsVolume.BestName(vol.GetFileNames(rec));
                result = fn == null ? $"<{record}>" : (PathOf(fn.ParentRecord, depth + 1) + "\\" + fn.Name).TrimStart('\\');
            }
            catch { result = $"<{record}>"; }
            cache[record] = result;
            return result;
        }
        foreach (var r in list) r.Path = (PathOf(r.ParentRecord, 0) + "\\" + r.FileName).TrimStart('\\');
    }
}
