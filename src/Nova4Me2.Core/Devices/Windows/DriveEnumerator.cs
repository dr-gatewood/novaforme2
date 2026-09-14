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
    public bool ProbeTimedOut { get; init; }
    public bool IsRaw => string.IsNullOrEmpty(FileSystem) && !ProbeTimedOut;
    public string Display => (MountPoints.Length > 0 ? string.Join(", ", MountPoints) : VolumeGuidPath) + " — " + (ProbeTimedOut ? "not responding (Windows is stuck probing it)" : IsRaw ? "RAW (Windows cannot read it)" : FileSystem + (Label.Length > 0 ? $" \"{Label}\"" : ""));
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
    public DiskAttributes Attributes { get; init; } = new();
    public bool ProbeTimedOut { get; init; }
    public string Model => Storage.Model.Length > 0 ? Storage.Model : $"Physical drive {Number}";
    public override string ToString() => $"[{Number}] {Model} {Format.Bytes(Length)} {Storage.BusTypeName}" + (IsSystemDisk ? " (system)" : "");
}

/// <summary>Enumerates <c>\\.\PhysicalDriveN</c> devices and correlates them with Windows volumes (to spot RAW volumes and the system disk).</summary>
[SupportedOSPlatform("windows")]
public static class DriveEnumerator
{
    /// <summary>Time budget for probing one drive / one volume. A disk that Windows is busy (mis)mounting is reported as "not responding" instead of stalling the list.</summary>
    public static int ProbeTimeoutMs { get; set; } = 8000;

    public static List<PhysicalDriveInfo> List(int maxDrives = 64)
    {
        var volumesByDisk = WithTimeout(MapVolumes, ProbeTimeoutMs * 2) ?? new Dictionary<int, List<WindowsVolumeInfo>>();
        int systemDisk = WithTimeout(SystemDiskNumber, ProbeTimeoutMs) is { } sd ? sd : -1;
        var list = new List<PhysicalDriveInfo>();
        int consecutiveMisses = 0;
        for (int n = 0; n < maxDrives; n++)
        {
            int num = n;
            var probed = WithTimeout(() => Probe(num, systemDisk, volumesByDisk), ProbeTimeoutMs);
            if (probed == null)
            {
                list.Add(new PhysicalDriveInfo { Number = n, OpenError = "Not responding (Windows may be trying to mount it; take it offline or re-plug it)", ProbeTimedOut = true, Volumes = volumesByDisk.TryGetValue(n, out var v0) ? v0 : new() });
                consecutiveMisses = 0;
                continue;
            }
            if (probed.OpenError == "missing") { if (++consecutiveMisses > 8) break; continue; }
            list.Add(probed);
            consecutiveMisses = 0;
        }
        return list;
    }

    private static PhysicalDriveInfo Probe(int n, int systemDisk, Dictionary<int, List<WindowsVolumeInfo>> volumesByDisk)
    {
        try
        {
            using var d = new WindowsPhysicalDrive($@"\\.\PhysicalDrive{n}", ioTimeoutMs: ProbeTimeoutMs);
            return new PhysicalDriveInfo
            {
                Number = n, Storage = d.Info, Length = d.Length, SectorSize = d.SectorSize, IsSystemDisk = n == systemDisk,
                Volumes = volumesByDisk.TryGetValue(n, out var v) ? v : new(), Attributes = GetAttributesViaHandle(d)
            };
        }
        catch (IOException ex)
        {
            if (ex.HResult == NativeMethods.ERROR_ACCESS_DENIED || (ex.HResult & 0xFFFF) == NativeMethods.ERROR_ACCESS_DENIED)
                return new PhysicalDriveInfo { Number = n, OpenError = "Access denied (run as Administrator)" };
            return new PhysicalDriveInfo { Number = n, OpenError = "missing" };
        }
        catch { return new PhysicalDriveInfo { Number = n, OpenError = "missing" }; }
    }

    private static DiskAttributes GetAttributesViaHandle(WindowsPhysicalDrive d)
    {
        var o = NativeMethods.Ioctl(d.Handle, NativeMethods.IOCTL_DISK_GET_DISK_ATTRIBUTES_EX, ReadOnlySpan<byte>.Empty, 16, out _, 5000);
        if (o == null || o.Length < 16) return new DiskAttributes();
        ulong a = Bin.U64(o, 8);
        return new DiskAttributes { Offline = (a & 0x1) != 0, ReadOnly = (a & 0x2) != 0, Queried = true };
    }

    /// <summary>Run a probe on its own background thread and give up (returning null) after the time-out; a stuck thread is abandoned, never awaited.</summary>
    private static T? WithTimeout<T>(Func<T> f, int timeoutMs) where T : class
    {
        T? result = null;
        var done = new ManualResetEventSlim(false);
        var t = new Thread(() => { try { result = f(); } catch { } finally { done.Set(); } }) { IsBackground = true, Name = "nova-probe" };
        t.Start();
        return done.Wait(timeoutMs) ? result : null;
    }

    private static T? WithTimeout<T>(Func<T> f, int timeoutMs, T? _ = null) where T : struct
    {
        T? result = null;
        var done = new ManualResetEventSlim(false);
        var t = new Thread(() => { try { result = f(); } catch { } finally { done.Set(); } }) { IsBackground = true, Name = "nova-probe" };
        t.Start();
        return done.Wait(timeoutMs) ? result : null;
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
                // GetVolumeInformation makes Windows try to mount the volume; on a RAW volume of a sick drive that can take minutes.
                // Run it on a throwaway thread with a short budget and report "unknown" instead of waiting.
                var info = WithTimeout(() =>
                {
                    var fs = new StringBuilder(64); var label = new StringBuilder(261);
                    return NativeMethods.GetVolumeInformationW(guidPath, label, 261, out _, out _, out _, fs, 64) ? new[] { fs.ToString(), label.ToString() } : new[] { "", "" };
                }, 3000);
                string fsName = info?[0] ?? "", lbl = info?[1] ?? "";
                if (!map.TryGetValue(disk, out var l)) map[disk] = l = new();
                l.Add(new WindowsVolumeInfo { VolumeGuidPath = guidPath, MountPoints = mounts, FileSystem = fsName, Label = lbl, ProbeTimedOut = info == null });
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
