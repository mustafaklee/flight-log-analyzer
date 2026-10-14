using System.Text;

namespace FlightLog.LogParsing;

internal static class TextDecoding
{
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    /// <summary>Decodes a byte string up to the first NUL; invalid sequences become U+FFFD.</summary>
    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        var end = bytes.IndexOf((byte)0);
        if (end >= 0)
        {
            bytes = bytes[..end];
        }

        return Utf8.GetString(bytes);
    }
}
