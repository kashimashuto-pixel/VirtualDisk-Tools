namespace Qcow2Explorer.FileSystems;

public interface IFileContentWriter
{
    bool CanReplaceFile(VfsNode file, long replacementLength, out string reason);

    void ReplaceFileContent(
        VfsNode file,
        Stream replacement,
        long replacementLength,
        CancellationToken cancellationToken = default);
}
