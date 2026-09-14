using System.IO;
using Nova4Me2.Core.Devices;
using Nova4Me2.Core.Devices.Windows;
using Nova4Me2.Core.Hardware;
using Nova4Me2.Core.Analysis;
using Nova4Me2.Core.Ntfs;
using Nova4Me2.Core.Partitions;
using Nova4Me2.Core.Recovery;
using Nova4Me2.Core.Util;
using Nova4Me2.Mount;

namespace Nova4Me2.App.Services;

/// <summary>Process-wide state: the opened device, its volumes, the selected volume, directory source, mount, last analysis.</summary>
public sealed class AppState
{
    public static AppState Current { get; } = new();

    public Settings Settings { get; } = Settings.Load();
    public string SourceSpec { get; private set; } = "";
    public string SourceName { get; private set; } = "";
    public int? DriveNumber { get; private set; }
    public ResilientBlockDevice? Device { get; private set; }
    public List<NtfsVolumeCandidate> Volumes { get; private set; } = new();
    public NtfsVolumeCandidate? SelectedCandidate { get; private set; }
    public NtfsVolume? Volume { get; private set; }
    public IDirectorySource? Source { get; private set; }
    public MftIndex? MftIndex { get; private set; }
    public bool UseMftScan { get; private set; }
    public HardwareInfo? Hardware { get; private set; }
    public HealthReport? LastHealth { get; set; }
    public MountSession? Mount { get; set; }
    public PartitionTableInfo? Partitions { get; private set; }
    public JobManager Jobs { get; } = new();
    public ProtectionWatcher Protection { get; } = new();
    public event Action? HardwareChanged;
    public ConnectionState LinkState { get; private set; } = ConnectionState.Closed;

    public event Action? SourceChanged;
    public event Action? VolumeChanged;
    public event Action<ConnectionState>? LinkChanged;
    public event Action<string>? DeviceMessage;

    public bool HasDevice => Device != null;
    public bool HasVolume => Volume != null && Source != null;

    public ResilienceOptions BuildResilience() => new()
    {
        ReconnectTimeout = TimeSpan.FromSeconds(Math.Max(5, Settings.ReconnectTimeoutSeconds)),
        MaxChunk = Math.Max(64, Settings.ChunkKiB) * 1024,
        MaxBytesPerSecond = (long)(Settings.ThrottleMBps * 1024 * 1024),
        Keepalive = Settings.Keepalive ? TimeSpan.FromSeconds(4) : null,
        IoTimeout = TimeSpan.FromSeconds(Math.Clamp(Settings.IoTimeoutSeconds, 5, 300))
    };

    /// <summary>Open a physical drive (Windows) or an image file. Runs on a worker thread.</summary>
    public async Task OpenAsync(string spec, string displayName, int? driveNumber, IProgress<string>? progress = null)
    {
        Close();
        await Task.Run(() =>
        {
            progress?.Report("Opening device…");
            var dev = DeviceFactory.Open(spec, BuildResilience());
            dev.StateChanged += (_, s) => { LinkState = s; LinkChanged?.Invoke(s); };
            dev.Message += (_, m) => DeviceMessage?.Invoke(m);
            Device = dev;
            LinkState = ConnectionState.Connected;
            SourceSpec = spec;
            SourceName = displayName;
            DriveNumber = driveNumber;
            progress?.Report("Reading partition table…");
            try { Partitions = PartitionTable.Read(dev); } catch (Exception ex) { Log.Warn("Partition table: " + ex.Message); }
            progress?.Report("Locating NTFS volumes…");
            Volumes = VolumeLocator.Find(dev, Partitions, true, progress);
            foreach (var c in Volumes)
            {
                try { using var v = NtfsVolume.Open(dev, c); c.Label = v.Info.Label; } catch { }
            }
            Hardware = HardwareInfo.ForDevice(dev, spec);
        });
        LinkChanged?.Invoke(LinkState);
        SourceChanged?.Invoke();
        if (OperatingSystem.IsWindows() && driveNumber is { } n)
        {
            // SMART / identify pass-through can take a while through a USB bridge; never hold up opening the volume for it.
            var myDevice = Device;
            _ = Task.Run(() =>
            {
                try
                {
                    var hw = HardwareInfo.Collect(n);
                    if (Device != myDevice) return;
                    Hardware = hw;
                    HardwareChanged?.Invoke();
                }
                catch (Exception ex) { Log.Warn("Hardware query: " + ex.Message); }
            });
        }
        var pick = Volumes.OrderByDescending(v => v.Length).FirstOrDefault();
        if (pick != null) await SelectVolumeAsync(pick, progress);
    }

    public async Task SelectVolumeAsync(NtfsVolumeCandidate c, IProgress<string>? progress = null)
    {
        if (Device == null) return;
        await Task.Run(() =>
        {
            progress?.Report($"Opening NTFS volume at {Format.Bytes(c.StartOffset)}…");
            var vol = NtfsVolume.Open(Device, c);
            Volume = vol;
            Hardware ??= HardwareInfo.ForDevice(Device, SourceSpec);
            SelectedCandidate = c;
            MftIndex = null;
            UseMftScan = false;
            Source = new IndexDirectorySource(vol);
        });
        VolumeChanged?.Invoke();
    }

    /// <summary>Switch between the live directory index and a rebuilt MFT scan (which can also show deleted files).</summary>
    public async Task SetModeAsync(bool mftScan, bool includeDeleted, IProgress<(long Done, long Total)>? progress = null, CancellationToken ct = default)
    {
        if (Volume == null) return;
        if (!mftScan) { UseMftScan = false; Source = new IndexDirectorySource(Volume); VolumeChanged?.Invoke(); return; }
        var vol = Volume;
        var idx = await Task.Run(() => MftScanner.Scan(vol, includeDeleted, progress, ct), ct);
        if (Volume != vol) return;
        MftIndex = idx;
        UseMftScan = true;
        Source = new MftIndexDirectorySource(vol, idx);
        VolumeChanged?.Invoke();
    }

    /// <summary>Re-open the device for writing (repairs / clone target checks). Returns a separate writable handle; caller disposes.</summary>
    public ResilientBlockDevice OpenWritable() => DeviceFactory.Open(SourceSpec, new ResilienceOptions { Keepalive = null, ReconnectTimeout = TimeSpan.FromSeconds(60) }, writable: true);

    /// <summary>Detach from the current device. Never blocks the caller: handles are released on a worker thread (a read stuck in a hung USB bridge may take up to the I/O time-out to let go).</summary>
    public void Close()
    {
        var mount = Mount;
        var d = Device;
        Mount = null;
        Source = null;
        Volume = null;
        MftIndex = null;
        SelectedCandidate = null;
        Volumes = new();
        Partitions = null;
        Hardware = null;
        LastHealth = null;
        Device = null;
        LinkState = ConnectionState.Closed;
        SourceSpec = "";
        SourceName = "";
        DriveNumber = null;
        if (mount != null || d != null)
            _ = Task.Run(() => { try { mount?.Dispose(); } catch { } try { d?.Dispose(); } catch { } });
    }

    /// <summary>Windows-side protection of the drive being recovered: offline (mount manager ignores it) and read-only.</summary>
    public bool IsWindowsProtected(int driveNumber)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var a = DiskControl.GetAttributes(driveNumber);
        return a.Offline && a.ReadOnly;
    }
}
