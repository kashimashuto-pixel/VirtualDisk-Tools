using Qcow2Explorer.FileSystems;
using Qcow2Explorer.Partitions;

internal static class VerificationRobustnessTests
{
    private const int ChunkSize = 1024 * 1024;

    public static void Run()
    {
        TestContentAndErrorAggregation();
        TestDirectoryCycle();
        TestDepthLimit();
        TestCancellation();
    }

    private static void TestContentAndErrorAggregation()
    {
        var fileSystem = new ContentFileSystem();
        var result = FileSystemVerifier.Verify(fileSystem, fileSystem.Root);
        Assert(result.Completed, "verification with file error completes traversal");
        Assert(!result.IsValid, "verification with file error fails");
        Assert(result.EntriesChecked == 3, "verification entry count");
        Assert(result.FilesChecked == 2 && result.DirectoriesChecked == 1, "verification node counts");
        Assert(result.BytesRead == ChunkSize + 17, "verification counts fully read bytes only");
        Assert(
            result.Issues.Count == 1
            && result.Issues[0].Path == "/bad.bin"
            && result.Issues[0].Message.Contains("actual=16", StringComparison.Ordinal),
            "verification aggregates short-read diagnostic");
    }

    private static void TestDirectoryCycle()
    {
        var fileSystem = new CyclicFileSystem();
        var result = FileSystemVerifier.Verify(fileSystem, fileSystem.Root);
        Assert(result.Completed, "cyclic verification safely completes");
        Assert(
            result.Issues.Count == 1
            && result.Issues[0].Message.Contains("循環参照", StringComparison.Ordinal),
            "cyclic verification diagnostic");
    }

    private static void TestDepthLimit()
    {
        var fileSystem = new DeepFileSystem(maximumLevel: 16);
        var result = FileSystemVerifier.Verify(
            fileSystem,
            fileSystem.Root,
            options: new FileSystemVerificationOptions(MaximumDepth: 4));
        Assert(!result.Completed && !result.IsValid, "depth-limited verification is incomplete");
        Assert(result.Issues.Any(issue => issue.Message.Contains("深度", StringComparison.Ordinal)),
            "verification depth-limit diagnostic");
    }

    private static void TestCancellation()
    {
        var fileSystem = new LargeFileSystem();
        using var cancellationSource = new CancellationTokenSource();
        AssertThrows<OperationCanceledException>(() => FileSystemVerifier.Verify(
            fileSystem,
            fileSystem.File,
            new CallbackProgress<FileSystemVerificationProgress>(_ => cancellationSource.Cancel()),
            cancellationSource.Token));
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
        public string Name => "Verification test filesystem";
        public PartitionInfo Partition { get; } = new()
        {
            Number = 1,
            Scheme = "Test",
            Name = "Test",
            SectorCount = 1
        };

        public abstract IReadOnlyList<VfsNode> ListDirectory(VfsNode directory);
        public abstract byte[] ReadFile(VfsNode file, long offset, int count);
    }

    private sealed class ContentFileSystem : TestFileSystem
    {
        private readonly VfsNode _good = FileNode("good.bin", ChunkSize + 17L);
        private readonly VfsNode _bad = FileNode("bad.bin", 17);

        public override VfsNode Root { get; } = DirectoryNode("root", "/");

        public override IReadOnlyList<VfsNode> ListDirectory(VfsNode directory) => [_good, _bad];

        public override byte[] ReadFile(VfsNode file, long offset, int count)
        {
            var actual = ReferenceEquals(file, _bad) ? Math.Max(0, count - 1) : count;
            return new byte[actual];
        }
    }

    private sealed class CyclicFileSystem : TestFileSystem
    {
        public override VfsNode Root { get; } = DirectoryNode("root", "/");
        public override IReadOnlyList<VfsNode> ListDirectory(VfsNode directory) => [Root];
        public override byte[] ReadFile(VfsNode file, long offset, int count) => [];
    }

    private sealed class DeepFileSystem(int maximumLevel) : TestFileSystem
    {
        public override VfsNode Root { get; } = CreateDirectory(0);

        public override IReadOnlyList<VfsNode> ListDirectory(VfsNode directory)
        {
            var level = (int)(directory.Metadata ?? throw new InvalidDataException("missing level"));
            return level >= maximumLevel ? [] : [CreateDirectory(level + 1)];
        }

        public override byte[] ReadFile(VfsNode file, long offset, int count) => [];

        private static VfsNode CreateDirectory(int level) => new()
        {
            Name = $"level-{level}",
            VirtualPath = $"/level-{level}",
            IsDirectory = true,
            Metadata = level
        };
    }

    private sealed class LargeFileSystem : TestFileSystem
    {
        public VfsNode File { get; } = FileNode("large.bin", ChunkSize * 2L);
        public override VfsNode Root { get; } = DirectoryNode("root", "/");
        public override IReadOnlyList<VfsNode> ListDirectory(VfsNode directory) => [File];
        public override byte[] ReadFile(VfsNode file, long offset, int count) => new byte[count];
    }

    private static VfsNode DirectoryNode(string name, string path) => new()
    {
        Name = name,
        VirtualPath = path,
        IsDirectory = true
    };

    private static VfsNode FileNode(string name, long size) => new()
    {
        Name = name,
        VirtualPath = $"/{name}",
        Size = size
    };

    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
