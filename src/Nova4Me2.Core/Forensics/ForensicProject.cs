using System.Text.Json;
using Nova4Me2.Core.Util;

namespace Nova4Me2.Core.Forensics;

/// <summary>A named folder that receives everything extracted during an analysis session.</summary>
public sealed class ForensicProject
{
    public string Name { get; init; } = "";
    public string Root { get; init; } = "";
    public string Source { get; set; } = "";
    public DateTime Created { get; init; } = DateTime.Now;
    public List<string> Log { get; } = new();

    public string AdsDir => Sub("ads");
    public string CarvedDir => Sub("carved");
    public string StegoDir => Sub("stego");
    public string TskDir => Sub("tsk");
    public string ReportsDir => Sub("reports");

    private string Sub(string n) { string p = Path.Combine(Root, n); Directory.CreateDirectory(p); return p; }

    /// <summary>Base folder for projects: "Projects" next to the executable when writable, else the per-user app data folder.</summary>
    public static string DefaultBaseDir
    {
        get
        {
            string app = Path.Combine(AppContext.BaseDirectory, "Projects");
            try
            {
                Directory.CreateDirectory(app);
                string probe = Path.Combine(app, ".write-test");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return app;
            }
            catch { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nova4Me2", "Projects"); }
        }
    }

    public static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var s = new string(name.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray()).Trim();
        return s.Length == 0 ? $"Project-{DateTime.Now:yyyyMMdd-HHmm}" : s;
    }

    public static ForensicProject Create(string name, string source, string? baseDir = null)
    {
        baseDir ??= DefaultBaseDir;
        string root = Path.Combine(baseDir, SafeName(name));
        Directory.CreateDirectory(root);
        var p = new ForensicProject { Name = SafeName(name), Root = root, Source = source };
        p.Save();
        Util.Log.Info($"Forensic project '{p.Name}' at {root}");
        return p;
    }

    public static ForensicProject? Open(string root)
    {
        string meta = Path.Combine(root, "project.json");
        if (!File.Exists(meta)) return null;
        try
        {
            var d = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(meta)) ?? new();
            return new ForensicProject { Name = d.GetValueOrDefault("name", Path.GetFileName(root)), Root = root, Source = d.GetValueOrDefault("source", ""), Created = DateTime.TryParse(d.GetValueOrDefault("created"), out var c) ? c : DateTime.Now };
        }
        catch { return null; }
    }

    public static List<ForensicProject> List(string? baseDir = null)
    {
        baseDir ??= DefaultBaseDir;
        if (!Directory.Exists(baseDir)) return new();
        return Directory.GetDirectories(baseDir).Select(Open).Where(p => p != null).OrderByDescending(p => p!.Created).ToList()!;
    }

    public void Save()
    {
        File.WriteAllText(Path.Combine(Root, "project.json"), JsonSerializer.Serialize(new Dictionary<string, string> { ["name"] = Name, ["source"] = Source, ["created"] = Created.ToString("o") }, new JsonSerializerOptions { WriteIndented = true }));
    }

    public void Note(string line)
    {
        Log.Add($"{DateTime.Now:HH:mm:ss} {line}");
        try { File.AppendAllText(Path.Combine(Root, "activity.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}"); } catch { }
    }

    /// <summary>A unique, filesystem-safe target path inside a project sub-folder.</summary>
    public static string UniquePath(string dir, string fileName)
    {
        string safe = Recovery.Extractor.SafeName(fileName);
        string p = Path.Combine(dir, safe);
        if (!File.Exists(p)) return p;
        string stem = Path.GetFileNameWithoutExtension(safe), ext = Path.GetExtension(safe);
        for (int i = 1; ; i++) { p = Path.Combine(dir, $"{stem} ({i}){ext}"); if (!File.Exists(p)) return p; }
    }
}
