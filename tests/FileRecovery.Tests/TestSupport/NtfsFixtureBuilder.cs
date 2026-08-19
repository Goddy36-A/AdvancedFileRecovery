using System.Text;

namespace FileRecovery.Tests.TestSupport;

/// <summary>
/// Hand-encodes the exact on-disk NTFS structures <c>NtfsMftParser</c> reads:
/// the boot sector's BPB fields, FILE record headers (including the update
/// sequence array "fixup"), and $FILE_NAME / $DATA attributes (resident and
/// non-resident, with data-run encoding). This exists so tests exercise the
/// parser against bytes laid out the way real NTFS lays them out — not
/// against a mock that already agrees with the parser's assumptions.
/// </summary>
public static class NtfsFixtureBuilder
{
    public const int BytesPerSector = 512;
    public const int SectorsPerCluster = 8;
    public const int BytesPerCluster = BytesPerSector * SectorsPerCluster; // 4096
    public const int FileRecordSize = 1024;

    public sealed class RecordSpec
    {
        public bool InUse;
        public bool IsDirectory;
        public string? FileName;
        public long ParentRef = 5; // NTFS root directory record number, unless overridden
        public DateTime? Modified;
        public byte[]? ResidentData;
        public List<(long Lcn, long ClusterCount)>? NonResidentRuns;
        public long NonResidentRealSize;
    }

    /// <summary>
    /// Builds a full volume image. <paramref name="totalClusters"/> sizes the
    /// backing buffer; <paramref name="mftStartCluster"/> is where the $MFT
    /// begins; <paramref name="records"/>[0] becomes MFT record 0 (its
    /// non-resident $DATA real-size drives how many records
    /// <c>NtfsMftParser.EstimateMftSize</c> believes the MFT contains, so it
    /// scans exactly <paramref name="records"/>.Count records — no more).
    /// </summary>
    public static byte[] BuildVolume(int totalClusters, int mftStartCluster, IReadOnlyList<RecordSpec> records)
    {
        var vol = new byte[(long)totalClusters * BytesPerCluster];

        Encoding.ASCII.GetBytes("NTFS    ").CopyTo(vol, 3);
        BinWrite.U16(vol, 0x0B, BytesPerSector);
        vol[0x0D] = SectorsPerCluster;
        long totalSectors = (long)totalClusters * BytesPerCluster / BytesPerSector;
        BinWrite.I64(vol, 0x28, totalSectors);
        BinWrite.I64(vol, 0x30, mftStartCluster);
        vol[0x40] = unchecked((byte)(sbyte)-10); // clusters-per-file-record negative => 2^10 = 1024-byte records

        long mftOffset = (long)mftStartCluster * BytesPerCluster;

        // Record 0 ($MFT's own record): its non-resident $DATA real-size tells
        // EstimateMftSize exactly how many records to scan.
        long mftDefinedSize = (long)records.Count * FileRecordSize;
        var record0 = BuildRawRecord(inUse: true, isDirectory: false, writeAttributes: (rec, pos) =>
            WriteNonResidentDataAttribute(rec, pos, mftDefinedSize, new List<(long, long)>()));
        Array.Copy(record0, 0, vol, mftOffset, record0.Length);

        for (int i = 1; i < records.Count; i++)
        {
            var spec = records[i];
            byte[] rec = BuildRawRecord(spec.InUse, spec.IsDirectory, (buf, pos) =>
            {
                int p = pos;
                if (spec.FileName != null)
                    p = WriteFileNameAttribute(buf, p, spec.ParentRef, spec.Modified ?? DateTime.UtcNow, spec.FileName);
                if (spec.ResidentData != null)
                    p = WriteResidentDataAttribute(buf, p, spec.ResidentData);
                else if (spec.NonResidentRuns != null)
                    p = WriteNonResidentDataAttribute(buf, p, spec.NonResidentRealSize, spec.NonResidentRuns);
                return p;
            });
            Array.Copy(rec, 0, vol, mftOffset + (long)i * FileRecordSize, rec.Length);
        }

        return vol;
    }

    private static byte[] BuildRawRecord(bool inUse, bool isDirectory, Func<byte[], int, int> writeAttributes)
    {
        var rec = new byte[FileRecordSize];
        Encoding.ASCII.GetBytes("FILE").CopyTo(rec, 0);

        int sectorsInRecord = FileRecordSize / BytesPerSector;
        ushort usaOffset = 0x30;
        ushort usaCount = (ushort)(sectorsInRecord + 1);
        BinWrite.U16(rec, 0x04, usaOffset);
        BinWrite.U16(rec, 0x06, usaCount);

        ushort flags = 0;
        if (inUse) flags |= 0x0001;
        if (isDirectory) flags |= 0x0002;
        BinWrite.U16(rec, 0x16, flags);

        int attrOffset = BinWrite.Align8(usaOffset + usaCount * 2);
        BinWrite.U16(rec, 0x14, (ushort)attrOffset);

        int endPos = writeAttributes(rec, attrOffset);
        BinWrite.U32(rec, endPos, 0xFFFFFFFF); // attribute list terminator

        // Fixup no-op: NtfsMftParser.ApplyFixup copies the USA array's stored
        // "backup" bytes over each sector's last 2 bytes. Real NTFS uses this
        // to detect torn writes; our synthetic record was never torn, so we
        // pre-fill the USA slots with whatever is already at each sector-end,
        // making the restore step an identity operation instead of corrupting
        // the attribute bytes we just wrote.
        for (int i = 1; i < usaCount; i++)
        {
            int sectorEnd = i * BytesPerSector - 2;
            int fixupPos = usaOffset + i * 2;
            if (sectorEnd + 2 <= rec.Length && fixupPos + 2 <= rec.Length)
            {
                rec[fixupPos] = rec[sectorEnd];
                rec[fixupPos + 1] = rec[sectorEnd + 1];
            }
        }

        return rec;
    }

