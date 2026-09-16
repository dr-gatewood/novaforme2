using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Nova4Me2.App.Services;
using Nova4Me2.Core.Devices.Windows;
using Nova4Me2.Core.Util;

namespace Nova4Me2.App.Views;

public sealed class WinFindingRow
{
    public WindowsFinding F { get; init; } = null!;
    public string Title => F.Title;
    public string Detail => F.Detail;
    public string Command => F.Action?.Command ?? "";
    public string ActionLabel => F.Action?.Label ?? "";
    public Visibility ActionVisibility => F.Action != null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CommandVisibility => F.Action != null ? Visibility.Visible : Visibility.Collapsed;
    public Brush Dot => F.Severity switch { "good" => (Brush)Application.Current.Resources["Good"], "warn" => (Brush)Application.Current.Resources["Warn"], "error" => (Brush)Application.Current.Resources["Bad"], _ => (Brush)Application.Current.Resources["Accent"] };
}

public sealed class WinDiskRow
{
    public WinPhysicalDisk P { get; init; } = null!;
    public WinDisk? D { get; init; }
    public DiskpartDisk? Dp { get; init; }
    public WinPnpDisk? PnpDevice { get; init; }
    public int Number => P.Number;
    public string Model => P.FriendlyName ?? "";
    public string Status
    {
        get
        {
            if (Dp is { Foreign: true }) return "Foreign dynamic disk (import needed)";
            if (D == null) return "no disk object (not in Disk Management)";
            var parts = new List<string> { D.IsOffline == true ? "Offline" + (string.IsNullOrEmpty(D.OfflineReason) ? "" : $" ({D.OfflineReason})") : "Online" };
            if (D.IsReadOnly == true) parts.Add("read-only");
            if (Dp is { Dynamic: true }) parts.Add("dynamic");
            if (D.IsSystem == true || D.IsBoot == true) parts.Add("system");
            return string.Join(", ", parts);
        }
    }
    public string Style => D?.PartitionStyle ?? (Dp != null ? (Dp.Gpt ? "GPT" : "MBR") : "");
    public string Size => Format.Bytes(P.Size ?? 0);
    public string Bus => P.BusType ?? "";
    public string Media => P.MediaType ?? "";
    public string Health => P.HealthStatus ?? "";
    public string Firmware => P.FirmwareVersion ?? "";
    public string Serial => P.SerialNumber ?? "";
    public string Pnp => PnpDevice == null ? "" : PnpDevice.IsPhantom ? "phantom" : PnpDevice.HasProblem ? PnpDevice.Problem ?? "" : PnpDevice.Status ?? "";
    public bool IsOffline => D?.IsOffline == true;
    public bool IsReadOnly => D?.IsReadOnly == true;
    public bool IsForeign => Dp is { Foreign: true };
}

public sealed class WinVolumeRow
{
    public WinVolume? V { get; init; }
    public DiskpartVolume? Dp { get; init; }
    public string Number => Dp?.Number.ToString() ?? "";
    public string Letter => V?.Letter ?? Dp?.Letter ?? "";
    public string Label => V?.FileSystemLabel ?? Dp?.Label ?? "";
    public string Fs => V != null ? (string.IsNullOrEmpty(V.FileSystem) ? "RAW" : V.FileSystem) : Dp?.Fs ?? "";
    public string Type => Dp?.Type ?? V?.DriveType ?? "";
    public string Size => V?.Size is { } s ? Format.Bytes(s) : Dp?.Size ?? "";
    public string Free => V?.SizeRemaining is { } f ? Format.Bytes(f) : "";
    public string Health => V?.HealthStatus ?? Dp?.Status ?? "";
    public string Disk => V?.DiskNumber?.ToString() ?? "";
    public string Info => Dp?.Info ?? "";
    public bool HasLetter => Letter.Length > 0;
}

public sealed class WinPnpRow
{
    public WinPnpDisk P { get; init; } = null!;
    public string Name => P.FriendlyName ?? "";
    public string Status => P.Status ?? "";
    public string Problem => P.IsPhantom ? "phantom (unplugged)" : P.Problem ?? "";
    public string InstanceId => P.InstanceId ?? "";
}

public sealed class WinEventRow
{
    public WinEvent E { get; init; } = null!;
    public string Time => E.Time == DateTime.MinValue ? E.TimeCreated ?? "" : E.Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public string Source => E.ProviderName ?? "";
    public string Id => E.Id?.ToString() ?? "";
    public string Level => E.LevelDisplayName ?? "";
    public string Message => (E.Message ?? "").Replace("\r", "").Replace("\n", " ");
}

