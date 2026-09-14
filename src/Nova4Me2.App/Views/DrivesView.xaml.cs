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
    public bool HasRaw { get; init; }
    public string Serial { get; init; } = "";
    public long Length { get; init; }
    public string Identity => Length > 0 ? $"{Serial}|{Model}|{Length}" : $"#{Number}";
    public bool NotResponding { get; init; }
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

    private readonly HashSet<string> _guardShownFor = new();

    public DrivesView()
    {
        InitializeComponent();
        Ui.State.SourceChanged += () => Dispatcher.BeginInvoke(RefreshVolumes);
        Ui.Main.DeviceChanged += arrival =>
        {
            Ui.State.Protection.OnDeviceChanged();
            Ui.Main.Toast(arrival ? "Disk connected" : "Disk removed", arrival ? "Refreshing the drive list…" : "The drive list is being refreshed.");
            Cards.ItemsSource = null;
            _ = LoadAsync(promptGuard: arrival);
        };
        Ui.State.Protection.StatusChanged += m => Dispatcher.BeginInvoke(() => { ProtectText.Text = m; EmptyText.Text = m; EmptyText.Visibility = Visibility.Visible; });
        Ui.State.Protection.Protected += (t, n) => Dispatcher.BeginInvoke(() => { Ui.Main.Toast("Disk protected", $"{t.Display} is offline and read-only in Windows; it can now be opened here without Windows interfering."); Cards.ItemsSource = null; _ = LoadAsync(); });
    }

    public void OnShown() { if (Cards.Items.Count == 0) _ = LoadAsync(); RefreshVolumes(); }

    private async Task LoadAsync(bool promptGuard = false)
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
                if (d.OpenError != null)
                {
                    cards.Add(new DriveCard { Number = d.Number, Model = $"Physical drive {d.Number}", SizeText = "?", Detail = d.OpenError, Badge = d.ProbeTimedOut ? "hung" : "locked", NotResponding = d.ProbeTimedOut,
                        VolumesText = string.Join("\n", d.Volumes.Select(v => "• " + v.Display)), Warning = d.ProbeTimedOut ? "Windows is holding this disk busy (mount attempts). Take it offline below or re-plug it, then refresh." : "" });
                    continue;
                }
                bool raw = d.Volumes.Any(v => v.IsRaw || v.ProbeTimedOut);
                string vols = d.Volumes.Count == 0 ? "No volumes visible to Windows" : string.Join("\n", d.Volumes.Select(v => "• " + v.Display));
                cards.Add(new DriveCard
                {
                    Number = d.Number, Model = d.Model, SizeText = Format.Bytes(d.Length), IsSystem = d.IsSystemDisk,
                    Serial = d.Storage.Serial, Length = d.Length,
                    Detail = $"{d.Storage.BusTypeName}{(d.Storage.IsUsb ? " (USB bridge)" : "")} · {d.SectorSize} B sectors · S/N {(d.Storage.Serial.Length > 0 ? d.Storage.Serial : "n/a")} · FW {d.Storage.Revision}",
                    VolumesText = vols + (d.Attributes.Queried && d.Attributes.Offline ? "\n• Offline in Windows (protected)" : ""), Badge = d.IsSystemDisk ? "SYSTEM" : d.Attributes.Offline ? "OFFLINE" : raw ? "RAW" : $"disk {d.Number}", HasRaw = raw,
                    Warning = raw ? "Windows cannot read a volume on this disk (RAW). Nova4Me2 reads it directly." : d.IsSystemDisk ? "This is the Windows system disk." : ""
                });
            }
            Cards.ItemsSource = cards;
            EmptyText.Visibility = cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyText.Text = "No drives found. Run Nova4Me2 as Administrator.";
            Highlight();
            if (promptGuard) MaybeShowGuard(cards.FirstOrDefault(c => (c.HasRaw || c.NotResponding) && !c.IsSystem));
        }
        catch (Exception ex) { EmptyText.Text = "Drive scan failed: " + ex.Message; }
        finally { _loading = false; }
    }

    /// <summary>Offer to keep Windows away from a RAW / hung disk. Shown at most once per session unless the user asks again.</summary>
    private async void MaybeShowGuard(DriveCard? card, bool force = false)
    {
        if (card == null || !OperatingSystem.IsWindows()) return;
        var prot = Ui.State.Protection;
        if (prot.IsPending(card.Serial, card.Model, card.Length)) { Ui.Main.Toast("Protection pending", $"Still waiting for {card.Model} to answer; it will be taken offline the moment it does."); return; }
        if (!force && (Ui.State.Settings.MountGuardDismissed || _guardShownFor.Contains(card.Identity))) return;
        if (_guardShownFor.Contains(card.Identity) && force) return; // asked once for this disk already
        int num = card.Number;
        bool alreadyOffline = card.Length > 0 && await Task.Run(() => Core.Devices.Windows.DiskControl.GetAttributes(num).Offline);
        if (alreadyOffline) return;
        _guardShownFor.Add(card.Identity);
        var target = new Services.ProtectionWatcher.Target(card.Serial, card.Model, card.Length, $"{card.Model} ({card.SizeText})", card.Number);
        var dlg = new Dialogs.MountGuardDialog(card.Number, $"{card.Model}, {card.SizeText}", target) { Owner = Ui.Main };
        if (dlg.ShowDialog() == true)
        {
            if (dlg.Choice == Dialogs.MountGuardChoice.DisableAutomount) Ui.Main.Toast("Automount disabled", "Windows will stop mounting newly connected volumes. If the disk still flaps, use 'Keep Windows off this disk'.");
            else Ui.Main.Toast("Protecting disk", "Nova4Me2 will take the disk offline + read-only as soon as it answers (keep it plugged in; re-plug if needed).");
        }
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
        if (c.NotResponding)
        {
            if (!Ui.State.Protection.IsPending(c.Serial, c.Model, c.Length) && !_guardShownFor.Contains(c.Identity)) MaybeShowGuard(c, force: true);
            else Ui.Main.Toast("Disk not responding", "Windows is still holding this disk. Protection is pending; it will be applied as soon as the disk answers. Re-plugging the enclosure usually speeds this up.", true);
            return;
        }
        if (c.HasRaw && !c.IsSystem) MaybeShowGuard(c);
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
        _ = UpdateProtectUiAsync();
        var t = st.Partitions;
        PartitionsText.Text = t == null ? "" : $"Partition table: {t.Scheme}{(t.UsedBackupGpt ? " (recovered from backup GPT)" : "")} · " + string.Join(" · ", t.Partitions.Select(p => $"#{p.Index} {p.TypeName} {Format.Bytes(p.Length)}")) + (t.Problems.Count > 0 ? $"\n{string.Join("\n", t.Problems)}" : "");
    }

    private async Task UpdateProtectUiAsync()
    {
        var st = Ui.State;
        bool win = OperatingSystem.IsWindows() && st.DriveNumber is not null;
        ProtectButton.Visibility = OnlineButton.Visibility = win ? Visibility.Visible : Visibility.Collapsed;
        if (!win) { ProtectText.Text = ""; return; }
        int num = st.DriveNumber!.Value;
        ProtectText.Text = "Checking Windows disk state…";
        var a = await Task.Run(() => Core.Devices.Windows.DiskControl.GetAttributes(num));
        bool auto = Core.Devices.Windows.DiskControl.IsAutomountEnabled();
        if (st.DriveNumber != num) return;
        ProtectText.Text = $"Windows: disk {(a.Offline ? "OFFLINE" : "online")}, {(a.ReadOnly ? "read-only" : "writable")} · automount {(auto ? "ON" : "off")}";
        ProtectButton.IsEnabled = !(a.Offline && a.ReadOnly);
        OnlineButton.IsEnabled = a.Offline || a.ReadOnly;
    }

    private async void Protect_Click(object sender, RoutedEventArgs e)
    {
        var st = Ui.State;
        if (st.DriveNumber is not { } n) return;
        var hw = st.Hardware;
        var target = new Services.ProtectionWatcher.Target(hw?.Serial ?? "", hw?.Model ?? "", st.Device?.Length ?? 0, st.SourceName, n);
        st.Protection.Request(target);
        Ui.Main.Toast("Protecting disk", "Nova4Me2 is taking the disk offline + read-only in Windows; if the disk is busy this is retried automatically until it succeeds.");
        await UpdateProtectUiAsync();
    }

    private async void Online_Click(object sender, RoutedEventArgs e)
    {
        if (Ui.State.DriveNumber is not { } n) return;
        try
        {
            await Task.Run(() => Core.Devices.Windows.DiskControl.SetAttributes(n, offline: false, readOnly: false));
            Ui.Main.Toast("Disk online", "Windows will treat the disk normally again (it may start probing the RAW volume).");
        }
        catch (Exception ex) { Ui.Main.Toast("Could not bring the disk online", ex.Message, true); }
        await UpdateProtectUiAsync();
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
