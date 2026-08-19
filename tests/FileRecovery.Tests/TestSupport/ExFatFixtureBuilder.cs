using System.Text;

namespace FileRecovery.Tests.TestSupport;

/// <summary>
/// Hand-encodes a minimal exFAT boot sector and directory clusters containing
/// real entry-sets (File Directory Entry + Stream Extension + File Name
/// entries), matching exactly what <c>ExFatParser</c> reads. Supports writing
/// entries into arbitrary clusters and setting up FAT chain entries, so tests
/// can build multi-cluster directories (both NoFatChain/contiguous and
/// genuinely FAT-chained/fragmented) and subdirectory hierarchies.
/// </summary>
public static class ExFatFixtureBuilder
{
    public const int BytesPerSectorShift = 9;  // 512 bytes/sector
    public const int SectorsPerClusterShift = 3; // 8 sectors/cluster => 4096 bytes/cluster
    public const int BytesPerSector = 1 << BytesPerSectorShift;
    public const int BytesPerCluster = BytesPerSector << SectorsPerClusterShift;

    // Kept deliberately non-overlapping: the FAT needs clusterCount*4 bytes,
    // which must fit before ClusterHeapOffsetSectors starts.
    public const uint FatOffsetSectors = 24;
    public const uint ClusterHeapOffsetSectors = 200;
    public const uint RootDirCluster = 5;

    public sealed class DirEntrySet
    {
        public required byte[] Bytes;
    }

    public static long ClusterOffset(uint cluster) =>
        ((long)ClusterHeapOffsetSectors + (cluster - 2) * (1 << SectorsPerClusterShift)) * BytesPerSector;

    public static byte[] BuildEmptyVolume(uint clusterCount)
    {
        long volumeBytes = (long)ClusterHeapOffsetSectors * BytesPerSector + (long)clusterCount * BytesPerCluster;
        var vol = new byte[volumeBytes];

        Encoding.ASCII.GetBytes("EXFAT   ").CopyTo(vol, 3);
        BinWrite.U32(vol, 80, FatOffsetSectors);
        BinWrite.U32(vol, 88, ClusterHeapOffsetSectors);
        BinWrite.U32(vol, 92, clusterCount);
        BinWrite.U32(vol, 96, RootDirCluster);
        vol[108] = BytesPerSectorShift;
        vol[109] = SectorsPerClusterShift;

        return vol;
    }

    /// <summary>Convenience wrapper: empty volume + entries written into the root directory's (first) cluster.</summary>
    public static byte[] BuildVolume(uint clusterCount, IReadOnlyList<DirEntrySet> rootDirEntrySets)
    {
        byte[] vol = BuildEmptyVolume(clusterCount);
        WriteEntriesAtCluster(vol, RootDirCluster, rootDirEntrySets);
        return vol;
    }

    public static void WriteEntriesAtCluster(byte[] volume, uint cluster, IReadOnlyList<DirEntrySet> entrySets)
    {
        long pos = ClusterOffset(cluster);
        foreach (var set in entrySets)
        {
            Array.Copy(set.Bytes, 0, volume, pos, set.Bytes.Length);
            pos += set.Bytes.Length;
        }
    }

    /// <summary>Sets the FAT entry for a cluster — i.e., "the next cluster in this chain is...".</summary>
    public static void WriteFatEntry(byte[] volume, uint cluster, uint nextClusterOrMarker)
    {
        long offset = (long)FatOffsetSectors * BytesPerSector + cluster * 4;
        BinWrite.U32(volume, (int)offset, nextClusterOrMarker);
    }

    public const uint FatEndOfChain = 0xFFFFFFFF;

    public static DirEntrySet DeletedEntry(string name, ulong size, uint firstCluster) =>
        BuildEntrySet(name, size, firstCluster, inUse: false, isDirectory: false, noFatChain: true);

    public static DirEntrySet LiveEntry(string name, ulong size, uint firstCluster) =>
        BuildEntrySet(name, size, firstCluster, inUse: true, isDirectory: false, noFatChain: true);

    /// <summary>A live (intact) subdirectory entry — ExFatParser should recurse into it looking for deleted files.</summary>
    public static DirEntrySet LiveDirectory(string name, uint firstCluster, long dataLength, bool noFatChain) =>
        BuildEntrySet(name, (ulong)dataLength, firstCluster, inUse: true, isDirectory: true, noFatChain: noFatChain);

    private static DirEntrySet BuildEntrySet(string name, ulong size, uint firstCluster, bool inUse, bool isDirectory, bool noFatChain)
    {
        var nameChunks = new List<string>();
        for (int i = 0; i < name.Length; i += 15)
            nameChunks.Add(name.Substring(i, Math.Min(15, name.Length - i)));
        if (nameChunks.Count == 0) nameChunks.Add("");

        byte secondaryCount = (byte)(1 + nameChunks.Count); // stream extension + name entries

        var fileDirEntry = new byte[32];
        fileDirEntry[0] = (byte)(0x05 | (inUse ? 0x80 : 0x00)); // File Directory Entry
        fileDirEntry[1] = secondaryCount;
        BinWrite.U16(fileDirEntry, 4, isDirectory ? (ushort)0x10 : (ushort)0x20); // FileAttributes: Directory vs Archive

        var streamEntry = new byte[32];
        streamEntry[0] = (byte)(0x40 | (inUse ? 0x80 : 0x00)); // Stream Extension
        streamEntry[1] = (byte)(noFatChain ? 0x02 : 0x00);      // GeneralSecondaryFlags bit1 = NoFatChain
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
