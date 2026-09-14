using System.Security.Cryptography;

namespace Qcow2Explorer.FileSystems;

public sealed record FileSystemVerificationOptions(
    int MaximumDepth = 256,
    int MaximumEntries = 1_000_000,
    int MaximumIssues = 10_000);

public sealed record FileSystemVerificationProgress(
    string CurrentPath,
    int EntriesChecked,
    int FilesChecked,
    int DirectoriesChecked,
    long BytesRead);

public sealed record FileSystemVerificationIssue(
    string Path,
    string ErrorType,
    string Message);

public sealed record FileSystemVerificationResult(
    bool Completed,
    int EntriesChecked,
    int FilesChecked,
    int DirectoriesChecked,
    long BytesRead,
    IReadOnlyList<FileSystemVerificationIssue> Issues)
{
    public bool IsValid => Completed && Issues.Count == 0;
}

public static class FileSystemVerifier
{
    private const int ReadBufferSize = 1024 * 1024;

    public static FileSystemVerificationResult Verify(
        IReadOnlyFileSystem fileSystem,
        VfsNode start,
        IProgress<FileSystemVerificationProgress>? progress = null,
        CancellationToken cancellationToken = default,
        FileSystemVerificationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(start);
        options ??= new FileSystemVerificationOptions();
        if (options.MaximumDepth < 1
            || options.MaximumEntries < 1
            || options.MaximumIssues < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "検査の最大深度、項目数、問題数には正の整数を指定してください。");
        }

        var pending = new Stack<(VfsNode Node, int Depth)>();
        pending.Push((start, 0));
        var visitedDirectories = new HashSet<VfsNode>(ReferenceEqualityComparer.Instance);
        var visitedDirectoryPaths = new HashSet<string>(StringComparer.Ordinal);
        var issues = new List<FileSystemVerificationIssue>();
        var completed = true;
        var entriesChecked = 0;
        var filesChecked = 0;
        var directoriesChecked = 0;
        long bytesRead = 0;

        while (pending.TryPop(out var item))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entriesChecked >= options.MaximumEntries)
            {
                AddLimitIssue(
                    "(filesystem)",
                    $"検査項目数が上限 ({options.MaximumEntries:N0}) に達しました。");
                completed = false;
                break;
            }

            entriesChecked++;
            var node = item.Node;
            var path = DisplayPath(node);
            if (item.Depth > options.MaximumDepth)
            {
                AddLimitIssue(path, $"ディレクトリ深度が上限 ({options.MaximumDepth:N0}) を超えました。");
                completed = false;
                continue;
            }

            if (node.IsDirectory)
            {
                directoriesChecked++;
                if (!visitedDirectories.Add(node)
                    || (!string.IsNullOrEmpty(node.VirtualPath)
                        && !visitedDirectoryPaths.Add(node.VirtualPath)))
                {
                    if (!AddIssue(node, new InvalidDataException("ディレクトリ構造に重複または循環参照があります。")))
                    {
                        completed = false;
                        break;
                    }

                    Report(path);
                    continue;
                }

                try
                {
                    var children = fileSystem.ListDirectory(node)
                        ?? throw new InvalidDataException("ファイルシステムがnullの一覧を返しました。");
                    for (var index = children.Count - 1; index >= 0; index--)
                    {
                        var child = children[index]
                            ?? throw new InvalidDataException("ファイルシステムがnullノードを返しました。");
                        pending.Push((child, checked(item.Depth + 1)));
                    }
                }
                catch (Exception exception) when (IsRecoverable(exception))
                {
                    if (!AddIssue(node, exception))
                    {
                        completed = false;
                        break;
                    }
                }

                Report(path);
                continue;
            }

            filesChecked++;
            try
            {
                if (node.Size < 0)
                {
                    throw new InvalidDataException($"ファイルサイズが不正です: {node.Size:N0} bytes");
                }

                long offset = 0;
                while (offset < node.Size)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var count = checked((int)Math.Min(ReadBufferSize, node.Size - offset));
                    var data = fileSystem.ReadFile(node, offset, count)
                        ?? throw new InvalidDataException("ファイルシステムがnullデータを返しました。");
                    if (data.Length != count)
                    {
                        throw new EndOfStreamException(
                            $"ファイルが途中で終了しました: size={node.Size:N0}, "
                            + $"offset={offset:N0}, requested={count:N0}, actual={data.Length:N0}");
                    }

                    offset += data.Length;
                    bytesRead = AddSaturating(bytesRead, data.Length);
                    Report(path);
                }
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                if (!AddIssue(node, exception))
                {
                    completed = false;
                    break;
                }
            }

            Report(path);
        }

        return new FileSystemVerificationResult(
            completed,
            entriesChecked,
            filesChecked,
            directoriesChecked,
            bytesRead,
            issues);

        bool AddIssue(VfsNode node, Exception exception)
        {
            if (issues.Count >= options.MaximumIssues)
            {
                return false;
            }

            issues.Add(new FileSystemVerificationIssue(
                DisplayPath(node),
                exception.GetType().Name,
                exception.Message));
            return issues.Count < options.MaximumIssues;
        }

        void AddLimitIssue(string issuePath, string message)
        {
            if (issues.Count < options.MaximumIssues)
            {
                issues.Add(new FileSystemVerificationIssue(
                    issuePath,
                    nameof(NotSupportedException),
                    message));
            }
        }

        void Report(string currentPath) => progress?.Report(new FileSystemVerificationProgress(
            currentPath,
            entriesChecked,
            filesChecked,
            directoriesChecked,
            bytesRead));
    }

    private static bool IsRecoverable(Exception exception) =>
        exception is IOException
            or InvalidDataException
            or NotSupportedException
            or UnauthorizedAccessException
            or ArgumentException
            or OverflowException
            or CryptographicException;

    private static string DisplayPath(VfsNode node) =>
        string.IsNullOrWhiteSpace(node.VirtualPath) ? node.DisplayName : node.VirtualPath;

    private static long AddSaturating(long left, int right) =>
        right > long.MaxValue - left ? long.MaxValue : left + right;
}
