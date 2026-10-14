namespace FlightLog.Application.Abstractions;

/// <summary>Object storage for raw log files and large derived artifacts (S3 compatible in production).</summary>
public interface IFileStorage
{
    Task<bool> ExistsAsync(string key, CancellationToken cancellationToken);

    /// <summary>Uploads a local file under <paramref name="key"/>, overwriting any existing object.</summary>
    Task UploadFileAsync(string key, string localPath, string contentType, CancellationToken cancellationToken);

    /// <summary>Opens the object for streaming reads. Throws <see cref="FileNotFoundException"/> when it does not exist.</summary>
    Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken);

    Task DeleteAsync(string key, CancellationToken cancellationToken);
}
