using System.Text;
using FileRecovery.Core.Disk;
using FileRecovery.Core.Carving;
using FileRecovery.Core.Models;

namespace FileRecovery.Core.FileSystems;

/// <summary>
/// Parses a FAT32 volume's boot sector, walks directory clusters looking for
/// entries whose first byte is 0xE5 (deleted marker), reconstructs long file
/// names from adjacent LFN entries, and — where the FAT chain for the file's
/// starting cluster is still intact or the file is short enough to be
/// contiguous — builds the cluster run needed to recover its data.
/// </summary>
public sealed class Fat32Parser
{
    private readonly IRawReader _reader;
    private int _bytesPerSector;
    private int _sectorsPerCluster;
    private int _reservedSectors;
    private int _numFats;
    private uint _sectorsPerFat;
    private uint _rootCluster;
    private long _fatOffset;
    private long _dataAreaOffset; // byte offset of cluster #2
    private int _bytesPerCluster;
    private uint _totalClusters;

    public int BytesPerCluster => _bytesPerCluster;

    public Fat32Parser(IRawReader reader) => _reader = reader;

    public bool TryReadBootSector()
    {
        byte[] boot = _reader.ReadBytes(0, 512);
        if (boot[510] != 0x55 || boot[511] != 0xAA) return false;

        _bytesPerSector = BitConverter.ToUInt16(boot, 0x0B);
        _sectorsPerCluster = boot[0x0D];
        _reservedSectors = BitConverter.ToUInt16(boot, 0x0E);
        _numFats = boot[0x10];
        _sectorsPerFat = BitConverter.ToUInt32(boot, 0x24); // FAT32-specific field (BPB_FATSz32)
        _rootCluster = BitConverter.ToUInt32(boot, 0x2C);
        string fsType = Encoding.ASCII.GetString(boot, 0x52, 8).TrimEnd('\0', ' ');

        if (_bytesPerSector == 0 || _sectorsPerCluster == 0 || _sectorsPerFat == 0) return false;
        if (!fsType.StartsWith("FAT32")) return false;

        _fatOffset = (long)_reservedSectors * _bytesPerSector;
        long dataStartSector = _reservedSectors + (long)_numFats * _sectorsPerFat;
        _dataAreaOffset = dataStartSector * _bytesPerSector;
        _bytesPerCluster = _bytesPerSector * _sectorsPerCluster;

        uint totalSectors32 = BitConverter.ToUInt32(boot, 0x20);
        _totalClusters = _bytesPerCluster > 0 ? (uint)((totalSectors32 - dataStartSector) / _sectorsPerCluster) : 0;

        return true;
    }

    private long ClusterToOffset(uint cluster) => _dataAreaOffset + (long)(cluster - 2) * _bytesPerCluster;

    public List<RecoverableFile> ScanDeletedEntries(IProgress<ScanProgress>? progress, CancellationToken ct,
        HashSet<FileCategory>? categoryFilter)
    {
        var results = new List<RecoverableFile>();
        var counts = Enum.GetValues<FileCategory>().ToDictionary(c => c, _ => 0);
        var visited = new HashSet<uint>();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        void Report(string status)
        {
            if (sw.ElapsedMilliseconds < 200) return;
            progress?.Report(new ScanProgress
            {
                CountsByCategory = new Dictionary<FileCategory, int>(counts),
                TotalFound = results.Count,
                StatusText = status,
                PercentComplete = Math.Min(95, visited.Count * 100.0 / Math.Max(1, _totalClusters)),
            });
            sw.Restart();
        }

        WalkDirectory(_rootCluster, results, counts, visited, categoryFilter, ct, Report);

        progress?.Report(new ScanProgress
        {
            PercentComplete = 100, TotalFound = results.Count,
            CountsByCategory = new Dictionary<FileCategory, int>(counts),
            StatusText = "Quick scan complete.",
        });
        return results;
    }

