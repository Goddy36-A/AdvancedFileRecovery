using FileRecovery.Core.Disk;
using FileRecovery.Core.Models;
using FileRecovery.Core.Recovery;
using FileRecovery.Tests.TestSupport;
using Xunit;

namespace FileRecovery.Tests;

public class RecoveryEngineTests : IDisposable
{
    private readonly string _destDir = Path.Combine(Path.GetTempPath(), "FileRecoveryTests_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_destDir)) Directory.Delete(_destDir, recursive: true);
    }

    // A device path that deliberately does NOT look like a single drive letter,
    // so DestinationSafety's WMI lookup finds nothing and its safe fallback
    // (only treats a literal single-letter match as "same device") lets these
    // tests proceed without depending on the CI runner's actual disk layout.
    private static VolumeInfo FakeSource(long totalSize = 1024 * 1024) => new()
    {
        DevicePath = @"\\.\FAKETESTVOL",
        DisplayName = "Fake Test Volume",
        TotalSizeBytes = totalSize,
        FileSystem = FileSystemKind.NTFS,
    };

    [Fact]
    public void Recover_ResidentFile_WritesExactBytes()
    {
        byte[] residentBytes = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        var file = new RecoverableFile
        {
            Id = "r1",
            Name = "tiny.txt",
            SizeBytes = residentBytes.Length,
            ResidentData = residentBytes,
        };

        var engine = new RecoveryEngine(_ => new MemoryRawReader(new byte[0])); // never touched for resident files
        var result = engine.Recover(FakeSource(), new[] { file }, _destDir, progress: null, CancellationToken.None);

        Assert.Equal(1, result.SucceededCount);
        Assert.Equal(0, result.FailedCount);
        string outPath = Path.Combine(_destDir, "tiny.txt");
        Assert.True(File.Exists(outPath));
        Assert.Equal(residentBytes, File.ReadAllBytes(outPath));
    }

    [Fact]
    public void Recover_ClusterRunFile_CopiesExactByteRangeFromSource()
    {
        var sourceData = new byte[100_000];
        new Random(7).NextBytes(sourceData);
        // Plant a recognizable marker at the range we expect the engine to copy.
        const long offset = 40_000;
        const int length = 5000;
        for (int i = 0; i < length; i++) sourceData[offset + i] = (byte)(i % 251);

        var file = new RecoverableFile
        {
            Id = "r2",
            Name = "photo.jpg",
            SizeBytes = length,
        };
        file.ClusterRuns.Add(new ClusterRun(offset, length));

        var engine = new RecoveryEngine(_ => new MemoryRawReader(sourceData));
        var result = engine.Recover(FakeSource(), new[] { file }, _destDir, progress: null, CancellationToken.None);

        Assert.Equal(1, result.SucceededCount);
        byte[] expected = sourceData[(int)offset..(int)(offset + length)];
        byte[] actual = File.ReadAllBytes(Path.Combine(_destDir, "photo.jpg"));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Recover_CarvedFile_CopiesExactByteRangeFromSource()
    {
        var sourceData = new byte[10_000];
        byte[] jpegBytes = { 0xFF, 0xD8, 0xFF, 1, 2, 3, 4, 5, 0xFF, 0xD9 };
        const long offset = 500;
        jpegBytes.CopyTo(sourceData, offset);

        var file = new RecoverableFile
        {
            Id = "r3",
            Name = "recovered.jpg",
            SizeBytes = jpegBytes.Length,
            FromCarving = true,
            CarveOffset = offset,
            CarveLength = jpegBytes.Length,
        };

        var engine = new RecoveryEngine(_ => new MemoryRawReader(sourceData));
        var result = engine.Recover(FakeSource(), new[] { file }, _destDir, progress: null, CancellationToken.None);

        Assert.Equal(1, result.SucceededCount);
        Assert.Equal(jpegBytes, File.ReadAllBytes(Path.Combine(_destDir, "recovered.jpg")));
    }

    [Fact]
    public void Recover_MultipleClusterRuns_ConcatenatesInOrder()
    {
        var sourceData = new byte[10_000];
        byte[] part1 = { 10, 11, 12, 13 };
        byte[] part2 = { 20, 21, 22, 23, 24 };
        part1.CopyTo(sourceData, 100);
        part2.CopyTo(sourceData, 300);

        var file = new RecoverableFile { Id = "r4", Name = "fragmented.bin", SizeBytes = part1.Length + part2.Length };
        file.ClusterRuns.Add(new ClusterRun(100, part1.Length));
        file.ClusterRuns.Add(new ClusterRun(300, part2.Length));

        var engine = new RecoveryEngine(_ => new MemoryRawReader(sourceData));
        var result = engine.Recover(FakeSource(), new[] { file }, _destDir, progress: null, CancellationToken.None);

        Assert.Equal(1, result.SucceededCount);
        byte[] expected = part1.Concat(part2).ToArray();
        Assert.Equal(expected, File.ReadAllBytes(Path.Combine(_destDir, "fragmented.bin")));
    }

    [Fact]
    public void Recover_FileWithNoDataLocation_IsReportedAsFailureNotThrown()
    {
        var file = new RecoverableFile { Id = "r5", Name = "broken.dat", SizeBytes = 10 };
        // No ResidentData, no ClusterRuns, FromCarving=false — nothing to copy from.

        var engine = new RecoveryEngine(_ => new MemoryRawReader(new byte[100]));
        var result = engine.Recover(FakeSource(), new[] { file }, _destDir, progress: null, CancellationToken.None);

        Assert.Equal(0, result.SucceededCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Single(result.Failures);
    }

    [Fact]
    public void Recover_TwoFilesWithSameName_DisambiguatesOutputFileNames()
    {
        var file1 = new RecoverableFile { Id = "a", Name = "dup.txt", SizeBytes = 1, ResidentData = new byte[] { 1 } };
        var file2 = new RecoverableFile { Id = "b", Name = "dup.txt", SizeBytes = 1, ResidentData = new byte[] { 2 } };

        var engine = new RecoveryEngine(_ => new MemoryRawReader(Array.Empty<byte>()));
        var result = engine.Recover(FakeSource(), new[] { file1, file2 }, _destDir, progress: null, CancellationToken.None);

        Assert.Equal(2, result.SucceededCount);
        Assert.True(File.Exists(Path.Combine(_destDir, "dup.txt")));
        Assert.True(File.Exists(Path.Combine(_destDir, "dup (1).txt")));
    }
}
