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
