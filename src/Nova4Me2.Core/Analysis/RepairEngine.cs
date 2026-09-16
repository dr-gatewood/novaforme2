using System.Text.Json;
using Nova4Me2.Core.Devices;
using Nova4Me2.Core.Ntfs;
using Nova4Me2.Core.Partitions;
using Nova4Me2.Core.Util;

namespace Nova4Me2.Core.Analysis;

public sealed class RepairResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public string? BackupFile { get; init; }
    public List<(long Offset, int Length)> Written { get; init; } = new();
}

public sealed class SectorBackup
{
    public string Source { get; set; } = "";
    public string Action { get; set; } = "";
    public DateTime When { get; set; }
    public List<SectorBackupRange> Ranges { get; set; } = new();
}

public sealed class SectorBackupRange
{
    public long Offset { get; set; }
    public int Length { get; set; }
    public string DataBase64 { get; set; } = "";
}

/// <summary>
/// The few well-understood, reversible on-disk repairs. Every write is preceded by a JSON backup of the exact sectors
/// being replaced so <see cref="Undo"/> can put them back. The device must have been opened writable.
/// </summary>
public static class RepairEngine
{
    public static string DefaultBackupDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nova4Me2", "sector-backups");

    public static RepairResult RestoreBootSectorFromBackup(IBlockDevice dev, NtfsVolumeCandidate c, string? backupDir = null)
    {
        if (!dev.CanWrite) return Fail("Device is opened read-only. Re-open it for repair (writable) first.");
        var bs = c.BootSector;
        long backupOff = c.StartOffset + bs.TotalSectors * bs.BytesPerSector;
        var backup = dev.ReadBytes(backupOff, 512);
        var problems = BootSector.Validate(backup, dev.SectorSize);
        if (problems.Count > 0) return Fail("Backup boot sector is not valid: " + string.Join("; ", problems));
        return WriteWithBackup(dev, "RestoreBootSectorFromBackup", backupDir, (c.StartOffset, Pad(backup, dev.SectorSize, dev, c.StartOffset)));
    }

    public static RepairResult RestoreBackupBootSectorFromPrimary(IBlockDevice dev, NtfsVolumeCandidate c, string? backupDir = null)
    {
        if (!dev.CanWrite) return Fail("Device is opened read-only.");
        var prim = dev.ReadBytes(c.StartOffset, 512);
        var problems = BootSector.Validate(prim, dev.SectorSize);
        if (problems.Count > 0) return Fail("Primary boot sector is not valid: " + string.Join("; ", problems));
        var bs = BootSector.Parse(prim);
        long backupOff = c.StartOffset + bs.TotalSectors * bs.BytesPerSector;
        if (backupOff + dev.SectorSize > dev.Length) return Fail("Backup boot sector location lies beyond the end of the device.");
        return WriteWithBackup(dev, "RestoreBackupBootSectorFromPrimary", backupDir, (backupOff, Pad(prim, dev.SectorSize, dev, backupOff)));
    }

    public static RepairResult RestoreGptFromBackup(IBlockDevice dev, string? backupDir = null)
    {
        if (!dev.CanWrite) return Fail("Device is opened read-only.");
        int ss = dev.SectorSize;
        long lastLba = dev.Length / ss - 1;
        var bh = GptHeader.Parse(dev.ReadBytes(lastLba * ss, ss), lastLba);
        if (!bh.Valid) return Fail("Backup GPT header is not valid either: " + bh.Problem);
        int entriesBytes = (int)(bh.EntryCount * bh.EntrySize);
        var entries = dev.ReadBytes((long)bh.EntriesLba * ss, (int)Bin.AlignUp(entriesBytes, ss));
        if (Crc32.Compute(entries.AsSpan(0, entriesBytes)) != bh.EntriesCrc) return Fail("Backup partition entries fail their CRC; refusing to write them.");
        int entrySectors = (int)Bin.AlignUp(entriesBytes, ss) / ss;
        // Primary header = backup header with swapped LBAs and entries at LBA 2.
        var hdr = (byte[])bh.Raw.Clone();
        uint hsize = Bin.U32(hdr, 12);
        Bin.PutU64(hdr, 24, 1);
        Bin.PutU64(hdr, 32, (ulong)lastLba);
        Bin.PutU64(hdr, 72, 2);
        Bin.PutU32(hdr, 16, 0);
        Bin.PutU32(hdr, 16, Crc32.Compute(hdr.AsSpan(0, (int)hsize)));
        var writes = new List<(long, byte[])> { (ss, hdr), (2L * ss, entries) };
        // Protective MBR if sector 0 lacks one.
        byte[] mbr;
        try { mbr = dev.ReadBytes(0, ss); } catch { mbr = new byte[ss]; }
        bool hasProtective = mbr[510] == 0x55 && mbr[511] == 0xAA && Enumerable.Range(0, 4).Any(i => mbr[446 + i * 16 + 4] == 0xEE);
        if (!hasProtective)
        {
            var p = new byte[ss];
            p[446 + 4] = 0xEE;
            Bin.PutU32(p, 446 + 8, 1);
            Bin.PutU32(p, 446 + 12, (uint)Math.Min(lastLba, 0xFFFFFFFF));
            p[510] = 0x55; p[511] = 0xAA;
            writes.Insert(0, (0, p));
        }
        if (2 + entrySectors > (long)bh.FirstUsableLba) return Fail("Not enough room before the first usable LBA for the entry array.");
        return WriteWithBackup(dev, "RestoreGptFromBackup", backupDir, writes.ToArray());
    }

