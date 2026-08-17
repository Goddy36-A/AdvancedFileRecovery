using FileRecovery.Core.FileSystems;
using FileRecovery.Core.Models;
using FileRecovery.Tests.TestSupport;
using Xunit;
using static FileRecovery.Tests.TestSupport.ExFatFixtureBuilder;

namespace FileRecovery.Tests;

public class ExFatParserTests
{
    private const uint ClusterCount = 1000;

    [Fact]
    public void ScanDeletedEntries_FindsDeletedEntry_WithCorrectNameSizeAndClusterOffset()
    {
        var entries = new List<DirEntrySet> { DeletedEntry("holiday.mp4", size: 5000, firstCluster: 20) };
        byte[] volume = BuildVolume(ClusterCount, entries);
        using var reader = new MemoryRawReader(volume);
        var parser = new ExFatParser(reader);

        Assert.True(parser.TryReadBootSector());
        var found = parser.ScanDeletedEntries(null, CancellationToken.None, null);

        var file = Assert.Single(found);
        Assert.Equal("holiday.mp4", file.Name);
        Assert.Equal(5000, file.SizeBytes);
        Assert.Equal(FileCategory.Video, file.Category);
        var run = Assert.Single(file.ClusterRuns);
        long expectedOffset = ((long)ClusterHeapOffsetSectors + (20 - 2) * (1 << SectorsPerClusterShift)) * BytesPerSector;
        Assert.Equal(expectedOffset, run.ByteOffset);
    }

    [Fact]
    public void ScanDeletedEntries_SkipsLiveEntries()
    {
        var entries = new List<DirEntrySet>
        {
            LiveEntry("still_here.pdf", size: 100, firstCluster: 10),
            DeletedEntry("gone.zip", size: 200, firstCluster: 11),
        };
        byte[] volume = BuildVolume(ClusterCount, entries);
        using var reader = new MemoryRawReader(volume);
        var parser = new ExFatParser(reader);
        Assert.True(parser.TryReadBootSector());

        var found = parser.ScanDeletedEntries(null, CancellationToken.None, null);

        var file = Assert.Single(found);
        Assert.Equal("gone.zip", file.Name);
    }

    [Fact]
    public void ScanDeletedEntries_ReconstructsNameLongerThanOneFileNameEntry()
    {
        string longName = "a_pretty_long_filename_that_spans_multiple_15char_chunks.txt";
        var entries = new List<DirEntrySet> { DeletedEntry(longName, size: 1, firstCluster: 15) };
        byte[] volume = BuildVolume(ClusterCount, entries);
        using var reader = new MemoryRawReader(volume);
        var parser = new ExFatParser(reader);
        Assert.True(parser.TryReadBootSector());

        var found = parser.ScanDeletedEntries(null, CancellationToken.None, null);

        var file = Assert.Single(found);
        Assert.Equal(longName, file.Name);
    }
}
