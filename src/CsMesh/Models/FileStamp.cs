using System.Security.Cryptography;

namespace CsMesh.Models;

/// <summary>
/// Tracks file metadata to determine index freshness against the working tree.
/// </summary>
public sealed class FileStamp
{
    public string Path { get; set; } = string.Empty;
    public long Ticks { get; set; }
    public long Size { get; set; }

    /// <summary>
    /// SHA-256 over the exact bytes at <see cref="Path"/>, base64.
    ///
    /// Size and write time cannot see a same-size edit whose timestamp lands inside the freshness
    /// tolerance: before this field existed, <c>DirtyFiles</c> answered "clean" for it, no
    /// <c>[STALE]</c> was raised, and the query served pre-edit symbols -- a silent stale answer.
    /// The digest is the third input that settles that band.
    ///
    /// Base64 rather than hex because the value is opaque: it is compared ordinal and never shown,
    /// so the compact encoding is the only one with a reason to exist -- 44 characters against hex's
    /// 64 for the same 32 digest bytes.
    ///
    /// The hash is also what lets the tolerance stay: an exFAT or network mount that rounds a write
    /// time by up to two seconds no longer forces a permanent <c>[STALE]</c> and a heal that never
    /// finishes, because a file whose timestamp drifted while its bytes did not is still clean.
    /// The one case neither catches is an edit whose write time rounds to the same coarse granule as
    /// the stamp: then the delta is zero and the content is not read. That gap is deliberate -- see
    /// <c>GraphStore.DirtyFiles</c> for why hashing every file on every query is not the answer.
    /// </summary>
    public string Hash { get; set; } = string.Empty;

    /// <summary>
    /// The stored form of a digest: base64 of SHA-256 over the exact bytes. Shared by the indexer,
    /// which records it, and the freshness check, which recomputes it only for the narrow timestamp
    /// band where size and time cannot decide.
    /// </summary>
    public static string HashOf(byte[] bytes) => Convert.ToBase64String(SHA256.HashData(bytes));
}
