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

    bool CanCreateDirectory(VfsNode directory, string name, out string reason)
    {
        reason = "このファイルシステムのディレクトリ作成にはまだ対応していません。";
        return false;
    }

    VfsNode CreateDirectory(
        VfsNode directory,
        string name,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("このファイルシステムのディレクトリ作成にはまだ対応していません。");

    bool CanDeleteDirectory(VfsNode parentDirectory, VfsNode directory, out string reason)
    {
        reason = "このファイルシステムのディレクトリ削除にはまだ対応していません。";
        return false;
    }

    void DeleteDirectory(
        VfsNode parentDirectory,
        VfsNode directory,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("このファイルシステムのディレクトリ削除にはまだ対応していません。");

    bool CanMoveEntry(
        VfsNode sourceDirectory,
        VfsNode entry,
        VfsNode destinationDirectory,
        string destinationName,
        out string reason)
    {
        reason = "このファイルシステムの移動・名前変更にはまだ対応していません。";
        return false;
    }

    VfsNode MoveEntry(
        VfsNode sourceDirectory,
        VfsNode entry,
        VfsNode destinationDirectory,
        string destinationName,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("このファイルシステムの移動・名前変更にはまだ対応していません。");

    bool CanSetAttributes(VfsNode entry, FileAttributes attributes, out string reason)
    {
        reason = "このファイルシステムの属性編集にはまだ対応していません。";
        return false;
    }

    void SetAttributes(
        VfsNode entry,
        FileAttributes attributes,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("このファイルシステムの属性編集にはまだ対応していません。");

    bool CanSetLastWriteTimeUtc(VfsNode entry, DateTime modifiedUtc, out string reason)
    {
        reason = "このファイルシステムの更新日時編集にはまだ対応していません。";
        return false;
    }

    void SetLastWriteTimeUtc(
        VfsNode entry,
        DateTime modifiedUtc,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("このファイルシステムの更新日時編集にはまだ対応していません。");
}
