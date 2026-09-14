using Nova4Me2.Core.Devices;
using Nova4Me2.Core.Devices.Windows;
using Nova4Me2.Core.Partitions;
using Nova4Me2.Core.Util;

namespace Nova4Me2.Core.Hardware;

/// <summary>Everything we can learn about a drive without writing to it: identity, interface, geometry, SMART, partitions.</summary>
public sealed class HardwareInfo
{
    public string Source { get; init; } = "";
    public string Model { get; init; } = "";
    public string Serial { get; init; } = "";
    public string Firmware { get; init; } = "";
    public string Vendor { get; init; } = "";
    public string BusType { get; init; } = "";
    public bool ViaUsbBridge { get; init; }
    public long Length { get; init; }
    public int LogicalSectorSize { get; init; }
    public int PhysicalSectorSize { get; init; }
    public bool Removable { get; init; }
    public bool SeekPenalty { get; init; }
    public bool TrimSupported { get; init; }
    public uint MaxTransferLength { get; init; }
    public bool IsSystemDisk { get; init; }
    public VendorInfo VendorInfo { get; init; } = VendorDatabase.Unknown;
    public ModelInfo? ModelInfo { get; init; }
    public NvmeIdentifyController? NvmeIdentify { get; init; }
    public NvmeHealthLog? NvmeHealth { get; init; }
    public string NvmeError { get; init; } = "";
    public AtaSmartData? AtaSmart { get; init; }
    public string AtaError { get; init; } = "";
    public PartitionTableInfo? Partitions { get; init; }
    public List<WindowsVolumeInfo> WindowsVolumes { get; init; } = new();
    public string CapacityLabel => VendorDatabase.CapacityFromModelCode(Model) is { Length: > 0 } c ? c : Format.Bytes(Length);
    public bool SmartAvailable => NvmeHealth != null || AtaSmart != null;

    public List<(string Key, string Value)> IdentityRows()
    {
        var r = new List<(string, string)>
        {
            ("Model", Model), ("Vendor", VendorInfo.Name), ("Serial number", Serial.Length > 0 ? Serial : "(not reported through this interface)"), ("Firmware", Firmware.Length > 0 ? Firmware : "(not reported)"),
            ("Capacity", $"{Format.Bytes(Length)} ({Length:N0} bytes)"), ("Interface (as seen by Windows)", BusType + (ViaUsbBridge ? " — USB bridge in front of the drive" : "")),
            ("Logical sector", LogicalSectorSize > 0 ? $"{LogicalSectorSize} bytes" : "?"), ("Physical sector", PhysicalSectorSize > 0 ? $"{PhysicalSectorSize} bytes" : "?"),
            ("Media", SeekPenalty ? "Rotational (HDD)" : "Solid state"), ("TRIM", TrimSupported ? "supported" : "not reported"), ("Max transfer", MaxTransferLength > 0 ? Format.Bytes(MaxTransferLength) : "?"),
            ("System disk", IsSystemDisk ? "YES — never clone onto this disk" : "no")
        };
        if (ModelInfo != null)
        {
            r.Add(("Product family", ModelInfo.Family));
            if (ModelInfo.FormFactor.Length > 0) r.Add(("Form factor", ModelInfo.FormFactor));
            if (ModelInfo.Interface.Length > 0) r.Add(("Native interface", ModelInfo.Interface));
            if (ModelInfo.Controller.Length > 0) r.Add(("Controller", ModelInfo.Controller));
            if (ModelInfo.Nand.Length > 0) r.Add(("NAND", ModelInfo.Nand));
            if (ModelInfo.Dram.Length > 0) r.Add(("DRAM cache", ModelInfo.Dram));
            if (ModelInfo.SeqRead.Length > 0) r.Add(("Rated sequential read/write", $"{ModelInfo.SeqRead} / {ModelInfo.SeqWrite}"));
            if (ModelInfo.Endurance.Length > 0) r.Add(("Rated endurance", ModelInfo.Endurance));
            if (ModelInfo.Warranty.Length > 0) r.Add(("Warranty", ModelInfo.Warranty));
        }
        return r;
    }

