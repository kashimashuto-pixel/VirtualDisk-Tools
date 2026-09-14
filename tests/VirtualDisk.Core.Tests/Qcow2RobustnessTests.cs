using System.Buffers.Binary;
using System.Diagnostics;
using Qcow2Explorer.Core;

internal static class Qcow2RobustnessTests
{
    public static void Run(string directory)
    {
        TestMinimalImage(directory);
        TestTruncatedHeaders(directory);
        TestUntrustedHeaderLimits(directory);
        TestDeterministicHeaderMutations(directory);
    }

    private static void TestMinimalImage(string directory)
    {
        var path = Path.Combine(directory, "minimal-valid.qcow2");
        File.WriteAllBytes(path, CreateMinimalImage());
        using var reader = new Qcow2Reader(path);
        Assert(reader.Length == 1, "minimal QCOW2 virtual size");
        Assert(reader.ReadByte(0) == 0, "minimal QCOW2 sparse data");
    }

    private static void TestTruncatedHeaders(string directory)
    {
        var valid = CreateMinimalImage();
        foreach (var length in new[] { 0, 1, 4, 71, 72, 103 })
        {
            AssertRejectedAndDeletable(
                Path.Combine(directory, $"truncated-{length}.qcow2"),
                valid[..length],
                $"truncated QCOW2 header ({length} bytes)");
        }
    }

    private static void TestUntrustedHeaderLimits(string directory)
    {
        AssertHeaderMutationRejected(directory, "cluster-bits", image =>
            BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(20), 31));
        AssertHeaderMutationRejected(directory, "header-length", image =>
            BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(100), uint.MaxValue));
        AssertHeaderMutationRejected(directory, "backing-offset", image =>
        {
            BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(8), ulong.MaxValue);
            BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(16), 1);
        });
        AssertHeaderMutationRejected(directory, "backing-size-mismatch", image =>
            BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(16), 1));
        AssertHeaderMutationRejected(directory, "l1-count", image =>
            BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(36), uint.MaxValue));
        AssertHeaderMutationRejected(directory, "l1-offset", image =>
            BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(40), 4096));
        AssertHeaderMutationRejected(directory, "snapshot-count", image =>
        {
            BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(60), uint.MaxValue);
            BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(64), 512);
        });

        var snapshotMetadata = CreateMinimalImage(560);
        BinaryPrimitives.WriteUInt32BigEndian(snapshotMetadata.AsSpan(60), 1);
        BinaryPrimitives.WriteUInt64BigEndian(snapshotMetadata.AsSpan(64), 520);
        BinaryPrimitives.WriteUInt32BigEndian(snapshotMetadata.AsSpan(520 + 36), uint.MaxValue);
        AssertRejectedAndDeletable(
            Path.Combine(directory, "oversized-snapshot-metadata.qcow2"),
            snapshotMetadata,
            "oversized QCOW2 snapshot metadata");
    }

    private static void TestDeterministicHeaderMutations(string directory)
    {
        var baseline = CreateMinimalImage();
        var stopwatch = Stopwatch.StartNew();
        for (var index = 0; index < 128; index++)
        {
            var image = (byte[])baseline.Clone();
            var offset = (index * 37 + 11) % 104;
            image[offset] ^= checked((byte)(1 << (index % 8)));
            var path = Path.Combine(directory, $"mutated-header-{index:D3}.qcow2");
            File.WriteAllBytes(path, image);
            try
            {
                using var reader = new Qcow2Reader(path);
                _ = reader.Length;
            }
            catch (Exception exception) when (IsExpectedMalformedInputException(exception))
            {
                // Rejection is an expected outcome for a mutated header.
            }

            File.Delete(path);
            Assert(!File.Exists(path), $"mutated QCOW2 handle released ({index})");
        }

        stopwatch.Stop();
        Assert(stopwatch.Elapsed < TimeSpan.FromSeconds(10), "mutated QCOW2 headers finish promptly");
    }

    private static void AssertHeaderMutationRejected(
        string directory,
        string name,
        Action<byte[]> mutate)
    {
        var image = CreateMinimalImage();
        mutate(image);
        AssertRejectedAndDeletable(
            Path.Combine(directory, $"invalid-{name}.qcow2"),
            image,
            $"invalid QCOW2 {name}");
    }

    private static void AssertRejectedAndDeletable(
        string path,
        byte[] image,
        string description)
    {
        File.WriteAllBytes(path, image);
        var rejected = false;
        try
        {
            using var reader = new Qcow2Reader(path);
        }
        catch (Exception exception) when (IsExpectedMalformedInputException(exception))
        {
            rejected = true;
        }

        Assert(rejected, $"{description} rejected");
        File.Delete(path);
        Assert(!File.Exists(path), $"{description} file handle released");
    }

    private static bool IsExpectedMalformedInputException(Exception exception) =>
        exception is InvalidDataException
            or EndOfStreamException
            or NotSupportedException
            or OverflowException
            or FileNotFoundException;

    private static byte[] CreateMinimalImage(int physicalLength = 520)
    {
        var image = new byte[physicalLength];
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(0), Qcow2Header.MagicValue);
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(4), 3);
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(20), 9);
        BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(24), 1);
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(36), 1);
        BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(40), 512);
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(96), 4);
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(100), 104);
        return image;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Assertion failed: {message}");
        }
    }

    private static byte ReadByte(this IBlockReader reader, long offset)
    {
        var buffer = new byte[1];
        reader.ReadAt(offset, buffer, 0, buffer.Length);
        return buffer[0];
    }
}
