using Nova4Me2.Core.Devices.Windows;
using Nova4Me2.Core.Util;

namespace Nova4Me2.Core.Devices;

/// <summary>Opens a "source" given as a physical drive number, a device path, or an image file path.</summary>
public static class DeviceFactory
{
    /// <summary>
    /// Accepts: "0" / "PhysicalDrive0" / "\\.\PhysicalDrive0" (Windows), "\\.\C:" (Windows volume), or a path to an image file.
    /// </summary>
    public static ResilientBlockDevice Open(string source, ResilienceOptions? options = null, bool writable = false)
    {
        options ??= new ResilienceOptions();
        if (OperatingSystem.IsWindows())
        {
            int? num = ParseDriveNumber(source);
            if (num is { } n) return OpenPhysicalDrive(n, options, writable);
            if (source.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase) || source.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
            {
                int t = (int)options.IoTimeout.TotalMilliseconds;
                var d = new WindowsPhysicalDrive(source, writable, t);
                return new ResilientBlockDevice(d, () => new WindowsPhysicalDrive(source, writable, t), options);
            }
        }
        if (!File.Exists(source) && !source.StartsWith("/dev/"))
            throw new FileNotFoundException($"Source '{source}' is neither a drive number, a device path nor an existing image file.", source);
        var f = new FileBlockDevice(source, writable);
        return new ResilientBlockDevice(f, () => new FileBlockDevice(source, writable), new ResilienceOptions
        {
            ReconnectTimeout = options.ReconnectTimeout, MaxChunk = options.MaxChunk, RetriesPerSector = options.RetriesPerSector,
            ZeroFillBadSectors = options.ZeroFillBadSectors, MaxBytesPerSecond = options.MaxBytesPerSecond, Keepalive = null
        });
    }

    public static int? ParseDriveNumber(string s)
    {
        s = s.Trim();
        if (int.TryParse(s, out int n) && n >= 0 && n < 256) return n;
        const string p1 = @"\\.\PhysicalDrive", p2 = "PhysicalDrive";
        if (s.StartsWith(p1, StringComparison.OrdinalIgnoreCase) && int.TryParse(s[p1.Length..], out n)) return n;
        if (s.StartsWith(p2, StringComparison.OrdinalIgnoreCase) && int.TryParse(s[p2.Length..], out n)) return n;
        return null;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static ResilientBlockDevice OpenPhysicalDrive(int number, ResilienceOptions options, bool writable = false)
    {
        int timeout = (int)options.IoTimeout.TotalMilliseconds;
        var d = new WindowsPhysicalDrive($@"\\.\PhysicalDrive{number}", writable, timeout);
        var id = DriveIdentity.Capture(d, d.Info.Serial, d.Info.Model);
        Log.Info($"Opened PhysicalDrive{number}: {d.Description}, S/N {d.Info.Serial}");
        IBlockDevice? Reopen()
        {
            // The drive number can change after a USB re-enumeration; search by identity first.
            var found = DriveEnumerator.FindByIdentity(id);
            if (found != null) return new WindowsPhysicalDrive(found.DevicePath, writable, timeout);
            // Fall back to the original path in case identity queries fail through this bridge.
            try
            {
                var same = new WindowsPhysicalDrive($@"\\.\PhysicalDrive{number}", writable, timeout);
                if (same.Length == id.Length) return same;
                same.Dispose();
            }
            catch { }
            return null;
        }
        return new ResilientBlockDevice(d, Reopen, options, id);
    }
}
