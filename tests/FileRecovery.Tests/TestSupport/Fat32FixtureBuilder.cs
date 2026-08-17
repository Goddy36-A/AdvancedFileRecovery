using System.Text;

namespace FileRecovery.Tests.TestSupport;

/// <summary>
/// Hand-encodes a minimal FAT32 boot sector/BPB and root-directory cluster
/// with real 32-byte 8.3 directory entries (plus LFN sub-entries), matching
/// exactly what <c>Fat32Parser</c> reads.
/// </summary>
public static class Fat32FixtureBuilder
{
    public const int BytesPerSector = 512;
    public const int SectorsPerCluster = 8;
    public const int BytesPerCluster = BytesPerSector * SectorsPerCluster; // 4096
    public const int ReservedSectors = 32;
    public const int NumFats = 2;
    public const uint SectorsPerFat = 16;
    public const uint RootCluster = 2;

    /// <summary>One raw 32-byte directory entry, pre-encoded by the helpers below.</summary>
    public sealed class DirEntryBlock
    {
        public required byte[] Bytes; // one or more concatenated 32-byte entries (LFN parts + the 8.3 entry)
    }

    public static byte[] BuildVolume(int totalClusters, IReadOnlyList<DirEntryBlock> rootDirEntries)
    {
        long dataStartSector = ReservedSectors + (long)NumFats * SectorsPerFat;
        long totalSectors = dataStartSector + (long)totalClusters * SectorsPerCluster;
        var vol = new byte[totalSectors * BytesPerSector];

        vol[510] = 0x55; vol[511] = 0xAA;
        BinWrite.U16(vol, 0x0B, BytesPerSector);
        vol[0x0D] = SectorsPerCluster;
        BinWrite.U16(vol, 0x0E, ReservedSectors);
        vol[0x10] = NumFats;
        BinWrite.U32(vol, 0x20, (uint)totalSectors);
        BinWrite.U32(vol, 0x24, SectorsPerFat);
        BinWrite.U32(vol, 0x2C, RootCluster);
        Encoding.ASCII.GetBytes("FAT32   ").CopyTo(vol, 0x52);

        long rootDirOffset = dataStartSector * BytesPerSector; // cluster 2 == first data cluster == root dir here
        long pos = rootDirOffset;
        foreach (var block in rootDirEntries)
        {
            Array.Copy(block.Bytes, 0, vol, pos, block.Bytes.Length);
            pos += block.Bytes.Length;
        }

        return vol;
    }

    /// <summary>A deleted (0xE5) or live short 8.3-only entry — no LFN.</summary>
    public static DirEntryBlock ShortEntry(string name8_3, uint size, uint startCluster, bool deleted, bool isVolumeLabel = false, bool isDirectory = false)
    {
        var e = new byte[32];
        WriteShortName(e, name8_3);
        e[11] = (byte)((isVolumeLabel ? 0x08 : 0x00) | (isDirectory ? 0x10 : 0x00) | (isVolumeLabel || isDirectory ? 0 : 0x20));
        BinWrite.U16(e, 20, (ushort)(startCluster >> 16));
        BinWrite.U16(e, 26, (ushort)(startCluster & 0xFFFF));
        BinWrite.U32(e, 28, size);
        if (deleted) e[0] = 0xE5;
        return new DirEntryBlock { Bytes = e };
    }

    /// <summary>A deleted entry with a real long file name, encoded as LFN sub-entries followed by the (deleted) short entry.</summary>
    public static DirEntryBlock DeletedLongNameEntry(string longName, uint size, uint startCluster)
    {
        var chunks = SplitLfnChunks(longName);
        var bytes = new List<byte>();
        // FAT stores LFN sub-entries in reverse order on disk (highest sequence
        // number, i.e. the LAST piece of the name, comes first), with the
        // highest-sequence entry's low bit 0x40 marking it as the "last" LFN entry.
        for (int i = 0; i < chunks.Count; i++)
        {
            int chunkIndexFromEnd = i; // 0 = last chunk of the name = highest sequence number
            int sequenceNumber = chunks.Count - chunkIndexFromEnd;
            byte seqByte = chunkIndexFromEnd == 0 ? (byte)(sequenceNumber | 0x40) : (byte)sequenceNumber;
            string chunkText = chunks[chunks.Count - 1 - chunkIndexFromEnd];
            bytes.AddRange(BuildLfnEntry(seqByte, chunkText));
        }

        var shortEntry = new byte[32];
        WriteShortName(shortEntry, GenerateFakeShortName(longName));
        shortEntry[11] = 0x20; // archive
        BinWrite.U16(shortEntry, 20, (ushort)(startCluster >> 16));
        BinWrite.U16(shortEntry, 26, (ushort)(startCluster & 0xFFFF));
        BinWrite.U32(shortEntry, 28, size);
        shortEntry[0] = 0xE5; // deleted

        bytes.AddRange(shortEntry);
        return new DirEntryBlock { Bytes = bytes.ToArray() };
    }

    public static DirEntryBlock EndMarker() => new() { Bytes = new byte[32] }; // first byte 0x00 => Fat32Parser stops here

    private static void WriteShortName(byte[] entry, string name8_3)
    {
        for (int i = 0; i < 11; i++) entry[i] = 0x20; // space-padded
        string[] parts = name8_3.Split('.');
        string namePart = parts[0].PadRight(8)[..8];
        string extPart = (parts.Length > 1 ? parts[1] : "").PadRight(3)[..3];
        Encoding.ASCII.GetBytes(namePart).CopyTo(entry, 0);
        Encoding.ASCII.GetBytes(extPart).CopyTo(entry, 8);
    }

    private static string GenerateFakeShortName(string longName)
    {
        string stem = new string(longName.Where(char.IsLetterOrDigit).ToArray());
        stem = (stem.Length > 6 ? stem[..6] : stem.PadRight(6, 'X')).ToUpperInvariant();
        string ext = Path.GetExtension(longName).TrimStart('.').ToUpperInvariant();
        ext = ext.Length > 3 ? ext[..3] : ext;
        return $"{stem}~1.{ext}";
    }

    private static List<string> SplitLfnChunks(string name)
    {
        const int charsPerChunk = 13;
        var chunks = new List<string>();
        for (int i = 0; i < name.Length; i += charsPerChunk)
            chunks.Add(name.Substring(i, Math.Min(charsPerChunk, name.Length - i)));
        return chunks;
    }

    private static byte[] BuildLfnEntry(byte sequenceByte, string chunk)
    {
        var e = new byte[32];
        e[0] = sequenceByte;
        e[11] = 0x0F; // LFN attribute

        // Real FAT terminates a short-of-13-chars chunk with 0x0000 then pads the
        // rest with 0xFFFF. Our parser stops appending at the first 0xFFFF/0x0000
        // character it sees, so this padding scheme round-trips correctly.
        var padded = new char[13];
        for (int i = 0; i < 13; i++) padded[i] = i < chunk.Length ? chunk[i] : (i == chunk.Length ? '\0' : '\uFFFF');

        void WriteChars(int entryOffset, int startIdx, int count)
        {
            for (int i = 0; i < count; i++)
                BinWrite.U16(e, entryOffset + i * 2, padded[startIdx + i]);
        }
        WriteChars(1, 0, 5);
        WriteChars(14, 5, 6);
        WriteChars(28, 11, 2);
        return e;
    }
}
