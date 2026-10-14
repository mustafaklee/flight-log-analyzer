using System.Buffers.Binary;
using System.IO.Compression;

namespace FlightLog.LogParsing.Signals;

/// <summary>Timestamps and numeric columns of one chunk of a series.</summary>
public sealed record SignalChunkData(long[] Timestamps, double[][] Columns);

/// <summary>
/// Binary format of a signal chunk ("FLS1"):
/// <code>
/// 0  4  magic "FLS1"
/// 4  1  format version (1)
/// 5  3  reserved (0)
/// 8  4  record count (int32 LE)
/// 12 4  column count (int32 LE)
/// 16 4  length of the compressed payload (int32 LE), so that a cut-off object is detected
/// 20 .. zlib: timestamps as zig-zag varint deltas (the first relative to 0), then each column as
///       8 byte planes of IEEE-754 float64 LE (byte 0 of every value, then byte 1, ...).
/// </code>
/// Byte planes keep similar exponent bytes together, which compresses well. NaN and infinities are stored as is.
/// </summary>
public static class SignalChunkCodec
{
    public const int FormatVersion = 1;
    public const int HeaderLength = 20;

    /// <summary>Upper bounds that protect decoding against damaged or hostile objects.</summary>
    public const int MaxRecords = 1 << 20;

    public const int MaxColumns = 4096;

    private static ReadOnlySpan<byte> Magic => "FLS1"u8;

    public static byte[] Encode(ReadOnlySpan<long> timestamps, IReadOnlyList<double[]> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        var count = timestamps.Length;
        if (count > MaxRecords || columns.Count > MaxColumns)
        {
            throw new ArgumentException("Chunk is too large.", nameof(columns));
        }

        using var output = new MemoryStream();
        Span<byte> header = stackalloc byte[HeaderLength];
        header.Clear();
        Magic.CopyTo(header);
        header[4] = FormatVersion;
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], count);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..], columns.Count);
        output.Write(header);

        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            var varint = new byte[10];
            long previous = 0;
            foreach (var t in timestamps)
            {
                var delta = t - previous;
                previous = t;
                var zigzag = (ulong)((delta << 1) ^ (delta >> 63));
                var n = 0;
                while (zigzag >= 0x80)
                {
                    varint[n++] = (byte)(zigzag | 0x80);
                    zigzag >>= 7;
                }

                varint[n++] = (byte)zigzag;
                zlib.Write(varint, 0, n);
            }

            var planes = new byte[count];
            foreach (var column in columns)
            {
                if (column.Length < count)
                {
                    throw new ArgumentException("Every column needs a value per timestamp.", nameof(columns));
                }

                for (var plane = 0; plane < 8; plane++)
                {
                    var shift = plane * 8;
                    for (var i = 0; i < count; i++)
                    {
                        planes[i] = (byte)(BitConverter.DoubleToUInt64Bits(column[i]) >> shift);
                    }

                    zlib.Write(planes, 0, count);
                }
            }
        }

        var result = output.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(16), result.Length - HeaderLength);
        return result;
    }

    /// <summary>Decodes a chunk; <paramref name="columns"/> selects which columns to return (all when null), in ascending column order.</summary>
    public static SignalChunkData Decode(byte[] data, IReadOnlyCollection<int>? columns = null)
    {
        try
        {
            return DecodeCore(data, columns);
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException("Signal chunk is truncated.", ex);
        }
    }

    private static SignalChunkData DecodeCore(byte[] data, IReadOnlyCollection<int>? columns)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length < HeaderLength || !data.AsSpan(0, 4).SequenceEqual(Magic) || data[4] != FormatVersion)
        {
            throw new InvalidDataException("Not a signal chunk of a supported version.");
        }

        var count = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(8));
        var columnCount = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(12));
        if (BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(16)) != data.Length - HeaderLength)
        {
            throw new InvalidDataException("Signal chunk length does not match its header.");
        }

        if (count is < 0 or > MaxRecords || columnCount is < 0 or > MaxColumns)
        {
            throw new InvalidDataException("Signal chunk header is out of range.");
        }

        using var zlib = new ZLibStream(new MemoryStream(data, HeaderLength, data.Length - HeaderLength), CompressionMode.Decompress);
        var timestamps = new long[count];
        long previous = 0;
        for (var i = 0; i < count; i++)
        {
            ulong zigzag = 0;
            for (var shift = 0; ; shift += 7)
            {
                var b = zlib.ReadByte();
                if (b < 0 || shift > 63)
                {
                    throw new InvalidDataException("Signal chunk timestamps are truncated.");
                }

                zigzag |= (ulong)(b & 0x7F) << shift;
                if (b < 0x80)
                {
                    break;
                }
            }

            previous += (long)(zigzag >> 1) ^ -(long)(zigzag & 1);
            timestamps[i] = previous;
        }

        var result = new double[columns?.Count ?? columnCount][];
        var planes = new byte[count];
        var bits = new ulong[count];
        var outputIndex = 0;
        for (var c = 0; c < columnCount; c++)
        {
            var wanted = columns is null || columns.Contains(c);
            if (wanted)
            {
                Array.Clear(bits);
            }

            for (var plane = 0; plane < 8; plane++)
            {
                zlib.ReadExactly(planes, 0, count);
                if (!wanted)
                {
                    continue;
                }

                var shift = plane * 8;
                for (var i = 0; i < count; i++)
                {
                    bits[i] |= (ulong)planes[i] << shift;
                }
            }

            if (wanted)
            {
                var values = new double[count];
                for (var i = 0; i < count; i++)
                {
                    values[i] = BitConverter.UInt64BitsToDouble(bits[i]);
                }

                result[outputIndex++] = values;
            }
        }

        if (zlib.ReadByte() >= 0)
        {
            throw new InvalidDataException("Signal chunk has trailing data.");
        }

        if (outputIndex != result.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(columns), "A requested column does not exist in the chunk.");
        }

        return new SignalChunkData(timestamps, result);
    }
}
