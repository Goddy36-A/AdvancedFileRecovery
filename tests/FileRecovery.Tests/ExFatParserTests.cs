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

    [Fact]
    public void ScanDeletedEntries_RealZipBytesAtAssumedLocation_ScoresExcellent()
    {
        byte[] zip = new byte[64];
        new byte[] { 0x50, 0x4B, 0x03, 0x04 }.CopyTo(zip, 0);       // local file header sig
        new byte[] { 0x50, 0x4B, 0x05, 0x06 }.CopyTo(zip, 40);      // EOCD sig somewhere near the end

        var entries = new List<DirEntrySet> { DeletedEntry("archive.zip", size: (ulong)zip.Length, firstCluster: 20) };
        byte[] volume = BuildVolume(ClusterCount, entries);

        long dataOffset = ((long)ClusterHeapOffsetSectors + (20 - 2) * (1 << SectorsPerClusterShift)) * BytesPerSector;
        Array.Copy(zip, 0, volume, dataOffset, zip.Length);

        using var reader = new MemoryRawReader(volume);
        var parser = new ExFatParser(reader);
        Assert.True(parser.TryReadBootSector());

        var found = parser.ScanDeletedEntries(null, CancellationToken.None, null);

        var zipFile = Assert.Single(found);
        Assert.Equal(Recoverability.Excellent, zipFile.Recoverability);
    }

    // --- Multi-cluster directories and subdirectory recursion ---

    [Fact]
    public void ScanDeletedEntries_RootDirectorySpansMultipleClusters_ViaFatChain_FindsEntriesInBothClusters()
    {
        byte[] volume = BuildEmptyVolume(ClusterCount);
        WriteEntriesAtCluster(volume, RootDirCluster, new List<DirEntrySet> { DeletedEntry("first.txt", size: 10, firstCluster: 50) });
        WriteEntriesAtCluster(volume, RootDirCluster + 1, new List<DirEntrySet> { DeletedEntry("second.txt", size: 20, firstCluster: 51) });
        WriteFatEntry(volume, RootDirCluster, RootDirCluster + 1);
        WriteFatEntry(volume, RootDirCluster + 1, FatEndOfChain);

        using var reader = new MemoryRawReader(volume);
        var parser = new ExFatParser(reader);
        Assert.True(parser.TryReadBootSector());

        var found = parser.ScanDeletedEntries(null, CancellationToken.None, null);

        Assert.Equal(2, found.Count);
        Assert.Contains(found, f => f.Name == "first.txt");
        Assert.Contains(found, f => f.Name == "second.txt");
    }

    [Fact]
    public void ScanDeletedEntries_RecursesIntoLiveSubdirectory_FindsDeletedFileInside()
    {
        // Previously, subdirectory recursion wasn't implemented at all — a
        // deleted file living inside any folder (not the root) was invisible.
        byte[] volume = BuildEmptyVolume(ClusterCount);
        WriteEntriesAtCluster(volume, RootDirCluster, new List<DirEntrySet>
        {
            LiveDirectory("Photos", firstCluster: 30, dataLength: BytesPerCluster, noFatChain: true),
        });
        WriteEntriesAtCluster(volume, 30, new List<DirEntrySet>
        {
            DeletedEntry("vacation.jpg", size: 500, firstCluster: 60),
        });

        using var reader = new MemoryRawReader(volume);
        var parser = new ExFatParser(reader);
        Assert.True(parser.TryReadBootSector());

        var found = parser.ScanDeletedEntries(null, CancellationToken.None, null);

        var file = Assert.Single(found);
        Assert.Equal("vacation.jpg", file.Name);
    }

    [Fact]
    public void ScanDeletedEntries_NoFatChainMultiClusterSubdirectory_WalksContiguouslyWithoutConsultingFat()
    {
        // A NoFatChain (contiguous) directory's FAT entries may legitimately be
        // left unpopulated (zeroed) — walking clusters here must NOT depend on
        // the FAT at all, or this would incorrectly stop after one cluster.
        byte[] volume = BuildEmptyVolume(ClusterCount);
        WriteEntriesAtCluster(volume, RootDirCluster, new List<DirEntrySet>
        {
            LiveDirectory("BigFolder", firstCluster: 40, dataLength: 2L * BytesPerCluster, noFatChain: true),
        });
        // Deliberately leave FAT[40] unpopulated (zero/free) — must not matter.
        WriteEntriesAtCluster(volume, 41, new List<DirEntrySet>
        {
            DeletedEntry("second_cluster_file.pdf", size: 999, firstCluster: 70),
        });

        using var reader = new MemoryRawReader(volume);
        var parser = new ExFatParser(reader);
        Assert.True(parser.TryReadBootSector());

        var found = parser.ScanDeletedEntries(null, CancellationToken.None, null);

        var file = Assert.Single(found);
        Assert.Equal("second_cluster_file.pdf", file.Name);
    }

    [Fact]
    public void ScanDeletedEntries_FatChainedFragmentedSubdirectory_FollowsChainAcrossNonContiguousClusters()
    {
        byte[] volume = BuildEmptyVolume(ClusterCount);
        WriteEntriesAtCluster(volume, RootDirCluster, new List<DirEntrySet>
        {
            LiveDirectory("Fragmented", firstCluster: 80, dataLength: 2L * BytesPerCluster, noFatChain: false),
        });
        WriteFatEntry(volume, 80, 150); // chain jumps to a non-adjacent cluster — only a real FAT walk finds this
        WriteFatEntry(volume, 150, FatEndOfChain);
        WriteEntriesAtCluster(volume, 150, new List<DirEntrySet>
        {
            DeletedEntry("far_cluster_file.zip", size: 42, firstCluster: 90),
        });

        using var reader = new MemoryRawReader(volume);
        var parser = new ExFatParser(reader);
        Assert.True(parser.TryReadBootSector());

        var found = parser.ScanDeletedEntries(null, CancellationToken.None, null);

        var file = Assert.Single(found);
        Assert.Equal("far_cluster_file.zip", file.Name);
    }

    [Fact]
    public void ScanDeletedEntries_CyclicFatChain_TerminatesInsteadOfHanging()
    {
        byte[] volume = BuildEmptyVolume(ClusterCount);
        WriteEntriesAtCluster(volume, RootDirCluster, new List<DirEntrySet>
        {
            LiveDirectory("Cyclic", firstCluster: 80, dataLength: BytesPerCluster, noFatChain: false),
        });
        WriteFatEntry(volume, 80, 81);
        WriteFatEntry(volume, 81, 80); // cycles back to 80 instead of ever reaching end-of-chain

        using var reader = new MemoryRawReader(volume);
        var parser = new ExFatParser(reader);
        Assert.True(parser.TryReadBootSector());

        var found = parser.ScanDeletedEntries(null, CancellationToken.None, null);

        Assert.Empty(found); // no files placed anywhere; this test's only real assertion is that it returns at all
    }
}