    private static int WriteFileNameAttribute(byte[] rec, int pos, long parentRef, DateTime modified, string name, byte nameSpace = 1)
    {
        const int valueOffset = 0x18;
        byte[] nameBytes = Encoding.Unicode.GetBytes(name);
        int valueLength = 0x42 + nameBytes.Length;
        int attrLen = BinWrite.Align8(valueOffset + valueLength);

        BinWrite.U32(rec, pos + 0, 0x30);
        BinWrite.U32(rec, pos + 4, (uint)attrLen);
        rec[pos + 8] = 0;  // resident
        rec[pos + 9] = 0;  // attribute name length
        BinWrite.U32(rec, pos + 0x10, (uint)valueLength);
        BinWrite.U16(rec, pos + 0x14, valueOffset);

        int vp = pos + valueOffset;
        BinWrite.I64(rec, vp + 0x00, parentRef & 0x0000FFFFFFFFFFFF);
        BinWrite.I64(rec, vp + 0x08, 0);                        // creation time (unused by parser)
        BinWrite.I64(rec, vp + 0x10, modified.ToFileTimeUtc()); // content modification time — the field the parser reads
        BinWrite.I64(rec, vp + 0x18, 0);                        // MFT modification time (unused by parser)
        rec[vp + 0x40] = (byte)name.Length;
        rec[vp + 0x41] = nameSpace;
        nameBytes.CopyTo(rec, vp + 0x42);

        return pos + attrLen;
    }

    private static int WriteResidentDataAttribute(byte[] rec, int pos, byte[] data)
    {
        const int valueOffset = 0x18;
        int attrLen = BinWrite.Align8(valueOffset + data.Length);

        BinWrite.U32(rec, pos + 0, 0x80);
        BinWrite.U32(rec, pos + 4, (uint)attrLen);
        rec[pos + 8] = 0; // resident
        rec[pos + 9] = 0; // unnamed (primary) stream
        BinWrite.U32(rec, pos + 0x10, (uint)data.Length); // 4-byte value-length field per NTFS attribute header spec
        BinWrite.U16(rec, pos + 0x14, valueOffset);
        data.CopyTo(rec, pos + valueOffset);

        return pos + attrLen;
    }

    private static int WriteNonResidentDataAttribute(byte[] rec, int pos, long realSize, List<(long Lcn, long ClusterCount)> runs)
    {
        const int dataRunOffset = 0x40;
        byte[] runBytes = EncodeDataRuns(runs);
        int attrLen = BinWrite.Align8(dataRunOffset + runBytes.Length);

        BinWrite.U32(rec, pos + 0, 0x80);
        BinWrite.U32(rec, pos + 4, (uint)attrLen);
        rec[pos + 8] = 1; // non-resident
        rec[pos + 9] = 0; // unnamed (primary) stream
        BinWrite.U16(rec, pos + 0x20, dataRunOffset);
        long allocatedSize = runs.Sum(r => r.ClusterCount) * BytesPerCluster;
        BinWrite.I64(rec, pos + 0x28, allocatedSize);
        BinWrite.I64(rec, pos + 0x30, realSize);
        BinWrite.I64(rec, pos + 0x38, realSize);
        runBytes.CopyTo(rec, pos + dataRunOffset);

        return pos + attrLen;
    }

    /// <summary>Encodes NTFS data runs: (header byte, length bytes, signed LCN-delta bytes)* then a 0x00 terminator.</summary>
    private static byte[] EncodeDataRuns(List<(long Lcn, long ClusterCount)> runs)
    {
        var bytes = new List<byte>();
        long prevLcn = 0;
        foreach (var (lcn, count) in runs)
        {
            byte[] lenBytes = MinimalUnsignedBytes(count);
            long delta = lcn - prevLcn;
            byte[] offBytes = MinimalSignedBytes(delta);
            byte header = (byte)((offBytes.Length << 4) | lenBytes.Length);
            bytes.Add(header);
            bytes.AddRange(lenBytes);
            bytes.AddRange(offBytes);
            prevLcn = lcn;
        }
        bytes.Add(0x00); // terminator
        return bytes.ToArray();
    }

    private static byte[] MinimalUnsignedBytes(long value)
    {
        var full = BitConverter.GetBytes(value); // little-endian, 8 bytes
        int len = 1;
        while (len < 8 && full[len] != 0) len++;
        return full[..len];
    }

    /// <summary>Smallest byte-length (1, 2, 4, or 8) whose two's-complement range holds <paramref name="value"/>.</summary>
    private static byte[] MinimalSignedBytes(long value)
    {
        var full = BitConverter.GetBytes(value);
        foreach (int len in new[] { 1, 2, 4, 8 })
        {
            long min = len == 8 ? long.MinValue : -(1L << (8 * len - 1));
            long max = len == 8 ? long.MaxValue : (1L << (8 * len - 1)) - 1;
            if (value >= min && value <= max) return full[..len];
        }
        return full; // unreachable (8 bytes always fits a long)
    }
}
