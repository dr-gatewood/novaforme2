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

    /// <summary>
    /// Try to protect the drive with this identity right now: locate it by serial/model/size with a short budget and set
    /// offline + read-only. Returns the drive number on success, null if it is not present or did not answer in time.
    /// Meant to be called repeatedly (on every arrival event) for a disk that keeps dropping off the bus.
    /// </summary>
    public static int? TryProtectByIdentity(string serial, string model, long length, int budgetMs = 6000)
    {
        int? result = null;
        var done = new ManualResetEventSlim(false);
        var t = new Thread(() =>
        {
            try
            {
                foreach (var d in DriveEnumerator.QuickList(budgetMs: Math.Max(1000, budgetMs / 3)))
                {
                    if (d.OpenError != null) continue;
                    bool match = d.Length == length && (serial.Length > 0 && d.Storage.Serial.Length > 0 ? string.Equals(serial, d.Storage.Serial, StringComparison.OrdinalIgnoreCase) : string.Equals(model, d.Storage.Model, StringComparison.OrdinalIgnoreCase));
                    if (!match) continue;
                    if (d.Attributes.Queried && d.Attributes.Offline && d.Attributes.ReadOnly) { result = d.Number; break; }
                    SetAttributes(d.Number, offline: true, readOnly: true);
                    result = d.Number;
                    break;
                }
            }
            catch (Exception ex) { Log.Warn("protect attempt: " + ex.Message); }
            finally { done.Set(); }
        }) { IsBackground = true, Name = "nova-protect" };
        t.Start();
        return done.Wait(budgetMs) ? result : null;
    }

    /// <summary>Protect by drive number with a time budget (for disks whose identity could not be read yet). Returns the number on success.</summary>
    public static int? TryProtectByNumber(int number, int budgetMs = 6000)
    {
        bool ok = false;
        var done = new ManualResetEventSlim(false);
        var t = new Thread(() => { try { SetAttributes(number, offline: true, readOnly: true); ok = true; } catch (Exception ex) { Log.Warn($"protect PhysicalDrive{number}: {ex.Message}"); } finally { done.Set(); } }) { IsBackground = true, Name = "nova-protect" };
        t.Start();
        return done.Wait(budgetMs) && ok ? number : null;
    }

    public enum SanPolicy { Unknown = 0, OnlineAll = 1, OfflineShared = 2, OfflineAll = 3, OfflineInternal = 4 }

    /// <summary>Windows' policy for newly discovered disks. OfflineAll = every new disk arrives offline (nothing mounts) until brought online by hand.</summary>
    public static SanPolicy GetSanPolicy()
    {
        try
        {
            string o = RunDiskpart("san");
            if (o.Contains("Offline All", StringComparison.OrdinalIgnoreCase)) return SanPolicy.OfflineAll;
            if (o.Contains("Offline Shared", StringComparison.OrdinalIgnoreCase)) return SanPolicy.OfflineShared;
            if (o.Contains("Offline Internal", StringComparison.OrdinalIgnoreCase)) return SanPolicy.OfflineInternal;
            if (o.Contains("Online All", StringComparison.OrdinalIgnoreCase)) return SanPolicy.OnlineAll;
        }
        catch (Exception ex) { Log.Warn("san policy query: " + ex.Message); }
        return SanPolicy.Unknown;
    }

    public static void SetSanPolicy(SanPolicy policy)
    {
        string name = policy switch { SanPolicy.OfflineAll => "OfflineAll", SanPolicy.OfflineShared => "OfflineShared", SanPolicy.OfflineInternal => "OfflineInternal", _ => "OnlineAll" };
        RunDiskpart("san policy=" + name);
        Log.Info("SAN policy set to " + name);
    }

    private static string RunDiskpart(string script)
    {
        string file = Path.Combine(Path.GetTempPath(), $"nova4me2-diskpart-{Guid.NewGuid():N}.txt");
        File.WriteAllText(file, script + Environment.NewLine + "exit" + Environment.NewLine);
        try
        {
            var psi = new ProcessStartInfo("diskpart.exe", $"/s \"{file}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("Cannot start diskpart");
            string o = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(30000)) { try { p.Kill(); } catch { } throw new TimeoutException("diskpart did not finish."); }
            if (p.ExitCode != 0) throw new InvalidOperationException("diskpart failed: " + o.Trim());
            return o;
        }
        finally { try { File.Delete(file); } catch { } }
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
