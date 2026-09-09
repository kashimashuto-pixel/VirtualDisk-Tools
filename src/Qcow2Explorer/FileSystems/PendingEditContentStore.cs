namespace Qcow2Explorer.FileSystems;

internal sealed class PendingEditContentStore : IDisposable
{
    private const int CopyBufferSize = 1024 * 1024;
    private readonly string _rootPath;
    private bool _disposed;

    public PendingEditContentStore(string? basePath = null)
    {
        var root = string.IsNullOrWhiteSpace(basePath)
            ? Path.Combine(Path.GetTempPath(), "VirtualDiskExplorer", "PendingEdits")
            : Path.GetFullPath(basePath);
        _rootPath = Path.Combine(root, Guid.NewGuid().ToString("N"));
    }

    internal string RootPath => _rootPath;

    public string CreateEmptyWorkingCopy(string virtualFileName)
    {
        ThrowIfDisposed();
        var path = CreateArtifactPath("working", virtualFileName);
        using var _ = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        return path;
    }

    public string CreateWorkingCopy(
        string sourcePath,
        string virtualFileName,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        sourcePath = Path.GetFullPath(sourcePath);
        var path = CreateArtifactPath("working", virtualFileName);
        try
        {
            using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            CopyStream(source, destination, cancellationToken);
            destination.Flush(flushToDisk: true);
            return path;
        }
        catch
        {
            TryDeleteOwnedFile(path);
            throw;
        }
    }

    public string CreateWorkingCopy(
        IReadOnlyFileSystem fileSystem,
        VfsNode file,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(file);
        if (file.IsDirectory || file.Size < 0)
        {
            throw new ArgumentException("外部編集用に取り出せるのは通常ファイルだけです。", nameof(file));
        }

        var path = CreateArtifactPath("working", file.Name);
        long offset = 0;
        try
        {
            using var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            while (offset < file.Size)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = checked((int)Math.Min(CopyBufferSize, file.Size - offset));
                var data = fileSystem.ReadFile(file, offset, count);
                if (data.Length == 0 || data.Length > count || offset > file.Size - data.Length)
                {
                    throw new EndOfStreamException(
                        $"外部編集用ファイルを最後まで取り出せませんでした: {file.Name}, offset={offset:N0}, size={file.Size:N0}");
                }

                destination.Write(data);
                offset += data.Length;
            }

            destination.Flush(flushToDisk: true);
            return path;
        }
        catch
        {
            TryDeleteOwnedFile(path);
            throw;
        }
    }

    public string CaptureWorkingCopy(
        string workingPath,
        string virtualFileName,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!OwnsPath(workingPath) || !File.Exists(workingPath))
        {
            throw new FileNotFoundException("外部編集用の一時ファイルが見つかりません。", workingPath);
        }

        var path = CreateArtifactPath("captured", virtualFileName);
        try
        {
            // Do not capture while an editor still owns a writable handle. This prevents a torn
            // snapshot if the user presses "import" while the editor is in the middle of saving.
            using var source = new FileStream(workingPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            CopyStream(source, destination, cancellationToken);
            destination.Flush(flushToDisk: true);
            return path;
        }
        catch
        {
            TryDeleteOwnedFile(path);
            throw;
        }
    }

    public bool TryDeleteOwnedFile(string path)
    {
        if (!OwnsPath(path))
        {
            return false;
        }

        try
        {
            File.Delete(Path.GetFullPath(path));
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (directory is not null && Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public bool OwnsPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var rootPrefix = Path.GetFullPath(_rootPath).TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (Directory.Exists(_rootPath))
            {
                Directory.Delete(_rootPath, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private string CreateArtifactPath(string kind, string virtualFileName)
    {
        var directory = Path.Combine(_rootPath, $"{kind}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, SanitizeFileName(virtualFileName));
    }

    private static string SanitizeFileName(string name)
    {
        name = Path.GetFileName(name);
        if (string.IsNullOrWhiteSpace(name))
        {
            return "edit.bin";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var characters = name
            .Select(character => invalid.Contains(character) || char.IsControl(character) ? '_' : character)
            .ToArray();
        var sanitized = new string(characters).Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(sanitized) ? "edit.bin" : sanitized;
    }

    private static void CopyStream(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        var buffer = new byte[CopyBufferSize];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = source.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                return;
            }

            destination.Write(buffer, 0, read);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
