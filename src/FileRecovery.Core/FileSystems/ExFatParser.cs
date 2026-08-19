using System.Text;
using FileRecovery.Core.Disk;
using FileRecovery.Core.Carving;
using FileRecovery.Core.Models;

namespace FileRecovery.Core.FileSystems;

/// <summary>
/// Parses an exFAT volume. exFAT directory entries come in "entry sets":
/// a File Directory Entry (0x85) followed by a Stream Extension entry (0xC0)
/// and one or more File Name entries (0xC1). The high bit of EntryType
/// (0x80) marks the entry as "in use"; when a file is deleted, Windows
/// clears that bit (0x85 -> 0x05, 0xC0 -> 0x40, 0xC1 -> 0x41) but leaves the
/// bytes on disk — exactly the residue an undelete tool looks for.
/// </summary>
public sealed class ExFatParser
{
    private readonly IRawReader _reader;
    private int _bytesPerSectorShift;
    private int _sectorsPerClusterShift;
    private long _fatOffsetSectors;
    private long _clusterHeapOffsetSectors;
    private uint _clusterCount;
    private uint _rootDirCluster;
    private int _bytesPerSector;
    private int _bytesPerCluster;

    public int BytesPerCluster => _bytesPerCluster;

    public ExFatParser(IRawReader reader) => _reader = reader;

    public bool TryReadBootSector()
    {
        byte[] boot = _reader.ReadBytes(0, 512);
        string oemName = Encoding.ASCII.GetString(boot, 3, 8);
        if (!oemName.StartsWith("EXFAT")) return false;

        _fatOffsetSectors = BitConverter.ToUInt32(boot, 80);
        _clusterHeapOffsetSectors = BitConverter.ToUInt32(boot, 88);
        _clusterCount = BitConverter.ToUInt32(boot, 92);
        _rootDirCluster = BitConverter.ToUInt32(boot, 96);
        _bytesPerSectorShift = boot[108];
        _sectorsPerClusterShift = boot[109];

        _bytesPerSector = 1 << _bytesPerSectorShift;
        _bytesPerCluster = _bytesPerSector << _sectorsPerClusterShift;
        return _bytesPerSector is >= 512 and <= 4096;
    }

    private long ClusterToOffset(uint cluster) =>
        (_clusterHeapOffsetSectors + (long)(cluster - 2) * (1 << _sectorsPerClusterShift)) * _bytesPerSector;

    /// <summary>Reads one 32-bit exFAT FAT entry for the given cluster.</summary>
    private uint ReadFatEntry(uint cluster)
    {
        long offset = _fatOffsetSectors * _bytesPerSector + (long)cluster * 4;
        byte[] bytes = _reader.ReadBytes(offset, 4);
        return BitConverter.ToUInt32(bytes, 0);
    }

    /// <summary>
    /// Enumerates the clusters belonging to a directory or file stream.
    ///
    /// exFAT lets a stream be marked "NoFatChain" (GeneralSecondaryFlags bit 1)
    /// when it's contiguously allocated — and critically, the FAT is allowed to
    /// be left unpopulated (zeroed) for such streams, since there's no chain to
    /// record. Walking the FAT for a NoFatChain stream would see those zeros as
    /// "free/end of chain" and stop after one cluster — silently truncating the
    /// very common case of an unfragmented directory. So NoFatChain streams are
    /// enumerated by simple cluster-number increment instead of a FAT lookup;
    /// only genuinely fragmented (chain-allocated) streams walk the FAT.
    /// </summary>
    private IEnumerable<uint> EnumerateClusters(uint firstCluster, long dataLength, bool noFatChain, int maxClusters = 200_000)
    {
        if (firstCluster < 2) yield break;

        if (noFatChain)
        {
            long clusterCount = _bytesPerCluster > 0 ? (dataLength + _bytesPerCluster - 1) / _bytesPerCluster : 0;
            if (clusterCount <= 0) clusterCount = 1;
            for (long i = 0; i < clusterCount && i < maxClusters; i++)
            {
                uint c = (uint)(firstCluster + i);
                if (c < 2 || c >= _clusterCount + 2) yield break;
                yield return c;
            }
        }
        else
        {
            uint cluster = firstCluster;
            var seen = new HashSet<uint>();
            int count = 0;
            while (cluster >= 2 && cluster < 0xFFFFFFF7 && count < maxClusters)
            {
                if (!seen.Add(cluster)) yield break; // cycle guard against a corrupted chain
                yield return cluster;
                count++;
                uint next = ReadFatEntry(cluster);
                if (next < 2 || next >= 0xFFFFFFF7) yield break; // free, bad, or end-of-chain marker
                cluster = next;
            }
        }
    }

