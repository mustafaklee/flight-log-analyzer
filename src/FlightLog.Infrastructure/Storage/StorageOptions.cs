namespace FlightLog.Infrastructure.Storage;

/// <summary>S3 compatible object storage (SeaweedFS locally, see ADR-0009). Credentials come from environment variables or a secret store.</summary>
public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>Endpoint such as <c>http://storage:8333</c>. Leave empty to use AWS S3 with <see cref="Region"/>.</summary>
    public string? ServiceUrl { get; set; }

    public string Region { get; set; } = "us-east-1";

    public string Bucket { get; set; } = "flightlog";

    public string? AccessKey { get; set; }

    public string? SecretKey { get; set; }

    /// <summary>Path-style addressing; required by most self-hosted S3 servers.</summary>
    public bool ForcePathStyle { get; set; } = true;

    /// <summary>Create the bucket at startup when it does not exist (local and test environments).</summary>
    public bool CreateBucketIfMissing { get; set; }
}
