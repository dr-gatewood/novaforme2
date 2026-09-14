using System.Windows;
using System.Windows.Controls;
using Nova4Me2.Core.Hardware;

namespace Nova4Me2.App.Views;

public partial class FirmwareView : UserControl, INovaView
{
    private VendorInfo _vendor = VendorDatabase.Unknown;

    public FirmwareView()
    {
        InitializeComponent();
        Ui.State.SourceChanged += () => Dispatcher.BeginInvoke(OnShown);
    }

    public void OnShown()
    {
        var hw = Ui.State.Hardware;
        if (hw == null) { ModelText.Text = "Open a drive first."; FamilyText.Text = ""; InstalledText.Text = "—"; LatestText.Text = "—"; StatusText.Text = ""; IssuesText.Text = ""; NvmeText.Text = ""; KnownText.Text = ""; ToolNote.Text = ""; return; }
        _vendor = hw.VendorInfo;
        var mi = hw.ModelInfo;
        ModelText.Text = hw.Model;
        FamilyText.Text = (mi?.Family ?? hw.VendorInfo.Name) + (hw.ViaUsbBridge ? "  ·  seen through a USB bridge" : "");
        InstalledText.Text = hw.Firmware.Length > 0 ? hw.Firmware : "unknown";
        LatestText.Text = mi?.LatestKnownFirmware is { Length: > 0 } l ? l : "no table entry";
        if (hw.Firmware.Length == 0) StatusText.Text = "Firmware revision not reported through this interface (USB bridges often hide it). Attach natively to read it.";
        else if (mi == null || mi.KnownFirmware.Length == 0) StatusText.Text = "No reference data for this model; check with the vendor tool.";
        else if (hw.Firmware.Equals(mi.LatestKnownFirmware, StringComparison.OrdinalIgnoreCase)) StatusText.Text = "Installed firmware matches the latest version known to Nova4Me2.";
        else if (mi.KnownFirmware.Any(f => f.Equals(hw.Firmware, StringComparison.OrdinalIgnoreCase))) StatusText.Text = "An older known revision is installed; a newer one exists. Update only after your data is safe.";
        else StatusText.Text = "Installed revision is not in the reference table (newer than the table, or OEM firmware).";
        KnownText.Text = mi is { KnownFirmware.Length: > 0 } ? "Known revisions (oldest → newest): " + string.Join(" → ", mi.KnownFirmware) + ". The table is a static reference; always confirm on the vendor site." : "";
        IssuesText.Text = mi?.KnownIssues is { Length: > 0 } ki ? ki : "No model-specific issues recorded. General rule for SSDs after a power loss: if the drive reports read-only mode or its capacity as 0, it has entered a firmware protection state; copy data off and contact the vendor.";
        if (hw.NvmeIdentify is { } id)
            NvmeText.Text = $"NVMe {id.VersionText}, {id.FirmwareSlots} firmware slot(s){(id.FirmwareSlot1ReadOnly ? " (slot 1 read-only/factory)" : "")}, firmware download {(id.SupportsFirmwareDownload ? "supported" : "not supported")}, activation without reset {(id.FirmwareActivationWithoutReset ? "yes" : "no")}.";
        else NvmeText.Text = hw.NvmeError.Length > 0 ? "NVMe identify unavailable: " + hw.NvmeError : "Not an NVMe device or not queried.";
        ToolButton.Content = _vendor.ToolName.Length > 0 ? "Open " + _vendor.ToolName : "Vendor tool";
        ToolButton.IsEnabled = _vendor.ToolUrl.Length > 0;
        SupportButton.IsEnabled = _vendor.SupportUrl.Length > 0;
        ToolNote.Text = _vendor.Note;
    }

    private void Tool_Click(object sender, RoutedEventArgs e) => Ui.OpenUrl(_vendor.ToolUrl);
    private void Support_Click(object sender, RoutedEventArgs e) => Ui.OpenUrl(_vendor.SupportUrl);
}
