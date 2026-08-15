using System.Management;
using FileRecovery.Core.Models;

namespace FileRecovery.Core.Recovery;

/// <summary>
/// Determines whether a chosen recovery destination folder lives on the same
/// physical disk as the volume/device being scanned. This is enforced as a
/// hard block in RecoveryEngine, not just a UI hint, because writing
/// recovered files back onto the source drive can overwrite the very data
/// the user is trying to recover.
/// </summary>
public static class DestinationSafety
{
    public static bool IsSameDevice(VolumeInfo source, string destinationFolder)
    {
        try
        {
            string? destRoot = Path.GetPathRoot(Path.GetFullPath(destinationFolder));
            if (string.IsNullOrEmpty(destRoot)) return false;

            int? sourcePhysicalIndex = ResolvePhysicalDiskIndex(source);
            int? destPhysicalIndex = ResolvePhysicalDiskIndexForDriveLetter(destRoot.TrimEnd('\\'));

            if (sourcePhysicalIndex.HasValue && destPhysicalIndex.HasValue)
                return sourcePhysicalIndex.Value == destPhysicalIndex.Value;

            // Fallback string comparison if WMI lookups failed for any reason —
            // err on the side of caution (treat unresolvable as "same" only
            // when the literal device paths match).
            string sourceLetter = source.DevicePath.TrimStart('\\', '.').TrimEnd(':').ToUpperInvariant();
            string destLetter = destRoot.TrimEnd('\\', ':').ToUpperInvariant();
            return sourceLetter.Length == 1 && sourceLetter == destLetter;
        }
        catch
        {
            return false; // never block recovery on an unexpected error here — RecoveryEngine still shows the persistent UI warning
        }
    }

    private static int? ResolvePhysicalDiskIndex(VolumeInfo volume)
    {
        if (volume.IsPhysicalDrive)
        {
            string idxStr = volume.DevicePath.Replace(@"\\.\PhysicalDrive", "");
            return int.TryParse(idxStr, out int idx) ? idx : null;
        }

        string driveLetter = volume.DevicePath.Replace(@"\\.\", "").TrimEnd(':');
        return ResolvePhysicalDiskIndexForDriveLetter(driveLetter);
    }

    private static int? ResolvePhysicalDiskIndexForDriveLetter(string driveLetterNoColon)
    {
        string letter = driveLetterNoColon.TrimEnd(':');
        try
        {
            using var partitionSearcher = new ManagementObjectSearcher(
                $"ASSOCIATORS OF {{Win32_LogicalDisk.DeviceID='{letter}:'}} WHERE AssocClass=Win32_LogicalDiskToPartition");
            foreach (ManagementObject partition in partitionSearcher.Get())
            {
                using var diskSearcher = new ManagementObjectSearcher(
                    $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{partition["DeviceID"]}'}} WHERE AssocClass=Win32_DiskDriveToDiskPartition");
                foreach (ManagementObject disk in diskSearcher.Get())
                {
                    string model = disk["DeviceID"]?.ToString() ?? "";
                    var m = System.Text.RegularExpressions.Regex.Match(model, @"PHYSICALDRIVE(\d+)",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (m.Success) return int.Parse(m.Groups[1].Value);
                }
            }
        }
        catch (ManagementException)
        {
            return null;
        }
        return null;
    }
}
