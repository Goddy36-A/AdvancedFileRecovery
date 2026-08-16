using FileRecovery.Core.Disk;
using FileRecovery.Core.FileSystems;
using FileRecovery.Core.Carving;
using FileRecovery.Core.Models;

namespace FileRecovery.Core.Recovery;

public sealed class RecoveryEngine
{
    /// <summary>
    /// Runs a scan (Quick or Deep) against the given volume/drive and returns
    /// everything discovered. The RawDiskReader opened here is strictly
    /// read-only for the lifetime of the scan.
    /// </summary>
    public List<RecoverableFile> Scan(ScanOptions options, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        using var reader = RawDiskReader.Open(options.Volume.DevicePath);

        if (options.Type == ScanType.Quick)
        {
            var ntfs = new NtfsMftParser(reader);
            if (ntfs.TryReadBootSector(out _))
            {
                options.Volume.FileSystem = FileSystemKind.NTFS;
                options.Volume.ClusterSizeBytes = ntfs.BytesPerCluster;
                return ntfs.ScanDeletedEntries(progress, ct, options.CategoryFilter);
            }

            var fat32 = new Fat32Parser(reader);
            if (fat32.TryReadBootSector())
            {
                options.Volume.FileSystem = FileSystemKind.FAT32;
                options.Volume.ClusterSizeBytes = fat32.BytesPerCluster;
                return fat32.ScanDeletedEntries(progress, ct, options.CategoryFilter);
            }

            var exfat = new ExFatParser(reader);
            if (exfat.TryReadBootSector())
            {
                options.Volume.FileSystem = FileSystemKind.exFAT;
                options.Volume.ClusterSizeBytes = exfat.BytesPerCluster;
                return exfat.ScanDeletedEntries(progress, ct, options.CategoryFilter);
            }

            // No recognizable filesystem metadata (e.g. RAW/formatted volume) —
            // Quick Scan has nothing to index; caller should suggest Deep Scan.
            return new List<RecoverableFile>();
        }
        else
        {
            var carver = new SignatureCarver(reader);
            long size = reader.TotalSizeBytes > 0 ? reader.TotalSizeBytes : options.Volume.TotalSizeBytes;
            return carver.Carve(0, size, progress, ct, options.CategoryFilter);
        }
    }

    /// <summary>
    /// Copies the selected recovered files to <paramref name="destinationFolder"/>.
    /// Throws InvalidOperationException if the destination resolves to the same
    /// physical device as the source — this is a hard safety gate, not just a UI warning.
    /// </summary>
    public RecoveryResult Recover(VolumeInfo source, IEnumerable<RecoverableFile> files, string destinationFolder,
        IProgress<RecoveryProgress>? progress, CancellationToken ct)
    {
        if (DestinationSafety.IsSameDevice(source, destinationFolder))
        {
            throw new InvalidOperationException(
                "The recovery destination is on the same drive being scanned. " +
                "Choose a different physical drive to avoid overwriting recoverable data.");
        }

        Directory.CreateDirectory(destinationFolder);
        var list = files.ToList();
        int succeeded = 0;
        var failures = new List<(RecoverableFile, string)>();

        using var reader = RawDiskReader.Open(source.DevicePath);

        for (int i = 0; i < list.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var file = list[i];
            try
            {
                string safeName = MakeSafeFileName(file.Name, file.Id);
                string destPath = Path.Combine(destinationFolder, safeName);
                destPath = DisambiguateIfExists(destPath);

                using (var outStream = new FileStream(destPath, FileMode.Create, FileAccess.Write))
                {
                    if (file.ResidentData != null)
                    {
                        outStream.Write(file.ResidentData, 0, file.ResidentData.Length);
                    }
                    else if (file.FromCarving)
                    {
                        CopyRange(reader, outStream, file.CarveOffset, file.CarveLength);
                    }
                    else if (file.ClusterRuns.Count > 0)
                    {
                        long remaining = file.SizeBytes;
                        foreach (var run in file.ClusterRuns)
                        {
                            long runBytes = Math.Min(run.LengthBytes, remaining);
                            if (runBytes <= 0) break;
                            CopyRange(reader, outStream, run.ByteOffset, runBytes);
                            remaining -= runBytes;
                        }
                    }
                    else
                    {
                        throw new InvalidOperationException("File has no recoverable data location.");
                    }
                }
                succeeded++;
            }
            catch (Exception ex)
            {
                failures.Add((file, ex.Message));
            }

            progress?.Report(new RecoveryProgress
            {
                FilesDone = i + 1,
                FilesTotal = list.Count,
                CurrentFileName = file.Name,
                PercentComplete = (i + 1) * 100.0 / Math.Max(1, list.Count),
            });
        }

        return new RecoveryResult
        {
            SucceededCount = succeeded,
            FailedCount = failures.Count,
            Failures = failures,
            DestinationFolder = destinationFolder,
        };
    }

    private static void CopyRange(RawDiskReader reader, FileStream outStream, long offset, long length)
    {
        const int bufSize = 4 * 1024 * 1024;
        long remaining = length;
        long pos = offset;
        while (remaining > 0)
        {
            int chunk = (int)Math.Min(bufSize, remaining);
            byte[] data = reader.ReadBytes(pos, chunk);
            outStream.Write(data, 0, Math.Min(data.Length, chunk));
            pos += chunk;
            remaining -= chunk;
        }
    }

    private static string MakeSafeFileName(string name, string fallbackId)
    {
        string cleaned = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return string.IsNullOrWhiteSpace(cleaned) ? $"recovered_{fallbackId}" : cleaned;
    }

    private static string DisambiguateIfExists(string path)
    {
        if (!File.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path)!;
        string name = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        int n = 1;
        string candidate;
        do
        {
            candidate = Path.Combine(dir, $"{name} ({n}){ext}");
            n++;
        } while (File.Exists(candidate));
        return candidate;
    }
}
