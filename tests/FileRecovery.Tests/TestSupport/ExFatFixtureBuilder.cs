using System.Text;

namespace FileRecovery.Tests.TestSupport;

/// <summary>
/// Hand-encodes a minimal exFAT boot sector and a root-directory cluster
/// containing real entry-sets (File Directory Entry + Stream Extension +
/// File Name entries), matching exactly what <c>ExFatParser</c> reads.
/// </summary>
public static class ExFatFixtureBuilder
{
    public const int BytesPerSectorShift = 9;  // 512 bytes/sector
    public const int SectorsPerClusterShift = 3; // 8 sectors/cluster => 4096 bytes/cluster
    public const int BytesPerSector = 1 << BytesPerSectorShift;
    public const int BytesPerCluster = BytesPerSector << SectorsPerClusterShift;
    public const uint ClusterHeapOffsetSectors = 200;
    public const uint RootDirCluster = 5;

    public sealed class DirEntrySet
    {
        public required byte[] Bytes;
    }

    public static byte[] BuildVolume(uint clusterCount, IReadOnlyList<DirEntrySet> rootDirEntrySets)
    {
        long volumeBytes = (long)ClusterHeapOffsetSectors * BytesPerSector + (long)clusterCount * BytesPerCluster;
        var vol = new byte[volumeBytes];

        Encoding.ASCII.GetBytes("EXFAT   ").CopyTo(vol, 3);
        BinWrite.U32(vol, 80, ClusterHeapOffsetSectors); // FAT offset (unused by our parser's directory-scan path)
        BinWrite.U32(vol, 88, ClusterHeapOffsetSectors);
        BinWrite.U32(vol, 92, clusterCount);
        BinWrite.U32(vol, 96, RootDirCluster);
        vol[108] = BytesPerSectorShift;
        vol[109] = SectorsPerClusterShift;

        long rootDirOffset = ((long)ClusterHeapOffsetSectors + (RootDirCluster - 2) * (1 << SectorsPerClusterShift)) * BytesPerSector;
        long pos = rootDirOffset;
        foreach (var set in rootDirEntrySets)
        {
            Array.Copy(set.Bytes, 0, vol, pos, set.Bytes.Length);
            pos += set.Bytes.Length;
        }

        return vol;
    }

    public static DirEntrySet DeletedEntry(string name, ulong size, uint firstCluster) =>
        BuildEntrySet(name, size, firstCluster, inUse: false);

    public static DirEntrySet LiveEntry(string name, ulong size, uint firstCluster) =>
        BuildEntrySet(name, size, firstCluster, inUse: true);

    private static DirEntrySet BuildEntrySet(string name, ulong size, uint firstCluster, bool inUse)
    {
        var nameChunks = new List<string>();
        for (int i = 0; i < name.Length; i += 15)
            nameChunks.Add(name.Substring(i, Math.Min(15, name.Length - i)));
        if (nameChunks.Count == 0) nameChunks.Add("");

        byte secondaryCount = (byte)(1 + nameChunks.Count); // stream extension + name entries

        var fileDirEntry = new byte[32];
        fileDirEntry[0] = (byte)(0x05 | (inUse ? 0x80 : 0x00)); // File Directory Entry
        fileDirEntry[1] = secondaryCount;

        var streamEntry = new byte[32];
        streamEntry[0] = (byte)(0x40 | (inUse ? 0x80 : 0x00)); // Stream Extension
        streamEntry[3] = (byte)name.Length;
        BinWrite.U32(streamEntry, 20, firstCluster);
        BinWrite.U64(streamEntry, 24, size);

        var all = new List<byte>();
        all.AddRange(fileDirEntry);
        all.AddRange(streamEntry);
        foreach (var chunk in nameChunks)
        {
            var nameEntry = new byte[32];
            nameEntry[0] = (byte)(0x41 | (inUse ? 0x80 : 0x00)); // File Name entry
            for (int i = 0; i < 15; i++)
            {
                char c = i < chunk.Length ? chunk[i] : '\0';
                BinWrite.U16(nameEntry, 2 + i * 2, c);
            }
            all.AddRange(nameEntry);
        }

        return new DirEntrySet { Bytes = all.ToArray() };
    }
}
