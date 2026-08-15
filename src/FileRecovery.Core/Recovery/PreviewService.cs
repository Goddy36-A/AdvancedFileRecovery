using FileRecovery.Core.Disk;
using FileRecovery.Core.Models;

namespace FileRecovery.Core.Recovery;

/// <summary>
/// Reads a bounded number of bytes for a single recoverable file, purely for
/// generating an in-memory thumbnail/preview in the results grid. Opens the
/// source in the same strict read-only mode as every other reader in the app.
/// </summary>
public sealed class PreviewService
{
    public byte[] ReadPreviewBytes(VolumeInfo source, RecoverableFile file, int maxBytes = 8 * 1024 * 1024)
    {
        using var reader = RawDiskReader.Open(source.DevicePath);

        if (file.FromCarving)
        {
            int len = (int)Math.Min(file.CarveLength, maxBytes);
            return reader.ReadBytes(file.CarveOffset, len);
        }

        if (file.ClusterRuns.Count > 0)
        {
            var run = file.ClusterRuns[0];
            int len = (int)Math.Min(Math.Min(run.LengthBytes, file.SizeBytes), maxBytes);
            return reader.ReadBytes(run.ByteOffset, Math.Max(len, 0));
        }

        return Array.Empty<byte>();
    }
}
