namespace Qcow2Explorer.FileSystems;

public sealed record SearchMatch(VfsNode Node, string Path);

public static class FileSystemSearch
{
    private const int MaximumSupportedResults = 1_000_000;
    private const int MaximumEntriesPerDirectory = 1_000_000;

    public static IReadOnlyList<SearchMatch> Search(
        IReadOnlyFileSystem fileSystem,
        string query,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default,
        int maximumResults = 5000,
        int maximumDirectories = 100_000)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (maximumResults is <= 0 or > MaximumSupportedResults)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumResults));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(maximumDirectories, 1);
        var results = new List<SearchMatch>();
        var pending = new Stack<(VfsNode Directory, string Path)>();
        pending.Push((fileSystem.Root, "/"));
        var visited = 0;

        while (pending.Count > 0 && results.Count < maximumResults)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (visited >= maximumDirectories)
            {
                throw new InvalidDataException(
                    $"検索対象directory数が対応上限 ({maximumDirectories:N0}) を超えています。");
            }

            var (directory, path) = pending.Pop();
            IReadOnlyList<VfsNode> children;
            try
            {
                children = fileSystem.ListDirectory(directory);
            }
            catch (Exception ex) when (IsRecoverableDirectoryError(ex))
            {
                continue;
            }

            if (children.Count > MaximumEntriesPerDirectory)
            {
                throw new InvalidDataException(
                    $"directory内entry数が検索上限 ({MaximumEntriesPerDirectory:N0}) を超えています: {path}");
            }

            foreach (var child in children)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var childPath = path == "/" ? $"/{child.DisplayName}" : $"{path}/{child.DisplayName}";
                if (child.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(new SearchMatch(child, childPath));
                    if (results.Count >= maximumResults)
                    {
                        break;
                    }
                }

                if (child.IsDirectory)
                {
                    pending.Push((child, childPath));
                }
            }

            visited++;
            progress?.Report(visited);
        }

        return results;
    }

    private static bool IsRecoverableDirectoryError(Exception exception) =>
        exception is IOException
            or InvalidDataException
            or NotSupportedException
            or ArgumentException
            or OverflowException;
}
