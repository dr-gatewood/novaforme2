using Nova4Me2.Core.Devices;
using Nova4Me2.Core.Ntfs;
using Nova4Me2.Core.Util;
using Xunit;

namespace Nova4Me2.Tests;

public class UnitTests
{
    [Fact]
    public void Lznt1_DecodesLiteralAndBackReference()
    {
        // chunk: header 0xB003 (compressed, size 4), flags 0x02, literal 'a', backref len 11 disp 1
        byte[] src = { 0x03, 0xB0, 0x02, (byte)'a', 0x08, 0x00 };
        var dst = new byte[64];
        int n = Lznt1.Decompress(src, dst);
        Assert.Equal(12, n);
        Assert.All(dst.Take(12), b => Assert.Equal((byte)'a', b));
    }

    [Fact]
    public void Lznt1_UncompressedChunkCopiesThrough()
    {
        var payload = System.Text.Encoding.ASCII.GetBytes("hello lznt1");
        var src = new byte[2 + payload.Length];
        int hdr = (payload.Length - 1) | 0x3000;
        src[0] = (byte)hdr; src[1] = (byte)(hdr >> 8);
        payload.CopyTo(src, 2);
        var dst = new byte[32];
        Assert.Equal(payload.Length, Lznt1.Decompress(src, dst));
        Assert.Equal(payload, dst.Take(payload.Length));
    }

    [Fact]
    public void DataRuns_DecodeRelativeOffsetsAndSparse()
    {
        byte[] r = { 0x21, 0x18, 0x34, 0x56, 0x11, 0x05, 0xF0, 0x01, 0x10, 0x00 };
        var runs = new List<DataRun>();
        Assert.True(MftRecord.DecodeRuns(r, 0, 1_000_000, runs, out var err));
        Assert.Null(err);
        Assert.Equal(3, runs.Count);
        Assert.Equal(0x5634, runs[0].Lcn); Assert.Equal(0x18, runs[0].Count); Assert.Equal(0, runs[0].Vcn);
        Assert.Equal(0x5624, runs[1].Lcn); Assert.Equal(5, runs[1].Count); Assert.Equal(0x18, runs[1].Vcn);
        Assert.True(runs[2].IsSparse); Assert.Equal(16, runs[2].Count); Assert.Equal(0x1D, runs[2].Vcn);
    }

    [Fact]
    public void DataRuns_RejectOutOfVolume()
    {
        byte[] r = { 0x21, 0x18, 0x34, 0x56, 0x00 };
        var runs = new List<DataRun>();
        Assert.False(MftRecord.DecodeRuns(r, 0, 0x100, runs, out var err));
        Assert.Contains("outside", err);
    }

    [Fact]
    public void Fixups_DetectTornWrite()
    {
        var b = new byte[1024];
        b[0] = (byte)'F'; b[1] = (byte)'I'; b[2] = (byte)'L'; b[3] = (byte)'E';
        Bin.PutU16(b, 4, 0x30); Bin.PutU16(b, 6, 3);
        Bin.PutU16(b, 0x30, 0x1234); Bin.PutU16(b, 0x32, 0xAAAA); Bin.PutU16(b, 0x34, 0xBBBB);
        Bin.PutU16(b, 510, 0x1234); Bin.PutU16(b, 1022, 0x1234);
        var probs = new List<string>();
        Assert.True(MftRecord.ApplyFixups(b, 512, probs));
        Assert.Equal(0xAAAA, Bin.U16(b, 510)); Assert.Equal(0xBBBB, Bin.U16(b, 1022));
        Bin.PutU16(b, 510, 0x9999);
        Assert.False(MftRecord.ApplyFixups(b, 512, probs));
    }

    [Fact]
    public void Crc32_KnownVector()
    {
        Assert.Equal(0xCBF43926u, Crc32.Compute("123456789"u8));
    }

    [Fact]
    public void BootSector_ValidatesFields()
    {
        var s = new byte[512];
        Assert.NotEmpty(BootSector.Validate(s));
        "NTFS    "u8.CopyTo(s.AsSpan(3));
        Bin.PutU16(s, 0x0B, 512); s[0x0D] = 8; s[0x15] = 0xF8;
        Bin.PutU64(s, 0x28, 1000000); Bin.PutU64(s, 0x30, 4); Bin.PutU64(s, 0x38, 500);
        s[0x40] = 0xF6; s[0x44] = 1; s[510] = 0x55; s[511] = 0xAA;
        Assert.Empty(BootSector.Validate(s));
        var bs = BootSector.Parse(s);
        Assert.Equal(4096, bs.BytesPerCluster); Assert.Equal(1024, bs.MftRecordSize); Assert.Equal(4096, bs.IndexBlockSize);
    }

