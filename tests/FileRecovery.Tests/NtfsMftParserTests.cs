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

    // --- $Bitmap-based recoverability scoring ---
    // Record 6 is always $Bitmap regardless of how many "real" file records
    // come before it, so these tests pad records 1-5 with empty placeholders
    // (BuildRawRecord writes a valid but attribute-less FILE record for
    // those — ParseAttributes correctly returns null for them, same pattern
    // already used for record 0 in every test above).
    private const long BitmapDataLcn = 20;

    private static List<RecordSpec> Fillers(int count) => Enumerable.Range(0, count).Select(_ => new RecordSpec()).ToList();

    private static RecordSpec BitmapRecord(long dataLcn = BitmapDataLcn, long clusterCount = 1) => new()
    {
        InUse = true,
        NonResidentRuns = new List<(long, long)> { (dataLcn, clusterCount) },
        NonResidentRealSize = clusterCount * BytesPerCluster,
    };

    private static void MarkClusterAllocated(byte[] volume, long bitmapDataLcn, long cluster)
    {
        long bitmapDataOffset = bitmapDataLcn * BytesPerCluster;
        long byteIndex = cluster / 8;
        int bitIndex = (int)(cluster % 8);
        volume[bitmapDataOffset + byteIndex] |= (byte)(1 << bitIndex);
    }

    [Fact]
    public void ScanDeletedEntries_WithBitmapPresent_AllClustersFree_IsExcellent()
    {
        var records = new List<RecordSpec> { new RecordSpec() };
        records.AddRange(new[]
        {
            new RecordSpec { InUse = false, FileName = "video.mp4", NonResidentRuns = new List<(long, long)> { (50, 3) }, NonResidentRealSize = 12000 },
        });
        records.AddRange(Fillers(4)); // records 2-5
        records.Add(BitmapRecord()); // record 6

        byte[] volume = BuildVolume(TotalClusters, MftStartCluster, records);
        // Bitmap bytes at BitmapDataLcn are left all-zero (default) => every queried cluster reads as free.

        using var reader = new MemoryRawReader(volume);
        var parser = new NtfsMftParser(reader);
        Assert.True(parser.TryReadBootSector(out _));

        var found = parser.ScanDeletedEntries(null, CancellationToken.None, null);

        var file = Assert.Single(found, f => f.Name == "video.mp4");
        Assert.Equal(Recoverability.Excellent, file.Recoverability);
    }

    [Fact]
    public void ScanDeletedEntries_WithBitmapPresent_AllClustersReallocated_IsPoor()
    {
        var records = new List<RecordSpec> { new RecordSpec() };
        records.Add(new RecordSpec { InUse = false, FileName = "video.mp4", NonResidentRuns = new List<(long, long)> { (50, 3) }, NonResidentRealSize = 12000 });
        records.AddRange(Fillers(4));
        records.Add(BitmapRecord());

        byte[] volume = BuildVolume(TotalClusters, MftStartCluster, records);
        MarkClusterAllocated(volume, BitmapDataLcn, 50);
        MarkClusterAllocated(volume, BitmapDataLcn, 51);
        MarkClusterAllocated(volume, BitmapDataLcn, 52);

        using var reader = new MemoryRawReader(volume);
        var parser = new NtfsMftParser(reader);
        Assert.True(parser.TryReadBootSector(out _));

        var found = parser.ScanDeletedEntries(null, CancellationToken.None, null);

        var file = Assert.Single(found, f => f.Name == "video.mp4");
        Assert.Equal(Recoverability.Poor, file.Recoverability);
    }

    [Fact]
    public void ScanDeletedEntries_WithBitmapPresent_OneOfFiveClustersReallocated_IsPartial()
    {
        var records = new List<RecordSpec> { new RecordSpec() };
        records.Add(new RecordSpec { InUse = false, FileName = "bigfile.zip", NonResidentRuns = new List<(long, long)> { (50, 5) }, NonResidentRealSize = 5 * BytesPerCluster });
        records.AddRange(Fillers(4));
        records.Add(BitmapRecord());

        byte[] volume = BuildVolume(TotalClusters, MftStartCluster, records);
        MarkClusterAllocated(volume, BitmapDataLcn, 50); // 1 of 5 => 20%, under the 25% Partial threshold

        using var reader = new MemoryRawReader(volume);
        var parser = new NtfsMftParser(reader);
        Assert.True(parser.TryReadBootSector(out _));

        var found = parser.ScanDeletedEntries(null, CancellationToken.None, null);

        var file = Assert.Single(found, f => f.Name == "bigfile.zip");
        Assert.Equal(Recoverability.Partial, file.Recoverability);
    }

    [Fact]
    public void ScanDeletedEntries_WithoutBitmapRecord_FallsBackToUnknown()
    {
        // No record 6 at all (records.Count stops at 2) — LoadBitmapRunsBestEffort
        // should fail gracefully rather than throw, and scoring should say Unknown
        // rather than silently guessing.
        var records = new List<RecordSpec>
        {
            new RecordSpec(),
            new RecordSpec { InUse = false, FileName = "video.mp4", NonResidentRuns = new List<(long, long)> { (50, 3) }, NonResidentRealSize = 12000 },
        };

        byte[] volume = BuildVolume(TotalClusters, MftStartCluster, records);
        using var reader = new MemoryRawReader(volume);
        var parser = new NtfsMftParser(reader);
        Assert.True(parser.TryReadBootSector(out _));

        var found = parser.ScanDeletedEntries(null, CancellationToken.None, null);

        var file = Assert.Single(found);
        Assert.Equal(Recoverability.Unknown, file.Recoverability);
    }
}