    public static RepairResult RestoreMftFromMirror(IBlockDevice dev, NtfsVolume vol, string? backupDir = null, bool onlyDamaged = true)
    {
        if (!dev.CanWrite) return Fail("Device is opened read-only.");
        var bs = vol.Boot;
        int rs = vol.RecordSize;
        long mftOff = vol.Offset + bs.MftLcn * vol.ClusterSize;
        long mirrOff = vol.Offset + bs.MftMirrLcn * vol.ClusterSize;
        var writes = new List<(long, byte[])>();
        int n = Math.Max(4, vol.ClusterSize / rs);
        for (int i = 0; i < n; i++)
        {
            var mirr = dev.ReadBytes(mirrOff + (long)i * rs, rs);
            if (!MftRecord.HasFileSignature(mirr)) continue;
            var probs = new List<string>();
            var copy = (byte[])mirr.Clone();
            if (!MftRecord.ApplyFixups(copy, bs.BytesPerSector, probs)) continue; // mirror record itself torn
            byte[]? prim = null;
            try { prim = dev.ReadBytes(mftOff + (long)i * rs, rs); } catch { }
            bool damaged = prim == null || !MftRecord.HasFileSignature(prim) || !MftRecord.ApplyFixups((byte[])prim.Clone(), bs.BytesPerSector, null);
            if (onlyDamaged && !damaged) continue;
            writes.Add((mftOff + (long)i * rs, mirr));
        }
        if (writes.Count == 0) return new RepairResult { Success = true, Message = "Nothing to do: the $MFT system records are already valid (or the mirror has nothing better)." };
        return WriteWithBackup(dev, "RestoreMftFromMirror", backupDir, writes.ToArray());
    }

    /// <summary>Put back the sectors recorded in a backup file produced by any repair.</summary>
    /// <summary>Result of checking whether a dynamic (LDM) disk can be converted to basic by rewriting only the partition table.</summary>
    public sealed class DynamicDiskCheck
    {
        public bool IsDynamic { get; init; }
        public bool Convertible { get; init; }
        public string Reason { get; init; } = "";
        public PartitionScheme Scheme { get; init; }
        public PartitionInfo? DataPartition { get; init; }
        public PartitionInfo? MetadataPartition { get; init; }
        public NtfsVolumeCandidate? Volume { get; init; }
        public string Summary => !IsDynamic ? "Not a dynamic disk." : Convertible
            ? $"Dynamic {Scheme} disk with one simple NTFS volume that fills its LDM partition exactly ({Format.Bytes(DataPartition!.Length)}); convertible in place."
            : "Dynamic disk, but not convertible in place: " + Reason;
    }

