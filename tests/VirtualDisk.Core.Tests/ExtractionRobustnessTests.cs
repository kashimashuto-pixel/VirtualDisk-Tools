using Qcow2Explorer.FileSystems;
using Qcow2Explorer.Partitions;

internal static class ExtractionRobustnessTests
{
    private const int ChunkSize = 1024 * 1024;

    public static void Run(string directory)
    {
        TestAtomicReplacement(directory);
        TestUnexpectedEofPreservesDestination(directory);
        TestCancellationPreservesDestination(directory);
        TestExistingDestinationRequiresOverwrite(directory);
    }

    private static void TestAtomicReplacement(string directory)
    {
        var destination = Path.Combine(directory, "extract-success.bin");
        File.WriteAllText(destination, "old destination");
        var fileSystem = new SyntheticFileSystem(ChunkSize + 17);
        CopyProgress? latest = null;
        FileSystemExporter.ExtractFileAsync(
            fileSystem,
            fileSystem.File,
            destination,
            overwrite: true,
            progress: new CallbackProgress<CopyProgress>(update => latest = update))
            .GetAwaiter()
            .GetResult();

        var actual = File.ReadAllBytes(destination);
        Assert(actual.Length == fileSystem.File.Size, "atomic extraction length");
        Assert(actual.Select((value, index) => value == SyntheticFileSystem.ValueAt(index)).All(value => value),
            "atomic extraction content");
        Assert(latest is { FilesCopied: 1 } && latest.BytesCopied == actual.Length,
            "atomic extraction completion progress");
        AssertNoPartialFiles(directory, "successful extraction");
    }

    private static void TestUnexpectedEofPreservesDestination(string directory)
    {
        var destination = Path.Combine(directory, "extract-eof.bin");
        var original = "destination must survive EOF"u8.ToArray();
        File.WriteAllBytes(destination, original);
        var fileSystem = new SyntheticFileSystem(ChunkSize + 17, failAfterOffset: ChunkSize);
        var exception = AssertThrows<EndOfStreamException>(() => FileSystemExporter.ExtractFileAsync(
            fileSystem,
            fileSystem.File,
            destination,
            overwrite: true));

        Assert(exception.Message.Contains("actual=16", StringComparison.Ordinal), "short extraction diagnostic");
        Assert(File.ReadAllBytes(destination).SequenceEqual(original), "EOF preserves existing destination");
        AssertNoPartialFiles(directory, "failed extraction");
    }

    private static void TestCancellationPreservesDestination(string directory)
    {
        var destination = Path.Combine(directory, "extract-cancel.bin");
        var original = "destination must survive cancellation"u8.ToArray();
        File.WriteAllBytes(destination, original);
        var fileSystem = new SyntheticFileSystem(ChunkSize * 2L);
        using var cancellationSource = new CancellationTokenSource();
        AssertThrows<OperationCanceledException>(() => FileSystemExporter.ExtractFileAsync(
            fileSystem,
            fileSystem.File,
            destination,
            overwrite: true,
            progress: new CallbackProgress<CopyProgress>(_ => cancellationSource.Cancel()),
            cancellationToken: cancellationSource.Token));

        Assert(File.ReadAllBytes(destination).SequenceEqual(original), "cancellation preserves existing destination");
        AssertNoPartialFiles(directory, "cancelled extraction");
    }

    private static void TestExistingDestinationRequiresOverwrite(string directory)
    {
        var destination = Path.Combine(directory, "extract-existing.bin");
        var original = "existing"u8.ToArray();
        File.WriteAllBytes(destination, original);
        var fileSystem = new SyntheticFileSystem(1);
        _ = AssertThrows<IOException>(() => FileSystemExporter.ExtractFileAsync(
            fileSystem,
            fileSystem.File,
            destination));
        Assert(File.ReadAllBytes(destination).SequenceEqual(original), "non-overwrite extraction preserves destination");
        AssertNoPartialFiles(directory, "rejected extraction");
    }

    private static TException AssertThrows<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            action().GetAwaiter().GetResult();
        }
        catch (TException exception)
        {
            return exception;
        }

        throw new InvalidOperationException($"Assertion failed: expected {typeof(TException).Name}");
    }

    private static void AssertNoPartialFiles(string directory, string description)
    {
        Assert(
            !Directory.EnumerateFiles(directory, "*.vdt-partial", SearchOption.TopDirectoryOnly).Any(),
            $"{description} removes temporary file");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Assertion failed: {message}");
        }
    }

    private sealed class SyntheticFileSystem(long size, long failAfterOffset = long.MaxValue) : IReadOnlyFileSystem
    {
        public string Name => "Synthetic extraction filesystem";
        public PartitionInfo Partition { get; } = new()
        {
            Number = 1,
            Scheme = "Test",
            Name = "Test",
            SectorCount = 1
        };
        public VfsNode Root { get; } = new() { Name = "", VirtualPath = "/", IsDirectory = true };
        public VfsNode File { get; } = new()
        {
            Name = "payload.bin",
            VirtualPath = "/payload.bin",
            Size = size,
            Metadata = "synthetic=1"
        };

        public IReadOnlyList<VfsNode> ListDirectory(VfsNode directory) => [File];

        public byte[] ReadFile(VfsNode file, long offset, int count)
        {
            if (!ReferenceEquals(file, File) || offset < 0 || count < 0 || offset > File.Size)
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }

            var available = checked((int)Math.Min(count, File.Size - offset));
            if (offset >= failAfterOffset && available > 0)
            {
                available--;
            }

            var result = new byte[available];
            for (var index = 0; index < result.Length; index++)
            {
                result[index] = ValueAt(offset + index);
            }

            return result;
        }

        public static byte ValueAt(long offset) => checked((byte)((offset * 31 + 7) & 0xff));
    }

    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
