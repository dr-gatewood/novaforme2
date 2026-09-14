using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using Nova4Me2.Core.Util;

namespace Nova4Me2.Core.Devices.Windows;

/// <summary>Identity information from IOCTL_STORAGE_QUERY_PROPERTY (works for NVMe, SATA and USB bridged devices).</summary>
public sealed class StorageInfo
{
    public string Vendor { get; init; } = "";
    public string Model { get; init; } = "";
    public string Revision { get; init; } = "";
    public string Serial { get; init; } = "";
    public int BusType { get; init; }
    public bool Removable { get; init; }
    public bool CommandQueueing { get; init; }
    public uint MaxTransferLength { get; init; }
    public uint AlignmentMask { get; init; }
    public int PhysicalSectorSize { get; init; }
    public int LogicalSectorSize { get; init; }
    public bool SeekPenalty { get; init; } = true;
    public bool TrimSupported { get; init; }
    public bool IsWindowsHost { get; init; }

    public string BusTypeName => BusType switch
    {
        1 => "SCSI", 2 => "ATAPI", 3 => "ATA", 4 => "IEEE 1394", 5 => "SSA", 6 => "Fibre Channel", 7 => "USB", 8 => "RAID",
        9 => "iSCSI", 10 => "SAS", 11 => "SATA", 12 => "SD", 13 => "MMC", 14 => "Virtual", 15 => "File-backed virtual",
        16 => "Storage Spaces", 17 => "NVMe", 18 => "SCM", 19 => "UFS", _ => $"Unknown ({BusType})"
    };

    public bool IsUsb => BusType == 7;
    public bool IsNvme => BusType == 17;

    [SupportedOSPlatform("windows")]
    public static StorageInfo Query(SafeFileHandle h)
    {
        string vendor = "", model = "", rev = "", serial = "";
        int bus = 0; bool removable = false, cq = false;
        Span<byte> q = stackalloc byte[12];
        q.Clear();
        // STORAGE_PROPERTY_QUERY { PropertyId = StorageDeviceProperty (0), QueryType = PropertyStandardQuery (0) }
        var d = NativeMethods.Ioctl(h, NativeMethods.IOCTL_STORAGE_QUERY_PROPERTY, q, 4096, out _);
        if (d != null && d.Length >= 40)
        {
            removable = d[10] != 0; cq = d[11] != 0;
            vendor = Str(d, Bin.U32(d, 12)); model = Str(d, Bin.U32(d, 16)); rev = Str(d, Bin.U32(d, 20)); serial = Str(d, Bin.U32(d, 24));
            bus = (int)Bin.U32(d, 28);
            // USB bridges often report the vendor as a separate word; merge for a readable model name.
            if (vendor.Length > 0 && !model.StartsWith(vendor, StringComparison.OrdinalIgnoreCase)) model = (vendor + " " + model).Trim();
            serial = DecodeSerial(serial);
        }
        uint maxXfer = 0, align = 0;
        Bin.PutU32(q, 0, 1); // StorageAdapterProperty
        var a = NativeMethods.Ioctl(h, NativeMethods.IOCTL_STORAGE_QUERY_PROPERTY, q, 64, out _);
        if (a != null && a.Length >= 24) { maxXfer = Bin.U32(a, 8); align = Bin.U32(a, 16); }
        int phys = 0, logical = 0;
        Bin.PutU32(q, 0, 6); // StorageAccessAlignmentProperty
        var al = NativeMethods.Ioctl(h, NativeMethods.IOCTL_STORAGE_QUERY_PROPERTY, q, 64, out _);
        // STORAGE_ACCESS_ALIGNMENT_DESCRIPTOR: BytesPerCacheLine@8, BytesOffsetForCacheAlignment@12, BytesPerLogicalSector@16, BytesPerPhysicalSector@20
        if (al != null && al.Length >= 28) { logical = (int)Bin.U32(al, 16); phys = (int)Bin.U32(al, 20); }
        bool seek = true;
        Bin.PutU32(q, 0, 7); // StorageDeviceSeekPenaltyProperty
        var sp = NativeMethods.Ioctl(h, NativeMethods.IOCTL_STORAGE_QUERY_PROPERTY, q, 16, out _);
        if (sp != null && sp.Length >= 9) seek = sp[8] != 0;
        bool trim = false;
        Bin.PutU32(q, 0, 8); // StorageDeviceTrimProperty
        var tr = NativeMethods.Ioctl(h, NativeMethods.IOCTL_STORAGE_QUERY_PROPERTY, q, 16, out _);
        if (tr != null && tr.Length >= 9) trim = tr[8] != 0;

        return new StorageInfo
        {
            Vendor = vendor, Model = model, Revision = rev, Serial = serial, BusType = bus, Removable = removable, CommandQueueing = cq,
            MaxTransferLength = maxXfer, AlignmentMask = align, PhysicalSectorSize = phys, LogicalSectorSize = logical,
            SeekPenalty = seek, TrimSupported = trim, IsWindowsHost = true
        };
    }

    private static string Str(byte[] d, uint off)
    {
        if (off == 0 || off >= d.Length) return "";
        int end = (int)off;
        while (end < d.Length && d[end] != 0) end++;
        return System.Text.Encoding.ASCII.GetString(d, (int)off, end - (int)off).Trim();
    }

    /// <summary>Some drivers return the serial as byte-swapped hex (ATA identify order). Normalise when it looks like that.</summary>
    private static string DecodeSerial(string s)
    {
        s = s.Trim();
        if (s.Length >= 8 && s.Length % 2 == 0 && s.All(Uri.IsHexDigit))
        {
            try
            {
                var bytes = Convert.FromHexString(s);
                for (int i = 0; i + 1 < bytes.Length; i += 2) (bytes[i], bytes[i + 1]) = (bytes[i + 1], bytes[i]);
                var dec = System.Text.Encoding.ASCII.GetString(bytes).Trim();
                if (dec.Length > 0 && dec.All(c => c >= 0x20 && c < 0x7F)) return dec;
            }
            catch { }
        }
        return s;
    }
}
