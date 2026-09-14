namespace Nova4Me2.Core.Devices;

/// <summary>Block device backed by a plain file (raw disk image, .img / .dd / .bin) or, on Linux, a /dev node.</summary>
public sealed class FileBlockDevice : IBlockDevice
{
    private readonly FileStream _fs;
    private readonly object _lock = new();

    public FileBlockDevice(string path, bool writable = false, int sectorSize = 512, long? lengthOverride = null)
    {
        Path = path;
        SectorSize = sectorSize;
        _fs = new FileStream(path, FileMode.Open, writable ? FileAccess.ReadWrite : FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.None);
        CanWrite = writable;
        long len = lengthOverride ?? _fs.Length;
        if (len <= 0)
        {
            // Block special files report 0 length via FileStream on some platforms; seek to end to discover size.
            try { len = _fs.Seek(0, SeekOrigin.End); } catch { len = 0; }
        }
        Length = len;
        Description = $"Image file {System.IO.Path.GetFileName(path)} ({Util.Format.Bytes(Length)})";
    }

    /// <summary>Create (or truncate) a new image file of the given size.</summary>
    public static FileBlockDevice Create(string path, long length, int sectorSize = 512)
    {
        using (var f = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            f.SetLength(length);
        return new FileBlockDevice(path, writable: true, sectorSize: sectorSize);
    }

    public string Path { get; }
    public long Length { get; }
    public int SectorSize { get; }
    public string Description { get; }
    public bool CanWrite { get; }

    public void ReadExact(long offset, Span<byte> buffer)
    {
        if (offset < 0 || offset + buffer.Length > Length)
            throw new ArgumentOutOfRangeException(nameof(offset), $"Read of {buffer.Length} bytes at {offset} exceeds device length {Length}.");
        lock (_lock)
        {
            _fs.Position = offset;
            int total = 0;
            while (total < buffer.Length)
            {
                int n = _fs.Read(buffer[total..]);
                if (n <= 0) throw new EndOfStreamException($"Short read at {offset + total}.");
                total += n;
            }
        }
    }

    public void WriteExact(long offset, ReadOnlySpan<byte> buffer)
    {
        if (!CanWrite) throw new InvalidOperationException("Device is read-only.");
        lock (_lock)
        {
            _fs.Position = offset;
            _fs.Write(buffer);
        }
    }

    public void Flush() { lock (_lock) _fs.Flush(true); }

    public void Dispose() => _fs.Dispose();
}
