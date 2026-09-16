using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Nova4Me2.Core.Reports;
using Nova4Me2.Core.Util;

namespace Nova4Me2.Core.Devices.Windows;

/// <summary>Outcome of one external command (PowerShell, diskpart, pnputil, chkdsk…).</summary>
public sealed class ShellResult
{
    public string Command { get; init; } = "";
    public string Output { get; init; } = "";
    public string Error { get; init; } = "";
    public int ExitCode { get; init; }
    public bool TimedOut { get; init; }
    public TimeSpan Elapsed { get; init; }
    public bool Ok => ExitCode == 0 && !TimedOut;
    public string Text => (Output + (Error.Length > 0 ? "\n" + Error : "")).Trim();
    public override string ToString() => $"{Command}\n{Text}{(TimedOut ? "\n[timed out]" : ExitCode != 0 ? $"\n[exit code {ExitCode}]" : "")}";
}

/// <summary>Runs Windows command-line tools with a hard timeout so the UI never waits on a stuck process.</summary>
public static class Shell
{
    public static ShellResult Run(string exe, string args, int timeoutMs = 30000, string display = "")
    {
        var sw = Stopwatch.StartNew();
        string cmd = display.Length > 0 ? display : $"{exe} {args}".Trim();
        try
        {
            var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("cannot start " + exe);
            var so = p.StandardOutput.ReadToEndAsync();
            var se = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(true); } catch { }
                return new ShellResult { Command = cmd, Output = so.IsCompletedSuccessfully ? so.Result : "", Error = "The command did not finish within " + timeoutMs / 1000 + " s and was killed.", ExitCode = -1, TimedOut = true, Elapsed = sw.Elapsed };
            }
            return new ShellResult { Command = cmd, Output = so.Result, Error = se.Result, ExitCode = p.ExitCode, Elapsed = sw.Elapsed };
        }
        catch (Exception ex) { return new ShellResult { Command = cmd, Error = ex.Message, ExitCode = -2, Elapsed = sw.Elapsed }; }
    }

    /// <summary>Run a PowerShell script (Windows PowerShell 5.1, no profile). The script is passed base64-encoded so quoting never bites.</summary>
    public static ShellResult PowerShell(string script, int timeoutMs = 45000, string? display = null)
    {
        string full = "$ProgressPreference='SilentlyContinue'; $ErrorActionPreference='Continue'; [Console]::OutputEncoding=[System.Text.Encoding]::UTF8; " + script;
        string enc = Convert.ToBase64String(Encoding.Unicode.GetBytes(full));
        return Run("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {enc}", timeoutMs, display ?? script);
    }

    /// <summary>Run a PowerShell expression and deserialise its JSON output. The expression is wrapped so a single object still comes back as an array.</summary>
    public static (List<T> Items, ShellResult Result) PowerShellJson<T>(string expression, int timeoutMs = 45000)
    {
        var r = PowerShell($"$r = @({expression}); if ($r.Count -eq 0) {{ '[]' }} else {{ ConvertTo-Json -InputObject $r -Depth 4 -Compress }}", timeoutMs, expression);
        if (!r.Ok || r.Output.Trim().Length == 0) return (new List<T>(), r);
        try
        {
            string json = r.Output.Trim();
            int i = json.IndexOf('['); if (i > 0) json = json[i..];
            return (JsonSerializer.Deserialize<List<T>>(json, JsonOpts) ?? new List<T>(), r);
        }
        catch (Exception ex) { return (new List<T>(), new ShellResult { Command = r.Command, Output = r.Output, Error = "JSON parse: " + ex.Message, ExitCode = -3, Elapsed = r.Elapsed }); }
    }

    public static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true, NumberHandling = JsonNumberHandling.AllowReadingFromString, Converters = { new LenientStringConverter(), new LenientBoolConverter(), new LenientLongConverter() } };

    public static ShellResult Diskpart(string script, int timeoutMs = 60000)
    {
        string file = Path.Combine(Path.GetTempPath(), $"nova4me2-diskpart-{Guid.NewGuid():N}.txt");
        File.WriteAllText(file, script.TrimEnd() + Environment.NewLine + "exit" + Environment.NewLine);
        try { return Run("diskpart.exe", $"/s \"{file}\"", timeoutMs, "diskpart\n  " + script.Replace("\n", "\n  ")); }
        finally { try { File.Delete(file); } catch { } }
    }

    private sealed class LenientStringConverter : JsonConverter<string>
    {
        public override string? Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o) => r.TokenType switch
        {
            JsonTokenType.String => r.GetString(),
            JsonTokenType.Number => r.TryGetInt64(out long l) ? l.ToString() : r.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture),
            JsonTokenType.True => "True", JsonTokenType.False => "False", JsonTokenType.Null => null,
            JsonTokenType.StartArray => ReadArray(ref r),
            JsonTokenType.StartObject => ReadObject(ref r),
            _ => null,
        };
        private static string ReadArray(ref Utf8JsonReader r) { using var d = JsonDocument.ParseValue(ref r); return string.Join(", ", d.RootElement.EnumerateArray().Select(e => e.ToString())); }
        private static string ReadObject(ref Utf8JsonReader r) { using var d = JsonDocument.ParseValue(ref r); return d.RootElement.TryGetProperty("value", out var v) ? v.ToString() : d.RootElement.ToString(); }
        public override void Write(Utf8JsonWriter w, string v, JsonSerializerOptions o) => w.WriteStringValue(v);
    }
    private sealed class LenientBoolConverter : JsonConverter<bool?>
    {
        public override bool? Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o) => r.TokenType switch
        {
            JsonTokenType.True => true, JsonTokenType.False => false, JsonTokenType.Null => null,
            JsonTokenType.Number => r.GetDouble() != 0,
            JsonTokenType.String => bool.TryParse(r.GetString(), out var b) ? b : null,
            _ => Skip(ref r),
        };
        private static bool? Skip(ref Utf8JsonReader r) { r.Skip(); return null; }
        public override void Write(Utf8JsonWriter w, bool? v, JsonSerializerOptions o) { if (v is { } b) w.WriteBooleanValue(b); else w.WriteNullValue(); }
    }
    private sealed class LenientLongConverter : JsonConverter<long?>
    {
        public override long? Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o) => r.TokenType switch
        {
            JsonTokenType.Number => r.TryGetInt64(out long l) ? l : (long)r.GetDouble(),
            JsonTokenType.String => long.TryParse(r.GetString(), out var l) ? l : double.TryParse(r.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? (long)d : null,
            JsonTokenType.Null => null,
            _ => Skip(ref r),
        };
        private static long? Skip(ref Utf8JsonReader r) { r.Skip(); return null; }
        public override void Write(Utf8JsonWriter w, long? v, JsonSerializerOptions o) { if (v is { } l) w.WriteNumberValue(l); else w.WriteNullValue(); }
    }
}

