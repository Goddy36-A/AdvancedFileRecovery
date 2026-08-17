using FileRecovery.Core.Disk;

namespace FileRecovery.Tests.TestSupport;

/// <summary>
/// Wraps a plain byte array as an <see cref="IRawReader"/>, standing in for a
/// physical disk in tests. Mirrors <see cref="RawDiskReader"/>'s contract:
/// reads past the end of the backing data return zero-padded bytes rather
/// than throwing, and every read returns exactly the requested length.
/// </summary>
public sealed class MemoryRawReader : IRawReader
{
    private readonly byte[] _data;

    public int BytesPerSector { get; }
    public long TotalSizeBytes => _data.Length;

    public MemoryRawReader(byte[] data, int bytesPerSector = 512)
    {
        _data = data;
        BytesPerSector = bytesPerSector;
    }

    public byte[] ReadBytes(long offset, int count)
    {
        var result = new byte[count];
        if (offset >= _data.Length || offset < 0 || count <= 0) return result;

        int available = (int)Math.Min(count, _data.Length - offset);
        Array.Copy(_data, offset, result, 0, available);
        return result;
    }

    public void Dispose() { /* nothing to release for an in-memory buffer */ }
}
