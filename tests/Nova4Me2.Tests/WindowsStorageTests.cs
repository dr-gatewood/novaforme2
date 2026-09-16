using System.Text.Json;
using Nova4Me2.Core.Devices.Windows;
using Xunit;

namespace Nova4Me2.Tests;

public class WindowsStorageTests
{
    private const string ListDisk = @"
Microsoft DiskPart version 10.0.19041.3636

  Disk ###  Status         Size     Free     Dyn  Gpt
  --------  -------------  -------  -------  ---  ---
  Disk 0    Foreign         931 GB      0 B   *    *
  Disk 1    Online          931 GB      0 B        *
  Disk 2    Online          465 GB  1024 KB        *
  Disk 3    Offline         232 GB      0 B
  Disk 4    Online          111 GB      0 B   *
";
    private const string ListVolume = @"
  Volume ###  Ltr  Label        Fs     Type        Size     Status     Info
  ----------  ---  -----------  -----  ----------  -------  ---------  --------
  Volume 0     C                NTFS   Partition    931 GB  Healthy    Boot
  Volume 1                      FAT32  Partition    100 MB  Healthy    System
  Volume 2     D                NTFS   Partition    465 GB  Healthy
  Volume 3                      RAW    Partition    100 MB  Healthy    Hidden
  Volume 5     T   Apps         NTFS   Simple       931 GB  Healthy
";

    [Fact]
    public void ParseDiskpartDisks_ReadsStatusDynamicAndGptColumns()
    {
        var d = WindowsStorage.ParseDiskpartDisks(ListDisk);
        Assert.Equal(5, d.Count);
        Assert.True(d[0].Foreign); Assert.True(d[0].Dynamic); Assert.True(d[0].Gpt); Assert.Equal("931 GB", d[0].Size);
        Assert.Equal("Online", d[1].Status); Assert.False(d[1].Dynamic); Assert.True(d[1].Gpt);
        Assert.Equal("1024 KB", d[2].Free);
        Assert.True(d[3].Offline); Assert.False(d[3].Dynamic); Assert.False(d[3].Gpt);
        Assert.True(d[4].Dynamic); Assert.False(d[4].Gpt);
    }

    [Fact]
    public void ParseDiskpartVolumes_UsesHeaderColumns()
    {
        var v = WindowsStorage.ParseDiskpartVolumes(ListVolume);
        Assert.Equal(5, v.Count);
        Assert.Equal("C", v[0].Letter); Assert.Equal("NTFS", v[0].Fs); Assert.Equal("Boot", v[0].Info); Assert.Equal("Partition", v[0].Type);
        Assert.Equal("", v[1].Letter); Assert.Equal("FAT32", v[1].Fs); Assert.Equal("System", v[1].Info);
        Assert.Equal("Hidden", v[3].Info); Assert.Equal("RAW", v[3].Fs);
        var apps = v[4]; Assert.Equal(5, apps.Number); Assert.Equal("T", apps.Letter); Assert.Equal("Apps", apps.Label); Assert.Equal("Simple", apps.Type); Assert.Equal("931 GB", apps.Size); Assert.Equal("Healthy", apps.Status);
    }

    [Fact]
    public void Json_DeserialisesCmdletShapesLeniently()
    {
        // PowerShell serialises enums as numbers or strings depending on the type data, bools as true/false, sizes as numbers.
        string json = "[{\"DeviceId\":\"0\",\"FriendlyName\":\"Samsung SSD 860 EVO 1TB\",\"SerialNumber\":\"S5B3\",\"MediaType\":\"SSD\",\"BusType\":11,\"Size\":1000204886016,\"FirmwareVersion\":\"RVT04B6Q\",\"OperationalStatus\":\"OK\",\"HealthStatus\":\"Healthy\"}]";
        var pd = JsonSerializer.Deserialize<List<WinPhysicalDisk>>(json, Shell.JsonOpts)!;
        Assert.Single(pd);
        Assert.Equal(0, pd[0].Number); Assert.Equal("11", pd[0].BusType); Assert.Equal(1000204886016, pd[0].Size);
        string disk = "[{\"Number\":1,\"FriendlyName\":\"SPCC\",\"IsOffline\":false,\"IsReadOnly\":\"False\",\"Size\":\"1000204886016\",\"OfflineReason\":null,\"PartitionStyle\":\"GPT\"}]";
        var d = JsonSerializer.Deserialize<List<WinDisk>>(disk, Shell.JsonOpts)!;
        Assert.Equal(1, d[0].Number); Assert.False(d[0].IsOffline); Assert.False(d[0].IsReadOnly); Assert.Equal(1000204886016, d[0].Size); Assert.Null(d[0].OfflineReason);
        string ev = "[{\"TimeCreated\":\"2026-09-14T02:46:04.0000000-07:00\",\"Id\":153,\"ProviderName\":\"disk\",\"LevelDisplayName\":\"Warning\",\"Message\":\"The IO operation at logical block address 0x10 for Disk 2 was retried.\"}]";
        var e = JsonSerializer.Deserialize<List<WinEvent>>(ev, Shell.JsonOpts)!;
        Assert.Equal(153, e[0].Id); Assert.Equal(2026, e[0].Time.Year);
    }

