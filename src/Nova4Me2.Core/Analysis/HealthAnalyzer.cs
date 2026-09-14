using Nova4Me2.Core.Devices;
using Nova4Me2.Core.Hardware;
using Nova4Me2.Core.Ntfs;
using Nova4Me2.Core.Partitions;
using Nova4Me2.Core.Util;

namespace Nova4Me2.Core.Analysis;

public sealed class VolumeHealth
{
    public NtfsVolumeCandidate Candidate { get; init; } = null!;
    public NtfsVolume? Volume { get; set; }
    public string Label { get; set; } = "";
    public long Size { get; set; }
    public bool PrimaryBootOk, BackupBootOk, BootSectorsIdentical, SectorSizeMismatch, MftOk, MftFromMirror, MftMirrorMatches, Dirty, RootListable, WindowsInstall, HasHiberfile, HasPagefile;
    public int MftMirrorRecordsChecked, MftMirrorMismatches;
    public long MftRecords;
    public string NtfsVersion = "";
    public int DirsSampled, DirErrors;
    public string OpenError = "";
    public List<Finding> Findings { get; } = new();
    public bool Healthy => OpenError.Length == 0 && PrimaryBootOk && MftOk && RootListable && DirErrors == 0 && !SectorSizeMismatch;
}

public sealed class BootFixAssessment
{
    public int LikelihoodPercent { get; set; }
    public string Verdict { get; set; } = "";
    public List<string> Reasons { get; } = new();
    public List<RepairKind> Recommended { get; } = new();
}

public sealed class HealthReport
{
    public DateTime When { get; init; } = DateTime.Now;
    public string Source { get; init; } = "";
    public string DriveModel { get; init; } = "";
    public string DriveSerial { get; init; } = "";
    public long DriveSize { get; init; }
    public int DeviceSectorSize { get; init; }
    public string BusType { get; init; } = "";
    public PartitionTableInfo Table { get; init; } = null!;
    public List<VolumeHealth> Volumes { get; } = new();
    public List<Finding> DiskFindings { get; } = new();
    public NvmeHealthLog? NvmeHealth { get; set; }
    public AtaSmartData? AtaSmart { get; set; }
    public SurfaceScanResult? Surface { get; set; }
    public int Score { get; set; }
    public string Grade { get; set; } = "";
    public BootFixAssessment BootFix { get; set; } = new();
    public DeviceStats? DeviceStats { get; set; }
    public IEnumerable<Finding> AllFindings => DiskFindings.Concat(Volumes.SelectMany(v => v.Findings));
}

public sealed class HealthOptions
{
    public int DirectorySample { get; set; } = 250;
    public bool CheckMftMirror { get; set; } = true;
}

