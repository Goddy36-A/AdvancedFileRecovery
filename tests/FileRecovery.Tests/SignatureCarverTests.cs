using System.Text;
using FileRecovery.Core.Carving;
using FileRecovery.Core.Models;
using FileRecovery.Tests.TestSupport;
using Xunit;

namespace FileRecovery.Tests;

public class SignatureCarverTests
{
    private static List<byte> Garbage(int n, byte seed = 0x33) => Enumerable.Range(0, n).Select(i => (byte)(seed + i)).ToList();

    private static byte[] BuildMinimalPng(byte[] ihdrData)
    {
        var bytes = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        AppendChunk(bytes, "IHDR", ihdrData);
        AppendChunk(bytes, "IEND", Array.Empty<byte>());
        return bytes.ToArray();
    }

    private static void AppendChunk(List<byte> bytes, string type, byte[] data)
    {
        bytes.AddRange(BigEndian((uint)data.Length));
        bytes.AddRange(Encoding.ASCII.GetBytes(type));
        bytes.AddRange(data);
        bytes.AddRange(new byte[4]); // CRC — carver doesn't verify it
    }

    private static byte[] BigEndian(uint value) => new[]
    {
        (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value,
    };

    private static byte[] BuildMinimalZip(byte[] content)
    {
        var bytes = new List<byte>();
        bytes.AddRange(new byte[] { 0x50, 0x4B, 0x03, 0x04 }); // local file header sig
        bytes.AddRange(new byte[14]);                            // version/flags/method/time/date/crc (unused by carver)
        bytes.AddRange(BitConverter.GetBytes((uint)content.Length)); // compressed size
        bytes.AddRange(BitConverter.GetBytes((uint)content.Length)); // uncompressed size
        bytes.AddRange(BitConverter.GetBytes((ushort)0));            // file name length
        bytes.AddRange(BitConverter.GetBytes((ushort)0));            // extra field length
        bytes.AddRange(content);
        // End Of Central Directory record, placed immediately after (no central directory entries —
        // exercises exactly the path SignatureCarver.CarveZipLength walks to termination).
        bytes.AddRange(new byte[] { 0x50, 0x4B, 0x05, 0x06 });
        bytes.AddRange(new byte[16]);  // disk numbers / entry counts / CD size / CD offset (unused by carver)
        bytes.AddRange(BitConverter.GetBytes((ushort)0)); // comment length
        return bytes.ToArray();
    }

    [Fact]
    public void Carve_FindsJpegPngZipAndPdf_AllInOneBuffer_WithExactOffsetsAndLengths()
    {
        byte[] jpeg = { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 };
        jpeg = jpeg.Concat(Encoding.ASCII.GetBytes("EXIFDATA")).Concat(new byte[] { 0xFF, 0xD9 }).ToArray();

        byte[] png = BuildMinimalPng(new byte[13]); // IHDR is always exactly 13 bytes
        byte[] zip = BuildMinimalZip(Encoding.ASCII.GetBytes("HELLO"));
        byte[] pdf = Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj\n<< >>\nendobj\n%%EOF");

        var buffer = new List<byte>();
        buffer.AddRange(Garbage(20));
        int jpegOffset = buffer.Count; buffer.AddRange(jpeg);
        buffer.AddRange(Garbage(15));
        int pngOffset = buffer.Count; buffer.AddRange(png);
        buffer.AddRange(Garbage(15));
        int zipOffset = buffer.Count; buffer.AddRange(zip);
        buffer.AddRange(Garbage(15));
        int pdfOffset = buffer.Count; buffer.AddRange(pdf);
        buffer.AddRange(Garbage(20));

        var bytes = buffer.ToArray();
        using var reader = new MemoryRawReader(bytes);
        var carver = new SignatureCarver(reader);

        var found = carver.Carve(0, bytes.Length, progress: null, CancellationToken.None);

        var foundJpeg = Assert.Single(found, f => f.Extension == ".jpg");
        Assert.Equal(jpegOffset, foundJpeg.CarveOffset);
        Assert.Equal(jpeg.Length, foundJpeg.CarveLength);

        var foundPng = Assert.Single(found, f => f.Extension == ".png");
        Assert.Equal(pngOffset, foundPng.CarveOffset);
        Assert.Equal(png.Length, foundPng.CarveLength);

        var foundZip = Assert.Single(found, f => f.Category == FileCategory.Archives);
        Assert.Equal(zipOffset, foundZip.CarveOffset);
        Assert.Equal(zip.Length, foundZip.CarveLength);

        var foundPdf = Assert.Single(found, f => f.Extension == ".pdf");
        Assert.Equal(pdfOffset, foundPdf.CarveOffset);
        Assert.Equal(pdf.Length, foundPdf.CarveLength);
    }

    [Fact]
    public void Carve_Mp4_SumsBoxSizes_ToExactTotalLength()
    {
        var bytes = new List<byte>();
        bytes.AddRange(Garbage(10));
        int mp4Offset = bytes.Count;
        // ftyp box: size=16, type="ftyp", 8 bytes of payload
        bytes.AddRange(BigEndian(16));
        bytes.AddRange(Encoding.ASCII.GetBytes("ftyp"));
        bytes.AddRange(new byte[8]);
        // mdat box: size=12, type="mdat", 4 bytes of payload
        bytes.AddRange(BigEndian(12));
        bytes.AddRange(Encoding.ASCII.GetBytes("mdat"));
        bytes.AddRange(new byte[4]);
        int mp4Length = 16 + 12;
        // Deliberately no trailing bytes: MP4 box-walking has no explicit end
        // marker, so anything appended here would be summed into the result —
        // ending the buffer exactly at the real boundary keeps this assertion exact.

        var arr = bytes.ToArray();
        using var reader = new MemoryRawReader(arr);
        var carver = new SignatureCarver(reader);

        var found = carver.Carve(0, arr.Length, null, CancellationToken.None);

        var mp4 = Assert.Single(found);
        Assert.Equal(FileCategory.Video, mp4.Category);
        Assert.Equal(mp4Offset, mp4.CarveOffset);
        Assert.Equal(mp4Length, mp4.CarveLength);
    }

    [Fact]
    public void Carve_Mp3_ReadsId3TagSize_AndCapsAtAvailableBudget()
    {
        var bytes = new List<byte>();
        bytes.AddRange(Garbage(5));
        int mp3Offset = bytes.Count;
        bytes.AddRange(Encoding.ASCII.GetBytes("ID3"));
        bytes.Add(0x04); bytes.Add(0x00); // version
        bytes.Add(0x00);                   // flags
        int tagSize = 20;
        // Synchsafe 7-bit-per-byte encoding of the tag size.
        bytes.Add((byte)((tagSize >> 21) & 0x7F));
        bytes.Add((byte)((tagSize >> 14) & 0x7F));
        bytes.Add((byte)((tagSize >> 7) & 0x7F));
        bytes.Add((byte)(tagSize & 0x7F));
        bytes.AddRange(new byte[tagSize]); // the ID3 tag body
        bytes.AddRange(new byte[100]);      // stand-in "audio frame" bytes — far short of the carver's 10MB allowance

        var arr = bytes.ToArray();
        using var reader = new MemoryRawReader(arr);
        var carver = new SignatureCarver(reader);

        var found = carver.Carve(0, arr.Length, null, CancellationToken.None);

        var mp3 = Assert.Single(found);
        Assert.Equal(FileCategory.Audio, mp3.Category);
        Assert.Equal(mp3Offset, mp3.CarveOffset);
        // With only 100 bytes of "audio" available (vastly less than the 10MB
        // allowance), the carve is capped by the buffer's own end.
        Assert.Equal(arr.Length - mp3Offset, mp3.CarveLength);
    }

    [Fact]
    public void Carve_FindsNothing_InRandomNonMatchingData()
    {
        var rng = new Random(42);
        var bytes = new byte[2000];
        rng.NextBytes(bytes);
        // Scrub any bytes that would coincidentally form a real header so the test
        // is deterministic regardless of the RNG.
        for (int i = 0; i < bytes.Length - 4; i++)
        {
            if (bytes[i] == 0xFF && bytes[i + 1] == 0xD8) bytes[i] = 0x00;
            if (bytes[i] == 0x50 && bytes[i + 1] == 0x4B) bytes[i] = 0x00;
        }

        using var reader = new MemoryRawReader(bytes);
        var carver = new SignatureCarver(reader);

        var found = carver.Carve(0, bytes.Length, null, CancellationToken.None);

        Assert.Empty(found);
    }
}