    /// <summary>
    /// A dynamic disk is convertible in place when it carries exactly one LDM data partition, that partition starts with a valid
    /// NTFS boot sector, and the NTFS volume is no larger than the partition (i.e. it is a simple volume, not spanned/striped/mirrored).
    /// </summary>
    public static DynamicDiskCheck CheckDynamicDisk(IBlockDevice dev, PartitionTableInfo? table = null, IReadOnlyList<NtfsVolumeCandidate>? volumes = null)
    {
        table ??= PartitionTable.Read(dev);
        PartitionInfo? data, meta = null;
        if (table.Scheme == PartitionScheme.Gpt)
        {
            var datas = table.Partitions.Where(p => p.TypeGuid == PartitionTable.GptLdmData).ToList();
            meta = table.Partitions.FirstOrDefault(p => p.TypeGuid == PartitionTable.GptLdmMeta);
            if (datas.Count == 0 && meta == null) return new DynamicDiskCheck { IsDynamic = false, Scheme = table.Scheme };
            if (datas.Count != 1) return new DynamicDiskCheck { IsDynamic = true, Scheme = table.Scheme, Reason = $"{datas.Count} LDM data partitions; only a single-partition simple volume can be converted by retyping the entry." };
            if (!table.PrimaryGptValid) return new DynamicDiskCheck { IsDynamic = true, Scheme = table.Scheme, Reason = "the primary GPT is damaged; rebuild it from the backup first." };
            data = datas[0];
        }
        else if (table.Scheme == PartitionScheme.Mbr)
        {
            var datas = table.Partitions.Where(p => p.MbrType == 0x42).ToList();
            if (datas.Count == 0) return new DynamicDiskCheck { IsDynamic = false, Scheme = table.Scheme };
            if (datas.Count != 1) return new DynamicDiskCheck { IsDynamic = true, Scheme = table.Scheme, Reason = $"{datas.Count} LDM (type 42) partitions; only a single-partition simple volume can be converted." };
            data = datas[0];
        }
        else return new DynamicDiskCheck { IsDynamic = false, Scheme = table.Scheme };

        // The simple volume must start at the partition start (NTFS boot sector there) and fit inside the partition.
        NtfsVolumeCandidate? vol = volumes?.FirstOrDefault(v => v.StartOffset == data.StartOffset);
        if (vol == null)
        {
            try { vol = VolumeLocator.Probe(dev, data.StartOffset, data.Length, data); } catch { vol = null; }
        }
        if (vol == null) return new DynamicDiskCheck { IsDynamic = true, Scheme = table.Scheme, DataPartition = data, MetadataPartition = meta, Reason = "no NTFS boot sector at the start of the LDM partition. The volume is spanned/striped/mirrored, starts at an offset inside the LDM container, or is not NTFS; retyping the partition would not expose it." };
        long volBytes = vol.BootSector.TotalSectors * (long)vol.BootSector.BytesPerSector + vol.BootSector.BytesPerSector; // +1 backup boot sector
        if (volBytes > data.Length) return new DynamicDiskCheck { IsDynamic = true, Scheme = table.Scheme, DataPartition = data, MetadataPartition = meta, Volume = vol, Reason = $"the NTFS volume ({Format.Bytes(volBytes)}) is larger than the LDM partition ({Format.Bytes(data.Length)}); it must be spanned across more than one extent." };
        return new DynamicDiskCheck { IsDynamic = true, Convertible = true, Scheme = table.Scheme, DataPartition = data, MetadataPartition = meta, Volume = vol };
    }

    /// <summary>Rewrite the partition table so the LDM simple volume becomes a plain basic partition. Data sectors are not touched.</summary>
    public static RepairResult ConvertDynamicToBasic(IBlockDevice dev, string? backupDir = null, PartitionTableInfo? table = null, IReadOnlyList<NtfsVolumeCandidate>? volumes = null)
    {
        if (!dev.CanWrite) return Fail("Device is opened read-only.");
        var check = CheckDynamicDisk(dev, table, volumes);
        if (!check.IsDynamic) return Fail("This is not a dynamic (LDM) disk; nothing to convert.");
        if (!check.Convertible) return Fail("Refusing to convert: " + check.Reason);
        int ss = dev.SectorSize;
        var data = check.DataPartition!;
        if (check.Scheme == PartitionScheme.Mbr)
        {
            var mbr = dev.ReadBytes(0, ss);
            int o = 446 + (data.Index - 1) * 16;
            if (mbr[o + 4] != 0x42) return Fail("MBR entry no longer reads as type 42; re-open the disk and analyse again.");
            mbr[o + 4] = 0x07;
            return WriteWithBackup(dev, "ConvertDynamicToBasic", backupDir, (0, mbr));
        }
        long lastLba = dev.Length / ss - 1;
        var ph = GptHeader.Parse(dev.ReadBytes(ss, ss), 1);
        if (!ph.Valid) return Fail("Primary GPT header is not valid: " + ph.Problem);
        var bh = GptHeader.Parse(dev.ReadBytes(lastLba * ss, ss), lastLba);
        int entriesBytes = (int)(ph.EntryCount * ph.EntrySize);
        int entriesLen = (int)Bin.AlignUp(entriesBytes, ss);
        var entries = dev.ReadBytes((long)ph.EntriesLba * ss, entriesLen);
        if (Crc32.Compute(entries.AsSpan(0, entriesBytes)) != ph.EntriesCrc) return Fail("Primary GPT entries fail their CRC; rebuild the GPT from the backup first.");
        // Retype the LDM data entry, clear the LDM metadata entry; keep unique GUID, range, attributes and name.
        int dataOff = (data.Index - 1) * (int)ph.EntrySize;
        if (new Guid(entries.AsSpan(dataOff, 16)) != PartitionTable.GptLdmData) return Fail("GPT entry no longer reads as LDM data; re-open the disk and analyse again.");
        PartitionTable.GptBasicData.TryWriteBytes(entries.AsSpan(dataOff, 16));
        if (Bin.Utf16(entries, dataOff + 56, 36).TrimEnd('\0').Length == 0)
            System.Text.Encoding.Unicode.GetBytes("Basic data partition").CopyTo(entries.AsSpan(dataOff + 56));
        if (check.MetadataPartition is { } m) entries.AsSpan((m.Index - 1) * (int)ph.EntrySize, (int)ph.EntrySize).Clear();
        uint ecrc = Crc32.Compute(entries.AsSpan(0, entriesBytes));
        var writes = new List<(long, byte[])>();
        writes.Add((ss, Reheader(ph.Raw, ecrc)));
        writes.Add(((long)ph.EntriesLba * ss, entries));
        if (bh.Valid)
        {
            writes.Add(((long)bh.EntriesLba * ss, entries));
            writes.Add((lastLba * ss, Reheader(bh.Raw, ecrc)));
        }
        var res = WriteWithBackup(dev, "ConvertDynamicToBasic", backupDir, writes.ToArray());
        if (res.Success && !bh.Valid) res = new RepairResult { Success = true, Message = res.Message + " The backup GPT was already invalid and was left alone; run 'Rebuild GPT' later if wanted.", BackupFile = res.BackupFile, Written = res.Written };
        return res;

        static byte[] Reheader(byte[] raw, uint entriesCrc)
        {
            var h = (byte[])raw.Clone();
            uint hsize = Bin.U32(h, 12);
            Bin.PutU32(h, 88, entriesCrc);
            Bin.PutU32(h, 16, 0);
            Bin.PutU32(h, 16, Crc32.Compute(h.AsSpan(0, (int)hsize)));
            return h;
        }
    }

