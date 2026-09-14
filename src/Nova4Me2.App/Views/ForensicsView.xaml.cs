using System.IO;
using System.Windows;
using System.Windows.Controls;
using Nova4Me2.App.Services;
using Nova4Me2.Core.Analysis;
using Nova4Me2.Core.Forensics;
using Nova4Me2.Core.Ntfs;
using Nova4Me2.Core.Recovery;
using Nova4Me2.Core.Util;

namespace Nova4Me2.App.Views;

public sealed class AdsRow
{
    public AdsEntry A { get; init; } = null!;
    public NtfsEntry File => A.File;
    public string StreamName => A.StreamName;
    public string SizeText => Format.Bytes(A.Size);
    public string Kind => A.Kind;
    public string Content => A.Content;
    public string ExtractedPath => A.ExtractedPath ?? "";
}

public sealed class CarveRow
{
    public CarvedFile C { get; init; } = null!;
    public string OffsetText => C.Offset.ToString("N0");
    public string TypeName => C.Signature.Name;
    public string SizeText => Format.Bytes(C.Length);
    public string Confidence => C.Confidence;
    public string Notes => (C.Nested ? "nested " : "") + (C.Truncated ? "truncated" : "");
    public string ExtractedPath => C.ExtractedPath ?? "";
}

public sealed class StegoRow
{
    public StegoFinding F { get; init; } = null!;
    public string SeverityText => F.Severity.ToString();
    public string Title => F.Title;
    public string Detail => F.Detail;
    public string ExtractableText => F.Extractable ? "yes" : "";
}

public sealed class BadRow
{
    public string Where { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Lost { get; init; } = "";
    public string Size { get; init; } = "";
    public string Range { get; init; } = "";
    public string Hits { get; init; } = "";
    public string Impact { get; init; } = "";

    public static BadRow From(AffectedFile f) => new()
    {
        Where = f.Display + (f.Attribute.Length > 0 && f.Attribute != "$DATA" ? "  [" + f.Attribute + "]" : ""),
        Kind = f.Kind, Lost = f.BytesLost > 0 ? Format.Bytes(f.BytesLost) + (f.FileSize > 0 ? $" ({f.PercentLost:0.##}%)" : "") : "none",
        Size = f.FileSize > 0 ? Format.Bytes(f.FileSize) : "", Range = f.WhereText, Hits = f.Hits.ToString(), Impact = f.Impact,
    };

    public static BadRow Area(BadSectorArea a, long bytes, int hits) => new()
    {
        Where = BadSectorReport.AreaName(a), Kind = "area", Lost = a is BadSectorArea.NtfsFile or BadSectorArea.NtfsMetadata ? Format.Bytes(bytes) : "none", Size = Format.Bytes(bytes), Hits = hits.ToString(),
        Impact = a switch
        {
            BadSectorArea.NtfsUnallocated => "Free space: no file was using these clusters. Only matters for carving deleted data.",
            BadSectorArea.NtfsSlack => "Past the end of a file's data: nothing lost.",
            BadSectorArea.NtfsDeletedFile => "Former contents of deleted files: only matters if you undelete them.",
            BadSectorArea.Unpartitioned => "Alignment gap outside every partition: nothing lost.",
            BadSectorArea.PartitionTable => "Partition table sectors: Repair can rebuild them from the backup copy.",
            BadSectorArea.NonNtfsPartition => "Inside a non-NTFS partition (EFI/recovery): not analysed.",
            BadSectorArea.NtfsUnowned => "Allocated but not referenced by any file (lost clusters): nothing lost.",
            BadSectorArea.NtfsUnreadable => "NTFS structures of this volume could not be read; position only.",
            _ => "",
        },
    };
}

public partial class ForensicsView : UserControl, INovaView
{
    private ForensicProject? _project;
    private CancellationTokenSource? _cts;
    private ICarveSource? _carveSource;
    private ICarveSource? _stegoSource;
    private StegoReport? _stegoReport;
    private DecodedImage? _stegoImage;
    private ClusterOwnerMap? _owners;
    private BadSectorReport? _badReport;
    private CancellationTokenSource? _badCts;
    private List<NtfsForensics.SlackEntry> _slack = new();