public sealed class ServicePill
{
    public WinService S { get; init; } = null!;
    public string Text => $"{S.Name} {S.Status?.ToLowerInvariant()}";
    public string Tip => $"{S.DisplayName} ({S.StartType}). Click to start it if stopped.";
    public Brush Dot => S.Running ? (Brush)Application.Current.Resources["Good"] : (Brush)Application.Current.Resources["Bad"];
}

public partial class WindowsView : UserControl, INovaView
{
    private WindowsStorageSnapshot? _snap;
    private bool _busy;
    private double Hours => Hours3.IsChecked == true ? 3 : Hours168.IsChecked == true ? 168 : 24;

    public WindowsView()
    {
        InitializeComponent();
        foreach (char c in "DEFGHIJKLMNOPQRSTUVWXYZ") LetterBox.Items.Add($"{c}:");
    }

    public void OnShown()
    {
        if (!OperatingSystem.IsWindows()) { OsText.Text = "Windows tools are only available on Windows."; return; }
        if (_snap == null) _ = RefreshAsync();
    }

    // ---- refresh ----
    private async Task RefreshAsync()
    {
        if (_busy) return;
        _busy = true;
        RefreshButton.IsEnabled = false;
        BusyCard.Visibility = Visibility.Visible;
        Animations.FadeIn(BusyCard);
        BusyPhase.Text = "Asking Windows…";
        Animations.Pulse(BusyPhase);
        var started = DateTime.UtcNow;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => BusyElapsed.Text = Format.Duration(DateTime.UtcNow - started);
        timer.Start();
        try
        {
            double hours = Hours;
            var prog = new Progress<string>(m => BusyPhase.Text = m);
            _snap = await Task.Run(() => WindowsStorage.Snapshot(hours, prog));
            Render(_snap);
        }
        catch (Exception ex) { Ui.Main.Toast("Windows query failed", ex.GetBaseException().Message, true); }
        finally
        {
            timer.Stop();
            Animations.StopPulse(BusyPhase);
            BusyCard.Visibility = Visibility.Collapsed;
            RefreshButton.IsEnabled = true;
            _busy = false;
        }
    }

    private void Render(WindowsStorageSnapshot s)
    {
        OsText.Text = $"{s.Os.Caption} · build {s.Os.BuildNumber}";
        AutomountText.Text = s.AutomountEnabled ? "automount on" : "automount OFF";
        AutomountDot.Fill = (Brush)Application.Current.Resources[s.AutomountEnabled ? "Good" : "Warn"];
        SanText.Text = "SAN policy " + s.SanPolicy;
        ServicePills.ItemsSource = s.Services.Select(x => new ServicePill { S = x }).ToList();
        FindingList.ItemsSource = s.Findings.Select(f => new WinFindingRow { F = f }).ToList();
        DiskList.ItemsSource = s.PhysicalDisks.OrderBy(p => p.Number).Select(p => new WinDiskRow
        {
            P = p, D = s.Disks.FirstOrDefault(d => d.Number == p.Number), Dp = s.DiskpartDisks.FirstOrDefault(d => d.Number == p.Number),
            PnpDevice = s.PnpDisks.Where(x => !x.IsPhantom).FirstOrDefault(x => Same(x.FriendlyName, p.FriendlyName)),
        }).ToList();
        var vols = new List<WinVolumeRow>();
        var usedDp = new HashSet<int>();
        foreach (var v in s.Volumes.OrderBy(v => v.Letter.Length == 0).ThenBy(v => v.Letter))
        {
            var dp = s.DiskpartVolumes.FirstOrDefault(d => !usedDp.Contains(d.Number) && (v.Letter.Length > 0 ? d.Letter.Equals(v.Letter.TrimEnd(':'), StringComparison.OrdinalIgnoreCase) : d.Letter.Length == 0 && d.Label == (v.FileSystemLabel ?? "") && SizeClose(d.Size, v.Size)));
            if (dp != null) usedDp.Add(dp.Number);
            vols.Add(new WinVolumeRow { V = v, Dp = dp });
        }
        foreach (var d in s.DiskpartVolumes.Where(d => !usedDp.Contains(d.Number))) vols.Add(new WinVolumeRow { Dp = d });
        VolumeList.ItemsSource = vols;
        PnpList.ItemsSource = s.PnpDisks.Select(p => new WinPnpRow { P = p }).ToList();
        EventList.ItemsSource = s.Events.Select(e => new WinEventRow { E = e }).ToList();
        if (s.Problems.Count > 0) Append("# queries that failed\n" + string.Join("\n", s.Problems));
        var used = new HashSet<string>(s.Volumes.Select(v => v.Letter).Where(l => l.Length > 0));
        var free = LetterBox.Items.Cast<string>().FirstOrDefault(l => !used.Contains(l));
        if (free != null) LetterBox.SelectedItem = free;
        DiskList_SelectionChanged(this, null!);
        VolumeList_SelectionChanged(this, null!);
    }

