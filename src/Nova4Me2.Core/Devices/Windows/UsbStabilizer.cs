using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32;

namespace Nova4Me2.Core.Devices.Windows;

public sealed class UsbStabilityStatus
{
    public bool? SelectiveSuspendDisabledAc { get; set; }
    public bool? SelectiveSuspendDisabledDc { get; set; }
    public bool? DiskIdleTimeoutNever { get; set; }
    public bool? AutomountDisabled { get; set; }
    public int? DiskTimeoutSeconds { get; set; }
    public int UsbStorageDevicesFound { get; set; }
    public int UsbStorageDevicesTuned { get; set; }
    public List<string> Notes { get; } = new();
}

/// <summary>
/// Applies the standard Windows tweaks that stop USB enclosures from being suspended or re-enumerated mid-transfer,
/// and stops Windows from repeatedly trying to mount the RAW volume. Records prior values so it can be reverted.
/// </summary>
[SupportedOSPlatform("windows")]
public static class UsbStabilizer
{
    private const string UsbSubgroup = "2a737441-1930-4402-8d77-b2bebba308a3";
    private const string SelectiveSuspend = "48e6b7a6-50f5-4782-a5d4-53bb50f7e3e4";
    private const string DiskSubgroup = "0012ee47-9041-4b5d-9b77-535fba8b1442";
    private const string DiskIdle = "6738e2c4-e8a5-4a42-b16a-e040e769756e";
    private static string BackupFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nova4Me2", "usb-stability-backup.json");

    private sealed class Backup
    {
        public Dictionary<string, object?> Values { get; set; } = new();
        public DateTime When { get; set; }
    }

    public static UsbStabilityStatus Status()
    {
        var s = new UsbStabilityStatus();
        try
        {
            s.SelectiveSuspendDisabledAc = PowerIndex(UsbSubgroup, SelectiveSuspend, "AC") == 0;
            s.SelectiveSuspendDisabledDc = PowerIndex(UsbSubgroup, SelectiveSuspend, "DC") == 0;
            s.DiskIdleTimeoutNever = PowerIndex(DiskSubgroup, DiskIdle, "AC") == 0;
        }
        catch (Exception ex) { s.Notes.Add("powercfg query failed: " + ex.Message); }
        try
        {
            using var mm = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\mountmgr");
            s.AutomountDisabled = (mm?.GetValue("NoAutoMount") as int?) == 1;
            using var disk = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\disk");
            s.DiskTimeoutSeconds = disk?.GetValue("TimeOutValue") as int?;
        }
        catch (Exception ex) { s.Notes.Add("registry query failed: " + ex.Message); }
        try
        {
            foreach (var key in UsbStorageDeviceKeys())
            {
                s.UsbStorageDevicesFound++;
                using var dp = Registry.LocalMachine.OpenSubKey(key + @"\Device Parameters");
                if (dp != null && (dp.GetValue("EnhancedPowerManagementEnabled") as int?) == 0 && (dp.GetValue("SelectiveSuspendEnabled") as int?) == 0) s.UsbStorageDevicesTuned++;
            }
        }
        catch (Exception ex) { s.Notes.Add("USB enumeration failed: " + ex.Message); }
        return s;
    }

