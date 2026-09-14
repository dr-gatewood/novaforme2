namespace Nova4Me2.Core.Ntfs;

/// <summary>Read-only, seekable stream over one (logical) NTFS attribute: resident, plain, sparse or LZNT1-compressed.</summary>
public sealed class NtfsStream : Stream
{
    private readonly NtfsVolume _vol;
    private readonly NtfsAttribute _attr;
    private readonly List<DataRun> _runs;
    private readonly long _length;
    private readonly long _initialized;
    private readonly int _cs;
    private readonly bool _compressed;
    private readonly int _cuClusters;
    private readonly long _cuBytes;
    private byte[]? _cuBuffer;
    private long _cuCached = -1;
    private long _pos;

    public NtfsStream(NtfsVolume vol, NtfsAttribute attr)
    {
        _vol = vol;
        _attr = attr;
        _cs = vol.ClusterSize;
        _runs = attr.Runs.OrderBy(r => r.Vcn).ToList();
        _length = attr.NonResident ? attr.RealSize : attr.ResidentValue.Length;
        _initialized = attr.NonResident ? Math.Min(attr.InitializedSize, _length) : _length;
        _compressed = attr.NonResident && attr.IsCompressed && attr.CompressionUnit > 0;
        _cuClusters = _compressed ? 1 << attr.CompressionUnit : 0;
        _cuBytes = (long)_cuClusters * _cs;
    }

    public NtfsAttribute Attribute => _attr;
    public bool IsCompressed => _compressed;
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;
    public override long Position { get => _pos; set => _pos = value; }
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin)
    {
        _pos = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => _pos + offset, _ => _length + offset };
        return _pos;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_pos >= _length || buffer.Length == 0) return 0;
        int want = (int)Math.Min(buffer.Length, _length - _pos);
        if (!_attr.NonResident)
        {
            _attr.ResidentValue.AsSpan((int)_pos, want).CopyTo(buffer);
            _pos += want;
            return want;
        }
        int done = 0;
        while (done < want)
        {
            var dst = buffer.Slice(done, want - done);
            int n = _compressed ? ReadCompressed(_pos, dst) : ReadPlain(_pos, dst);
            if (n <= 0) break;
            // Beyond the initialized size NTFS guarantees zeros regardless of what the clusters hold.
            if (_pos + n > _initialized)
            {
                long zeroFrom = Math.Max(0, _initialized - _pos);
                dst.Slice((int)zeroFrom, n - (int)zeroFrom).Clear();
            }
            _pos += n;
            done += n;
        }
        return done;
    }

    private DataRun? FindRun(long vcn)
    {
        int lo = 0, hi = _runs.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            var r = _runs[mid];
            if (vcn < r.Vcn) hi = mid - 1;
            else if (vcn >= r.Vcn + r.Count) lo = mid + 1;
            else return r;
        }
        return null;
    }

    private long MapVcn(long vcn)
    {
        var r = FindRun(vcn);
        return r == null || r.IsSparse ? -1 : r.Lcn + (vcn - r.Vcn);
    }

    private int ReadPlain(long pos, Span<byte> dst)
    {
        long vcn = pos / _cs;
        int inCluster = (int)(pos % _cs);
        var run = FindRun(vcn);
        if (run == null)
        {
            // Hole in the mapping (truncated run list / corruption): treat as sparse for the rest of this cluster.
            int z = (int)Math.Min(dst.Length, _cs - inCluster);
            dst[..z].Clear();
            return z;
        }
        long avail = (run.Vcn + run.Count - vcn) * _cs - inCluster;
        int n = (int)Math.Min(dst.Length, avail);
        if (run.IsSparse) { dst[..n].Clear(); return n; }
        long lcn = run.Lcn + (vcn - run.Vcn);
        _vol.ReadBytesAtCluster(lcn, inCluster, dst[..n]);
        return n;
    }

    private int ReadCompressed(long pos, Span<byte> dst)
    {
        long cu = pos / _cuBytes;
        EnsureCu(cu);
        int inCu = (int)(pos % _cuBytes);
        int n = (int)Math.Min(dst.Length, _cuBytes - inCu);
        _cuBuffer.AsSpan(inCu, n).CopyTo(dst);
        return n;
    }

    private void EnsureCu(long cu)
    {
        if (_cuCached == cu && _cuBuffer != null) return;
        _cuBuffer ??= new byte[_cuBytes];
        long firstVcn = cu * _cuClusters;
        var lcns = new long[_cuClusters];
        int mapped = 0, leading = 0;
        bool contiguousLeading = true;
        for (int i = 0; i < _cuClusters; i++)
        {
            lcns[i] = MapVcn(firstVcn + i);
            if (lcns[i] >= 0) { mapped++; if (contiguousLeading) leading++; }
            else contiguousLeading = false;
        }
        if (mapped == 0)
        {
            Array.Clear(_cuBuffer);
        }
        else if (mapped == _cuClusters)
        {
            // Stored uncompressed.
            for (int i = 0; i < _cuClusters; i++) _vol.ReadBytesAtCluster(lcns[i], 0, _cuBuffer.AsSpan(i * _cs, _cs));
        }
        else
        {
            var packed = new byte[leading * _cs];
            for (int i = 0; i < leading; i++) _vol.ReadBytesAtCluster(lcns[i], 0, packed.AsSpan(i * _cs, _cs));
            int produced = Lznt1.Decompress(packed, _cuBuffer);
            if (produced < _cuBytes) _cuBuffer.AsSpan(produced).Clear();
        }
        _cuCached = cu;
    }
}