    /// <summary>In-memory device that can simulate a USB drop-out and bad sectors.</summary>
    private sealed class FlakyDevice : IBlockDevice
    {
        public byte[] Data;
        public HashSet<long> BadSectors = new();
        public int FailNextReads;      // simulates the link dying: every read fails while > 0 (decrement on probe)
        public bool Dead;
        public int Reads;
        public FlakyDevice(byte[] data) { Data = data; }
        public long Length => Data.Length;
        public int SectorSize => 512;
        public string Description => "flaky";
        public bool CanWrite => false;
        public void ReadExact(long offset, Span<byte> buffer)
        {
            Reads++;
            if (Dead) throw new IOException("device not connected", 1167);
            for (long s = offset / 512; s <= (offset + buffer.Length - 1) / 512; s++)
                if (BadSectors.Contains(s)) throw new IOException("CRC error", 23);
            Data.AsSpan((int)offset, buffer.Length).CopyTo(buffer);
        }
        public void WriteExact(long offset, ReadOnlySpan<byte> buffer) => throw new NotSupportedException();
        public void Flush() { }
        public void Dispose() { }
    }

    [Fact]
    public void Resilient_ReconnectsAfterDisconnect()
    {
        var data = new byte[1024 * 1024];
        new Random(1).NextBytes(data);
        var dev = new FlakyDevice(data);
        int reopenCalls = 0;
        FlakyDevice? replacement = null;
        var res = new ResilientBlockDevice(dev, () =>
        {
            reopenCalls++;
            if (reopenCalls < 3) return null; // gone for two polls
            return replacement = new FlakyDevice(data);
        }, new ResilienceOptions { PollInterval = TimeSpan.FromMilliseconds(5), ReconnectTimeout = TimeSpan.FromSeconds(5), Keepalive = null, MaxChunk = 64 * 1024 });
        var got = new byte[300_000];
        res.ReadExact(100, got);
        Assert.Equal(data.AsSpan(100, 300_000).ToArray(), got);
        dev.Dead = true;
        var states = new List<ConnectionState>();
        res.StateChanged += (_, s) => states.Add(s);
        res.ReadExact(500_000, got);
        Assert.Equal(data.AsSpan(500_000, 300_000).ToArray(), got);
        Assert.Equal(1, res.Stats.Reconnects);
        Assert.Contains(ConnectionState.Reconnecting, states);
        Assert.Equal(ConnectionState.Connected, res.State);
        Assert.NotNull(replacement);
    }

    [Fact]
    public void Resilient_GivesUpAfterTimeout()
    {
        var dev = new FlakyDevice(new byte[65536]) { Dead = true };
        var res = new ResilientBlockDevice(dev, () => null, new ResilienceOptions { PollInterval = TimeSpan.FromMilliseconds(5), ReconnectTimeout = TimeSpan.FromMilliseconds(60), Keepalive = null });
        Assert.Throws<DeviceLostException>(() => res.ReadExact(0, new byte[512]));
        Assert.Equal(ConnectionState.Lost, res.State);
    }

    [Fact]
    public void Resilient_IsolatesBadSectors()
    {
        var data = new byte[256 * 1024];
        new Random(2).NextBytes(data);
        var dev = new FlakyDevice(data);
        dev.BadSectors.Add(100); dev.BadSectors.Add(101);
        var res = new ResilientBlockDevice(dev, () => new FlakyDevice(data), new ResilienceOptions { Keepalive = null, RetriesPerSector = 1, ZeroFillBadSectors = false });
        var ex = Assert.Throws<BadSectorException>(() => res.ReadExact(0, new byte[128 * 1024]));
        Assert.Equal(100 * 512, ex.Offset);
        Assert.Equal(0, res.Stats.Reconnects);

        var bad = new List<long>();
        var res2 = new ResilientBlockDevice(new FlakyDevice(data) { BadSectors = { 100, 101 } }, () => null, new ResilienceOptions { Keepalive = null, RetriesPerSector = 1, ZeroFillBadSectors = true });
        res2.BadSector += (_, e) => bad.Add(e.Offset / 512);
        var got = new byte[128 * 1024];
        res2.ReadExact(0, got);
        Assert.Equal(new long[] { 100, 101 }, bad);
        Assert.All(got.AsSpan(100 * 512, 1024).ToArray(), b => Assert.Equal(0, b));
        Assert.Equal(data.AsSpan(0, 100 * 512).ToArray(), got.AsSpan(0, 100 * 512).ToArray());
        Assert.Equal(data.AsSpan(102 * 512).ToArray()[..1000], got.AsSpan(102 * 512, 1000).ToArray());
    }
}
