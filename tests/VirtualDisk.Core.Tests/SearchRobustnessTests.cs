using Qcow2Explorer.FileSystems;
using Qcow2Explorer.Partitions;

internal static class SearchRobustnessTests
{
    public static void Run()
    {
        AssertThrows<OperationCanceledException>(
            () => FileSystemSearch.Search(new ThrowingFileSystem(new OperationCanceledException()), "item"),
            "search propagates filesystem cancellation");
        AssertThrows<InvalidOperationException>(
            () => FileSystemSearch.Search(new ThrowingFileSystem(new InvalidOperationException("fatal")), "item"),
            "search propagates unexpected failures");

        var recoverable = FileSystemSearch.Search(
            new ThrowingFileSystem(new InvalidDataException("damaged directory")),
            "item");
        Assert(recoverable.Count == 0, "search skips a damaged directory");

        var cyclic = new CyclicFileSystem();
        AssertThrows<InvalidDataException>(
            () => FileSystemSearch.Search(cyclic, "never-matches"),
            "search rejects cyclic directory traversal");
        Assert(cyclic.ListDirectoryCalls == 1, "search detects a direct cycle before reading it again");
        AssertThrows<InvalidDataException>(
            () => FileSystemSearch.Search(new NullListFileSystem(), "item"),
            "search rejects null directory listings");
        AssertThrows<InvalidDataException>(
            () => FileSystemSearch.Search(new NullNodeFileSystem(), "item"),
            "search rejects null directory entries");
        AssertThrows<ArgumentOutOfRangeException>(
            () => FileSystemSearch.Search(new CyclicFileSystem(), "item", maximumResults: 0),
            "search validates result limit");
    }

    private static void AssertThrows<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Assertion failed: {message}");
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
        public string Name => "Search test filesystem";
        public PartitionInfo Partition { get; } = new()
        {
            Number = 1,
            Scheme = "Test",
            Name = "Test",
            SectorCount = 1,
        };
        public VfsNode Root { get; } = new()
        {
            Name = "",
            VirtualPath = "/",
            IsDirectory = true,
        };

        public abstract IReadOnlyList<VfsNode> ListDirectory(VfsNode directory);
        public byte[] ReadFile(VfsNode file, long offset, int count) => [];
    }

    private sealed class ThrowingFileSystem(Exception exception) : TestFileSystem
    {
        public override IReadOnlyList<VfsNode> ListDirectory(VfsNode directory) => throw exception;
    }

    private sealed class CyclicFileSystem : TestFileSystem
    {
        public int ListDirectoryCalls { get; private set; }

        public override IReadOnlyList<VfsNode> ListDirectory(VfsNode directory)
        {
            ListDirectoryCalls++;
            return [Root];
        }
    }

    private sealed class NullListFileSystem : TestFileSystem
    {
        public override IReadOnlyList<VfsNode> ListDirectory(VfsNode directory) => null!;
    }

    private sealed class NullNodeFileSystem : TestFileSystem
    {
        public override IReadOnlyList<VfsNode> ListDirectory(VfsNode directory) => [null!];
    }
}
