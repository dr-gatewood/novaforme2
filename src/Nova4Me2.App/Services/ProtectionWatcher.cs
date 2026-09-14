using System.Windows.Threading;
using Nova4Me2.Core.Devices.Windows;
using Nova4Me2.Core.Util;

namespace Nova4Me2.App.Services;

/// <summary>
/// "Protect this disk" for drives that keep dropping off the bus: remembers the disk's identity and keeps trying to set
/// offline + read-only (short, abandonable attempts) on every device-arrival event and on a timer, until it succeeds.
/// Never blocks the UI thread; reports progress through <see cref="StatusChanged"/>.
/// </summary>
public sealed class ProtectionWatcher
{
    public sealed record Target(string Serial, string Model, long Length, string Display, int Number = -1);

    private readonly List<Target> _pending = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool _busy;
    public event Action<string>? StatusChanged;
    public event Action<Target, int>? Protected;

    public ProtectionWatcher()
    {
        _timer.Tick += (_, _) => _ = TryAllAsync("timer");
    }

    public bool IsPending(string serial, string model, long length) => _pending.Any(t => t.Length == length && (t.Serial.Length > 0 ? t.Serial.Equals(serial, StringComparison.OrdinalIgnoreCase) : t.Model.Equals(model, StringComparison.OrdinalIgnoreCase)));
    public int PendingCount => _pending.Count;

    public void Request(Target t)
    {
        if (IsPending(t.Serial, t.Model, t.Length)) return;
        _pending.Add(t);
        StatusChanged?.Invoke($"Waiting for {t.Display} to answer so it can be taken offline…");
        _timer.Start();
        _ = TryAllAsync("request");
    }

    public void Cancel(string serial, string model, long length)
    {
        _pending.RemoveAll(t => t.Length == length && (t.Serial.Length > 0 ? t.Serial.Equals(serial, StringComparison.OrdinalIgnoreCase) : t.Model.Equals(model, StringComparison.OrdinalIgnoreCase)));
        if (_pending.Count == 0) _timer.Stop();
    }

    public void OnDeviceChanged() { if (_pending.Count > 0) _ = TryAllAsync("device change"); }

    private async Task TryAllAsync(string reason)
    {
        if (_busy || _pending.Count == 0 || !OperatingSystem.IsWindows()) return;
        _busy = true;
        try
        {
            foreach (var t in _pending.ToList())
            {
                int? n = await Task.Run(() => t.Length > 0 ? DiskControl.TryProtectByIdentity(t.Serial, t.Model, t.Length) : DiskControl.TryProtectByNumber(t.Number));
                if (n is { } num)
                {
                    _pending.Remove(t);
                    Log.Info($"Protected {t.Display} as PhysicalDrive{num} ({reason}).");
                    StatusChanged?.Invoke($"{t.Display} is now offline and read-only in Windows.");
                    Protected?.Invoke(t, num);
                }
                else StatusChanged?.Invoke($"{t.Display}: not answering yet, will retry ({reason}).");
            }
            if (_pending.Count == 0) _timer.Stop();
        }
        finally { _busy = false; }
    }
}
