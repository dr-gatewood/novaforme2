using Fsp;
using Nova4Me2.Core.Recovery;
using Nova4Me2.Core.Util;

namespace Nova4Me2.Mount;

/// <summary>Owns one WinFsp mount of a volume at a drive letter (or directory). Dispose to unmount.</summary>
public sealed class MountSession : IDisposable
{
    private FileSystemHost? _host;
    /// <summary>What the user asked for: "R:" or a directory path.</summary>
    public string MountPoint { get; }
    /// <summary>The mount point WinFsp actually used ("\\.\R:" for a mount-manager drive, "R:" for a per-session drive, or a directory).</summary>
    public string EffectiveMountPoint { get; private set; } = "";
    public NtfsReadOnlyFileSystem FileSystem { get; }
    public bool IsMounted => _host != null;
    /// <summary>True when every program on the machine can see the mount (mount-manager drive or directory); false for a plain drive letter created by an elevated process, which only other elevated programs can see.</summary>
    public bool IsGlobal { get; private set; }
    public bool IsDirectory => MountPoint.Length > 2;
    /// <summary>Human-readable note about visibility, empty when there is nothing to warn about.</summary>
    public string VisibilityNote { get; private set; } = "";

    private MountSession(string mountPoint, NtfsReadOnlyFileSystem fs) { MountPoint = mountPoint; FileSystem = fs; }

    /// <summary>Normalise "r", "R", "R:", "R:\" to "R:"; directory paths are returned trimmed.</summary>
    public static string NormalizeMountPoint(string mountPoint)
    {
        mountPoint = mountPoint.Trim();
        if (mountPoint.StartsWith(@"\\.\", StringComparison.Ordinal) || mountPoint.StartsWith(@"\\?\", StringComparison.Ordinal)) mountPoint = mountPoint[4..];
        if (mountPoint.Length >= 1 && mountPoint.Length <= 3 && char.IsLetter(mountPoint[0]) && (mountPoint.Length == 1 || mountPoint[1] == ':')) return char.ToUpperInvariant(mountPoint[0]) + ":";
        return mountPoint.TrimEnd('\\', '/');
    }

    /// <summary>
    /// Mount points to try, in order. For a drive letter: first the mount-manager form ("\\.\R:"), which Windows registers globally so
    /// Explorer and non-elevated programs see it, then the plain letter as a fallback. Directories are used as given.
    /// </summary>
    public static IReadOnlyList<string> Candidates(string mountPoint)
    {
        string mp = NormalizeMountPoint(mountPoint);
        return mp.Length == 2 ? new[] { @"\\.\" + mp, mp } : new[] { mp };
    }

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
        mountPoint = NormalizeMountPoint(mountPoint);
        if (mountPoint.Length > 2 && Directory.Exists(mountPoint) && Directory.EnumerateFileSystemEntries(mountPoint).Any())
            throw new InvalidOperationException($"{mountPoint} exists and is not empty. WinFsp needs a new or empty folder as a mount point.");
        var fs = new NtfsReadOnlyFileSystem(source) { ShowSystemFiles = showSystemFiles };
        var session = new MountSession(mountPoint, fs);
        int status = 0;
        string? tried = null;
        foreach (var mp in Candidates(mountPoint))
        {
            FileSystemHost host;
            try { host = new FileSystemHost(fs); }
            catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException or BadImageFormatException)
            {
                throw new InvalidOperationException("WinFsp native library could not be loaded (" + ex.GetBaseException().Message + "). Install WinFsp from https://winfsp.dev/rel/ and make sure the 64-bit build is used.", ex);
            }
            status = host.Mount(mp, null, false, 0);
            if (status != 0)
            {
                Log.Info($"WinFsp mount at {mp} failed with NTSTATUS 0x{status:X8}{(mp != mountPoint ? "; trying the next form" : "")}.");
                tried = mp;
                try { host.Dispose(); } catch { }
                continue;
            }
            session._host = host;
            session.EffectiveMountPoint = mp;
            session.IsGlobal = mp.StartsWith(@"\\.\", StringComparison.Ordinal) || session.IsDirectory;
            if (!session.IsGlobal)
                session.VisibilityNote = $"Drive {mountPoint} was created by this elevated program, and Windows keeps a separate drive map for programs running as Administrator: it will not appear in Explorer or in programs started normally. Mount into a folder instead, or open files from an elevated program.";
            Log.Info($"Mounted read-only volume at {host.MountPoint()} ({source.Name}){(session.IsGlobal ? ", visible to all programs" : ", visible to elevated programs only")}.");
            return session;
        }
        throw new InvalidOperationException($"WinFsp refused to mount at {tried ?? mountPoint} (NTSTATUS 0x{status:X8}). Is the drive letter free? Is the folder new or empty? Are you running as Administrator?");
    }

    public void Unmount()
    {
        var h = _host;
        _host = null;
        if (h == null) return;
        try { h.Unmount(); } catch { }
        try { h.Dispose(); } catch { }
        Log.Info($"Unmounted {EffectiveMountPoint}.");
    }

    public void Dispose() => Unmount();
}
