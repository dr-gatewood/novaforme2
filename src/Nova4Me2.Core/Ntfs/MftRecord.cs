using Nova4Me2.Core.Util;

namespace Nova4Me2.Core.Ntfs;

/// <summary>A parsed FILE record from the $MFT (fixups applied).</summary>
public sealed class MftRecord
{
    public long Number { get; init; }
    public ushort Sequence { get; init; }
    public ushort LinkCount { get; init; }
    public ushort Flags { get; init; }
    public long BaseRecord { get; init; }
    public ushort BaseSequence { get; init; }
    public uint UsedSize { get; init; }
    public uint AllocatedSize { get; init; }
    public ulong LogSequenceNumber { get; init; }
    public List<NtfsAttribute> Attributes { get; } = new();
    public List<string> Problems { get; } = new();
    public byte[] Raw { get; init; } = Array.Empty<byte>();

    public bool InUse => (Flags & 1) != 0;
    public bool IsDirectory => (Flags & 2) != 0;
    public bool IsExtension => BaseRecord != 0;
    public bool IsValid => Problems.Count == 0 || Attributes.Count > 0;

    public static bool HasFileSignature(ReadOnlySpan<byte> b) => b.Length >= 4 && b[0] == 'F' && b[1] == 'I' && b[2] == 'L' && b[3] == 'E';
    public static bool HasBaadSignature(ReadOnlySpan<byte> b) => b.Length >= 4 && b[0] == 'B' && b[1] == 'A' && b[2] == 'A' && b[3] == 'D';

    /// <summary>Apply the update sequence array in place. Returns false (and lists the problem) if the sequence numbers disagree (torn write).</summary>
    public static bool ApplyFixups(Span<byte> b, int sectorSize, List<string>? problems = null)
    {
        int usaOff = Bin.U16(b, 4), usaCount = Bin.U16(b, 6);
        if (usaOff < 8 || usaCount < 2 || usaOff + usaCount * 2 > b.Length) { problems?.Add("update sequence array out of range"); return false; }
        ushort usn = Bin.U16(b, usaOff);
        bool ok = true;
        for (int i = 1; i < usaCount; i++)
        {
            int pos = i * sectorSize - 2;
            if (pos + 2 > b.Length) break;
            if (Bin.U16(b, pos) != usn) { ok = false; problems?.Add($"fixup mismatch in sector {i - 1} (torn write / corruption)"); }
            Bin.PutU16(b, pos, Bin.U16(b, usaOff + i * 2));
        }
        return ok;
    }

    public static MftRecord Parse(byte[] raw, long number, int sectorSize, long totalClusters)
    {
        var problems = new List<string>();
        var b = (byte[])raw.Clone();
        if (!HasFileSignature(b))
        {
            problems.Add(HasBaadSignature(b) ? "record marked BAAD by NTFS (multi-sector transfer failure)" : "missing FILE signature");
            return new MftRecord { Number = number, Raw = raw, Problems = { problems[0] } };
        }
        ApplyFixups(b, sectorSize, problems);
        ulong baseRef = Bin.U64(b, 0x20);
        var rec = new MftRecord
        {
            Number = number, Sequence = Bin.U16(b, 0x10), LinkCount = Bin.U16(b, 0x12), Flags = Bin.U16(b, 0x16), UsedSize = Bin.U32(b, 0x18), AllocatedSize = Bin.U32(b, 0x1C),
            BaseRecord = (long)(baseRef & 0xFFFFFFFFFFFFUL), BaseSequence = (ushort)(baseRef >> 48), LogSequenceNumber = Bin.U64(b, 8), Raw = b
        };
        foreach (var p in problems) rec.Problems.Add(p);
        int off = Bin.U16(b, 0x14);
        int limit = (int)Math.Min(rec.UsedSize > 0 ? rec.UsedSize : (uint)b.Length, (uint)b.Length);
        if (off < 0x30 || off >= limit) { rec.Problems.Add("first attribute offset invalid"); return rec; }
        while (off + 8 <= limit)
        {
            uint type = Bin.U32(b, off);
            if (type == AttrType.End) break;
            int len = (int)Bin.U32(b, off + 4);
            if (len < 24 || off + len > limit || (len & 7) != 0) { rec.Problems.Add($"attribute at 0x{off:X} has invalid length {len}"); break; }
            try
            {
                var a = ParseAttribute(b.AsSpan(off, len), number, totalClusters, rec.Problems);
                if (a != null) rec.Attributes.Add(a);
            }
            catch (Exception ex) { rec.Problems.Add($"attribute at 0x{off:X}: {ex.Message}"); }
            off += len;
        }
        return rec;
    }

