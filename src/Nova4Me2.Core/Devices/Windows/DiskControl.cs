using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Nova4Me2.Core.Util;

namespace Nova4Me2.Core.Devices.Windows;

public sealed class DiskAttributes
{
    public bool Offline { get; init; }
    public bool ReadOnly { get; init; }
    public bool Queried { get; init; }
}

/// <summary>
/// Keeps Windows away from a drive under recovery: take the disk offline (the mount manager then ignores it entirely while
/// raw \\.\PhysicalDriveN reads keep working), mark it read-only, and switch automount off. All reversible.
/// </summary>
[SupportedOSPlatform("windows")]
public static class DiskControl
{
    private const ulong DISK_ATTRIBUTE_OFFLINE = 0x1, DISK_ATTRIBUTE_READ_ONLY = 0x2;

    public static DiskAttributes GetAttributes(int driveNumber)
    {
        try
        {
            using var h = NativeMethods.CreateFileW($@"\\.\PhysicalDrive{driveNumber}", 0, NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE, IntPtr.Zero, NativeMethods.OPEN_EXISTING, NativeMethods.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (h.IsInvalid) return new DiskAttributes();
            var o = NativeMethods.Ioctl(h, NativeMethods.IOCTL_DISK_GET_DISK_ATTRIBUTES_EX, ReadOnlySpan<byte>.Empty, 16, out _, 5000);
            if (o == null || o.Length < 16) return new DiskAttributes();
            ulong a = Bin.U64(o, 8);
            return new DiskAttributes { Offline = (a & DISK_ATTRIBUTE_OFFLINE) != 0, ReadOnly = (a & DISK_ATTRIBUTE_READ_ONLY) != 0, Queried = true };
        }
        catch { return new DiskAttributes(); }
    }

    /// <summary>Set or clear the OFFLINE and READ_ONLY disk attributes (persisted across re-plugs/reboots so a flaky link does not undo it).</summary>
    public static void SetAttributes(int driveNumber, bool offline, bool readOnly, bool persist = true)
    {
        using var h = NativeMethods.CreateFileW($@"\\.\PhysicalDrive{driveNumber}", NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE, NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE, IntPtr.Zero, NativeMethods.OPEN_EXISTING, NativeMethods.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        if (h.IsInvalid) throw new IOException($"Cannot open PhysicalDrive{driveNumber} for attribute change: {NativeMethods.ErrorText(System.Runtime.InteropServices.Marshal.GetLastWin32Error())}");
        // SET_DISK_ATTRIBUTES { ULONG Version; BOOLEAN Persist; BOOLEAN Reserved1[3]; ULONGLONG Attributes; ULONGLONG AttributesMask; ULONG Reserved2[4]; } = 40 bytes
        var q = new byte[40];
        Bin.PutU32(q, 0, 40);
        q[4] = (byte)(persist ? 1 : 0);
        ulong attrs = (offline ? DISK_ATTRIBUTE_OFFLINE : 0) | (readOnly ? DISK_ATTRIBUTE_READ_ONLY : 0);
        Bin.PutU64(q, 8, attrs);
        Bin.PutU64(q, 16, DISK_ATTRIBUTE_OFFLINE | DISK_ATTRIBUTE_READ_ONLY);
        var o = NativeMethods.Ioctl(h, NativeMethods.IOCTL_DISK_SET_DISK_ATTRIBUTES, q, 0, out int err, 10000);
        if (o == null) throw new IOException($"IOCTL_DISK_SET_DISK_ATTRIBUTES failed: {NativeMethods.ErrorText(err)}", err);
        // Ask the partition manager to re-read so the change takes effect immediately.
        NativeMethods.Ioctl(h, NativeMethods.IOCTL_DISK_UPDATE_PROPERTIES, ReadOnlySpan<byte>.Empty, 0, out _, 5000);
        Log.Info($"PhysicalDrive{driveNumber}: offline={offline} read-only={readOnly} (persist={persist})");
    }

    public static bool IsAutomountEnabled()
    {
        try
        {
            using var mm = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\mountmgr");
            return (mm?.GetValue("NoAutoMount") as int?) != 1;
        }
        catch { return true; }
    }

    public static void SetAutomount(bool enabled)
    {
        var psi = new ProcessStartInfo("mountvol", enabled ? "/E" : "/N") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Cannot start mountvol");
        p.WaitForExit(15000);
        if (p.ExitCode != 0) throw new InvalidOperationException("mountvol failed: " + p.StandardError.ReadToEnd());
        Log.Info("Windows automount " + (enabled ? "enabled" : "disabled"));
    }
}
