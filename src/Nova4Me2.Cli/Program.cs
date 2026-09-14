using System.Diagnostics;
using System.Text;
using Nova4Me2.Core.Analysis;
using Nova4Me2.Core.Devices;
using Nova4Me2.Core.Devices.Windows;
using Nova4Me2.Core.Hardware;
using Nova4Me2.Core.Ntfs;
using Nova4Me2.Core.Partitions;
using Nova4Me2.Core.Recovery;
using Nova4Me2.Core.Reports;
using Nova4Me2.Core.Util;
using Nova4Me2.Mount;

namespace Nova4Me2.Cli;

public static class Program
{
    private static bool _quiet;

    public static int Main(string[] argv)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var args = Args.Parse(argv);
        _quiet = args.Has("quiet");
        if (args.Has("verbose")) Log.MinLevel = LogLevel.Debug;
        using var logSub = Log.Subscribe(e => { if (!_quiet || e.Level >= LogLevel.Warn) Console.Error.WriteLine($"  [{e.Level}] {e.Message}"); });
        using var fileLog = args.Get("log") is { } lf ? Log.ToFile(lf) : null;
        if (args.Positional.Count == 0 || args.Has("help")) { Usage(); return args.Positional.Count == 0 ? 1 : 0; }
        string cmd = args.Positional[0].ToLowerInvariant();
        try
        {
            return cmd switch
            {
                "list" or "drives" => ListDrives(args),
                "info" => Info(args),
                "scan" => Scan(args),
                "ls" or "dir" => Ls(args),
                "copy" or "extract" => Copy(args),
                "image" => Image(args),
                "clone" => Clone(args),
                "health" or "check" => Health(args),
                "repair" or "fix" => Repair(args),
                "mount" => MountCmd(args),
                "stabilize-usb" or "usb" => StabilizeUsb(args),
                "protect" => Protect(args),
                "cat" => Cat(args),
                _ => throw new UsageException($"Unknown command '{cmd}'.")
            };
        }
        catch (UsageException ex) { Console.Error.WriteLine("error: " + ex.Message); Console.Error.WriteLine("Run 'nova4me2 --help' for usage."); return 2; }
        catch (OperationCanceledException) { Console.Error.WriteLine("Cancelled."); return 130; }
        catch (Exception ex) { Console.Error.WriteLine("error: " + ex.Message); if (args.Has("verbose")) Console.Error.WriteLine(ex); return 1; }
    }

    private static void Usage()
    {
        Console.WriteLine(@"Nova4Me2 — raw NTFS recovery for drives Windows calls RAW
Usage: nova4me2 <command> [options]

  list                                  List physical drives (Windows) with Windows' view of their volumes
  info <src> [--report FILE...]         Hardware / SMART / firmware identification (CPU-Z style)
  scan <src> [--deep]                   Show partitions and NTFS volumes (incl. backup boot sectors / signature scan)
  ls <src> [path] [--volume N] [-R] [--mft-scan] [--deleted] [--all] [--long]
  cat <src> <path> [--stream NAME]      Write a file's contents to stdout
  copy <src> <path>... --to DIR [--volume N] [--mft-scan] [--overwrite|--rename] [--ads] [--verify] [--report FILE]
  image <src> <target.img> [--partition N | --start BYTES --length BYTES] [--resume] [--single-pass] [--verify] [--md5] [--report FILE]
  clone <src> <targetdrive> [--partition N --target-offset BYTES] [--verify] --yes
  health <src> [--surface quick|full] [--no-smart] [--report FILE...]
  repair <src> <boot-sector|backup-boot-sector|gpt|mft-mirror|undo FILE> [--volume N] --yes
  mount <src> <letter> [--volume N] [--mft-scan] [--show-system]   (needs WinFsp)
  stabilize-usb [--status|--revert]     Stop Windows from suspending / re-probing the USB enclosure
  protect <N> [--online] [--status]     Take disk N offline + read-only in Windows (mount manager ignores it; raw reads still work)

<src> is a drive number (0), \\.\PhysicalDrive0, \\.\C:, or a raw image file (.img/.dd).
Global: --reconnect-timeout SEC (180)  --throttle MB/s  --chunk KB (1024)  --no-keepalive  --log FILE  --verbose  --quiet
Reports: any of .txt .html .pdf by extension. Nothing is ever written to the source unless you run 'repair' or 'clone' onto it.");
    }

    // ---------------------------------------------------------------- helpers
    private static ResilienceOptions Resilience(Args a) => new()
    {
        ReconnectTimeout = TimeSpan.FromSeconds(a.GetInt("reconnect-timeout", 180)),
        MaxChunk = Math.Max(4, a.GetInt("chunk", 1024)) * 1024,
        MaxBytesPerSecond = (long)(a.GetDouble("throttle", 0) * 1024 * 1024),
        Keepalive = a.Has("no-keepalive") ? null : TimeSpan.FromSeconds(4)
    };

    private static ResilientBlockDevice OpenSource(Args a, bool writable = false)
    {
        var dev = DeviceFactory.Open(a.Pos(1), Resilience(a), writable);
        dev.StateChanged += (_, s) => { if (s != ConnectionState.Closed) Console.Error.WriteLine($"  [link] {s}"); };
        dev.Reconnected += (_, d) => Console.Error.WriteLine($"  [link] re-attached: {d}");
        return dev;
    }

    private static (NtfsVolume vol, NtfsVolumeCandidate cand) OpenVolume(ResilientBlockDevice dev, Args a)
    {
        var cands = VolumeLocator.Find(dev, progress: new Progress<string>(m => Console.Error.WriteLine("  " + m)));
        if (cands.Count == 0) throw new Exception("No NTFS volume found on this source (try 'scan --deep').");
        int idx = a.GetInt("volume", 0);
        NtfsVolumeCandidate c;
        if (idx > 0) { if (idx > cands.Count) throw new UsageException($"--volume {idx}: only {cands.Count} volume(s) found."); c = cands[idx - 1]; }
        else { c = cands.OrderByDescending(x => x.Length).First(); if (cands.Count > 1) Console.Error.WriteLine($"  Using the largest of {cands.Count} NTFS volumes ({c.Display}); pick another with --volume N."); }
        var vol = NtfsVolume.Open(dev, c);
        foreach (var p in vol.Problems) Console.Error.WriteLine("  [volume] " + p);
        return (vol, c);
    }

    private static IDirectorySource Source(NtfsVolume vol, Args a)
    {
        if (!a.Has("mft-scan")) return new IndexDirectorySource(vol);
        Console.Error.Write("  Scanning $MFT…");
        int lastPct = -1;
        var idx = MftScanner.Scan(vol, a.Has("deleted"), new Progress<(long d, long t)>(p => { int pct = (int)(p.d * 100 / Math.Max(1, p.t)); if (pct / 10 != lastPct / 10) { lastPct = pct; Console.Error.Write($" {pct}%"); } }));
        Console.Error.WriteLine($"\r  MFT scan: {idx.InUseRecords:N0} live records, {idx.DeletedRecords:N0} deleted, {idx.Orphans.Count:N0} orphans in {Format.Duration(idx.Elapsed)}");
        return new MftIndexDirectorySource(vol, idx);
    }

    private static void SaveReports(Args a, Report r)
    {
        foreach (var f in a.GetAll("report")) { ReportWriter.Save(r, f); Console.Error.WriteLine($"  Report written: {f}"); }
    }

    private static string Bar(double frac, int w = 30) { int n = (int)Math.Round(frac * w); return "[" + new string('█', n) + new string('░', w - n) + "]"; }

    private static bool Confirm(Args a, string prompt)
    {
        if (a.Has("yes")) return true;
        Console.Write(prompt + " Type YES to continue: ");
        return Console.ReadLine()?.Trim() == "YES";
    }

    // ---------------------------------------------------------------- commands
    private static int ListDrives(Args a)
    {
        if (!OperatingSystem.IsWindows()) { Console.WriteLine("Drive enumeration is Windows-only. On other systems pass an image file or /dev node as <src>."); return 0; }
        var drives = DriveEnumerator.List();
        if (drives.Count == 0) { Console.WriteLine("No drives could be opened. Run as Administrator."); return 1; }
        foreach (var d in drives)
        {
            if (d.OpenError != null) { Console.WriteLine($"[{d.Number}] {d.OpenError}"); continue; }
            Console.WriteLine($"[{d.Number}] {d.Model}  {Format.Bytes(d.Length)}  {d.Storage.BusTypeName}{(d.Storage.IsUsb ? " (via USB bridge)" : "")}  S/N {d.Storage.Serial}{(d.IsSystemDisk ? "  ** SYSTEM DISK **" : "")}");
            Console.WriteLine($"      sector {d.SectorSize} B{(d.Storage.PhysicalSectorSize > 0 ? $" (physical {d.Storage.PhysicalSectorSize} B)" : "")}, firmware {d.Storage.Revision}");
            foreach (var v in d.Volumes) Console.WriteLine("      volume: " + v.Display);
        }
        return 0;
    }

    private static int Info(Args a)
    {
        HardwareInfo hw;
        int? num = DeviceFactory.ParseDriveNumber(a.Pos(1));
        if (OperatingSystem.IsWindows() && num is { } n) hw = HardwareInfo.Collect(n);
        else { using var dev = OpenSource(a); hw = HardwareInfo.ForDevice(dev, a.Pos(1)); }
        var r = ReportBuilder.FromHardware(hw);
        Console.Write(ReportWriter.ToText(r));
        SaveReports(a, r);
        return 0;
    }

    private static int Scan(Args a)
    {
        using var dev = OpenSource(a);
        Console.WriteLine($"Source: {dev.Description}, {Format.Bytes(dev.Length)}, sector {dev.SectorSize} B");
        var t = PartitionTable.Read(dev);
        Console.WriteLine($"Partition scheme: {t.Scheme}{(t.UsedBackupGpt ? " (recovered from backup GPT)" : "")}");
        foreach (var p in t.Partitions) Console.WriteLine($"  #{p.Index} {p.TypeName,-22} {Format.Bytes(p.StartOffset),10} +{Format.Bytes(p.Length),-10} {p.Name}");
        foreach (var p in t.Problems) Console.WriteLine("  ! " + p);
        var cands = VolumeLocator.Find(dev, t, scanIfNoneFound: true, new Progress<string>(m => Console.Error.WriteLine("  " + m)));
        if (a.Has("deep")) cands = VolumeLocator.Scan(dev, new Progress<string>(m => Console.Error.WriteLine("  " + m)), exhaustive: false).ToList();
        Console.WriteLine($"NTFS volumes: {cands.Count}");
        int i = 1;
        foreach (var c in cands)
        {
            try { using var v = NtfsVolume.Open(dev, c); c.Label = v.Info.Label; Console.WriteLine($"  [{i}] {c.Display}  NTFS {v.Info.Version}, {v.MftRecordCount:N0} MFT records{(v.Info.IsDirty ? ", DIRTY" : "")}{(v.MftLoadedFromMirror ? ", $MFT from mirror" : "")}"); }
            catch (Exception ex) { Console.WriteLine($"  [{i}] {c.Display}  (cannot open: {ex.Message})"); }
            foreach (var n in c.Notes) Console.WriteLine("       " + n);
            i++;
        }
        return 0;
    }

    private static int Ls(Args a)
    {
        using var dev = OpenSource(a);
        var (vol, _) = OpenVolume(dev, a);
        var src = Source(vol, a);
        string path = a.Positional.Count > 2 ? a.Positional[2] : "";
        var dir = path.Trim('\\', '/').Length == 0 ? src.Root : Find(src, path) ?? throw new Exception($"Path not found: {path}");
        bool all = a.Has("all"), deleted = a.Has("deleted"), longFmt = a.Has("long") || a.Has("l");
        long files = 0, dirs = 0, bytes = 0;
        void Walk(NtfsEntry d, string prefix)
        {
            List<NtfsEntry> kids;
            try { kids = src.List(d); } catch (Exception ex) { Console.WriteLine($"{prefix}<error: {ex.Message}>"); return; }
            foreach (var e in kids)
            {
                if (!all && e.IsMetaFile) continue;
                if (e.IsDeleted && !deleted) continue;
                string flags = (e.IsDirectory ? "d" : "-") + (e.IsCompressed ? "c" : "-") + (e.IsSparse ? "s" : "-") + (e.IsEncrypted ? "e" : "-") + (e.IsReparsePoint ? "r" : "-") + (e.IsHidden ? "h" : "-") + (e.IsDeleted ? "X" : "-");
                if (longFmt) Console.WriteLine($"{flags} {e.Modified:yyyy-MM-dd HH:mm} {(e.IsDirectory ? "" : e.Size.ToString("N0")),15} {prefix}{e.Name}{(e.IsReparsePoint ? $" <{e.ReparseKind}>" : "")}");
                else Console.WriteLine($"{prefix}{e.Name}{(e.IsDirectory ? "\\" : "")}{(e.IsDeleted ? "  (deleted)" : "")}");
                if (e.IsDirectory) dirs++; else { files++; bytes += e.Size; }
                if (e.IsDirectory && (a.Has("recursive") || a.Has("R")) && !e.IsReparsePoint) Walk(e, prefix + e.Name + "\\");
            }
        }
        if (!dir.IsDirectory) { Console.WriteLine($"{dir.Path}  {dir.Size:N0} bytes"); return 0; }
        Walk(dir, "");
        Console.Error.WriteLine($"  {files:N0} files ({Format.Bytes(bytes)}), {dirs:N0} folders");
        return 0;
    }

    private static NtfsEntry? Find(IDirectorySource src, string path)
    {
        var cur = src.Root;
        foreach (var part in path.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!cur.IsDirectory) return null;
            var next = src.List(cur).FirstOrDefault(e => string.Equals(e.Name, part, StringComparison.OrdinalIgnoreCase) && !e.IsDeleted)
                    ?? src.List(cur).FirstOrDefault(e => string.Equals(e.Name, part, StringComparison.OrdinalIgnoreCase));
            if (next == null) return null;
            cur = next;
        }
        return cur;
    }

    private static int Cat(Args a)
    {
        using var dev = OpenSource(a);
        var (vol, _) = OpenVolume(dev, a);
        var src = Source(vol, a);
        var e = Find(src, a.Pos(2)) ?? throw new Exception("Path not found.");
        using var s = vol.OpenFile(e, a.Get("stream") ?? "");
        using var o = Console.OpenStandardOutput();
        s.CopyTo(o);
        return 0;
    }

    private static int Copy(Args a)
    {
        string dest = a.Get("to") ?? throw new UsageException("--to DIR is required.");
        using var dev = OpenSource(a);
        var (vol, _) = OpenVolume(dev, a);
        var src = Source(vol, a);
        var roots = new List<NtfsEntry>();
        foreach (var p in a.Positional.Skip(2)) roots.Add(Find(src, p) ?? throw new Exception($"Path not found: {p}"));
        if (roots.Count == 0) roots.Add(src.Root);
        var opt = new CopyOptions
        {
            Collision = a.Has("overwrite") ? CollisionPolicy.Overwrite : a.Has("rename") ? CollisionPolicy.Rename : CollisionPolicy.Skip,
            CopyAlternateStreams = a.Has("ads"), ZeroFillUnreadable = !a.Has("no-zero-fill"), VerifyAfterCopy = a.Has("verify"), PreserveTimestamps = !a.Has("no-timestamps")
        };
        Directory.CreateDirectory(dest);
        var ex = new Extractor(src);
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        var sw = Stopwatch.StartNew();
        CopyProgress? last = null;
        var prog = new Progress<CopyProgress>(p =>
        {
            last = p;
            if (_quiet) return;
            string line = p.Enumerating ? $"  Enumerating… {p.FilesTotal:N0} files, {Format.Bytes(p.BytesTotal)}" : $"  {Bar(p.Fraction)} {p.Fraction * 100,5:0.0}%  {p.FilesDone:N0}/{p.FilesTotal:N0} files  {Format.Bytes(p.BytesDone)}  {Format.Rate(p.BytesPerSecond)}  {Trunc(p.CurrentFile, 40)}";
            Console.Error.Write("\r" + line.PadRight(Math.Max(1, Console.WindowWidth - 1))[..Math.Max(1, Console.WindowWidth - 1)]);
        });
        CopyProgress result;
        try { result = ex.Copy(roots, dest, opt, prog, cts.Token); }
        finally { Console.Error.WriteLine(); }
        Console.WriteLine($"Copied {result.FilesDone - result.FilesFailed - result.FilesSkipped:N0} files ({Format.Bytes(result.BytesDone)}) in {Format.Duration(result.Elapsed)}; {result.FilesPartial} partial, {result.FilesSkipped} skipped, {result.FilesFailed} failed.");
        foreach (var er in result.Errors.Take(50)) Console.WriteLine($"  {(er.Partial ? "partial" : "FAILED ")}: {er.Path}: {er.Message}");
        if (result.Errors.Count > 50) Console.WriteLine($"  … {result.Errors.Count - 50} more (see --report).");
        SaveReports(a, ReportBuilder.FromCopy(result, a.Pos(1), dest));
        return result.FilesFailed == 0 ? 0 : 3;
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : "…" + s[^(n - 1)..];

    private static (long start, long length) Range(Args a, ResilientBlockDevice dev)
    {
        if (a.Get("partition") is { } ps)
        {
            var t = PartitionTable.Read(dev);
            var p = t.Partitions.FirstOrDefault(x => x.Index == int.Parse(ps)) ?? throw new UsageException($"Partition {ps} not found ({t.Partitions.Count} partitions).");
            return (p.StartOffset, p.Length);
        }
        return (a.GetLong("start", 0), a.GetLong("length", 0));
    }

    private static int Image(Args a)
    {
        using var dev = OpenSource(a);
        string target = a.Pos(2);
        var (start, length) = Range(a, dev);
        if (length == 0) length = dev.Length - start;
        if (File.Exists(target) && !a.Has("resume") && !Confirm(a, $"{target} exists and will be overwritten.")) return 1;
        using var dst = a.Has("resume") && File.Exists(target) ? new FileBlockDevice(target, writable: true) : FileBlockDevice.Create(target, length);
        var opt = new ImageOptions { StartOffset = start, Length = length, TwoPass = !a.Has("single-pass"), VerifyTarget = a.Has("verify"), ComputeMd5 = a.Has("md5"), Resume = a.Has("resume"), ChunkSize = Math.Max(64, a.GetInt("chunk", 4096)) * 1024 };
        var p = RunImaging(a, dev, dst, opt);
        Console.WriteLine($"{p.Phase}: {Format.Bytes(p.BytesDone)} in {Format.Duration(p.Elapsed)}, {p.BadSectorCount} unreadable sectors{(p.Sha256 != null ? $", SHA-256 {p.Sha256}" : "")}{(p.TargetSha256 != null ? $", verify {(p.VerifyOk ? "OK" : "MISMATCH")}" : "")}");
        foreach (var m in p.Messages) Console.WriteLine("  " + m);
        SaveReports(a, ReportBuilder.FromImaging(p, a.Pos(1), target));
        return 0;
    }

    private static ImageProgress RunImaging(Args a, ResilientBlockDevice dev, IBlockDevice dst, ImageOptions opt)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        var prog = new Progress<ImageProgress>(p =>
        {
            if (_quiet) return;
            string line = $"  {p.Phase}: {Bar(p.Fraction)} {p.Fraction * 100,5:0.0}%  {Format.Bytes(p.BytesDone)}/{Format.Bytes(p.BytesTotal)}  {Format.Rate(p.BytesPerSecond)}  bad {p.BadSectorCount}  ETA {(p.Eta is { } e ? Format.Duration(e) : "…")}";
            Console.Error.Write("\r" + line.PadRight(Math.Max(1, Console.WindowWidth - 1))[..Math.Max(1, Console.WindowWidth - 1)]);
        });
        try { return Imager.Run(dev, dst, opt, prog, cts.Token); }
        finally { Console.Error.WriteLine(); }
    }

    private static int Clone(Args a)
    {
        if (!OperatingSystem.IsWindows() && !File.Exists(a.Pos(2))) throw new UsageException("On this platform the clone target must be an existing image file or /dev node.");
        using var dev = OpenSource(a);
        var (start, length) = Range(a, dev);
        if (length == 0) length = dev.Length - start;
        string targetSpec = a.Pos(2);
        if (OperatingSystem.IsWindows() && DeviceFactory.ParseDriveNumber(targetSpec) is { } tn)
        {
            if (tn == DriveEnumerator.SystemDiskNumber()) throw new Exception("Refusing to clone onto the Windows system disk.");
            if (dev.Inner is WindowsPhysicalDrive wp && wp.DevicePath.Equals($@"\\.\PhysicalDrive{tn}", StringComparison.OrdinalIgnoreCase)) throw new Exception("Source and target are the same drive.");
        }
        using var dst = DeviceFactory.Open(targetSpec, new ResilienceOptions { Keepalive = null }, writable: true);
        long targetOffset = a.GetLong("target-offset", 0);
        if (dst.Length < targetOffset + length) throw new Exception($"Target ({Format.Bytes(dst.Length)}) is smaller than the data to copy ({Format.Bytes(targetOffset + length)}).");
        Console.WriteLine($"Source : {dev.Description}  range {Format.Bytes(start)} +{Format.Bytes(length)}");
        Console.WriteLine($"Target : {dst.Description}  at offset {Format.Bytes(targetOffset)}");
        Console.WriteLine("EVERYTHING on the target in that range will be overwritten.");
        if (!Confirm(a, "Continue?")) return 1;
        var opt = new ImageOptions { StartOffset = start, Length = length, TargetOffset = targetOffset, TwoPass = !a.Has("single-pass"), VerifyTarget = a.Has("verify"), ComputeMd5 = a.Has("md5"), LogPrefix = Path.Combine(Path.GetTempPath(), "nova4me2-clone") };
        var p = RunImaging(a, dev, dst, opt);
        Console.WriteLine($"{p.Phase}: {Format.Bytes(p.BytesDone)} in {Format.Duration(p.Elapsed)}, {p.BadSectorCount} unreadable sectors{(p.Sha256 != null ? $", SHA-256 {p.Sha256}" : "")}");
        foreach (var m in p.Messages) Console.WriteLine("  " + m);
        SaveReports(a, ReportBuilder.FromImaging(p, a.Pos(1), targetSpec));
        return 0;
    }

    private static int Health(Args a)
    {
        using var dev = OpenSource(a);
        HardwareInfo? hw = null;
        if (OperatingSystem.IsWindows() && !a.Has("no-smart") && DeviceFactory.ParseDriveNumber(a.Pos(1)) is { } n) { try { hw = HardwareInfo.Collect(n); } catch (Exception ex) { Console.Error.WriteLine("  hardware query failed: " + ex.Message); } }
        var h = HealthAnalyzer.Analyze(dev, null, hw?.Model, hw?.Serial, hw?.BusType, new Progress<string>(m => Console.Error.WriteLine("  " + m)));
        h.NvmeHealth = hw?.NvmeHealth;
        h.AtaSmart = hw?.AtaSmart;
        if (a.Get("surface") is { } mode)
        {
            var so = new SurfaceScanOptions { SampleEvery = mode.Equals("full", StringComparison.OrdinalIgnoreCase) ? 1 : 16 };
            var prog = new Progress<SurfaceScanResult>(r => { if (!_quiet) Console.Error.Write($"\r  Surface scan {Bar(r.Fraction)} {r.Fraction * 100,5:0.0}%  bad {r.BadSectors}  slow {r.ChunksSlow}  {Format.Rate(r.BytesPerSecond)}   "); });
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
            try { h.Surface = SurfaceScan.Run(dev, so, prog, cts.Token); } catch (OperationCanceledException) { }
            Console.Error.WriteLine();
            // Re-score with the surface results.
            h.BootFix = HealthAnalyzer.AssessBootFix(h);
        }
        var r = ReportBuilder.FromHealth(h, hw);
        Console.Write(ReportWriter.ToText(r));
        SaveReports(a, r);
        return h.Score >= 70 ? 0 : 4;
    }

    private static int Repair(Args a)
    {
        string action = a.Pos(2).ToLowerInvariant();
        using var dev = OpenSource(a, writable: true);
        RepairResult res;
        switch (action)
        {
            case "undo":
                if (!Confirm(a, $"Write the sectors saved in {a.Pos(3)} back to {dev.Description}?")) return 1;
                res = RepairEngine.Undo(dev, a.Pos(3));
                break;
            case "gpt":
                if (!Confirm(a, $"Rebuild the primary GPT of {dev.Description} from its backup?")) return 1;
                res = RepairEngine.RestoreGptFromBackup(dev);
                break;
            case "boot-sector":
            case "backup-boot-sector":
            case "mft-mirror":
                {
                    var cands = VolumeLocator.Find(dev);
                    if (cands.Count == 0) throw new Exception("No NTFS volume found.");
                    int idx = a.GetInt("volume", 0);
                    var c = idx > 0 ? cands[idx - 1] : cands.OrderByDescending(x => x.Length).First();
                    Console.WriteLine($"Volume: {c.Display}");
                    foreach (var n in c.Notes) Console.WriteLine("  " + n);
                    if (action == "boot-sector") { if (!Confirm(a, "Overwrite the primary NTFS boot sector with the backup copy?")) return 1; res = RepairEngine.RestoreBootSectorFromBackup(dev, c); }
                    else if (action == "backup-boot-sector") { if (!Confirm(a, "Overwrite the backup NTFS boot sector with the primary?")) return 1; res = RepairEngine.RestoreBackupBootSectorFromPrimary(dev, c); }
                    else { var vol = NtfsVolume.Open(dev, c); if (!Confirm(a, "Overwrite damaged $MFT system records with the $MFTMirr copies?")) return 1; res = RepairEngine.RestoreMftFromMirror(dev, vol); }
                    break;
                }
            default: throw new UsageException("repair action must be boot-sector | backup-boot-sector | gpt | mft-mirror | undo FILE");
        }
        Console.WriteLine((res.Success ? "OK: " : "FAILED: ") + res.Message);
        return res.Success ? 0 : 1;
    }

    private static int MountCmd(Args a)
    {
        if (!OperatingSystem.IsWindows()) throw new UsageException("mount is Windows-only (WinFsp).");
        if (!MountSession.IsWinFspInstalled(out var detail)) throw new Exception(detail);
        using var dev = OpenSource(a);
        var (vol, _) = OpenVolume(dev, a);
        var src = Source(vol, a);
        using var session = MountSession.Mount(src, a.Pos(2), a.Has("show-system"));
        Console.WriteLine($"Mounted {vol.Info.Label} read-only at {session.MountPoint}. Press Ctrl+C to unmount.");
        var done = new ManualResetEventSlim();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.Set(); };
        while (!done.Wait(2000))
        {
            if (!_quiet) Console.Error.Write($"\r  link {dev.State}  reads {session.FileSystem.Reads:N0}  served {Format.Bytes(session.FileSystem.BytesServed)}  reconnects {dev.Stats.Reconnects}   ");
        }
        Console.Error.WriteLine();
        return 0;
    }

    private static int Protect(Args a)
    {
        if (!OperatingSystem.IsWindows()) throw new UsageException("protect is Windows-only.");
        int n = DeviceFactory.ParseDriveNumber(a.Pos(1)) ?? throw new UsageException("protect needs a physical drive number.");
        if (a.Has("status"))
        {
            var at = DiskControl.GetAttributes(n);
            Console.WriteLine($"PhysicalDrive{n}: {(at.Queried ? $"{(at.Offline ? "OFFLINE" : "online")}, {(at.ReadOnly ? "read-only" : "writable")}" : "attributes not available")}; automount {(DiskControl.IsAutomountEnabled() ? "ON" : "off")}");
            return 0;
        }
        if (n == DriveEnumerator.SystemDiskNumber()) throw new Exception("Refusing to change the system disk.");
        bool online = a.Has("online");
        DiskControl.SetAttributes(n, offline: !online, readOnly: !online);
        if (!online) { try { DiskControl.SetAutomount(false); } catch (Exception ex) { Console.Error.WriteLine("  automount: " + ex.Message); } }
        Console.WriteLine(online ? $"PhysicalDrive{n} is online and writable again." : $"PhysicalDrive{n} is now offline and read-only in Windows; automount disabled. Nova4Me2 can still read it. Undo with: nova4me2 protect {n} --online");
        return 0;
    }

    private static int StabilizeUsb(Args a)
    {
        if (!OperatingSystem.IsWindows()) throw new UsageException("stabilize-usb is Windows-only.");
        if (a.Has("status"))
        {
            var s = UsbStabilizer.Status();
            Console.WriteLine($"USB selective suspend disabled: AC {Y(s.SelectiveSuspendDisabledAc)}, DC {Y(s.SelectiveSuspendDisabledDc)}");
            Console.WriteLine($"Disk idle time-out = never: {Y(s.DiskIdleTimeoutNever)}");
            Console.WriteLine($"Windows automount disabled: {Y(s.AutomountDisabled)}");
            Console.WriteLine($"Disk I/O time-out: {(s.DiskTimeoutSeconds?.ToString() ?? "default")} s");
            Console.WriteLine($"USB storage devices: {s.UsbStorageDevicesFound} found, {s.UsbStorageDevicesTuned} with power management off");
            foreach (var n in s.Notes) Console.WriteLine("  " + n);
            return 0;
        }
        var log = a.Has("revert") ? UsbStabilizer.Revert() : UsbStabilizer.Apply();
        foreach (var l in log) Console.WriteLine("  " + l);
        if (!a.Has("revert")) Console.WriteLine("Unplug and re-plug the enclosure for the device-level settings to take effect.");
        return 0;
        static string Y(bool? b) => b == null ? "?" : b.Value ? "yes" : "no";
    }
}
