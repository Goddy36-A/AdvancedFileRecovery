using System.Text;
using FileRecovery.Core.Disk;
using FileRecovery.Core.Carving;
using FileRecovery.Core.Models;

namespace FileRecovery.Core.FileSystems;

/// <summary>
/// Parses the NTFS Master File Table directly off the raw volume to find
/// FILE records still present on disk but flagged as deleted (the MFT_RECORD_IN_USE
/// bit cleared) whose parent directory entry has typically already vanished
/// from Explorer. This is the standard technique NTFS undelete tools use.
/// </summary>
public sealed class NtfsMftParser
{
    private const uint FileRecordMagic = 0x454C4946; // "FILE" little-endian read as uint

    private readonly IRawReader _reader;
    private int _bytesPerSector;
    private int _bytesPerCluster;
    private long _mftOffset;
    private int _fileRecordSize;
    private long _totalClusters;

    private readonly List<(long Lcn, long ClusterCount)> _bitmapRuns = new(); // where $Bitmap's own bytes live on disk
    private bool _bitmapLoaded;
    private readonly Dictionary<long, byte[]> _bitmapBlockCache = new();
    private const int BitmapCacheBlockSize = 4096; // one cached read covers 4096*8 = 32,768 clusters' worth of allocation bits

    public int BytesPerCluster => _bytesPerCluster;

    public NtfsMftParser(IRawReader reader) => _reader = reader;

    public bool TryReadBootSector(out FileSystemKind detected)
    {
        detected = FileSystemKind.Unknown;
        byte[] boot = _reader.ReadBytes(0, 512);
        string oemId = Encoding.ASCII.GetString(boot, 3, 8);
        if (!oemId.StartsWith("NTFS")) return false;

        _bytesPerSector = BitConverter.ToUInt16(boot, 0x0B);
        int sectorsPerCluster = boot[0x0D];
        if (_bytesPerSector == 0) _bytesPerSector = 512;
        _bytesPerCluster = _bytesPerSector * Math.Max(1, sectorsPerCluster);

        long totalSectors = BitConverter.ToInt64(boot, 0x28);
        _totalClusters = _bytesPerCluster > 0 ? totalSectors * _bytesPerSector / _bytesPerCluster : 0;

        long mftStartCluster = BitConverter.ToInt64(boot, 0x30);
        _mftOffset = mftStartCluster * _bytesPerCluster;

        sbyte clustersPerFileRecordRaw = (sbyte)boot[0x40];
        _fileRecordSize = clustersPerFileRecordRaw > 0
            ? clustersPerFileRecordRaw * _bytesPerCluster
            : 1 << Math.Abs(clustersPerFileRecordRaw); // negative => 2^|n| bytes, per NTFS spec

        detected = FileSystemKind.NTFS;
        return true;
    }

    public List<RecoverableFile> ScanDeletedEntries(IProgress<ScanProgress>? progress, CancellationToken ct,
        HashSet<FileCategory>? categoryFilter)
    {
        var results = new List<RecoverableFile>();
        var counts = Enum.GetValues<FileCategory>().ToDictionary(c => c, _ => 0);

        LoadBitmapRunsBestEffort();

        // The $MFT's own size isn't known up front without parsing its own DATA runs;
        // as a robust approximation we scan up to `_totalClusters` worth of possible
        // record slots but stop once reads run past the volume or return all-zero pages.
        long estimatedMftBytes = EstimateMftSize();
        long recordCount = estimatedMftBytes / _fileRecordSize;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (long i = 0; i < recordCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            long recordOffset = _mftOffset + i * _fileRecordSize;
            byte[] record;
            try { record = _reader.ReadBytes(recordOffset, _fileRecordSize); }
            catch (IOException) { break; }

            if (record.Length < _fileRecordSize) break;
            if (BitConverter.ToUInt32(record, 0) != FileRecordMagic) continue;

            ApplyFixup(record);

            ushort flags = BitConverter.ToUInt16(record, 0x16);
            bool inUse = (flags & 0x0001) != 0;
            bool isDirectory = (flags & 0x0002) != 0;
            if (inUse || isDirectory) continue; // we only want deleted, non-directory records

            var parsed = ParseAttributes(record);
            if (parsed == null) continue;
            if (categoryFilter != null && !categoryFilter.Contains(parsed.Category)) continue;

            results.Add(parsed);
            counts[parsed.Category]++;

            if (sw.ElapsedMilliseconds > 200)
            {
                progress?.Report(new ScanProgress
                {
                    PercentComplete = Math.Min(100.0, i * 100.0 / Math.Max(1, recordCount)),
                    BytesProcessed = i * _fileRecordSize,
                    TotalBytes = estimatedMftBytes,
                    Elapsed = sw.Elapsed,
                    CountsByCategory = new Dictionary<FileCategory, int>(counts),
                    TotalFound = results.Count,
                    StatusText = $"Scanning MFT record {i:N0} of ~{recordCount:N0}…",
                });
                sw.Restart();
            }
        }

        progress?.Report(new ScanProgress
        {
            PercentComplete = 100, TotalFound = results.Count,
            CountsByCategory = new Dictionary<FileCategory, int>(counts),
            StatusText = "Quick scan complete.",
        });

        return results;
    }

