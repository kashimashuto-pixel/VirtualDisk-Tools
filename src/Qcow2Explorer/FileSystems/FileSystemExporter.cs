using System.Text;

namespace Qcow2Explorer.FileSystems;

public sealed record CopyProgress(string CurrentPath, long BytesCopied, int FilesCopied, int DirectoriesCreated);

public sealed record CopyResult(int FilesCopied, int DirectoriesCreated, long BytesCopied)
{
    public static CopyResult Empty { get; } = new(0, 0, 0);

    public CopyResult Add(CopyResult other)
    {
        return new CopyResult(
            FilesCopied + other.FilesCopied,
            DirectoriesCreated + other.DirectoriesCreated,
            BytesCopied + other.BytesCopied);
    }
}

public static class FileSystemExporter
{
    private const int CopyBufferSize = 1024 * 1024;

    public static CopyResult CopyNodes(
        IReadOnlyFileSystem fileSystem,
        IEnumerable<VfsNode> nodes,
        string destinationDirectory,
        IProgress<CopyProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(destinationDirectory);
        var result = CopyResult.Empty;
        foreach (var node in nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result = result.Add(CopyNode(fileSystem, node, destinationDirectory, progress, cancellationToken));
        }

        return result;
    }

    public static CopyResult CopyNode(
        IReadOnlyFileSystem fileSystem,
        VfsNode node,
        string destinationDirectory,
        IProgress<CopyProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(destinationDirectory);
        var targetName = string.IsNullOrWhiteSpace(node.Name) ? "root" : SanitizeFileName(node.Name);
        var targetPath = GetAvailablePath(Path.Combine(destinationDirectory, targetName), node.IsDirectory);
        return node.IsDirectory
            ? CopyDirectory(fileSystem, node, targetPath, progress, cancellationToken)
            : CopyFile(fileSystem, node, targetPath, progress, cancellationToken);
    }

    private static CopyResult CopyDirectory(
        IReadOnlyFileSystem fileSystem,
        VfsNode directory,
        string destinationPath,
        IProgress<CopyProgress>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destinationPath);
        var result = new CopyResult(0, 1, 0);
        progress?.Report(new CopyProgress(destinationPath, 0, 0, 1));

        foreach (var child in fileSystem.ListDirectory(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            result = result.Add(CopyNode(fileSystem, child, destinationPath, progress, cancellationToken));
        }

        if (directory.ModifiedUtc.HasValue)
        {
            TrySetLastWriteTime(destinationPath, directory.ModifiedUtc.Value, isDirectory: true);
        }

        return result;
    }

    private static CopyResult CopyFile(
        IReadOnlyFileSystem fileSystem,
        VfsNode file,
        string destinationPath,
        IProgress<CopyProgress>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? ".");

        long offset = 0;
        using (var output = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            if (file.Size == 0)
            {
                progress?.Report(new CopyProgress(destinationPath, 0, 1, 0));
            }

            while (offset < file.Size)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var chunkSize = (int)Math.Min(CopyBufferSize, file.Size - offset);
                var chunk = fileSystem.ReadFile(file, offset, chunkSize);
                if (chunk.Length == 0)
                {
                    throw new EndOfStreamException($"ファイルの途中で読み込みが止まりました: {file.Name}");
                }

                output.Write(chunk, 0, chunk.Length);
                offset += chunk.Length;
                progress?.Report(new CopyProgress(destinationPath, offset, 0, 0));
            }
        }

        if (file.ModifiedUtc.HasValue)
        {
            TrySetLastWriteTime(destinationPath, file.ModifiedUtc.Value, isDirectory: false);
        }

        return new CopyResult(1, 0, file.Size);
    }

    private static void TrySetLastWriteTime(string path, DateTime utc, bool isDirectory)
    {
        try
        {
            if (isDirectory)
            {
                Directory.SetLastWriteTimeUtc(path, utc);
            }
            else
            {
                File.SetLastWriteTimeUtc(path, utc);
            }
        }
        catch
        {
            // Timestamp preservation is helpful, but copying the bytes is the important part.
        }
    }

    private static string GetAvailablePath(string path, bool isDirectory)
    {
        if (!Exists(path, isDirectory))
        {
            return path;
        }

        var directory = Path.GetDirectoryName(path) ?? "";
        var fileName = Path.GetFileNameWithoutExtension(path);
        var extension = isDirectory ? "" : Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(directory, $"{fileName} ({i}){extension}");
            if (!Exists(candidate, isDirectory))
            {
                return candidate;
            }
        }

        static bool Exists(string candidate, bool directory)
        {
            return directory ? Directory.Exists(candidate) || File.Exists(candidate) : File.Exists(candidate) || Directory.Exists(candidate);
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var builder = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            builder.Append(invalid.Contains(ch) || ch < 32 ? '_' : ch);
        }

        var sanitized = builder.ToString().Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            sanitized = "unnamed";
        }

        return IsReservedWindowsName(sanitized) ? $"_{sanitized}" : sanitized;
    }

    private static bool IsReservedWindowsName(string name)
    {
        var baseName = Path.GetFileNameWithoutExtension(name).ToUpperInvariant();
        return baseName is "CON" or "PRN" or "AUX" or "NUL"
            or "COM1" or "COM2" or "COM3" or "COM4" or "COM5" or "COM6" or "COM7" or "COM8" or "COM9"
            or "LPT1" or "LPT2" or "LPT3" or "LPT4" or "LPT5" or "LPT6" or "LPT7" or "LPT8" or "LPT9";
    }
}
