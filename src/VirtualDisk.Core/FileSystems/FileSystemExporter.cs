using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Qcow2Explorer.Core;

namespace Qcow2Explorer.FileSystems;

public sealed record CopyProgress(
    string CurrentPath,
    long BytesCopied,
    long TotalBytes,
    int FilesCopied,
    int DirectoriesCreated,
    TimeSpan Elapsed);

public sealed record CopyOptions(
    bool ContinueOnError = true,
    int MaximumDepth = 256,
    int MaximumEntries = 1_000_000,
    int MaximumErrors = 10_000);

public sealed record CopyError(string SourceName, string DestinationPath, string Message);

public sealed record CopyResult(
    int FilesCopied,
    int DirectoriesCreated,
    long BytesCopied,
    IReadOnlyList<CopyError> Errors)
{
    public static CopyResult Empty { get; } = new(0, 0, 0, Array.Empty<CopyError>());

    public CopyResult Add(CopyResult other)
    {
        return new CopyResult(
            FilesCopied + other.FilesCopied,
            DirectoriesCreated + other.DirectoriesCreated,
            BytesCopied + other.BytesCopied,
            Errors.Concat(other.Errors).ToArray());
    }
}

public static class FileSystemExporter
{
    private const int CopyBufferSize = 1024 * 1024;

    public static async Task ExtractFileAsync(
        IReadOnlyFileSystem fileSystem,
        VfsNode file,
        string destinationPath,
        bool overwrite = false,
        IProgress<CopyProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(file);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        if (file.IsDirectory || file.Size < 0)
        {
            throw new ArgumentException("抽出対象はサイズが確定した通常ファイルである必要があります。", nameof(file));
        }

        var fullDestinationPath = Path.GetFullPath(destinationPath);
        if (Directory.Exists(fullDestinationPath)
            || (!overwrite && File.Exists(fullDestinationPath)))
        {
            throw new IOException($"抽出先は既に存在します: {fullDestinationPath}");
        }

        var parent = Path.GetDirectoryName(fullDestinationPath)
            ?? throw new IOException("抽出先ディレクトリを取得できません。");
        Directory.CreateDirectory(parent);
        var partialPath = Path.Combine(
            parent,
            $".{Path.GetFileName(fullDestinationPath)}.{Guid.NewGuid():N}.vdt-partial");
        var stopwatch = Stopwatch.StartNew();
        long offset = 0;
        try
        {
            await using (var output = new FileStream(
                partialPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                CopyBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                while (offset < file.Size)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var chunkSize = checked((int)Math.Min(CopyBufferSize, file.Size - offset));
                    var chunk = fileSystem.ReadFile(file, offset, chunkSize);
                    if (chunk.Length != chunkSize)
                    {
                        var exception = CreateUnexpectedEofException(
                            fileSystem,
                            file,
                            offset,
                            chunkSize,
                            chunk.Length);
                        DiagnosticLog.Write($"File extraction failed: {exception}");
                        throw exception;
                    }

                    await output.WriteAsync(chunk, cancellationToken);
                    offset += chunk.Length;
                    progress?.Report(new CopyProgress(
                        fullDestinationPath,
                        offset,
                        file.Size,
                        0,
                        0,
                        stopwatch.Elapsed));
                }

                await output.FlushAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(partialPath, fullDestinationPath, overwrite);
            progress?.Report(new CopyProgress(
                fullDestinationPath,
                offset,
                file.Size,
                1,
                0,
                stopwatch.Elapsed));
        }
        catch
        {
            TryDeletePartialFile(partialPath);
            throw;
        }
    }

    public static CopyResult CopyNodes(
        IReadOnlyFileSystem fileSystem,
        IEnumerable<VfsNode> nodes,
        string destinationDirectory,
        IProgress<CopyProgress>? progress = null,
        CancellationToken cancellationToken = default,
        CopyOptions? options = null)
    {
        options ??= new CopyOptions();
        ValidateOptions(options);
        var nodeList = MaterializeNodes(nodes, options.MaximumEntries, cancellationToken);
        Directory.CreateDirectory(destinationDirectory);
        var progressState = new CopyProgressState(
            progress,
            CalculateTotalBytes(
                fileSystem,
                nodeList,
                cancellationToken,
                options,
                new TraversalState(options),
                depth: 0));
        var traversal = new TraversalState(options);
        var pathAllocator = new DestinationPathAllocator();
        var result = new CopyAccumulator(options.MaximumErrors);
        foreach (var node in nodeList)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                CopyNodeCore(
                    fileSystem,
                    node,
                    destinationDirectory,
                    progressState,
                    cancellationToken,
                    options,
                    traversal,
                    pathAllocator,
                    result,
                    depth: 0);
            }
            catch (Exception ex) when (CanContinueAfter(ex, options))
            {
                result.AddError(node, destinationDirectory, ex);
            }
        }

