using Nova4Me2.Core.Devices;
using Nova4Me2.Core.Util;

namespace Nova4Me2.Core.Partitions;

public enum PartitionScheme { None, Mbr, Gpt }

public sealed class PartitionInfo
{
    public int Index { get; init; }
    public long StartOffset { get; init; }
    public long Length { get; init; }
    public long EndOffset => StartOffset + Length;
    public string TypeName { get; init; } = "";
    public byte MbrType { get; init; }
    public Guid TypeGuid { get; init; }
    public Guid UniqueGuid { get; init; }
    public string Name { get; init; } = "";
    public ulong Attributes { get; init; }
    public bool Bootable { get; init; }
    public override string ToString() => $"#{Index} {TypeName} {Name} @ {StartOffset} ({Format.Bytes(Length)})";
}

public sealed class PartitionTableInfo
{
    public PartitionScheme Scheme { get; init; }
    public List<PartitionInfo> Partitions { get; init; } = new();
    public List<string> Problems { get; init; } = new();
    public Guid DiskGuid { get; init; }
    public uint MbrSignature { get; init; }
    public bool PrimaryGptValid { get; init; }
    public bool BackupGptValid { get; init; }
    public bool UsedBackupGpt { get; init; }
}

public static class PartitionTable
{
    public static readonly Guid GptBasicData = new("EBD0A0A2-B9E5-4433-87C0-68B6B72699C7");
    public static readonly Guid GptEfiSystem = new("C12A7328-F81F-11D2-BA4B-00A0C93EC93B");
    public static readonly Guid GptMsReserved = new("E3C9E316-0B5C-4DB8-817D-F92DF00215AE");
    public static readonly Guid GptWinRecovery = new("DE94BBA4-06D1-4D40-A16A-BFD50179D6AC");
    public static readonly Guid GptLdmMeta = new("5808C8AA-7E8F-42E0-85D2-E1E90434CFB3");
    public static readonly Guid GptLdmData = new("AF9B60A0-1431-4F62-BC68-3311714A69AD");
    public static readonly Guid GptLinuxData = new("0FC63DAF-8483-4772-8E79-3D69D8477DE4");

    public static string GptTypeName(Guid g)
    {
        if (g == GptBasicData) return "Basic data";
        if (g == GptEfiSystem) return "EFI System";
        if (g == GptMsReserved) return "Microsoft Reserved";
        if (g == GptWinRecovery) return "Windows Recovery";
        if (g == GptLdmMeta) return "LDM metadata";
        if (g == GptLdmData) return "LDM data";
        if (g == GptLinuxData) return "Linux data";
        if (g == Guid.Empty) return "Unused";
        return g.ToString();
    }

    public static string MbrTypeName(byte t) => t switch
    {
        0x00 => "Empty", 0x01 => "FAT12", 0x04 => "FAT16 <32M", 0x05 => "Extended", 0x06 => "FAT16", 0x07 => "NTFS/exFAT",
        0x0B => "FAT32", 0x0C => "FAT32 LBA", 0x0E => "FAT16 LBA", 0x0F => "Extended LBA", 0x27 => "Windows RE", 0x42 => "LDM",
        0x82 => "Linux swap", 0x83 => "Linux", 0x8E => "Linux LVM", 0xA5 => "FreeBSD", 0xAF => "HFS+", 0xEE => "GPT protective", 0xEF => "EFI System",
        _ => $"Type 0x{t:X2}"
    };

