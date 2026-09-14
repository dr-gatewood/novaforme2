using Fsp;
using Nova4Me2.Core.Recovery;
using Nova4Me2.Core.Util;

namespace Nova4Me2.Mount;

/// <summary>Owns one WinFsp mount of a volume at a drive letter (or directory). Dispose to unmount.</summary>
public sealed class MountSession : IDisposable
{
    private FileSystemHost? _host;
    public string MountPoint { get; }
    public NtfsReadOnlyFileSystem FileSystem { get; }
    public bool IsMounted => _host != null;

    private MountSession(string mountPoint, NtfsReadOnlyFileSystem fs) { MountPoint = mountPoint; FileSystem = fs; }

    public static bool IsWinFspInstalled(out string detail)
    {
        detail = "";
        if (!OperatingSystem.IsWindows()) { detail = "WinFsp is Windows-only."; return false; }
        try
        {
            foreach (var key in new[] { @"SOFTWARE\WOW6432Node\WinFsp", @"SOFTWARE\WinFsp" })
            {
                using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(key);
                if (k?.GetValue("InstallDir") is string dir && Directory.Exists(dir)) { detail = dir; return true; }
            }
            string pf = Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? Environment.GetEnvironmentVariable("ProgramFiles") ?? "";
            string dll = Path.Combine(pf, "WinFsp", "bin", "winfsp-x64.dll");
            if (File.Exists(dll)) { detail = Path.GetDirectoryName(dll)!; return true; }
        }
        catch (Exception ex) { detail = ex.Message; }
        detail = "WinFsp not found. Install it from https://winfsp.dev/rel/ (free, MIT-style licence), then try again.";
        return false;
    }

    /// <summary>Mount at e.g. "R:" (drive letter) or a path to an empty directory.</summary>
    public static MountSession Mount(IDirectorySource source, string mountPoint, bool showSystemFiles = false)
    {
        if (!IsWinFspInstalled(out var detail)) throw new InvalidOperationException(detail);
        mountPoint = mountPoint.Trim();
        if (mountPoint.Length == 1 && char.IsLetter(mountPoint[0])) mountPoint += ":";
        var fs = new NtfsReadOnlyFileSystem(source) { ShowSystemFiles = showSystemFiles };
        var session = new MountSession(mountPoint, fs);
        FileSystemHost host;
        try { host = new FileSystemHost(fs); }
        catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException or BadImageFormatException)
        {
            throw new InvalidOperationException("WinFsp native library could not be loaded (" + ex.GetBaseException().Message + "). Install WinFsp from https://winfsp.dev/rel/ and make sure the 64-bit build is used.", ex);
        }
        int status = host.Mount(mountPoint, null, false, 0);
        if (status != 0)
        {
            host.Dispose();
            throw new InvalidOperationException($"WinFsp refused to mount at {mountPoint} (NTSTATUS 0x{status:X8}). Is the drive letter free? Are you running as Administrator?");
        }
        session._host = host;
        Log.Info($"Mounted read-only volume at {host.MountPoint()} ({source.Name}).");
        return session;
    }

    public void Unmount()
    {
        var h = _host;
        _host = null;
        if (h == null) return;
        try { h.Unmount(); } catch { }
        try { h.Dispose(); } catch { }
        Log.Info($"Unmounted {MountPoint}.");
    }

    public void Dispose() => Unmount();
}
