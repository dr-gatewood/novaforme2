namespace Nova4Me2.Cli;

/// <summary>Tiny argument parser: positionals, --flag, --key value, --key=value.</summary>
public sealed class Args
{
    public List<string> Positional { get; } = new();
    public Dictionary<string, List<string>> Options { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static Args Parse(IEnumerable<string> argv)
    {
        var a = new Args();
        var list = argv.ToList();
        for (int i = 0; i < list.Count; i++)
        {
            string s = list[i];
            if (s.StartsWith("--") && s.Length > 2)
            {
                string key = s[2..], val = "";
                int eq = key.IndexOf('=');
                bool hasVal = false;
                if (eq >= 0) { val = key[(eq + 1)..]; key = key[..eq]; hasVal = true; }
                else if (i + 1 < list.Count && !list[i + 1].StartsWith("--") && !IsFlag(key)) { val = list[++i]; hasVal = true; }
                if (!a.Options.TryGetValue(key, out var l)) a.Options[key] = l = new();
                if (hasVal) l.Add(val);
            }
            else if (s.StartsWith('-') && s.Length == 2 && char.IsLetter(s[1]))
            {
                string key = s[1..];
                if (!a.Options.TryGetValue(key, out var l)) a.Options[key] = l = new();
            }
            else a.Positional.Add(s);
        }
        return a;
    }

    private static readonly HashSet<string> Flags = new(StringComparer.OrdinalIgnoreCase)
    {
        "help", "recursive", "R", "deleted", "all", "mft-scan", "overwrite", "rename", "ads", "no-zero-fill", "verify", "resume", "single-pass", "md5", "yes", "deep", "no-keepalive", "verbose",
        "revert", "status", "system", "no-smart", "json", "quiet", "long", "l", "no-timestamps", "show-system"
    };
    private static bool IsFlag(string key) => Flags.Contains(key);

    public bool Has(string key) => Options.ContainsKey(key);
    public string? Get(string key) => Options.TryGetValue(key, out var l) && l.Count > 0 ? l[^1] : null;
    public List<string> GetAll(string key) => Options.TryGetValue(key, out var l) ? l : new();
    public int GetInt(string key, int def) => int.TryParse(Get(key), out var v) ? v : def;
    public long GetLong(string key, long def) => long.TryParse(Get(key), out var v) ? v : def;
    public double GetDouble(string key, double def) => double.TryParse(Get(key), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : def;
    public string Pos(int i, string? def = null) => i < Positional.Count ? Positional[i] : def ?? throw new UsageException($"Missing argument #{i + 1}.");
}

public sealed class UsageException(string message) : Exception(message);