    public static PartitionTableInfo Read(IBlockDevice dev)
    {
        int ss = dev.SectorSize;
        var problems = new List<string>();
        byte[] mbr;
        try { mbr = dev.ReadBytes(0, ss); }
        catch (Exception ex)
        {
            problems.Add($"Sector 0 unreadable: {ex.Message}");
            return TryGpt(dev, problems, 0) ?? new PartitionTableInfo { Scheme = PartitionScheme.None, Problems = problems };
        }
        bool sig = mbr[510] == 0x55 && mbr[511] == 0xAA;
        uint mbrSig = Bin.U32(mbr, 440);
        var mbrParts = new List<PartitionInfo>();
        bool protective = false;
        if (sig)
        {
            for (int i = 0; i < 4; i++)
            {
                int o = 446 + i * 16;
                byte type = mbr[o + 4];
                uint lba = Bin.U32(mbr, o + 8), cnt = Bin.U32(mbr, o + 12);
                if (type == 0 || cnt == 0) continue;
                if (type == 0xEE) protective = true;
                mbrParts.Add(new PartitionInfo { Index = i + 1, StartOffset = (long)lba * ss, Length = (long)cnt * ss, MbrType = type, TypeName = MbrTypeName(type), Bootable = mbr[o] == 0x80 });
            }
        }
        else problems.Add("Sector 0 has no 55AA boot signature (MBR/protective MBR missing or damaged).");

        var gpt = TryGpt(dev, problems, mbrSig);
        if (gpt != null)
        {
            if (!protective && sig && mbrParts.Count > 0) problems.Add("A GPT is present but the MBR is not a protective MBR; treating the disk as GPT.");
            return gpt;
        }
        if (protective) problems.Add("Protective MBR says GPT, but neither the primary nor the backup GPT header is valid.");
        // Extended partitions (rare on modern Windows disks, but cheap to support).
        var all = new List<PartitionInfo>();
        foreach (var p in mbrParts)
        {
            if (p.MbrType is 0x05 or 0x0F)
            {
                all.Add(p);
                all.AddRange(ReadExtended(dev, p.StartOffset, all.Count + 1, problems));
            }
            else all.Add(p);
        }
        return new PartitionTableInfo { Scheme = mbrParts.Count > 0 ? PartitionScheme.Mbr : PartitionScheme.None, Partitions = all, Problems = problems, MbrSignature = mbrSig };
    }

    private static IEnumerable<PartitionInfo> ReadExtended(IBlockDevice dev, long extStart, int nextIndex, List<string> problems)
    {
        long cur = extStart;
        int guard = 0;
        var list = new List<PartitionInfo>();
        while (guard++ < 64)
        {
            byte[] ebr;
            try { ebr = dev.ReadBytes(cur, dev.SectorSize); } catch { problems.Add($"EBR at {cur} unreadable."); break; }
            if (ebr[510] != 0x55 || ebr[511] != 0xAA) break;
            byte type = ebr[446 + 4];
            uint lba = Bin.U32(ebr, 446 + 8), cnt = Bin.U32(ebr, 446 + 12);
            if (type != 0 && cnt != 0)
                list.Add(new PartitionInfo { Index = nextIndex++, StartOffset = cur + (long)lba * dev.SectorSize, Length = (long)cnt * dev.SectorSize, MbrType = type, TypeName = MbrTypeName(type) });
            byte t2 = ebr[462 + 4];
            uint lba2 = Bin.U32(ebr, 462 + 8);
            if (t2 == 0 || lba2 == 0) break;
            cur = extStart + (long)lba2 * dev.SectorSize;
        }
        return list;
    }

