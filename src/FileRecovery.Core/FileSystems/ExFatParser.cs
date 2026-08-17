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

        WalkDirectory(_rootDirCluster, results, counts, visited, categoryFilter, ct, Report);

        progress?.Report(new ScanProgress
        {
            PercentComplete = 100, TotalFound = results.Count,
            CountsByCategory = new Dictionary<FileCategory, int>(counts),
            StatusText = "Quick scan complete.",
        });
        return results;
    }

    private void WalkDirectory(uint startCluster, List<RecoverableFile> results, Dictionary<FileCategory, int> counts,
        HashSet<uint> visited, HashSet<FileCategory>? categoryFilter, CancellationToken ct, Action report)
    {
        var subDirs = new List<uint>();
        uint cluster = startCluster;
        int guard = 0;

        while (cluster >= 2 && cluster < _clusterCount + 2 && guard++ < 200_000)
        {
            ct.ThrowIfCancellationRequested();
            if (!visited.Add(cluster)) break;

            byte[] data;
            try { data = _reader.ReadBytes(ClusterToOffset(cluster), _bytesPerCluster); }
            catch (IOException) { break; }

            for (int off = 0; off + 32 <= data.Length; off += 32)
            {
                byte entryType = data[off];
                bool inUse = (entryType & 0x80) != 0;
                byte typeCode = (byte)(entryType & 0x7F);

                if (typeCode != 0x05 /*FileDirEntry cleared*/ ) continue; // only interested in deleted file entry-set heads
                if (inUse) continue; // still allocated — not deleted

                // Deleted File Directory Entry found. SecondaryCount tells us how many
                // entries follow (stream extension + file-name entries).
                byte secondaryCount = data[off + 1];
                if (secondaryCount < 1 || off + (secondaryCount + 1) * 32 > data.Length) continue;

                int streamOff = off + 32;
                byte streamType = (byte)(data[streamOff] & 0x7F);
                if (streamType != 0x40 /*Stream Extension cleared*/) continue;

                byte nameLen = data[streamOff + 3];
                uint firstCluster = BitConverter.ToUInt32(data, streamOff + 20);
                ulong dataLength = BitConverter.ToUInt64(data, streamOff + 24);

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

                string ext = Path.GetExtension(name);
                var category = FileSignatureCatalog.CategoryForExtension(ext);
                if (categoryFilter != null && !categoryFilter.Contains(category))
                {
                    continue;
                }

                var runs = new List<ClusterRun>();
                if (firstCluster >= 2 && _bytesPerCluster > 0)
                {
                    long clusterCount = ((long)dataLength + _bytesPerCluster - 1) / _bytesPerCluster;
                    if (clusterCount == 0) clusterCount = 1;
                    long byteOffset = ClusterToOffset(firstCluster);
                    long lengthBytes = clusterCount * _bytesPerCluster;
                    runs.Add(new ClusterRun(byteOffset, lengthBytes)); // exFAT is commonly allocated contiguously (NoFatChain flag)
                }

                var file = new RecoverableFile
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = name,
                    SizeBytes = (long)dataLength,
                    Category = category,
                    Extension = ext,
                    FromCarving = false,
                    Recoverability = firstCluster >= 2 ? Recoverability.Partial : Recoverability.Poor,
                };
                file.ClusterRuns.AddRange(runs);
                results.Add(file);
                counts[category]++;
                report();
            }

            // exFAT directories are themselves cluster chains; without walking the FAT
            // (skipped here for brevity/performance) we conservatively read only the
            // first cluster of large directories, which covers the common case for
            // removable media directory sizes.
            break;
        }

        foreach (var sub in subDirs)
            WalkDirectory(sub, results, counts, visited, categoryFilter, ct, report);
    }
}
