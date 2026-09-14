using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Nova4Me2.Core.Util;

namespace Nova4Me2.Core.Devices.Windows;

public sealed class WindowsVolumeInfo
{
    public string VolumeGuidPath { get; init; } = "";
    public string[] MountPoints { get; init; } = Array.Empty<string>();
    public string FileSystem { get; init; } = "";
    public string Label { get; init; } = "";
    public bool IsRaw => string.IsNullOrEmpty(FileSystem);
    public string Display => (MountPoints.Length > 0 ? string.Join(", ", MountPoints) : VolumeGuidPath) + " — " + (IsRaw ? "RAW (Windows cannot read it)" : FileSystem + (Label.Length > 0 ? $" \"{Label}\"" : ""));
}

public sealed class PhysicalDriveInfo
{
    public int Number { get; init; }
    public string DevicePath => $@"\\.\PhysicalDrive{Number}";
    public StorageInfo Storage { get; init; } = new();
    public long Length { get; init; }
    public int SectorSize { get; init; } = 512;
    public bool IsSystemDisk { get; init; }
    public List<WindowsVolumeInfo> Volumes { get; init; } = new();
    public string? OpenError { get; init; }
    public string Model => Storage.Model.Length > 0 ? Storage.Model : $"Physical drive {Number}";
    public override string ToString() => $"[{Number}] {Model} {Format.Bytes(Length)} {Storage.BusTypeName}" + (IsSystemDisk ? " (system)" : "");
}

/// <summary>Enumerates <c>\\.\PhysicalDriveN</c> devices and correlates them with Windows volumes (to spot RAW volumes and the system disk).</summary>
[SupportedOSPlatform("windows")]
public static class DriveEnumerator
{
    public static List<PhysicalDriveInfo> List(int maxDrives = 64)
    {
        var volumesByDisk = MapVolumes();
        int systemDisk = SystemDiskNumber();
        var list = new List<PhysicalDriveInfo>();
        int consecutiveMisses = 0;
        for (int n = 0; n < maxDrives; n++)
        {
            try
            {
                using var d = new WindowsPhysicalDrive($@"\\.\PhysicalDrive{n}");
                list.Add(new PhysicalDriveInfo
                {
                    Number = n, Storage = d.Info, Length = d.Length, SectorSize = d.SectorSize, IsSystemDisk = n == systemDisk,
                    Volumes = volumesByDisk.TryGetValue(n, out var v) ? v : new()
                });
                consecutiveMisses = 0;
            }
            catch (IOException ex)
            {
                if (ex.HResult == NativeMethods.ERROR_ACCESS_DENIED || (ex.HResult & 0xFFFF) == NativeMethods.ERROR_ACCESS_DENIED)
                    list.Add(new PhysicalDriveInfo { Number = n, OpenError = "Access denied (run as Administrator)" });
                else if (++consecutiveMisses > 8) break;
            }
            catch { if (++consecutiveMisses > 8) break; }
        }
        return list;
    }

    public static int SystemDiskNumber()
    {
        try
        {
            var sb = new StringBuilder(260);
            NativeMethods.GetWindowsDirectoryW(sb, 260);
            string root = sb.ToString().Substring(0, 2);
            return DiskNumberOf(@"\\.\" + root);
        }
        catch { return -1; }
    }

    /// <summary>Disk number for a volume path such as <c>\\.\C:</c> or <c>\\?\Volume{...}</c> (no trailing backslash).</summary>
    public static int DiskNumberOf(string volumeDevicePath)
    {
        using var h = NativeMethods.CreateFileW(volumeDevicePath, 0, NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE, IntPtr.Zero, NativeMethods.OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid) return -1;
        var o = NativeMethods.Ioctl(h, NativeMethods.IOCTL_STORAGE_GET_DEVICE_NUMBER, ReadOnlySpan<byte>.Empty, 12, out _);
        if (o == null || o.Length < 8) return -1;
        // STORAGE_DEVICE_NUMBER { DeviceType, DeviceNumber, PartitionNumber }
        return (int)Bin.U32(o, 4);
    }

    public static Dictionary<int, List<WindowsVolumeInfo>> MapVolumes()
    {
        var map = new Dictionary<int, List<WindowsVolumeInfo>>();
        var name = new StringBuilder(512);
        using var find = NativeMethods.FindFirstVolumeW(name, 512);
        if (find.IsInvalid) return map;
        do
        {
            string guidPath = name.ToString(); // \\?\Volume{...}\
            try
            {
                int disk = DiskNumberOf(guidPath.TrimEnd('\\'));
                if (disk < 0) continue;
                var paths = new char[1024];
                string[] mounts = Array.Empty<string>();
                if (NativeMethods.GetVolumePathNamesForVolumeNameW(guidPath, paths, 1024, out uint len))
                    mounts = new string(paths, 0, (int)Math.Max(0, len - 1)).Split('\0', StringSplitOptions.RemoveEmptyEntries);
                var fs = new StringBuilder(64); var label = new StringBuilder(261);
                string fsName = "", lbl = "";
                if (NativeMethods.GetVolumeInformationW(guidPath, label, 261, out _, out _, out _, fs, 64)) { fsName = fs.ToString(); lbl = label.ToString(); }
                if (!map.TryGetValue(disk, out var l)) map[disk] = l = new();
                l.Add(new WindowsVolumeInfo { VolumeGuidPath = guidPath, MountPoints = mounts, FileSystem = fsName, Label = lbl });
            }
            catch { }
        } while (NativeMethods.FindNextVolumeW(find, name, 512));
        return map;
    }

    /// <summary>Find a drive by identity (serial/model/size) after a USB re-enumeration; the drive number may have changed.</summary>
    public static PhysicalDriveInfo? FindByIdentity(DriveIdentity id)
    {
        foreach (var d in List())
        {
            if (d.OpenError != null) continue;
            if (id.Matches(d.Storage.Serial, d.Storage.Model, d.Length)) return d;
        }
        return null;
    }
}
