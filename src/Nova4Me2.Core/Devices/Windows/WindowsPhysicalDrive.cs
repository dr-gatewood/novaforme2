using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using Nova4Me2.Core.Util;

namespace Nova4Me2.Core.Devices.Windows;

/// <summary>
/// Direct sector access to <c>\\.\PhysicalDriveN</c> (or any other Win32 device path such as <c>\\.\C:</c>).
/// All reads are sector aligned and go through a page-aligned native buffer, which is what the storage stack
/// requires for unbuffered device I/O. Opened read-only unless explicitly requested otherwise.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class WindowsPhysicalDrive : IBlockDevice
{
    private const int MaxIo = 4 * 1024 * 1024;
    private readonly SafeFileHandle _h;
    private readonly object _lock = new();
    private byte* _buf;

    public WindowsPhysicalDrive(string devicePath, bool writable = false)
    {
        DevicePath = devicePath;
        uint access = NativeMethods.GENERIC_READ | (writable ? NativeMethods.GENERIC_WRITE : 0);
        _h = NativeMethods.CreateFileW(devicePath, access, NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE, IntPtr.Zero,
            NativeMethods.OPEN_EXISTING, NativeMethods.FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
        if (_h.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            throw new IOException($"Cannot open {devicePath}: {NativeMethods.ErrorText(err)}" +
                (err == NativeMethods.ERROR_ACCESS_DENIED ? " (run as Administrator)" : ""), err);
        }
        CanWrite = writable;

        // Sector size and length.
        int sector = 512;
        long length = 0;
        var geo = NativeMethods.Ioctl(_h, NativeMethods.IOCTL_DISK_GET_DRIVE_GEOMETRY_EX, ReadOnlySpan<byte>.Empty, 256, out _);
        if (geo != null && geo.Length >= 32)
        {
            sector = (int)Bin.U32(geo, 20);
            length = Bin.I64(geo, 24);
            if (sector <= 0) sector = 512;
        }
        var len = NativeMethods.Ioctl(_h, NativeMethods.IOCTL_DISK_GET_LENGTH_INFO, ReadOnlySpan<byte>.Empty, 8, out _);
        if (len != null && len.Length >= 8) length = Bin.I64(len, 0);
        if (length <= 0)
        {
            // Volume handles (\\.\C:) fall back to seeking.
            if (NativeMethods.SetFilePointerEx(_h, 0, out long end, 2)) length = end;
        }
        SectorSize = sector;
        Length = length;
        _buf = (byte*)NativeMemory.AlignedAlloc(MaxIo, 4096);
        Info = StorageInfo.Query(_h);
        Description = Info.Model is { Length: > 0 } ? $"{Info.Model} ({Format.Bytes(Length)}, {Info.BusTypeName})" : $"{devicePath} ({Format.Bytes(Length)})";
    }

    public string DevicePath { get; }
    public long Length { get; }
    public int SectorSize { get; }
    public string Description { get; }
    public bool CanWrite { get; }
    public StorageInfo Info { get; }
    public SafeFileHandle Handle => _h;

    public void ReadExact(long offset, Span<byte> buffer)
    {
        if (offset < 0 || offset + buffer.Length > Length)
            throw new ArgumentOutOfRangeException(nameof(offset), $"Read of {buffer.Length} at {offset} exceeds device length {Length}.");
        if (buffer.Length == 0) return;
        long start = Bin.AlignDown(offset, SectorSize);
        long end = Bin.AlignUp(offset + buffer.Length, SectorSize);
        lock (_lock)
        {
            ThrowIfDisposed();
            long pos = start;
            int outPos = 0;
            while (pos < end)
            {
                int chunk = (int)Math.Min(MaxIo, end - pos);
                if (!NativeMethods.SetFilePointerEx(_h, pos, out _, 0))
                    throw Win32("seek", pos);
                if (!NativeMethods.ReadFile(_h, _buf, (uint)chunk, out uint got, IntPtr.Zero))
                    throw Win32("read", pos);
                if (got != chunk)
                    throw new IOException($"Short read at {pos}: wanted {chunk}, got {got}.", NativeMethods.ERROR_SECTOR_NOT_FOUND);
                // Copy the part of this chunk that overlaps the requested range.
                long chunkStart = pos, chunkEnd = pos + chunk;
                long copyFrom = Math.Max(chunkStart, offset);
                long copyTo = Math.Min(chunkEnd, offset + buffer.Length);
                if (copyTo > copyFrom)
                {
                    int n = (int)(copyTo - copyFrom);
                    new ReadOnlySpan<byte>(_buf + (copyFrom - chunkStart), n).CopyTo(buffer.Slice(outPos, n));
                    outPos += n;
                }
                pos += chunk;
            }
        }
    }

    public void WriteExact(long offset, ReadOnlySpan<byte> buffer)
    {
        if (!CanWrite) throw new InvalidOperationException("Device opened read-only.");
        if (offset % SectorSize != 0 || buffer.Length % SectorSize != 0)
            throw new ArgumentException("Writes to a physical device must be sector aligned.");
        if (offset < 0 || offset + buffer.Length > Length) throw new ArgumentOutOfRangeException(nameof(offset));
        lock (_lock)
        {
            ThrowIfDisposed();
            long pos = offset;
            int inPos = 0;
            while (inPos < buffer.Length)
            {
                int chunk = Math.Min(MaxIo, buffer.Length - inPos);
                buffer.Slice(inPos, chunk).CopyTo(new Span<byte>(_buf, chunk));
                if (!NativeMethods.SetFilePointerEx(_h, pos, out _, 0)) throw Win32("seek", pos);
                if (!NativeMethods.WriteFile(_h, _buf, (uint)chunk, out uint put, IntPtr.Zero)) throw Win32("write", pos);
                if (put != chunk) throw new IOException($"Short write at {pos}.");
                pos += chunk;
                inPos += chunk;
            }
        }
    }

    public void Flush() { lock (_lock) NativeMethods.FlushFileBuffers(_h); }

    /// <summary>Cheap liveness probe: reads the first sector. Returns false if the device is gone.</summary>
    public bool Probe()
    {
        try
        {
            Span<byte> s = stackalloc byte[SectorSize];
            ReadExact(0, s);
            return true;
        }
        catch { return false; }
    }

    private static IOException Win32(string op, long pos)
    {
        int err = Marshal.GetLastWin32Error();
        return new IOException($"Device {op} failed at byte {pos}: {NativeMethods.ErrorText(err)}", err);
    }

    private void ThrowIfDisposed()
    {
        if (_buf == null) throw new ObjectDisposedException(nameof(WindowsPhysicalDrive));
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_buf != null) { NativeMemory.AlignedFree(_buf); _buf = null; }
            _h.Dispose();
        }
    }
}