// ---------------------------------------------------------------- models (what the cmdlets return)

public sealed class WinPhysicalDisk
{
    public string? DeviceId { get; set; }
    public string? FriendlyName { get; set; }
    public string? SerialNumber { get; set; }
    public string? MediaType { get; set; }
    public string? BusType { get; set; }
    public long? Size { get; set; }
    public string? FirmwareVersion { get; set; }
    public string? OperationalStatus { get; set; }
    public string? HealthStatus { get; set; }
    public int Number => int.TryParse(DeviceId, out int n) ? n : -1;
}

public sealed class WinDisk
{
    public long? Number { get; set; }
    public string? FriendlyName { get; set; }
    public string? SerialNumber { get; set; }
    public string? OperationalStatus { get; set; }
    public string? PartitionStyle { get; set; }
    public bool? IsOffline { get; set; }
    public bool? IsReadOnly { get; set; }
    public bool? IsSystem { get; set; }
    public bool? IsBoot { get; set; }
    public long? Size { get; set; }
    public long? NumberOfPartitions { get; set; }
    public string? OfflineReason { get; set; }
    public string? BusType { get; set; }
    public string? Guid { get; set; }
    public string? Model { get; set; }
}

public sealed class WinVolume
{
    public string? DriveLetter { get; set; }
    public string? FileSystemLabel { get; set; }
    public string? FileSystem { get; set; }
    public string? HealthStatus { get; set; }
    public string? OperationalStatus { get; set; }
    public long? Size { get; set; }
    public long? SizeRemaining { get; set; }
    public string? DriveType { get; set; }
    public string? Path { get; set; }
    public string? UniqueId { get; set; }
    public long? DiskNumber { get; set; }
    public string Letter => string.IsNullOrWhiteSpace(DriveLetter) || DriveLetter == "\0" ? "" : DriveLetter.Trim().TrimEnd(':') + ":";
}

public sealed class WinPartition
{
    public long? DiskNumber { get; set; }
    public long? PartitionNumber { get; set; }
    public string? DriveLetter { get; set; }
    public long? Offset { get; set; }
    public long? Size { get; set; }
    public string? Type { get; set; }
    public string? GptType { get; set; }
    public bool? IsHidden { get; set; }
    public bool? IsOffline { get; set; }
    public bool? IsReadOnly { get; set; }
    public bool? IsSystem { get; set; }
    public bool? IsBoot { get; set; }
    public bool? IsActive { get; set; }
    public string? AccessPaths { get; set; }
}

public sealed class WinPnpDisk
{
    public string? FriendlyName { get; set; }
    public string? Status { get; set; }
    public string? Problem { get; set; }
    public string? ProblemDescription { get; set; }
    public string? InstanceId { get; set; }
    public bool? Present { get; set; }
    public bool IsPhantom => (Problem ?? "").Contains("PHANTOM", StringComparison.OrdinalIgnoreCase) || Present == false;
    public bool HasProblem => !IsPhantom && !string.IsNullOrEmpty(Problem) && !Problem.EndsWith("NONE", StringComparison.OrdinalIgnoreCase) && Problem != "0";
}

public sealed class WinService
{
    public string? Name { get; set; }
    public string? DisplayName { get; set; }
    public string? Status { get; set; }
    public string? StartType { get; set; }
    public bool Running => string.Equals(Status, "Running", StringComparison.OrdinalIgnoreCase) || Status == "4";
}

public sealed class WinEvent
{
    public string? TimeCreated { get; set; }
    public long? Id { get; set; }
    public string? ProviderName { get; set; }
    public string? LevelDisplayName { get; set; }
    public string? Message { get; set; }
    public string? LogName { get; set; }
    public DateTime Time => DateTime.TryParse(TimeCreated, null, System.Globalization.DateTimeStyles.RoundtripKind, out var t) ? t : DateTime.MinValue;
}

public sealed class WinOs
{
    public string? Caption { get; set; }
    public string? Version { get; set; }
    public string? BuildNumber { get; set; }
}

/// <summary>One row of diskpart's "list disk".</summary>
public sealed class DiskpartDisk
{
    public int Number { get; init; }
    public string Status { get; init; } = "";
    public string Size { get; init; } = "";
    public string Free { get; init; } = "";
    public bool Dynamic { get; init; }
    public bool Gpt { get; init; }
    public bool Foreign => Status.Equals("Foreign", StringComparison.OrdinalIgnoreCase);
    public bool Offline => Status.Equals("Offline", StringComparison.OrdinalIgnoreCase);
}

