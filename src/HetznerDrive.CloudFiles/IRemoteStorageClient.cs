namespace HetznerDrive.CloudFiles;

/// <summary>One file listed under a remote prefix.</summary>
/// <param name="Key">
/// Full remote key: for S3 the object key, for the file protocols the path relative to the
/// mapping's root, always using forward slashes.
/// </param>
/// <param name="Size">Size in bytes.</param>
/// <param name="LastModifiedUtc">Last-modified timestamp (UTC).</param>
/// <param name="ETag">
/// A token that changes whenever the remote content does. S3 supplies a real ETag; the file
/// protocols have no equivalent, so <see cref="RemoteEntry.SyntheticTag"/> derives one from size
/// and mtime. Either way the reconciler only ever compares it for equality.
/// </param>
public sealed record RemoteEntry(string Key, long Size, DateTime LastModifiedUtc, string? ETag)
{
    /// <summary>
    /// A change token for backends without ETags. Size alone misses same-length edits and mtime
    /// alone misses timestamp-preserving writes, so both go in.
    /// </summary>
    public static string SyntheticTag(long size, DateTime lastModifiedUtc) =>
        $"{size:x}-{lastModifiedUtc.ToUniversalTime().Ticks:x}";
}

/// <summary>The outcome of a batched delete: which keys went, and which the server rejected.</summary>
/// <param name="Deleted">Keys confirmed removed.</param>
/// <param name="Failed">Keys the server refused or errored on.</param>
public sealed record DeleteResult(IReadOnlyList<string> Deleted, IReadOnlyList<string> Failed);

/// <summary>
/// The storage operations the Files On-Demand engine needs, independent of protocol.
///
/// The four Hetzner access paths differ enough that the on-demand layer cannot be written against
/// any one of them: S3 has no directories but has server-side copy and presigned URLs, SFTP and
/// SMB have real directories and cheap renames, WebDAV has directories and a MOVE verb. This
/// interface is the narrow set they can all honour, with the awkward differences (rename cost,
/// share links, directory markers) declared as capabilities rather than assumed.
/// </summary>
public interface IRemoteStorageClient : IDisposable
{
    /// <summary>Human-facing name of the protocol, for log lines and the UI.</summary>
    string ProtocolName { get; }

    /// <summary>
    /// True when the backend can produce a time-limited public URL for an object. Only S3 can;
    /// on a Storage Box, sharing goes through Hetzner's own share feature instead.
    /// </summary>
    bool SupportsShareLinks { get; }

    /// <summary>Enumerates every file at or under <paramref name="prefix"/>, recursively.</summary>
    IAsyncEnumerable<RemoteEntry> ListAsync(string? prefix = null, CancellationToken ct = default);

    /// <summary>
    /// Opens a read stream for a byte range. When <paramref name="length"/> is null, reads to the
    /// end from <paramref name="offset"/>. Used to feed hydration.
    /// </summary>
    Task<Stream> OpenReadAsync(string key, long offset, long? length, CancellationToken ct = default);

    /// <summary>
    /// Uploads a local file to <paramref name="key"/>, overwriting, creating parent directories as
    /// needed. Returns the new change token (see <see cref="RemoteEntry.ETag"/>).
    /// </summary>
    Task<string?> PutAsync(string key, string localPath, CancellationToken ct = default);

    /// <summary>Deletes one file. Missing files are not an error.</summary>
    Task DeleteAsync(string key, CancellationToken ct = default);

    /// <summary>
    /// Deletes many files, batching where the protocol supports it. Partial failures are reported
    /// rather than thrown, so the keys that did go can be forgotten while the rest stay tracked.
    /// </summary>
    Task<DeleteResult> DeleteManyAsync(IEnumerable<string> keys, CancellationToken ct = default);

    /// <summary>Renames/moves a file server-side.</summary>
    Task MoveAsync(string sourceKey, string destKey, CancellationToken ct = default);

    /// <summary>
    /// A time-limited share URL, or null when <see cref="SupportsShareLinks"/> is false.
    /// </summary>
    string? CreateShareLink(string key, TimeSpan expiresIn);
}
