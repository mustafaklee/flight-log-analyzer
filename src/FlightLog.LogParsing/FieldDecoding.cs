using System.Buffers.Binary;

namespace FlightLog.LogParsing;

/// <summary>Primitive little-endian encodings shared by both formats.</summary>
internal enum PrimitiveType : byte
{
    Int8,
    UInt8,
    Int16,
    UInt16,
    Int32,
    UInt32,
    Int64,
    UInt64,
    Float32,
    Float64,
    Float16,
    Bool,
    Chars,
    Skip,
}

/// <summary>
/// Where a field's bytes are and how they become a value. <see cref="Scale"/> is applied like pymavlink does:
/// factors between 0 and 1 divide by their reciprocal (1e-7 becomes "/ 1e7"), which is more accurate.
/// </summary>
internal readonly record struct FieldSlot(int Offset, PrimitiveType Type, int Length, int FieldIndex, double Scale)
{
    public static int SizeOf(PrimitiveType type) => type switch
    {
        PrimitiveType.Int8 or PrimitiveType.UInt8 or PrimitiveType.Bool => 1,
        PrimitiveType.Int16 or PrimitiveType.UInt16 or PrimitiveType.Float16 => 2,
        PrimitiveType.Int32 or PrimitiveType.UInt32 or PrimitiveType.Float32 => 4,
        PrimitiveType.Int64 or PrimitiveType.UInt64 or PrimitiveType.Float64 => 8,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    public double ReadNumber(ReadOnlySpan<byte> payload)
    {
        var b = payload.Slice(Offset);
        var value = Type switch
        {
            PrimitiveType.Int8 => (sbyte)b[0],
            PrimitiveType.UInt8 => b[0],
            PrimitiveType.Bool => b[0] != 0 ? 1 : 0,
            PrimitiveType.Int16 => BinaryPrimitives.ReadInt16LittleEndian(b),
            PrimitiveType.UInt16 => BinaryPrimitives.ReadUInt16LittleEndian(b),
            PrimitiveType.Int32 => BinaryPrimitives.ReadInt32LittleEndian(b),
            PrimitiveType.UInt32 => BinaryPrimitives.ReadUInt32LittleEndian(b),
            PrimitiveType.Int64 => BinaryPrimitives.ReadInt64LittleEndian(b),
            PrimitiveType.UInt64 => BinaryPrimitives.ReadUInt64LittleEndian(b),
            PrimitiveType.Float32 => BinaryPrimitives.ReadSingleLittleEndian(b),
            PrimitiveType.Float64 => BinaryPrimitives.ReadDoubleLittleEndian(b),
            PrimitiveType.Float16 => (double)BinaryPrimitives.ReadHalfLittleEndian(b),
            _ => double.NaN,
        };
        if (Scale == 1)
        {
            return value;
        }

        return Scale is > 0 and < 1 ? value / (1 / Scale) : value * Scale;
    }

    public string ReadText(ReadOnlySpan<byte> payload) => TextDecoding.Decode(payload.Slice(Offset, Length));
}