/// <summary>Non-destructive structural check of a disk and its NTFS volumes, with an estimate of whether a boot-record repair would help.</summary>
public static class HealthAnalyzer
{
    public static HealthReport Analyze(ResilientBlockDevice dev, HealthOptions? opt = null, string? model = null, string? serial = null, string? bus = null, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        opt ??= new HealthOptions();
        progress?.Report("Reading partition table…");
        var table = PartitionTable.Read(dev);
        var rep = new HealthReport { Source = dev.Description, DriveModel = model ?? "", DriveSerial = serial ?? "", DriveSize = dev.Length, DeviceSectorSize = dev.SectorSize, BusType = bus ?? "", Table = table };
        var df = rep.DiskFindings;
        switch (table.Scheme)
        {
            case PartitionScheme.Gpt:
                df.Add(new Finding { Severity = Severity.Info, Area = "Partition table", Title = "GPT partition table", Detail = $"{table.Partitions.Count} partitions, disk GUID {table.DiskGuid}." });
                if (!table.PrimaryGptValid && table.BackupGptValid)
                    df.Add(new Finding { Severity = Severity.Error, Area = "Partition table", Title = "Primary GPT is damaged; backup GPT is intact", Detail = "Windows may show the disk as uninitialised or the volumes as RAW. The backup copy at the end of the disk is valid and can be copied back.", Repair = RepairKind.RestoreGptFromBackup });
                else if (table.PrimaryGptValid && !table.BackupGptValid)
                    df.Add(new Finding { Severity = Severity.Warning, Area = "Partition table", Title = "Backup GPT is damaged", Detail = "The primary GPT works; the backup at the end of the disk does not. Not blocking, but reduces resilience." });
                else df.Add(new Finding { Severity = Severity.Good, Area = "Partition table", Title = "Primary and backup GPT valid", Detail = "Both GPT copies are consistent." });
                break;
            case PartitionScheme.Mbr:
                df.Add(new Finding { Severity = Severity.Info, Area = "Partition table", Title = "MBR partition table", Detail = $"{table.Partitions.Count} partitions, signature 0x{table.MbrSignature:X8}." });
                break;
            default:
                df.Add(new Finding { Severity = Severity.Critical, Area = "Partition table", Title = "No usable partition table", Detail = "Neither MBR nor GPT could be read. Volumes can still be located by scanning for NTFS boot sectors.", Repair = RepairKind.CopyDataOffNow });
                break;
        }
        foreach (var p in table.Problems) df.Add(new Finding { Severity = Severity.Warning, Area = "Partition table", Title = "Partition table note", Detail = p });
        foreach (var p in table.Partitions)
        {
            if (p.TypeGuid == PartitionTable.GptEfiSystem || p.MbrType == 0xEF)
            {
                bool fat = false;
                try { var b = dev.ReadBytes(p.StartOffset, 512); fat = b[510] == 0x55 && b[511] == 0xAA && (Bin.Ascii(b, 0x52, 5) == "FAT32" || Bin.Ascii(b, 0x36, 3) == "FAT"); } catch { }
                df.Add(new Finding { Severity = fat ? Severity.Good : Severity.Warning, Area = "EFI System Partition", Title = fat ? "EFI System Partition present and readable" : "EFI System Partition boot sector not recognised", Detail = $"Partition {p.Index} at {Format.Bytes(p.StartOffset)}, {Format.Bytes(p.Length)}. {(fat ? "Contains the FAT file system that holds the Windows Boot Manager and BCD store." : "The boot manager files may be unreachable; a bcdboot rebuild recreates them.")}", Repair = fat ? RepairKind.None : RepairKind.RebuildBcd });
            }
            else if (p.TypeGuid == PartitionTable.GptMsReserved) df.Add(new Finding { Severity = Severity.Info, Area = "Partitions", Title = "Microsoft Reserved partition", Detail = $"Partition {p.Index}, {Format.Bytes(p.Length)} (normal on GPT Windows disks)." });
            else if (p.TypeGuid == PartitionTable.GptWinRecovery) df.Add(new Finding { Severity = Severity.Info, Area = "Partitions", Title = "Windows Recovery partition", Detail = $"Partition {p.Index}, {Format.Bytes(p.Length)}." });
        }

        progress?.Report("Locating NTFS volumes…");
        var cands = VolumeLocator.Find(dev, table, scanIfNoneFound: true, progress);
        if (cands.Count == 0) df.Add(new Finding { Severity = Severity.Critical, Area = "File systems", Title = "No NTFS volume found", Detail = "No valid NTFS boot sector (primary or backup) was found in any partition or by scanning.", Repair = RepairKind.CopyDataOffNow });
        if (table.Scheme == PartitionScheme.None && cands.Any(c => c.StartOffset == 0))
        {
            // A bare volume (partition image or \\.\X: handle) legitimately has no partition table.
            df.RemoveAll(f => f.Title == "No usable partition table");
            df.Insert(0, new Finding { Severity = Severity.Info, Area = "Partition table", Title = "Bare NTFS volume (no partition table)", Detail = "The source starts directly with an NTFS boot sector: a partition image or a volume handle rather than a whole disk." });
        }
        foreach (var c in cands)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Checking NTFS volume at {Format.Bytes(c.StartOffset)}…");
            rep.Volumes.Add(CheckVolume(dev, c, opt, ct));
        }
        rep.DeviceStats = dev.Stats.Clone();
        Rescore(rep);
        rep.BootFix = AssessBootFix(rep);
        return rep;
    }

    private static VolumeHealth CheckVolume(ResilientBlockDevice dev, NtfsVolumeCandidate c, HealthOptions opt, CancellationToken ct)
    {
        var v = new VolumeHealth { Candidate = c, Size = c.Length, PrimaryBootOk = c.PrimaryBootSectorValid, BackupBootOk = c.BackupBootSectorValid };
        var f = v.Findings;
        string area = $"NTFS @ {Format.Bytes(c.StartOffset)}";
        var bs = c.BootSector;
        v.SectorSizeMismatch = bs.BytesPerSector != dev.SectorSize;
        if (v.SectorSizeMismatch)
            f.Add(new Finding { Severity = Severity.Critical, Area = area, Title = $"Sector size mismatch: volume uses {bs.BytesPerSector}-byte sectors, the device reports {dev.SectorSize}-byte sectors",
                Detail = "This is the classic reason a perfectly healthy NTFS volume shows as RAW inside a USB NVMe enclosure: the bridge chip exposes 4Kn sectors while the drive was formatted natively with 512e sectors (or vice versa). Windows' NTFS driver refuses the volume; Nova4Me2 reads it byte-for-byte and is unaffected.", Repair = RepairKind.ConnectDirectly,
                Advice = "Connect the SSD directly to an M.2 slot (or use an enclosure that presents 512-byte sectors) and Windows will most likely mount it normally. No boot record repair is needed for this." });
        if (!c.PrimaryBootSectorValid && c.BackupBootSectorValid)
            f.Add(new Finding { Severity = Severity.Error, Area = area, Title = "Primary NTFS boot sector damaged; backup copy is valid", Detail = "Windows reads the first sector to recognise NTFS; with it damaged the volume shows as RAW. The backup in the last sector of the volume is intact.", Repair = RepairKind.RestoreBootSectorFromBackup });
        else if (c.PrimaryBootSectorValid && !c.BackupBootSectorValid)
            f.Add(new Finding { Severity = Severity.Warning, Area = area, Title = "Backup NTFS boot sector missing or damaged", Detail = "The primary boot sector is fine. The backup copy at the end of the volume is not (often harmless, but chkdsk may complain).", Repair = RepairKind.RestoreBackupBootSectorFromPrimary });
        else if (c.PrimaryBootSectorValid && c.BackupBootSectorValid)
        {
            byte[]? backupRaw = null;
            try { backupRaw = dev.ReadBytes(c.StartOffset + bs.TotalSectors * bs.BytesPerSector, 512); } catch { }
            v.BootSectorsIdentical = backupRaw != null && backupRaw.AsSpan().SequenceEqual(bs.Raw);
            f.Add(new Finding { Severity = v.BootSectorsIdentical ? Severity.Good : Severity.Warning, Area = area, Title = v.BootSectorsIdentical ? "Boot sector and backup boot sector valid and identical" : "Boot sector and backup differ", Detail = v.BootSectorsIdentical ? $"Cluster {bs.BytesPerCluster} bytes, MFT record {bs.MftRecordSize} bytes, {Format.Bytes(bs.VolumeBytes)}." : "Both parse as NTFS but are not byte-identical; the volume may have been resized or one copy is stale." });
        }
        if (!bs.HasBootCode && c.PrimaryBootSectorValid) f.Add(new Finding { Severity = Severity.Info, Area = area, Title = "No boot code in boot sector", Detail = "The NTFS boot sector carries no bootstrap code (normal for data volumes; on UEFI systems the EFI partition boots, not this sector)." });
        try
        {
            var vol = NtfsVolume.Open(dev, c);
            v.Volume = vol;
            v.Label = vol.Info.Label;
            v.MftOk = true;
            v.MftFromMirror = vol.MftLoadedFromMirror;
            v.MftRecords = vol.MftRecordCount;
            v.NtfsVersion = vol.Info.Version;
            v.Dirty = vol.Info.IsDirty;
            if (vol.MftLoadedFromMirror) f.Add(new Finding { Severity = Severity.Error, Area = area, Title = "$MFT record 0 unreadable; using $MFTMirr", Detail = "The master file table's own record is damaged. Windows reports RAW in this state. The mirror copy is intact and can be written back.", Repair = RepairKind.RestoreMftFromMirror });
            else f.Add(new Finding { Severity = Severity.Good, Area = area, Title = "$MFT readable", Detail = $"{vol.MftRecordCount:N0} MFT records ({Format.Bytes(vol.MftDataAttribute.RealSize)}), NTFS {vol.Info.Version}{(v.Label.Length > 0 ? $", label \"{v.Label}\"" : "")}." });
            foreach (var p in vol.Problems.Where(p => !c.Notes.Contains(p))) f.Add(new Finding { Severity = Severity.Warning, Area = area, Title = "Volume note", Detail = p });
            if (vol.Info.IsDirty) f.Add(new Finding { Severity = Severity.Warning, Area = area, Title = "Volume is marked DIRTY", Detail = "The dirty bit means Windows did not unmount cleanly (matches the power loss). Windows normally runs CHKDSK on the next mount; when it cannot even mount the volume the bit stays set. Data is usually intact.", Repair = RepairKind.RunChkdskAfterImaging });
            if (opt.CheckMftMirror)
            {
                try
                {
                    int n = Math.Max(1, Math.Min(4, vol.ClusterSize / vol.RecordSize));
                    n = Math.Max(n, 4);
                    for (int i = 0; i < n; i++)
                    {
                        var prim = vol.ReadRawRecord(i);
                        var mirr = dev.ReadBytes(c.StartOffset + bs.MftMirrLcn * vol.ClusterSize + (long)i * vol.RecordSize, vol.RecordSize);
                        v.MftMirrorRecordsChecked++;
                        if (!prim.AsSpan().SequenceEqual(mirr)) v.MftMirrorMismatches++;
                    }
                    v.MftMirrorMatches = v.MftMirrorMismatches == 0;
                    f.Add(new Finding { Severity = v.MftMirrorMatches ? Severity.Good : Severity.Warning, Area = area, Title = v.MftMirrorMatches ? "$MFT and $MFTMirr agree" : $"$MFTMirr differs from $MFT in {v.MftMirrorMismatches} of {v.MftMirrorRecordsChecked} records", Detail = v.MftMirrorMatches ? "The first system records match their mirror copies." : "Usually means the last write before the power loss did not complete; chkdsk would repair this. Reads are unaffected." });
                }
                catch (Exception ex) { f.Add(new Finding { Severity = Severity.Warning, Area = area, Title = "$MFTMirr check failed", Detail = ex.Message }); }
            }
            try
            {
                var root = vol.ListDirectory(NtfsVolume.RootRecord, "");
                v.RootListable = true;
                var names = root.ToDictionary(e => e.Name, e => e, StringComparer.OrdinalIgnoreCase);
                v.HasHiberfile = names.ContainsKey("hiberfil.sys");
                v.HasPagefile = names.ContainsKey("pagefile.sys");
                if (names.ContainsKey("Windows"))
                {
                    var sys = vol.Resolve(@"Windows\System32\config\SYSTEM");
                    var krn = vol.Resolve(@"Windows\System32\ntoskrnl.exe");
                    var wl = vol.Resolve(@"Windows\System32\winload.efi") ?? vol.Resolve(@"Windows\System32\winload.exe");
                    v.WindowsInstall = sys != null && krn != null;
                    if (v.WindowsInstall) f.Add(new Finding { Severity = Severity.Good, Area = area, Title = "Windows installation found", Detail = $"Windows\\System32 with registry hives and kernel present{(wl != null ? ", winload present" : ", winload missing")}{(names.ContainsKey("Users") ? "; Users folder present" : "")}." });
                }
                if (v.HasHiberfile) f.Add(new Finding { Severity = Severity.Info, Area = area, Title = "hiberfil.sys present", Detail = "A hibernation file exists; if the machine was hibernating (fast startup), the volume state on disk may lag the last session. Data files are unaffected." });
                // Sample directories breadth-first and count index problems.
                int before = vol.Problems.Count;
                var q = new Queue<NtfsEntry>(root.Where(e => e.IsDirectory && !e.IsMetaFile));
                while (q.Count > 0 && v.DirsSampled < opt.DirectorySample)
                {
                    ct.ThrowIfCancellationRequested();
                    var d = q.Dequeue();
                    if (d.IsReparsePoint) continue;
                    v.DirsSampled++;
                    try { foreach (var k in vol.ListDirectory(d.Record, d.Path)) if (k.IsDirectory) q.Enqueue(k); }
                    catch { v.DirErrors++; }
                }
                v.DirErrors += Math.Max(0, vol.Problems.Count - before);
                f.Add(new Finding { Severity = v.DirErrors == 0 ? Severity.Good : Severity.Warning, Area = area, Title = v.DirErrors == 0 ? $"Directory structure consistent ({v.DirsSampled} folders sampled)" : $"{v.DirErrors} directory index problems in {v.DirsSampled} folders sampled", Detail = v.DirErrors == 0 ? "Directory indexes parse cleanly." : "Some folders have damaged index blocks; use the \"Rebuild from MFT scan\" mode to list their contents." });
            }
            catch (Exception ex) { f.Add(new Finding { Severity = Severity.Error, Area = area, Title = "Root directory unreadable", Detail = ex.Message + " — the MFT scan mode can still recover files by walking every record." }); }
            try
            {
                var bmp = vol.GetRecord(6);
                var data = vol.FindAttribute(bmp, AttrType.Data);
                long expect = (vol.TotalClusters + 7) / 8;
                if (data != null && Math.Abs(data.Length - expect) > 8 * vol.ClusterSize) f.Add(new Finding { Severity = Severity.Warning, Area = area, Title = "$Bitmap size unexpected", Detail = $"{data.Length} bytes vs expected ≈{expect}." });
            }
            catch { }
        }
        catch (Exception ex)
        {
            v.OpenError = ex.Message;
            f.Add(new Finding { Severity = Severity.Critical, Area = area, Title = "Cannot open the NTFS volume", Detail = ex.Message, Repair = RepairKind.CopyDataOffNow });
        }
        return v;
    }

    public static void Rescore(HealthReport r)
    {
        int s = 100;
        foreach (var f in r.AllFindings)
            s -= f.Severity switch { Severity.Critical => 40, Severity.Error => 20, Severity.Warning => 6, _ => 0 };
        if (r.NvmeHealth is { } n)
        {
            if (n.ReadOnlyMode) s -= 50;
            if (n.ReliabilityDegraded) s -= 25;
            if (n.SpareBelowThreshold) s -= 20;
            if (n.PercentageUsed >= 100) s -= 15;
            if (n.MediaErrors > 0) s -= Math.Min(20, (int)n.MediaErrors);
        }
        if (r.Surface is { } su && su.ChunksRead > 0)
        {
            if (su.BadSectors > 0) s -= Math.Min(40, 10 + (int)Math.Log2(su.BadSectors + 1) * 5);
            if (su.ChunksSlow > su.ChunksRead / 20) s -= 8;
        }
        r.Score = Math.Clamp(s, 0, 100);
        r.Grade = r.Score >= 90 ? "Good" : r.Score >= 70 ? "Fair" : r.Score >= 40 ? "Poor" : "Critical";
    }

    public static BootFixAssessment AssessBootFix(HealthReport r)
    {
        var a = new BootFixAssessment();
        var win = r.Volumes.FirstOrDefault(v => v.WindowsInstall) ?? r.Volumes.OrderByDescending(v => v.Size).FirstOrDefault();
        if (r.NvmeHealth is { ReadOnlyMode: true })
        {
            a.LikelihoodPercent = 0;
            a.Verdict = "The drive's firmware has put it into read-only mode. Nothing can be written to it, so no boot record repair can be applied. Copy everything off (or image it) and replace the drive.";
            a.Reasons.Add("NVMe SMART critical warning bit 3 (media in read-only mode) is set.");
            a.Recommended.Add(RepairKind.CopyDataOffNow);
            a.Recommended.Add(RepairKind.VendorFirmwareTool);
            return a;
        }
        if (win == null)
        {
            a.LikelihoodPercent = 5;
            a.Verdict = "No NTFS volume could be opened, so a boot record repair alone will not bring Windows back. Focus on data recovery from the MFT scan / signature scan.";
            a.Reasons.Add("No NTFS volume found.");
            a.Recommended.Add(RepairKind.CopyDataOffNow);
            return a;
        }
        int pct;
        if (win.SectorSizeMismatch)
        {
            pct = 8;
            a.Reasons.Add($"The volume was formatted with {win.Candidate.BootSector.BytesPerSector}-byte sectors but the device currently reports {r.DeviceSectorSize}-byte sectors (USB bridge translation).");
            a.Verdict = "Not a boot record problem. Connected directly (M.2), the volume should mount normally; a boot record fix through this enclosure cannot help and could hurt.";
            a.Recommended.Add(RepairKind.ConnectDirectly);
        }
        else if (!win.PrimaryBootOk && win.BackupBootOk)
        {
            pct = win.MftOk ? 85 : 55;
            a.Reasons.Add("Primary NTFS boot sector is damaged while the backup copy is valid: an exact replacement exists.");
            if (!win.MftOk) a.Reasons.Add("However the $MFT is also unreadable, which the boot sector fix does not address.");
            a.Verdict = "High: restoring the boot sector from its backup is a proven, reversible fix for exactly this symptom.";
            a.Recommended.Add(RepairKind.RestoreBootSectorFromBackup);
        }
        else if (!r.Table.PrimaryGptValid && r.Table.BackupGptValid)
        {
            pct = 80;
            a.Reasons.Add("Primary GPT damaged, backup GPT valid: rebuilding the primary copy restores the partition map.");
            a.Verdict = "High: the partition table (not the NTFS boot sector) is what Windows cannot read; rebuilding it from the backup is reliable.";
            a.Recommended.Add(RepairKind.RestoreGptFromBackup);
        }
        else if (win.MftFromMirror)
        {
            pct = 60;
            a.Reasons.Add("$MFT record 0 is unreadable; the mirror copy is usable.");
            a.Verdict = "Moderate: restoring the $MFT system records from $MFTMirr usually lets Windows mount the volume again; a chkdsk pass (after imaging) may still be needed.";
            a.Recommended.Add(RepairKind.RestoreMftFromMirror);
            a.Recommended.Add(RepairKind.RunChkdskAfterImaging);
        }
        else if (win.Healthy)
        {
            pct = win.WindowsInstall ? 20 : 15;
            a.Reasons.Add("Boot sector, backup boot sector, $MFT and sampled directories are all consistent.");
            if (win.Dirty) a.Reasons.Add("The volume's dirty flag is set (unclean shutdown), which by itself does not stop Windows from mounting it.");
            if (r.DeviceStats is { Reconnects: > 0 } ds) a.Reasons.Add($"The USB link dropped {ds.Reconnects} time(s) during analysis: the RAW status is likely the enclosure, not the file system.");
            a.Verdict = "Low: the NTFS boot record is already intact, so rewriting it changes nothing. The RAW status seen in Windows is most likely caused by the USB bridge (link drops or sector-size translation), and the failure to boot points to the boot manager/BCD or to the drive itself (a firmware read-only lock after power loss is a known Samsung 970 EVO failure mode).";
            a.Recommended.Add(RepairKind.ConnectDirectly);
            a.Recommended.Add(RepairKind.RebuildBcd);
            a.Recommended.Add(RepairKind.VendorFirmwareTool);
        }
        else
        {
            pct = 30;
            a.Reasons.Add("The volume opens but shows structural problems beyond the boot sector.");
            a.Verdict = "Uncertain: a boot record fix may be part of the solution but other NTFS structures are also damaged. Image the drive first, then repair the clone.";
            a.Recommended.Add(RepairKind.CopyDataOffNow);
            a.Recommended.Add(RepairKind.RunChkdskAfterImaging);
        }
        if (r.Surface is { BadSectors: > 0 } su)
        {
            bool bootArea = su.BadRanges.Any(b => b.Offset < win.Candidate.StartOffset + 1024 * 1024 && b.Offset + b.Length > win.Candidate.StartOffset);
            if (bootArea) { pct = Math.Min(pct, 10); a.Reasons.Add("Unreadable sectors sit in the boot area itself; a rewritten boot sector is unlikely to stick."); }
            else a.Reasons.Add($"{su.BadSectors} unreadable sectors elsewhere on the drive: the hardware is deteriorating, prioritise copying data.");
        }
        if (r.NvmeHealth is { } n && (n.ReliabilityDegraded || n.SpareBelowThreshold || n.MediaErrors > 0))
        {
            pct = Math.Min(pct, 25);
            a.Reasons.Add("SMART reports degraded reliability / media errors; repairs on failing media are not durable.");
            if (!a.Recommended.Contains(RepairKind.CopyDataOffNow)) a.Recommended.Insert(0, RepairKind.CopyDataOffNow);
        }
        a.LikelihoodPercent = Math.Clamp(pct, 0, 100);
        return a;
    }
}
