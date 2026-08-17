using FileRecovery.Core.FileSystems;
using FileRecovery.Core.Models;
using FileRecovery.Tests.TestSupport;
using Xunit;
using static FileRecovery.Tests.TestSupport.NtfsFixtureBuilder;

namespace FileRecovery.Tests;

public class NtfsMftParserTests
{
    private const int TotalClusters = 1000;
    private const int MftStartCluster = 4;

    [Fact]
    public void ScanDeletedEntries_FindsResidentFile_WithExactBytesAndExcellentRecoverability()
    {
        var jpegBytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', 0xFF, 0xD9 };
        var modified = new DateTime(2024, 3, 15, 10, 30, 0, DateTimeKind.Utc);

        var records = new List<RecordSpec>
        {
            new RecordSpec(), // record 0 = $MFT itself, content ignored by BuildVolume
            new RecordSpec { InUse = false, IsDirectory = false, FileName = "photo.jpg", Modified = modified, ResidentData = jpegBytes },
        };

        byte[] volume = BuildVolume(TotalClusters, MftStartCluster, records);
        using var reader = new MemoryRawReader(volume);
        var parser = new NtfsMftParser(reader);

        Assert.True(parser.TryReadBootSector(out var kind));
        Assert.Equal(FileSystemKind.NTFS, kind);

        var found = parser.ScanDeletedEntries(progress: null, CancellationToken.None, categoryFilter: null);

        var file = Assert.Single(found);
        Assert.Equal("photo.jpg", file.Name);
        Assert.Equal(jpegBytes.Length, file.SizeBytes);
        Assert.Equal(FileCategory.Photos, file.Category);
        Assert.NotNull(file.ResidentData);
        Assert.Equal(jpegBytes, file.ResidentData);
        Assert.Equal(Recoverability.Excellent, file.Recoverability);
        Assert.Equal(modified, file.ModifiedUtc);
        Assert.False(file.FromCarving);
        Assert.Empty(file.ClusterRuns);
    }

    [Fact]
    public void ScanDeletedEntries_FindsNonResidentFile_WithCorrectByteOffsetAndLength()
    {
        var records = new List<RecordSpec>
        {
            new RecordSpec(),
            new RecordSpec
            {
                InUse = false,
                IsDirectory = false,
                FileName = "movie.mp4",
                NonResidentRuns = new List<(long, long)> { (50, 3) }, // LCN 50, 3 clusters
                NonResidentRealSize = 12000, // less than 3*4096=12288, simulating a partially-used final cluster
            },
        };

        byte[] volume = BuildVolume(TotalClusters, MftStartCluster, records);
        using var reader = new MemoryRawReader(volume);
        var parser = new NtfsMftParser(reader);
        Assert.True(parser.TryReadBootSector(out _));

        var found = parser.ScanDeletedEntries(null, CancellationToken.None, null);

        var file = Assert.Single(found);
        Assert.Equal("movie.mp4", file.Name);
        Assert.Equal(FileCategory.Video, file.Category);
        Assert.Equal(12000, file.SizeBytes);
        var run = Assert.Single(file.ClusterRuns);
        Assert.Equal(50L * BytesPerCluster, run.ByteOffset);
        Assert.Equal(3L * BytesPerCluster, run.LengthBytes);
        Assert.Null(file.ResidentData);
        // No $Bitmap fixture is present in this synthetic volume, so the parser
        // correctly reports "Unknown" rather than guessing at reallocation.
        Assert.Equal(Recoverability.Unknown, file.Recoverability);
    }

    [Fact]
    public void ScanDeletedEntries_SkipsInUseAndDirectoryRecords_ButFindsTheDeletedFileAmongThem()
    {
        var records = new List<RecordSpec>
        {
            new RecordSpec(), // record 0 = $MFT
            new RecordSpec { InUse = true, IsDirectory = false, FileName = "still_here.docx", ResidentData = new byte[] { 1, 2, 3 } },
            new RecordSpec { InUse = false, IsDirectory = true, FileName = "deleted_folder" },
            new RecordSpec { InUse = false, IsDirectory = false, FileName = "keepme.txt", ResidentData = new byte[] { (byte)'h', (byte)'i' } },
        };

        byte[] volume = BuildVolume(TotalClusters, MftStartCluster, records);
        using var reader = new MemoryRawReader(volume);
        var parser = new NtfsMftParser(reader);
        Assert.True(parser.TryReadBootSector(out _));

        var found = parser.ScanDeletedEntries(null, CancellationToken.None, null);

        var file = Assert.Single(found);
        Assert.Equal("keepme.txt", file.Name);
    }

    [Fact]
    public void ScanDeletedEntries_RespectsCategoryFilter()
    {
        var records = new List<RecordSpec>
        {
            new RecordSpec(),
            new RecordSpec { InUse = false, FileName = "photo.jpg", ResidentData = new byte[] { 1 } },
            new RecordSpec { InUse = false, FileName = "notes.txt", ResidentData = new byte[] { 2 } },
        };

        byte[] volume = BuildVolume(TotalClusters, MftStartCluster, records);
        using var reader = new MemoryRawReader(volume);
        var parser = new NtfsMftParser(reader);
        Assert.True(parser.TryReadBootSector(out _));

        var found = parser.ScanDeletedEntries(null, CancellationToken.None, new HashSet<FileCategory> { FileCategory.Documents });

        var file = Assert.Single(found);
        Assert.Equal("notes.txt", file.Name);
    }
}