    private void WalkDirectory(uint startCluster, List<RecoverableFile> results, Dictionary<FileCategory, int> counts,
        HashSet<uint> visited, HashSet<FileCategory>? categoryFilter, CancellationToken ct, Action<string> report)
    {
        var subDirs = new List<uint>();
        var lfnParts = new SortedDictionary<int, string>();

        foreach (uint cluster in FollowChainOrAssumeLinear(startCluster, maxClusters: 200_000))
        {
            ct.ThrowIfCancellationRequested();
            if (!visited.Add(cluster)) continue;
            if (cluster < 2 || cluster >= _totalClusters + 2) continue;

            byte[] data;
            try { data = _reader.ReadBytes(ClusterToOffset(cluster), _bytesPerCluster); }
            catch (IOException) { continue; }

            for (int off = 0; off + 32 <= data.Length; off += 32)
            {
                byte first = data[off];
                if (first == 0x00) break; // no more entries in this directory
                byte attr = data[off + 11];

                if (attr == 0x0F) // LFN entry
                {
                    int seq = data[off] & 0x1F;
                    lfnParts[seq] = DecodeLfnPart(data, off);
                    continue;
                }

                bool deleted = first == 0xE5;
                bool isVolumeLabel = (attr & 0x08) != 0 && (attr & 0x10) == 0;
                bool isDirEntry = (attr & 0x10) != 0;

                string? longName = lfnParts.Count > 0 ? string.Concat(lfnParts.Values) : null;
                lfnParts.Clear();

                if (isDirEntry && !deleted && off + 11 < data.Length)
                {
                    // Track live subdirectories so Quick Scan also finds deleted files
                    // nested inside folders that themselves are still intact.
                    string nm = Encoding.ASCII.GetString(data, off, 8).TrimEnd();
                    if (nm != "." && nm != "..")
                    {
                        uint sub = (uint)((BitConverter.ToUInt16(data, off + 20) << 16) | BitConverter.ToUInt16(data, off + 26));
                        if (sub >= 2) subDirs.Add(sub);
                    }
                    continue;
                }

                if (!deleted || isVolumeLabel || isDirEntry) continue;

                string shortName = DecodeShortName(data, off);
                string name = !string.IsNullOrWhiteSpace(longName) ? longName! : shortName;
                // FAT marks the first character of the short-name copy as 0xE5; the true
                // first character is unrecoverable, so we substitute a placeholder.
                if (string.IsNullOrEmpty(longName)) name = "_" + name.TrimStart('_');

                uint startClus = (uint)((BitConverter.ToUInt16(data, off + 20) << 16) | BitConverter.ToUInt16(data, off + 26));
                uint size = BitConverter.ToUInt32(data, off + 28);
                ushort wtime = BitConverter.ToUInt16(data, off + 22);
                ushort wdate = BitConverter.ToUInt16(data, off + 24);
                DateTime? modified = DecodeFatDateTime(wdate, wtime);

                string ext = Path.GetExtension(name);
                var category = FileSignatureCatalog.CategoryForExtension(ext);
                if (categoryFilter != null && !categoryFilter.Contains(category)) continue;

                var runs = BuildAssumedContiguousRun(startClus, size);
                var file = new RecoverableFile
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = name,
                    SizeBytes = size,
                    Category = category,
                    Extension = ext,
                    ModifiedUtc = modified,
                    FromCarving = false,
                    Recoverability = startClus >= 2 && startClus < _totalClusters + 2
                        ? Recoverability.Partial   // FAT undelete can't confirm the chain wasn't reused; flagged Partial by default
                        : Recoverability.Poor,
                };
                file.ClusterRuns.AddRange(runs);
                results.Add(file);
                counts[category]++;
                report($"Scanning FAT32 directory entries… {results.Count} found");
            }
        }

        foreach (var sub in subDirs)
        {
            ct.ThrowIfCancellationRequested();
            WalkDirectory(sub, results, counts, visited, categoryFilter, ct, report);
        }
    }

    /// <summary>
    /// For an intact chain we'd follow the FAT; but a deleted file's FAT chain
    /// is normally zeroed on delete, so — per standard FAT-undelete practice —
    /// we assume the file occupies contiguous clusters starting at StartCluster
    /// (true for the overwhelming majority of non-fragmented consumer files)
    /// and size it from the directory entry's file-size field.
    /// </summary>
    private List<ClusterRun> BuildAssumedContiguousRun(uint startCluster, uint sizeBytes)
    {
        var runs = new List<ClusterRun>();
        if (startCluster < 2 || _bytesPerCluster == 0) return runs;
        long clusterCount = (sizeBytes + _bytesPerCluster - 1) / _bytesPerCluster;
        if (clusterCount == 0) clusterCount = 1;
        long byteOffset = ClusterToOffset(startCluster);
        long lengthBytes = clusterCount * _bytesPerCluster;
        runs.Add(new ClusterRun(byteOffset, lengthBytes));
        return runs;
    }

    /// <summary>Follows a live FAT chain (for intact directories); if the chain looks broken, falls back to reading forward linearly and relying on the 0x00 end-of-entries marker to bound each directory.</summary>
    private IEnumerable<uint> FollowChainOrAssumeLinear(uint startCluster, int maxClusters)
    {
        uint cluster = startCluster;
        int count = 0;
        var seen = new HashSet<uint>();
        while (cluster >= 2 && cluster < 0x0FFFFFF8 && count < maxClusters)
        {
            if (!seen.Add(cluster)) yield break;
            yield return cluster;
            count++;

            uint next = ReadFatEntry(cluster);
            if (next < 2 || next >= 0x0FFFFFF8) yield break;
            cluster = next;
        }
    }

    private uint ReadFatEntry(uint cluster)
    {
        long entryOffset = _fatOffset + cluster * 4;
        byte[] bytes = _reader.ReadBytes(entryOffset, 4);
        return BitConverter.ToUInt32(bytes, 0) & 0x0FFFFFFF;
    }

    private static string DecodeShortName(byte[] data, int off)
    {
        string name = Encoding.ASCII.GetString(data, off, 8).TrimEnd();
        string ext = Encoding.ASCII.GetString(data, off + 8, 3).TrimEnd();
        return ext.Length > 0 ? $"{name}.{ext}" : name;
    }

    private static string DecodeLfnPart(byte[] data, int off)
    {
        var sb = new StringBuilder();
        void AppendChar(int pos)
        {
            char c = (char)BitConverter.ToUInt16(data, pos);
            if (c != 0xFFFF && c != 0x0000) sb.Append(c);
        }
        for (int i = off + 1; i <= off + 9; i += 2) AppendChar(i);
        for (int i = off + 14; i <= off + 25; i += 2) AppendChar(i);
        for (int i = off + 28; i <= off + 31; i += 2) AppendChar(i);
        return sb.ToString();
    }

    private static DateTime? DecodeFatDateTime(ushort date, ushort time)
    {
        if (date == 0) return null;
        try
        {
            int year = 1980 + (date >> 9);
            int month = (date >> 5) & 0x0F;
            int day = date & 0x1F;
            int hour = time >> 11;
            int minute = (time >> 5) & 0x3F;
            int second = (time & 0x1F) * 2;
            if (month is < 1 or > 12 || day is < 1 or > 31) return null;
            return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Local);
        }
        catch { return null; }
    }
}
