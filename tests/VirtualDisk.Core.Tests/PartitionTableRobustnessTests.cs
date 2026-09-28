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
        TestExtendedMbrIntegrity(directory);
        TestGptRecoveryAndChecksums(directory);
        TestGptCopiesMustAgree(directory);
        TestGptOverlap(directory);
        TestInvalidGptEntryRejectsTable(directory);
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

        image.AsSpan(446, 64).Clear();
        entry = image.AsSpan(446, 16);
        entry[4] = 0x83;
        BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[12..], 1);
        File.WriteAllBytes(path, image);
        Assert(PartitionTableReader.ReadPartitions(reader).Count == 0, "LBA zero MBR partition rejected");

        BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[12..], 4);
        var secondEntry = image.AsSpan(462, 16);
        secondEntry[4] = 0x83;
        BinaryPrimitives.WriteUInt32LittleEndian(secondEntry[8..], 3);
        BinaryPrimitives.WriteUInt32LittleEndian(secondEntry[12..], 4);
        File.WriteAllBytes(path, image);
        Assert(PartitionTableReader.ReadPartitions(reader).Count == 0, "overlapping MBR partitions rejected");
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

    private static void TestExtendedMbrIntegrity(string directory)
    {
        var path = Path.Combine(directory, "partition-extended-mbr.raw");
        var image = new byte[8 * SectorSize];
        WriteMbrSignature(image.AsSpan(0, SectorSize));
        var extended = image.AsSpan(446, 16);
        extended[4] = 0x0f;
        BinaryPrimitives.WriteUInt32LittleEndian(extended[8..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(extended[12..], 7);

        var firstEbr = image.AsSpan(SectorSize, SectorSize);
        WriteMbrSignature(firstEbr);
        var firstLogical = firstEbr.Slice(446, 16);
        firstLogical[4] = 0x83;
        BinaryPrimitives.WriteUInt32LittleEndian(firstLogical[8..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(firstLogical[12..], 1);
        var link = firstEbr.Slice(462, 16);
        link[4] = 0x0f;
        BinaryPrimitives.WriteUInt32LittleEndian(link[8..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(link[12..], 7);
        File.WriteAllBytes(path, image);
        using (var reader = new RawDiskImageReader(path))
        {
            Assert(PartitionTableReader.ReadPartitions(reader).Count == 0, "cyclic EBR chain rejected without partial results");
        }

        link.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(extended[12..], 4);
        BinaryPrimitives.WriteUInt32LittleEndian(firstLogical[8..], 3);
        BinaryPrimitives.WriteUInt32LittleEndian(firstLogical[12..], 2);
        File.WriteAllBytes(path, image);
        using (var reader = new RawDiskImageReader(path))
        {
            Assert(PartitionTableReader.ReadPartitions(reader).Count == 0,
                "logical partition outside extended container rejected");
        }

        image.AsSpan().Clear();
        WriteMbrSignature(image.AsSpan(0, SectorSize));
        extended = image.AsSpan(446, 16);
        extended[4] = 0x0f;
        BinaryPrimitives.WriteUInt32LittleEndian(extended[8..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(extended[12..], 7);
        firstEbr = image.AsSpan(SectorSize, SectorSize);
        WriteMbrSignature(firstEbr);
        firstLogical = firstEbr.Slice(446, 16);
        firstLogical[4] = 0x83;
        BinaryPrimitives.WriteUInt32LittleEndian(firstLogical[8..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(firstLogical[12..], 1);
        link = firstEbr.Slice(462, 16);
        link[4] = 0x0f;
        BinaryPrimitives.WriteUInt32LittleEndian(link[8..], 3);
        BinaryPrimitives.WriteUInt32LittleEndian(link[12..], 4);
        var secondEbr = image.AsSpan(4 * SectorSize, SectorSize);
        WriteMbrSignature(secondEbr);
        var secondLogical = secondEbr.Slice(446, 16);
        secondLogical[4] = 0x83;
        BinaryPrimitives.WriteUInt32LittleEndian(secondLogical[8..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(secondLogical[12..], 1);
        File.WriteAllBytes(path, image);
        using (var reader = new RawDiskImageReader(path))
        {
            var partitions = PartitionTableReader.ReadPartitions(reader);
            Assert(partitions.Count == 2, "valid EBR chain partition count");
            Assert(partitions[0].StartLba == 2 && partitions[1].StartLba == 5,
                "valid EBR chain partition offsets");
        }
    }

    private static void TestGptCopiesMustAgree(string directory)
    {
        var path = CreateGptImage(directory, "partition-gpt-mismatch.raw");
        var primaryEntriesOffset = 2L * SectorSize;
        var backupEntriesOffset = DiskLength - 33L * SectorSize;
        var primaryHeaderOffset = SectorSize;
        var backupHeaderOffset = DiskLength - SectorSize;

        FlipGptEntryByteAndRepair(path, backupEntriesOffset, backupHeaderOffset, 56);
        using var reader = new RawDiskImageReader(path);
        Assert(PartitionTableReader.ReadPartitions(reader).Count == 0, "mismatched valid GPT copies rejected");

        FlipGptEntryByteAndRepair(path, primaryEntriesOffset, primaryHeaderOffset, 56);
        AssertSingleGptPartition(reader, "matching repaired GPT copies");
    }

    private static void WriteMbrSignature(Span<byte> sector)
    {
        sector[510] = 0x55;
        sector[511] = 0xaa;
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

    private static void TestGptOverlap(string directory)
    {
        var path = Path.Combine(directory, "partition-overlap-gpt.raw");
        var layouts = VirtualDiskPartitionTableWriter.Plan(
            DiskLength,
            VirtualDiskPartitionTableKind.Gpt,
            [
                new VirtualDiskPartitionDefinition(
                    64L * 1024 * 1024,
                    "First",
                    "VDT_FIRST",
                    VirtualDiskFileSystemKind.Ext4),
                new VirtualDiskPartitionDefinition(
                    64L * 1024 * 1024,
                    "Second",
                    "VDT_SECOND",
                    VirtualDiskFileSystemKind.Ext4),
            ]);
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            stream.SetLength(DiskLength);
            VirtualDiskPartitionTableWriter.Write(stream, DiskLength, VirtualDiskPartitionTableKind.Gpt, layouts);
        }

        MakeSecondGptEntryOverlap(path, 2L * SectorSize, SectorSize);
        MakeSecondGptEntryOverlap(path, DiskLength - 33L * SectorSize, DiskLength - SectorSize);
        using var reader = new RawDiskImageReader(path);
        Assert(PartitionTableReader.ReadPartitions(reader).Count == 0, "overlapping GPT partitions rejected");
    }

    private static void TestInvalidGptEntryRejectsTable(string directory)
    {
        var path = CreateGptImage(directory, "partition-invalid-entry-gpt.raw");
        SetGptEntryUInt64AndRepair(path, 2L * SectorSize, SectorSize, 40, (ulong)(DiskLength / SectorSize));
        SetGptEntryUInt64AndRepair(
            path,
            DiskLength - 33L * SectorSize,
            DiskLength - SectorSize,
            40,
            (ulong)(DiskLength / SectorSize));

        using var reader = new RawDiskImageReader(path);
        Assert(PartitionTableReader.ReadPartitions(reader).Count == 0, "invalid populated GPT entry rejects whole table");
    }

    private static void FlipGptEntryByteAndRepair(
        string path,
        long entriesOffset,
        long headerOffset,
        int relativeOffset)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        stream.Position = entriesOffset + relativeOffset;
        var value = stream.ReadByte();
        Assert(value >= 0, "GPT entry mutation source byte exists");
        stream.Position = entriesOffset + relativeOffset;
        stream.WriteByte(checked((byte)(value ^ 0x01)));
        RepairGptChecksums(stream, entriesOffset, headerOffset);
    }

    private static void SetGptEntryUInt64AndRepair(
        string path,
        long entriesOffset,
        long headerOffset,
        int relativeOffset,
        ulong value)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        stream.Position = entriesOffset + relativeOffset;
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        stream.Write(bytes);
        RepairGptChecksums(stream, entriesOffset, headerOffset);
    }

    private static void MakeSecondGptEntryOverlap(string path, long entriesOffset, long headerOffset)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        stream.Position = entriesOffset + 128 + 32;
        Span<byte> firstLba = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(firstLba, 2049);
        stream.Write(firstLba);

        RepairGptChecksums(stream, entriesOffset, headerOffset);
    }

    private static void RepairGptChecksums(FileStream stream, long entriesOffset, long headerOffset)
    {
        const int entryArrayBytes = 128 * 128;

        var entries = new byte[entryArrayBytes];
        stream.Position = entriesOffset;
        stream.ReadExactly(entries);
        var entriesCrc = ComputeCrc32(entries);

        var header = new byte[SectorSize];
        stream.Position = headerOffset;
        stream.ReadExactly(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(88), entriesCrc);
        Array.Clear(header, 16, sizeof(uint));
        var headerSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12)));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), ComputeCrc32(header.AsSpan(0, headerSize)));
        stream.Position = headerOffset;
        stream.Write(header);
        stream.Flush();
    }

    private static uint ComputeCrc32(ReadOnlySpan<byte> data)
    {
        var crc = uint.MaxValue;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xedb88320U : crc >> 1;
            }
        }

        return ~crc;
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
