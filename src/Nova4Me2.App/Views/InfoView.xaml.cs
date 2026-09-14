using System.Windows;
using System.Windows.Controls;
using Nova4Me2.Core.Hardware;
using Nova4Me2.Core.Reports;
using Nova4Me2.Core.Util;

namespace Nova4Me2.App.Views;

public partial class InfoView : UserControl, INovaView
{
    public InfoView()
    {
        InitializeComponent();
        Ui.State.SourceChanged += () => Dispatcher.BeginInvoke(OnShown);
    }

    public void OnShown()
    {
        var hw = Ui.State.Hardware;
        if (hw == null)
        {
            ModelText.Text = "Open a drive first."; FamilyText.Text = ""; CapChip.Text = BusChip.Text = SerialChip.Text = FwChip.Text = HealthChipText.Text = "";
            IdentityRows.ItemsSource = NvmeRows.ItemsSource = AtaRows.ItemsSource = PartRows.ItemsSource = WinRows.ItemsSource = null;
            Picture.Set("", "", "generic", "#7A8699", "", "");
            ReportButton.IsEnabled = false;
            return;
        }
        ReportButton.IsEnabled = true;
        ModelText.Text = hw.Model;
        FamilyText.Text = $"{hw.VendorInfo.Name}{(hw.ModelInfo != null ? " · " + hw.ModelInfo.Family : "")}";
        CapChip.Text = hw.CapacityLabel;
        BusChip.Text = hw.BusType + (hw.ViaUsbBridge ? " bridge" : "");
        SerialChip.Text = "S/N " + (hw.Serial.Length > 0 ? hw.Serial : "n/a");
        FwChip.Text = "FW " + (hw.Firmware.Length > 0 ? hw.Firmware : "n/a");
        if (hw.NvmeHealth is { } n) { HealthChipText.Text = n.CriticalWarning == 0 ? $"SMART OK · {n.TemperatureCelsius:0} °C · {n.PercentageUsed}% used" : "SMART WARNING: " + n.CriticalWarningText; HealthChip.SetResourceReference(BackgroundProperty, n.CriticalWarning == 0 ? "PanelAlt" : "Bad"); }
        else if (hw.AtaSmart is { } a) { HealthChipText.Text = a.AnyFailing ? "SMART: attributes FAILING" : "SMART OK"; HealthChip.SetResourceReference(BackgroundProperty, a.AnyFailing ? "Bad" : "PanelAlt"); }
        else { HealthChipText.Text = "SMART n/a"; HealthChip.SetResourceReference(BackgroundProperty, "PanelAlt"); }
        Picture.Set(hw.Model, hw.VendorInfo.Name, hw.VendorInfo.Key, hw.VendorInfo.BrandColor, hw.CapacityLabel, hw.ModelInfo?.FormFactor ?? (hw.BusType.Contains("NVMe") || hw.ModelInfo == null ? "M.2" : "2.5"));
        IdentityRows.ItemsSource = hw.IdentityRows().Select(r => new KeyValueRow { Key = r.Key, Value = r.Value }).ToList();
        NvmeRows.ItemsSource = hw.NvmeRows().Select(r => new KeyValueRow { Key = r.Key, Value = r.Value }).ToList();
        AtaRows.ItemsSource = hw.AtaSmart != null
            ? hw.AtaSmart.Attributes.Select(at => new KeyValueRow { Key = $"{at.Id} {at.Name}", Value = $"current {at.Current}, worst {at.Worst}, threshold {at.Threshold}, raw {at.Raw}{(at.Failing ? " — FAILING" : "")}" }).ToList()
            : new List<KeyValueRow> { new() { Key = "ATA SMART", Value = hw.AtaError.Length > 0 ? "unavailable: " + hw.AtaError : "not an ATA device or not queried" } };
        var parts = new List<KeyValueRow>();
        if (hw.Partitions is { } t)
        {
            parts.Add(new KeyValueRow { Key = "Scheme", Value = t.Scheme + (t.UsedBackupGpt ? " (primary GPT damaged, backup used)" : "") });
            if (t.Scheme == Core.Partitions.PartitionScheme.Gpt) parts.Add(new KeyValueRow { Key = "Disk GUID", Value = t.DiskGuid.ToString() });
            foreach (var p in t.Partitions) parts.Add(new KeyValueRow { Key = $"Partition {p.Index}", Value = $"{p.TypeName} {p.Name}  start {Format.Bytes(p.StartOffset)} ({p.StartOffset:N0})  size {Format.Bytes(p.Length)}" });
            foreach (var pr in t.Problems) parts.Add(new KeyValueRow { Key = "Note", Value = pr });
        }
        foreach (var v in Ui.State.Volumes) parts.Add(new KeyValueRow { Key = "NTFS volume", Value = v.Display + (v.Notes.Count > 0 ? " — " + string.Join("; ", v.Notes) : "") });
        PartRows.ItemsSource = parts;
        WinRows.ItemsSource = hw.WindowsVolumes.Count > 0 ? hw.WindowsVolumes.Select(v => new KeyValueRow { Key = v.MountPoints.Length > 0 ? string.Join(", ", v.MountPoints) : "(no letter)", Value = v.Display }).ToList()
            : new List<KeyValueRow> { new() { Key = "Windows volumes", Value = "None visible (image file, or Windows found no mountable volume on this disk)." } };
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => OnShown();

    private void Report_Click(object sender, RoutedEventArgs e)
    {
        if (Ui.State.Hardware is not { } hw) return;
        Ui.SaveReport(ReportBuilder.FromHardware(hw), $"Nova4Me2-drive-{DateTime.Now:yyyyMMdd-HHmm}");
    }
}