    public ForensicsView()
    {
        InitializeComponent();
        foreach (var cat in Signatures.Categories)
            TypePanel.Children.Add(new CheckBox { Content = cat, IsChecked = cat is "Images" or "Documents" or "Archives" or "Media", Tag = cat });
        ProjectName.Text = $"Project-{DateTime.Now:yyyyMMdd-HHmm}";
        Ui.State.SourceChanged += () => Dispatcher.BeginInvoke(OnShown);
        RefreshProjects();
    }

    public void OnShown()
    {
        bool has = Ui.State.HasVolume;
        AdsScanButton.IsEnabled = CarveStart.IsEnabled = has || ScopeFile.IsChecked == true;
        StegoRun.IsEnabled = true;
        if (_project == null) ProjectPath.Text = "No project yet — name one and press Start.";
        if (BadLogPath.Text.Trim().Length == 0 && BadSectorAnalyzer.FindLogFor(Ui.State.SourceSpec) is { } found) { BadLogPath.Text = found; BadStatus.Text = "log found next to the image"; }
        BadRun.IsEnabled = Ui.State.HasDevice;
    }

    // ---- bad sectors ----
    private void BadBrowse_Click(object sender, RoutedEventArgs e)
    {
        var f = Ui.OpenFile("Bad-sector log", "Bad sector logs (*.badsectors.txt;*.map;*.mapfile;*.txt;*.log)|*.badsectors.txt;*.map;*.mapfile;*.txt;*.log|All files|*.*");
        if (f != null) BadLogPath.Text = f;
    }

    private async void BadAnalyze_Click(object sender, RoutedEventArgs e)
    {
        var dev = Ui.State.Device;
        if (dev == null) { Ui.Main.Toast("Open the image or drive first", "Select the cloned image in the drive selector, then analyse its log.", true); return; }
        string path = BadLogPath.Text.Trim();
        if (path.Length == 0 || !File.Exists(path)) { Ui.Main.Toast("No log", "Pick the .badsectors.txt written next to the image.", true); return; }
        BadRun.IsEnabled = false; BadStop.IsEnabled = true;
        BadStatus.Text = "";
        BadHeadline.Text = "Analysing… on a large image this takes a while: every MFT record is read once to learn which file owns each cluster.";
        BadVerdict.Text = "";
        BadList.ItemsSource = null; BadDetail.Text = "";
        BadProgressCard.Visibility = Visibility.Visible;
        Animations.FadeIn(BadProgressCard);
        BadPhase.Text = "Reading the log…"; BadPercent.Text = ""; BadElapsed.Text = "";
        BadBar.IsIndeterminate = true; BadBar.Value = 0;
        Animations.Pulse(BadPhase);
        var started = DateTime.UtcNow;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => BadElapsed.Text = $"elapsed {Format.Duration(DateTime.UtcNow - started)}";
        timer.Start();
        var table = Ui.State.Partitions; var vols = Ui.State.Volumes;
        _badCts = new CancellationTokenSource();
        var ct = _badCts.Token;
        try
        {
            var prog = new Progress<BadSectorProgress>(m =>
            {
                BadPhase.Text = m.Phase;
                if (m.Determinate)
                {
                    if (BadBar.IsIndeterminate) BadBar.IsIndeterminate = false;
                    Animations.Grow(BadBar, m.Fraction * 100);
                    BadPercent.Text = m.Percent + "%";
                    var el = DateTime.UtcNow - started;
                    if (m.Fraction > 0.02 && m.Fraction < 1) BadElapsed.Text = $"elapsed {Format.Duration(el)} · about {Format.Duration(TimeSpan.FromSeconds(el.TotalSeconds / m.Fraction * (1 - m.Fraction)))} left in this step";
                }
                else { BadBar.IsIndeterminate = true; BadPercent.Text = ""; }
            });
            var report = await Task.Run(() =>
            {
                var log = BadSectorLog.Load(path, dev.SectorSize);
                return BadSectorAnalyzer.Analyze(dev, log, table, vols, prog, ct);
            }, ct);
            _badReport = report;
            BadHeadline.Text = report.Headline;
            BadVerdict.Text = report.Verdict;
            var rows = report.Files.OrderByDescending(f => f.BytesLost).ThenBy(f => f.Display).Select(BadRow.From).ToList();
            foreach (var kv in report.BytesByArea.OrderByDescending(k => k.Value))
                if (kv.Key is not (BadSectorArea.NtfsFile or BadSectorArea.NtfsMetadata or BadSectorArea.NtfsDeletedFile))
                    rows.Add(BadRow.Area(kv.Key, kv.Value, report.Hits.Count(h => h.Area == kv.Key)));
            BadList.ItemsSource = rows;
            BadDetail.Text = report.ToText();
            BadStatus.Text = $"{report.TotalSectors:N0} sectors ({Format.Bytes(report.TotalBytes)}) in {report.RangeCount:N0} ranges — {report.UserFilesAffected} file(s) with lost data";
            Ui.Main.Toast("Bad-sector analysis", report.Headline, report.UserFilesAffected > 0);
        }
        catch (OperationCanceledException) { BadStatus.Text = "stopped"; BadHeadline.Text = "Analysis stopped."; }
        catch (Exception ex) { BadStatus.Text = "failed"; BadHeadline.Text = "Analysis failed: " + ex.GetBaseException().Message; Ui.Main.Toast("Bad-sector analysis failed", ex.GetBaseException().Message, true); }
        finally
        {
            timer.Stop();
            Animations.StopPulse(BadPhase);
            BadProgressCard.Visibility = Visibility.Collapsed;
            BadRun.IsEnabled = true; BadStop.IsEnabled = false;
            _badCts?.Dispose(); _badCts = null;
        }
    }

