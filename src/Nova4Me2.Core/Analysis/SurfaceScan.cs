using System.Diagnostics;
using Nova4Me2.Core.Devices;
using Nova4Me2.Core.Recovery;
using Nova4Me2.Core.Util;

namespace Nova4Me2.Core.Analysis;

public sealed class SurfaceScanOptions
{
    public long StartOffset { get; set; }
    public long Length { get; set; }
    public int ChunkSize { get; set; } = 4 * 1024 * 1024;
    /// <summary>Quick mode reads only every Nth chunk (1 = full scan).</summary>
    public int SampleEvery { get; set; } = 1;
    public double SlowThresholdSeconds { get; set; } = 1.0;
    public int MapCells { get; set; } = 1024;
}

public sealed class SurfaceScanResult
{
    public long BytesTotal, BytesRead;
    public long ChunksRead, ChunksBad, ChunksSlow;
    public long BadSectors;
    public List<(long Offset, long Length)> BadRanges = new();
    public double MinSeconds = double.MaxValue, MaxSeconds, AvgSeconds;
    public double BytesPerSecond;
    public TimeSpan Elapsed;
    public long Reconnects;
    public BlockMap? Map;
    public bool Complete;
    public string Phase = "";
    public double Fraction => BytesTotal > 0 ? Math.Min(1, (double)BytesRead / BytesTotal) : 0;
    public SurfaceScanResult Snapshot() { var c = (SurfaceScanResult)MemberwiseClone(); c.BadRanges = new(BadRanges); c.Map = Map?.Clone(); return c; }
    public string Grade => ChunksRead == 0 ? "n/a" : BadSectors == 0 && ChunksSlow == 0 ? "Clean" : BadSectors == 0 ? "Slow areas" : BadSectors < 16 ? "Isolated bad sectors" : "Many bad sectors";
}

/// <summary>Read-only surface test with latency tracking and a block map.</summary>
public static class SurfaceScan
{
    public static SurfaceScanResult Run(ResilientBlockDevice dev, SurfaceScanOptions opt, IProgress<SurfaceScanResult>? progress = null, CancellationToken ct = default)
    {
        long start = opt.StartOffset;
        long length = opt.Length > 0 ? opt.Length : dev.Length - start;
        int chunk = (int)Bin.AlignUp(Math.Max(dev.SectorSize, opt.ChunkSize), dev.SectorSize);
        var r = new SurfaceScanResult { BytesTotal = length, Map = new BlockMap(start, length, opt.MapCells), Phase = opt.SampleEvery > 1 ? "Quick surface scan" : "Full surface scan" };
        var buf = new byte[chunk];
        var sw = Stopwatch.StartNew();
        long reconnectsBefore = dev.Stats.Reconnects;
        double totalSecs = 0;
        var origZero = dev.Options.ZeroFillBadSectors;
        var origSub = dev.Options.SubdivideOnError;
        dev.Options.ZeroFillBadSectors = true;
        dev.Options.SubdivideOnError = true;
        var handler = new EventHandler<BadSectorEventArgs>((_, e) => { lock (r) { r.BadSectors++; r.BadRanges.Add((e.Offset, e.Length)); r.Map!.Mark(e.Offset, e.Length, BlockState.Bad); } });
        dev.BadSector += handler;
        DateTime last = DateTime.MinValue;
        long windowBytes = 0; var window = Stopwatch.StartNew();
        try
        {
            long idx = 0;
            for (long off = 0; off < length; off += chunk, idx++)
            {
                ct.ThrowIfCancellationRequested();
                int n = (int)Math.Min(chunk, length - off);
                long abs = start + off;
                if (opt.SampleEvery > 1 && idx % opt.SampleEvery != 0) { r.BytesRead += n; continue; }
                r.Map!.Mark(abs, n, BlockState.Reading);
                long bad0 = r.BadSectors;
                var t0 = Stopwatch.GetTimestamp();
                dev.ReadExact(abs, buf.AsSpan(0, n));
                double secs = Stopwatch.GetElapsedTime(t0).TotalSeconds;
                r.ChunksRead++;
                totalSecs += secs;
                r.MinSeconds = Math.Min(r.MinSeconds, secs);
                r.MaxSeconds = Math.Max(r.MaxSeconds, secs);
                if (r.BadSectors > bad0) r.ChunksBad++;
                else if (secs > opt.SlowThresholdSeconds) { r.ChunksSlow++; r.Map.Mark(abs, n, BlockState.Slow); }
                else r.Map.Mark(abs, n, BlockState.Good);
                r.BytesRead += n;
                windowBytes += n;
                if (window.ElapsedMilliseconds > 1500) { r.BytesPerSecond = windowBytes / window.Elapsed.TotalSeconds; window.Restart(); windowBytes = 0; }
                if ((DateTime.UtcNow - last).TotalMilliseconds > 200) { r.Elapsed = sw.Elapsed; r.AvgSeconds = totalSecs / r.ChunksRead; r.Reconnects = dev.Stats.Reconnects - reconnectsBefore; progress?.Report(r.Snapshot()); last = DateTime.UtcNow; }
            }
            r.Complete = true;
        }
        finally
        {
            dev.BadSector -= handler;
            dev.Options.ZeroFillBadSectors = origZero;
            dev.Options.SubdivideOnError = origSub;
            r.Elapsed = sw.Elapsed;
            if (r.ChunksRead > 0) r.AvgSeconds = totalSecs / r.ChunksRead;
            if (r.MinSeconds == double.MaxValue) r.MinSeconds = 0;
            r.Reconnects = dev.Stats.Reconnects - reconnectsBefore;
            r.Phase = r.Complete ? "Complete" : "Stopped";
            progress?.Report(r.Snapshot());
        }
        return r;
    }
}
