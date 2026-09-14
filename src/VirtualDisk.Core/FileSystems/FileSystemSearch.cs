namespace Qcow2Explorer.FileSystems;

public sealed record SearchMatch(VfsNode Node, string Path);

public static class FileSystemSearch
{
    public static IReadOnlyList<SearchMatch> Search(
        IReadOnlyFileSystem fileSystem,
        string query,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default,
        int maximumResults = 5000)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var results = new List<SearchMatch>();
        var pending = new Stack<(VfsNode Directory, string Path)>();
        pending.Push((fileSystem.Root, "/"));
        var visited = 0;

        while (pending.Count > 0 && results.Count < maximumResults)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (directory, path) = pending.Pop();
            IReadOnlyList<VfsNode> children;
            try
            {
                children = fileSystem.ListDirectory(directory);
            }
            catch
            {
                continue;
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

            progress?.Report(++visited);
        }

        return results;
    }
}