    private void BadStop_Click(object sender, RoutedEventArgs e) => _badCts?.Cancel();

    private void BadSave_Click(object sender, RoutedEventArgs e)
    {
        if (_badReport == null) { Ui.Main.Toast("Nothing to save", "Run Analyze first.", true); return; }
        try
        {
            var proj = Project();
            string txt = ForensicProject.UniquePath(proj.ReportsDir, "badsectors.txt");
            string csv = ForensicProject.UniquePath(proj.ReportsDir, "badsectors.csv");
            _badReport.WriteText(txt); _badReport.WriteCsv(csv);
            proj.Note($"bad-sector analysis of {_badReport.LogPath} -> {txt}, {csv}");
            BadStatus.Text = "saved: " + txt;
            Ui.Main.Toast("Report saved", proj.ReportsDir);
        }
        catch (Exception ex) { Ui.Main.Toast("Save failed", ex.Message, true); }
    }

    // ---- project ----
    private void RefreshProjects()
    {
        ProjectList.SelectionChanged -= ProjectList_SelectionChanged;
        ProjectList.ItemsSource = ForensicProject.List().Select(p => p.Name).ToList();
        ProjectList.SelectionChanged += ProjectList_SelectionChanged;
    }

    private ForensicProject Project()
    {
        if (_project == null) StartProject_Click(this, new RoutedEventArgs());
        return _project!;
    }

