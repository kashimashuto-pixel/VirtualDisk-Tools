using Qcow2Explorer.Core;

internal static class BlockStreamRobustnessTests
{
    public static void Run()
    {
        TestReadArgumentsValidatedAtEndOfStream();
        TestSeekOverflowRejected();
        TestInvalidAdaptersRejected();
    }

    private static void TestReadArgumentsValidatedAtEndOfStream()
    {
        using var stream = new BlockReaderStream(new ArrayBlockDevice([1, 2, 3, 4]));
        stream.Position = stream.Length;
        AssertThrows<ArgumentNullException>(
            () => _ = stream.Read(null!, 0, 0),
            "null read buffer rejected at EOF");
        AssertThrows<ArgumentOutOfRangeException>(
            () => _ = stream.Read(Array.Empty<byte>(), 0, -1),
            "negative read length rejected at EOF");
        AssertThrows<ArgumentException>(
            () => _ = stream.Read(new byte[1], 1, 1),
            "out-of-range read buffer rejected at EOF");
    }

    private static void TestSeekOverflowRejected()
    {
        using var stream = new BlockReaderStream(new ArrayBlockDevice([1]));
        stream.Position = long.MaxValue;
        AssertThrows<IOException>(
            () => stream.Seek(1, SeekOrigin.Current),
            "overflowing seek rejected");
    }

    private static void TestInvalidAdaptersRejected()
    {
        AssertThrows<ArgumentNullException>(
            () => _ = new BlockReaderStream(null!),
            "null block reader rejected");
        AssertThrows<ArgumentException>(
            () => _ = new BlockReaderStream(new NegativeLengthReader()),
            "negative block reader length rejected");
        using var source = new NonSeekableStream();
        AssertThrows<ArgumentException>(
            () => _ = new StreamBlockReader(source),
            "non-seekable source stream rejected");
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

    private sealed class ArrayBlockDevice(byte[] bytes) : IBlockDevice
    {
        public long Length => bytes.Length;

        public void ReadAt(long offset, byte[] buffer, int bufferOffset, int count) =>
            Array.Copy(bytes, offset, buffer, bufferOffset, count);

        public void WriteAt(long offset, byte[] buffer, int bufferOffset, int count) =>
            Array.Copy(buffer, bufferOffset, bytes, offset, count);

        public void Flush()
        {
        }
    }

    private sealed class NegativeLengthReader : IBlockReader
    {
        public long Length => -1;

        public void ReadAt(long offset, byte[] buffer, int bufferOffset, int count) =>
            throw new NotSupportedException();
    }

    private sealed class NonSeekableStream : MemoryStream
    {
        public override bool CanSeek => false;
    }
}
