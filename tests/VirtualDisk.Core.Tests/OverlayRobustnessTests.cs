using Qcow2Explorer.Core;

internal static class OverlayRobustnessTests
{
    private const int ExportChunkSize = 4 * 1024 * 1024;

    public static void Run(string directory)
    {
        TestExportUsesStableSnapshot(directory);
        TestCancelledExportCleanup(directory);
        TestChangedSourceRejected(directory);
        TestSourceChangedDuringExportRejected(directory);
    }

    private static void TestExportUsesStableSnapshot(string directory)
    {
        var sourceData = Enumerable.Repeat((byte)0x11, ExportChunkSize * 2).ToArray();
        var overlay = new CopyOnWriteBlockDevice(new MemoryBlockReader(sourceData), 4096);
        var destination = Path.Combine(directory, "overlay-stable-snapshot.raw");
        var modification = Enumerable.Repeat((byte)0x77, 4096).ToArray();
        var modificationOffset = ExportChunkSize + 1024L * 1024;
        var injected = false;
        var progress = new CallbackProgress(value =>
        {
            if (!injected && value.Completed >= ExportChunkSize)
            {
                overlay.WriteAt(modificationOffset, modification, 0, modification.Length);
                injected = true;
            }
        });

        overlay.ExportRawAsync(destination, progress).GetAwaiter().GetResult();
        Assert(injected, "overlay mutation injected during export");
        using (var exported = new RawDiskImageReader(destination))
        {
            var actual = new byte[modification.Length];
            exported.ReadAt(modificationOffset, actual, 0, actual.Length);
            Assert(actual.All(value => value == 0x11), "export retained start-of-operation snapshot");
        }

        var current = new byte[modification.Length];
        overlay.ReadAt(modificationOffset, current, 0, current.Length);
        Assert(current.SequenceEqual(modification), "concurrent overlay edit remains pending after export");
    }

    private static void TestCancelledExportCleanup(string directory)
    {
        var sourceData = new byte[ExportChunkSize * 2];
        var overlay = new CopyOnWriteBlockDevice(new MemoryBlockReader(sourceData), 4096);
        var destination = Path.Combine(directory, "overlay-cancelled.raw");
        using var source = new CancellationTokenSource();
        var progress = new CallbackProgress(value =>
        {
            if (value.Completed >= ExportChunkSize)
            {
                source.Cancel();
            }
        });

        AssertThrows<OperationCanceledException>(
            () => overlay.ExportRawAsync(destination, progress, source.Token).GetAwaiter().GetResult(),
            "overlay export cancellation propagated");
        Assert(!File.Exists(destination), "cancelled overlay destination absent");
        var prefix = $".{Path.GetFileName(destination)}.";
        Assert(
            !Directory.EnumerateFiles(directory)
                .Any(path => Path.GetFileName(path).StartsWith(prefix, StringComparison.Ordinal)),
            "cancelled overlay partial file removed");
    }

    private static void TestChangedSourceRejected(string directory)
    {
        var sourcePath = Path.Combine(directory, "overlay-changing-source.raw");
        var destination = Path.Combine(directory, "overlay-changing-source-output.raw");
        File.WriteAllBytes(sourcePath, new byte[128 * 1024]);
        var originalWriteTime = File.GetLastWriteTimeUtc(sourcePath);
        using var reader = new RawDiskImageReader(sourcePath);
        var overlay = new CopyOnWriteBlockDevice(reader, 4096);
        overlay.WriteAt(0, new byte[] { 0x55 }, 0, 1);

        using (var writer = new FileStream(sourcePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            writer.Position = 64 * 1024;
            writer.WriteByte(0x77);
            writer.Flush(flushToDisk: true);
        }

        File.SetLastWriteTimeUtc(sourcePath, originalWriteTime.AddSeconds(2));
        AssertThrows<IOException>(
            () => overlay.ExportRawAsync(destination).GetAwaiter().GetResult(),
            "overlay export rejects a changed file source");
        Assert(!File.Exists(destination), "changed-source overlay destination absent");
        var prefix = $".{Path.GetFileName(destination)}.";
        Assert(
            !Directory.EnumerateFiles(directory)
                .Any(path => Path.GetFileName(path).StartsWith(prefix, StringComparison.Ordinal)),
            "changed-source overlay partial file absent");
    }

    private static void TestSourceChangedDuringExportRejected(string directory)
    {
        var sourcePath = Path.Combine(directory, "overlay-concurrent-source.raw");
        var destination = Path.Combine(directory, "overlay-concurrent-source-output.raw");
        File.WriteAllBytes(sourcePath, new byte[ExportChunkSize * 2]);
        var originalWriteTime = File.GetLastWriteTimeUtc(sourcePath);
        using var reader = new RawDiskImageReader(sourcePath);
        var overlay = new CopyOnWriteBlockDevice(reader, 4096);
        var changed = false;
        var progress = new CallbackProgress(value =>
        {
            if (changed || value.Completed < ExportChunkSize)
            {
                return;
            }

            using var writer = new FileStream(sourcePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            writer.Position = ExportChunkSize + 4096;
            writer.WriteByte(0x66);
            writer.Flush(flushToDisk: true);
            File.SetLastWriteTimeUtc(sourcePath, originalWriteTime.AddSeconds(2));
            changed = true;
        });

        AssertThrows<IOException>(
            () => overlay.ExportRawAsync(destination, progress).GetAwaiter().GetResult(),
            "overlay export rejects a source changed during export");
        Assert(changed, "source mutation injected during overlay export");
        Assert(!File.Exists(destination), "concurrent-source overlay destination absent");
        var prefix = $".{Path.GetFileName(destination)}.";
        Assert(
            !Directory.EnumerateFiles(directory)
                .Any(path => Path.GetFileName(path).StartsWith(prefix, StringComparison.Ordinal)),
            "concurrent-source overlay partial file removed");
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

    private sealed class CallbackProgress(Action<DiskImageProgress> callback) : IProgress<DiskImageProgress>
    {
        public void Report(DiskImageProgress value) => callback(value);
    }

    private sealed class MemoryBlockReader(byte[] data) : IBlockReader
    {
        public long Length => data.Length;

        public void ReadAt(long offset, byte[] buffer, int bufferOffset, int count)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            if (offset > data.Length - count)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            Array.Copy(data, offset, buffer, bufferOffset, count);
        }
    }
}
