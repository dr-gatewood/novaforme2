using Nova4Me2.Core.Devices;
using Nova4Me2.Core.Ntfs;
using Nova4Me2.Core.Util;

namespace Nova4Me2.Core.Partitions;

public enum BootSectorSource { Primary, Backup, Scan }

/// <summary>A located NTFS volume: where it starts, how its boot sector was found, and the parsed boot sector.</summary>
public sealed class NtfsVolumeCandidate
{
    public long StartOffset { get; init; }
    public long Length { get; init; }
    public PartitionInfo? Partition { get; init; }
    public BootSector BootSector { get; init; } = null!;
    public BootSectorSource Source { get; init; }
    public bool PrimaryBootSectorValid { get; init; }
    public bool BackupBootSectorValid { get; init; }
    public string Label { get; set; } = "";
    public List<string> Notes { get; init; } = new();
    public string Display => $"{(Partition != null ? $"Partition {Partition.Index}" : "Volume")} @ {Format.Bytes(StartOffset)} — NTFS {Format.Bytes(Length)}{(Label.Length > 0 ? $" \"{Label}\"" : "")}{(Source != BootSectorSource.Primary ? $" [boot sector from {Source}]" : "")}";
}

/// <summary>Finds NTFS volumes on a disk: via the partition table, then the backup boot sector, then a raw signature scan.</summary>
public static class VolumeLocator
{
    public static List<NtfsVolumeCandidate> Find(IBlockDevice dev, PartitionTableInfo? table = null, bool scanIfNoneFound = true, IProgress<string>? progress = null)
    {
        table ??= PartitionTable.Read(dev);
        var found = new List<NtfsVolumeCandidate>();
        foreach (var p in table.Partitions)
        {
            var c = Probe(dev, p.StartOffset, p.Length, p);
            if (c != null) found.Add(c);
        }
        if (found.Count == 0 && table.Partitions.Count == 0)
        {
            // Maybe the "disk" is really a partition image or a volume handle.
            var c = Probe(dev, 0, dev.Length, null);
            if (c != null) found.Add(c);
        }
        if (found.Count == 0 && scanIfNoneFound)
        {
            progress?.Report("No NTFS boot sector found via the partition table; scanning the disk for NTFS signatures…");
            found.AddRange(Scan(dev, progress));
        }
        return found;
    }

    /// <summary>Try the primary boot sector at <paramref name="start"/> and the backup at the end of the partition.</summary>
    public static NtfsVolumeCandidate? Probe(IBlockDevice dev, long start, long length, PartitionInfo? part)
    {
        int ss = dev.SectorSize;
        BootSector? primary = null, backup = null;
        var notes = new List<string>();
        try { primary = BootSector.TryParse(dev.ReadBytes(start, 512), ss); } catch (Exception ex) { notes.Add($"Primary boot sector unreadable: {ex.Message}"); }
        long backupOff = -1;
        if (primary != null)
        {
            backupOff = start + primary.TotalSectors * primary.BytesPerSector; // sector right after the last NTFS sector
        }
        else if (length > 0)
        {
            backupOff = start + length - ss; // last sector of the partition
        }
        if (backupOff > 0 && backupOff + 512 <= dev.Length)
        {
            try { backup = BootSector.TryParse(dev.ReadBytes(backupOff, 512), ss); } catch { }
            if (backup == null && primary == null && length > 0 && ss == 512 && backupOff - ss > start)
            {
                // 4K-sector partitions sometimes leave the backup one 512-byte sector earlier.
                try { backup = BootSector.TryParse(dev.ReadBytes(backupOff - 512, 512), ss); } catch { }
            }
        }
        if (primary == null && backup == null) return null;
        var bs = primary ?? backup!;
        long volLen = (bs.TotalSectors + 1) * bs.BytesPerSector;
        if (primary == null) notes.Add("Primary NTFS boot sector is damaged; using the backup boot sector (fixable: the backup can be copied back).");
        if (primary != null && backup == null) notes.Add("Backup NTFS boot sector missing or damaged (not fatal).");
        if (primary != null && backup != null && !primary.Raw.AsSpan().SequenceEqual(backup.Raw)) notes.Add("Primary and backup boot sectors differ.");
        if (length > 0 && volLen > length + ss) notes.Add($"Boot sector claims {Format.Bytes(volLen)} but partition is {Format.Bytes(length)}.");
        return new NtfsVolumeCandidate
        {
            StartOffset = start, Length = length > 0 ? Math.Min(length, volLen) : volLen, Partition = part, BootSector = bs,
            Source = primary != null ? BootSectorSource.Primary : BootSectorSource.Backup, PrimaryBootSectorValid = primary != null, BackupBootSectorValid = backup != null, Notes = notes
        };
    }

