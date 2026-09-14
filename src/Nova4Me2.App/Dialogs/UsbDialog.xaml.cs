using System.Windows;
using System.Windows.Controls;
using Nova4Me2.Core.Devices.Windows;

namespace Nova4Me2.App.Dialogs;

public partial class UsbDialog : Window
{
    public UsbDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    private void Refresh()
    {
        StatusPanel.Children.Clear();
        if (!OperatingSystem.IsWindows()) { StatusPanel.Children.Add(new TextBlock { Text = "Windows only." }); return; }
        var s = UsbStabilizer.Status();
        void Row(string k, bool? v) { StatusPanel.Children.Add(new TextBlock { Text = $"{(v == true ? "✔" : v == false ? "✖" : "?")}  {k}", Margin = new Thickness(0, 2, 0, 2) }); }
        Row("USB selective suspend disabled (AC)", s.SelectiveSuspendDisabledAc);
        Row("USB selective suspend disabled (battery)", s.SelectiveSuspendDisabledDc);
        Row("Hard disk idle time-out = never", s.DiskIdleTimeoutNever);
        Row("Windows automount disabled (stops repeated RAW probing)", s.AutomountDisabled);
        Row($"Disk I/O time-out raised ({(s.DiskTimeoutSeconds?.ToString() ?? "default")} s)", s.DiskTimeoutSeconds is >= 120);
        Row($"USB storage devices with power management off: {s.UsbStorageDevicesTuned} of {s.UsbStorageDevicesFound}", s.UsbStorageDevicesFound > 0 ? s.UsbStorageDevicesTuned == s.UsbStorageDevicesFound : null);
        foreach (var n in s.Notes) StatusPanel.Children.Add(new TextBlock { Text = n, Style = (Style)FindResource("Muted") });
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        try { LogText.Text = string.Join("\n", UsbStabilizer.Apply()); } catch (Exception ex) { LogText.Text = ex.Message; }
        Refresh();
    }

    private void Revert_Click(object sender, RoutedEventArgs e)
    {
        try { LogText.Text = string.Join("\n", UsbStabilizer.Revert()); } catch (Exception ex) { LogText.Text = ex.Message; }
        Refresh();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
