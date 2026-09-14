using System.Diagnostics;
using Nova4Me2.Core.Util;

namespace Nova4Me2.Core.Devices;

public enum ConnectionState { Connected, Degraded, Reconnecting, Lost, Closed }

public sealed class ResilienceOptions
{
    /// <summary>How long to keep waiting for a vanished drive to come back before giving up.</summary>
    public TimeSpan ReconnectTimeout { get; set; } = TimeSpan.FromMinutes(3);
    /// <summary>How often to look for the drive while it is gone.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(750);
    /// <summary>Largest single transfer; smaller chunks are gentler on flaky USB bridges.</summary>
    public int MaxChunk { get; set; } = 1024 * 1024;
    /// <summary>Retries for a single sector before it is declared bad.</summary>
    public int RetriesPerSector { get; set; } = 3;
    /// <summary>Maximum reconnects tolerated inside one read call before giving up.</summary>
    public int MaxReconnectsPerRead { get; set; } = 25;
    /// <summary>Return zeros for unreadable sectors instead of throwing (imaging / best-effort copies).</summary>
    public bool ZeroFillBadSectors { get; set; }
    /// <summary>Periodic tiny read to stop enclosures from idling/suspending. Null disables.</summary>
    public TimeSpan? Keepalive { get; set; } = TimeSpan.FromSeconds(4);
    /// <summary>Optional throughput cap (0 = unlimited). Some failing drives survive better when not saturated.</summary>
    public long MaxBytesPerSecond { get; set; }
    /// <summary>Optional pause between chunks.</summary>
    public TimeSpan InterChunkDelay { get; set; } = TimeSpan.Zero;
    /// <summary>Hard time-out for a single device request (Windows devices); a hung bridge is treated as a failed read after this.</summary>
    public TimeSpan IoTimeout { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>When a chunk fails on a live device, retry it in smaller pieces down to one sector (true) or fail the whole chunk fast (false; used by the first imaging pass).</summary>
    public bool SubdivideOnError { get; set; } = true;
}

public sealed class BadSectorEventArgs(long offset, int length, string reason) : EventArgs
{
    public long Offset { get; } = offset;
    public int Length { get; } = length;
    public string Reason { get; } = reason;
}

public sealed class DeviceStats
{
    public long BytesRead;
    public long ReadCalls;
    public long ReadErrors;
    public long Reconnects;
    public long BadSectors;
    public DateTime LastIo = DateTime.MinValue;
    public DateTime LastReconnect = DateTime.MinValue;
    public DeviceStats Clone() => (DeviceStats)MemberwiseClone();
}

/// <summary>
/// Wraps a block device and keeps it usable across USB disconnects: reads are chunked, failures are classified as
/// "device gone" (re-open by identity and retry) or "localized" (subdivide, retry, then declare bad sector).
/// Optional keepalive reads and throttling. Thread-safe; all I/O is serialized.
/// </summary>
public sealed class ResilientBlockDevice : IBlockDevice
{
    private readonly object _lock = new();
    private readonly Func<IBlockDevice?> _reopen;
    private readonly ResilienceOptions _opt;
    private IBlockDevice? _inner;
    private Timer? _keepalive;
    private volatile ConnectionState _state = ConnectionState.Connected;
    private readonly Stopwatch _throttleClock = Stopwatch.StartNew();
    private long _throttleBytes;
    private volatile bool _disposed;
    private readonly bool _canWrite;

    public event EventHandler<ConnectionState>? StateChanged;
    public event EventHandler<BadSectorEventArgs>? BadSector;
    public event EventHandler<string>? Reconnected;
    public event EventHandler<string>? Message;

    public DeviceStats Stats { get; } = new();
    public ResilienceOptions Options => _opt;
    public ConnectionState State => _state;
    public DriveIdentity? Identity { get; }

    public ResilientBlockDevice(IBlockDevice initial, Func<IBlockDevice?> reopen, ResilienceOptions? options = null, DriveIdentity? identity = null)
    {
        _inner = initial ?? throw new ArgumentNullException(nameof(initial));
        _reopen = reopen;
        _opt = options ?? new ResilienceOptions();
        Identity = identity;
        Length = initial.Length;
        SectorSize = initial.SectorSize;
        Description = initial.Description;
        _canWrite = initial.CanWrite;
        if (_opt.Keepalive is { } ka && ka > TimeSpan.Zero)
            _keepalive = new Timer(_ => KeepaliveTick(), null, ka, ka);
    }

