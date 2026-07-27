using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using HetznerDrive.Core.Models;

namespace HetznerDrive.CloudFiles;

/// <summary>
/// Hetzner Object Storage over the S3 API. Used by the Cloud Files provider to enumerate objects
/// (to build placeholders), range-read bytes on hydration, and push local changes back.
/// </summary>
public sealed class S3StorageClient : IRemoteStorageClient
{
    /// <summary>Files at or above this size upload as a concurrent multipart transfer.</summary>
    private const long MultipartThresholdBytes = 16L * 1024 * 1024;

    /// <summary>Size of each multipart chunk. Peak upload memory is this × <see cref="UploadConcurrency"/>.</summary>
    private const long MultipartPartSizeBytes = 16L * 1024 * 1024;

    /// <summary>Parallel part uploads within one file.</summary>
    private const int UploadConcurrency = 8;

    /// <summary>S3 caps a single DeleteObjects request at 1000 keys.</summary>
    private const int DeleteBatchSize = 1000;

    private readonly IAmazonS3 _s3;
    private readonly TransferUtility _transfer;
    private readonly string _bucket;

    public S3StorageClient(string endpointHost, string regionCode, string accessKeyId,
        string secretAccessKey, string bucket)
    {
        if (string.IsNullOrWhiteSpace(endpointHost)) throw new ArgumentException("Endpoint is required.", nameof(endpointHost));
        if (string.IsNullOrWhiteSpace(bucket)) throw new ArgumentException("Bucket is required.", nameof(bucket));

        _bucket = bucket;
        var config = new AmazonS3Config
        {
            ServiceURL = "https://" + endpointHost.TrimEnd('/'),
            // Hetzner is S3-compatible; path-style avoids DNS and certificate trouble with
            // dotted bucket names, which virtual-hosted style cannot serve over TLS.
            ForcePathStyle = true,
            AuthenticationRegion = regionCode,
        };
        _s3 = new AmazonS3Client(new BasicAWSCredentials(accessKeyId, secretAccessKey), config);
        _transfer = new TransferUtility(_s3, new TransferUtilityConfig
        {
            ConcurrentServiceRequests = UploadConcurrency,
            MinSizeBeforePartUpload = MultipartThresholdBytes,
        });
    }

    /// <summary>Builds a client for an Object Storage mapping.</summary>
    public static S3StorageClient ForMapping(Mapping mapping, HetznerCredentials creds)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(creds);