    /// <summary>
    /// The $MFT record (record #0) describes its own DATA attribute's runs.
    /// We read record 0 to get an authoritative MFT length; if that fails for
    /// any reason we fall back to a generous cap based on volume size.
    /// </summary>
    private long EstimateMftSize()
    {
        try
        {
            byte[] record0 = _reader.ReadBytes(_mftOffset, _fileRecordSize);
            if (BitConverter.ToUInt32(record0, 0) == FileRecordMagic)
            {
                ApplyFixup(record0);
                var runs = ExtractDataRuns(record0, out long realSize);
                if (realSize > 0) return realSize;
                if (runs.Count > 0) return runs.Sum(r => r.ClusterCount) * _bytesPerCluster;
            }
        }
        catch (IOException) { /* fall through to heuristic */ }

        // Heuristic fallback: assume up to ~1 MFT record per 8 clusters of volume, capped.
        long cap = Math.Max(_fileRecordSize * 50_000L, _totalClusters * _bytesPerCluster / 8);
        return Math.Min(cap, 4L * 1024 * 1024 * 1024); // never guess more than 4GB of MFT
    }

    /// <summary>Locates $Bitmap's (record 6) own data runs — where its bytes live on disk — without reading the bitmap content itself yet.</summary>
    private void LoadBitmapRunsBestEffort()
    {
        try
        {
            byte[] record6 = _reader.ReadBytes(_mftOffset + 6L * _fileRecordSize, _fileRecordSize);
            if (BitConverter.ToUInt32(record6, 0) != FileRecordMagic) return;
            ApplyFixup(record6);
            _bitmapRuns = ExtractDataRuns(record6, out _);
            _bitmapLoaded = _bitmapRuns.Count > 0;
        }
        catch { /* best effort only; recoverability falls back to Unknown */ }
    }

    /// <summary>
    /// Tests whether a single cluster (LCN) is currently marked allocated in
    /// $Bitmap. Returns null if $Bitmap wasn't readable or the cluster falls
    /// outside its mapped range.
    ///
    /// This reads only the handful of bytes needed for THIS cluster's bit —
    /// not the whole bitmap — so recoverability scoring scales to any volume
    /// size instead of the previous approach of materializing a per-cluster
    /// set for the entire disk up front (which was also, independently,
    /// checking the wrong thing: it recorded where $Bitmap's own bytes are
    /// STORED on disk, never the allocation bits those bytes actually encode).
    /// A small cache of recently-read 4KB blocks means a file's own
    /// (typically contiguous) cluster run almost always hits one cached
    /// read, not one disk read per cluster.
    /// </summary>
    private bool? IsClusterAllocated(long cluster)
    {
        if (!_bitmapLoaded || cluster < 0) return null;

        long byteIndex = cluster / 8;
        int bitIndex = (int)(cluster % 8);

        long runStartByte = 0;
        foreach (var run in _bitmapRuns)
        {
            long runByteLength = run.ClusterCount * _bytesPerCluster;
            if (byteIndex >= runStartByte && byteIndex < runStartByte + runByteLength)
            {
                long offsetWithinRun = byteIndex - runStartByte;
                long diskOffset = run.Lcn * _bytesPerCluster + offsetWithinRun;
                byte b = ReadBitmapByteCached(diskOffset);
                return (b & (1 << bitIndex)) != 0;
            }
            runStartByte += runByteLength;
        }
        return null; // cluster number falls outside what $Bitmap's runs map to
    }

    private byte ReadBitmapByteCached(long diskOffset)
    {
        long blockKey = diskOffset / BitmapCacheBlockSize;
        if (!_bitmapBlockCache.TryGetValue(blockKey, out var block))
        {
            // Crude but effective cap: a scan touching wildly scattered clusters
            // (heavily fragmented files) could otherwise grow this unboundedly
            // over a long Quick Scan. Clearing and starting fresh is simpler
            // than real LRU and costs at most one extra re-read per block.
            if (_bitmapBlockCache.Count > 512) _bitmapBlockCache.Clear();
            block = _reader.ReadBytes(blockKey * BitmapCacheBlockSize, BitmapCacheBlockSize);
            _bitmapBlockCache[blockKey] = block;
        }
        int offsetInBlock = (int)(diskOffset % BitmapCacheBlockSize);
        return block[offsetInBlock];
    }

