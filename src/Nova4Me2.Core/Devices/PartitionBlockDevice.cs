namespace Nova4Me2.Core.Devices;

/// <summary>A window (offset + length) onto another block device, e.g. a single partition.</summary>
public sealed class PartitionBlockDevice : IBlockDevice
{
    private readonly IBlockDevice _inner;
    private readonly bool _ownsInner;

    public PartitionBlockDevice(IBlockDevice inner, long offset, long length, string? description = null, bool ownsInner = false)
    {
        if (offset < 0 || length < 0 || offset + length > inner.Length)
            throw new ArgumentOutOfRangeException(nameof(offset), "Window exceeds underlying device.");
        _inner = inner;
        _ownsInner = ownsInner;
        Offset = offset;
        Length = length;
        Description = description ?? $"{inner.Description} @ {offset}";
    }

    public long Offset { get; }
    public long Length { get; }
    public int SectorSize => _inner.SectorSize;
    public string Description { get; }
    public bool CanWrite => _inner.CanWrite;
    public IBlockDevice Inner => _inner;

    public void ReadExact(long offset, Span<byte> buffer)
    {
        if (offset < 0 || offset + buffer.Length > Length)
            throw new ArgumentOutOfRangeException(nameof(offset), $"Read of {buffer.Length} at {offset} exceeds partition length {Length}.");
        _inner.ReadExact(Offset + offset, buffer);
    }

    public void WriteExact(long offset, ReadOnlySpan<byte> buffer)
    {
        if (offset < 0 || offset + buffer.Length > Length)
            throw new ArgumentOutOfRangeException(nameof(offset));
        _inner.WriteExact(Offset + offset, buffer);
    }

    public void Flush() => _inner.Flush();

    public void Dispose() { if (_ownsInner) _inner.Dispose(); }
}
