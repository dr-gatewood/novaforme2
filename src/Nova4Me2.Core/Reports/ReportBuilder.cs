using Nova4Me2.Core.Analysis;
using Nova4Me2.Core.Hardware;
using Nova4Me2.Core.Recovery;
using Nova4Me2.Core.Util;

namespace Nova4Me2.Core.Reports;

public static class ReportBuilder
{
    public static Report FromHealth(HealthReport h, HardwareInfo? hw = null)
    {
        var r = new Report { Title = "Drive health & recovery assessment", Subtitle = $"{(h.DriveModel.Length > 0 ? h.DriveModel : h.Source)}{(h.DriveSerial.Length > 0 ? $" — S/N {h.DriveSerial}" : "")}", When = h.When, Score = h.Score, Grade = h.Grade, BootFixLikelihood = h.BootFix.LikelihoodPercent };
        r.Summary = $"Overall structural health {h.Score}/100 ({h.Grade}). Boot-record repair likelihood: {h.BootFix.LikelihoodPercent}%. {h.BootFix.Verdict}";
        var ov = r.Add("Overview");
        ov.Rows.Add(("Source", h.Source));
        if (h.DriveModel.Length > 0) ov.Rows.Add(("Drive", h.DriveModel));
        if (h.DriveSerial.Length > 0) ov.Rows.Add(("Serial", h.DriveSerial));
        ov.Rows.Add(("Capacity", $"{Format.Bytes(h.DriveSize)} ({h.DriveSize:N0} bytes)"));
        ov.Rows.Add(("Device sector size", $"{h.DeviceSectorSize} bytes"));
        if (h.BusType.Length > 0) ov.Rows.Add(("Bus", h.BusType));
        ov.Rows.Add(("Partition scheme", h.Table.Scheme.ToString()));
        ov.Rows.Add(("NTFS volumes found", h.Volumes.Count.ToString()));
        if (h.DeviceStats != null) ov.Rows.Add(("I/O during analysis", $"{Format.Bytes(h.DeviceStats.BytesRead)} read, {h.DeviceStats.ReadErrors} read errors, {h.DeviceStats.Reconnects} reconnects, {h.DeviceStats.BadSectors} bad sectors"));

        var bf = r.Add("Boot record fix assessment");
        bf.Rows.Add(("Likelihood a boot record repair helps", $"{h.BootFix.LikelihoodPercent}%"));
        bf.Paragraphs.Add(h.BootFix.Verdict);
        foreach (var reason in h.BootFix.Reasons) bf.Bullets.Add(reason);
        foreach (var k in h.BootFix.Recommended) { bf.Table.Add(new[] { RepairKindText.Title(k), RepairKindText.Explain(k) }); }
        if (bf.Table.Count > 0) bf.Table.Insert(0, new[] { "Recommended action", "Why / how" });

        var pt = r.Add("Partition table");
        pt.Table.Add(new[] { "#", "Type", "Name", "Start", "Size" });
        foreach (var p in h.Table.Partitions) pt.Table.Add(new[] { p.Index.ToString(), p.TypeName, p.Name, Format.Bytes(p.StartOffset), Format.Bytes(p.Length) });
        pt.Findings.AddRange(h.DiskFindings);

        foreach (var v in h.Volumes)
        {
            var s = r.Add($"NTFS volume at {Format.Bytes(v.Candidate.StartOffset)}{(v.Label.Length > 0 ? $" \"{v.Label}\"" : "")}");
            s.Rows.Add(("Size", Format.Bytes(v.Size)));
            s.Rows.Add(("Boot sector", $"primary {(v.PrimaryBootOk ? "OK" : "DAMAGED")}, backup {(v.BackupBootOk ? "OK" : "DAMAGED")}{(v.PrimaryBootOk && v.BackupBootOk ? (v.BootSectorsIdentical ? ", identical" : ", differ") : "")}"));
            s.Rows.Add(("Volume sector size", $"{v.Candidate.BootSector.BytesPerSector} bytes{(v.SectorSizeMismatch ? " — MISMATCH with device" : "")}"));
            s.Rows.Add(("Cluster size", $"{v.Candidate.BootSector.BytesPerCluster} bytes"));
            s.Rows.Add(("$MFT", v.OpenError.Length > 0 ? "unreadable" : $"{v.MftRecords:N0} records{(v.MftFromMirror ? " (loaded from $MFTMirr)" : "")}"));
            if (v.MftMirrorRecordsChecked > 0) s.Rows.Add(("$MFTMirr", v.MftMirrorMatches ? "matches" : $"{v.MftMirrorMismatches} mismatching records"));
            if (v.NtfsVersion.Length > 0) s.Rows.Add(("NTFS version", v.NtfsVersion));
            s.Rows.Add(("Dirty flag", v.Dirty ? "SET (unclean shutdown)" : "clear"));
            s.Rows.Add(("Root directory", v.RootListable ? "readable" : "NOT readable"));
            s.Rows.Add(("Directories sampled", $"{v.DirsSampled} ({v.DirErrors} with errors)"));
            s.Rows.Add(("Windows installation", v.WindowsInstall ? "yes" : "no"));
            s.Findings.AddRange(v.Findings);
        }
        if (h.NvmeHealth != null || h.AtaSmart != null)
        {
            var sm = r.Add("SMART / drive self-reported health");
            if (h.NvmeHealth is { } n)
            {
                sm.Rows.Add(("Critical warning", n.CriticalWarningText)); sm.Rows.Add(("Temperature", $"{n.TemperatureCelsius:0} °C")); sm.Rows.Add(("Available spare", $"{n.AvailableSpare}% (threshold {n.AvailableSpareThreshold}%)"));
                sm.Rows.Add(("Percentage used", $"{n.PercentageUsed}%")); sm.Rows.Add(("Media errors", n.MediaErrors.ToString())); sm.Rows.Add(("Unsafe shutdowns", n.UnsafeShutdowns.ToString()));
                sm.Rows.Add(("Power-on hours", n.PowerOnHours.ToString())); sm.Rows.Add(("Data written", Format.Bytes(n.BytesWritten)));
            }
            if (h.AtaSmart is { } a)
            {
                sm.Table.Add(new[] { "ID", "Attribute", "Current", "Worst", "Threshold", "Raw", "Status" });
                foreach (var at in a.Attributes) sm.Table.Add(new[] { at.Id.ToString(), at.Name, at.Current.ToString(), at.Worst.ToString(), at.Threshold.ToString(), at.Raw.ToString(), at.Failing ? "FAILING" : "ok" });
            }
        }
        if (h.Surface is { } su)
        {
            var ss = r.Add("Surface scan");
            ss.Rows.Add(("Scanned", $"{Format.Bytes(su.BytesRead)} of {Format.Bytes(su.BytesTotal)} ({(su.Complete ? "complete" : "stopped early")})"));
            ss.Rows.Add(("Result", su.Grade));
            ss.Rows.Add(("Unreadable sectors", su.BadSectors.ToString()));
            ss.Rows.Add(("Slow chunks", $"{su.ChunksSlow} of {su.ChunksRead}"));
            ss.Rows.Add(("Latency (min/avg/max)", $"{su.MinSeconds * 1000:0} / {su.AvgSeconds * 1000:0} / {su.MaxSeconds * 1000:0} ms per {Format.Bytes(4 * 1024 * 1024)} chunk"));
            ss.Rows.Add(("Throughput", Format.Rate(su.BytesPerSecond)));
            ss.Rows.Add(("Link drops during scan", su.Reconnects.ToString()));
            if (su.BadRanges.Count > 0)
            {
                ss.Table.Add(new[] { "Byte offset", "Length", "LBA" });
                foreach (var (o, l) in su.BadRanges.Take(200)) ss.Table.Add(new[] { o.ToString(), l.ToString(), (o / h.DeviceSectorSize).ToString() });
                if (su.BadRanges.Count > 200) ss.Paragraphs.Add($"… and {su.BadRanges.Count - 200} more.");
            }
        }
        if (hw != null) AddHardware(r, hw);
        var gl = r.Add("Glossary");
        gl.Bullets.Add("RAW: Windows could not recognise the file system on the volume. It does not mean the data is gone; it means the NTFS driver refused to mount it (damaged boot sector, damaged $MFT, sector-size mismatch, or I/O errors).");
        gl.Bullets.Add("$MFT / $MFTMirr: the master file table and its partial mirror. Every file is a record in the $MFT.");
        gl.Bullets.Add("Boot sector (VBR): first sector of the volume with the NTFS geometry; a backup copy sits in the last sector.");
        gl.Bullets.Add("GPT: partition table with a backup copy at the end of the disk.");
        return r;
    }

    public static void AddHardware(Report r, HardwareInfo hw)
    {
        var s = r.Add("Hardware identification");
        s.Rows.AddRange(hw.IdentityRows());
        if (hw.VendorInfo.ToolName.Length > 0) s.Rows.Add(("Vendor tool", $"{hw.VendorInfo.ToolName} — {hw.VendorInfo.ToolUrl}"));
        if (hw.ModelInfo is { } mi)
        {
            if (mi.KnownFirmware.Length > 0) s.Rows.Add(("Known firmware versions", string.Join(", ", mi.KnownFirmware) + (hw.Firmware.Length > 0 && mi.LatestKnownFirmware.Length > 0 && !hw.Firmware.Equals(mi.LatestKnownFirmware, StringComparison.OrdinalIgnoreCase) ? $" — installed {hw.Firmware} is not the latest known ({mi.LatestKnownFirmware})" : "")));
            if (mi.KnownIssues.Length > 0) s.Paragraphs.Add("Known issues: " + mi.KnownIssues);
        }
        var n = r.Add("NVMe controller & health log");
        n.Rows.AddRange(hw.NvmeRows());
        if (hw.AtaSmart is { } a)
        {
            var at = r.Add("ATA SMART attributes");
            at.Table.Add(new[] { "ID", "Attribute", "Current", "Worst", "Threshold", "Raw", "Status" });
            foreach (var x in a.Attributes) at.Table.Add(new[] { x.Id.ToString(), x.Name, x.Current.ToString(), x.Worst.ToString(), x.Threshold.ToString(), x.Raw.ToString(), x.Failing ? "FAILING" : "ok" });
        }
        if (hw.WindowsVolumes.Count > 0)
        {
            var w = r.Add("Windows' view of the volumes");
            foreach (var v in hw.WindowsVolumes) w.Bullets.Add(v.Display);
        }
    }

    public static Report FromHardware(HardwareInfo hw)
    {
        var r = new Report { Title = "Drive hardware report", Subtitle = $"{hw.Model}{(hw.Serial.Length > 0 ? $" — S/N {hw.Serial}" : "")}" };
        r.Summary = $"{hw.VendorInfo.Name} {hw.ModelInfo?.Family ?? hw.Model}, {hw.CapacityLabel}, seen over {hw.BusType}. {(hw.SmartAvailable ? "SMART data available." : "SMART data not available through this interface.")}";
        AddHardware(r, hw);
        return r;
    }

    public static Report FromImaging(ImageProgress p, string source, string target)
    {
        var r = new Report { Title = "Forensic image / clone report", Subtitle = $"{source} → {target}", Summary = $"{p.Phase}. {Format.Bytes(p.BytesDone)} of {Format.Bytes(p.BytesTotal)} copied, {p.BadSectorCount} unreadable sectors, {Format.Duration(p.Elapsed)} elapsed." };
        var s = r.Add("Result");
        s.Rows.Add(("Source", source)); s.Rows.Add(("Target", target)); s.Rows.Add(("Bytes copied", $"{p.BytesDone:N0} of {p.BytesTotal:N0}")); s.Rows.Add(("Unreadable sectors", p.BadSectorCount.ToString()));
        s.Rows.Add(("Bytes zero-filled", Format.Bytes(p.BytesBad + p.BytesSkipped))); s.Rows.Add(("Elapsed", Format.Duration(p.Elapsed))); s.Rows.Add(("Average rate", p.Elapsed.TotalSeconds > 0 ? Format.Rate(p.BytesDone / p.Elapsed.TotalSeconds) : "n/a"));
        if (p.Sha256 != null) s.Rows.Add(("SHA-256 (source as read)", p.Sha256));
        if (p.Md5 != null) s.Rows.Add(("MD5 (source as read)", p.Md5));
        if (p.TargetSha256 != null) s.Rows.Add(("SHA-256 (target verify)", p.TargetSha256 + (p.VerifyOk ? " — MATCH" : " — MISMATCH")));
        foreach (var m in p.Messages) s.Bullets.Add(m);
        if (p.BadRanges.Count > 0)
        {
            var b = r.Add("Unreadable ranges");
            b.Table.Add(new[] { "Byte offset", "Length" });
            foreach (var (o, l) in p.BadRanges.Take(500)) b.Table.Add(new[] { o.ToString(), l.ToString() });
            if (p.BadRanges.Count > 500) b.Paragraphs.Add($"… and {p.BadRanges.Count - 500} more (see the .badsectors.txt log).");
        }
        return r;
    }

    public static Report FromCopy(CopyProgress p, string source, string destination)
    {
        var r = new Report { Title = "File recovery report", Subtitle = $"{source} → {destination}", Summary = $"{p.FilesDone:N0} files processed: {p.FilesDone - p.FilesFailed - p.FilesSkipped:N0} copied, {p.FilesPartial} partially, {p.FilesSkipped} skipped, {p.FilesFailed} failed; {Format.Bytes(p.BytesDone)} in {Format.Duration(p.Elapsed)}." };
        var s = r.Add("Result");
        s.Rows.Add(("Files", $"{p.FilesDone:N0} of {p.FilesTotal:N0}")); s.Rows.Add(("Folders created", p.DirsCreated.ToString())); s.Rows.Add(("Bytes", $"{p.BytesDone:N0} of {p.BytesTotal:N0}"));
        s.Rows.Add(("Partially recovered (zero-filled sectors)", p.FilesPartial.ToString())); s.Rows.Add(("Skipped", p.FilesSkipped.ToString())); s.Rows.Add(("Failed", p.FilesFailed.ToString())); s.Rows.Add(("Elapsed", Format.Duration(p.Elapsed)));
        if (p.Errors.Count > 0)
        {
            var e = r.Add("Problems");
            e.Table.Add(new[] { "Path", "Problem", "Partial?" });
            foreach (var er in p.Errors.Take(2000)) e.Table.Add(new[] { er.Path, er.Message, er.Partial ? "yes" : "no" });
        }
        return r;
    }
}