    [Fact]
    public void Diagnose_FlagsForeignDisk_MissingDiskObject_UnletteredVolume_AutomountOff_Phantoms()
    {
        var s = new WindowsStorageSnapshot { AutomountEnabled = false };
        s.PhysicalDisks.Add(new WinPhysicalDisk { DeviceId = "0", FriendlyName = "Samsung SSD 860 EVO 1TB", HealthStatus = "Healthy", Size = 1000204886016 });
        s.PhysicalDisks.Add(new WinPhysicalDisk { DeviceId = "1", FriendlyName = "SPCC M.2 PCIe SSD", HealthStatus = "Healthy", Size = 1000204886016 });
        s.PhysicalDisks.Add(new WinPhysicalDisk { DeviceId = "3", FriendlyName = "Ghost Drive", HealthStatus = "Healthy", Size = 1 });
        s.Disks.Add(new WinDisk { Number = 1, IsOffline = false, IsReadOnly = false, OperationalStatus = "Online", IsSystem = true });
        s.DiskpartDisks.AddRange(WindowsStorage.ParseDiskpartDisks(ListDisk));
        s.DiskpartVolumes.AddRange(WindowsStorage.ParseDiskpartVolumes("  Volume ###  Ltr  Label        Fs     Type        Size     Status     Info\n  Volume 7         Data         NTFS   Partition    500 GB  Healthy\n"));
        s.Volumes.Add(new WinVolume { DriveLetter = "", FileSystemLabel = "Data", FileSystem = "NTFS", Size = 500L * 1024 * 1024 * 1024, DriveType = "Fixed" });
        s.Volumes.Add(new WinVolume { DriveLetter = "", FileSystemLabel = "", FileSystem = "", Size = 100L * 1024 * 1024, DriveType = "Fixed" });
        s.PnpDisks.Add(new WinPnpDisk { FriendlyName = "Samsung SSD 970 EVO 500G USB Device", Status = "Unknown", Problem = "CM_PROB_PHANTOM", InstanceId = "USBSTOR\\x" });
        s.PnpDisks.Add(new WinPnpDisk { FriendlyName = "Ghost Drive", Status = "OK", Problem = "CM_PROB_NONE", InstanceId = "SCSI\\ghost", Present = true });
        s.Services.Add(new WinService { Name = "vds", Status = "Stopped", StartType = "Manual" });
        s.Services.Add(new WinService { Name = "StorSvc", Status = "Running" });
        var f = WindowsStorage.Diagnose(s);
        Assert.Contains(f, x => x.Title.Contains("foreign dynamic") && x.Action?.Kind == WindowsActionKind.ImportForeign && x.Action.Target == "0");
        var ghost = Assert.Single(f, x => x.Title.Contains("no disk object"));
        Assert.Equal(WindowsActionKind.RedetectDevice, ghost.Action!.Kind); Assert.Equal("SCSI\\ghost", ghost.Action.Target);
        var unl = Assert.Single(f, x => x.Title.Contains("has no drive letter"));
        Assert.Equal(WindowsActionKind.AssignLetter, unl.Action!.Kind); Assert.Equal("7", unl.Action.Target);
        Assert.Contains(f, x => x.Title.Contains("is RAW"));
        Assert.Contains(f, x => x.Title.Contains("automount is off") && x.Action?.Kind == WindowsActionKind.EnableAutomount);
        Assert.Contains(f, x => x.Title.Contains("phantom") && x.Action?.Kind == WindowsActionKind.RemovePhantoms);
        Assert.DoesNotContain(f, x => x.Title.Contains("Service vds")); // vds is manual/on-demand; only core services are flagged
        Assert.DoesNotContain(f, x => x.Severity == "good");

        var clean = new WindowsStorageSnapshot();
        clean.PhysicalDisks.Add(new WinPhysicalDisk { DeviceId = "1", FriendlyName = "SPCC", HealthStatus = "Healthy" });
        clean.Disks.Add(new WinDisk { Number = 1, IsOffline = false, OperationalStatus = "Online" });
        Assert.Single(WindowsStorage.Diagnose(clean), x => x.Severity == "good");
    }

    [Fact]
    public void Actions_CarryTheCommandTheyRun()
    {
        Assert.Contains("Set-Disk -Number 3 -IsOffline $false", WindowsStorage.Actions.OnlineDisk(3).Command);
        Assert.Contains("select disk 0 / import", WindowsStorage.Actions.ImportForeign(0).Command);
        Assert.Contains("assign letter=T", WindowsStorage.Actions.AssignLetter(5, "T:").Command);
        Assert.Equal("mountvol /E", WindowsStorage.Actions.EnableAutomount().Command);
        var rep = WindowsStorage.BuildReport(new WindowsStorageSnapshot());
        Assert.Contains(rep.Sections, s => s.Title == "Get-Disk");
    }
}
