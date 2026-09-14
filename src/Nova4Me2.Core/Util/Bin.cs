using System.Buffers.Binary;
using System.Text;

namespace Nova4Me2.Core.Util;

/// <summary>Little-endian binary helpers.</summary>
public static class Bin
{
    public static ushort U16(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(o, 2));
    public static uint U32(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(o, 4));
    public static ulong U64(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt64LittleEndian(b.Slice(o, 8));
    public static short I16(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadInt16LittleEndian(b.Slice(o, 2));
    public static int I32(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadInt32LittleEndian(b.Slice(o, 4));
    public static long I64(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadInt64LittleEndian(b.Slice(o, 8));
    public static void PutU16(Span<byte> b, int o, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(b.Slice(o, 2), v);
    public static void PutU32(Span<byte> b, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.Slice(o, 4), v);
    public static void PutU64(Span<byte> b, int o, ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(b.Slice(o, 8), v);
    public static string Utf16(ReadOnlySpan<byte> b, int o, int chars) => Encoding.Unicode.GetString(b.Slice(o, chars * 2));
    public static string Ascii(ReadOnlySpan<byte> b, int o, int len) => Encoding.ASCII.GetString(b.Slice(o, len)).TrimEnd('\0', ' ');

    /// <summary>Round up to a multiple of <paramref name="align"/> (power of two not required).</summary>
    public static long AlignUp(long v, long align) => (v + align - 1) / align * align;
    public static long AlignDown(long v, long align) => v / align * align;
}
