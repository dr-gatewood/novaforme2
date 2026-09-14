using System.Diagnostics;

namespace Nova4Me2.Tests;

/// <summary>Locates (or on Linux, generates) the NTFS test images produced by scripts/make-test-image.sh.</summary>
public static class TestImages
{
    private static readonly object _lock = new();
    private static string? _dir;

    public static string? Dir
    {
        get
        {
            lock (_lock)
            {
                if (_dir != null) return _dir;
                string dir = Environment.GetEnvironmentVariable("NOVA_TEST_IMAGES") ?? Path.Combine(Path.GetTempPath(), "nova4me2-testimages");
                if (File.Exists(Path.Combine(dir, "plain.img")) && File.Exists(Path.Combine(dir, "manifest.txt"))) return _dir = dir;
                if (!OperatingSystem.IsLinux()) return null;
                string script = FindScript();
                if (script.Length == 0) return null;
                var psi = new ProcessStartInfo("bash", $"\"{script}\" \"{dir}\"") { RedirectStandardOutput = true, RedirectStandardError = true };
                using var p = Process.Start(psi)!;
                p.WaitForExit();
                return File.Exists(Path.Combine(dir, "plain.img")) ? _dir = dir : null;
            }
        }
    }

    private static string FindScript()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null)
        {
            string s = Path.Combine(d.FullName, "scripts", "make-test-image.sh");
            if (File.Exists(s)) return s;
            d = d.Parent;
        }
        return "";
    }

    public static string Plain => Path.Combine(Dir!, "plain.img");
    public static string DamagedBoot => Path.Combine(Dir!, "damaged-boot.img");
    public static string Gpt => Path.Combine(Dir!, "gpt.img");
    public static string DamagedGpt => Path.Combine(Dir!, "damaged-gpt.img");

    public static Dictionary<string, string> Manifest()
    {
        var m = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadAllLines(Path.Combine(Dir!, "manifest.txt")))
        {
            int sp = line.IndexOf("  ", StringComparison.Ordinal);
            if (sp < 0) continue;
            string hash = line[..sp], path = line[(sp + 2)..];
            if (path.StartsWith("./")) path = path[2..];
            m[path.Replace('/', '\\')] = hash;
        }
        return m;
    }
}