    public List<RecoverableFile> ScanDeletedEntries(IProgress<ScanProgress>? progress, CancellationToken ct,
        HashSet<FileCategory>? categoryFilter)
    {
        var results = new List<RecoverableFile>();
        var counts = Enum.GetValues<FileCategory>().ToDictionary(c => c, _ => 0);
        var visited = new HashSet<uint>();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        void Report()
        {
            if (sw.ElapsedMilliseconds < 200) return;
            progress?.Report(new ScanProgress
            {
                CountsByCategory = new Dictionary<FileCategory, int>(counts),
                TotalFound = results.Count,
                StatusText = $"Scanning exFAT directory entries… {results.Count} found",
                PercentComplete = Math.Min(95, visited.Count * 100.0 / Math.Max(1, _clusterCount)),
            });
            sw.Restart();
        }

        // The root directory is always FAT-chain allocated per the exFAT spec —
        // there's no Stream Extension entry for the root to carry a NoFatChain flag.
        WalkDirectory(_rootDirCluster, dataLength: 0, noFatChain: false, results, counts, visited, categoryFilter, ct, Report);

        progress?.Report(new ScanProgress
        {
            PercentComplete = 100, TotalFound = results.Count,
            CountsByCategory = new Dictionary<FileCategory, int>(counts),
            StatusText = "Quick scan complete.",
        });
        return results;
    }

    private void WalkDirectory(uint startCluster, long dataLength, bool noFatChain, List<RecoverableFile> results,
        Dictionary<FileCategory, int> counts, HashSet<uint> visited, HashSet<FileCategory>? categoryFilter,
        CancellationToken ct, Action report)
    {
        var subDirs = new List<(uint FirstCluster, long DataLength, bool NoFatChain)>();

        foreach (uint cluster in EnumerateClusters(startCluster, dataLength, noFatChain))
        {
            ct.ThrowIfCancellationRequested();
            if (!visited.Add(cluster)) continue;

            byte[] data;
            try { data = _reader.ReadBytes(ClusterToOffset(cluster), _bytesPerCluster); }
            catch (IOException) { continue; }

            for (int off = 0; off + 32 <= data.Length; off += 32)
            {
                byte entryType = data[off];
                bool inUse = (entryType & 0x80) != 0;
                byte typeCode = (byte)(entryType & 0x7F);

                if (typeCode != 0x05 /*FileDirEntry, cleared-of-in-use-bit form*/) continue;

                byte secondaryCount = data[off + 1];
                if (secondaryCount < 1 || off + (secondaryCount + 1) * 32 > data.Length) continue;

                int streamOff = off + 32;
                bool streamInUse = (data[streamOff] & 0x80) != 0;
                byte streamType = (byte)(data[streamOff] & 0x7F);
                if (streamType != 0x40 /*Stream Extension, cleared form*/) continue;
                if (inUse != streamInUse) continue; // entry-set head and stream extension disagree — corrupted, skip

                ushort attributes = BitConverter.ToUInt16(data, off + 4);
                bool isDirectory = (attributes & 0x10) != 0;

                byte generalSecondaryFlags = data[streamOff + 1];
                bool subNoFatChain = (generalSecondaryFlags & 0x02) != 0;
                byte nameLen = data[streamOff + 3];
                uint firstCluster = BitConverter.ToUInt32(data, streamOff + 20);
                ulong dataLen = BitConverter.ToUInt64(data, streamOff + 24);

                var nameBuilder = new StringBuilder();
                for (int n = 2; n <= secondaryCount; n++)
                {
                    int nameOff = off + n * 32;
                    if (nameOff + 32 > data.Length) break;
                    byte nameEntryType = (byte)(data[nameOff] & 0x7F);
                    if (nameEntryType != 0x41 /*File Name cleared*/) break;
                    for (int c = 0; c < 15; c++)
                    {
                        int cp = nameOff + 2 + c * 2;
                        if (cp + 2 > data.Length) break;
                        char ch = (char)BitConverter.ToUInt16(data, cp);
                        if (ch == 0) break;
                        nameBuilder.Append(ch);
                    }
                }

                string name = nameBuilder.Length > 0 ? nameBuilder.ToString() : $"_recovered_{firstCluster:X}";
                if (name.Length > (int)nameLen && nameLen > 0) name = name[..nameLen];

                if (inUse)
                {
                    // A live, intact directory — recurse into it so deleted files inside
                    // still-existing folders are found too (the common real case: the
                    // user deleted a FILE, not the folder it was in).
                    if (isDirectory && firstCluster >= 2)
                        subDirs.Add((firstCluster, (long)dataLen, subNoFatChain));
                    continue;
                }

                if (isDirectory) continue; // a deleted folder itself isn't a recoverable "file"

                string ext = Path.GetExtension(name);
                var category = FileSignatureCatalog.CategoryForExtension(ext);
                if (categoryFilter != null && !categoryFilter.Contains(category))
                {
                    continue;
                }

                var runs = new List<ClusterRun>();
                if (firstCluster >= 2 && _bytesPerCluster > 0)
                {
                    long clusterCount = ((long)dataLen + _bytesPerCluster - 1) / _bytesPerCluster;
                    if (clusterCount == 0) clusterCount = 1;
                    long byteOffset = ClusterToOffset(firstCluster);
                    long lengthBytes = clusterCount * _bytesPerCluster;
                    runs.Add(new ClusterRun(byteOffset, lengthBytes)); // exFAT is commonly allocated contiguously (NoFatChain flag)
                }

                var file = new RecoverableFile
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = name,
                    SizeBytes = (long)dataLen,
                    Category = category,
                    Extension = ext,
                    FromCarving = false,
                    Recoverability = EstimateRecoverability(runs, ext, (long)dataLen, firstCluster >= 2),
                };
                file.ClusterRuns.AddRange(runs);
                results.Add(file);
                counts[category]++;
                report();
            }
        }

