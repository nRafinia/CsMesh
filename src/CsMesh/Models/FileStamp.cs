namespace CsMesh.Models;

/// <summary>
/// Tracks file metadata to determine index freshness against the working tree.
/// </summary>
public sealed class FileStamp
{
    public string Path { get; set; } = string.Empty;
    public long Ticks { get; set; }
    public long Size { get; set; }
}
