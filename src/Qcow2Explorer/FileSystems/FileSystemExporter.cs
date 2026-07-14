using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Qcow2Explorer.FileSystems;

public sealed record CopyProgress(string CurrentPath, long BytesCopied, int FilesCopied, int DirectoriesCreated);

public sealed record CopyOptions(bool ContinueOnError = true, bool CreateSha256Manifest = true);

public sealed record CopyError(string SourceName, string DestinationPath, string Message);

public sealed record CopyManifestEntry(string RelativePath, long Size, string Sha256);

public sealed record CopyResult(
    int FilesCopied,
    int DirectoriesCreated,
    long BytesCopied,
    IReadOnlyList<CopyError> Errors,
    IReadOnlyList<CopyManifestEntry> Manifest)
{
    public static CopyResult Empty { get; } = new(0, 0, 0, Array.Empty<CopyError>(), Array.Empty<CopyManifestEntry>());

    public CopyResult Add(CopyResult other)
    {
        return new CopyResult(
            FilesCopied + other.FilesCopied,
            DirectoriesCreated + other.DirectoriesCreated,
            BytesCopied + other.BytesCopied,
            Errors.Concat(other.Errors).ToArray(),
            Manifest.Concat(other.Manifest).ToArray());
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
        CancellationToken cancellationToken = default,
        CopyOptions? options = null)
    {
        options ??= new CopyOptions();
        Directory.CreateDirectory(destinationDirectory);
        var result = CopyResult.Empty;
        foreach (var node in nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                result = result.Add(CopyNode(fileSystem, node, destinationDirectory, destinationDirectory, progress, cancellationToken, options));
            }
            catch (Exception ex) when (options.ContinueOnError && ex is not OperationCanceledException)
            {
                result = result.Add(ErrorResult(node, destinationDirectory, ex));
            }
        }

        WriteResultFiles(destinationDirectory, result, options);

        return result;
    }

    public static CopyResult CopyNode(
        IReadOnlyFileSystem fileSystem,
        VfsNode node,
        string destinationDirectory,
        IProgress<CopyProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return CopyNode(fileSystem, node, destinationDirectory, destinationDirectory, progress, cancellationToken, new CopyOptions());
    }

    public static CopyResult CopyNode(
        IReadOnlyFileSystem fileSystem,
        VfsNode node,
        string destinationDirectory,
        string copyRoot,
        IProgress<CopyProgress>? progress = null,
        CancellationToken cancellationToken = default,
        CopyOptions? options = null)
    {
        options ??= new CopyOptions();
        Directory.CreateDirectory(destinationDirectory);
        var targetName = string.IsNullOrWhiteSpace(node.Name) ? "root" : SanitizeFileName(node.Name);
        var targetPath = GetAvailablePath(Path.Combine(destinationDirectory, targetName), node.IsDirectory);
        return node.IsDirectory
            ? CopyDirectory(fileSystem, node, targetPath, copyRoot, progress, cancellationToken, options)
            : CopyFile(fileSystem, node, targetPath, copyRoot, progress, cancellationToken, options);
    }

    private static CopyResult CopyDirectory(
        IReadOnlyFileSystem fileSystem,
        VfsNode directory,
        string destinationPath,
        string copyRoot,
        IProgress<CopyProgress>? progress,
        CancellationToken cancellationToken,
        CopyOptions options)
    {
        Directory.CreateDirectory(destinationPath);
        var result = new CopyResult(0, 1, 0, Array.Empty<CopyError>(), Array.Empty<CopyManifestEntry>());
        progress?.Report(new CopyProgress(destinationPath, 0, 0, 1));

        foreach (var child in fileSystem.ListDirectory(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                result = result.Add(CopyNode(fileSystem, child, destinationPath, copyRoot, progress, cancellationToken, options));
            }
            catch (Exception ex) when (options.ContinueOnError && ex is not OperationCanceledException)
            {
                result = result.Add(ErrorResult(child, destinationPath, ex));
            }
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
        string copyRoot,
        IProgress<CopyProgress>? progress,
        CancellationToken cancellationToken,
        CopyOptions options)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? ".");

        long offset = 0;
        using var hash = options.CreateSha256Manifest ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
        try
        {
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
                    hash?.AppendData(chunk);
                    offset += chunk.Length;
                    progress?.Report(new CopyProgress(destinationPath, offset, 0, 0));
                }
            }
        }
        catch
        {
            TryDeletePartialFile(destinationPath);
            throw;
        }

        if (file.ModifiedUtc.HasValue)
        {
            TrySetLastWriteTime(destinationPath, file.ModifiedUtc.Value, isDirectory: false);
        }

        var manifest = options.CreateSha256Manifest
            ? new[] { new CopyManifestEntry(Path.GetRelativePath(copyRoot, destinationPath), file.Size, Convert.ToHexString(hash!.GetHashAndReset()).ToLowerInvariant()) }
            : Array.Empty<CopyManifestEntry>();
        return new CopyResult(1, 0, file.Size, Array.Empty<CopyError>(), manifest);
    }

    private static CopyResult ErrorResult(VfsNode node, string destinationPath, Exception exception)
    {
        return new CopyResult(0, 0, 0,
            new[] { new CopyError(node.DisplayName, destinationPath, exception.Message) },
            Array.Empty<CopyManifestEntry>());
    }

    private static void WriteResultFiles(string destinationDirectory, CopyResult result, CopyOptions options)
    {
        if (options.CreateSha256Manifest && result.Manifest.Count > 0)
        {
            var lines = result.Manifest.Select(entry => $"{entry.Sha256} *{entry.RelativePath}");
            File.WriteAllLines(Path.Combine(destinationDirectory, "VirtualDiskExplorer.sha256"), lines, new UTF8Encoding(false));
        }

        if (result.Errors.Count > 0)
        {
            var json = JsonSerializer.Serialize(result.Errors, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(destinationDirectory, "VirtualDiskExplorer-copy-errors.json"), json, new UTF8Encoding(false));
        }
    }

    private static void TryDeletePartialFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
        }
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
