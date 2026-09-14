using Qcow2Explorer.Creation;
using Qcow2Explorer.Core;

internal static class CreationRobustnessTests
{
    public static void Run(string directory)
    {
        TestFormatterFailureCleanup(directory);
        TestCancellationCleanup(directory);
        TestWrongFormatterSizeCleanup(directory);
        TestExistingDestinationPreserved(directory);
        TestFinalVerificationCancellationCleanup(directory);
    }

    private static void TestFormatterFailureCleanup(string directory)
    {
        var destination = Path.Combine(directory, "formatter-failure.raw");
        var formatter = new TestFormatter((_, _, _, _) =>
            throw new InvalidOperationException("Injected formatter failure."));

        AssertThrows<InvalidOperationException>(
            () => Create(destination, formatter),
            "formatter failure propagated");
        AssertCreationArtifactsAbsent(destination, "formatter failure cleanup");
    }

    private static void TestCancellationCleanup(string directory)
    {
        var destination = Path.Combine(directory, "formatter-cancel.raw");
        using var source = new CancellationTokenSource();
        var formatter = new TestFormatter((_, _, _, token) =>
        {
            source.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        });

        AssertThrows<OperationCanceledException>(
            () => Create(destination, formatter, source.Token),
            "formatter cancellation propagated");
        AssertCreationArtifactsAbsent(destination, "formatter cancellation cleanup");
    }

    private static void TestWrongFormatterSizeCleanup(string directory)
    {
        var destination = Path.Combine(directory, "formatter-wrong-size.raw");
        var formatter = new TestFormatter((path, layout, _, _) =>
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            stream.SetLength(layout.SizeBytes - 512);
            return Task.CompletedTask;
        });

        AssertThrows<InvalidDataException>(
            () => Create(destination, formatter),
            "wrong formatter size rejected");
        AssertCreationArtifactsAbsent(destination, "wrong formatter size cleanup");
    }

    private static void TestExistingDestinationPreserved(string directory)
    {
        var destination = Path.Combine(directory, "existing-destination.raw");
        var sentinel = new byte[] { 0x56, 0x44, 0x54, 0x21 };
        File.WriteAllBytes(destination, sentinel);
        var formatter = new TestFormatter((_, _, _, _) => Task.CompletedTask);

        AssertThrows<IOException>(
            () => Create(destination, formatter),
            "existing destination rejected");
        Assert(File.ReadAllBytes(destination).SequenceEqual(sentinel), "existing destination preserved");
        AssertNoTemporaryArtifacts(destination, "existing destination temporary cleanup");
    }

    private static void TestFinalVerificationCancellationCleanup(string directory)
    {
        var destination = Path.Combine(directory, "final-verification-cancel.raw");
        using var cancellationSource = new CancellationTokenSource();
        var progress = new CallbackProgress<DiskImageProgress>(update =>
        {
            if (update.Message.StartsWith("最終イメージの整合性を検証", StringComparison.Ordinal))
            {
                cancellationSource.Cancel();
            }
        });
        var request = new VirtualDiskCreationRequest(
            destination,
            512L * 1024 * 1024,
            VirtualDiskContainerFormat.Raw,
            VirtualDiskPartitionTableKind.Gpt,
            [
                new VirtualDiskPartitionDefinition(
                    64L * 1024 * 1024,
                    "Final verification cancellation",
                    "VDT_CANCEL",
                    VirtualDiskFileSystemKind.Ext4),
            ]);

        AssertThrows<OperationCanceledException>(
            () => VirtualDiskCreationService.CreateAsync(
                request,
                progress,
                cancellationSource.Token).GetAwaiter().GetResult(),
            "final verification cancellation propagated");
        AssertCreationArtifactsAbsent(destination, "final verification cancellation cleanup");
    }

    private static void Create(
        string destination,
        IVirtualDiskFileSystemFormatter formatter,
        CancellationToken cancellationToken = default)
    {
        var request = new VirtualDiskCreationRequest(
            destination,
            512L * 1024 * 1024,
            VirtualDiskContainerFormat.Raw,
            VirtualDiskPartitionTableKind.Gpt,
            [
                new VirtualDiskPartitionDefinition(
                    64L * 1024 * 1024,
                    "Failure injection",
                    "VDT_FAIL",
                    VirtualDiskFileSystemKind.Ext4),
            ]);
        var registry = new VirtualDiskFormatterRegistry([formatter]);
        VirtualDiskCreationService.CreateAsync(request, registry, cancellationToken: cancellationToken)
            .GetAwaiter()
            .GetResult();
    }

    private static void AssertCreationArtifactsAbsent(string destination, string message)
    {
        Assert(!File.Exists(destination), $"{message}: destination absent");
        AssertNoTemporaryArtifacts(destination, message);
    }

    private static void AssertNoTemporaryArtifacts(string destination, string message)
    {
        var directory = Path.GetDirectoryName(destination)!;
        var prefix = $".{Path.GetFileName(destination)}.";
        var leftovers = Directory.EnumerateFiles(directory)
            .Where(path => Path.GetFileName(path).StartsWith(prefix, StringComparison.Ordinal))
            .ToArray();
        Assert(leftovers.Length == 0, $"{message}: temporary artifacts absent");
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

    private sealed class TestFormatter(
        Func<string, VirtualDiskPartitionLayout, IReadOnlyList<VirtualDiskInitialFile>, CancellationToken, Task> format)
        : IVirtualDiskFileSystemFormatter
    {
        public string Name => "Failure-injection formatter";

        public bool Supports(VirtualDiskFileSystemKind fileSystem) =>
            fileSystem == VirtualDiskFileSystemKind.Ext4;

        public ValueTask VerifyAvailableAsync(
            VirtualDiskFileSystemKind fileSystem,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public Task FormatAsync(
            string imagePath,
            VirtualDiskPartitionLayout layout,
            IReadOnlyList<VirtualDiskInitialFile> initialFiles,
            CancellationToken cancellationToken = default) =>
            format(imagePath, layout, initialFiles, cancellationToken);
    }

    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
