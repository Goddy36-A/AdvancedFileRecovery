using FileRecovery.Core.FileSystems;
using FileRecovery.Core.Models;
using FileRecovery.Tests.TestSupport;
using Xunit;
using static FileRecovery.Tests.TestSupport.Fat32FixtureBuilder;

namespace FileRecovery.Tests;

public class Fat32ParserTests
{
    private const int TotalClusters = 50;

    [Fact]
    public void ScanDeletedEntries_FindsDeletedEntryWithLongFileName_AndCorrectClusterOffset()
    {
        var entries = new List<DirEntryBlock>
        {
            DeletedLongNameEntry("vacation photo march.jpg", size: 100, startCluster: 10),
            EndMarker(),
        };
        byte[] volume = BuildVolume(TotalClusters, entries);
        using var reader = new MemoryRawReader(volume);
        var parser = new Fat32Parser(reader);

        Assert.True(parser.TryReadBootSector());
        var found = parser.ScanDeletedEntries(null, CancellationToken.None, null);

        var file = Assert.Single(found);
        Assert.Equal("vacation photo march.jpg", file.Name);
        Assert.Equal(100, file.SizeBytes);
        Assert.Equal(FileCategory.Photos, file.Category);
        var run = Assert.Single(file.ClusterRuns);
        long expectedDataAreaOffset = (ReservedSectors + (long)NumFats * SectorsPerFat) * BytesPerSector;
        long expectedOffset = expectedDataAreaOffset + (10 - 2) * BytesPerCluster;
        Assert.Equal(expectedOffset, run.ByteOffset);
        Assert.Equal(BytesPerCluster, run.LengthBytes); // 100 bytes rounds up to one cluster
    }

    [Fact]
    public void ScanDeletedEntries_SkipsLiveEntries()
    {
        var entries = new List<DirEntryBlock>
        {
            ShortEntry("STILL", size: 50, startCluster: 5, deleted: false),
            DeletedLongNameEntry("gone.pdf", size: 200, startCluster: 6),
            EndMarker(),
        };
        byte[] volume = BuildVolume(TotalClusters, entries);
        using var reader = new MemoryRawReader(volume);
        var parser = new Fat32Parser(reader);
        Assert.True(parser.TryReadBootSector());

        var found = parser.ScanDeletedEntries(null, CancellationToken.None, null);

        var file = Assert.Single(found);
        Assert.Equal("gone.pdf", file.Name);
        Assert.Equal(FileCategory.Documents, file.Category);
    }

    [Fact]
    public void ScanDeletedEntries_ShortNameOnlyDeletedEntry_IsStillFoundWithCorrectSizeAndCluster()
    {
        // No LFN sub-entries — exercises the fallback path where only the mangled
        // 8.3 name survives (real FAT behavior: deletion permanently overwrites
        // the short name's first character, which the parser flags with a "_" prefix).
        var entries = new List<DirEntryBlock>
        {
            ShortEntry("AFILE.TXT", size: 42, startCluster: 8, deleted: true),
            EndMarker(),
        };
        byte[] volume = BuildVolume(TotalClusters, entries);
        using var reader = new MemoryRawReader(volume);
        var parser = new Fat32Parser(reader);
        Assert.True(parser.TryReadBootSector());

        var found = parser.ScanDeletedEntries(null, CancellationToken.None, null);

        var file = Assert.Single(found);
        Assert.StartsWith("_", file.Name);
        Assert.EndsWith("FILE.TXT", file.Name);
        Assert.Equal(42, file.SizeBytes);
    }

    [Fact]
    public void ScanDeletedEntries_SkipsVolumeLabelEntries_EvenIfDeleted()
    {
        // A deleted volume-label entry must still be excluded — this exercises
        // the isVolumeLabel guard specifically, not just the "not deleted" path.
        var entries = new List<DirEntryBlock>
        {
            ShortEntry("MYUSBDRV", size: 0, startCluster: 0, deleted: true, isVolumeLabel: true),
            DeletedLongNameEntry("real_file.zip", size: 10, startCluster: 9),
            EndMarker(),
        };
        byte[] volume = BuildVolume(TotalClusters, entries);
        using var reader = new MemoryRawReader(volume);
        var parser = new Fat32Parser(reader);
        Assert.True(parser.TryReadBootSector());

        var found = parser.ScanDeletedEntries(null, CancellationToken.None, null);

        var file = Assert.Single(found);
        Assert.Equal("real_file.zip", file.Name);
    }

    [Fact]
    public void ScanDeletedEntries_RealJpegBytesAtAssumedLocation_ScoresExcellent()
    {
        byte[] jpeg = { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0x00, 0xFF, 0xD9 };
        var entries = new List<DirEntryBlock> { DeletedLongNameEntry("photo.jpg", size: (uint)jpeg.Length, startCluster: 10) };
        byte[] volume = BuildVolume(TotalClusters, entries);

        long dataAreaOffset = (ReservedSectors + (long)NumFats * SectorsPerFat) * BytesPerSector;
        long dataOffset = dataAreaOffset + (10 - 2) * BytesPerCluster;
        Array.Copy(jpeg, 0, volume, dataOffset, jpeg.Length);

        using var reader = new MemoryRawReader(volume);
        var parser = new Fat32Parser(reader);
        Assert.True(parser.TryReadBootSector());

        var found = parser.ScanDeletedEntries(null, CancellationToken.None, null);

        var file2 = Assert.Single(found);
        Assert.Equal(Recoverability.Excellent, file2.Recoverability);
    }

    [Fact]
    public void ScanDeletedEntries_GarbageAtAssumedLocation_ScoresPoorInsteadOfOptimisticPartial()
    {
        // No real file bytes written at the assumed cluster — the region is
        // zero-filled, which should fail the JPEG header check outright.
        var entries = new List<DirEntryBlock> { DeletedLongNameEntry("photo.jpg", size: 5000, startCluster: 10) };
        byte[] volume = BuildVolume(TotalClusters, entries);

        using var reader = new MemoryRawReader(volume);
        var parser = new Fat32Parser(reader);
        Assert.True(parser.TryReadBootSector());

        var found = parser.ScanDeletedEntries(null, CancellationToken.None, null);

        var file3 = Assert.Single(found);
        Assert.Equal(Recoverability.Poor, file3.Recoverability);
    }

    [Fact]
    public void ScanDeletedEntries_UnvalidatableExtension_FallsBackToBoundsHeuristic()
    {
        // .txt has no structural validator — should keep the old bounds-based
        // Partial estimate rather than being downgraded for lack of evidence.
        var entries = new List<DirEntryBlock> { DeletedLongNameEntry("notes.txt", size: 100, startCluster: 10) };
        byte[] volume = BuildVolume(TotalClusters, entries);

        using var reader = new MemoryRawReader(volume);
        var parser = new Fat32Parser(reader);
        Assert.True(parser.TryReadBootSector());

        var found = parser.ScanDeletedEntries(null, CancellationToken.None, null);

        var file4 = Assert.Single(found);
        Assert.Equal(Recoverability.Partial, file4.Recoverability);
    }
}