    private static bool Same(string? a, string? b) => a != null && b != null && a.Replace(" ", "").Equals(b.Replace(" ", ""), StringComparison.OrdinalIgnoreCase);
    private static bool SizeClose(string diskpartSize, long? bytes)
    {
        if (bytes == null) return true;
        var m = System.Text.RegularExpressions.Regex.Match(diskpartSize, @"(\d+)\s*([KMGT]?)B", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success) return true;
        double v = double.Parse(m.Groups[1].Value); double mult = m.Groups[2].Value.ToUpperInvariant() switch { "K" => 1024, "M" => 1024 * 1024, "G" => 1024.0 * 1024 * 1024, "T" => 1024.0 * 1024 * 1024 * 1024, _ => 1 };
        double approx = v * mult;
        return Math.Abs(approx - bytes.Value) <= Math.Max(approx * 0.02, 2 * 1024 * 1024);
    }

    private void Append(string text)
    {
        Console.AppendText((Console.Text.Length > 0 ? "\n\n" : "") + text.TrimEnd());
        Console.ScrollToEnd();
    }

    // ---- actions ----
    private async Task RunAction(WindowsAction a, bool confirm = true, bool refresh = true)
    {
        if (_busy) return;
        if (confirm && MessageBox.Show(Ui.Main, $"{a.Label}\n\nThis runs:\n{a.Command}\n\nContinue?", "Windows tools", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _busy = true;
        BusyCard.Visibility = Visibility.Visible; BusyPhase.Text = a.Label + "…"; Animations.Pulse(BusyPhase);
        try
        {
            var r = await Task.Run(() => WindowsStorage.Execute(a));
            Append($"> {r.Command}\n{r.Text}{(r.TimedOut ? "\n[timed out]" : r.ExitCode != 0 ? $"\n[exit code {r.ExitCode}]" : "")}");
            Ui.Main.Toast(r.Ok ? a.Label + " done" : a.Label + " failed", r.Ok ? (r.Text.Length > 0 ? FirstLines(r.Text) : "OK") : FirstLines(r.Text.Length > 0 ? r.Text : "see the console"), !r.Ok);
        }
        finally { Animations.StopPulse(BusyPhase); BusyCard.Visibility = Visibility.Collapsed; _busy = false; }
        if (refresh) await RefreshAsync();
    }

    private static string FirstLines(string s) { var l = s.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).Take(3); return string.Join(" ", l); }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = RefreshAsync();
    private void Rescan_Click(object sender, RoutedEventArgs e) => _ = RunAction(WindowsStorage.Actions.Rescan(), confirm: false);
    private void RestartServices_Click(object sender, RoutedEventArgs e) => _ = RunAction(WindowsStorage.Actions.RestartServices());
    private void Automount_Click(object sender, RoutedEventArgs e) { if (_snap != null) _ = RunAction(_snap.AutomountEnabled ? WindowsStorage.Actions.DisableAutomount() : WindowsStorage.Actions.EnableAutomount()); }
    private void Service_Click(object sender, RoutedEventArgs e) { if ((sender as FrameworkElement)?.Tag is ServicePill p && !p.S.Running) _ = RunAction(WindowsStorage.Actions.StartService(p.S.Name!)); }
    private void FindingAction_Click(object sender, RoutedEventArgs e) { if ((sender as FrameworkElement)?.Tag is WinFindingRow row && row.F.Action != null) _ = RunAction(row.F.Action); }
    private void Phantoms_Click(object sender, RoutedEventArgs e) => _ = RunAction(WindowsStorage.Actions.RemovePhantoms());
    private void PnpScan_Click(object sender, RoutedEventArgs e) => _ = RunAction(new WindowsAction(WindowsActionKind.RedetectDevice, "Scan for hardware changes", "pnputil /scan-devices", ""), confirm: false);
    private void Hours_Changed(object sender, RoutedEventArgs e) { if (IsLoaded && _snap != null) _ = RefreshAsync(); }

    private void DiskList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var r = DiskList.SelectedItem as WinDiskRow;
        DiskActions.IsEnabled = r != null;
        foreach (var b in DiskActions.Children.OfType<Button>())
            b.IsEnabled = r != null && (b.Content as string) switch
            {
                "Bring online" => r.IsOffline, "Take offline" => r.D != null && !r.IsOffline && r.D.IsSystem != true && r.D.IsBoot != true,
                "Clear read-only" => r.IsReadOnly, "Import foreign" => r.IsForeign, _ => true,
            };
    }

