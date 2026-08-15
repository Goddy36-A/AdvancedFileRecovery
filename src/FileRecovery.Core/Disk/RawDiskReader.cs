using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FileRecovery.Core.Disk;

/// <summary>
/// Opens a physical drive (\\.\PhysicalDriveN) or a volume (\\.\C:) for
/// exclusively READ access and exposes sector-aligned reads.
///
/// SAFETY CONTRACT: this type never opens a handle with GENERIC_WRITE and
/// never calls WriteFile / DeviceIoControl write codes. It is the ONLY
/// class in the solution allowed to touch the source path string. Every
/// other component reads through this class.
/// </summary>
public sealed class RawDiskReader : IDisposable
{
    private readonly SafeFileHandle _handle;
    private readonly object _sync = new();

    public string DevicePath { get; }
    public int BytesPerSector { get; }
    public long TotalSizeBytes { get; }

    private RawDiskReader(string devicePath, SafeFileHandle handle, int bytesPerSector, long totalSize)
    {
        DevicePath = devicePath;
        _handle = handle;
        BytesPerSector = bytesPerSector <= 0 ? 512 : bytesPerSector;
        TotalSizeBytes = totalSize;
    }

    /// <summary>
    /// Opens a device path such as \\.\PhysicalDrive1 or \\.\D: for raw read-only access.
    /// Throws UnauthorizedAccessException if the process is not elevated / access is denied.
    /// </summary>
    public static RawDiskReader Open(string devicePath)
    {
        var handle = NativeMethods.CreateFile(
            devicePath,
            NativeMethods.GENERIC_READ,
            NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
            IntPtr.Zero,
            NativeMethods.OPEN_EXISTING,
            NativeMethods.FILE_FLAG_SEQUENTIAL_SCAN,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (err == 5 /*ACCESS_DENIED*/ || err == 32 /*SHARING_VIOLATION*/)
            {
                throw new UnauthorizedAccessException(
                    $"Could not open {devicePath} for raw read access (Win32 error {err}). " +
                    "This tool must be run as Administrator to read raw disks/volumes.");
            }
            throw new IOException($"Failed to open {devicePath} (Win32 error {err}).");
        }

        // Allow reading beyond the recognized filesystem bounds (needed on volume handles).
        NativeMethods.DeviceIoControl(handle, NativeMethods.FSCTL_ALLOW_EXTENDED_DASD_IO,
            IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);

        int bytesPerSector = QuerySectorSize(handle);
        long totalSize = QueryLength(handle);

        return new RawDiskReader(devicePath, handle, bytesPerSector, totalSize);
    }

    private static int QuerySectorSize(SafeFileHandle handle)
    {
        int size = Marshal.SizeOf<NativeMethods.DISK_GEOMETRY_EX>();
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            if (NativeMethods.DeviceIoControl(handle, NativeMethods.IOCTL_DISK_GET_DRIVE_GEOMETRY_EX,
                    IntPtr.Zero, 0, buf, (uint)size, out _, IntPtr.Zero))
            {
                var geo = Marshal.PtrToStructure<NativeMethods.DISK_GEOMETRY_EX>(buf);
                return geo.Geometry.BytesPerSector;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
        return 512;
    }

    private static long QueryLength(SafeFileHandle handle)
    {
        int size = Marshal.SizeOf<long>();
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            if (NativeMethods.DeviceIoControl(handle, NativeMethods.IOCTL_DISK_GET_LENGTH_INFO,
                    IntPtr.Zero, 0, buf, (uint)size, out _, IntPtr.Zero))
            {
                return Marshal.ReadInt64(buf);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
        return 0;
    }

    /// <summary>
    /// Reads <paramref name="count"/> bytes starting at absolute byte offset
    /// <paramref name="offset"/>. Offset and count are internally rounded
    /// out to sector boundaries as raw device handles require aligned I/O.
    /// Thread-safe (serialized) so multiple parsers can share one reader.
    /// </summary>
    public byte[] ReadBytes(long offset, int count)
    {
        lock (_sync)
        {
            long alignedStart = (offset / BytesPerSector) * BytesPerSector;
            long endOffset = offset + count;
            long alignedEnd = ((endOffset + BytesPerSector - 1) / BytesPerSector) * BytesPerSector;
            int alignedLength = (int)(alignedEnd - alignedStart);

            byte[] buffer = new byte[alignedLength];
            int totalRead = 0;
            while (totalRead < alignedLength)
            {
                int read = System.IO.RandomAccess.Read(_handle, buffer.AsSpan(totalRead), alignedStart + totalRead);
                if (read <= 0) break;
                totalRead += read;
            }

            int frontTrim = (int)(offset - alignedStart);
            if (frontTrim == 0 && alignedLength == count)
                return buffer;

            byte[] result = new byte[count];
            int available = Math.Max(0, totalRead - frontTrim);
            Array.Copy(buffer, frontTrim, result, 0, Math.Min(count, available));
            return result;
        }
    }

    public void Dispose()
    {
        if (!_handle.IsClosed) _handle.Dispose();
    }
}