    private void StartProject_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _project = ForensicProject.Create(ProjectName.Text, Ui.State.SourceName);
            ProjectPath.Text = _project.Root;
            ProjectName.Text = _project.Name;
            RefreshProjects();
            Ui.Main.Toast("Project ready", _project.Root);
        }
        catch (Exception ex) { Ui.Main.Toast("Cannot create project", ex.Message, true); }
    }

    private void ProjectList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProjectList.SelectedItem is not string name) return;
        var p = ForensicProject.List().FirstOrDefault(x => x.Name == name);
        if (p == null) return;
        _project = p;
        ProjectName.Text = p.Name;
        ProjectPath.Text = p.Root;
    }

    private void OpenProject_Click(object sender, RoutedEventArgs e) { if (_project != null) Ui.OpenFolder(_project.Root); }

    // ---- ADS ----
    private async void AdsScan_Click(object sender, RoutedEventArgs e)
    {
        var src = Ui.State.Source;
        if (src == null) { Ui.Main.Toast("Open a drive first", "", true); return; }
        AdsScanButton.IsEnabled = false;
        try
        {
            var prog = new Progress<(int Files, int Found)>(p => AdsStatus.Text = $"{p.Files:N0} files scanned, {p.Found} streams");
            var list = await Task.Run(() => AdsScanner.Scan(src, prog));
            AdsList.ItemsSource = list.Select(a => new AdsRow { A = a }).ToList();
            AdsStatus.Text = $"{list.Count} alternate data streams found";
        }
        catch (Exception ex) { Ui.Main.Toast("ADS scan failed", ex.Message, true); }
        finally { AdsScanButton.IsEnabled = true; }
    }

    private async Task ExtractAds(IEnumerable<AdsRow> rows)
    {
        var vol = Ui.State.Volume; if (vol == null) return;
        var proj = Project();
        int n = 0;
        foreach (var r in rows.ToList())
        {
            try { await Task.Run(() => AdsScanner.Extract(vol, r.A, proj.AdsDir)); proj.Note($"ADS {r.A.Display} -> {r.A.ExtractedPath}"); n++; }
            catch (Exception ex) { proj.Note($"ADS {r.A.Display} FAILED: {ex.Message}"); }
        }
        AdsList.Items.Refresh();
        Ui.Main.Toast("Streams extracted", $"{n} stream(s) written to {proj.AdsDir}");
    }

    private void AdsExtractSelected_Click(object sender, RoutedEventArgs e) => _ = ExtractAds(AdsList.SelectedItems.Cast<AdsRow>());
    private void AdsExtractAll_Click(object sender, RoutedEventArgs e) => _ = ExtractAds(AdsList.Items.Cast<AdsRow>());

    // ---- carving ----
    private void CarveBrowse_Click(object sender, RoutedEventArgs e)
    {
        var p = Ui.OpenFile("Choose a local file to carve inside", "All files (*.*)|*.*");
        if (p != null) { CarveFilePath.Text = p; ScopeFile.IsChecked = true; }
    }

    private (ICarveSource src, List<(long, long)> regions, int align)? BuildCarveScope()
    {
        var st = Ui.State;
        if (ScopeFile.IsChecked == true)
        {
            string path = CarveFilePath.Text.Trim();
            if (path.Length == 0) { Ui.Main.Toast("Enter a file path", "A path on the volume or a local file.", true); return null; }
            if (File.Exists(path)) { var s = new StreamCarveSource(File.OpenRead(path), Path.GetFileName(path)); return (s, FileCarver.WholeSource(s), 1); }
            if (st.Volume == null) { Ui.Main.Toast("Open a drive first", "", true); return null; }
            var e = st.Volume.Resolve(path);
            if (e == null || e.IsDirectory) { Ui.Main.Toast("File not found on the volume", path, true); return null; }
            var fs = new StreamCarveSource(st.Volume.OpenFile(e), e.Name);
            return (fs, FileCarver.WholeSource(fs), 1);
        }
        if (st.Device == null) { Ui.Main.Toast("Open a drive first", "", true); return null; }
        if (ScopeDisk.IsChecked == true) { var d = new DeviceCarveSource(st.Device); return (d, FileCarver.WholeSource(d), st.Device.SectorSize); }
        if (st.Volume == null) { Ui.Main.Toast("No NTFS volume selected", "", true); return null; }
        if (ScopeVolume.IsChecked == true) { var v = new DeviceCarveSource(st.Device, st.Volume.Offset, st.Volume.Length, "volume"); return (v, FileCarver.WholeSource(v), st.Device.SectorSize); }
        var bm = ClusterBitmap.Load(st.Volume);
        var u = new DeviceCarveSource(st.Device, st.Volume.Offset, st.Volume.Length, "unallocated space");
        int cs = st.Volume.ClusterSize;
        return (u, bm.UnallocatedRanges().Select(r => (r.Lcn * (long)cs, r.Count * (long)cs)).ToList(), cs);
    }

    private async void CarveStart_Click(object sender, RoutedEventArgs e)
    {
        var scope = await Task.Run(() => { try { return Dispatcher.Invoke(BuildCarveScope); } catch (Exception ex) { Dispatcher.Invoke(() => Ui.Main.Toast("Cannot prepare scan", ex.Message, true)); return null; } });
        if (scope == null) return;
        var (src, regions, align) = scope.Value;
        _carveSource?.Dispose();
        _carveSource = src;
        var types = new HashSet<string>(Signatures.All.Where(s => TypePanel.Children.OfType<CheckBox>().Any(c => c.IsChecked == true && (string)c.Tag == s.Category)).Select(s => s.Name));
        var opt = new CarveOptions { Alignment = align, IncludeNested = CarveNested.IsChecked == true || align == 1, Types = types.Count > 0 ? types : null };
        _cts = new CancellationTokenSource();
        CarveStart.IsEnabled = false; CarveStop.IsEnabled = true;
        CarveList.ItemsSource = null;
        var prog = new Progress<CarveProgress>(p => { Animations.Grow(CarveProgress, p.Fraction * 100); CarveStatus.Text = $"{p.Phase}: {Format.Bytes(p.BytesDone)} of {Format.Bytes(p.BytesTotal)} · {p.Found} files · {Format.Rate(p.BytesPerSecond)} · {Format.Duration(p.Elapsed)}"; });
        try
        {
            var found = await Task.Run(() => FileCarver.Scan(src, regions, opt, prog, _cts.Token));
            CarveList.ItemsSource = found.Select(c => new CarveRow { C = c }).ToList();
            CarveStatus.Text = $"Done: {found.Count} files found in {src.Name}.";
            Ui.Main.Toast("Carving complete", $"{found.Count} file(s) found.");
        }
        catch (OperationCanceledException) { CarveStatus.Text = "Stopped."; }
        catch (Exception ex) { Ui.Main.Toast("Carving failed", ex.GetBaseException().Message, true); }
        finally { _cts = null; CarveStart.IsEnabled = true; CarveStop.IsEnabled = false; }
    }

    private void CarveStop_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private async Task ExtractCarved(IEnumerable<CarveRow> rows)
    {
        if (_carveSource == null) return;
        var proj = Project();
        var list = rows.ToList();
        int n = 0;
        foreach (var r in list)
        {
            try { await Task.Run(() => FileCarver.Extract(_carveSource, r.C, Path.Combine(proj.CarvedDir, r.C.Signature.Extension))); proj.Note($"carved {r.C} -> {r.C.ExtractedPath}"); n++; }
            catch (Exception ex) { proj.Note($"carve extract {r.C} FAILED: {ex.Message}"); }
        }
        CarveList.Items.Refresh();
        Ui.Main.Toast("Carved files extracted", $"{n} file(s) written under {proj.CarvedDir}");
    }

    private void CarveExtractSelected_Click(object sender, RoutedEventArgs e) => _ = ExtractCarved(CarveList.SelectedItems.Cast<CarveRow>());
    private void CarveExtractAll_Click(object sender, RoutedEventArgs e) => _ = ExtractCarved(CarveList.Items.Cast<CarveRow>());

    private void CarvePreview_Click(object sender, RoutedEventArgs e)
    {
        if (_carveSource == null || CarveList.SelectedItem is not CarveRow r) return;
        var buf = new byte[(int)Math.Min(512, r.C.Length)];
        _carveSource.Read(r.C.Offset, buf);
        Tabs.SelectedIndex = 3;
        TskOutput.Text = $"{r.C}\n\n" + NtfsForensics.HexDump(buf, r.C.Offset);
    }

    // ---- stego ----
    private void StegoBrowse_Click(object sender, RoutedEventArgs e)
    {
        var p = Ui.OpenFile("Choose a local file to analyse", "All files (*.*)|*.*");
        if (p != null) StegoPath.Text = p;
    }

    private async void Stego_Click(object sender, RoutedEventArgs e)
    {
        string path = StegoPath.Text.Trim();
        if (path.Length == 0) { Ui.Main.Toast("Enter a file path", "A path on the volume or a local file.", true); return; }
        _stegoSource?.Dispose();
        try
        {
            if (File.Exists(path)) _stegoSource = new StreamCarveSource(File.OpenRead(path), Path.GetFileName(path));
            else
            {
                var vol = Ui.State.Volume;
                var en = vol?.Resolve(path);
                if (vol == null || en == null || en.IsDirectory) { Ui.Main.Toast("File not found", path, true); return; }
                _stegoSource = new StreamCarveSource(vol.OpenFile(en), en.Name);
            }
        }
        catch (Exception ex) { Ui.Main.Toast("Cannot open file", ex.Message, true); return; }
        StegoRun.IsEnabled = false;
        StegoSummary.Text = "Analysing…";
        try
        {
            var src = _stegoSource;
            var rep = await Task.Run(() => StegoAnalyzer.Analyze(src));
            _stegoReport = rep;
            _stegoImage = await Task.Run(() => ImageDecoder.TryDecode(src, FileCarver.Identify(ReadHead(src))));
            StegoSummary.Text = $"{rep.Name}: {rep.DetectedType} · {Format.Bytes(rep.Length)} · entropy {rep.OverallEntropy:0.00} bits/byte · logical end {(rep.LogicalEnd?.ToString("N0") ?? "unknown")} · suspicion score {rep.Score}/100";
            StegoLsbText.Text = rep.LsbChiSquareScore is { } l ? $"LSB chi-square score {l:0.00}: {rep.LsbVerdict}" : "LSB analysis: not applicable (uncompressed BMP or 8-bit non-interlaced PNG only)";
            StegoList.ItemsSource = rep.Findings.Select(f => new StegoRow { F = f }).ToList();
        }
        catch (Exception ex) { Ui.Main.Toast("Analysis failed", ex.GetBaseException().Message, true); }
        finally { StegoRun.IsEnabled = true; }
    }

    private static byte[] ReadHead(ICarveSource s) { var b = new byte[(int)Math.Min(64 * 1024, s.Length)]; if (b.Length > 0) s.Read(0, b); return b; }

    private async Task ExtractStego(IEnumerable<StegoRow> rows)
    {
        if (_stegoSource == null || _stegoReport == null) return;
        var proj = Project();
        int n = 0;
        foreach (var r in rows.Where(r => r.F.Extractable).ToList())
        {
            try
            {
                var src = _stegoSource;
                string name = $"{Path.GetFileNameWithoutExtension(_stegoReport.Name)}_{r.F.Offset:X}.{r.F.Extension ?? "bin"}";
                var p = await Task.Run(() => StegoAnalyzer.ExtractRange(src, r.F.Offset!.Value, r.F.Length!.Value, proj.StegoDir, name));
                proj.Note($"stego {_stegoReport.Name}: {r.F.Title} -> {p}"); n++;
            }
            catch (Exception ex) { proj.Note($"stego extract FAILED: {ex.Message}"); }
        }
        Ui.Main.Toast("Payloads extracted", $"{n} file(s) written to {proj.StegoDir}");
    }

    private void StegoExtractSelected_Click(object sender, RoutedEventArgs e) => _ = ExtractStego(StegoList.SelectedItems.Cast<StegoRow>());
    private void StegoExtractAll_Click(object sender, RoutedEventArgs e) => _ = ExtractStego(StegoList.Items.Cast<StegoRow>());

    private void StegoLsb_Click(object sender, RoutedEventArgs e)
    {
        if (_stegoImage == null || _stegoReport == null) { Ui.Main.Toast("No decoded image", "LSB export needs an uncompressed BMP or an 8-bit non-interlaced PNG.", true); return; }
        var proj = Project();
        var bits = LsbAnalysis.ExtractLsbPlane(_stegoImage);
        string p = ForensicProject.UniquePath(proj.StegoDir, Path.GetFileNameWithoutExtension(_stegoReport.Name) + ".lsb.bin");
        File.WriteAllBytes(p, bits);
        string desc = FileCarver.DescribeContent(bits.AsSpan(0, Math.Min(512, bits.Length)));
        proj.Note($"LSB plane of {_stegoReport.Name} -> {p} ({desc})");
        Ui.Main.Toast("LSB plane exported", $"{Format.Bytes(bits.Length)} written; content looks like: {desc}");
    }

    // ---- Sleuth Kit ----
    private NtfsVolume? Vol()
    {
        var v = Ui.State.Volume;
        if (v == null) Ui.Main.Toast("Open a drive first", "", true);
        return v;
    }

    private async Task RunTool(string name, Func<string> work)
    {
        TskStatus.Text = name + "…";
        try { TskOutput.Text = await Task.Run(work); TskStatus.Text = name + " done."; }
        catch (Exception ex) { TskOutput.Text = ex.Message; TskStatus.Text = name + " failed."; }
    }

    private void FsStat_Click(object sender, RoutedEventArgs e) { if (Vol() is { } v) _ = RunTool("fsstat", () => NtfsForensics.FsStat(v, ClusterBitmap.Load(v))); }

    private void IStat_Click(object sender, RoutedEventArgs e)
    {
        if (Vol() is not { } v || !long.TryParse(RecordBox.Text, out long n)) return;
        _ = RunTool($"istat {n}", () => NtfsForensics.IStat(v, n, Ui.State.MftIndex));
    }

    private void ICat_Click(object sender, RoutedEventArgs e)
    {
        if (Vol() is not { } v || !long.TryParse(RecordBox.Text, out long n)) return;
        var proj = Project();
        string stream = StreamBox.Text.Trim();
        _ = RunTool($"icat {n}", () => { string p = Path.Combine(proj.TskDir, $"icat-{n}{(stream.Length > 0 ? "-" + Extractor.SafeName(stream) : "")}.bin"); NtfsForensics.ICat(v, n, stream, p); proj.Note($"icat {n}:{stream} -> {p}"); return "Written: " + p; });
    }

    private void FFind_Click(object sender, RoutedEventArgs e)
    {
        if (Vol() is not { } v || !long.TryParse(RecordBox.Text, out long n)) return;
        _ = RunTool($"ffind {n}", () => { var en = v.EntryFromRecord(v.GetRecord(n)); var idx = Ui.State.MftIndex; return "\\" + (idx != null ? idx.PathOf(en) : ResolvePath(v, en)); });
    }

    private static string ResolvePath(NtfsVolume v, NtfsEntry e)
    {
        var parts = new List<string>(); var cur = e; int guard = 0;
        while (cur != null && cur.Record != NtfsVolume.RootRecord && guard++ < 128)
        {
            parts.Add(cur.Name);
            try { cur = v.EntryFromRecord(v.GetRecord(cur.Parent)); } catch { break; }
        }
        parts.Reverse();
        return string.Join("\\", parts);
    }

    private void BuildOwners_Click(object sender, RoutedEventArgs e)
    {
        if (Vol() is not { } v) return;
        var prog = new Progress<(long Done, long Total)>(p => TskStatus.Text = $"cluster owner map… indexing MFT records {p.Done:N0} of {p.Total:N0} ({(p.Total > 0 ? p.Done * 100 / p.Total : 0)}%)");
        _ = RunTool("cluster owner map", () => { _owners = ClusterOwnerMap.Build(v, prog); return $"Cluster owner map built: {_owners.Count:N0} runs indexed. blkstat now reports which file owns a cluster."; });
    }

    private void BlkStat_Click(object sender, RoutedEventArgs e)
    {
        if (Vol() is not { } v || !long.TryParse(ClusterBox.Text, out long lcn)) return;
        _ = RunTool($"blkstat {lcn}", () => NtfsForensics.BlkStat(v, ClusterBitmap.Load(v), _owners, lcn, Ui.State.MftIndex) + (_owners == null ? "\n(build the cluster map to see the owning file)" : ""));
    }

    private void BlkCat_Click(object sender, RoutedEventArgs e)
    {
        if (Vol() is not { } v || !long.TryParse(ClusterBox.Text, out long lcn)) return;
        _ = RunTool($"blkcat {lcn}", () => NtfsForensics.HexDump(NtfsForensics.BlkCat(v, lcn), lcn * (long)v.ClusterSize, v.ClusterSize));
    }

    private void Blkls_Click(object sender, RoutedEventArgs e)
    {
        if (Vol() is not { } v) return;
        var proj = Project();
        _ = RunTool("blkls", () => { string p = Path.Combine(proj.TskDir, "unallocated.bin"); long w = NtfsForensics.Blkls(v, ClusterBitmap.Load(v), p, new Progress<(long d, long t)>(x => Dispatcher.BeginInvoke(() => TskStatus.Text = $"blkls… {x.d * 100 / Math.Max(1, x.t)}%"))); proj.Note($"blkls -> {p} ({w} bytes)"); return $"{Format.Bytes(w)} of unallocated space written to {p}\nRange map: {p}.map.txt"; });
    }

    private IDirectorySource? SourceFor(bool deleted)
    {
        var st = Ui.State;
        if (st.Source == null) { Ui.Main.Toast("Open a drive first", "", true); return null; }
        if (deleted && st.MftIndex == null) { Ui.Main.Toast("MFT scan needed", "Switch Browse to 'Rebuild from MFT' with 'Deleted' ticked to include deleted entries.", true); }
        return st.Source;
    }

    private void FlsCsv_Click(object sender, RoutedEventArgs e)
    {
        if (SourceFor(FlsDeleted.IsChecked == true) is not { } src) return;
        var proj = Project(); bool del = FlsDeleted.IsChecked == true;
        _ = RunTool("fls", () => { var rows = NtfsForensics.Fls(src, del); string p = Path.Combine(proj.TskDir, "fls.csv"); NtfsForensics.WriteCsv(rows, p); proj.Note($"fls -> {p}"); return $"{rows.Count:N0} entries written to {p}"; });
    }

    private void FlsBody_Click(object sender, RoutedEventArgs e)
    {
        if (SourceFor(FlsDeleted.IsChecked == true) is not { } src) return;
        var proj = Project(); bool del = FlsDeleted.IsChecked == true;
        _ = RunTool("fls (body)", () => { var rows = NtfsForensics.Fls(src, del); string p = Path.Combine(proj.TskDir, "bodyfile.txt"); NtfsForensics.WriteBodyFile(rows, p); proj.Note($"body file -> {p}"); return $"{rows.Count:N0} entries written to {p} (TSK body format v3; feed to mactime/Plaso/Timesketch)"; });
    }

    private void Timeline_Click(object sender, RoutedEventArgs e)
    {
        if (SourceFor(FlsDeleted.IsChecked == true) is not { } src) return;
        var proj = Project(); bool del = FlsDeleted.IsChecked == true;
        _ = RunTool("timeline", () =>
        {
            var rows = NtfsForensics.Fls(src, del);
            var ev = NtfsForensics.Timeline(rows).ToList();
            string p = Path.Combine(proj.TskDir, "timeline.csv");
            using (var w = new StreamWriter(p)) { w.WriteLine("time_utc,macb,record,size,path"); foreach (var x in ev) w.WriteLine($"{x.Time:o},{x.Macb},{x.Row.Record},{x.Row.Size},\"{x.Row.Path.Replace("\"", "\"\"")}\""); }
            proj.Note($"timeline -> {p}");
            var sb = new System.Text.StringBuilder($"{ev.Count:N0} events written to {p}\n\nMost recent 200:\n");
            foreach (var x in ev.TakeLast(200)) sb.AppendLine($"{x.Time:yyyy-MM-dd HH:mm:ss} {x.Macb} {x.Row.Size,12:N0} {(x.Row.Deleted ? "(deleted) " : "")}{x.Row.Path}");
            return sb.ToString();
        });
    }

    private void Ils_Click(object sender, RoutedEventArgs e)
    {
        if (Vol() is not { } v) return;
        _ = RunTool("ils", () => { var sb = new System.Text.StringBuilder("record  alloc type size  name (deleted entries)\n"); int n = 0; foreach (var r in NtfsForensics.Ils(v, true)) { sb.AppendLine($"{r.Record,8} {(r.InUse ? "a" : "f")} {(r.IsDir ? "d" : "r")} {r.Size,14:N0}  {r.Name}"); if (++n >= 5000) { sb.AppendLine("… (first 5000)"); break; } } return sb.ToString(); });
    }

    private void Slack_Click(object sender, RoutedEventArgs e)
    {
        if (SourceFor(false) is not { } src) return;
        _ = RunTool("slack scan", () => { _slack = NtfsForensics.ScanSlack(src); var sb = new System.Text.StringBuilder($"{_slack.Count} files with non-zero slack space\n"); foreach (var s in _slack.Take(3000)) sb.AppendLine($"{s.Length,6} B  H={s.Entropy:0.00}  {s.Content,-26} {s.File.Path}  |{s.Preview}|"); return sb.ToString(); });
    }

    private void SlackExtract_Click(object sender, RoutedEventArgs e)
    {
        if (Vol() is not { } v || _slack.Count == 0) { Ui.Main.Toast("Run the slack scan first", "", true); return; }
        var proj = Project();
        _ = RunTool("slack extract", () => { int n = 0; foreach (var s in _slack) { try { NtfsForensics.ExtractSlack(v, s, proj.TskDir); n++; } catch { } } proj.Note($"slack: {n} files extracted"); return $"{n} slack regions written to {proj.TskDir}"; });
    }

    private void Usn_Click(object sender, RoutedEventArgs e)
    {
        if (Vol() is not { } v) return;
        _ = RunTool("USN journal", () =>
        {
            var list = UsnJournal.Read(v);
            if (list.Count == 0) return "No $UsnJrnl on this volume (or it is empty).";
            var sb = new System.Text.StringBuilder($"{list.Count:N0} change-journal records; most recent 1000:\n");
            foreach (var u in list.TakeLast(1000)) sb.AppendLine($"{u.Time:yyyy-MM-dd HH:mm:ss} {u.ReasonText,-44} {u.Path}");
            var proj = Project();
            string p = Path.Combine(proj.TskDir, "usn-journal.csv");
            using (var w = new StreamWriter(p)) { w.WriteLine("time_utc,usn,record,parent,reason,path"); foreach (var u in list) w.WriteLine($"{u.Time:o},{u.Usn},{u.FileRecord},{u.ParentRecord},{u.ReasonText},\"{u.Path.Replace("\"", "\"\"")}\""); }
            sb.AppendLine($"\nFull journal written to {p}");
            return sb.ToString();
        });
    }
}