        var location = HetznerEndpoints.FindLocation(mapping.LocationCode)
            ?? throw new InvalidOperationException($"Unknown Object Storage location '{mapping.LocationCode}'.");
        return new S3StorageClient(location.Endpoint, location.Code,
            creds.AccessKeyId, creds.SecretAccessKey, mapping.BucketName);
    }

    public string ProtocolName => "S3";

    public bool SupportsShareLinks => true;

    public async IAsyncEnumerable<RemoteEntry> ListAsync(
        string? prefix = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var request = new ListObjectsV2Request
        {
            BucketName = _bucket,
            Prefix = string.IsNullOrWhiteSpace(prefix) ? null : prefix,
            MaxKeys = 1000,
        };

        ListObjectsV2Response response;
        do
        {
            response = await _s3.ListObjectsV2Async(request, ct).ConfigureAwait(false);
            foreach (var o in response.S3Objects)
                yield return new RemoteEntry(o.Key, o.Size, o.LastModified.ToUniversalTime(), o.ETag);
            request.ContinuationToken = response.NextContinuationToken;
        }
        while (response.IsTruncated);
    }

    public async Task<Stream> OpenReadAsync(string key, long offset, long? length, CancellationToken ct = default)
    {
        var request = new GetObjectRequest { BucketName = _bucket, Key = key };
        if (offset > 0 || length is not null)
        {
            var end = length is null ? "" : (offset + length.Value - 1).ToString();
            request.ByteRange = new ByteRange($"bytes={offset}-{end}");
        }
        var response = await _s3.GetObjectAsync(request, ct).ConfigureAwait(false);
        return response.ResponseStream;
    }

    public async Task<string?> PutAsync(string key, string localPath, CancellationToken ct = default)
    {
        // A single PUT means one serial stream, which leaves most of the link idle on big files.
        // Above the threshold, hand off to a concurrent multipart transfer instead.
        long length;
        try { length = new FileInfo(localPath).Length; }
        catch { length = 0; }

        if (length >= MultipartThresholdBytes)
        {
            await _transfer.UploadAsync(new TransferUtilityUploadRequest
            {
                BucketName = _bucket,
                Key = key,
                FilePath = localPath,
                PartSize = MultipartPartSizeBytes,
                DisablePayloadSigning = true,
            }, ct).ConfigureAwait(false);

            // TransferUtility doesn't surface the CompleteMultipartUpload response, and the caller
            // needs the real ETag: the pull reconcile compares it against the remote one to decide
            // whether an object changed, so a missing ETag would make every large upload look like
            // a remote change and pull the file straight back down. One HEAD is cheap next to a
            // multipart upload.
            var head = await _s3.GetObjectMetadataAsync(
                new GetObjectMetadataRequest { BucketName = _bucket, Key = key }, ct).ConfigureAwait(false);
            return head.ETag;
        }

        var request = new PutObjectRequest
        {
            BucketName = _bucket,
            Key = key,
            FilePath = localPath,
            DisablePayloadSigning = true, // large-file friendly against S3-compatible endpoints
        };
        var response = await _s3.PutObjectAsync(request, ct).ConfigureAwait(false);
        return response.ETag;
    }

    public async Task DeleteAsync(string key, CancellationToken ct = default) =>
        await _s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = _bucket, Key = key }, ct)
            .ConfigureAwait(false);

    /// <summary>
    /// Deletes many objects using batched DeleteObjects requests (up to 1000 keys each) instead of
    /// one round trip per key — the difference between one request and a thousand when a folder goes.
    /// </summary>
    public async Task<DeleteResult> DeleteManyAsync(IEnumerable<string> keys, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var deleted = new List<string>();
        var failed = new List<string>();

        foreach (var batch in keys.Distinct(StringComparer.Ordinal).Chunk(DeleteBatchSize))
        {
            // Quiet keeps the response to just the failures, so a 1000-key delete doesn't ship a
            // large success payload back. Note the SDK signals per-key failures by throwing
            // DeleteObjectsException rather than returning them, so both paths are handled.
            var request = new DeleteObjectsRequest
            {
                BucketName = _bucket,
                Objects = batch.Select(k => new KeyVersion { Key = k }).ToList(),
                Quiet = true,
            };

            try
            {
                var response = await _s3.DeleteObjectsAsync(request, ct).ConfigureAwait(false);
                Record(batch, response.DeleteErrors);
            }
            catch (DeleteObjectsException ex)
            {
                // Some keys in this batch failed; the others were still removed.
                Record(batch, ex.Response.DeleteErrors);
            }
        }

        return new DeleteResult(deleted, failed);

        void Record(string[] batch, List<DeleteError>? errors)
        {
            var bad = errors?.Select(e => e.Key).ToHashSet(StringComparer.Ordinal)
                      ?? new HashSet<string>(StringComparer.Ordinal);
            deleted.AddRange(batch.Where(k => !bad.Contains(k)));
            failed.AddRange(batch.Where(bad.Contains));
        }
    }

    /// <summary>Server-side copy then delete of the source (an S3 "rename").</summary>
    public async Task MoveAsync(string sourceKey, string destKey, CancellationToken ct = default)
    {
        await _s3.CopyObjectAsync(new CopyObjectRequest
        {
            SourceBucket = _bucket,
            SourceKey = sourceKey,
            DestinationBucket = _bucket,
            DestinationKey = destKey,
        }, ct).ConfigureAwait(false);
        await DeleteAsync(sourceKey, ct).ConfigureAwait(false);
    }

    /// <summary>Creates a time-limited presigned GET URL for sharing an object.</summary>
    public string? CreateShareLink(string key, TimeSpan expiresIn) =>
        _s3.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = _bucket,
            Key = key,
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.Add(expiresIn),
        });

    public void Dispose()
    {
        _transfer.Dispose();
        _s3.Dispose();
    }
}
