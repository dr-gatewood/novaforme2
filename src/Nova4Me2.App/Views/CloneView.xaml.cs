using System.IO;
using System.Windows;
using System.Windows.Controls;
using Nova4Me2.Core.Devices;
using Nova4Me2.Core.Devices.Windows;
using Nova4Me2.Core.Partitions;
using Nova4Me2.Core.Recovery;
using Nova4Me2.Core.Reports;
using Nova4Me2.Core.Util;

namespace Nova4Me2.App.Views;

public partial class CloneView : UserControl, INovaView
{
    private CancellationTokenSource? _cts;
    private ImageProgress? _last;
    private string _lastTarget = "";
    private List<PhysicalDriveInfo> _drives = new();

    public CloneView()
    {
        InitializeComponent();
        Ui.State.SourceChanged += () => Dispatcher.BeginInvoke(RefreshSource);
    }

    public void OnShown() { RefreshSource(); if (_drives.Count == 0) RefreshDrives(); }

    private void RefreshSource()
    {
        var st = Ui.State;
        PartitionCombo.Items.Clear();
        if (!st.HasDevice) { SourceText.Text = "Open a drive first."; StartButton.IsEnabled = false; return; }
        StartButton.IsEnabled = _cts == null;
        SourceText.Text = $"Source: {st.SourceName} — {Format.Bytes(st.Device!.Length)}, {st.Device.SectorSize} B sectors";
        foreach (var p in st.Partitions?.Partitions ?? new()) PartitionCombo.Items.Add($"#{p.Index} {p.TypeName} {p.Name} — {Format.Bytes(p.Length)} @ {Format.Bytes(p.StartOffset)}");
        if (PartitionCombo.Items.Count > 0) PartitionCombo.SelectedIndex = st.Partitions!.Partitions.Select((p, i) => (p, i)).OrderByDescending(x => x.p.Length).First().i;
        if (FilePath.Text.Length == 0) FilePath.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), $"{Extractor.SafeName(st.SourceName)}.img");
    }

    private void RefreshDrives()
    {
        DriveCombo.Items.Clear();
        _drives.Clear();
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            foreach (var d in DriveEnumerator.List())
            {
                if (d.OpenError != null) continue;
                _drives.Add(d);
                string tag = d.IsSystemDisk ? " — SYSTEM DISK (blocked)" : d.Number == Ui.State.DriveNumber ? " — source (blocked)" : "";
                DriveCombo.Items.Add($"[{d.Number}] {d.Model} {Format.Bytes(d.Length)} {d.Storage.BusTypeName}{tag}");
            }
        }
        catch (Exception ex) { TargetWarning.Text = "Drive list failed: " + ex.Message; }
    }

    private void RefreshDrives_Click(object sender, RoutedEventArgs e) => RefreshDrives();
    private void Scope_Changed(object sender, RoutedEventArgs e) { if (IsLoaded) PartitionCombo.IsEnabled = ScopePartition.IsChecked == true; }

    private void Target_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        bool file = TargetFile.IsChecked == true;
        FilePath.IsEnabled = BrowseButton.IsEnabled = file;
        DriveCombo.IsEnabled = RefreshDrivesButton.IsEnabled = !file;
        Resume.IsEnabled = file;
        TargetWarning.Text = file ? "" : "Everything on the target drive will be overwritten. The Windows system disk and the source drive are blocked.";
    }

    private void BrowseFile_Click(object sender, RoutedEventArgs e)
    {
        var p = Ui.SaveFile("Save disk image as", "Raw disk image (*.img)|*.img|All files (*.*)|*.*", Path.GetFileName(FilePath.Text));
        if (p != null) FilePath.Text = p;
    }

    private (long start, long length, string label) Range()
    {
        var st = Ui.State;
        if (ScopePartition.IsChecked == true && st.Partitions != null && PartitionCombo.SelectedIndex >= 0)
        {
            var p = st.Partitions.Partitions[PartitionCombo.SelectedIndex];
            return (p.StartOffset, p.Length, $"partition {p.Index}");
        }
        return (0, st.Device!.Length, "whole disk");
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        var st = Ui.State;
        var dev = st.Device;
        if (dev == null) return;
        var (start, length, label) = Range();
        IBlockDevice target;
        string targetDesc;
        bool toFile = TargetFile.IsChecked == true;
        try
        {
            if (toFile)
            {
                string path = FilePath.Text.Trim();
                if (path.Length == 0) { Ui.Main.Toast("Choose a target file", "", true); return; }
                bool resume = Resume.IsChecked == true && File.Exists(path);
                if (File.Exists(path) && !resume && !Ui.Main.ConfirmTyped("Overwrite image file", $"{path} already exists and will be overwritten.", "OVERWRITE")) return;
                var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!);
                if (!resume && drive.AvailableFreeSpace < length) { Ui.Main.Toast("Not enough free space", $"Need {Format.Bytes(length)}, {drive.Name} has {Format.Bytes(drive.AvailableFreeSpace)} free.", true); return; }
                target = resume ? new FileBlockDevice(path, writable: true) : FileBlockDevice.Create(path, length);
                targetDesc = path;
            }
            else
            {
                if (DriveCombo.SelectedIndex < 0) { Ui.Main.Toast("Choose a target drive", "", true); return; }
                var d = _drives[DriveCombo.SelectedIndex];
                if (d.IsSystemDisk) { Ui.Main.Toast("Blocked", "Refusing to overwrite the Windows system disk.", true); return; }
                if (d.Number == st.DriveNumber) { Ui.Main.Toast("Blocked", "Source and target are the same drive.", true); return; }
                if (d.Length < length) { Ui.Main.Toast("Target too small", $"Target is {Format.Bytes(d.Length)}, source range is {Format.Bytes(length)}.", true); return; }
                if (!Ui.Main.ConfirmTyped("Overwrite physical drive", $"ALL DATA on [{d.Number}] {d.Model} ({Format.Bytes(d.Length)}) will be destroyed and replaced with a sector-by-sector copy of {label} from {st.SourceName}.", "CONFIRM")) return;
                target = DeviceFactory.Open(d.DevicePath, new ResilienceOptions { Keepalive = null }, writable: true);
                targetDesc = $"[{d.Number}] {d.Model}";
            }
        }
        catch (Exception ex) { Ui.Main.Toast("Cannot open target", ex.Message, true); return; }

        var opt = new ImageOptions { StartOffset = start, Length = length, TwoPass = TwoPass.IsChecked == true, ComputeSha256 = Hash.IsChecked == true, ComputeMd5 = Md5.IsChecked == true, VerifyTarget = Verify.IsChecked == true, Resume = toFile && Resume.IsChecked == true, LogPrefix = toFile ? null : Path.Combine(Settings_Dir(), "clone") };
        _cts = new CancellationTokenSource();
        StartButton.IsEnabled = false; CancelButton.IsEnabled = true; ReportButton.IsEnabled = false;
        _lastTarget = targetDesc;
        var progress = new Progress<ImageProgress>(Show);
        try
        {
            var result = await Task.Run(() => Imager.Run(dev, target, opt, progress, _cts.Token));
            _last = result;
            Show(result);
            Ui.Main.Toast("Clone complete", $"{Format.Bytes(result.BytesDone)} copied, {result.BadSectorCount} unreadable sectors{(result.TargetSha256 != null ? (result.VerifyOk ? ", verification OK" : ", VERIFICATION MISMATCH") : "")}.", result.TargetSha256 != null && !result.VerifyOk);
        }
        catch (OperationCanceledException) { PhaseText.Text = "Cancelled (resumable for image files)"; Ui.Main.Toast("Clone cancelled", "Progress was saved; tick Resume to continue later."); }
        catch (Exception ex) { PhaseText.Text = "Failed: " + ex.Message; Ui.Main.Toast("Clone failed", ex.GetBaseException().Message, true); }
        finally
        {
            try { target.Dispose(); } catch { }
            _cts = null;
            StartButton.IsEnabled = true; CancelButton.IsEnabled = false; ReportButton.IsEnabled = _last != null;
        }
    }

    private static string Settings_Dir() { var d = Services.Settings.Dir; Directory.CreateDirectory(d); return d; }

    private void Show(ImageProgress p)
    {
        PhaseText.Text = p.Phase;
        Progress.Value = p.Fraction * 100;
        EtaText.Text = p.Eta is { } eta ? $"ETA {Format.Duration(eta)} · elapsed {Format.Duration(p.Elapsed)}" : $"elapsed {Format.Duration(p.Elapsed)}";
        StatBytes.Text = $"{Format.Bytes(p.BytesDone)} of {Format.Bytes(p.BytesTotal)}";
        StatRate.Text = Format.Rate(p.BytesPerSecond);
        StatBad.Text = $"{p.BadSectorCount} unreadable sectors" + (p.BytesSkipped > 0 ? $", {Format.Bytes(p.BytesSkipped)} pending retry" : "");
        StatHash.Text = (p.Sha256 != null ? "SHA-256 " + p.Sha256 : "") + (p.TargetSha256 != null ? (p.VerifyOk ? "  ✔ verified" : "  ✖ mismatch") : "");
        Map.Update(p.Map, p.Phase == "Complete" ? null : p.CurrentOffset);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void Report_Click(object sender, RoutedEventArgs e)
    {
        if (_last == null) return;
        Ui.SaveReport(ReportBuilder.FromImaging(_last, Ui.State.SourceName, _lastTarget), $"Nova4Me2-clone-{DateTime.Now:yyyyMMdd-HHmm}");
    }
}