    private void DiskOnline_Click(object sender, RoutedEventArgs e) { if (DiskList.SelectedItem is WinDiskRow r) _ = RunAction(WindowsStorage.Actions.OnlineDisk(r.Number)); }
    private void DiskOffline_Click(object sender, RoutedEventArgs e) { if (DiskList.SelectedItem is WinDiskRow r) _ = RunAction(WindowsStorage.Actions.OfflineDisk(r.Number)); }
    private void DiskClearRo_Click(object sender, RoutedEventArgs e) { if (DiskList.SelectedItem is WinDiskRow r) _ = RunAction(WindowsStorage.Actions.ClearReadOnly(r.Number)); }
    private void DiskImport_Click(object sender, RoutedEventArgs e) { if (DiskList.SelectedItem is WinDiskRow r) _ = RunAction(WindowsStorage.Actions.ImportForeign(r.Number)); }
    private void DiskRedetect_Click(object sender, RoutedEventArgs e)
    {
        if (DiskList.SelectedItem is not WinDiskRow r) return;
        if (r.D?.IsSystem == true || r.D?.IsBoot == true) { Ui.Main.Toast("Not on the system disk", "Re-detecting the disk Windows runs from would take the system down.", true); return; }
        _ = RunAction(WindowsStorage.Actions.RedetectDevice(r.PnpDevice?.InstanceId ?? "", r.Model));
    }

    private void VolumeList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var r = VolumeList.SelectedItem as WinVolumeRow;
        VolumeActions.IsEnabled = r != null;
        foreach (var b in VolumeActions.Children.OfType<Button>())
            b.IsEnabled = r != null && (b.Content as string) switch
            {
                "Assign letter" => r.Dp != null, "Remove letter" => r.Dp != null && r.HasLetter, "chkdsk (read-only)" => r.HasLetter && r.Fs != "RAW", "Open in Explorer" => r.HasLetter, _ => true,
            };
    }

    private void VolAssign_Click(object sender, RoutedEventArgs e) { if (VolumeList.SelectedItem is WinVolumeRow { Dp: { } dp }) _ = RunAction(WindowsStorage.Actions.AssignLetter(dp.Number, LetterBox.SelectedItem as string)); }
    private void VolRemove_Click(object sender, RoutedEventArgs e) { if (VolumeList.SelectedItem is WinVolumeRow { Dp: { } dp }) _ = RunAction(WindowsStorage.Actions.RemoveLetter(dp.Number)); }
    private void VolChkdsk_Click(object sender, RoutedEventArgs e) { if (VolumeList.SelectedItem is WinVolumeRow r && r.HasLetter) _ = RunAction(WindowsStorage.Actions.ChkdskScan(r.Letter), confirm: false, refresh: false); }
    private void VolOpen_Click(object sender, RoutedEventArgs e) { if (VolumeList.SelectedItem is WinVolumeRow r && r.HasLetter) Ui.OpenFolder(r.Letter + "\\"); }

    private void ConsoleCopy_Click(object sender, RoutedEventArgs e) { try { Clipboard.SetText(Console.Text); Ui.Main.Toast("Copied", "Console text copied to the clipboard."); } catch { } }
    private void ConsoleClear_Click(object sender, RoutedEventArgs e) => Console.Clear();

    private void Report_Click(object sender, RoutedEventArgs e)
    {
        if (_snap == null) { Ui.Main.Toast("Nothing to report yet", "Press Refresh first.", true); return; }
        Ui.SaveReport(WindowsStorage.BuildReport(_snap), $"windows-storage-{DateTime.Now:yyyyMMdd-HHmm}.html");
    }
}
