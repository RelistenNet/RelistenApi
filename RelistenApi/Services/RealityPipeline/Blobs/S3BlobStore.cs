using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace Relisten.RealityPipeline.Blobs;

public sealed class S3BlobStoreOptions
{
    /// e.g. https://{account}.r2.cloudflarestorage.com
    public string ServiceUrl { get; set; } = "";
    public string Bucket { get; set; } = "";
    public string AccessKeyId { get; set; } = "";
    public string SecretAccessKey { get; set; } = "";

    /// R2 takes "auto".
    public string Region { get; set; } = "auto";
}

/// S3-compatible object storage; production uses Cloudflare R2.
public sealed class S3BlobStore : IBlobStore, IDisposable
{
    private readonly IAmazonS3 s3;
    private readonly string bucket;

    // R2 rejects streaming (chunked) payload signatures; the SDK allows unsigned payloads only over https.
    private readonly bool unsignedPayload;

    public S3BlobStore(S3BlobStoreOptions options)
    {
        bucket = options.Bucket;
        unsignedPayload = options.ServiceUrl.StartsWith(
            "https://",
            StringComparison.OrdinalIgnoreCase
        );
        s3 = new AmazonS3Client(
            new BasicAWSCredentials(options.AccessKeyId, options.SecretAccessKey),
            new AmazonS3Config
            {
                ServiceURL = options.ServiceUrl,
                AuthenticationRegion = options.Region,
                ForcePathStyle = true,
                // R2 (and MinIO) don't implement the SDK's default trailing checksums.
                RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
                ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
            }
        );
    }

    public async Task<PutOutcome> PutIfAbsentAsync(
        string key,
        ReadOnlyMemory<byte> bytes,
        CancellationToken ct = default
    )
    {
        BlobKeys.Validate(key);
        try
        {
            await s3.PutObjectAsync(
                new PutObjectRequest
                {
                    BucketName = bucket,
                    Key = key,
                    InputStream = new MemoryStream(bytes.ToArray(), writable: false),
                    IfNoneMatch = "*",
                    DisablePayloadSigning = unsignedPayload,
                    ContentType = "application/octet-stream",
                },
                ct
            );
            return PutOutcome.Created;
        }
        catch (AmazonS3Exception e)
            when (e.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict)
        {
            return PutOutcome.AlreadyExists;
        }
    }

    public async Task<byte[]?> GetAsync(string key, CancellationToken ct = default)
    {
        BlobKeys.Validate(key);
        try
        {
            using var response = await s3.GetObjectAsync(bucket, key, ct);
            using var buffer = new MemoryStream();
            await response.ResponseStream.CopyToAsync(buffer, ct);
            return buffer.ToArray();
        }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken ct = default)
    {
        BlobKeys.Validate(key);
        try
        {
            await s3.GetObjectMetadataAsync(bucket, key, ct);
            return true;
        }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        BlobKeys.Validate(key);
        await s3.DeleteObjectAsync(bucket, key, ct);
    }

    public void Dispose() => s3.Dispose();
}
