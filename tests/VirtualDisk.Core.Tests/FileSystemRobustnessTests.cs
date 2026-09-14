using System.Buffers.Binary;
using System.Collections;
using Qcow2Explorer.Core;
using Qcow2Explorer.FileSystems;
using Qcow2Explorer.Partitions;

internal static class FileSystemRobustnessTests
{
    public static void Run(string directory)
    {
        TestNtfsResidentLengthBounds();
        TestExtGeometryBounds(directory);
        TestXfsGeometryBounds(directory);
        TestBtrfsInitializationCancellation(directory);
        TestAmbiguousVirtualPathsRejected();
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

    private static void TestXfsGeometryBounds(string directory)
    {
        var validPath = Path.Combine(directory, "xfs-minimal-geometry.raw");
        WriteXfsSuperblock(validPath, blockSize: 4096, blockSizeLog2: 12, dataBlocks: 256);
        using (var reader = new RawDiskImageReader(validPath))
        {
            Assert(XfsRawFileSystem.TryOpen(reader) is not null, "bounded XFS geometry accepted");
        }

        var blockPath = Path.Combine(directory, "xfs-invalid-block-size.raw");
        WriteXfsSuperblock(blockPath, blockSize: 3, blockSizeLog2: 0, dataBlocks: 256);
        AssertXfsRejected<InvalidDataException>(blockPath, "XFS invalid block size");

        var logarithmPath = Path.Combine(directory, "xfs-invalid-block-log.raw");
        WriteXfsSuperblock(logarithmPath, blockSize: 4096, blockSizeLog2: 31, dataBlocks: 256);
        AssertXfsRejected<InvalidDataException>(logarithmPath, "XFS inconsistent block logarithm");

        var lengthPath = Path.Combine(directory, "xfs-out-of-range-blocks.raw");
        WriteXfsSuperblock(lengthPath, blockSize: 4096, blockSizeLog2: 12, dataBlocks: 257);
        AssertXfsRejected<InvalidDataException>(lengthPath, "XFS data blocks beyond input");
    }

    private static void TestBtrfsInitializationCancellation(string directory)
    {
        var path = Path.Combine(directory, "btrfs-cancel-initialization.raw");
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.SetLength(128 * 1024);
        }

        using var reader = new RawDiskImageReader(path);
        var partition = new PartitionInfo
        {
            Number = 1,
            Scheme = "RAW",
            Name = "cancel",
            SectorCount = checked((ulong)(reader.Length / 512)),
            FileSystem = "Btrfs",
        };
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();
        AssertThrows<OperationCanceledException>(
            () => _ = FileSystemDetector.TryOpen(
                reader,
                partition,
                out _,
                cancellationSource.Token),
            "Btrfs initialization cancellation propagated");
    }

