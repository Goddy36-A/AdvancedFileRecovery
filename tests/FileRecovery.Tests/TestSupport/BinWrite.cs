namespace FileRecovery.Tests.TestSupport;

internal static class BinWrite
{
    public static void U16(byte[] buf, int offset, ushort value) => BitConverter.GetBytes(value).CopyTo(buf, offset);
    public static void U32(byte[] buf, int offset, uint value) => BitConverter.GetBytes(value).CopyTo(buf, offset);
    public static void I64(byte[] buf, int offset, long value) => BitConverter.GetBytes(value).CopyTo(buf, offset);
    public static void U64(byte[] buf, int offset, ulong value) => BitConverter.GetBytes(value).CopyTo(buf, offset);

    public static int Align8(int value) => (value + 7) & ~7;
}