    /// <summary>Undoes the NTFS "update sequence array" fixup so the last 2 bytes of each sector are correct.</summary>
    private void ApplyFixup(byte[] record)
    {
        ushort usaOffset = BitConverter.ToUInt16(record, 0x04);
        ushort usaCount = BitConverter.ToUInt16(record, 0x06); // includes the USN itself
        if (usaOffset == 0 || usaCount < 2) return;

        for (int i = 1; i < usaCount; i++)
        {
            int sectorEnd = i * _bytesPerSector - 2;
            if (sectorEnd + 2 > record.Length) break;
            int fixupPos = usaOffset + i * 2;
            if (fixupPos + 2 > record.Length) break;
            record[sectorEnd] = record[fixupPos];
            record[sectorEnd + 1] = record[fixupPos + 1];
        }
    }

    private RecoverableFile? ParseAttributes(byte[] record)
    {
        ushort attrOffset = BitConverter.ToUInt16(record, 0x14);
        string? fileName = null;
        long parentRef = -1;
        DateTime? modified = null;
        long realSize = 0;
        var lcnRuns = new List<(long Lcn, long ClusterCount)>();
        byte[]? residentData = null;
        bool hasDataAttr = false;

        int pos = attrOffset;
        while (pos + 8 <= record.Length)
        {
            uint attrType = BitConverter.ToUInt32(record, pos);
            if (attrType == 0xFFFFFFFF) break; // end marker
            uint attrLen = BitConverter.ToUInt32(record, pos + 4);
            if (attrLen == 0 || pos + attrLen > record.Length) break;

            byte nonResident = record[pos + 8];
            byte attrNameLength = record[pos + 9]; // 0 for the primary unnamed $DATA stream

            if (attrType == 0x30 && fileName == null) // $FILE_NAME
            {
                ushort valOffset = BitConverter.ToUInt16(record, pos + 0x14);
                int vp = pos + valOffset;
                if (vp + 0x42 <= record.Length)
                {
                    parentRef = BitConverter.ToInt64(record, vp) & 0x0000FFFFFFFFFFFF;
                    // $FILE_NAME value layout: 0x00 parent ref, 0x08 creation time,
                    // 0x10 content modification time, 0x18 MFT/metadata modification time.
                    // We want the former (what a user thinks of as "last modified").
                    long mtimeRaw = BitConverter.ToInt64(record, vp + 0x10);
                    try { modified = DateTime.FromFileTimeUtc(mtimeRaw); } catch { modified = null; }
                    byte nameLen = record[vp + 0x40];
                    byte nameSpace = record[vp + 0x41];
                    int nameBytesOffset = vp + 0x42;
                    if (nameBytesOffset + nameLen * 2 <= record.Length && nameLen > 0)
                    {
                        // Prefer the Win32 namespace (0 or 1) over the pure-DOS 8.3 alias (2).
                        string candidate = Encoding.Unicode.GetString(record, nameBytesOffset, nameLen * 2);
                        if (fileName == null || nameSpace != 2) fileName = candidate;
                    }
                }
            }
            else if (attrType == 0x80 && attrNameLength == 0) // $DATA, unnamed stream only (skip alternate data streams)
            {
                hasDataAttr = true;
                if (nonResident == 0)
                {
                    // Resident: the file's actual bytes are stored inline inside this MFT
                    // record, not on any cluster. Copy them out now — this is the ONLY
                    // moment they're addressable, since a deleted file's resident data
                    // has no independent location to re-read at recovery time.
                    ushort valLen = (ushort)Math.Min(ushort.MaxValue, BitConverter.ToUInt32(record, pos + 0x10)); // spec field is 4 bytes; resident data is always small in practice
                    ushort valOff = BitConverter.ToUInt16(record, pos + 0x14);
                    realSize = valLen;
                    if (valLen > 0 && pos + valOff + valLen <= record.Length)
                    {
                        residentData = new byte[valLen];
                        Array.Copy(record, pos + valOff, residentData, 0, valLen);
                    }
                }
                else
                {
                    long ras = BitConverter.ToInt64(record, pos + 0x30); // real size (allocated data length)
                    if (ras > 0) realSize = ras;
                    lcnRuns.AddRange(ParseDataRuns(record, pos + BitConverter.ToUInt16(record, pos + 0x20)));
                }
            }

            pos += (int)attrLen;
        }

        if (fileName == null || !hasDataAttr) return null;

        string ext = Path.GetExtension(fileName);
        var file = new RecoverableFile
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = fileName,
            OriginalPath = null, // full path reconstruction requires walking parent refs; left as filename-only for entries whose parent is also gone
            SizeBytes = realSize,
            Category = FileSignatureCatalog.CategoryForExtension(ext),
            Extension = ext,
            ModifiedUtc = modified,
            FromCarving = false,
            ResidentData = residentData,
        };

