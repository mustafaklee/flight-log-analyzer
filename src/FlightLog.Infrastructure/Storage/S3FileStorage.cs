using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using FlightLog.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace FlightLog.Infrastructure.Storage;

internal sealed class S3FileStorage(IAmazonS3 s3, IOptions<StorageOptions> options) : IFileStorage
{
    private string Bucket => options.Value.Bucket;

    public async Task<bool> ExistsAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            await s3.GetObjectMetadataAsync(Bucket, key, cancellationToken);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public async Task UploadFileAsync(string key, string localPath, string contentType, CancellationToken cancellationToken)
    {
        // TransferUtility switches to multipart uploads for large files.
        using var transfer = new TransferUtility(s3);
        await transfer.UploadAsync(
            new TransferUtilityUploadRequest
            {
                BucketName = Bucket,
                Key = key,
                FilePath = localPath,
                ContentType = contentType,
            },
            cancellationToken);
    }

    public async Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            var response = await s3.GetObjectAsync(Bucket, key, cancellationToken);
            return new ResponseStream(response);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            throw new FileNotFoundException($"Object '{key}' does not exist.", ex);
        }
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken) =>
        s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = Bucket, Key = key }, cancellationToken);

    public async Task EnsureBucketAsync(CancellationToken cancellationToken)
    {
        if (!await Amazon.S3.Util.AmazonS3Util.DoesS3BucketExistV2Async(s3, Bucket))
        {
            await s3.PutBucketAsync(new PutBucketRequest { BucketName = Bucket }, cancellationToken);
        }
    }

    public async Task<bool> IsBucketReachableAsync(CancellationToken cancellationToken)
    {
        await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = Bucket, MaxKeys = 1 }, cancellationToken);
        return true;
    }

    /// <summary>Keeps the S3 response alive for as long as its body stream is read.</summary>
    private sealed class ResponseStream(GetObjectResponse response) : Stream
    {
        private readonly Stream _inner = response.ResponseStream;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => response.ContentLength;

        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            _inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                response.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
