namespace Qcow2Explorer.FileSystems;

public interface IFileSystemEditor
{
    bool ValidateFileSystem(out string reason);

    bool CanWriteFile(VfsNode file, long contentLength, out string reason);

    void WriteFileContent(
        VfsNode file,
        Stream content,
        long contentLength,
        CancellationToken cancellationToken = default);

    bool CanCreateFile(VfsNode directory, string name, long contentLength, out string reason);

    VfsNode CreateFile(
        VfsNode directory,
        string name,
        Stream content,
        long contentLength,
        CancellationToken cancellationToken = default);

    bool CanDeleteFile(VfsNode directory, VfsNode file, out string reason);

    void DeleteFile(
        VfsNode directory,
        VfsNode file,
        CancellationToken cancellationToken = default);
}
