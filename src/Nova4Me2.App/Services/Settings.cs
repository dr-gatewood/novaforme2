using System.IO;
using System.Text.Json;

namespace Nova4Me2.App.Services;

public sealed class Settings
{
    public string Theme { get; set; } = "NovaDark";
    public int ReconnectTimeoutSeconds { get; set; } = 180;
    public int ChunkKiB { get; set; } = 1024;
    public double ThrottleMBps { get; set; } = 0;
    public bool Keepalive { get; set; } = true;
    public string DefaultDestination { get; set; } = "";
    public bool VerifyAfterCopy { get; set; } = false;
    public bool ZeroFillUnreadable { get; set; } = true;
    public bool CopyAlternateStreams { get; set; } = false;
    public bool PreserveTimestamps { get; set; } = true;
    public string CollisionPolicy { get; set; } = "Skip";
    public bool ShowSystemFiles { get; set; } = false;
    public bool ShowDeletedFiles { get; set; } = false;
    public string MountLetter { get; set; } = "";
    public bool TwoPassImaging { get; set; } = true;
    public bool HashImaging { get; set; } = true;
    public bool ShowLogPanel { get; set; } = false;

    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nova4Me2");
    public static string File => Path.Combine(Dir, "settings.json");
    public static string LogFile => Path.Combine(Dir, "nova4me2.log");

    public static Settings Load()
    {
        try { if (System.IO.File.Exists(File)) return JsonSerializer.Deserialize<Settings>(System.IO.File.ReadAllText(File)) ?? new Settings(); }
        catch { }
        return new Settings();
    }

    public void Save()
    {
        try { Directory.CreateDirectory(Dir); System.IO.File.WriteAllText(File, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })); }
        catch { }
    }
}
