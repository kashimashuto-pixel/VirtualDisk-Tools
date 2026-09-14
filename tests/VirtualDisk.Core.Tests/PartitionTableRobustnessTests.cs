using System.Buffers.Binary;
using Qcow2Explorer.Core;
using Qcow2Explorer.Creation;
using Qcow2Explorer.Partitions;

internal static class PartitionTableRobustnessTests
{
    private const int SectorSize = 512;
    private const long DiskLength = 512L * 1024 * 1024;

    public static void Run(string directory)
    {
        TestTruncatedTables(directory);
        TestMbrBounds(directory);
        TestGptRecoveryAndChecksums(directory);
        TestCancellation(directory);
    }

    private static void TestTruncatedTables(string directory)
    {
        foreach (var length in new[] { 0, 1, 511 })
        {
            var path = Path.Combine(directory, $"partition-truncated-{length}.raw");
            File.WriteAllBytes(path, new byte[length]);
            using var reader = new RawDiskImageReader(path);
            Assert(PartitionTableReader.ReadPartitions(reader).Count == 0, $"truncated table ({length}) ignored");
        }
    }

    private static void TestMbrBounds(string directory)
    {
        var path = Path.Combine(directory, "partition-invalid-mbr.raw");
        var image = new byte[4096];
        image[510] = 0x55;
        image[511] = 0xaa;
        var entry = image.AsSpan(446, 16);
        entry[4] = 0x83;
        BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], 7);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[12..], 10);
        File.WriteAllBytes(path, image);

        using var reader = new RawDiskImageReader(path);
        Assert(PartitionTableReader.ReadPartitions(reader).Count == 0, "out-of-range MBR partition ignored");

        BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[12..], uint.MaxValue);
        File.WriteAllBytes(path, image);
        Assert(PartitionTableReader.ReadPartitions(reader).Count == 0, "overflowing MBR partition ignored");
    }

    private static void TestGptRecoveryAndChecksums(string directory)
    {
        var path = CreateGptImage(directory, "partition-gpt-recovery.raw");
        using var reader = new RawDiskImageReader(path);
        AssertSingleGptPartition(reader, "valid GPT");

        var primaryHeaderByte = SectorSize + 24L;
        var backupHeaderByte = DiskLength - SectorSize + 24L;
        FlipByte(path, primaryHeaderByte);
        AssertSingleGptPartition(reader, "backup GPT recovery");

        FlipByte(path, backupHeaderByte);
        Assert(PartitionTableReader.ReadPartitions(reader).Count == 0, "both invalid GPT headers rejected");
        FlipByte(path, primaryHeaderByte);
        FlipByte(path, backupHeaderByte);
        AssertSingleGptPartition(reader, "restored GPT headers");

        var primaryEntryByte = 2L * SectorSize;
        var backupEntryByte = DiskLength - 33L * SectorSize;
        FlipByte(path, primaryEntryByte);
        AssertSingleGptPartition(reader, "backup GPT used after primary entry CRC failure");

        FlipByte(path, backupEntryByte);
        Assert(PartitionTableReader.ReadPartitions(reader).Count == 0, "both invalid GPT entry arrays rejected");
        FlipByte(path, primaryEntryByte);
        FlipByte(path, backupEntryByte);
        AssertSingleGptPartition(reader, "restored GPT entry arrays");
    }

    private static void TestCancellation(string directory)
    {
        var path = CreateGptImage(directory, "partition-gpt-cancel.raw");
        using var reader = new RawDiskImageReader(path);
        using var source = new CancellationTokenSource();
        source.Cancel();
        AssertThrows<OperationCanceledException>(
            () => PartitionTableReader.ReadPartitions(reader, source.Token),
            "partition parsing cancellation");
    }

    private static string CreateGptImage(string directory, string name)
    {
        var path = Path.Combine(directory, name);
        var layouts = VirtualDiskPartitionTableWriter.Plan(
            DiskLength,
            VirtualDiskPartitionTableKind.Gpt,
            [
                new VirtualDiskPartitionDefinition(
                    64L * 1024 * 1024,
                    "CRC test",
                    "VDT_CRC",
                    VirtualDiskFileSystemKind.Ext4),
            ]);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite);
        stream.SetLength(DiskLength);
        VirtualDiskPartitionTableWriter.Write(
            stream,
            DiskLength,
            VirtualDiskPartitionTableKind.Gpt,
            layouts,
            new Guid("9d78669c-2a0d-41f7-86ac-f44dd1f112cb"));
        return path;
    }

    private static void FlipByte(string path, long offset)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        stream.Position = offset;
        var value = stream.ReadByte();
        Assert(value >= 0, "GPT mutation source byte exists");
        stream.Position = offset;
        stream.WriteByte(checked((byte)(value ^ 0x80)));
        stream.Flush();
    }

    private static void AssertSingleGptPartition(IBlockReader reader, string message)
    {
        var partitions = PartitionTableReader.ReadPartitions(reader);
        Assert(partitions.Count == 1, $"{message}: partition count");
        Assert(partitions[0].Scheme == "GPT", $"{message}: partition scheme");
        Assert(partitions[0].StartOffset == 1024L * 1024, $"{message}: partition offset");
        Assert(partitions[0].LengthBytes == 64L * 1024 * 1024, $"{message}: partition length");
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
}
