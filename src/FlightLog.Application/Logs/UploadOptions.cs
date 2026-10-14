namespace FlightLog.Application.Logs;

public sealed class UploadOptions
{
    public const string SectionName = "Uploads";

    /// <summary>Largest accepted log file. Default 512 MiB.</summary>
    public long MaxFileSizeBytes { get; set; } = 512L * 1024 * 1024;

    /// <summary>Where uploads are staged while they are hashed and validated. Defaults to the system temp directory.</summary>
    public string? TempDirectory { get; set; }
}