    /// <summary>Scan for NTFS boot sectors. Checks every sector in the first 64 MiB, then MiB boundaries, then (optionally) every sector.</summary>
    public static IEnumerable<NtfsVolumeCandidate> Scan(IBlockDevice dev, IProgress<string>? progress = null, bool exhaustive = false, CancellationToken ct = default)
    {
        int ss = dev.SectorSize;
        var seen = new HashSet<long>();
        var results = new List<NtfsVolumeCandidate>();
        const int block = 4 * 1024 * 1024;
        var buf = new byte[block];
        long denseLimit = Math.Min(dev.Length, 64L * 1024 * 1024);
        long lastReport = 0;
        for (long off = 0; off < dev.Length; off += block)
        {
            ct.ThrowIfCancellationRequested();
            int n = (int)Math.Min(block, dev.Length - off);
            bool dense = off < denseLimit || exhaustive;
            if (!dense && off % (1024 * 1024) != 0) continue;
            try
            {
                if (dense) dev.ReadExact(off, buf.AsSpan(0, n));
                else { n = 512; dev.ReadExact(off, buf.AsSpan(0, 512)); }
            }
            catch { continue; }
            for (int i = 0; i + 512 <= n; i += dense ? ss : n)
            {
                if (buf[i + 3] != (byte)'N' || buf[i + 4] != (byte)'T' || buf[i + 5] != (byte)'F' || buf[i + 6] != (byte)'S') continue;
                var bs = BootSector.TryParse(buf.AsSpan(i, 512).ToArray(), ss);
                if (bs == null) continue;
                long pos = off + i;
                long volBytes = bs.TotalSectors * bs.BytesPerSector;
                // Is this a backup boot sector? Then the volume starts volBytes earlier.
                long candidateStart = pos;
                var src = BootSectorSource.Scan;
                if (pos >= volBytes && pos - volBytes >= 0)
                {
                    BootSector? head = null;
                    try { head = BootSector.TryParse(dev.ReadBytes(pos - volBytes, 512), ss); } catch { }
                    if (head == null || head.Raw.AsSpan().SequenceEqual(bs.Raw)) { candidateStart = pos - volBytes; src = head == null ? BootSectorSource.Backup : BootSectorSource.Primary; }
                }
                if (!seen.Add(candidateStart)) continue;
                var c = new NtfsVolumeCandidate { StartOffset = candidateStart, Length = Math.Min(volBytes + bs.BytesPerSector, dev.Length - candidateStart), BootSector = bs, Source = src, PrimaryBootSectorValid = src == BootSectorSource.Primary, BackupBootSectorValid = src != BootSectorSource.Scan };
                c.Notes.Add($"Found by signature scan at byte {pos}{(src == BootSectorSource.Backup ? " (backup boot sector; primary is damaged)" : "")}.");
                results.Add(c);
                progress?.Report($"NTFS volume found at {Format.Bytes(candidateStart)} ({Format.Bytes(volBytes)})");
            }
            if (off - lastReport > 1024L * 1024 * 1024) { lastReport = off; progress?.Report($"Scanned {Format.Bytes(off)} of {Format.Bytes(dev.Length)}…"); }
        }
        return results;
    }
}
