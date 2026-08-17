using System.Text;
using FileRecovery.Core.Disk;
using FileRecovery.Core.Models;

namespace FileRecovery.Core.Carving;

/// <summary>
/// Deep Scan engine: streams the raw device in large chunks, searches for
/// known file-header signatures at every offset, and — for formats whose
/// internal structure encodes a length — walks that structure to find the
/// true end of the file instead of blindly grabbing MaxSizeBytes. This is
/// the same family of technique used by tools like PhotoRec/TestDisk.
/// </summary>
public sealed class SignatureCarver
{
    private const int ChunkSize = 8 * 1024 * 1024;   // 8 MB read window
    private const int Overlap = 4096;                 // so signatures spanning chunk boundaries aren't missed

    private readonly IRawReader _reader;

    public SignatureCarver(IRawReader reader) => _reader = reader;

    public List<RecoverableFile> Carve(long startOffset, long endOffset, IProgress<ScanProgress>? progress,
        CancellationToken ct, HashSet<FileCategory>? categoryFilter = null)
    {
        var found = new List<RecoverableFile>();
        var counts = new Dictionary<FileCategory, int>();
        foreach (FileCategory c in Enum.GetValues<FileCategory>()) counts[c] = 0;

        var signatures = FileSignatureCatalog.All
            .Where(s => categoryFilter == null || categoryFilter.Contains(s.Category))
            .ToList();

        long total = endOffset - startOffset;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long position = startOffset;
        long lastSkippedTo = -1;

        while (position < endOffset)
        {
            ct.ThrowIfCancellationRequested();

            int readLen = (int)Math.Min(ChunkSize, endOffset - position);
            byte[] chunk = _reader.ReadBytes(position, readLen);

            for (int i = 0; i < chunk.Length; i++)
            {
                // Don't re-match inside a region we already carved out as part of this chunk.
                if (position + i < lastSkippedTo) continue;

                foreach (var sig in signatures)
                {
                    if (!MatchesAt(chunk, i, sig)) continue;

                    long absoluteOffset = position + i; // where the signature bytes matched
                    long carvedLength = DetermineLength(chunk, i, sig, absoluteOffset, endOffset);
                    if (carvedLength <= 0) continue;

                    // For most formats the match position IS the true file start.
                    // MP4 is the exception (see TrueStartBackOffset) — correct for
                    // it here so CarveOffset/CarveLength always describe the same
                    // byte range a recovery copy should read.
                    long fileStartOffset = absoluteOffset - sig.TrueStartBackOffset;

                    var file = new RecoverableFile
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        Name = $"recovered_{fileStartOffset:X8}{sig.Extension}",
                        SizeBytes = carvedLength,
                        Category = sig.Category,
                        Extension = sig.Extension,
                        FromCarving = true,
                        CarveOffset = fileStartOffset,
                        CarveLength = carvedLength,
                        Recoverability = carvedLength >= sig.Header.Length ? Recoverability.Excellent : Recoverability.Partial,
                    };
                    found.Add(file);
                    counts[sig.Category]++;
                    lastSkippedTo = fileStartOffset + carvedLength;
                    break; // one match per offset is enough
                }
            }

            position += Math.Max(1, chunk.Length - Overlap);
            if (chunk.Length < Overlap) position = endOffset; // final chunk

            if (sw.ElapsedMilliseconds > 250)
            {
                long processed = position - startOffset;
                double pct = total > 0 ? Math.Min(100.0, processed * 100.0 / total) : 100;
                var elapsed = sw.Elapsed;
                TimeSpan? eta = processed > 0
                    ? TimeSpan.FromSeconds(elapsed.TotalSeconds * (total - processed) / processed)
                    : null;

                progress?.Report(new ScanProgress
                {
                    PercentComplete = pct,
                    BytesProcessed = processed,
                    TotalBytes = total,
                    Elapsed = elapsed,
                    EstimatedRemaining = eta,
                    CountsByCategory = new Dictionary<FileCategory, int>(counts),
                    TotalFound = found.Count,
                    StatusText = $"Deep scanning sector {absoluteSector(position)}…",
                });
            }
        }

        progress?.Report(new ScanProgress
        {
            PercentComplete = 100,
            BytesProcessed = total,
            TotalBytes = total,
            Elapsed = sw.Elapsed,
            EstimatedRemaining = TimeSpan.Zero,
            CountsByCategory = new Dictionary<FileCategory, int>(counts),
            TotalFound = found.Count,
            StatusText = "Deep scan complete.",
        });

