using Nova4Me2.Core.Util;

namespace Nova4Me2.Core.Ntfs;

/// <summary>Parsed NTFS boot sector ($Boot, first sector of the volume). Also used for the backup copy at the end of the volume.</summary>
public sealed class BootSector
{
    public byte[] Raw { get; private init; } = Array.Empty<byte>();
    public int BytesPerSector { get; private init; }
    public int SectorsPerCluster { get; private init; }
    public int BytesPerCluster => BytesPerSector * SectorsPerCluster;
    public long TotalSectors { get; private init; }
    public long MftLcn { get; private init; }
    public long MftMirrLcn { get; private init; }
    public int MftRecordSize { get; private init; }
    public int IndexBlockSize { get; private init; }
    public ulong VolumeSerial { get; private init; }
    public byte MediaDescriptor { get; private init; }
    public ushort SectorsPerTrack { get; private init; }
    public ushort Heads { get; private init; }
    public uint HiddenSectors { get; private init; }
    public long TotalClusters => TotalSectors / SectorsPerCluster;
    public long VolumeBytes => TotalSectors * BytesPerSector;
    public bool HasBootCode { get; private init; }

    public static BootSector? TryParse(byte[] s, int deviceSectorSize = 512)
    {
        var problems = Validate(s, deviceSectorSize);
        return problems.Count == 0 ? Parse(s) : null;
    }

    /// <summary>Returns a list of things wrong with this sector as an NTFS boot sector (empty = valid).</summary>
    public static List<string> Validate(byte[] s, int deviceSectorSize = 512)
    {
        var p = new List<string>();
        if (s.Length < 512) { p.Add("sector too short"); return p; }
        if (!(s[3] == 'N' && s[4] == 'T' && s[5] == 'F' && s[6] == 'S' && s[7] == ' ')) p.Add("OEM ID is not \"NTFS    \"");
        int bps = Bin.U16(s, 0x0B);
        if (bps < 256 || bps > 4096 || (bps & (bps - 1)) != 0) p.Add($"bytes per sector {bps} is not a power of two in 256..4096");
        int spcRaw = s[0x0D];
        int spc = spcRaw <= 0x80 ? spcRaw : 1 << (256 - spcRaw);
        if (spc == 0 || spc > 4096 || (spc & (spc - 1)) != 0) p.Add($"sectors per cluster {spcRaw} invalid");
        if (Bin.U16(s, 0x0E) != 0) p.Add("reserved sectors must be 0");
        if (s[0x10] != 0 || Bin.U16(s, 0x11) != 0 || Bin.U16(s, 0x13) != 0) p.Add("FAT fields must be 0");
        if (Bin.U16(s, 0x16) != 0) p.Add("sectors per FAT must be 0");
        if (Bin.U32(s, 0x20) != 0) p.Add("large sectors must be 0");
        long total = Bin.I64(s, 0x28);
        if (total <= 0) p.Add("total sectors is 0");
        long mft = Bin.I64(s, 0x30), mirr = Bin.I64(s, 0x38);
        if (spc > 0 && total > 0 && (mft < 0 || mft >= total / spc)) p.Add("$MFT cluster outside volume");
        if (spc > 0 && total > 0 && (mirr < 0 || mirr >= total / spc)) p.Add("$MFTMirr cluster outside volume");
        sbyte cpr = (sbyte)s[0x40];
        if (!(cpr is >= -31 and <= 0 or > 0 and <= 64)) p.Add("clusters per MFT record invalid");
        if (s[510] != 0x55 || s[511] != 0xAA) p.Add("missing 55AA signature");
        return p;
    }

    public static BootSector Parse(byte[] s)
    {
        int bps = Bin.U16(s, 0x0B);
        int spcRaw = s[0x0D];
        int spc = spcRaw <= 0x80 ? spcRaw : 1 << (256 - spcRaw);
        int bpc = bps * spc;
        sbyte cpr = (sbyte)s[0x40];
        int rec = cpr > 0 ? cpr * bpc : 1 << -cpr;
        sbyte cpi = (sbyte)s[0x44];
        int idx = cpi > 0 ? cpi * bpc : 1 << -cpi;
        bool code = false;
        for (int i = 0x54; i < 510; i++) if (s[i] != 0) { code = true; break; }
        return new BootSector
        {
            Raw = (byte[])s.Clone(), BytesPerSector = bps, SectorsPerCluster = spc, TotalSectors = Bin.I64(s, 0x28), MftLcn = Bin.I64(s, 0x30), MftMirrLcn = Bin.I64(s, 0x38),
            MftRecordSize = rec, IndexBlockSize = idx, VolumeSerial = Bin.U64(s, 0x48), MediaDescriptor = s[0x15], SectorsPerTrack = Bin.U16(s, 0x18), Heads = Bin.U16(s, 0x1A),
            HiddenSectors = Bin.U32(s, 0x1C), HasBootCode = code
        };
    }
}
