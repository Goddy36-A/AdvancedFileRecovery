using FileRecovery.Core.Models;

namespace FileRecovery.Core.Carving;

public enum SignatureMatchStrategy
{
    /// <summary>Look for a footer byte sequence; if not found within MaxSize, truncate at MaxSize.</summary>
    HeaderFooter,
    /// <summary>Length is encoded in the file's own structure (PNG chunks, MP4 atoms, ZIP EOCD, ID3 size).</summary>
    StructureAware,
}

public sealed class FileSignature
{
    public required string Name { get; init; }
    public required string Extension { get; init; }
    public required FileCategory Category { get; init; }
    public required byte[] Header { get; init; }
    public byte[]? Footer { get; init; }
    public SignatureMatchStrategy Strategy { get; init; } = SignatureMatchStrategy.HeaderFooter;
    public long MaxSizeBytes { get; init; } = 200 * 1024 * 1024; // safety cap per carved file

    public static byte[] B(params int[] bytes) => bytes.Select(b => (byte)b).ToArray();
}

/// <summary>
/// Catalogue of signatures for the formats required by the spec:
/// JPEG, PNG, PDF, DOCX/XLSX/PPTX (zip-based OOXML), MP4, MP3, ZIP, RAR.
/// </summary>
public static class FileSignatureCatalog
{
    public static readonly List<FileSignature> All = new()
    {
        new FileSignature
        {
            Name = "JPEG", Extension = ".jpg", Category = FileCategory.Photos,
            Header = FileSignature.B(0xFF, 0xD8, 0xFF),
            Footer = FileSignature.B(0xFF, 0xD9),
            Strategy = SignatureMatchStrategy.HeaderFooter,
            MaxSizeBytes = 50 * 1024 * 1024,
        },
        new FileSignature
        {
            Name = "PNG", Extension = ".png", Category = FileCategory.Photos,
            Header = FileSignature.B(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A),
            Strategy = SignatureMatchStrategy.StructureAware, // walk IHDR..IEND chunks
            MaxSizeBytes = 100 * 1024 * 1024,
        },
        new FileSignature
        {
            Name = "PDF", Extension = ".pdf", Category = FileCategory.Documents,
            Header = FileSignature.B(0x25, 0x50, 0x44, 0x46), // %PDF
            Footer = FileSignature.B(0x25, 0x25, 0x45, 0x4F, 0x46), // %%EOF
            Strategy = SignatureMatchStrategy.HeaderFooter,
            MaxSizeBytes = 200 * 1024 * 1024,
        },
        new FileSignature
        {
            Name = "ZIP/Office", Extension = ".zip", Category = FileCategory.Archives,
            Header = FileSignature.B(0x50, 0x4B, 0x03, 0x04), // PK\3\4 - also DOCX/XLSX/PPTX containers
            Strategy = SignatureMatchStrategy.StructureAware, // walk local file headers to End Of Central Directory
            MaxSizeBytes = 500 * 1024 * 1024,
        },
        new FileSignature
        {
            Name = "RAR", Extension = ".rar", Category = FileCategory.Archives,
            Header = FileSignature.B(0x52, 0x61, 0x72, 0x21, 0x1A, 0x07), // "Rar!\x1A\x07" (RAR4/5 common prefix)
            Strategy = SignatureMatchStrategy.HeaderFooter,
            Footer = null,
            MaxSizeBytes = 700 * 1024 * 1024,
        },
        new FileSignature
        {
            Name = "MP4/MOV", Extension = ".mp4", Category = FileCategory.Video,
            Header = FileSignature.B(0x66, 0x74, 0x79, 0x70), // "ftyp" (checked at offset 4 by carver)
            Strategy = SignatureMatchStrategy.StructureAware, // sum ISO-BMFF box sizes
            MaxSizeBytes = 2L * 1024 * 1024 * 1024, // 2 GB cap for a single carved video
        },
        new FileSignature
        {
            Name = "MP3", Extension = ".mp3", Category = FileCategory.Audio,
            Header = FileSignature.B(0x49, 0x44, 0x33), // "ID3"
            Strategy = SignatureMatchStrategy.StructureAware, // ID3v2 header size + frames until next sync/EOF
            MaxSizeBytes = 200 * 1024 * 1024,
        },
    };

    public static FileCategory CategoryForExtension(string ext) => ext.ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".heic" or ".tiff" => FileCategory.Photos,
        ".pdf" or ".doc" or ".docx" or ".xls" or ".xlsx" or ".ppt" or ".pptx" or ".txt" or ".rtf" => FileCategory.Documents,
        ".mp4" or ".mov" or ".avi" or ".mkv" or ".wmv" => FileCategory.Video,
        ".mp3" or ".wav" or ".flac" or ".aac" or ".m4a" => FileCategory.Audio,
        ".zip" or ".rar" or ".7z" or ".tar" or ".gz" => FileCategory.Archives,
        _ => FileCategory.Other,
    };
}
