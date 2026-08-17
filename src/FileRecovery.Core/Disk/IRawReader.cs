namespace FileRecovery.Core.Disk;

/// <summary>
/// The minimal read surface every filesystem parser and the carver need from
/// a "disk". <see cref="RawDiskReader"/> implements this against a real Win32
/// device handle in production; tests implement it against an in-memory byte
/// buffer representing a synthetic disk image, so the actual parsing logic
/// (NTFS $MFT records, FAT32/exFAT directory entries, signature carving) can
/// be exercised deterministically without a Windows machine, admin rights,
/// or a real disk.
/// </summary>
public interface IRawReader : IDisposable
{
    int BytesPerSector { get; }
    long TotalSizeBytes { get; }

    /// <summary>
    /// Reads <paramref name="count"/> bytes starting at absolute byte offset
    /// <paramref name="offset"/>. Implementations must always return an array
    /// of exactly <paramref name="count"/> bytes (zero-padded if the read
    /// extends past the end of the underlying data), matching
    /// <see cref="RawDiskReader"/>'s behavior against a real device.
    /// </summary>
    byte[] ReadBytes(long offset, int count);
}