    public static RepairResult Undo(IBlockDevice dev, string backupFile)
    {
        if (!dev.CanWrite) return Fail("Device is opened read-only.");
        var b = JsonSerializer.Deserialize<SectorBackup>(File.ReadAllText(backupFile)) ?? throw new InvalidDataException("Bad backup file.");
        var written = new List<(long, int)>();
        foreach (var r in b.Ranges)
        {
            var data = Convert.FromBase64String(r.DataBase64);
            dev.WriteExact(r.Offset, data);
            written.Add((r.Offset, data.Length));
        }
        dev.Flush();
        return new RepairResult { Success = true, Message = $"Restored {b.Ranges.Count} sector range(s) from {Path.GetFileName(backupFile)} (action was {b.Action}).", Written = written };
    }

    private static RepairResult WriteWithBackup(IBlockDevice dev, string action, string? backupDir, params (long Offset, byte[] Data)[] writes)
    {
        backupDir ??= DefaultBackupDir;
        Directory.CreateDirectory(backupDir);
        var backup = new SectorBackup { Source = dev.Description, Action = action, When = DateTime.Now };
        foreach (var (off, data) in writes)
        {
            if (off % dev.SectorSize != 0 || data.Length % dev.SectorSize != 0) return Fail($"Internal error: unaligned write planned at {off}.");
            var orig = dev.ReadBytes(off, data.Length);
            backup.Ranges.Add(new SectorBackupRange { Offset = off, Length = data.Length, DataBase64 = Convert.ToBase64String(orig) });
        }
        string file = Path.Combine(backupDir, $"{DateTime.Now:yyyyMMdd-HHmmss}-{action}.json");
        File.WriteAllText(file, JsonSerializer.Serialize(backup, new JsonSerializerOptions { WriteIndented = true }));
        var written = new List<(long, int)>();
        try
        {
            foreach (var (off, data) in writes) { dev.WriteExact(off, data); written.Add((off, data.Length)); }
            dev.Flush();
        }
        catch (Exception ex)
        {
            return new RepairResult { Success = false, Message = $"Write failed: {ex.Message}. Original sectors are saved in {file}; if the drive is in read-only mode no repair can be written.", BackupFile = file, Written = written };
        }
        Log.Info($"{action}: wrote {writes.Length} range(s); originals saved to {file}");
        return new RepairResult { Success = true, Message = $"{RepairKindText.Title(Enum.Parse<RepairKind>(action))}: done. Originals saved to {file}.", BackupFile = file, Written = written };
    }

    private static byte[] Pad(byte[] sector512, int deviceSector, IBlockDevice dev, long offset)
    {
        if (deviceSector == 512) return sector512;
        var full = dev.ReadBytes(offset, deviceSector);
        sector512.CopyTo(full, 0);
        return full;
    }

    private static RepairResult Fail(string m) => new() { Success = false, Message = m };
}
