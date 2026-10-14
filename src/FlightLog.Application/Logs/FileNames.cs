using System.Text;
using FlightLog.Domain.Flights;

namespace FlightLog.Application.Logs;

internal static class FileNames
{
    /// <summary>
    /// Reduces a client supplied name to a display-safe file name: no directories, no control or
    /// path characters, bounded length. The result is never used as a storage path.
    /// </summary>
    public static string Sanitize(string? fileName)
    {
        var name = (fileName ?? string.Empty).Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];

        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            builder.Append(char.IsControl(c) || Path.GetInvalidFileNameChars().Contains(c) || c is '"' or '<' or '>' or '|' or ':' or '*' or '?' ? '_' : c);
        }

        var result = builder.ToString().Trim().TrimStart('.');
        if (result.Length == 0)
        {
            result = "log";
        }

        if (result.Length > FlightLogFile.FileNameMaxLength)
        {
            var extension = Path.GetExtension(result);
            result = result[..(FlightLogFile.FileNameMaxLength - extension.Length)] + extension;
        }

        return result;
    }
}
