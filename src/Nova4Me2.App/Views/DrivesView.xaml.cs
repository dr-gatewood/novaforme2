using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Nova4Me2.App.Services;
using Nova4Me2.Core.Devices.Windows;
using Nova4Me2.Core.Partitions;
using Nova4Me2.Core.Util;

namespace Nova4Me2.App.Views;

public sealed class DriveCard
{
    public int Number { get; init; }
    public string Model { get; init; } = "";
    public string SizeText { get; init; } = "";
    public string Detail { get; init; } = "";
    public string VolumesText { get; init; } = "";
    public string Warning { get; init; } = "";
    public string Badge { get; init; } = "";
    public bool IsSystem { get; init; }
    public Brush BorderBrush { get; set; } = Brushes.Transparent;
    public string Spec => $@"\\.\PhysicalDrive{Number}";
}

public sealed class VolumeRow
{
    public NtfsVolumeCandidate Candidate { get; init; } = null!;
    public string Display => Candidate.Display;
    public string NotesText => string.Join(" ", Candidate.Notes);
}

public partial class DrivesView : UserControl, INovaView
{
    private bool _loading;

    public DrivesView()
    {
        InitializeComponent();
        Ui.State.SourceChanged += () => Dispatcher.BeginInvoke(RefreshVolumes);
    }

    public void OnShown() { if (Cards.Items.Count == 0) _ = LoadAsync(); RefreshVolumes(); }

    private async Task LoadAsync()
    {
        if (_loading) return;
        _loading = true;
        EmptyText.Text = "Scanning for drives…";
        EmptyText.Visibility = Visibility.Visible;
        try
        {
            if (!OperatingSystem.IsWindows()) { EmptyText.Text = "Physical drive enumeration needs Windows. Open an image file instead."; return; }
            var drives = await Task.Run(() => DriveEnumerator.List());
            var cards = new List<DriveCard>();
            foreach (var d in drives)
            {
                if (d.OpenError != null) { cards.Add(new DriveCard { Number = d.Number, Model = $"Physical drive {d.Number}", SizeText = "?", Detail = d.OpenError, Badge = "locked" }); continue; }
                bool raw = d.Volumes.Any(v => v.IsRaw);
                string vols = d.Volumes.Count == 0 ? "No volumes visible to Windows" : string.Join("\n", d.Volumes.Select(v => "• " + v.Display));
                cards.Add(new DriveCard
                {
                    Number = d.Number, Model = d.Model, SizeText = Format.Bytes(d.Length), IsSystem = d.IsSystemDisk,
                    Detail = $"{d.Storage.BusTypeName}{(d.Storage.IsUsb ? " (USB bridge)" : "")} · {d.SectorSize} B sectors · S/N {(d.Storage.Serial.Length > 0 ? d.Storage.Serial : "n/a")} · FW {d.Storage.Revision}",
                    VolumesText = vols, Badge = d.IsSystemDisk ? "SYSTEM" : raw ? "RAW" : $"disk {d.Number}",
                    Warning = raw ? "Windows cannot read a volume on this disk (RAW). Nova4Me2 reads it directly." : d.IsSystemDisk ? "This is the Windows system disk." : ""
                });
            }
            Cards.ItemsSource = cards;
            EmptyText.Visibility = cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyText.Text = "No drives found. Run Nova4Me2 as Administrator.";
            Highlight();
        }
        catch (Exception ex) { EmptyText.Text = "Drive scan failed: " + ex.Message; }
        finally { _loading = false; }
    }

    private void Highlight()
    {
        if (Cards.ItemsSource is not List<DriveCard> cards) return;
        foreach (var c in cards) c.BorderBrush = Ui.State.DriveNumber == c.Number ? (Brush)FindResource("Accent") : (Brush)FindResource("Border");
        Cards.Items.Refresh();
    }

    private async void Card_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DriveCard c) return;
        if (c.Badge == "locked") { Ui.Main.Toast("Cannot open drive", c.Detail, true); return; }
        await OpenAsync(c.Spec, $"{c.Model} (disk {c.Number})", c.Number);
        Highlight();
    }

    private async Task OpenAsync(string spec, string name, int? number)
    {
        await Ui.Main.RunBusy($"Opening {name}", async p => await Ui.State.OpenAsync(spec, name, number, p));
        if (Ui.State.HasDevice)
        {
            var n = Ui.State.Volumes.Count;
            Ui.Main.Toast(name, n == 0 ? "No NTFS volume found. Try a deep signature scan." : $"{n} NTFS volume(s) found. Browse files, or run Health first.");
            RefreshVolumes();
        }
    }

    private void RefreshVolumes()
    {
        var st = Ui.State;
        if (!st.HasDevice) { VolumesCard.Visibility = Visibility.Collapsed; return; }
        VolumesCard.Visibility = Visibility.Visible;
        VolumesTitle.Text = $"NTFS volumes on {st.SourceName}";
        VolumeList.SelectionChanged -= VolumeList_SelectionChanged;
        VolumeList.ItemsSource = st.Volumes.Select(v => new VolumeRow { Candidate = v }).ToList();
        VolumeList.SelectedIndex = st.SelectedCandidate != null ? st.Volumes.IndexOf(st.SelectedCandidate) : -1;
        VolumeList.SelectionChanged += VolumeList_SelectionChanged;
        var t = st.Partitions;
        PartitionsText.Text = t == null ? "" : $"Partition table: {t.Scheme}{(t.UsedBackupGpt ? " (recovered from backup GPT)" : "")} · " + string.Join(" · ", t.Partitions.Select(p => $"#{p.Index} {p.TypeName} {Format.Bytes(p.Length)}")) + (t.Problems.Count > 0 ? $"\n{string.Join("\n", t.Problems)}" : "");
    }

    private async void VolumeList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (VolumeList.SelectedItem is not VolumeRow r || r.Candidate == Ui.State.SelectedCandidate) return;
        await Ui.Main.RunBusy("Opening volume", async p => await Ui.State.SelectVolumeAsync(r.Candidate, p));
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) { Cards.ItemsSource = null; _ = LoadAsync(); }

    private async void OpenImage_Click(object sender, RoutedEventArgs e)
    {
        var path = Ui.OpenFile("Open disk or partition image", "Disk images (*.img;*.dd;*.raw;*.bin;*.001)|*.img;*.dd;*.raw;*.bin;*.001|All files (*.*)|*.*");
        if (path == null) return;
        await OpenAsync(path, Path.GetFileName(path), null);
        Highlight();
    }

    private void Browse_Click(object sender, RoutedEventArgs e) => Ui.Main.Navigate("Browse");

    private async void DeepScan_Click(object sender, RoutedEventArgs e)
    {
        var dev = Ui.State.Device;
        if (dev == null) return;
        List<NtfsVolumeCandidate> found = new();
        await Ui.Main.RunBusy("Scanning the whole disk for NTFS boot sectors", async p => { found = await Task.Run(() => VolumeLocator.Scan(dev, p).ToList()); });
        if (found.Count == 0) { Ui.Main.Toast("Deep scan", "No additional NTFS boot sectors found."); return; }
        var list = Ui.State.Volumes;
        int added = 0;
        foreach (var f in found) if (!list.Any(v => v.StartOffset == f.StartOffset)) { list.Add(f); added++; }
        Ui.Main.Toast("Deep scan", $"{found.Count} NTFS signature(s) found, {added} new.");
        RefreshVolumes();
    }

    private void Usb_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Dialogs.UsbDialog { Owner = Ui.Main };
        dlg.ShowDialog();
    }
}