    private static void TestAmbiguousVirtualPathsRejected()
    {
        var fileSystem = new DuplicateNameFileSystem();
        AssertThrows<InvalidDataException>(
            () => FileEditService.TryResolvePath(fileSystem, "/duplicate.txt", out _),
            "duplicate case-insensitive virtual path rejected");

        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();
        AssertThrows<OperationCanceledException>(
            () => FileEditService.TryResolvePath(fileSystem, "/", out _, cancellationSource.Token),
            "virtual path resolution cancellation propagated");
        AssertThrows<InvalidDataException>(
            () => FileEditService.TryResolvePath(new NullListFileSystem(), "/item", out _),
            "null virtual directory listing rejected");
        AssertThrows<InvalidDataException>(
            () => FileEditService.TryResolvePath(new NullNodeFileSystem(), "/item", out _),
            "null virtual directory node rejected");
        AssertThrows<InvalidDataException>(
            () => FileEditService.TryResolvePath(new OversizedDirectoryFileSystem(), "/item", out _),
            "oversized virtual directory rejected");
        var deepPath = "/" + string.Join('/', Enumerable.Repeat("directory", 257));
        AssertThrows<NotSupportedException>(
            () => FileEditService.TryResolvePath(fileSystem, deepPath, out _),
            "overly deep virtual path rejected");
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

    private static void WriteXfsSuperblock(
        string path,
        uint blockSize,
        byte blockSizeLog2,
        ulong dataBlocks)
    {
        const int imageLength = 1024 * 1024;
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.SetLength(imageLength);
        var super = new byte[512];
        BinaryPrimitives.WriteUInt32BigEndian(super.AsSpan(0x00), 0x58465342);
        BinaryPrimitives.WriteUInt32BigEndian(super.AsSpan(0x04), blockSize);
        BinaryPrimitives.WriteUInt64BigEndian(super.AsSpan(0x08), dataBlocks);
        new Guid("5f1c4201-a39f-410b-9203-f0ea4a57ac27").TryWriteBytes(super.AsSpan(0x20), bigEndian: true, out _);
        BinaryPrimitives.WriteUInt64BigEndian(super.AsSpan(0x38), 128);
        BinaryPrimitives.WriteUInt32BigEndian(super.AsSpan(0x54), 256);
        BinaryPrimitives.WriteUInt32BigEndian(super.AsSpan(0x58), 1);
        BinaryPrimitives.WriteUInt16BigEndian(super.AsSpan(0x64), 5);
        BinaryPrimitives.WriteUInt16BigEndian(super.AsSpan(0x66), 512);
        BinaryPrimitives.WriteUInt16BigEndian(super.AsSpan(0x68), 512);
        BinaryPrimitives.WriteUInt16BigEndian(super.AsSpan(0x6a), 8);
        super[0x78] = blockSizeLog2;
        super[0x7a] = 9;
        super[0x7b] = 3;
        super[0x7c] = 8;
        super[0xc0] = 0;
        stream.Position = 0;
        stream.Write(super);
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

    private static void AssertXfsRejected<TException>(string path, string message)
        where TException : Exception
    {
        using var reader = new RawDiskImageReader(path);
        AssertThrows<TException>(() => _ = XfsRawFileSystem.TryOpen(reader), message);
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

    private sealed class DuplicateNameFileSystem : IReadOnlyFileSystem
    {
        public string Name => "NTFS";

        public PartitionInfo Partition { get; } = new();

        public VfsNode Root { get; } = new() { Name = "", VirtualPath = "/", IsDirectory = true };

        public IReadOnlyList<VfsNode> ListDirectory(VfsNode directory) =>
            [
                new VfsNode { Name = "duplicate.txt", VirtualPath = "/duplicate.txt" },
                new VfsNode { Name = "DUPLICATE.TXT", VirtualPath = "/DUPLICATE.TXT" },
            ];

        public byte[] ReadFile(VfsNode file, long offset, int count) => throw new NotSupportedException();
    }

    private abstract class MalformedPathFileSystem : IReadOnlyFileSystem
    {
        public string Name => "test";
        public PartitionInfo Partition { get; } = new();
        public VfsNode Root { get; } = new() { Name = "", VirtualPath = "/", IsDirectory = true };
        public abstract IReadOnlyList<VfsNode> ListDirectory(VfsNode directory);
        public byte[] ReadFile(VfsNode file, long offset, int count) => throw new NotSupportedException();
    }

    private sealed class NullListFileSystem : MalformedPathFileSystem
    {
        public override IReadOnlyList<VfsNode> ListDirectory(VfsNode directory) => null!;
    }

    private sealed class NullNodeFileSystem : MalformedPathFileSystem
    {
        public override IReadOnlyList<VfsNode> ListDirectory(VfsNode directory) => [null!];
    }

    private sealed class OversizedDirectoryFileSystem : MalformedPathFileSystem
    {
        public override IReadOnlyList<VfsNode> ListDirectory(VfsNode directory) => new OversizedNodeList();
    }

    private sealed class OversizedNodeList : IReadOnlyList<VfsNode>
    {
        public int Count => 1_000_001;
        public VfsNode this[int index] => throw new NotSupportedException();
        public IEnumerator<VfsNode> GetEnumerator() => throw new NotSupportedException();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