        var copyResult = result.ToResult();
        WriteResultFiles(destinationDirectory, copyResult);

        return copyResult;
    }

    public static CopyResult CopyNode(
        IReadOnlyFileSystem fileSystem,
        VfsNode node,
        string destinationDirectory,
        IProgress<CopyProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var options = new CopyOptions();
        var totalTraversal = new TraversalState(options);
        var progressState = new CopyProgressState(
            progress,
            CalculateTotalBytes(fileSystem, [node], cancellationToken, options, totalTraversal, depth: 0));
        var result = new CopyAccumulator(options.MaximumErrors);
        var pathAllocator = new DestinationPathAllocator();
        CopyNodeCore(
            fileSystem,
            node,
            destinationDirectory,
            progressState,
            cancellationToken,
            options,
            new TraversalState(options),
            pathAllocator,
            result,
            depth: 0);
        return result.ToResult();
    }

    private static void CopyNodeCore(
        IReadOnlyFileSystem fileSystem,
        VfsNode node,
        string destinationDirectory,
        CopyProgressState progress,
        CancellationToken cancellationToken = default,
        CopyOptions? options = null,
        TraversalState? traversal = null,
        DestinationPathAllocator? pathAllocator = null,
        CopyAccumulator? result = null,
        int depth = 0)
    {
        options ??= new CopyOptions();
        traversal ??= new TraversalState(options);
        pathAllocator ??= new DestinationPathAllocator();
        result ??= new CopyAccumulator(options.MaximumErrors);
        traversal.Visit(depth);
        if (!node.IsDirectory && node.Size < 0)
        {
            throw new InvalidDataException($"ファイルサイズが不正です: {node.DisplayName} ({node.Size:N0} bytes)");
        }

        Directory.CreateDirectory(destinationDirectory);
        var targetName = string.IsNullOrWhiteSpace(node.Name) ? "root" : SanitizeFileName(node.Name);
        var targetPath = pathAllocator.Allocate(
            Path.Combine(destinationDirectory, targetName),
            node.IsDirectory,
            cancellationToken);
        if (node.IsDirectory)
        {
            CopyDirectory(
                fileSystem,
                node,
                targetPath,
                progress,
                cancellationToken,
                options,
                traversal,
                pathAllocator,
                result,
                depth);
        }
        else
        {
            CopyFile(fileSystem, node, targetPath, progress, cancellationToken, result);
        }
    }

    private static void CopyDirectory(
        IReadOnlyFileSystem fileSystem,
        VfsNode directory,
        string destinationPath,
        CopyProgressState progress,
        CancellationToken cancellationToken,
        CopyOptions options,
        TraversalState traversal,
        DestinationPathAllocator pathAllocator,
        CopyAccumulator result,
        int depth)
    {
        using var directoryScope = traversal.EnterDirectory(directory);
        Directory.CreateDirectory(destinationPath);
        result.AddDirectory();
        progress.Report(destinationPath, bytesCopied: 0, filesCopied: 0, directoriesCreated: 1);

        foreach (var child in fileSystem.ListDirectory(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (child is null)
            {
                throw new InvalidDataException("ファイルシステムがnullノードを返しました。");
            }

            try
            {
                CopyNodeCore(
                    fileSystem,
                    child,
                    destinationPath,
                    progress,
                    cancellationToken,
                    options,
                    traversal,
                    pathAllocator,
                    result,
                    checked(depth + 1));
            }
            catch (Exception ex) when (CanContinueAfter(ex, options))
            {
                result.AddError(child, destinationPath, ex);
            }
        }

        if (directory.ModifiedUtc.HasValue)
        {
            TrySetLastWriteTime(destinationPath, directory.ModifiedUtc.Value, isDirectory: true);
        }
    }

    private static void CopyFile(
        IReadOnlyFileSystem fileSystem,
        VfsNode file,
        string destinationPath,
        CopyProgressState progress,
        CancellationToken cancellationToken,
        CopyAccumulator result)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? ".");

        long offset = 0;
        try
        {
            using (var output = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                if (file.Size == 0)
                {
                    progress.Report(destinationPath, bytesCopied: 0, filesCopied: 1, directoriesCreated: 0);
                }

                while (offset < file.Size)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var chunkSize = (int)Math.Min(CopyBufferSize, file.Size - offset);
                    var chunk = fileSystem.ReadFile(file, offset, chunkSize);
                    if (chunk.Length != chunkSize)
                    {
                        var exception = CreateUnexpectedEofException(
                            fileSystem,
                            file,
                            offset,
                            chunkSize,
                            chunk.Length);
                        DiagnosticLog.Write($"File export failed: {exception}");
                        throw exception;
                    }

                    output.Write(chunk, 0, chunk.Length);
                    offset += chunk.Length;
                    progress.Report(destinationPath, chunk.Length, filesCopied: 0, directoriesCreated: 0);
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

        if (file.Size > 0)
        {
            progress.Report(destinationPath, bytesCopied: 0, filesCopied: 1, directoriesCreated: 0);
        }

        result.AddFile(file.Size);
    }

    private static long CalculateTotalBytes(
        IReadOnlyFileSystem fileSystem,
        IEnumerable<VfsNode> nodes,
        CancellationToken cancellationToken,
        CopyOptions options,
        TraversalState traversal,
        int depth)
    {
        long totalBytes = 0;
        foreach (var node in nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (node is null)
            {
                throw new InvalidDataException("ファイルシステムがnullノードを返しました。");
            }

            traversal.Visit(depth);
            if (!node.IsDirectory)
            {
                totalBytes = AddSaturating(totalBytes, Math.Max(0, node.Size));
                continue;
            }

            try
            {
                using var directoryScope = traversal.EnterDirectory(node);
                totalBytes = AddSaturating(totalBytes, CalculateTotalBytes(
                    fileSystem,
                    fileSystem.ListDirectory(node),
                    cancellationToken,
                    options,
                    traversal,
                    checked(depth + 1)));
            }
            catch (Exception ex) when (CanContinueAfter(ex, options))
            {
                // The copy pass records the directory error; an unreadable subtree contributes no known bytes.
            }
        }

        return totalBytes;
    }

    private static long AddSaturating(long left, long right) =>
        right > long.MaxValue - left ? long.MaxValue : left + right;

    private static List<VfsNode> MaterializeNodes(
        IEnumerable<VfsNode> nodes,
        int maximumEntries,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        var result = new List<VfsNode>();
        foreach (var node in nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (result.Count >= maximumEntries)
            {
                throw new TraversalLimitException(
                    $"エクスポート対象数が上限 ({maximumEntries:N0}) を超えています。");
            }

            result.Add(node ?? throw new InvalidDataException("ファイルシステムがnullノードを返しました。"));
        }

        return result;
    }

    private static bool CanContinueAfter(Exception exception, CopyOptions options) =>
        options.ContinueOnError
        && exception is not TraversalLimitException
        && exception is IOException
            or InvalidDataException
            or NotSupportedException
            or UnauthorizedAccessException
            or ArgumentException
            or OverflowException
            or CryptographicException;

    private static void ValidateOptions(CopyOptions options)
    {
        if (options.MaximumDepth < 1 || options.MaximumEntries < 1 || options.MaximumErrors < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "エクスポートの最大深度、最大項目数、最大エラー数には正の整数を指定してください。");
        }
    }

    private static void WriteResultFiles(string destinationDirectory, CopyResult result)
    {
        if (result.Errors.Count > 0)
        {
            var json = JsonSerializer.Serialize(result.Errors, new JsonSerializerOptions { WriteIndented = true });
            var errorPath = new DestinationPathAllocator().Allocate(
                Path.Combine(destinationDirectory, "VirtualDiskExplorer-copy-errors.json"),
                isDirectory: false,
                CancellationToken.None);
            File.WriteAllText(errorPath, json, new UTF8Encoding(false));
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

    private static EndOfStreamException CreateUnexpectedEofException(
        IReadOnlyFileSystem fileSystem,
        VfsNode file,
        long offset,
        int requested,
        int actual) =>
        new(
            $"Unexpected EOF while reading '{file.Name}'. size={file.Size}, offset={offset}, "
            + $"requested={requested}, actual={actual}, fileSystem={fileSystem.Name}, node={file.Metadata}");

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

    private sealed class DestinationPathAllocator
    {
        private readonly Dictionary<string, int> _nextSuffixByPath = new(
            OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);

        public string Allocate(string path, bool isDirectory, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = $"{(isDirectory ? 'D' : 'F')}\0{path}";
            if (!Exists(path))
            {
                return path;
            }

            var directory = Path.GetDirectoryName(path) ?? "";
            var fileName = Path.GetFileNameWithoutExtension(path);
            var extension = isDirectory ? "" : Path.GetExtension(path);
            var index = _nextSuffixByPath.TryGetValue(key, out var nextIndex) ? nextIndex : 2;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var candidate = Path.Combine(directory, $"{fileName} ({index}){extension}");
                if (!Exists(candidate))
                {
                    _nextSuffixByPath[key] = index == int.MaxValue
                        ? int.MaxValue
                        : index + 1;
                    return candidate;
                }

                if (index == int.MaxValue)
                {
                    throw new NotSupportedException("同名出力の連番が対応上限を超えています。");
                }

                index++;
            }
        }

        private static bool Exists(string candidate) =>
            File.Exists(candidate) || Directory.Exists(candidate);
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

    private sealed class CopyProgressState(IProgress<CopyProgress>? progress, long totalBytes)
    {
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private long _bytesCopied;
        private int _filesCopied;
        private int _directoriesCreated;

        public void Report(string currentPath, long bytesCopied, int filesCopied, int directoriesCreated)
        {
            _bytesCopied = AddSaturating(_bytesCopied, bytesCopied);
            _filesCopied += filesCopied;
            _directoriesCreated += directoriesCreated;
            progress?.Report(new CopyProgress(
                currentPath,
                _bytesCopied,
                totalBytes,
                _filesCopied,
                _directoriesCreated,
                _stopwatch.Elapsed));
        }
    }

    private sealed class CopyAccumulator(int maximumErrors)
    {
        private readonly List<CopyError> _errors = new();
        private int _filesCopied;
        private int _directoriesCreated;
        private long _bytesCopied;

        public void AddDirectory() => _directoriesCreated++;

        public void AddFile(long bytes)
        {
            _filesCopied++;
            _bytesCopied = AddSaturating(_bytesCopied, bytes);
        }

        public void AddError(VfsNode node, string destinationPath, Exception exception)
        {
            if (_errors.Count >= maximumErrors)
            {
                throw new TraversalLimitException(
                    $"エクスポートのエラー数が上限 ({maximumErrors:N0}) を超えています。");
            }

            _errors.Add(new CopyError(node.DisplayName, destinationPath, exception.Message));
        }

        public CopyResult ToResult() =>
            new(_filesCopied, _directoriesCreated, _bytesCopied, _errors.ToArray());
    }

    private sealed class TraversalState(CopyOptions options)
    {
        private readonly HashSet<VfsNode> _activeNodes = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<string> _activePaths = new(StringComparer.Ordinal);
        private int _entries;

        public void Visit(int depth)
        {
            if (depth > options.MaximumDepth)
            {
                throw new TraversalLimitException(
                    $"ファイルシステムのディレクトリ深度が上限 ({options.MaximumDepth:N0}) を超えています。");
            }

            _entries++;
            if (_entries > options.MaximumEntries)
            {
                throw new TraversalLimitException(
                    $"ファイルシステムの項目数が上限 ({options.MaximumEntries:N0}) を超えています。");
            }
        }

        public IDisposable EnterDirectory(VfsNode directory)
        {
            var path = directory.VirtualPath;
            if (!_activeNodes.Add(directory))
            {
                throw new InvalidDataException(
                    $"ファイルシステムのディレクトリ構造に循環参照があります: {directory.VirtualPath}");
            }

            if (!string.IsNullOrEmpty(path) && !_activePaths.Add(path))
            {
                _activeNodes.Remove(directory);
                throw new InvalidDataException(
                    $"ファイルシステムのディレクトリ構造に循環参照があります: {directory.VirtualPath}");
            }

            return new DirectoryScope(this, directory, path);
        }

        private void ExitDirectory(VfsNode directory, string path)
        {
            _activeNodes.Remove(directory);
            if (!string.IsNullOrEmpty(path))
            {
                _activePaths.Remove(path);
            }
        }

        private sealed class DirectoryScope(
            TraversalState owner,
            VfsNode directory,
            string path) : IDisposable
        {
            public void Dispose() => owner.ExitDirectory(directory, path);
        }
    }

    private sealed class TraversalLimitException(string message) : NotSupportedException(message);
}
