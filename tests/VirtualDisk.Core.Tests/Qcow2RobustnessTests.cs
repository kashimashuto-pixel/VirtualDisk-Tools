using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Qcow2Explorer.Core;
using Qcow2Explorer.Creation;

internal static class Qcow2RobustnessTests
{
    public static void Run(string directory)
    {
        TestMinimalImage(directory);
        TestTruncatedHeaders(directory);
        TestUntrustedHeaderLimits(directory);
        TestDeterministicHeaderMutations(directory);
        TestBoundedConcurrentMetadataCache(directory);
        TestBackingChains(directory);
        TestSparseL2Allocation(directory);
        TestWriterCancellationCleanup(directory);
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

    private static void TestBoundedConcurrentMetadataCache(string directory)
    {
        const int clusterBits = 21;
        const int clusterSize = 1 << clusterBits;
        const int l2Entries = clusterSize / 8;
        const int tableCount = 12;
        var path = Path.Combine(directory, "many-l2-tables.qcow2");
        CreateManyL2Image(path, clusterBits, tableCount);

        using var reader = new Qcow2Reader(path);
        Assert(reader.MaxL2CacheEntries < tableCount, "QCOW2 L2 cache test exceeds configured limit");
        for (var index = 0; index < tableCount; index++)
        {
            var offset = checked((long)index * l2Entries * clusterSize);
            Assert(reader.ReadByte(offset) == 0, $"QCOW2 sparse data through L2 table {index}");
            Assert(reader.CachedL2TableCount <= reader.MaxL2CacheEntries, "QCOW2 L2 cache remains bounded");
        }

        var snapshotSwitcher = Task.Run(() =>
        {
            for (var iteration = 0; iteration < 64; iteration++)
            {
                reader.SelectSnapshot(null);
            }
        });
        Parallel.For(0, 256, iteration =>
        {
            var table = iteration % tableCount;
            var offset = checked((long)table * l2Entries * clusterSize);
            Assert(reader.ReadByte(offset) == 0, "concurrent QCOW2 metadata read");
        });
        snapshotSwitcher.GetAwaiter().GetResult();
        Assert(reader.CachedL2TableCount <= reader.MaxL2CacheEntries, "concurrent QCOW2 L2 cache remains bounded");
    }

    private static void TestBackingChains(string directory)
    {
        var rawPath = Path.Combine(directory, "backing-base.raw");
        var middlePath = Path.Combine(directory, "backing-middle.qcow2");
        var topPath = Path.Combine(directory, "backing-top.qcow2");
        File.WriteAllBytes(rawPath, [0x5a]);
        File.WriteAllBytes(middlePath, CreateBackedImage(Path.GetFileName(rawPath)));
        File.WriteAllBytes(topPath, CreateBackedImage(Path.GetFileName(middlePath)));
        using (var reader = new Qcow2Reader(topPath))
        {
            Assert(reader.ReadByte(0) == 0x5a, "valid QCOW2 backing chain data");
        }

        var selfPath = Path.Combine(directory, "backing-self.qcow2");
        File.WriteAllBytes(selfPath, CreateBackedImage(Path.GetFileName(selfPath)));
        AssertOpenRejected(selfPath, "self-referencing QCOW2 backing chain", "循環参照");
        File.Delete(selfPath);
        Assert(!File.Exists(selfPath), "self-referencing QCOW2 handle released");

        var cycleA = Path.Combine(directory, "backing-cycle-a.qcow2");
        var cycleB = Path.Combine(directory, "backing-cycle-b.qcow2");
        File.WriteAllBytes(cycleA, CreateBackedImage(Path.GetFileName(cycleB)));
        File.WriteAllBytes(cycleB, CreateBackedImage(Path.GetFileName(cycleA)));
        AssertOpenRejected(cycleA, "cyclic QCOW2 backing chain", "循環参照");
        File.Delete(cycleA);
        File.Delete(cycleB);
        Assert(!File.Exists(cycleA) && !File.Exists(cycleB), "cyclic QCOW2 handles released");

        const int excessiveDepth = 65;
        var deepPaths = Enumerable.Range(0, excessiveDepth)
            .Select(index => Path.Combine(directory, $"backing-deep-{index:D2}.qcow2"))
            .ToArray();
        var deepBase = Path.Combine(directory, "backing-deep-base.raw");
        File.WriteAllBytes(deepBase, [0]);
        for (var index = 0; index < deepPaths.Length; index++)
        {
            var backingName = index + 1 < deepPaths.Length
                ? Path.GetFileName(deepPaths[index + 1])
                : Path.GetFileName(deepBase);
            File.WriteAllBytes(deepPaths[index], CreateBackedImage(backingName));
        }

        AssertOpenRejected(deepPaths[0], "excessively deep QCOW2 backing chain", "対応上限");
        foreach (var path in deepPaths)
        {
            File.Delete(path);
            Assert(!File.Exists(path), "deep QCOW2 backing handle released");
        }
    }

    private static void TestSparseL2Allocation(string directory)
    {
        const long rawLength = 512L * 1024 * 1024 + 512;
        const int clusterSize = 64 * 1024;
        var rawPath = Path.Combine(directory, "large-empty.raw");
        var qcow2Path = Path.Combine(directory, "large-empty.qcow2");
        using (var stream = new FileStream(rawPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.SetLength(rawLength);
        }

        Qcow2SparseWriter.WriteFromRawAsync(rawPath, qcow2Path).GetAwaiter().GetResult();
        Assert(
            new FileInfo(qcow2Path).Length == 4L * clusterSize,
            "empty QCOW2 omits unneeded L2 tables");
        using var reader = new Qcow2Reader(qcow2Path);
        Assert(reader.Length == rawLength, "large sparse QCOW2 virtual size");
        Assert(reader.ReadByte(rawLength - 1) == 0, "large sparse QCOW2 trailing data");
        Assert(reader.LookupCluster(rawLength - 1).HostClusterOffset is null, "large sparse QCOW2 trailing cluster unallocated");
    }

    private static void TestWriterCancellationCleanup(string directory)
    {
        const long rawLength = 64L * 1024 * 1024;
        var rawPath = Path.Combine(directory, "cancel-writer.raw");
        var qcow2Path = Path.Combine(directory, "cancel-writer.qcow2");
        using (var stream = new FileStream(rawPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.SetLength(rawLength);
        }

        using var cancellationSource = new CancellationTokenSource();
        var progress = new CallbackProgress<DiskImageProgress>(update =>
        {
            if (update.Completed >= 16L * 1024 * 1024)
            {
                cancellationSource.Cancel();
            }
        });
        var cancelled = false;
        try
        {
            Qcow2SparseWriter.WriteFromRawAsync(
                rawPath,
                qcow2Path,
                progress,
                cancellationSource.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        Assert(cancelled, "QCOW2 writer cancellation propagated");
        Assert(!File.Exists(qcow2Path), "cancelled QCOW2 destination absent");
        var partialPrefix = $".{Path.GetFileName(qcow2Path)}.";
        Assert(
            !Directory.EnumerateFiles(directory)
                .Any(path => Path.GetFileName(path).StartsWith(partialPrefix, StringComparison.Ordinal)),
            "cancelled QCOW2 partial output removed");
    }

    private static void AssertOpenRejected(string path, string description, string expectedMessage)
    {
        var rejected = false;
        try
        {
            using var reader = new Qcow2Reader(path);
        }
        catch (Exception exception) when (IsExpectedMalformedInputException(exception))
        {
            rejected = true;
            Assert(
                exception.Message.Contains(expectedMessage, StringComparison.Ordinal),
                $"{description} diagnostic");
        }

        Assert(rejected, $"{description} rejected");
    }

    private static void CreateManyL2Image(string path, int clusterBits, int tableCount)
    {
        var clusterSize = 1L << clusterBits;
        var l2Entries = clusterSize / 8;
        var virtualSize = checked((ulong)(tableCount * l2Entries * clusterSize));
        var header = new byte[104];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0), Qcow2Header.MagicValue);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), 3);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(20), checked((uint)clusterBits));
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(24), virtualSize);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(36), checked((uint)tableCount));
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(40), checked((ulong)clusterSize));
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(96), 4);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(100), checked((uint)header.Length));

        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        stream.SetLength(checked((tableCount + 2L) * clusterSize));
        stream.Write(header);
        stream.Position = clusterSize;
        Span<byte> entry = stackalloc byte[8];
        for (var index = 0; index < tableCount; index++)
        {
            BinaryPrimitives.WriteUInt64BigEndian(
                entry,
                checked((ulong)((index + 2L) * clusterSize)));
            stream.Write(entry);
        }
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

    private static byte[] CreateBackedImage(string backingName)
    {
        var image = CreateMinimalImage();
        var name = Encoding.UTF8.GetBytes(backingName);
        BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(8), 104);
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(16), checked((uint)name.Length));
        name.CopyTo(image.AsSpan(104));
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

    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