    private static PartitionTableInfo? TryGpt(IBlockDevice dev, List<string> problems, uint mbrSig)
    {
        int ss = dev.SectorSize;
        long lastLba = dev.Length / ss - 1;
        GptHeader? primary = null, backup = null;
        try { primary = GptHeader.Parse(dev.ReadBytes(ss, ss), 1); } catch (Exception ex) { problems.Add($"Primary GPT header unreadable: {ex.Message}"); }
        if (primary is { Valid: false }) problems.Add($"Primary GPT header invalid: {primary.Problem}");
        if (lastLba > 1)
        {
            try { backup = GptHeader.Parse(dev.ReadBytes(lastLba * ss, ss), lastLba); } catch (Exception ex) { problems.Add($"Backup GPT header unreadable: {ex.Message}"); }
            if (backup is { Valid: false } && primary is { Valid: true }) problems.Add($"Backup GPT header invalid: {backup.Problem}");
        }
        var use = primary is { Valid: true } ? primary : backup is { Valid: true } ? backup : null;
        if (use == null) return null;
        bool usedBackup = use == backup && !(primary?.Valid ?? false);
        if (usedBackup) problems.Add("Primary GPT damaged; partition list recovered from the backup GPT at the end of the disk.");
        var parts = new List<PartitionInfo>();
        try
        {
            long entriesOff = (long)use.EntriesLba * ss;
            int total = (int)(use.EntryCount * use.EntrySize);
            var bytes = dev.ReadBytes(entriesOff, (int)Bin.AlignUp(total, ss));
            uint crc = Crc32.Compute(bytes.AsSpan(0, total));
            if (crc != use.EntriesCrc) problems.Add($"GPT partition entry array CRC mismatch (0x{crc:X8} vs 0x{use.EntriesCrc:X8}); entries may be corrupted.");
            for (int i = 0; i < use.EntryCount; i++)
            {
                int o = (int)(i * use.EntrySize);
                var type = new Guid(bytes.AsSpan(o, 16));
                if (type == Guid.Empty) continue;
                var uniq = new Guid(bytes.AsSpan(o + 16, 16));
                ulong first = Bin.U64(bytes, o + 32), last = Bin.U64(bytes, o + 40), attrs = Bin.U64(bytes, o + 48);
                string name = Bin.Utf16(bytes, o + 56, 36).TrimEnd('\0');
                if (last < first) { problems.Add($"GPT entry {i + 1} has end before start."); continue; }
                parts.Add(new PartitionInfo
                {
                    Index = i + 1, StartOffset = (long)first * ss, Length = (long)(last - first + 1) * ss, TypeGuid = type, UniqueGuid = uniq,
                    TypeName = GptTypeName(type), Name = name, Attributes = attrs
                });
            }
        }
        catch (Exception ex) { problems.Add($"GPT entries unreadable: {ex.Message}"); }
        return new PartitionTableInfo
        {
            Scheme = PartitionScheme.Gpt, Partitions = parts, Problems = problems, DiskGuid = use.DiskGuid, MbrSignature = mbrSig,
            PrimaryGptValid = primary?.Valid ?? false, BackupGptValid = backup?.Valid ?? false, UsedBackupGpt = usedBackup
        };
    }
}

public sealed class GptHeader
{
    public bool Valid { get; init; }
    public string Problem { get; init; } = "";
    public ulong MyLba { get; init; }
    public ulong AlternateLba { get; init; }
    public ulong FirstUsableLba { get; init; }
    public ulong LastUsableLba { get; init; }
    public Guid DiskGuid { get; init; }
    public ulong EntriesLba { get; init; }
    public uint EntryCount { get; init; }
    public uint EntrySize { get; init; }
    public uint EntriesCrc { get; init; }
    public uint HeaderCrc { get; init; }
    public byte[] Raw { get; init; } = Array.Empty<byte>();

    public static GptHeader Parse(byte[] s, long expectedLba)
    {
        if (Bin.U64(s, 0) != 0x5452415020494645UL) return new GptHeader { Valid = false, Problem = "signature 'EFI PART' missing", Raw = s };
        uint hsize = Bin.U32(s, 12);
        if (hsize < 92 || hsize > s.Length) return new GptHeader { Valid = false, Problem = $"bad header size {hsize}", Raw = s };
        uint storedCrc = Bin.U32(s, 16);
        var copy = (byte[])s.Clone();
        Bin.PutU32(copy, 16, 0);
        uint crc = Crc32.Compute(copy.AsSpan(0, (int)hsize));
        ulong my = Bin.U64(s, 24);
        var h = new GptHeader
        {
            Valid = crc == storedCrc && my == (ulong)expectedLba, Problem = crc != storedCrc ? $"header CRC mismatch (0x{crc:X8} vs 0x{storedCrc:X8})" : my != (ulong)expectedLba ? $"MyLBA {my} != {expectedLba}" : "",
            MyLba = my, AlternateLba = Bin.U64(s, 32), FirstUsableLba = Bin.U64(s, 40), LastUsableLba = Bin.U64(s, 48), DiskGuid = new Guid(s.AsSpan(56, 16)),
            EntriesLba = Bin.U64(s, 72), EntryCount = Bin.U32(s, 80), EntrySize = Bin.U32(s, 84), EntriesCrc = Bin.U32(s, 88), HeaderCrc = storedCrc, Raw = s
        };
        if (h.Valid && (h.EntrySize < 128 || h.EntryCount == 0 || h.EntryCount > 1024)) return new GptHeader { Valid = false, Problem = "implausible entry size/count", Raw = s };
        return h;
    }
}
