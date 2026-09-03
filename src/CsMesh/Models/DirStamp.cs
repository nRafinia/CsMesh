namespace CsMesh.Models;

/// <summary>
/// Tracks a source directory's last write time. Creating, renaming or deleting a file inside a
/// directory updates that directory's timestamp, so comparing these is enough to decide whether
/// a full tree walk is needed to look for files added since the index was built.
/// </summary>
public sealed class DirStamp
{
    public string Path { get; set; } = string.Empty;
    public long Ticks { get; set; }
}
