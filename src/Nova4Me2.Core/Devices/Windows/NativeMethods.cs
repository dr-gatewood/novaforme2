using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Nova4Me2.Core.Devices.Windows;

[SupportedOSPlatform("windows")]
internal static partial class NativeMethods
{
    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_READ = 1;
    public const uint FILE_SHARE_WRITE = 2;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    public const uint FILE_FLAG_NO_BUFFERING = 0x20000000;

    public const uint IOCTL_DISK_GET_LENGTH_INFO = 0x0007405C;
    public const uint IOCTL_DISK_GET_DRIVE_GEOMETRY_EX = 0x000700A0;
    public const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
    public const uint IOCTL_STORAGE_GET_DEVICE_NUMBER = 0x002D1080;
    public const uint IOCTL_STORAGE_CHECK_VERIFY2 = 0x002D0800;
    public const uint IOCTL_DISK_GET_DISK_ATTRIBUTES = 0x000700F0;
    public const uint IOCTL_DISK_UPDATE_PROPERTIES = 0x00070140;
    public const uint FSCTL_LOCK_VOLUME = 0x00090018;
    public const uint FSCTL_UNLOCK_VOLUME = 0x0009001C;
    public const uint FSCTL_DISMOUNT_VOLUME = 0x00090020;
    public const uint FSCTL_ALLOW_EXTENDED_DASD_IO = 0x00090083;
    public const uint IOCTL_ATA_PASS_THROUGH = 0x0004D02C;
    public const uint IOCTL_SCSI_PASS_THROUGH_DIRECT = 0x0004D014;

    public const int ERROR_INVALID_FUNCTION = 1;
    public const int ERROR_FILE_NOT_FOUND = 2;
    public const int ERROR_PATH_NOT_FOUND = 3;
    public const int ERROR_ACCESS_DENIED = 5;
    public const int ERROR_INVALID_HANDLE = 6;
    public const int ERROR_NOT_READY = 21;
    public const int ERROR_BAD_COMMAND = 22;
    public const int ERROR_CRC = 23;
    public const int ERROR_SECTOR_NOT_FOUND = 27;
    public const int ERROR_GEN_FAILURE = 31;
    public const int ERROR_DEV_NOT_EXIST = 55;
    public const int ERROR_INSUFFICIENT_BUFFER = 122;
    public const int ERROR_MORE_DATA = 234;
    public const int ERROR_NO_SUCH_DEVICE = 433;
    public const int ERROR_IO_DEVICE = 1117;
    public const int ERROR_DEVICE_NOT_CONNECTED = 1167;
    public const int ERROR_DEVICE_REMOVED = 1617;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode, IntPtr lpInBuffer, uint nInBufferSize,
        IntPtr lpOutBuffer, uint nOutBufferSize, out uint lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetFilePointerEx(SafeFileHandle hFile, long liDistanceToMove, out long lpNewFilePointer, uint dwMoveMethod);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern unsafe bool ReadFile(SafeFileHandle hFile, byte* lpBuffer, uint nNumberOfBytesToRead, out uint lpNumberOfBytesRead, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern unsafe bool WriteFile(SafeFileHandle hFile, byte* lpBuffer, uint nNumberOfBytesToWrite, out uint lpNumberOfBytesWritten, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool FlushFileBuffers(SafeFileHandle hFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeFindVolumeHandle FindFirstVolumeW(System.Text.StringBuilder lpszVolumeName, uint cchBufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool FindNextVolumeW(SafeFindVolumeHandle hFindVolume, System.Text.StringBuilder lpszVolumeName, uint cchBufferLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool FindVolumeClose(IntPtr hFindVolume);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool GetVolumePathNamesForVolumeNameW(string lpszVolumeName, char[] lpszVolumePathNames, uint cchBufferLength, out uint lpcchReturnLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool GetVolumeInformationW(string lpRootPathName, System.Text.StringBuilder? lpVolumeNameBuffer, uint nVolumeNameSize,
        out uint lpVolumeSerialNumber, out uint lpMaximumComponentLength, out uint lpFileSystemFlags, System.Text.StringBuilder? lpFileSystemNameBuffer, uint nFileSystemNameSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint GetWindowsDirectoryW(System.Text.StringBuilder lpBuffer, uint uSize);

    public sealed class SafeFindVolumeHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeFindVolumeHandle() : base(true) { }
        protected override bool ReleaseHandle() => FindVolumeClose(handle);
    }

    public static bool IsDeviceGoneError(int err) => err is ERROR_FILE_NOT_FOUND or ERROR_PATH_NOT_FOUND or ERROR_INVALID_HANDLE
        or ERROR_NOT_READY or ERROR_BAD_COMMAND or ERROR_GEN_FAILURE or ERROR_DEV_NOT_EXIST or ERROR_NO_SUCH_DEVICE
        or ERROR_DEVICE_NOT_CONNECTED or ERROR_DEVICE_REMOVED or ERROR_INVALID_FUNCTION;

    public static string ErrorText(int err) => $"Win32 error {err}: {new System.ComponentModel.Win32Exception(err).Message}";

    /// <summary>Generic DeviceIoControl helper returning the output buffer (or null on failure, with lastError set).</summary>
    public static byte[]? Ioctl(SafeFileHandle h, uint code, ReadOnlySpan<byte> input, int outSize, out int lastError)
    {
        lastError = 0;
        IntPtr pin = IntPtr.Zero, pout = IntPtr.Zero;
        try
        {
            if (input.Length > 0) { pin = Marshal.AllocHGlobal(input.Length); input.CopyTo(AsSpan(pin, input.Length)); }
            if (outSize > 0) pout = Marshal.AllocHGlobal(outSize);
            if (!DeviceIoControl(h, code, pin, (uint)input.Length, pout, (uint)outSize, out uint ret, IntPtr.Zero))
            {
                lastError = Marshal.GetLastWin32Error();
                return null;
            }
            var result = new byte[Math.Min(ret, (uint)outSize)];
            if (result.Length > 0) Marshal.Copy(pout, result, 0, result.Length);
            return result;
        }
        finally
        {
            if (pin != IntPtr.Zero) Marshal.FreeHGlobal(pin);
            if (pout != IntPtr.Zero) Marshal.FreeHGlobal(pout);
        }
    }

    private static unsafe Span<byte> AsSpan(IntPtr p, int len) => new((void*)p, len);
}
