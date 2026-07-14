namespace Qcow2Explorer.FileSystems;

public sealed class VfsNode
{
    public string Name { get; init; } = "";
    public bool IsDirectory { get; init; }
    public long Size { get; init; }
    public DateTime? ModifiedUtc { get; init; }
    public FileAttributes Attributes { get; init; }
    public object? Metadata { get; init; }

    public string DisplayName => string.IsNullOrEmpty(Name) ? "/" : Name;
}
