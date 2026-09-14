using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Nova4Me2.Core.Analysis;
using Nova4Me2.Core.Reports;
using Nova4Me2.Core.Util;

namespace Nova4Me2.App.Views;

public sealed class FindingRow
{
    public Finding F { get; init; } = null!;
    public string Title => F.Title;
    public string Detail => F.Detail;
    public string SeverityText => F.Severity.ToString().ToUpperInvariant();
    public string FixText => F.Repair != RepairKind.None ? "→ " + RepairKindText.Title(F.Repair) + (F.IsFixableHere ? " (Repair view)" : "") : F.Advice ?? "";
    public Brush Brush => (Brush)Application.Current.FindResource(F.Severity switch { Severity.Good => "Good", Severity.Warning => "Warn", Severity.Error => "Bad", Severity.Critical => "Bad", _ => "Info" });
}

public sealed class KeyValueRow
{
    public string Key { get; init; } = "";
    public string Value { get; init; } = "";
}

public partial class HealthView : UserControl, INovaView
{
    private CancellationTokenSource? _scanCts;

    public HealthView()
    {
        InitializeComponent();
        Ui.State.SourceChanged += () => Dispatcher.BeginInvoke(Reset);
        Ui.State.HardwareChanged += () => Dispatcher.BeginInvoke(() =>
        {
            if (Ui.State.LastHealth is { } h && Ui.State.Hardware is { } hw) { h.NvmeHealth = hw.NvmeHealth; h.AtaSmart = hw.AtaSmart; HealthAnalyzer.Rescore(h); h.BootFix = HealthAnalyzer.AssessBootFix(h); Render(h); }
        });
    }

    public void OnShown()
    {
        bool has = Ui.State.HasDevice;
        AnalyzeButton.IsEnabled = QuickScan.IsEnabled = FullScan.IsEnabled = has && _scanCts == null;
        if (Ui.State.LastHealth != null) Render(Ui.State.LastHealth); else if (!has) GradeText.Text = "Open a drive first.";
    }

    private void Reset()
    {
        ScoreGauge.Value = 0; BootGauge.Value = 0;
        GradeText.Text = "Not analysed yet"; VerdictText.Text = ""; RecommendText.Text = "";
        ReasonList.ItemsSource = null; FindingList.ItemsSource = null; SmartRows.ItemsSource = null; FindingsSummary.Text = "";
        Map.Update(null, null); ScanPhase.Text = "Not run"; ScanStats.Text = ""; ScanProgress.Value = 0;
        ReportButton.IsEnabled = false;
        OnShown();
    }

    private async void Analyze_Click(object sender, RoutedEventArgs e)
    {
        var st = Ui.State;
        var dev = st.Device;
        if (dev == null) return;
        HealthReport? rep = null;
        var prev = st.LastHealth;
        await Ui.Main.RunBusy("Analysing disk structures", async p =>
        {
            var hw = st.Hardware;
            rep = await Task.Run(() => HealthAnalyzer.Analyze(dev, null, hw?.Model, hw?.Serial, hw?.BusType, p));
            rep.NvmeHealth = hw?.NvmeHealth;
            rep.AtaSmart = hw?.AtaSmart;
            if (prev?.Surface != null) rep.Surface = prev.Surface;
            HealthAnalyzer.Rescore(rep);
            rep.BootFix = HealthAnalyzer.AssessBootFix(rep);
        });
        if (rep == null) return;
        st.LastHealth = rep;
        Render(rep);
        Ui.Main.Toast("Analysis complete", $"Structural health {rep.Score}/100 ({rep.Grade}); boot record fix likelihood {rep.BootFix.LikelihoodPercent}%.");
    }