    private static NtfsAttribute? ParseAttribute(ReadOnlySpan<byte> a, long owner, long totalClusters, List<string> problems)
    {
        var at = new NtfsAttribute { Type = Bin.U32(a, 0), NonResident = a[8] != 0, Flags = Bin.U16(a, 0x0C), Id = Bin.U16(a, 0x0E), OwnerRecord = owner };
        int nameLen = a[9], nameOff = Bin.U16(a, 0x0A);
        if (nameLen > 0)
        {
            if (nameOff + nameLen * 2 > a.Length) { problems.Add("attribute name out of range"); return null; }
            at.Name = Bin.Utf16(a, nameOff, nameLen);
        }
        if (!at.NonResident)
        {
            int vlen = (int)Bin.U32(a, 0x10), voff = Bin.U16(a, 0x14);
            if (voff + vlen > a.Length || vlen < 0) { problems.Add($"{at.TypeName} resident value out of range"); vlen = Math.Max(0, Math.Min(vlen, a.Length - voff)); }
            at.ResidentValue = a.Slice(voff, vlen).ToArray();
            return at;
        }
        if (a.Length < 0x40) { problems.Add($"{at.TypeName} non-resident header truncated"); return null; }
        at.StartVcn = Bin.I64(a, 0x10);
        at.LastVcn = Bin.I64(a, 0x18);
        int runOff = Bin.U16(a, 0x20);
        at.CompressionUnit = a[0x22];
        at.AllocatedSize = Bin.I64(a, 0x28);
        at.RealSize = Bin.I64(a, 0x30);
        at.InitializedSize = Bin.I64(a, 0x38);
        if (runOff >= a.Length) { problems.Add($"{at.TypeName} run list offset out of range"); return at; }
        at.RunListTruncated = !DecodeRuns(a[runOff..], at.StartVcn, totalClusters, at.Runs, out string? err);
        if (err != null) problems.Add($"{at.TypeName}: {err}");
        return at;
    }

    /// <summary>Decode a mapping pairs array. Returns false if the list ended abnormally.</summary>
    public static bool DecodeRuns(ReadOnlySpan<byte> r, long startVcn, long totalClusters, List<DataRun> runs, out string? error)
    {
        error = null;
        long vcn = startVcn, lcn = 0;
        int i = 0;
        while (i < r.Length)
        {
            byte h = r[i++];
            if (h == 0) return true;
            int lenSize = h & 0xF, offSize = h >> 4;
            if (lenSize == 0 || lenSize > 8 || offSize > 8 || i + lenSize + offSize > r.Length) { error = "malformed run header"; return false; }
            long count = 0;
            for (int k = 0; k < lenSize; k++) count |= (long)r[i + k] << (8 * k);
            i += lenSize;
            if (count <= 0) { error = "run with zero length"; return false; }
            if (offSize == 0)
            {
                runs.Add(new DataRun { Vcn = vcn, Lcn = -1, Count = count });
            }
            else
            {
                long delta = 0;
                for (int k = 0; k < offSize; k++) delta |= (long)r[i + k] << (8 * k);
                if ((r[i + offSize - 1] & 0x80) != 0) delta -= 1L << (8 * offSize); // sign-extend
                i += offSize;
                lcn += delta;
                if (lcn < 0 || (totalClusters > 0 && lcn + count > totalClusters)) { error = $"run points outside the volume (LCN {lcn}, count {count})"; return false; }
                runs.Add(new DataRun { Vcn = vcn, Lcn = lcn, Count = count });
            }
            vcn += count;
        }
        error = "run list not terminated";
        return false;
    }

    public NtfsAttribute? First(uint type, string? name = null) => Attributes.FirstOrDefault(a => a.Type == type && (name == null || string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)));
    public IEnumerable<NtfsFileName> FileNames() => Attributes.Where(a => a.Type == AttrType.FileName && !a.NonResident).Select(a => NtfsFileName.Parse(a.ResidentValue)).Where(f => f != null)!;
    public NtfsStandardInfo? StandardInfo() { var a = First(AttrType.StandardInformation); return a is { NonResident: false } ? NtfsStandardInfo.Parse(a.ResidentValue) : null; }
}