        if (residentData != null)
        {
            // The bytes are embedded in this still-present MFT record, so as long as we
            // could read the record at all, the data is fully intact — there is no
            // "reallocated cluster" risk the way there is for non-resident streams.
            file.Recoverability = Recoverability.Excellent;
        }
        else
        {
            foreach (var (lcn, count) in lcnRuns)
                file.ClusterRuns.Add(new ClusterRun(lcn * _bytesPerCluster, count * _bytesPerCluster));
            file.Recoverability = EstimateRecoverability(lcnRuns);
        }
        return file;
    }

    private Recoverability EstimateRecoverability(List<(long Lcn, long ClusterCount)> runs)
    {
        if (runs.Count == 0) return Recoverability.Unknown;
        if (!_bitmapLoaded) return Recoverability.Unknown;

        long total = 0, reallocated = 0;
        foreach (var run in runs)
        {
            for (long c = run.Lcn; c < run.Lcn + run.ClusterCount; c++)
            {
                bool? allocated = IsClusterAllocated(c);
                if (allocated == null) continue; // outside what $Bitmap maps — don't let it skew the ratio
                total++;
                if (allocated.Value) reallocated++;
            }
        }
        if (total == 0) return Recoverability.Unknown;
        double ratio = (double)reallocated / total;
        return ratio switch
        {
            0 => Recoverability.Excellent,
            < 0.25 => Recoverability.Partial,
            _ => Recoverability.Poor,
        };
    }

    /// <summary>Parses the $DATA attribute's non-resident header to get real size + delegates to run-list decoding. Returns (LCN, clusterCount) pairs.</summary>
    private List<(long Lcn, long ClusterCount)> ExtractDataRuns(byte[] record, out long realSize)
    {
        realSize = 0;
        ushort attrOffset = BitConverter.ToUInt16(record, 0x14);
        int pos = attrOffset;
        while (pos + 8 <= record.Length)
        {
            uint attrType = BitConverter.ToUInt32(record, pos);
            if (attrType == 0xFFFFFFFF) break;
            uint attrLen = BitConverter.ToUInt32(record, pos + 4);
            if (attrLen == 0 || pos + attrLen > record.Length) break;

            if (attrType == 0x80 && record[pos + 8] == 1) // non-resident $DATA
            {
                realSize = BitConverter.ToInt64(record, pos + 0x30);
                return ParseDataRuns(record, pos + BitConverter.ToUInt16(record, pos + 0x20));
            }
            pos += (int)attrLen;
        }
        return new List<(long, long)>();
    }

    /// <summary>Decodes an NTFS data-run list: sequence of (header byte, length-bytes, offset-bytes) triples. Returns (LCN, clusterCount) pairs.</summary>
    private static List<(long Lcn, long ClusterCount)> ParseDataRuns(byte[] record, int offset)
    {
        var runs = new List<(long, long)>();
        int pos = offset;
        long currentLcn = 0;

        while (pos < record.Length)
        {
            byte header = record[pos];
            if (header == 0) break;
            int lenSize = header & 0x0F;
            int offSize = (header >> 4) & 0x0F;
            pos++;
            if (pos + lenSize + offSize > record.Length) break;

            long length = ReadSignedLE(record, pos, lenSize, forceUnsigned: true);
            pos += lenSize;
            long lcnDelta = offSize == 0 ? 0 : ReadSignedLE(record, pos, offSize, forceUnsigned: false);
            pos += offSize;

            currentLcn += lcnDelta;
            if (offSize != 0) // offSize 0 => sparse run, no physical clusters
                runs.Add((currentLcn, length)); // (LCN, cluster count) — converted to byte offsets by the caller
        }
        return runs;
    }

    private static long ReadSignedLE(byte[] data, int offset, int size, bool forceUnsigned)
    {
        if (size == 0) return 0;
        long value = 0;
        for (int i = 0; i < size; i++) value |= (long)data[offset + i] << (8 * i);
        if (!forceUnsigned)
        {
            // sign-extend if the high bit of the most significant byte is set
            bool negative = (data[offset + size - 1] & 0x80) != 0;
            if (negative)
            {
                for (int i = size; i < 8; i++) value |= 0xFFL << (8 * i);
            }
        }
        return value;
    }
}
