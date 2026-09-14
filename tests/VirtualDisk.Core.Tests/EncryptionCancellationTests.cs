using Qcow2Explorer.Core;
using Qcow2Explorer.FileSystems;
using Qcow2Explorer.Partitions;

internal static class EncryptionCancellationTests
{
    public static void Run()
    {
        var reader = new UnexpectedReadBlockReader();
        var partition = new PartitionInfo
        {
            Number = 1,
            Scheme = "test",
            SectorCount = 8,
        };
        var cancellationToken = new CancellationToken(canceled: true);

        AssertCanceled(
            () => FileSystemDetector.TryOpen(reader, partition, new byte[16], out _, cancellationToken),
            "BitLocker recovery-key open forwards cancellation");
        AssertCanceled(
            () => FileSystemDetector.TryOpenWithBitLockerPassword(
                reader,
                partition,
                "password".AsSpan(),
                out _,
                cancellationToken),
            "BitLocker password open forwards cancellation");
        AssertCanceled(
            () => FileSystemDetector.TryOpenWithLuksPassphrase(
                reader,
                partition,
                "password".AsSpan(),
                out _,
                cancellationToken),
            "LUKS passphrase open forwards cancellation");
    }

    private static void AssertCanceled(Action action, string message)
    {
        try
        {
            action();
        }
        catch (OperationCanceledException)
        {
            return;
        }

        throw new InvalidOperationException($"Assertion failed: {message}");
    }

    private sealed class UnexpectedReadBlockReader : IBlockReader
    {
        public long Length => 4096;

        public void ReadAt(long offset, byte[] buffer, int bufferOffset, int count) =>
            throw new InvalidOperationException("Cancellation was not observed before disk access.");
    }
}
