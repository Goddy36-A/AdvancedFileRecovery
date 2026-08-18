namespace FileRecovery.Core.Carving;

public enum StructuralValidation
{
    /// <summary>Header AND footer/end-structure both check out — strong evidence the bytes are intact.</summary>
    Valid,
    /// <summary>Header matches but footer/end-structure couldn't be confirmed (truncated, or the format has no reliable end marker to check).</summary>
    HeaderOnlyValid,
    /// <summary>Header bytes don't match at all — strong evidence this location holds something else now.</summary>
    Invalid,
    /// <summary>No validator exists for this extension — can't assess structurally.</summary>
    NotApplicable,
}

/// <summary>
/// Lightweight, read-only structural sanity check for a file whose data
/// location was ASSUMED rather than confirmed — specifically FAT32/exFAT
/// deleted files, where a zeroed FAT chain means recovery has to guess
/// "contiguous allocation starting here" instead of knowing it for certain.
/// Rather than reporting a blanket bounds-check heuristic regardless of
/// what's actually at that location, this peeks at the real bytes (a bounded
/// head/tail window, not the whole file) and checks whether they look like
/// an intact file of the claimed type, giving an evidence-based
/// recoverability estimate instead of an optimistic guess.
/// </summary>
public static class RecoveredFileValidator
{
    public static StructuralValidation Validate(byte[] head, byte[]? tail, string extension, long declaredSize)
    {
        var sig = FileSignatureCatalog.All.FirstOrDefault(s => s.Extension.Equals(extension, StringComparison.OrdinalIgnoreCase));
        if (sig == null) return StructuralValidation.NotApplicable;
        if (declaredSize < sig.Header.Length + sig.TrueStartBackOffset) return StructuralValidation.NotApplicable; // too small to meaningfully check

        // Header bytes may not sit at byte 0 of the file: MP4's "ftyp" match sits
        // TrueStartBackOffset bytes into the actual file (the box's own 4-byte
        // size field comes first) — same concept SignatureCarver uses for carved
        // files. `head` here is always the file's TRUE start (from a directory
        // entry), so we look for the header at that offset, not necessarily at 0.
        if (!MatchesAt(head, sig.TrueStartBackOffset, sig.Header)) return StructuralValidation.Invalid;

        return sig.Name switch
        {
            "PNG" => ValidatePng(tail),
            "ZIP/Office" => ValidateZip(tail),
            "MP4/MOV" => StructuralValidation.HeaderOnlyValid, // ISO-BMFF has no simple universal end marker to check
            "MP3" => StructuralValidation.HeaderOnlyValid,      // ID3 header present; frame-accurate end not checked
            "RAR" => StructuralValidation.HeaderOnlyValid,      // no simple footer for RAR
            _ when sig.Footer is { Length: > 0 } => ContainsFooter(tail, sig.Footer) ? StructuralValidation.Valid : StructuralValidation.HeaderOnlyValid,
            _ => StructuralValidation.HeaderOnlyValid,
        };
    }

    private static bool MatchesAt(byte[] data, int offset, byte[] pattern)
    {
        if (data.Length < offset + pattern.Length) return false;
        for (int i = 0; i < pattern.Length; i++)
            if (data[offset + i] != pattern[i]) return false;
        return true;
    }

    private static bool ContainsFooter(byte[]? tail, byte[] footer)
    {
        if (tail == null || tail.Length < footer.Length) return false;
        for (int i = 0; i <= tail.Length - footer.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < footer.Length; j++)
            {
                if (tail[i + j] != footer[j]) { match = false; break; }
            }
            if (match) return true;
        }
        return false;
    }

    /// <summary>A well-formed PNG's final 12 bytes are always: 00 00 00 00 'IEND' &lt;4-byte CRC&gt;.</summary>
    private static StructuralValidation ValidatePng(byte[]? tail)
    {
        if (tail == null || tail.Length < 12) return StructuralValidation.HeaderOnlyValid;
        var last12 = tail[^12..];
        bool lengthIsZero = last12[0] == 0 && last12[1] == 0 && last12[2] == 0 && last12[3] == 0;
        bool typeIsIEND = last12[4] == (byte)'I' && last12[5] == (byte)'E' && last12[6] == (byte)'N' && last12[7] == (byte)'D';
        return lengthIsZero && typeIsIEND ? StructuralValidation.Valid : StructuralValidation.HeaderOnlyValid;
    }

    /// <summary>A ZIP/OOXML container should have its End Of Central Directory record somewhere near the end.</summary>
    private static StructuralValidation ValidateZip(byte[]? tail)
    {
        byte[] eocd = { 0x50, 0x4B, 0x05, 0x06 };
        return ContainsFooter(tail, eocd) ? StructuralValidation.Valid : StructuralValidation.HeaderOnlyValid;
    }
}
