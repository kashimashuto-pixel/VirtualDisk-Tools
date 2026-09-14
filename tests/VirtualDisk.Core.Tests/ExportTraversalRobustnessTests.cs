using Qcow2Explorer.FileSystems;
using Qcow2Explorer.Partitions;

internal static class ExportTraversalRobustnessTests
{
    public static void Run(string directory)
    {
        TestDirectoryCycleIsReported(directory);
        TestDepthLimit(directory);
        TestEntryLimit(directory);
        TestTopLevelEnumerationLimit(directory);
        TestErrorLimit(directory);
    }

    private static void TestDirectoryCycleIsReported(string directory)
    {
        var fileSystem = new CyclicFileSystem();
        var result = FileSystemExporter.CopyNode(
            fileSystem,
            fileSystem.Root,
            Path.Combine(directory, "cyclic-export"));
        Assert(result.DirectoriesCreated == 1, "cyclic export creates root once");
        Assert(
            result.Errors.Count == 1
            && result.Errors[0].Message.Contains("循環参照", StringComparison.Ordinal),
            "cyclic export reports one bounded error");
    }

    private static void TestDepthLimit(string directory)
    {
        var fileSystem = new DeepFileSystem(maximumLevel: 16);
        var exception = AssertThrows<NotSupportedException>(() => FileSystemExporter.CopyNodes(
            fileSystem,
            [fileSystem.Root],
            Path.Combine(directory, "deep-export"),
            options: new CopyOptions(MaximumDepth: 4)));
        Assert(exception.Message.Contains("深度", StringComparison.Ordinal), "export depth-limit diagnostic");
    }

    private static void TestEntryLimit(string directory)
    {
        var fileSystem = new WideFileSystem(entryCount: 16);
        var exception = AssertThrows<NotSupportedException>(() => FileSystemExporter.CopyNodes(
            fileSystem,
            [fileSystem.Root],
            Path.Combine(directory, "wide-export"),
            options: new CopyOptions(MaximumEntries: 5)));
        Assert(exception.Message.Contains("項目数", StringComparison.Ordinal), "export entry-limit diagnostic");
    }

    private static void TestTopLevelEnumerationLimit(string directory)
    {
        var fileSystem = new WideFileSystem(entryCount: 0);
        var exception = AssertThrows<NotSupportedException>(() => FileSystemExporter.CopyNodes(
            fileSystem,
            InfiniteFiles(),
            Path.Combine(directory, "infinite-export"),
            options: new CopyOptions(MaximumEntries: 3)));
        Assert(exception.Message.Contains("対象数", StringComparison.Ordinal), "top-level enumeration-limit diagnostic");
    }

    private static void TestErrorLimit(string directory)
    {
        var fileSystem = new InvalidFileSystem(entryCount: 16);
        var exception = AssertThrows<NotSupportedException>(() => FileSystemExporter.CopyNodes(
            fileSystem,
            [fileSystem.Root],
            Path.Combine(directory, "error-limit-export"),
            options: new CopyOptions(MaximumEntries: 100, MaximumErrors: 3)));
        Assert(exception.Message.Contains("エラー数", StringComparison.Ordinal), "export error-limit diagnostic");
    }

    private static IEnumerable<VfsNode> InfiniteFiles()
    {
        for (var index = 0; ; index++)
        {
            yield return FileNode($"file-{index}");
        }
    }

    private static TException AssertThrows<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException exception)
        {
            return exception;
        }

        throw new InvalidOperationException($"Assertion failed: expected {typeof(TException).Name}");
    }

    private static VfsNode FileNode(string name) => new()
    {
        Name = name,
        VirtualPath = $"/{name}",
        Size = 0
    };

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Assertion failed: {message}");
        }
    }

    private abstract class TestFileSystem : IReadOnlyFileSystem
    {
        public abstract VfsNode Root { get; }
        public string Name => "Traversal test filesystem";
        public PartitionInfo Partition { get; } = new()
        {
            Number = 1,
            Scheme = "Test",
            Name = "Test",
            SectorCount = 1
        };

        public abstract IReadOnlyList<VfsNode> ListDirectory(VfsNode directory);
        public byte[] ReadFile(VfsNode file, long offset, int count) => [];
    }

    private sealed class CyclicFileSystem : TestFileSystem
    {
        public override VfsNode Root { get; } = new()
        {
            Name = "root",
            VirtualPath = "/",
            IsDirectory = true
        };

        public override IReadOnlyList<VfsNode> ListDirectory(VfsNode directory) => [Root];
    }

    private sealed class DeepFileSystem(int maximumLevel) : TestFileSystem
    {
        public override VfsNode Root { get; } = DirectoryNode(0);

        public override IReadOnlyList<VfsNode> ListDirectory(VfsNode directory)
        {
            var level = (int)(directory.Metadata ?? throw new InvalidDataException("missing level"));
            return level >= maximumLevel ? [] : [DirectoryNode(level + 1)];
        }

        private static VfsNode DirectoryNode(int level) => new()
        {
            Name = $"level-{level}",
            VirtualPath = $"/level-{level}",
            IsDirectory = true,
            Metadata = level
        };
    }

    private sealed class WideFileSystem : TestFileSystem
    {
        private readonly IReadOnlyList<VfsNode> _entries;

        public WideFileSystem(int entryCount)
        {
            _entries = Enumerable.Range(0, entryCount).Select(index => FileNode($"wide-{index}")).ToArray();
        }

        public override VfsNode Root { get; } = new()
        {
            Name = "root",
            VirtualPath = "/",
            IsDirectory = true
        };

        public override IReadOnlyList<VfsNode> ListDirectory(VfsNode directory) => _entries;
    }

    private sealed class InvalidFileSystem : TestFileSystem
    {
        private readonly IReadOnlyList<VfsNode> _entries;

        public InvalidFileSystem(int entryCount)
        {
            _entries = Enumerable.Range(0, entryCount).Select(index => new VfsNode
            {
                Name = $"invalid-{index}",
                VirtualPath = $"/invalid-{index}",
                Size = -1
            }).ToArray();
        }

        public override VfsNode Root { get; } = new()
        {
            Name = "root",
            VirtualPath = "/",
            IsDirectory = true
        };

        public override IReadOnlyList<VfsNode> ListDirectory(VfsNode directory) => _entries;
    }
}