/// <summary>One row of diskpart's "list volume".</summary>
public sealed class DiskpartVolume
{
    public int Number { get; init; }
    public string Letter { get; init; } = "";
    public string Label { get; init; } = "";
    public string Fs { get; init; } = "";
    public string Type { get; init; } = "";
    public string Size { get; init; } = "";
    public string Status { get; init; } = "";
    public string Info { get; init; } = "";
}

/// <summary>A single actionable observation about what Windows sees, with the command that addresses it.</summary>
public sealed class WindowsFinding
{
    public string Severity { get; init; } = "info"; // good | info | warn | error
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public WindowsAction? Action { get; init; }
    public int? DiskNumber { get; init; }
}

public enum WindowsActionKind { None, Rescan, OnlineDisk, OfflineDisk, ClearReadOnly, ImportForeign, AssignLetter, RemoveLetter, RestartServices, StartService, RemovePhantoms, RedetectDevice, EnableAutomount, DisableAutomount, ChkdskScan, RefreshCache }

public sealed record WindowsAction(WindowsActionKind Kind, string Label, string Command, string? Target = null, string? Arg = null);

public sealed class WindowsStorageSnapshot
{
    public DateTime Taken { get; init; } = DateTime.Now;
    public WinOs Os { get; set; } = new();
    public bool AutomountEnabled { get; set; } = true;
    public string SanPolicy { get; set; } = "";
    public List<WinPhysicalDisk> PhysicalDisks { get; } = new();
    public List<WinDisk> Disks { get; } = new();
    public List<WinVolume> Volumes { get; } = new();
    public List<WinPartition> Partitions { get; } = new();
    public List<WinPnpDisk> PnpDisks { get; } = new();
    public List<WinService> Services { get; } = new();
    public List<DiskpartDisk> DiskpartDisks { get; } = new();
    public List<DiskpartVolume> DiskpartVolumes { get; } = new();
    public List<WinEvent> Events { get; } = new();
    public List<WindowsFinding> Findings { get; } = new();
    public List<string> Problems { get; } = new();
    public TimeSpan Elapsed { get; set; }
}

// ---------------------------------------------------------------- the service

/// <summary>
/// Windows' own view of the storage stack — the same data the Get-PhysicalDisk / Get-Disk / Get-Volume / Get-Partition / Get-PnpDevice /
/// Get-Service / Get-WinEvent cmdlets and diskpart's list disk / list volume return — plus the actions those tools offer, each with the exact
/// command shown so the user learns what is being run. Every call runs out of process with a timeout.
/// </summary>
public static class WindowsStorage
{
    public const string StorageServices = "vds,StorSvc,PlugPlay,RpcSs,ShellHWDetection";
    public const string SystemEventProviders = "'disk','partmgr','storahci','stornvme','storport','volmgr','volmgrx','volsnap','Ntfs','Microsoft-Windows-Ntfs','Microsoft-Windows-Kernel-PnP','Microsoft-Windows-StorDiag','UASPStor','USBSTOR'";
    public const string AppEventProviders = "'Virtual Disk Service','VDS Basic Provider','VDS Dynamic Provider','Application Error'";

    public static string PhysicalDisksQuery => "Get-PhysicalDisk | Select-Object DeviceId, FriendlyName, SerialNumber, @{n='MediaType';e={[string]$_.MediaType}}, @{n='BusType';e={[string]$_.BusType}}, Size, FirmwareVersion, @{n='OperationalStatus';e={[string]::Join(', ', @($_.OperationalStatus))}}, @{n='HealthStatus';e={[string]$_.HealthStatus}}";
    public static string DisksQuery => "Get-Disk | Select-Object Number, FriendlyName, SerialNumber, @{n='OperationalStatus';e={[string]::Join(', ', @($_.OperationalStatus))}}, @{n='PartitionStyle';e={[string]$_.PartitionStyle}}, IsOffline, IsReadOnly, IsSystem, IsBoot, Size, NumberOfPartitions, @{n='OfflineReason';e={[string]$_.OfflineReason}}, @{n='BusType';e={[string]$_.BusType}}, @{n='Guid';e={[string]$_.Guid}}, Model";
    public static string VolumesQuery => "Get-Volume | ForEach-Object { $v = $_; $dn = $null; try { $dn = ($v | Get-Partition -ErrorAction SilentlyContinue | Select-Object -First 1).DiskNumber } catch {}; [pscustomobject]@{ DriveLetter=[string]$v.DriveLetter; FileSystemLabel=$v.FileSystemLabel; FileSystem=$v.FileSystem; HealthStatus=[string]$v.HealthStatus; OperationalStatus=[string]$v.OperationalStatus; Size=$v.Size; SizeRemaining=$v.SizeRemaining; DriveType=[string]$v.DriveType; Path=$v.Path; UniqueId=$v.UniqueId; DiskNumber=$dn } }";
    public static string PartitionsQuery => "Get-Partition | Select-Object DiskNumber, PartitionNumber, @{n='DriveLetter';e={[string]$_.DriveLetter}}, Offset, Size, @{n='Type';e={[string]$_.Type}}, @{n='GptType';e={[string]$_.GptType}}, IsHidden, IsOffline, IsReadOnly, IsSystem, IsBoot, IsActive, @{n='AccessPaths';e={[string]::Join(' ', @($_.AccessPaths))}}";
    public static string PnpQuery => "Get-PnpDevice -Class DiskDrive | Select-Object FriendlyName, @{n='Status';e={[string]$_.Status}}, @{n='Problem';e={[string]$_.Problem}}, ProblemDescription, InstanceId, Present";
    public static string ServicesQuery => $"Get-Service {StorageServices} -ErrorAction SilentlyContinue | Select-Object Name, DisplayName, @{{n='Status';e={{[string]$_.Status}}}}, @{{n='StartType';e={{[string]$_.StartType}}}}";
    public static string OsQuery => "Get-CimInstance Win32_OperatingSystem | Select-Object Caption, Version, BuildNumber";
    public static string EventsQuery(double hours, int max = 150) =>
        $"$s=(Get-Date).AddHours(-{hours.ToString(System.Globalization.CultureInfo.InvariantCulture)}); " +
        $"$a = Get-WinEvent -FilterHashtable @{{LogName='System'; ProviderName={SystemEventProviders}; StartTime=$s}} -MaxEvents {max} -ErrorAction SilentlyContinue; " +
        $"$b = Get-WinEvent -FilterHashtable @{{LogName='Application'; ProviderName={AppEventProviders}; StartTime=$s}} -MaxEvents 40 -ErrorAction SilentlyContinue; " +
        "@($a) + @($b) | Where-Object { $_ } | Sort-Object TimeCreated -Descending | Select-Object @{n='TimeCreated';e={$_.TimeCreated.ToString('o')}}, Id, ProviderName, LevelDisplayName, LogName, @{n='Message';e={ if ($_.Message) { $_.Message } else { '(no message text)' } }}";