    private void Render(HealthReport r)
    {
        ScoreGauge.AnimateTo(r.Score);
        BootGauge.AnimateTo(r.BootFix.LikelihoodPercent);
        GradeText.Text = $"{r.Grade} — {r.Score}/100";
        VerdictText.Text = r.BootFix.Verdict;
        ReasonList.ItemsSource = r.BootFix.Reasons.Select(x => "• " + x).ToList();
        RecommendText.Text = r.BootFix.Recommended.Count > 0 ? "Recommended: " + string.Join(" · ", r.BootFix.Recommended.Select(RepairKindText.Title)) : "";
        var findings = r.AllFindings.OrderByDescending(f => f.Severity).Select(f => new FindingRow { F = f }).ToList();
        FindingList.ItemsSource = findings;
        FindingsSummary.Text = $"{findings.Count(f => f.F.Severity == Severity.Critical)} critical · {findings.Count(f => f.F.Severity == Severity.Error)} errors · {findings.Count(f => f.F.Severity == Severity.Warning)} warnings";
        var rows = new List<KeyValueRow>();
        var hw = Ui.State.Hardware;
        if (r.NvmeHealth != null || hw?.NvmeIdentify != null) rows.AddRange((hw?.NvmeRows() ?? new()).Select(x => new KeyValueRow { Key = x.Key, Value = x.Value }));
        if (r.AtaSmart is { } a)
        {
            foreach (var at in a.Attributes) rows.Add(new KeyValueRow { Key = $"{at.Id} {at.Name}", Value = $"current {at.Current}, worst {at.Worst}, threshold {at.Threshold}, raw {at.Raw}{(at.Failing ? "  — FAILING" : "")}" });
        }
        SmartRows.ItemsSource = rows;
        SmartNote.Text = rows.Count == 0 ? (hw?.ViaUsbBridge == true ? "SMART is not exposed through this USB bridge. Connect the SSD directly (M.2) to read the NVMe health log." : hw?.NvmeError is { Length: > 0 } err ? "SMART unavailable: " + err : "No SMART data (image file or unsupported interface).") : "";
        if (r.Surface is { } su) ShowScan(su);
        ReportButton.IsEnabled = true;
    }

    private void Quick_Click(object sender, RoutedEventArgs e) => _ = ScanAsync(16);
    private void Full_Click(object sender, RoutedEventArgs e) => _ = ScanAsync(1);
    private void Stop_Click(object sender, RoutedEventArgs e) => _scanCts?.Cancel();

    private async Task ScanAsync(int sampleEvery)
    {
        var dev = Ui.State.Device;
        if (dev == null || _scanCts != null) return;
        _scanCts = new CancellationTokenSource();
        AnalyzeButton.IsEnabled = QuickScan.IsEnabled = FullScan.IsEnabled = false; StopScan.IsEnabled = true;
        var progress = new Progress<SurfaceScanResult>(ShowScan);
        try
        {
            var res = await Task.Run(() => SurfaceScan.Run(dev, new SurfaceScanOptions { SampleEvery = sampleEvery }, progress, _scanCts.Token));
            ShowScan(res);
            if (Ui.State.LastHealth is { } h) { h.Surface = res; HealthAnalyzer.Rescore(h); h.BootFix = HealthAnalyzer.AssessBootFix(h); Render(h); }
            Ui.Main.Toast("Surface scan finished", $"{res.Grade}: {res.BadSectors} unreadable sectors, {res.ChunksSlow} slow chunks, {Format.Rate(res.BytesPerSecond)}.");
        }
        catch (OperationCanceledException) { ScanPhase.Text = "Stopped"; }
        catch (Exception ex) { Ui.Main.Toast("Surface scan failed", ex.GetBaseException().Message, true); }
        finally { _scanCts = null; StopScan.IsEnabled = false; OnShown(); }
    }

    private void ShowScan(SurfaceScanResult r)
    {
        ScanPhase.Text = r.Phase + (r.ChunksRead > 0 ? $" — {r.Grade}" : "");
        ScanStats.Text = $"{Format.Bytes(r.BytesRead)} of {Format.Bytes(r.BytesTotal)} · {r.BadSectors} bad · {r.ChunksSlow} slow · {Format.Rate(r.BytesPerSecond)} · {r.Reconnects} link drops";
        ScanProgress.Value = r.Fraction * 100;
        Map.Update(r.Map, r.Complete ? null : r.Map?.Start + (long)(r.Fraction * (r.Map?.Length ?? 0)));
    }

    private void Report_Click(object sender, RoutedEventArgs e)
    {
        if (Ui.State.LastHealth is not { } h) return;
        Ui.SaveReport(ReportBuilder.FromHealth(h, Ui.State.Hardware), $"Nova4Me2-health-{DateTime.Now:yyyyMMdd-HHmm}");
    }
}
