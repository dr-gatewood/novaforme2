namespace Nova4Me2.Core.Devices;

/// <summary>
/// A random-access, read-only (optionally writable) block device: a physical disk, a partition window
/// onto a disk, or a raw image file. All offsets are in bytes; implementations handle sector alignment.
/// </summary>
public interface IBlockDevice : IDisposable
{
    /// <summary>Total size in bytes.</summary>
    long Length { get; }

    /// <summary>Logical sector size in bytes (512 or 4096).</summary>
    int SectorSize { get; }

    /// <summary>Human readable description (model, path, ...).</summary>
    string Description { get; }

    /// <summary>True if the device was opened with write access.</summary>
    bool CanWrite { get; }

    /// <summary>Read exactly <c>buffer.Length</c> bytes at <paramref name="offset"/>. Throws on unrecoverable error.</summary>
    void ReadExact(long offset, Span<byte> buffer);

    /// <summary>Write exactly <c>buffer.Length</c> bytes at <paramref name="offset"/>. Throws if not writable.</summary>
    void WriteExact(long offset, ReadOnlySpan<byte> buffer);

    /// <summary>Flush any pending writes to the device.</summary>
    void Flush();
}

/// <summary>Thrown when a sector range cannot be read even after every retry / reconnect strategy.</summary>
public sealed class BadSectorException : IOException
{
    public long Offset { get; }
    public int Length { get; }
    public BadSectorException(long offset, int length, Exception? inner)
        : base($"Unreadable sector range at byte offset {offset} (length {length}).", inner)
    {
        Offset = offset;
        Length = length;
    }
}

/// <summary>Thrown when a device disappeared and could not be re-attached within the configured timeout.</summary>
public sealed class DeviceLostException : IOException
{
    public DeviceLostException(string message, Exception? inner) : base(message, inner) { }
}

public static class BlockDeviceExtensions
{
    public static byte[] ReadBytes(this IBlockDevice dev, long offset, int length)
    {
        var buf = new byte[length];
        dev.ReadExact(offset, buf);
        return buf;
    }

    public static byte[] ReadSectors(this IBlockDevice dev, long lba, int count)
        => dev.ReadBytes(lba * dev.SectorSize, count * dev.SectorSize);
}
