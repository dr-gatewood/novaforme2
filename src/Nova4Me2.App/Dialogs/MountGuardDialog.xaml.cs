using System.Windows;
using Nova4Me2.App.Services;
using Nova4Me2.Core.Devices.Windows;

namespace Nova4Me2.App.Dialogs;

public enum MountGuardChoice { Ignore, DisableAutomount, TakeOffline }

public partial class MountGuardDialog : Window
{
    private readonly int? _driveNumber;
    private readonly ProtectionWatcher.Target? _target;
    public MountGuardChoice Choice { get; private set; } = MountGuardChoice.Ignore;

    public MountGuardDialog(int? driveNumber, string driveDescription, ProtectionWatcher.Target? target = null)
    {
        InitializeComponent();
        _driveNumber = driveNumber;
        _target = target;
        DriveText.Text = driveNumber is { } n ? $"Disk {n}: {driveDescription}" : driveDescription;
        if (driveNumber == null) ((FrameworkElement)FindName("DriveText")!).Visibility = Visibility.Collapsed;
    }

    private void Remember() { if (DontAsk.IsChecked == true) { AppState.Current.Settings.MountGuardDismissed = true; AppState.Current.Settings.Save(); } }

    private void Ignore_Click(object sender, RoutedEventArgs e) { Remember(); Choice = MountGuardChoice.Ignore; DialogResult = false; }

    private async void Automount_Click(object sender, RoutedEventArgs e)
    {
        try { await Task.Run(() => DiskControl.SetAutomount(false)); Choice = MountGuardChoice.DisableAutomount; Remember(); DialogResult = true; }
        catch (Exception ex) { ResultText.Text = "Failed: " + ex.Message; }
    }

    private void Offline_Click(object sender, RoutedEventArgs e)
    {
        if (_driveNumber is not { } n) { Automount_Click(sender, e); return; }
        // Hand the job to the watcher: it retries with short, abandonable attempts on every arrival event until the
        // attribute lands, so a disk that drops off the bus every few seconds still gets protected — and this dialog never hangs.
        var t = _target ?? new ProtectionWatcher.Target("", "", 0, $"disk {n}");
        AppState.Current.Protection.Request(t);
        try { Task.Run(() => DiskControl.SetAutomount(false)); } catch { }
        Choice = MountGuardChoice.TakeOffline; Remember(); DialogResult = true;
    }
}
