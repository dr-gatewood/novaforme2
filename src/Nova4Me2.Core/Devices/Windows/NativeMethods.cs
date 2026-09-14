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
    public const uint FILE_FLAG_OVERLAPPED = 0x40000000;
    public const int ERROR_IO_PENDING = 997;
    public const int ERROR_OPERATION_ABORTED = 995;
    public const int ERROR_SEM_TIMEOUT = 121;
    public const uint WAIT_TIMEOUT = 258;
    public const uint IOCTL_DISK_SET_DISK_ATTRIBUTES = 0x0007C0F4;
    public const uint IOCTL_DISK_GET_DISK_ATTRIBUTES_EX = 0x000700F0;
    /// <summary>Default time-out for control requests to a device (a hung USB bridge otherwise blocks for the full disk time-out).</summary>
    public static int IoctlTimeoutMs = 15000;

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

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern unsafe bool ReadFile(SafeFileHandle hFile, byte* lpBuffer, uint nNumberOfBytesToRead, IntPtr lpNumberOfBytesRead, NativeOverlapped* lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern unsafe bool WriteFile(SafeFileHandle hFile, byte* lpBuffer, uint nNumberOfBytesToWrite, IntPtr lpNumberOfBytesWritten, NativeOverlapped* lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern unsafe bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode, IntPtr lpInBuffer, uint nInBufferSize,
        IntPtr lpOutBuffer, uint nOutBufferSize, IntPtr lpBytesReturned, NativeOverlapped* lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern unsafe bool GetOverlappedResult(SafeFileHandle hFile, NativeOverlapped* lpOverlapped, out uint lpNumberOfBytesTransferred, bool bWait);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern unsafe bool CancelIoEx(SafeFileHandle hFile, NativeOverlapped* lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr CreateEventW(IntPtr lpEventAttributes, bool bManualReset, bool bInitialState, IntPtr lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);

    /// <summary>
    /// Runs one overlapped I/O request with a hard time-out. Works for handles opened with or without FILE_FLAG_OVERLAPPED
    /// (without it the call simply completes synchronously). On time-out the request is cancelled and an IOException with
    /// ERROR_SEM_TIMEOUT is thrown, so a hung USB bridge can never block a thread for longer than <paramref name="timeoutMs"/>.
    /// </summary>
    public static unsafe uint RunOverlapped(SafeFileHandle h, long offset, int timeoutMs, string what, Func<IntPtr, bool> issue)
    {
        NativeOverlapped ov = default;
        ov.OffsetLow = (int)(offset & 0xFFFFFFFF);
        ov.OffsetHigh = (int)(offset >> 32);
        ov.EventHandle = CreateEventW(IntPtr.Zero, true, false, IntPtr.Zero);
        if (ov.EventHandle == IntPtr.Zero) throw new IOException("CreateEvent failed", Marshal.GetLastWin32Error());
        try
        {
            NativeOverlapped* p = &ov;
            bool ok = issue((IntPtr)p);
            if (!ok)
            {
                int err = Marshal.GetLastWin32Error();
                if (err != ERROR_IO_PENDING) throw new IOException($"{what} failed: {ErrorText(err)}", err);
                uint w = WaitForSingleObject(ov.EventHandle, timeoutMs <= 0 ? 0xFFFFFFFF : (uint)timeoutMs);
                if (w == WAIT_TIMEOUT)
                {
                    CancelIoEx(h, p);
                    GetOverlappedResult(h, p, out _, true); // wait for the cancellation to land before the stack frame goes away
                    throw new IOException($"{what} timed out after {timeoutMs} ms (device not responding).", ERROR_SEM_TIMEOUT);
                }
            }
            if (!GetOverlappedResult(h, p, out uint transferred, true))
            {
                int err = Marshal.GetLastWin32Error();
                throw new IOException($"{what} failed: {ErrorText(err)}", err);
            }
            return transferred;
        }
        finally { CloseHandle(ov.EventHandle); }
    }

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

    /// <summary>Generic DeviceIoControl helper with a time-out; returns the output buffer (or null on failure, with lastError set).</summary>
    public static unsafe byte[]? Ioctl(SafeFileHandle h, uint code, ReadOnlySpan<byte> input, int outSize, out int lastError, int? timeoutMs = null)
    {
        lastError = 0;
        IntPtr pin = IntPtr.Zero, pout = IntPtr.Zero;
        try
        {
            if (input.Length > 0) { pin = Marshal.AllocHGlobal(input.Length); input.CopyTo(AsSpan(pin, input.Length)); }
            if (outSize > 0) pout = Marshal.AllocHGlobal(outSize);
            uint ret;
            try
            {
                int inLen = input.Length;
                ret = RunOverlapped(h, 0, timeoutMs ?? IoctlTimeoutMs, $"IOCTL 0x{code:X}", ovp => DeviceIoControl(h, code, pin, (uint)inLen, pout, (uint)outSize, IntPtr.Zero, (NativeOverlapped*)ovp));
            }
            catch (IOException ex) { lastError = ex.HResult; return null; }
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
