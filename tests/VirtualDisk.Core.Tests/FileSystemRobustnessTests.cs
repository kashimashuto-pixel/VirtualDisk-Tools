using System.Buffers.Binary;
using Qcow2Explorer.Core;
using Qcow2Explorer.FileSystems;
using Qcow2Explorer.Partitions;

internal static class FileSystemRobustnessTests
{
    public static void Run(string directory)
    {
        TestNtfsResidentLengthBounds();
        TestExtGeometryBounds(directory);
    }

    private static void TestNtfsResidentLengthBounds()
    {
        var record = new byte[64];
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(16), uint.MaxValue);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(20), 1);
        Assert(
            NtfsFileSystem.GetResidentValue(record, 0, 24) is null,
            "overflowing NTFS resident value rejected before allocation");

        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(16), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(20), 24);
        var expected = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        expected.CopyTo(record, 24);
        Assert(
            NtfsFileSystem.GetResidentValue(record, 0, 32)?.SequenceEqual(expected) == true,
            "bounded NTFS resident value accepted");
    }

    private static void TestExtGeometryBounds(string directory)
    {
        var exponentPath = Path.Combine(directory, "ext-invalid-block-exponent.raw");
        WriteExtSuperblock(exponentPath, logBlockSize: uint.MaxValue, blockCount: 4, inodeCount: 1, inodesPerGroup: 1);
        AssertExtRejected<NotSupportedException>(exponentPath, "ext invalid block-size exponent");

        var lengthPath = Path.Combine(directory, "ext-out-of-range-blocks.raw");
        WriteExtSuperblock(lengthPath, logBlockSize: 0, blockCount: 1000, inodeCount: 1, inodesPerGroup: 1);
        AssertExtRejected<InvalidDataException>(lengthPath, "ext block count beyond input");

        var inodePath = Path.Combine(directory, "ext-invalid-inode-geometry.raw");
        WriteExtSuperblock(inodePath, logBlockSize: 0, blockCount: 4, inodeCount: 10, inodesPerGroup: 1);
        AssertExtRejected<InvalidDataException>(inodePath, "ext inode count beyond groups");
    }

    private static void WriteExtSuperblock(
        string path,
        uint logBlockSize,
        uint blockCount,
        uint inodeCount,
        uint inodesPerGroup)
    {
        var image = new byte[4096];
        var super = image.AsSpan(1024, 1024);
        BinaryPrimitives.WriteUInt32LittleEndian(super[0x00..], inodeCount);
        BinaryPrimitives.WriteUInt32LittleEndian(super[0x04..], blockCount);
        BinaryPrimitives.WriteUInt32LittleEndian(super[0x14..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(super[0x18..], logBlockSize);
        BinaryPrimitives.WriteUInt32LittleEndian(super[0x20..], 8);
        BinaryPrimitives.WriteUInt32LittleEndian(super[0x28..], inodesPerGroup);
        BinaryPrimitives.WriteUInt16LittleEndian(super[0x38..], 0xef53);
        BinaryPrimitives.WriteUInt16LittleEndian(super[0x58..], 128);
        BinaryPrimitives.WriteUInt16LittleEndian(super[0xfe..], 32);
        File.WriteAllBytes(path, image);
    }

    private static void AssertExtRejected<TException>(string path, string message)
        where TException : Exception
    {
        using var reader = new RawDiskImageReader(path);
        var partition = new PartitionInfo
        {
            Number = 1,
            Scheme = "RAW",
            Name = "robustness",
            SectorCount = checked((ulong)(reader.Length / 512)),
        };
        AssertThrows<TException>(() => _ = new ExtFileSystem(reader, partition), message);
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