        foreach (var sub in subDirs)
        {
            ct.ThrowIfCancellationRequested();
            WalkDirectory(sub.FirstCluster, sub.DataLength, sub.NoFatChain, results, counts, visited, categoryFilter, ct, report);
        }
    }

    /// <summary>
    /// exFAT is commonly (though not always) allocated contiguously, but this
    /// cluster location is still an assumption, not a confirmed chain. This
    /// peeks at the actual bytes there and checks whether they look like an
    /// intact file of the claimed type instead of reporting a blind
    /// "cluster number is in range" guess. Falls back to that bounds-based
    /// heuristic for extensions with no structural validator, or if the
    /// validation read itself fails for any reason.
    /// </summary>
    private const int ValidationWindowBytes = 65536;

    private Recoverability EstimateRecoverability(List<ClusterRun> runs, string extension, long sizeBytes, bool clusterInBounds)
    {
        Recoverability fallback = clusterInBounds ? Recoverability.Partial : Recoverability.Poor;
        if (runs.Count == 0 || sizeBytes <= 0) return fallback;

        try
        {
            long byteOffset = runs[0].ByteOffset;
            int headLen = (int)Math.Min(sizeBytes, ValidationWindowBytes);
            byte[] head = _reader.ReadBytes(byteOffset, headLen);

            byte[] tail;
            if (sizeBytes > headLen)
            {
                int tailLen = (int)Math.Min(sizeBytes, ValidationWindowBytes);
                long tailOffset = byteOffset + sizeBytes - tailLen;
                tail = _reader.ReadBytes(tailOffset, tailLen);
            }
            else
            {
                tail = head;
            }

            var result = RecoveredFileValidator.Validate(head, tail, extension, sizeBytes);
            return result switch
            {
                StructuralValidation.Valid => Recoverability.Excellent,
                StructuralValidation.HeaderOnlyValid => Recoverability.Partial,
                StructuralValidation.Invalid => Recoverability.Poor,
                _ => fallback,
            };
        }
        catch (IOException)
        {
            return fallback;
        }
    }
}