    public static List<string> Apply()
    {
        var log = new List<string>();
        var backup = new Backup { When = DateTime.Now };
        void Save(string k, object? v) { if (!backup.Values.ContainsKey(k)) backup.Values[k] = v; }
        try
        {
            Save("ss_ac", PowerIndex(UsbSubgroup, SelectiveSuspend, "AC"));
            Save("ss_dc", PowerIndex(UsbSubgroup, SelectiveSuspend, "DC"));
            Save("disk_ac", PowerIndex(DiskSubgroup, DiskIdle, "AC"));
            Save("disk_dc", PowerIndex(DiskSubgroup, DiskIdle, "DC"));
            Run("powercfg", $"/SETACVALUEINDEX SCHEME_CURRENT {UsbSubgroup} {SelectiveSuspend} 0");
            Run("powercfg", $"/SETDCVALUEINDEX SCHEME_CURRENT {UsbSubgroup} {SelectiveSuspend} 0");
            Run("powercfg", $"/SETACVALUEINDEX SCHEME_CURRENT {DiskSubgroup} {DiskIdle} 0");
            Run("powercfg", $"/SETDCVALUEINDEX SCHEME_CURRENT {DiskSubgroup} {DiskIdle} 0");
            Run("powercfg", "/SETACTIVE SCHEME_CURRENT");
            log.Add("USB selective suspend disabled and disk idle time-out set to never (current power plan).");
        }
        catch (Exception ex) { log.Add("Power plan changes failed: " + ex.Message); }
        try
        {
            using var mm = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\mountmgr", writable: true);
            Save("NoAutoMount", mm?.GetValue("NoAutoMount"));
            Run("mountvol", "/N");
            log.Add("Windows automount disabled (mountvol /N): Windows stops re-probing the RAW volume on every reconnect. Existing drive letters are unaffected; re-enable later with mountvol /E.");
        }
        catch (Exception ex) { log.Add("Automount change failed: " + ex.Message); }
        try
        {
            using var disk = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\disk", writable: true);
            if (disk != null)
            {
                Save("TimeOutValue", disk.GetValue("TimeOutValue"));
                disk.SetValue("TimeOutValue", 120, RegistryValueKind.DWord);
                log.Add("Disk I/O time-out raised to 120 s (HKLM\\...\\Services\\disk\\TimeOutValue), effective after reboot.");
            }
        }
        catch (Exception ex) { log.Add("Disk time-out change failed: " + ex.Message); }
        int tuned = 0;
        try
        {
            foreach (var key in UsbStorageDeviceKeys())
            {
                using var dp = Registry.LocalMachine.CreateSubKey(key + @"\Device Parameters", writable: true);
                if (dp == null) continue;
                foreach (var name in new[] { "EnhancedPowerManagementEnabled", "AllowIdleIrpInD3", "SelectiveSuspendEnabled", "SelectiveSuspendOn", "DeviceSelectiveSuspended" })
                {
                    Save(key + "|" + name, dp.GetValue(name));
                    dp.SetValue(name, 0, RegistryValueKind.DWord);
                }
                tuned++;
            }
            log.Add($"Power management disabled on {tuned} USB storage device(s) (takes effect when the device is re-plugged).");
        }
        catch (Exception ex) { log.Add("USB device tuning failed: " + ex.Message); }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(BackupFile)!);
            if (!File.Exists(BackupFile)) File.WriteAllText(BackupFile, JsonSerializer.Serialize(backup, new JsonSerializerOptions { WriteIndented = true }));
            log.Add("Previous settings saved to " + BackupFile);
        }
        catch (Exception ex) { log.Add("Could not save backup: " + ex.Message); }
        return log;
    }

    public static List<string> Revert()
    {
        var log = new List<string>();
        if (!File.Exists(BackupFile)) { log.Add("No backup file found; nothing to revert."); return log; }
        Backup b;
        try { b = JsonSerializer.Deserialize<Backup>(File.ReadAllText(BackupFile))!; } catch (Exception ex) { log.Add("Backup unreadable: " + ex.Message); return log; }
        int Int(string k, int def) => b.Values.TryGetValue(k, out var v) && v is JsonElement je && je.ValueKind == JsonValueKind.Number ? je.GetInt32() : def;
        try
        {
            Run("powercfg", $"/SETACVALUEINDEX SCHEME_CURRENT {UsbSubgroup} {SelectiveSuspend} {Int("ss_ac", 1)}");
            Run("powercfg", $"/SETDCVALUEINDEX SCHEME_CURRENT {UsbSubgroup} {SelectiveSuspend} {Int("ss_dc", 1)}");
            Run("powercfg", $"/SETACVALUEINDEX SCHEME_CURRENT {DiskSubgroup} {DiskIdle} {Int("disk_ac", 1200)}");
            Run("powercfg", $"/SETDCVALUEINDEX SCHEME_CURRENT {DiskSubgroup} {DiskIdle} {Int("disk_dc", 600)}");
            Run("powercfg", "/SETACTIVE SCHEME_CURRENT");
            log.Add("Power plan values restored.");
        }
        catch (Exception ex) { log.Add("Power plan restore failed: " + ex.Message); }
        try
        {
            bool wasDisabled = b.Values.TryGetValue("NoAutoMount", out var v) && v is JsonElement j && j.ValueKind == JsonValueKind.Number && j.GetInt32() == 1;
            Run("mountvol", wasDisabled ? "/N" : "/E");
            log.Add(wasDisabled ? "Automount left disabled (it was disabled before)." : "Automount re-enabled (mountvol /E).");
        }
        catch (Exception ex) { log.Add("Automount restore failed: " + ex.Message); }
        try
        {
            using var disk = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\disk", writable: true);
            if (disk != null)
            {
                if (b.Values.TryGetValue("TimeOutValue", out var tv) && tv is JsonElement te && te.ValueKind == JsonValueKind.Number) disk.SetValue("TimeOutValue", te.GetInt32(), RegistryValueKind.DWord);
                else disk.DeleteValue("TimeOutValue", false);
                log.Add("Disk time-out restored.");
            }
        }
        catch (Exception ex) { log.Add("Disk time-out restore failed: " + ex.Message); }
        int n = 0;
        foreach (var (k, v) in b.Values)
        {
            int bar = k.IndexOf('|');
            if (bar < 0) continue;
            try
            {
                using var dp = Registry.LocalMachine.OpenSubKey(k[..bar] + @"\Device Parameters", writable: true);
                if (dp == null) continue;
                string name = k[(bar + 1)..];
                if (v is JsonElement je && je.ValueKind == JsonValueKind.Number) dp.SetValue(name, je.GetInt32(), RegistryValueKind.DWord);
                else dp.DeleteValue(name, false);
                n++;
            }
            catch { }
        }
        log.Add($"Restored {n} USB device parameter(s).");
        try { File.Delete(BackupFile); } catch { }
        return log;
    }

    private static IEnumerable<string> UsbStorageDeviceKeys()
    {
        using var usb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
        if (usb == null) yield break;
        foreach (var vidpid in usb.GetSubKeyNames())
        {
            using var k = usb.OpenSubKey(vidpid);
            if (k == null) continue;
            foreach (var inst in k.GetSubKeyNames())
            {
                using var ik = k.OpenSubKey(inst);
                var svc = ik?.GetValue("Service") as string;
                if (svc != null && (svc.Equals("USBSTOR", StringComparison.OrdinalIgnoreCase) || svc.Equals("UASPStor", StringComparison.OrdinalIgnoreCase)))
                    yield return $@"SYSTEM\CurrentControlSet\Enum\USB\{vidpid}\{inst}";
            }
        }
    }

    private static int? PowerIndex(string subgroup, string setting, string acdc)
    {
        string o = Run("powercfg", $"/QUERY SCHEME_CURRENT {subgroup} {setting}");
        string marker = acdc == "AC" ? "Current AC Power Setting Index:" : "Current DC Power Setting Index:";
        foreach (var line in o.Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith(marker, StringComparison.OrdinalIgnoreCase))
            {
                var hex = t[marker.Length..].Trim();
                if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && int.TryParse(hex[2..], System.Globalization.NumberStyles.HexNumber, null, out int v)) return v;
            }
        }
        return null;
    }

    private static string Run(string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Cannot start {exe}");
        string o = p.StandardOutput.ReadToEnd();
        p.WaitForExit(15000);
        if (p.ExitCode != 0) throw new InvalidOperationException($"{exe} {args} failed: {p.StandardError.ReadToEnd().Trim()}");
        return o;
    }
}