    public List<(string Key, string Value)> NvmeRows()
    {
        var r = new List<(string, string)>();
        if (NvmeIdentify is { } id)
        {
            r.Add(("NVMe version", id.VersionText)); r.Add(("PCI vendor ID", $"0x{id.VendorId:X4}")); r.Add(("Controller ID", id.ControllerId.ToString()));
            r.Add(("Namespaces", id.NumberOfNamespaces.ToString())); r.Add(("Firmware slots", $"{id.FirmwareSlots}{(id.FirmwareSlot1ReadOnly ? " (slot 1 read-only)" : "")}"));
            r.Add(("Firmware update supported", id.SupportsFirmwareDownload ? "yes" : "no")); r.Add(("Activate without reset", id.FirmwareActivationWithoutReset ? "yes" : "no"));
            r.Add(("Max data transfer", id.MaxDataTransferShift == 0 ? "unlimited" : $"2^{id.MaxDataTransferShift} × min page")); r.Add(("Format NVM / Sanitize", $"{(id.SupportsFormatNvm ? "yes" : "no")} / {(id.SupportsSanitize ? "yes" : "no")}"));
            r.Add(("Temperature thresholds", $"warning {id.WarningTempThreshold - 273} °C, critical {id.CriticalTempThreshold - 273} °C"));
            if (id.TotalNvmCapacity > 0) r.Add(("Total NVM capacity", Format.Bytes(NvmeHealthLog.Clamp(id.TotalNvmCapacity))));
        }
        if (NvmeHealth is { } h)
        {
            r.Add(("Critical warning", h.CriticalWarningText)); r.Add(("Temperature", $"{h.TemperatureCelsius:0} °C")); r.Add(("Available spare", $"{h.AvailableSpare}% (threshold {h.AvailableSpareThreshold}%)"));
            r.Add(("Percentage used (wear)", $"{h.PercentageUsed}%")); r.Add(("Data read / written", $"{Format.Bytes(h.BytesRead)} / {Format.Bytes(h.BytesWritten)}"));
            r.Add(("Power cycles / on hours", $"{h.PowerCycles} / {h.PowerOnHours}")); r.Add(("Unsafe shutdowns", h.UnsafeShutdowns.ToString())); r.Add(("Media & data integrity errors", h.MediaErrors.ToString()));
            r.Add(("Error log entries", h.ErrorLogEntries.ToString())); r.Add(("Controller busy time", $"{h.ControllerBusyMinutes} min"));
        }
        if (r.Count == 0) r.Add(("NVMe data", NvmeError.Length > 0 ? "unavailable: " + NvmeError : "not an NVMe device or not queried"));
        return r;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static HardwareInfo Collect(int driveNumber)
    {
        using var d = new WindowsPhysicalDrive($@"\\.\PhysicalDrive{driveNumber}");
        var s = d.Info;
        NvmeIdentifyController? id = null; NvmeHealthLog? hl = null; string nerr = "";
        if (s.IsNvme || s.IsUsb || s.BusType == 1)
        {
            id = NvmeQuery.IdentifyController(d.Handle, out nerr);
            hl = NvmeQuery.HealthLog(d.Handle, out var e2);
            if (hl == null && nerr.Length == 0) nerr = e2;
        }
        AtaSmartData? ata = null; string aerr = "";
        if (id == null && (s.BusType is 3 or 11 or 7 or 1 or 10)) ata = AtaQuery.Smart(d.Handle, out aerr);
        PartitionTableInfo? table = null;
        try { table = PartitionTable.Read(d); } catch { }
        var vols = DriveEnumerator.MapVolumes().TryGetValue(driveNumber, out var v) ? v : new();
        string model = id?.Model.Length > 0 ? id.Model : s.Model;
        string serial = id?.Serial.Length > 0 ? id.Serial : ata?.Serial.Length > 0 ? ata.Serial : s.Serial;
        string fw = id?.Firmware.Length > 0 ? id.Firmware : ata?.Firmware.Length > 0 ? ata.Firmware : s.Revision;
        var vendor = VendorDatabase.Identify(model, s.Vendor);
        return new HardwareInfo
        {
            Source = d.DevicePath, Model = model, Serial = serial, Firmware = fw, Vendor = s.Vendor, BusType = s.BusTypeName, ViaUsbBridge = s.IsUsb, Length = d.Length,
            LogicalSectorSize = s.LogicalSectorSize > 0 ? s.LogicalSectorSize : d.SectorSize, PhysicalSectorSize = s.PhysicalSectorSize, Removable = s.Removable, SeekPenalty = s.SeekPenalty,
            TrimSupported = s.TrimSupported, MaxTransferLength = s.MaxTransferLength, IsSystemDisk = DriveEnumerator.SystemDiskNumber() == driveNumber, VendorInfo = vendor, ModelInfo = VendorDatabase.Lookup(model),
            NvmeIdentify = id, NvmeHealth = hl, NvmeError = nerr, AtaSmart = ata, AtaError = aerr, Partitions = table, WindowsVolumes = vols
        };
    }

    /// <summary>Minimal info for an image file or non-Windows device.</summary>
    public static HardwareInfo ForDevice(IBlockDevice dev, string source)
    {
        PartitionTableInfo? table = null;
        try { table = PartitionTable.Read(dev); } catch { }
        return new HardwareInfo { Source = source, Model = dev.Description, BusType = "Image file", Length = dev.Length, LogicalSectorSize = dev.SectorSize, PhysicalSectorSize = dev.SectorSize, SeekPenalty = false, Partitions = table };
    }
}
