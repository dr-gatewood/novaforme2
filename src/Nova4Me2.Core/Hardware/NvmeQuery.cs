using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using Nova4Me2.Core.Devices.Windows;
using Nova4Me2.Core.Util;

namespace Nova4Me2.Core.Hardware;

/// <summary>NVMe SMART / Health Information log page (Log ID 02h).</summary>
public sealed class NvmeHealthLog
{
    public byte CriticalWarning { get; init; }
    public int TemperatureKelvin { get; init; }
    public byte AvailableSpare { get; init; }
    public byte AvailableSpareThreshold { get; init; }
    public byte PercentageUsed { get; init; }
    public UInt128 DataUnitsRead { get; init; }
    public UInt128 DataUnitsWritten { get; init; }
    public UInt128 HostReadCommands { get; init; }
    public UInt128 HostWriteCommands { get; init; }
    public UInt128 ControllerBusyMinutes { get; init; }
    public UInt128 PowerCycles { get; init; }
    public UInt128 PowerOnHours { get; init; }
    public UInt128 UnsafeShutdowns { get; init; }
    public UInt128 MediaErrors { get; init; }
    public UInt128 ErrorLogEntries { get; init; }
    public uint WarningTempMinutes { get; init; }
    public uint CriticalTempMinutes { get; init; }
    public ushort[] SensorsKelvin { get; init; } = Array.Empty<ushort>();
    public byte[] Raw { get; init; } = Array.Empty<byte>();

    public bool SpareBelowThreshold => (CriticalWarning & 0x01) != 0;
    public bool TemperatureExceeded => (CriticalWarning & 0x02) != 0;
    public bool ReliabilityDegraded => (CriticalWarning & 0x04) != 0;
    public bool ReadOnlyMode => (CriticalWarning & 0x08) != 0;
    public bool VolatileBackupFailed => (CriticalWarning & 0x10) != 0;
    public double TemperatureCelsius => TemperatureKelvin - 273.15;
    public long BytesRead => Clamp(DataUnitsRead * 512000);
    public long BytesWritten => Clamp(DataUnitsWritten * 512000);
    public string CriticalWarningText
    {
        get
        {
            var l = new List<string>();
            if (SpareBelowThreshold) l.Add("available spare below threshold");
            if (TemperatureExceeded) l.Add("temperature over/under threshold");
            if (ReliabilityDegraded) l.Add("NVM subsystem reliability degraded (media/internal errors)");
            if (ReadOnlyMode) l.Add("MEDIA IN READ-ONLY MODE");
            if (VolatileBackupFailed) l.Add("volatile memory backup failed");
            return l.Count == 0 ? "none" : string.Join("; ", l);
        }
    }

    public static NvmeHealthLog Parse(ReadOnlySpan<byte> b)
    {
        var sensors = new ushort[8];
        for (int i = 0; i < 8; i++) sensors[i] = Bin.U16(b, 200 + i * 2);
        return new NvmeHealthLog
        {
            CriticalWarning = b[0], TemperatureKelvin = Bin.U16(b, 1), AvailableSpare = b[3], AvailableSpareThreshold = b[4], PercentageUsed = b[5],
            DataUnitsRead = U128(b, 32), DataUnitsWritten = U128(b, 48), HostReadCommands = U128(b, 64), HostWriteCommands = U128(b, 80), ControllerBusyMinutes = U128(b, 96),
            PowerCycles = U128(b, 112), PowerOnHours = U128(b, 128), UnsafeShutdowns = U128(b, 144), MediaErrors = U128(b, 160), ErrorLogEntries = U128(b, 176),
            WarningTempMinutes = Bin.U32(b, 192), CriticalTempMinutes = Bin.U32(b, 196), SensorsKelvin = sensors, Raw = b.ToArray()
        };
    }

    public static long Clamp(UInt128 v) => v > (UInt128)long.MaxValue ? long.MaxValue : (long)v;

    private static UInt128 U128(ReadOnlySpan<byte> b, int o) => new(Bin.U64(b, o + 8), Bin.U64(b, o));
}

/// <summary>Subset of the NVMe Identify Controller data structure (CNS 01h).</summary>
public sealed class NvmeIdentifyController
{
    public ushort VendorId { get; init; }
    public ushort SubsystemVendorId { get; init; }
    public string Serial { get; init; } = "";
    public string Model { get; init; } = "";
    public string Firmware { get; init; } = "";
    public byte[] IeeeOui { get; init; } = Array.Empty<byte>();
    public byte MaxDataTransferShift { get; init; }
    public ushort ControllerId { get; init; }
    public uint Version { get; init; }
    public ushort Oacs { get; init; }
    public byte Frmw { get; init; }
    public byte Npss { get; init; }
    public uint NumberOfNamespaces { get; init; }
    public ushort Oncs { get; init; }
    public UInt128 TotalNvmCapacity { get; init; }
    public ushort WarningTempThreshold { get; init; }
    public ushort CriticalTempThreshold { get; init; }
    public byte[] Raw { get; init; } = Array.Empty<byte>();

