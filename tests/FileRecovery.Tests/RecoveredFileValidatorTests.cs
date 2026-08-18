using FileRecovery.Core.Carving;
using Xunit;

namespace FileRecovery.Tests;

public class RecoveredFileValidatorTests
{
    [Fact]
    public void Jpeg_ValidHeaderAndFooter_IsValid()
    {
        byte[] data = { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0x00, 0xFF, 0xD9 };
        var result = RecoveredFileValidator.Validate(data, data, ".jpg", data.Length);
        Assert.Equal(StructuralValidation.Valid, result);
    }

    [Fact]
    public void Jpeg_ValidHeaderButNoFooterInTail_IsHeaderOnlyValid()
    {
        byte[] head = { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 };
        byte[] tail = { 0x01, 0x02, 0x03, 0x04 }; // no FF D9 anywhere
        var result = RecoveredFileValidator.Validate(head, tail, ".jpg", 5000);
        Assert.Equal(StructuralValidation.HeaderOnlyValid, result);
    }

    [Fact]
    public void Jpeg_WrongHeaderBytes_IsInvalid()
    {
        byte[] data = { 0x00, 0x00, 0x00, 0x00 };
        var result = RecoveredFileValidator.Validate(data, data, ".jpg", data.Length);
        Assert.Equal(StructuralValidation.Invalid, result);
    }

    [Fact]
    public void Png_ValidHeaderAndIendTail_IsValid()
    {
        byte[] head = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        byte[] tail = { 0x00, 0x00, 0x00, 0x00, (byte)'I', (byte)'E', (byte)'N', (byte)'D', 0xAE, 0x42, 0x60, 0x82 };
        var result = RecoveredFileValidator.Validate(head, tail, ".png", 5000);
        Assert.Equal(StructuralValidation.Valid, result);
    }

    [Fact]
    public void Png_ValidHeaderButWrongTail_IsHeaderOnlyValid()
    {
        byte[] head = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        byte[] tail = new byte[12]; // not the IEND pattern
        var result = RecoveredFileValidator.Validate(head, tail, ".png", 5000);
        Assert.Equal(StructuralValidation.HeaderOnlyValid, result);
    }

    [Fact]
    public void Zip_ValidHeaderAndEocdInTail_IsValid()
    {
        byte[] head = { 0x50, 0x4B, 0x03, 0x04 };
        byte[] tail = { 0x11, 0x22, 0x50, 0x4B, 0x05, 0x06, 0x33, 0x44 };
        var result = RecoveredFileValidator.Validate(head, tail, ".zip", 5000);
        Assert.Equal(StructuralValidation.Valid, result);
    }

    [Fact]
    public void Zip_NoEocdInTail_IsHeaderOnlyValid()
    {
        byte[] head = { 0x50, 0x4B, 0x03, 0x04 };
        byte[] tail = { 0x11, 0x22, 0x33, 0x44 };
        var result = RecoveredFileValidator.Validate(head, tail, ".zip", 5000);
        Assert.Equal(StructuralValidation.HeaderOnlyValid, result);
    }

    [Fact]
    public void UnsupportedExtension_IsNotApplicable()
    {
        byte[] data = { 1, 2, 3, 4, 5 };
        var result = RecoveredFileValidator.Validate(data, data, ".txt", data.Length);
        Assert.Equal(StructuralValidation.NotApplicable, result);
    }

    [Fact]
    public void DeclaredSizeSmallerThanHeader_IsNotApplicable()
    {
        byte[] data = { 0xFF, 0xD8 }; // JPEG header is 3 bytes; declared size below that
        var result = RecoveredFileValidator.Validate(data, data, ".jpg", 2);
        Assert.Equal(StructuralValidation.NotApplicable, result);
    }

    [Fact]
    public void Mp4_ValidHeaderAtCorrectOffset_IsHeaderOnlyValid_NoEndMarkerToCheck()
    {
        // Real on-disk layout: 4-byte box size field, THEN "ftyp" — the file's
        // true start (from a directory entry) is the size field, not "ftyp" itself.
        byte[] data = { 0x00, 0x00, 0x00, 0x10, (byte)'f', (byte)'t', (byte)'y', (byte)'p' };
        var result = RecoveredFileValidator.Validate(data, data, ".mp4", 1000);
        Assert.Equal(StructuralValidation.HeaderOnlyValid, result);
    }

    [Fact]
    public void Mp4_MissingFtypAtExpectedOffset_IsInvalid()
    {
        byte[] data = { 0x00, 0x00, 0x00, 0x10, 0x01, 0x02, 0x03, 0x04 }; // no "ftyp" at offset 4
        var result = RecoveredFileValidator.Validate(data, data, ".mp4", 1000);
        Assert.Equal(StructuralValidation.Invalid, result);
    }
}
