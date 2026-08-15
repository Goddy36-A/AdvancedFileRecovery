namespace FileRecovery.Core.Models;

public enum ScanType { Quick, Deep }

public enum FileSystemKind { Unknown, NTFS, FAT32, exFAT, ReFS }

public enum FileCategory { Photos, Documents, Video, Audio, Archives, Other }

public enum Recoverability
{
    /// <summary>Metadata + all data clusters still unallocated -> high confidence.</summary>
    Excellent,
    /// <summary>Some fragmentation or partial cluster reuse detected.</summary>
    Partial,
    /// <summary>Data clusters confirmed reallocated -> unlikely to recover intact.</summary>
    Poor,
    Unknown
}

/// <summary>A drive/partition/volume the user can pick to scan.</summary>
public sealed class VolumeInfo
{
    public required string DevicePath { get; init; }      // e.g. \\.\D: or \\.\PhysicalDrive1
    public required string DisplayName { get; init; }      // e.g. "D: (USB_DRIVE)"
    public string? Label { get; init; }
    public FileSystemKind FileSystem { get; set; } = FileSystemKind.Unknown;
    public int ClusterSizeBytes { get; set; } // filled in once the boot sector is parsed during scan
    public long TotalSizeBytes { get; init; }
    public long FreeSizeBytes { get; init; }
    public bool IsRemovable { get; init; }
    public bool IsPhysicalDrive { get; init; }
}

/// <summary>
/// One contiguous data run describing where a file's bytes physically live,
/// expressed as an ABSOLUTE byte offset from the start of the device/volume
/// that was opened for reading (not a raw filesystem cluster number — NTFS,
/// FAT32, and exFAT each number clusters relative to different bases, so
/// each parser resolves to this common, unambiguous representation).
/// </summary>
public readonly record struct ClusterRun(long ByteOffset, long LengthBytes);

/// <summary>A single recoverable item, whether discovered via metadata parsing or raw carving.</summary>
public sealed class RecoverableFile
{
    public required string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required string Name { get; set; }
    public string? OriginalPath { get; set; }
    public long SizeBytes { get; set; }
    public FileCategory Category { get; set; } = FileCategory.Other;
    public string Extension { get; set; } = "";
    public DateTime? ModifiedUtc { get; set; }
    public Recoverability Recoverability { get; set; } = Recoverability.Unknown;
    public bool IsSelected { get; set; }

    /// <summary>Discovery source: filesystem metadata (Quick Scan) vs raw signature carve (Deep Scan).</summary>
    public bool FromCarving { get; set; }

    // For metadata-based recovery (NTFS/FAT/exFAT): the physical cluster runs holding the data.
    public List<ClusterRun> ClusterRuns { get; } = new();

    // For carved files: a single contiguous byte range on the raw device.
    public long CarveOffset { get; set; }
    public long CarveLength { get; set; }

    public byte[]? ThumbnailBytes { get; set; }
}

public sealed class ScanOptions
{
    public required VolumeInfo Volume { get; init; }
    public ScanType Type { get; init; }
    public HashSet<FileCategory> CategoryFilter { get; init; } = Enum.GetValues<FileCategory>().ToHashSet();
}

public sealed class ScanProgress
{
    public double PercentComplete { get; init; }
    public long BytesProcessed { get; init; }
    public long TotalBytes { get; init; }
    public TimeSpan Elapsed { get; init; }
    public TimeSpan? EstimatedRemaining { get; init; }
    public IReadOnlyDictionary<FileCategory, int> CountsByCategory { get; init; } = new Dictionary<FileCategory, int>();
    public int TotalFound { get; init; }
    public string StatusText { get; init; } = "";
}

public sealed class RecoveryProgress
{
    public int FilesDone { get; init; }
    public int FilesTotal { get; init; }
    public string CurrentFileName { get; init; } = "";
    public double PercentComplete { get; init; }
}

public sealed class RecoveryResult
{
    public int SucceededCount { get; init; }
    public int FailedCount { get; init; }
    public List<(RecoverableFile File, string Error)> Failures { get; init; } = new();
    public string DestinationFolder { get; init; } = "";
}