        return found;

        long absoluteSector(long byteOffset) => byteOffset / Math.Max(1, _reader.BytesPerSector);
    }

    private static bool MatchesAt(byte[] buffer, int index, FileSignature sig)
    {
        if (index + sig.Header.Length > buffer.Length) return false;
        for (int j = 0; j < sig.Header.Length; j++)
            if (buffer[index + j] != sig.Header[j]) return false;
        return true;
    }

    /// <summary>
    /// Determines how many bytes belong to the carved file. Uses format-aware
    /// structure walking where possible (PNG/ZIP/MP4/MP3), falls back to
    /// header/footer scanning, and otherwise caps at MaxSizeBytes.
    /// </summary>
    private long DetermineLength(byte[] firstChunk, int indexInChunk, FileSignature sig, long absoluteOffset, long hardEnd)
    {
        // Budget must be measured from the file's TRUE start, not the signature
        // match position — for MP4 those differ by TrueStartBackOffset bytes
        // ("ftyp" sits 4 bytes into the box). Using hardEnd-absoluteOffset alone
        // would under-count the available room by exactly that many bytes and
        // silently truncate the carve.
        long budget = Math.Min(sig.MaxSizeBytes, hardEnd - absoluteOffset + sig.TrueStartBackOffset);

        switch (sig.Name)
        {
            case "PNG":
                return CarvePngLength(absoluteOffset, budget);
            case "ZIP/Office":
                return CarveZipLength(absoluteOffset, budget);
            case "MP4/MOV":
                return CarveMp4Length(absoluteOffset, budget);
            case "MP3":
                return CarveMp3Length(absoluteOffset, budget);
        }

        if (sig.Strategy == SignatureMatchStrategy.HeaderFooter && sig.Footer is { Length: > 0 })
        {
            return FindFooterLength(absoluteOffset, sig.Footer, budget);
        }

        return budget; // no structural info available — best-effort fixed cap
    }

    /// <summary>Scans forward reading in windows until the footer byte sequence is found or budget exhausted.</summary>
    private long FindFooterLength(long absoluteOffset, byte[] footer, long budget)
    {
        const int window = 1024 * 1024;
        long scanned = 0;
        long prevTailKeep = footer.Length - 1;
        byte[]? prevTail = null;

        while (scanned < budget)
        {
            int toRead = (int)Math.Min(window, budget - scanned);
            byte[] data = _reader.ReadBytes(absoluteOffset + scanned, toRead);

            byte[] searchBuf = data;
            int searchStartOffsetAdjust = 0;
            if (prevTail is { Length: > 0 })
            {
                searchBuf = new byte[prevTail.Length + data.Length];
                Buffer.BlockCopy(prevTail, 0, searchBuf, 0, prevTail.Length);
                Buffer.BlockCopy(data, 0, searchBuf, prevTail.Length, data.Length);
                searchStartOffsetAdjust = prevTail.Length;
            }

            int idx = IndexOf(searchBuf, footer);
            if (idx >= 0)
            {
                long footerEndInStream = scanned - searchStartOffsetAdjust + idx + footer.Length;
                return Math.Min(budget, footerEndInStream);
            }

            scanned += toRead;
            prevTail = data.Length >= prevTailKeep
                ? data[^((int)prevTailKeep)..]
                : data;
        }

        return budget; // footer never found within cap — return truncated best-effort file
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length) return -1;
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            bool ok = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { ok = false; break; }
            }
            if (ok) return i;
        }
        return -1;
    }

    /// <summary>Walks PNG chunks (each: 4-byte length, 4-byte type, data, 4-byte CRC) until IEND.</summary>
    private long CarvePngLength(long absoluteOffset, long budget)
    {
        long pos = 8; // past the 8-byte PNG signature
        while (pos + 8 <= budget)
        {
            byte[] header = _reader.ReadBytes(absoluteOffset + pos, 8);
            uint chunkLen = ReadUInt32BE(header, 0);
            string type = Encoding.ASCII.GetString(header, 4, 4);
            long chunkTotal = 8 + chunkLen + 4; // len+type+data+crc
            pos += chunkTotal;
            if (type == "IEND") return Math.Min(budget, pos);
            if (chunkLen > 50 * 1024 * 1024) break; // corrupt/garbage length, bail out
        }
        return Math.Min(budget, pos);
    }

    /// <summary>
    /// Walks ZIP local file headers (PK\3\4) until it hits the End Of Central
    /// Directory record (PK\5\6) or a non-ZIP signature, covering plain .zip
    /// as well as OOXML containers (.docx/.xlsx/.pptx, which are zip files).
    /// </summary>
    private long CarveZipLength(long absoluteOffset, long budget)
    {
        long pos = 0;
        while (pos + 4 <= budget)
        {
            byte[] sig = _reader.ReadBytes(absoluteOffset + pos, 4);
            if (sig[0] == 0x50 && sig[1] == 0x4B && sig[2] == 0x03 && sig[3] == 0x04)
            {
                byte[] lfh = _reader.ReadBytes(absoluteOffset + pos, 30);
                if (lfh.Length < 30) break;
                uint compSize = ReadUInt32LE(lfh, 18);
                uint nameLen = ReadUInt16LE(lfh, 26);
                uint extraLen = ReadUInt16LE(lfh, 28);
                long entryLen = 30 + nameLen + extraLen + compSize;
                pos += Math.Max(entryLen, 30);
            }
            else if (sig[0] == 0x50 && sig[1] == 0x4B && sig[2] == 0x05 && sig[3] == 0x06)
            {
                byte[] eocd = _reader.ReadBytes(absoluteOffset + pos, 22);
                uint commentLen = eocd.Length >= 22 ? (uint)ReadUInt16LE(eocd, 20) : 0u;
                pos += 22 + commentLen;
                return Math.Min(budget, pos);
            }
            else
            {
                // Central directory entries (PK\1\2) or unknown — stop, best effort.
                return Math.Min(budget, Math.Max(pos, 30));
            }
        }
        return Math.Min(budget, Math.Max(pos, 1024));
    }

    /// <summary>Walks ISO-BMFF top-level boxes (ftyp, moov, mdat, ...) summing their sizes.</summary>
    private long CarveMp4Length(long absoluteOffset, long budget)
    {
        // Note: absoluteOffset here points at "ftyp" (4 bytes into the box); the box itself
        // starts 4 bytes earlier with a big-endian size field. Re-anchor to the box start.
        long boxStart = -4;
        long pos = boxStart;
        while (pos + 8 <= budget)
        {
            byte[] header = _reader.ReadBytes(absoluteOffset + pos, 8);
            long boxSize = ReadUInt32BE(header, 0);
            string type = Encoding.ASCII.GetString(header, 4, 4);

            if (boxSize == 1)
            {
                byte[] largeSize = _reader.ReadBytes(absoluteOffset + pos + 8, 8);
                boxSize = ReadInt64BE(largeSize, 0);
            }
            else if (boxSize == 0)
            {
                pos = budget; // box extends to EOF; best effort stop
                break;
            }

            if (boxSize <= 0 || boxSize > budget) { pos += 8; break; }
            pos += boxSize;

            if (!IsKnownMp4Box(type) && pos > 8) break; // stop at first unrecognized top-level box
        }
        long total = pos - boxStart;
        return Math.Max(0, Math.Min(budget, total));
    }

    private static bool IsKnownMp4Box(string type) =>
        type is "ftyp" or "free" or "mdat" or "moov" or "wide" or "skip" or "pnot" or "moof" or "mfra" or "styp" or "sidx";

    /// <summary>ID3v2 header declares its own tag size; audio frames follow until a non-frame sync or EOF cap.</summary>
    private long CarveMp3Length(long absoluteOffset, long budget)
    {
        byte[] id3 = _reader.ReadBytes(absoluteOffset, 10);
        if (id3.Length < 10) return budget;
        // Synchsafe 7-bit-per-byte size at bytes 6-9
        long tagSize = ((id3[6] & 0x7F) << 21) | ((id3[7] & 0x7F) << 14) | ((id3[8] & 0x7F) << 7) | (id3[9] & 0x7F);
        long afterTag = 10 + tagSize;
        // We don't fully parse MPEG audio frames here; take the tag plus a generous
        // audio-data allowance, capped by budget — good enough for a usable carve.
        long estimate = Math.Min(budget, afterTag + 10 * 1024 * 1024);
        return estimate;
    }

    private static uint ReadUInt32BE(byte[] b, int o) => (uint)((b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3]);
    private static long ReadInt64BE(byte[] b, int o)
    {
        long v = 0;
        for (int i = 0; i < 8; i++) v = (v << 8) | b[o + i];
        return v;
    }
    private static uint ReadUInt32LE(byte[] b, int o) => (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
    private static ushort ReadUInt16LE(byte[] b, int o) => (ushort)(b[o] | (b[o + 1] << 8));
}