    /// <summary>Open a raw image file with resilience (mostly useful for a uniform code path and tests).</summary>
    public static ResilientBlockDevice ForImage(string path, ResilienceOptions? options = null, bool writable = false)
    {
        var dev = new FileBlockDevice(path, writable);
        return new ResilientBlockDevice(dev, () => new FileBlockDevice(path, writable), options ?? new ResilienceOptions { Keepalive = null });
    }

    public long Length { get; }
    public int SectorSize { get; }
    public string Description { get; private set; }
    public bool CanWrite => _canWrite;
    public IBlockDevice? Inner => _inner;

    public void ReadExact(long offset, Span<byte> buffer)
    {
        if (offset < 0 || offset + buffer.Length > Length)
            throw new ArgumentOutOfRangeException(nameof(offset), $"Read of {buffer.Length} at {offset} exceeds device length {Length}.");
        lock (_lock)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ResilientBlockDevice));
            int pos = 0;
            while (pos < buffer.Length)
            {
                int chunk = (int)Math.Min(_opt.MaxChunk, buffer.Length - pos);
                ReadChunk(offset + pos, buffer.Slice(pos, chunk), 0);
                pos += chunk;
                Throttle(chunk);
            }
            Stats.ReadCalls++;
            Stats.LastIo = DateTime.UtcNow;
        }
    }

    private void ReadChunk(long off, Span<byte> span, int depth)
    {
        int reconnects = 0;
        int sectorRetries = 0;
        while (true)
        {
            var dev = _inner;
            try
            {
                if (dev == null) throw new IOException("Device handle not open.");
                dev.ReadExact(off, span);
                Stats.BytesRead += span.Length;
                if (_state == ConnectionState.Degraded) SetState(ConnectionState.Connected);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                Stats.ReadErrors++;
                if (!Probe(dev))
                {
                    if (++reconnects > _opt.MaxReconnectsPerRead)
                        throw new DeviceLostException($"Drive kept disappearing ({reconnects} reconnects during one read).", ex);
                    Reconnect(ex.Message); // throws DeviceLostException after timeout
                    continue;
                }
                // Device is alive: the error is localized to this range.
                if (span.Length > SectorSize && !_opt.SubdivideOnError)
                    throw new BadSectorException(off, span.Length, ex); // fast-fail: the caller (imager pass 1) will come back for this chunk
                if (span.Length > SectorSize)
                {
                    long mid = Bin.AlignDown(off + span.Length / 2, SectorSize);
                    if (mid <= off) mid = off + SectorSize;
                    int first = (int)Math.Min(span.Length, mid - off);
                    Emit($"Read error at {off} (+{span.Length}); retrying in smaller pieces: {ex.Message}");
                    ReadChunk(off, span[..first], depth + 1);
                    if (first < span.Length) ReadChunk(off + first, span[first..], depth + 1);
                    return;
                }
                if (++sectorRetries <= _opt.RetriesPerSector)
                {
                    Thread.Sleep(50 * sectorRetries);
                    continue;
                }
                Stats.BadSectors++;
                BadSector?.Invoke(this, new BadSectorEventArgs(off, span.Length, ex.Message));
                if (_opt.ZeroFillBadSectors)
                {
                    span.Clear();
                    Emit($"Bad sector at byte {off} (LBA {off / SectorSize}) filled with zeros.");
                    return;
                }
                throw new BadSectorException(off, span.Length, ex);
            }
        }
    }

    private bool Probe(IBlockDevice? dev)
    {
        if (dev == null) return false;
        try
        {
            Span<byte> s = stackalloc byte[512];
            dev.ReadExact(0, s[..Math.Min(512, (int)Math.Min(dev.Length, 512))]);
            return true;
        }
        catch { return false; }
    }

    private void Reconnect(string reason)
    {
        SetState(ConnectionState.Reconnecting);
        Emit($"Drive connection lost ({reason}). Waiting for it to come back (up to {Format.Duration(_opt.ReconnectTimeout)})…");
        try { _inner?.Dispose(); } catch { }
        _inner = null;
        var deadline = DateTime.UtcNow + _opt.ReconnectTimeout;
        int attempt = 0;
        while (true)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ResilientBlockDevice));
            Thread.Sleep(_opt.PollInterval);
            if (_disposed) throw new ObjectDisposedException(nameof(ResilientBlockDevice));
            attempt++;
            IBlockDevice? d = null;
            try
            {
                d = _reopen();
                if (d != null)
                {
                    if (d.Length != Length)
                    {
                        Emit($"Candidate drive has a different size ({d.Length} vs {Length}); ignoring.");
                        d.Dispose();
                        d = null;
                    }
                    else if (Identity != null && !Identity.MatchesSignature(d))
                    {
                        Emit("Candidate drive's first sectors do not match the original; ignoring.");
                        d.Dispose();
                        d = null;
                    }
                }
            }
            catch (Exception ex)
            {
                if (attempt % 10 == 0) Emit($"Still waiting for drive… ({ex.Message})");
                try { d?.Dispose(); } catch { }
                d = null;
            }
            if (d != null)
            {
                _inner = d;
                Description = d.Description;
                Stats.Reconnects++;
                Stats.LastReconnect = DateTime.UtcNow;
                SetState(ConnectionState.Connected);
                Emit($"Drive re-attached after {attempt} attempts: {d.Description}");
                Reconnected?.Invoke(this, d.Description);
                return;
            }
            if (DateTime.UtcNow > deadline)
            {
                SetState(ConnectionState.Lost);
                throw new DeviceLostException($"Drive did not come back within {Format.Duration(_opt.ReconnectTimeout)}.", null);
            }
        }
    }

    /// <summary>Force a reconnect attempt now (e.g. from a UI button). Returns true if a device is attached afterwards.</summary>
    public bool TryReconnectNow()
    {
        lock (_lock)
        {
            if (_disposed) return false;
            if (_inner != null && Probe(_inner)) { SetState(ConnectionState.Connected); return true; }
            try { Reconnect("manual"); return true; }
            catch (DeviceLostException) { return false; }
        }
    }

    private void KeepaliveTick()
    {
        if (_disposed) return;
        if (!Monitor.TryEnter(_lock, 0)) return; // busy: real I/O is the best keepalive
        try
        {
            if (_disposed) return;
            if (DateTime.UtcNow - Stats.LastIo < (_opt.Keepalive ?? TimeSpan.Zero)) return;
            var dev = _inner;
            if (dev != null && Probe(dev)) { if (_state != ConnectionState.Connected) SetState(ConnectionState.Connected); Stats.LastIo = DateTime.UtcNow; return; }
            if (_state == ConnectionState.Connected) { SetState(ConnectionState.Degraded); Emit("Keepalive read failed; drive may have dropped off the bus."); }
            // Opportunistic single reopen attempt so the UI recovers without waiting for the next real read.
            try
            {
                var d = _reopen();
                if (d != null && d.Length == Length) { try { _inner?.Dispose(); } catch { } _inner = d; Stats.Reconnects++; SetState(ConnectionState.Connected); Reconnected?.Invoke(this, d.Description); }
                else d?.Dispose();
            }
            catch { }
        }
        finally { Monitor.Exit(_lock); }
    }

    private void Throttle(int bytes)
    {
        if (_opt.InterChunkDelay > TimeSpan.Zero) Thread.Sleep(_opt.InterChunkDelay);
        if (_opt.MaxBytesPerSecond <= 0) return;
        _throttleBytes += bytes;
        double expected = (double)_throttleBytes / _opt.MaxBytesPerSecond;
        double actual = _throttleClock.Elapsed.TotalSeconds;
        if (expected > actual) Thread.Sleep(TimeSpan.FromSeconds(Math.Min(2, expected - actual)));
        if (actual > 5) { _throttleClock.Restart(); _throttleBytes = 0; }
    }

    private void SetState(ConnectionState s)
    {
        if (_state == s) return;
        _state = s;
        StateChanged?.Invoke(this, s);
    }

    private void Emit(string m)
    {
        Log.Warn(m);
        Message?.Invoke(this, m);
    }

    public void WriteExact(long offset, ReadOnlySpan<byte> buffer)
    {
        lock (_lock)
        {
            if (_inner == null || !_inner.CanWrite) throw new InvalidOperationException("Device is not writable.");
            _inner.WriteExact(offset, buffer);
            Stats.LastIo = DateTime.UtcNow;
        }
    }

    public void Flush() { lock (_lock) _inner?.Flush(); }

    public void Dispose()
    {
        // Flag first so a reconnect loop or keepalive that currently owns the lock bails out at its next check,
        // then take the lock to release the handle. Callers on a UI thread should dispose from a worker.
        _disposed = true;
        _keepalive?.Dispose();
        lock (_lock)
        {
            try { _inner?.Dispose(); } catch { }
            _inner = null;
            SetState(ConnectionState.Closed);
        }
    }
}