    public string VersionText => $"{Version >> 16}.{(Version >> 8) & 0xFF}{((Version & 0xFF) != 0 ? "." + (Version & 0xFF) : "")}";
    public int FirmwareSlots => (Frmw >> 1) & 0x7;
    public bool FirmwareSlot1ReadOnly => (Frmw & 1) != 0;
    public bool FirmwareActivationWithoutReset => (Frmw & 0x10) != 0;
    public bool SupportsFirmwareDownload => (Oacs & 0x04) != 0;
    public bool SupportsFormatNvm => (Oacs & 0x02) != 0;
    public bool SupportsSanitize => (Raw.Length > 331 && Bin.U32(Raw, 328) != 0);
    public bool SupportsTrim => (Oncs & 0x04) != 0;

    public static NvmeIdentifyController Parse(ReadOnlySpan<byte> b) => new()
    {
        VendorId = Bin.U16(b, 0), SubsystemVendorId = Bin.U16(b, 2), Serial = Bin.Ascii(b, 4, 20), Model = Bin.Ascii(b, 24, 40), Firmware = Bin.Ascii(b, 64, 8), IeeeOui = b.Slice(73, 3).ToArray(),
        MaxDataTransferShift = b[77], ControllerId = Bin.U16(b, 78), Version = Bin.U32(b, 80), Oacs = Bin.U16(b, 256), Frmw = b[260], Npss = b[263], NumberOfNamespaces = Bin.U32(b, 516), Oncs = Bin.U16(b, 520),
        TotalNvmCapacity = new UInt128(Bin.U64(b, 288), Bin.U64(b, 280)), WarningTempThreshold = Bin.U16(b, 266), CriticalTempThreshold = Bin.U16(b, 268), Raw = b.ToArray()
    };
}

/// <summary>NVMe pass-through via IOCTL_STORAGE_QUERY_PROPERTY (StorageAdapterProtocolSpecificProperty). Works for natively attached NVMe drives; USB bridges normally do not forward it.</summary>
[SupportedOSPlatform("windows")]
public static class NvmeQuery
{
    private const uint StorageAdapterProtocolSpecificProperty = 49;
    private const uint StorageDeviceProtocolSpecificProperty = 50;
    private const uint ProtocolTypeNvme = 3;
    private const uint NVMeDataTypeIdentify = 1;
    private const uint NVMeDataTypeLogPage = 2;

    public static NvmeIdentifyController? IdentifyController(SafeFileHandle h, out string error)
    {
        var d = Query(h, NVMeDataTypeIdentify, 1, 0, 4096, out error);
        return d == null ? null : NvmeIdentifyController.Parse(d);
    }

    public static NvmeHealthLog? HealthLog(SafeFileHandle h, out string error)
    {
        var d = Query(h, NVMeDataTypeLogPage, 2, 0, 512, out error);
        return d == null ? null : NvmeHealthLog.Parse(d);
    }

    private static byte[]? Query(SafeFileHandle h, uint dataType, uint requestValue, uint subValue, int dataLen, out string error)
    {
        error = "";
        foreach (uint propertyId in new[] { StorageAdapterProtocolSpecificProperty, StorageDeviceProtocolSpecificProperty })
        {
            var q = new byte[8 + 40 + dataLen];
            Bin.PutU32(q, 0, propertyId);
            Bin.PutU32(q, 4, 0); // PropertyStandardQuery
            Bin.PutU32(q, 8, ProtocolTypeNvme);
            Bin.PutU32(q, 12, dataType);
            Bin.PutU32(q, 16, requestValue);
            Bin.PutU32(q, 20, subValue);
            Bin.PutU32(q, 24, 40);          // ProtocolDataOffset (from start of STORAGE_PROTOCOL_SPECIFIC_DATA)
            Bin.PutU32(q, 28, (uint)dataLen); // ProtocolDataLength
            var o = NativeMethods.Ioctl(h, NativeMethods.IOCTL_STORAGE_QUERY_PROPERTY, q, q.Length, out int err, 5000);
            if (o == null) { error = NativeMethods.ErrorText(err); continue; }
            if (o.Length < 48 + dataLen) { error = "short reply"; continue; }
            uint off = Bin.U32(o, 8 + 16), len = Bin.U32(o, 8 + 20);
            if (off == 0 || len == 0 || 8 + off + len > o.Length) { error = "reply carries no protocol data (bridge does not forward NVMe commands)"; continue; }
            return o.AsSpan((int)(8 + off), (int)Math.Min(len, (uint)dataLen)).ToArray();
        }
        return null;
    }
}
