using System.Management;
using FileRecovery.Core.Models;

namespace FileRecovery.Core.Disk;

/// <summary>
/// Lists the drives/volumes a user can pick as a scan source, including
/// volumes that Windows can no longer mount a filesystem on (raw/formatted/
/// corrupted), since those are exactly the ones Deep Scan targets.
/// </summary>
public static class VolumeEnumerator
{
    public static List<VolumeInfo> EnumerateVolumes()
    {
        var results = new List<VolumeInfo>();

        // 1) Logical volumes with a live drive letter (works for Quick Scan on healthy filesystems).
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;

            bool ready = drive.IsReady;
            string letter = drive.Name.TrimEnd('\\');
            string devicePath = $@"\\.\{letter}";

            var info = new VolumeInfo
            {
                DevicePath = devicePath,
                DisplayName = ready
                    ? $"{letter} ({(string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "Local Disk" : drive.VolumeLabel)})"
                    : $"{letter} (No filesystem detected)",
                Label = ready ? drive.VolumeLabel : null,
                FileSystem = ready ? ParseFileSystem(drive.DriveFormat) : FileSystemKind.Unknown,
                TotalSizeBytes = ready ? drive.TotalSize : 0,
                FreeSizeBytes = ready ? drive.AvailableFreeSpace : 0,
                IsRemovable = drive.DriveType == DriveType.Removable,
                IsPhysicalDrive = false,
            };
            results.Add(info);
        }

        // 2) Raw physical drives (needed to scan unpartitioned / RAW disks, and for deep, whole-disk carving).
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_DiskDrive");
            foreach (ManagementObject disk in searcher.Get())
            {
                string index = disk["Index"]?.ToString() ?? "0";
                string model = disk["Model"]?.ToString() ?? "Unknown Drive";
                long size = disk["Size"] != null ? Convert.ToInt64(disk["Size"]) : 0;
                bool removable = (disk["MediaType"]?.ToString() ?? "").Contains("Removable", StringComparison.OrdinalIgnoreCase)
                                  || (disk["InterfaceType"]?.ToString() ?? "").Equals("USB", StringComparison.OrdinalIgnoreCase);

                results.Add(new VolumeInfo
                {
                    DevicePath = $@"\\.\PhysicalDrive{index}",
                    DisplayName = $"Disk {index}: {model} ({FormatSize(size)})",
                    FileSystem = FileSystemKind.Unknown,
                    TotalSizeBytes = size,
                    FreeSizeBytes = 0,
                    IsRemovable = removable,
                    IsPhysicalDrive = true,
                });
            }
        }
        catch (ManagementException)
        {
            // WMI unavailable (rare) — logical volumes above still let the user proceed.
        }

        return results;
    }

    private static FileSystemKind ParseFileSystem(string format) => format.ToUpperInvariant() switch
    {
        "NTFS" => FileSystemKind.NTFS,
        "FAT32" => FileSystemKind.FAT32,
        "EXFAT" => FileSystemKind.exFAT,
        "REFS" => FileSystemKind.ReFS,
        _ => FileSystemKind.Unknown,
    };

    private static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return $"{size:0.#} {units[unit]}";
    }
}