    public static WindowsStorageSnapshot Snapshot(double eventHours = 24, IProgress<string>? progress = null, CancellationToken ct = default, bool includeEvents = true)
    {
        var sw = Stopwatch.StartNew();
        var s = new WindowsStorageSnapshot();
        if (!OperatingSystem.IsWindows()) { s.Problems.Add("Windows storage tools are only available on Windows."); return s; }
        void Step<T>(string name, string query, List<T> into)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(name + "…");
            var (items, r) = Shell.PowerShellJson<T>(query);
            if (!r.Ok) s.Problems.Add($"{name}: {r.Error.Trim()}".TrimEnd(':', ' '));
            into.AddRange(items);
        }
        Step("Get-PhysicalDisk", PhysicalDisksQuery, s.PhysicalDisks);
        Step("Get-Disk", DisksQuery, s.Disks);
        Step("Get-Volume", VolumesQuery, s.Volumes);
        Step("Get-Partition", PartitionsQuery, s.Partitions);
        Step("Get-PnpDevice -Class DiskDrive", PnpQuery, s.PnpDisks);
        Step("Get-Service", ServicesQuery, s.Services);
        var os = new List<WinOs>(); Step("Get-CimInstance Win32_OperatingSystem", OsQuery, os); s.Os = os.FirstOrDefault() ?? new WinOs();
        progress?.Report("diskpart list disk / list volume…");
        var dp = Shell.Diskpart("list disk\nlist volume");
        if (dp.Ok) { s.DiskpartDisks.AddRange(ParseDiskpartDisks(dp.Output)); s.DiskpartVolumes.AddRange(ParseDiskpartVolumes(dp.Output)); }
        else s.Problems.Add("diskpart: " + dp.Text);
        try { s.AutomountEnabled = DiskControl.IsAutomountEnabled(); } catch { }
        try { s.SanPolicy = DiskControl.GetSanPolicy().ToString(); } catch { s.SanPolicy = "unknown"; }
        if (includeEvents) Step($"Get-WinEvent (last {eventHours:0.#} h)", EventsQuery(eventHours), s.Events);
        s.Findings.AddRange(Diagnose(s));
        s.Elapsed = sw.Elapsed;
        return s;
    }

    // ---- diskpart text parsing ----
    private static readonly Regex DiskRow = new(@"^\s*Disk\s+(\d+)\s+(Online|Offline|Foreign|Missing|No Media|Errors|Invalid|Unavailable|[A-Za-z ]+?)\s{2,}(\S+\s*\S*?)\s{2,}(\S+\s*\S*?)\s*(\*?)\s*(\*?)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    public static List<DiskpartDisk> ParseDiskpartDisks(string output)
    {
        var list = new List<DiskpartDisk>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (!line.TrimStart().StartsWith("Disk ", StringComparison.OrdinalIgnoreCase) || line.Contains("###")) continue;
            // Columns are fixed-width: "  Disk 0    Foreign         931 GB      0 B   *    *"
            var m = Regex.Match(line, @"^\s*Disk\s+(\d+)\s+(.+?)\s{2,}(\d+\s*[KMGT]?B)\s+(\d+\s*[KMGT]?B)\s*(.*)$", RegexOptions.IgnoreCase);
            if (!m.Success) continue;
            string flags = m.Groups[5].Value;
            // Dyn column comes before Gpt; a lone '*' at the far right is Gpt only.
            int dynCol = line.Length >= 60 ? line.IndexOf('*', Math.Min(line.Length - 1, 58)) : -1;
            bool dyn, gpt;
            var stars = flags.Split(' ', StringSplitOptions.RemoveEmptyEntries).Count(t => t == "*");
            if (stars >= 2) { dyn = true; gpt = true; }
            else if (stars == 1) { int starIdx = line.LastIndexOf('*'); int lastSizeEnd = m.Groups[4].Index + m.Groups[4].Length; dyn = starIdx - lastSizeEnd <= 4; gpt = !dyn; }
            else { dyn = false; gpt = false; }
            _ = dynCol;
            list.Add(new DiskpartDisk { Number = int.Parse(m.Groups[1].Value), Status = m.Groups[2].Value.Trim(), Size = m.Groups[3].Value.Trim(), Free = m.Groups[4].Value.Trim(), Dynamic = dyn, Gpt = gpt });
        }
        return list;
    }

    public static List<DiskpartVolume> ParseDiskpartVolumes(string output)
    {
        var list = new List<DiskpartVolume>();
        string? header = null;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Contains("Volume ###")) { header = line; continue; }
            if (header == null || !line.TrimStart().StartsWith("Volume ", StringComparison.OrdinalIgnoreCase)) continue;
            // Use the header's column positions: "  Volume ###  Ltr  Label        Fs     Type        Size     Status     Info"
            int cLtr = header.IndexOf("Ltr"), cLabel = header.IndexOf("Label"), cFs = header.IndexOf("Fs"), cType = header.IndexOf("Type"), cSize = header.IndexOf("Size"), cStatus = header.IndexOf("Status"), cInfo = header.IndexOf("Info");
            string Col(int a, int b) => a < 0 || a >= line.Length ? "" : line.Substring(a, Math.Max(0, Math.Min(b < 0 ? line.Length : b, line.Length) - a)).Trim();
            var m = Regex.Match(line, @"Volume\s+(\d+)");
            if (!m.Success) continue;
            list.Add(new DiskpartVolume { Number = int.Parse(m.Groups[1].Value), Letter = Col(cLtr, cLabel), Label = Col(cLabel, cFs), Fs = Col(cFs, cType), Type = Col(cType, cSize), Size = Col(cSize, cStatus), Status = Col(cStatus, cInfo), Info = Col(cInfo, -1) });
        }
        return list;
    }

    // ---- diagnosis: what is Windows not doing, and which command fixes it ----
    public static List<WindowsFinding> Diagnose(WindowsStorageSnapshot s)
    {
        var f = new List<WindowsFinding>();
        foreach (var svc in s.Services.Where(x => !x.Running && x.Name is "RpcSs" or "PlugPlay" or "StorSvc"))
            f.Add(new WindowsFinding { Severity = "error", Title = $"Service {svc.Name} is {svc.Status}", Detail = $"{svc.DisplayName} must run for Windows to enumerate disks and volumes.", Action = Actions.StartService(svc.Name!) });
        foreach (var pd in s.PhysicalDisks)
        {
            if (pd.Number < 0) continue;
            var d = s.Disks.FirstOrDefault(x => x.Number == pd.Number);
            var dp = s.DiskpartDisks.FirstOrDefault(x => x.Number == pd.Number);
            string name = $"{pd.FriendlyName} (disk {pd.Number})";
            if (dp is { Foreign: true })
                f.Add(new WindowsFinding { Severity = "warn", DiskNumber = pd.Number, Title = $"{name} is a foreign dynamic disk", Detail = "Its LDM database belongs to another Windows installation, so no volume is created until the disk group is imported (data is kept). Alternatively convert it to basic in the Repair view.", Action = Actions.ImportForeign(pd.Number) });
            else if (d == null)
                f.Add(new WindowsFinding { Severity = "error", DiskNumber = pd.Number, Title = $"{name} has no disk object", Detail = "Windows sees the device but the partition manager never built a disk for it, so Disk Management, Get-Disk and This PC cannot show it. Re-detecting the device rebuilds the disk object; a full shutdown does the same.", Action = Actions.RedetectDevice(FindInstanceId(s, pd) ?? "", pd.FriendlyName ?? "") });
            else
            {
                if (d.IsOffline == true)
                    f.Add(new WindowsFinding { Severity = "warn", DiskNumber = pd.Number, Title = $"{name} is offline{(string.IsNullOrEmpty(d.OfflineReason) ? "" : " (" + d.OfflineReason + ")")}", Detail = "Windows will not mount its volumes while offline. Leave it offline if Nova4Me2 is deliberately keeping Windows off a RAW disk; bring it online when Windows should use it again.", Action = Actions.OnlineDisk(pd.Number) });
                if (d.IsReadOnly == true)
                    f.Add(new WindowsFinding { Severity = "info", DiskNumber = pd.Number, Title = $"{name} is read-only", Detail = "The Windows read-only attribute blocks every write, including chkdsk and formatting. Clear it only when you want Windows to write to the disk again.", Action = Actions.ClearReadOnly(pd.Number) });
                if (!string.IsNullOrEmpty(d.OperationalStatus) && !d.OperationalStatus.Contains("Online", StringComparison.OrdinalIgnoreCase) && !d.OperationalStatus.Contains("Offline", StringComparison.OrdinalIgnoreCase))
                    f.Add(new WindowsFinding { Severity = "warn", DiskNumber = pd.Number, Title = $"{name}: {d.OperationalStatus}", Detail = "Windows reports an unusual operational status for this disk." });
            }
            if (!string.IsNullOrEmpty(pd.HealthStatus) && !pd.HealthStatus.Equals("Healthy", StringComparison.OrdinalIgnoreCase))
                f.Add(new WindowsFinding { Severity = "error", DiskNumber = pd.Number, Title = $"{name}: health {pd.HealthStatus}", Detail = "Windows' storage health rollup is not Healthy. Copy data off first; the Health view shows the SMART detail." });
        }
        var raw = s.Volumes.Where(v => string.IsNullOrEmpty(v.FileSystem) && (v.Size ?? 0) > 0 && !string.Equals(v.DriveType, "CD-ROM", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var v in raw)
            f.Add(new WindowsFinding { Severity = "warn", DiskNumber = (int?)v.DiskNumber, Title = $"Volume {(v.Letter.Length > 0 ? v.Letter : v.Path)} is RAW to Windows", Detail = "Windows cannot read the file system. Nova4Me2's Browse view reads NTFS directly; do not format." });
        var unlettered = s.Volumes.Where(v => v.Letter.Length == 0 && !string.IsNullOrEmpty(v.FileSystem) && (v.Size ?? 0) > 200L * 1024 * 1024 && !string.Equals(v.DriveType, "CD-ROM", StringComparison.OrdinalIgnoreCase) && !(v.FileSystem?.StartsWith("FAT", StringComparison.OrdinalIgnoreCase) == true && (v.Size ?? 0) < 600L * 1024 * 1024)).ToList();
        foreach (var v in unlettered)
        {
            var dpv = s.DiskpartVolumes.FirstOrDefault(x => x.Label.Length > 0 && x.Label == (v.FileSystemLabel ?? "") && x.Letter.Length == 0);
            f.Add(new WindowsFinding { Severity = s.AutomountEnabled ? "info" : "warn", DiskNumber = (int?)v.DiskNumber, Title = $"Volume \"{v.FileSystemLabel}\" ({v.FileSystem}, {Format.Bytes(v.Size ?? 0)}) has no drive letter", Detail = s.AutomountEnabled ? "Assign a letter to use it from Explorer." : "Automount is off (Nova4Me2 or mountvol /N turned it off), so Windows no longer assigns letters to new volumes. Assign one here or turn automount back on.", Action = dpv != null ? Actions.AssignLetter(dpv.Number, null) : null });
        }
        if (!s.AutomountEnabled)
            f.Add(new WindowsFinding { Severity = "info", Title = "Windows automount is off", Detail = "New volumes will not get drive letters until it is turned back on (mountvol /E). Keep it off while a flaky enclosure is attached; turn it on when done.", Action = Actions.EnableAutomount() });
        var phantoms = s.PnpDisks.Where(p => p.IsPhantom).ToList();
        if (phantoms.Count > 0)
            f.Add(new WindowsFinding { Severity = "info", Title = $"{phantoms.Count} phantom disk entr{(phantoms.Count == 1 ? "y" : "ies")} in Device Manager", Detail = "Leftovers of drives that were unplugged: " + string.Join("; ", phantoms.Select(p => p.FriendlyName)) + ". Harmless, but removing them keeps re-detection clean.", Action = Actions.RemovePhantoms() });
        foreach (var p in s.PnpDisks.Where(p => p.HasProblem))
            f.Add(new WindowsFinding { Severity = "error", Title = $"{p.FriendlyName}: {p.Problem}", Detail = p.ProblemDescription ?? "Device Manager reports a problem code for this disk.", Action = Actions.RedetectDevice(p.InstanceId ?? "", p.FriendlyName ?? "") });
        if (s.Events.Any(e => e.ProviderName is "disk" or "storahci" or "stornvme" && e.Id is 7 or 11 or 51 or 129 or 153 or 157))
        {
            var bad = s.Events.Where(e => e.ProviderName is "disk" or "storahci" or "stornvme" && e.Id is 7 or 11 or 51 or 129 or 153 or 157).ToList();
            f.Add(new WindowsFinding { Severity = "warn", Title = $"{bad.Count} disk I/O error or reset event{(bad.Count == 1 ? "" : "s")} in the event log", Detail = "IDs 7/11/51 are bad blocks or controller errors, 129 is a reset after a timeout, 153/157 a retried or surprise-removed I/O. Matches a failing drive or an unstable enclosure; the Events list has the details." });
        }
        if (f.Count == 0) f.Add(new WindowsFinding { Severity = "good", Title = "Windows sees every disk and volume normally", Detail = "No offline, foreign, unlettered or RAW volumes, no phantom devices, services running." });
        return f;
    }

    private static string? FindInstanceId(WindowsStorageSnapshot s, WinPhysicalDisk pd)
    {
        string model = (pd.FriendlyName ?? "").Replace(" ", "").ToUpperInvariant();
        return s.PnpDisks.Where(p => !p.IsPhantom).FirstOrDefault(p => (p.FriendlyName ?? "").Replace(" ", "").ToUpperInvariant().Contains(model) || model.Contains((p.FriendlyName ?? "~").Replace(" ", "").ToUpperInvariant()))?.InstanceId;
    }

    // ---- actions ----
    public static class Actions
    {
        public static WindowsAction Rescan() => new(WindowsActionKind.Rescan, "Rescan disks", "diskpart: rescan\nUpdate-HostStorageCache");
        public static WindowsAction RefreshCache() => new(WindowsActionKind.RefreshCache, "Refresh storage cache", "Update-HostStorageCache");
        public static WindowsAction OnlineDisk(int n) => new(WindowsActionKind.OnlineDisk, "Bring online", $"Set-Disk -Number {n} -IsOffline $false", n.ToString());
        public static WindowsAction OfflineDisk(int n) => new(WindowsActionKind.OfflineDisk, "Take offline", $"Set-Disk -Number {n} -IsOffline $true", n.ToString());
        public static WindowsAction ClearReadOnly(int n) => new(WindowsActionKind.ClearReadOnly, "Clear read-only", $"Set-Disk -Number {n} -IsReadOnly $false", n.ToString());
        public static WindowsAction ImportForeign(int n) => new(WindowsActionKind.ImportForeign, "Import foreign disk", $"diskpart: select disk {n} / import", n.ToString());
        public static WindowsAction AssignLetter(int volume, string? letter) => new(WindowsActionKind.AssignLetter, letter == null ? "Assign next free letter" : $"Assign {letter}", $"diskpart: select volume {volume} / assign{(letter != null ? " letter=" + letter.TrimEnd(':') : "")}", volume.ToString(), letter);
        public static WindowsAction RemoveLetter(int volume) => new(WindowsActionKind.RemoveLetter, "Remove letter", $"diskpart: select volume {volume} / remove", volume.ToString());
        public static WindowsAction RestartServices() => new(WindowsActionKind.RestartServices, "Restart storage services", "Restart-Service vds, StorSvc -Force");
        public static WindowsAction StartService(string name) => new(WindowsActionKind.StartService, $"Start {name}", $"Start-Service {name}", name);
        public static WindowsAction RemovePhantoms() => new(WindowsActionKind.RemovePhantoms, "Remove phantom entries", "pnputil /remove-device <id>  (for each phantom disk)");
        public static WindowsAction RedetectDevice(string instanceId, string name) => new(WindowsActionKind.RedetectDevice, "Re-detect device", $"pnputil /remove-device \"{instanceId}\"\npnputil /scan-devices", instanceId, name);
        public static WindowsAction EnableAutomount() => new(WindowsActionKind.EnableAutomount, "Turn automount on", "mountvol /E");
        public static WindowsAction DisableAutomount() => new(WindowsActionKind.DisableAutomount, "Turn automount off", "mountvol /N");
        public static WindowsAction ChkdskScan(string letter) => new(WindowsActionKind.ChkdskScan, $"chkdsk {letter} (read-only)", $"chkdsk {letter}", letter);
    }

    /// <summary>Execute an action. Nothing here writes to disk sectors; these are the Windows-side operations the cmdlets expose.</summary>
    public static ShellResult Execute(WindowsAction a)
    {
        if (!OperatingSystem.IsWindows()) return new ShellResult { Command = a.Command, Error = "Windows only.", ExitCode = -2 };
        switch (a.Kind)
        {
            case WindowsActionKind.Rescan:
            {
                var r1 = Shell.Diskpart("rescan");
                var r2 = Shell.PowerShell("Update-HostStorageCache", 30000);
                return Merge(a, r1, r2);
            }
            case WindowsActionKind.RefreshCache: return Shell.PowerShell("Update-HostStorageCache", 30000);
            case WindowsActionKind.OnlineDisk: return Shell.PowerShell($"Set-Disk -Number {a.Target} -IsOffline $false -ErrorAction Stop; Get-Disk -Number {a.Target} | Format-List Number, FriendlyName, OperationalStatus, IsOffline, IsReadOnly | Out-String", 60000, a.Command);
            case WindowsActionKind.OfflineDisk: return Shell.PowerShell($"Set-Disk -Number {a.Target} -IsOffline $true -ErrorAction Stop; Get-Disk -Number {a.Target} | Format-List Number, FriendlyName, OperationalStatus, IsOffline, IsReadOnly | Out-String", 60000, a.Command);
            case WindowsActionKind.ClearReadOnly: return Shell.PowerShell($"Set-Disk -Number {a.Target} -IsReadOnly $false -ErrorAction Stop; Get-Disk -Number {a.Target} | Format-List Number, FriendlyName, IsOffline, IsReadOnly | Out-String", 60000, a.Command);
            case WindowsActionKind.ImportForeign: return Shell.Diskpart($"select disk {a.Target}\nimport\nlist volume");
            case WindowsActionKind.AssignLetter: return Shell.Diskpart($"select volume {a.Target}\nassign{(a.Arg != null ? " letter=" + a.Arg.TrimEnd(':') : "")}\nlist volume");
            case WindowsActionKind.RemoveLetter: return Shell.Diskpart($"select volume {a.Target}\nremove\nlist volume");
            case WindowsActionKind.RestartServices: return Shell.PowerShell("Restart-Service vds -Force -ErrorAction Continue; Restart-Service StorSvc -Force -ErrorAction Continue; Get-Service vds, StorSvc | Format-Table Name, Status | Out-String", 90000, a.Command);
            case WindowsActionKind.StartService: return Shell.PowerShell($"Start-Service {a.Target} -ErrorAction Stop; Get-Service {a.Target} | Format-Table Name, Status | Out-String", 60000, a.Command);
            case WindowsActionKind.RemovePhantoms:
            {
                var (list, q) = Shell.PowerShellJson<WinPnpDisk>(PnpQuery);
                var sb = new StringBuilder();
                int n = 0, ok = 0;
                foreach (var p in list.Where(p => p.IsPhantom && !string.IsNullOrEmpty(p.InstanceId)))
                {
                    n++;
                    var r = Shell.Run("pnputil.exe", $"/remove-device \"{p.InstanceId}\"", 30000);
                    if (r.Ok) ok++;
                    sb.AppendLine($"{p.FriendlyName}: {(r.Ok ? "removed" : r.Text)}");
                }
                if (n == 0) sb.AppendLine("No phantom disk entries found.");
                return new ShellResult { Command = a.Command, Output = sb.ToString(), ExitCode = n == ok ? 0 : 1 };
            }
            case WindowsActionKind.RedetectDevice:
            {
                if (string.IsNullOrEmpty(a.Target)) return new ShellResult { Command = a.Command, Error = "No device instance id known for this disk; use Device Manager → Uninstall device, then Scan for hardware changes.", ExitCode = 1 };
                var r1 = Shell.Run("pnputil.exe", $"/remove-device \"{a.Target}\"", 60000);
                var r2 = Shell.Run("pnputil.exe", "/scan-devices", 60000);
                return Merge(a, r1, r2);
            }
            case WindowsActionKind.EnableAutomount: return Shell.Run("mountvol.exe", "/E", 15000, "mountvol /E");
            case WindowsActionKind.DisableAutomount: return Shell.Run("mountvol.exe", "/N", 15000, "mountvol /N");
            case WindowsActionKind.ChkdskScan: return Shell.Run("chkdsk.exe", a.Target ?? "", 600000, a.Command);
            default: return new ShellResult { Command = a.Command, Error = "Not an executable action.", ExitCode = 1 };
        }
    }

    private static ShellResult Merge(WindowsAction a, params ShellResult[] rs) => new()
    {
        Command = a.Command, Output = string.Join("\n", rs.Select(r => r.Output.Trim()).Where(s => s.Length > 0)), Error = string.Join("\n", rs.Select(r => r.Error.Trim()).Where(s => s.Length > 0)),
        ExitCode = rs.All(r => r.Ok) ? 0 : rs.First(r => !r.Ok).ExitCode, TimedOut = rs.Any(r => r.TimedOut), Elapsed = TimeSpan.FromTicks(rs.Sum(r => r.Elapsed.Ticks)),
    };

    // ---- report ----
    public static Report BuildReport(WindowsStorageSnapshot s)
    {
        var r = new Report { Title = "Windows storage diagnostics", Subtitle = $"{s.Os.Caption} {s.Os.Version} — {s.Taken:yyyy-MM-dd HH:mm}", Summary = string.Join(" ", s.Findings.Select(f => f.Title + ".")) };
        var sum = r.Add("Summary");
        sum.Rows.Add(("Windows", $"{s.Os.Caption} (build {s.Os.BuildNumber})"));
        sum.Rows.Add(("Automount", s.AutomountEnabled ? "on" : "OFF (mountvol /N)"));
        sum.Rows.Add(("SAN policy", s.SanPolicy));
        sum.Rows.Add(("Services", string.Join(", ", s.Services.Select(x => $"{x.Name} {x.Status}"))));
        foreach (var f in s.Findings) sum.Bullets.Add($"[{f.Severity}] {f.Title} — {f.Detail}{(f.Action != null ? "  → " + f.Action.Command.Replace("\n", "; ") : "")}");
        var pd = r.Add("Get-PhysicalDisk");
        pd.Table.Add(new[] { "#", "Model", "Serial", "Bus", "Media", "Size", "Firmware", "Status", "Health" });
        foreach (var x in s.PhysicalDisks.OrderBy(x => x.Number)) pd.Table.Add(new[] { x.DeviceId ?? "", x.FriendlyName ?? "", x.SerialNumber ?? "", x.BusType ?? "", x.MediaType ?? "", Format.Bytes(x.Size ?? 0), x.FirmwareVersion ?? "", x.OperationalStatus ?? "", x.HealthStatus ?? "" });
        var d = r.Add("Get-Disk");
        d.Table.Add(new[] { "#", "Model", "Status", "Style", "Offline", "Read-only", "Reason", "Partitions", "Size" });
        foreach (var x in s.Disks.OrderBy(x => x.Number)) d.Table.Add(new[] { x.Number?.ToString() ?? "", x.FriendlyName ?? "", x.OperationalStatus ?? "", x.PartitionStyle ?? "", x.IsOffline == true ? "yes" : "no", x.IsReadOnly == true ? "yes" : "no", x.OfflineReason ?? "", x.NumberOfPartitions?.ToString() ?? "", Format.Bytes(x.Size ?? 0) });
        var dp = r.Add("diskpart list disk / list volume");
        dp.Table.Add(new[] { "Disk", "Status", "Size", "Free", "Dyn", "Gpt" });
        foreach (var x in s.DiskpartDisks) dp.Table.Add(new[] { x.Number.ToString(), x.Status, x.Size, x.Free, x.Dynamic ? "*" : "", x.Gpt ? "*" : "" });
        foreach (var x in s.DiskpartVolumes) dp.Bullets.Add($"Volume {x.Number} {x.Letter,-3} {x.Label,-12} {x.Fs,-6} {x.Type,-10} {x.Size,-8} {x.Status,-9} {x.Info}");
        var v = r.Add("Get-Volume");
        v.Table.Add(new[] { "Letter", "Label", "FS", "Type", "Size", "Free", "Health", "Disk" });
        foreach (var x in s.Volumes.OrderBy(x => x.Letter)) v.Table.Add(new[] { x.Letter, x.FileSystemLabel ?? "", x.FileSystem ?? "(RAW)", x.DriveType ?? "", Format.Bytes(x.Size ?? 0), Format.Bytes(x.SizeRemaining ?? 0), x.HealthStatus ?? "", x.DiskNumber?.ToString() ?? "" });
        var p = r.Add("Get-Partition");
        p.Table.Add(new[] { "Disk", "#", "Letter", "Offset", "Size", "Type", "GPT type", "Flags" });
        foreach (var x in s.Partitions.OrderBy(x => x.DiskNumber).ThenBy(x => x.PartitionNumber)) p.Table.Add(new[] { x.DiskNumber?.ToString() ?? "", x.PartitionNumber?.ToString() ?? "", x.DriveLetter ?? "", (x.Offset ?? 0).ToString("N0"), Format.Bytes(x.Size ?? 0), x.Type ?? "", x.GptType ?? "", string.Join(" ", new[] { x.IsSystem == true ? "system" : "", x.IsBoot == true ? "boot" : "", x.IsActive == true ? "active" : "", x.IsHidden == true ? "hidden" : "", x.IsOffline == true ? "offline" : "", x.IsReadOnly == true ? "ro" : "" }.Where(t => t.Length > 0)) });
        var pnp = r.Add("Get-PnpDevice -Class DiskDrive");
        pnp.Table.Add(new[] { "Device", "Status", "Problem", "Instance id" });
        foreach (var x in s.PnpDisks) pnp.Table.Add(new[] { x.FriendlyName ?? "", x.Status ?? "", x.Problem ?? "", x.InstanceId ?? "" });
        var ev = r.Add($"Storage events ({s.Events.Count})");
        ev.Table.Add(new[] { "Time", "Source", "Id", "Level", "Message" });
        foreach (var x in s.Events.Take(200)) ev.Table.Add(new[] { x.Time.ToString("yyyy-MM-dd HH:mm:ss"), x.ProviderName ?? "", x.Id?.ToString() ?? "", x.LevelDisplayName ?? "", (x.Message ?? "").Replace("\r", "").Replace("\n", " ") });
        if (s.Problems.Count > 0) { var pr = r.Add("Queries that failed"); foreach (var x in s.Problems) pr.Bullets.Add(x); }
        return r;
    }
}
