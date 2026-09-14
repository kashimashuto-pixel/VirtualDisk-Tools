using System.Buffers.Binary;
using System.Text;

namespace Qcow2Explorer.Creation;

public sealed class ManagedExt4FileSystemFormatter : IVirtualDiskFileSystemFormatter
{
    private const int BlockSize = 4096;
    private const int InodeSize = 256;
    private const uint InodesPerGroup = 8192;
    private const uint MaximumBlocksPerGroup = BlockSize * 8;
    private const int GroupDescriptorSize = 64;
    private const uint FirstNonReservedInode = 11;
    private const uint ExtentsInodeFlag = 0x00080000;
    private const uint FileTypeIncompatFeature = 0x00000002;
    private const uint ExtentsIncompatFeature = 0x00000040;
    private const uint SixtyFourBitIncompatFeature = 0x00000080;
    private const uint ChecksumSeedIncompatFeature = 0x00002000;
    private const uint SparseSuperReadOnlyFeature = 0x00000001;
    private const uint LargeFileReadOnlyFeature = 0x00000002;
    private const uint MetadataChecksumReadOnlyFeature = 0x00000400;
    private const int DirectoryTailSize = 12;
    private static readonly byte[] ReadmeContent =
        Encoding.UTF8.GetBytes("Created by Virtual Disk Explorer. This file may be deleted.\n");

    public string Name => "VirtualDisk.Core ext4";

    public bool Supports(VirtualDiskFileSystemKind fileSystem) =>
        fileSystem == VirtualDiskFileSystemKind.Ext4;

    public ValueTask VerifyAvailableAsync(
        VirtualDiskFileSystemKind fileSystem,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureSupported(fileSystem);
        return ValueTask.CompletedTask;
    }

    public async Task FormatAsync(
        string imagePath,
        VirtualDiskPartitionLayout layout,
        IReadOnlyList<VirtualDiskInitialFile> initialFiles,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(initialFiles);
        EnsureSupported(layout.FileSystem);
        if (layout.SizeBytes < 64L * 1024 * 1024 || layout.SizeBytes % BlockSize != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(layout), "ext4容量は64 MiB以上かつ4 KiB境界にしてください。");
        }

        var totalBlocksLong = layout.SizeBytes / BlockSize;
        if (totalBlocksLong > uint.MaxValue)
        {
            throw new NotSupportedException("内部ext4フォーマッターは現在16 TiBまでに対応しています。");
        }

        var totalBlocks = checked((uint)totalBlocksLong);
        var desiredGroups = DivideRoundUp(totalBlocks, MaximumBlocksPerGroup);
        var blocksPerGroup = AlignUp(DivideRoundUp(totalBlocks, desiredGroups), 8);
        var groupCount = DivideRoundUp(totalBlocks, blocksPerGroup);
        var inodeCount = checked(groupCount * InodesPerGroup);
        if (initialFiles.Count + 1 > InodesPerGroup - FirstNonReservedInode)
        {
            throw new NotSupportedException("初期ファイル数が内部ext4フォーマッターのinode上限を超えています。");
        }

        var uuid = Guid.NewGuid().ToByteArray();
        Span<byte> seedBytes = stackalloc byte[4];
        Random.Shared.NextBytes(seedBytes);
        var checksumSeed = BinaryPrimitives.ReadUInt32LittleEndian(seedBytes);
        if (checksumSeed == 0)
        {
            checksumSeed = 1;
        }

        var descriptorBlocks = checked((uint)DivideRoundUp(
            checked(groupCount * GroupDescriptorSize),
            BlockSize));
        var inodeTableBlocks = checked((uint)((InodesPerGroup * InodeSize) / BlockSize));
        var groups = CreateGroups(
            totalBlocks,
            blocksPerGroup,
            groupCount,
            descriptorBlocks,
            inodeTableBlocks);
        var allocator = new BlockAllocator(groups);

        var files = new List<PlannedFile>(initialFiles.Count + 1)
        {
            PlannedFile.FromBytes(FirstNonReservedInode, "VDT-README.txt", ReadmeContent),
        };
        for (var index = 0; index < initialFiles.Count; index++)
        {
            var initialFile = initialFiles[index];
            var info = new FileInfo(initialFile.SourcePath);
            if (!info.Exists)
            {
                throw new FileNotFoundException("初期配置するファイルが見つかりません。", info.FullName);
            }

            files.Add(PlannedFile.FromPath(
                checked(FirstNonReservedInode + (uint)index + 1),
                initialFile.DestinationName,
                info.FullName,
                info.Length));
        }

        var directoryEntries = new List<DirectoryEntry>
        {
            new(2, ".", 2),
            new(2, "..", 2),
        };
        directoryEntries.AddRange(files.Select(file => new DirectoryEntry(file.InodeNumber, file.Name, 1)));
        var directoryBlocks = PackDirectoryEntries(directoryEntries);
        var rootExtents = allocator.Allocate(checked((uint)directoryBlocks.Count));
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var blockCount = file.Length == 0
                ? 0U
                : checked((uint)DivideRoundUp(checked((ulong)file.Length), BlockSize));
            file.Extents.AddRange(allocator.Allocate(blockCount));
            if (file.Extents.Count > 4)
            {
                var leafCount = DivideRoundUp(checked((uint)file.Extents.Count), 340);
                file.ExtentTreeBlocks.AddRange(allocator.Allocate(leafCount).SelectMany(ExpandBlocks));
                if (file.ExtentTreeBlocks.Count > 4)
                {
                    throw new NotSupportedException("初期ファイルのextent断片数が内部ext4フォーマッターの上限を超えています。");
                }
            }
        }

        const uint allocatedReservedInodes = FirstNonReservedInode - 1;
        var allocatedInodes = checked(allocatedReservedInodes + (uint)files.Count);
        groups[0].AllocatedInodes = allocatedInodes;
        groups[0].UsedDirectories = 1;
        var freeBlocks = groups.Aggregate<GroupLayout, ulong>(0, (total, group) => total + group.FreeBlocks);
        var freeInodes = checked((ulong)inodeCount - allocatedInodes);
        var now = checked((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        await using var stream = new FileStream(
            imagePath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        if (stream.Length != layout.SizeBytes)
        {
            throw new InvalidDataException("ext4フォーマット対象の容量がパーティション定義と一致しません。");
        }

        var descriptors = BuildGroupDescriptors(groups, blocksPerGroup, checksumSeed);
        WriteSuperBlocks(
            stream,
            groups,
            descriptors,
            totalBlocks,
            blocksPerGroup,
            inodeCount,
            checked((uint)freeBlocks),
            checked((uint)freeInodes),
            uuid,
            checksumSeed,
            layout.VolumeLabel,
            now);
        WriteAllocationMetadata(stream, groups, blocksPerGroup, checksumSeed, descriptors);

        var rootGeneration = NextGeneration();
        var rootInode = BuildInode(
            inodeNumber: 2,
            generation: rootGeneration,
            mode: 0x41ed,
            linkCount: 2,
            size: checked((ulong)directoryBlocks.Count * BlockSize),
            rootExtents,
            extentTreeBlocks: [],
            checksumSeed,
            now);
        WriteInode(stream, groups[0], 2, rootInode);
        for (var blockIndex = 0; blockIndex < directoryBlocks.Count; blockIndex++)
        {
            var directoryBlock = directoryBlocks[blockIndex];
            SetDirectoryChecksum(directoryBlock, 2, rootGeneration, checksumSeed);
            WriteFileSystemBlock(
                stream,
                GetPhysicalBlock(rootExtents, checked((uint)blockIndex)),
                directoryBlock);
        }

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            file.Generation = NextGeneration();
            var inode = BuildInode(
                file.InodeNumber,
                file.Generation,
                mode: 0x81a4,
                linkCount: 1,
                checked((ulong)file.Length),
                file.Extents,
                file.ExtentTreeBlocks,
                checksumSeed,
                now);
            WriteInode(stream, groups[0], file.InodeNumber, inode);
            WriteExtentTreeBlocks(stream, file.Extents, file.ExtentTreeBlocks);
            await WriteFileContentAsync(stream, file, cancellationToken);
        }

        stream.Flush(flushToDisk: true);
    }

    private static List<GroupLayout> CreateGroups(
        uint totalBlocks,
        uint blocksPerGroup,
        uint groupCount,
        uint descriptorBlocks,
        uint inodeTableBlocks)
    {
        var groups = new List<GroupLayout>(checked((int)groupCount));
        for (uint number = 0; number < groupCount; number++)
        {
            var start = checked(number * blocksPerGroup);
            var blockCount = Math.Min(blocksPerGroup, checked(totalBlocks - start));
            var hasSuper = HasSparseSuper(number);
            var cursor = checked(start + (hasSuper ? 1U + descriptorBlocks : 0U));
            var blockBitmap = cursor++;
            var inodeBitmap = cursor++;
            var inodeTable = cursor;
            cursor = checked(cursor + inodeTableBlocks);
            if (cursor > start + blockCount)
            {
                throw new NotSupportedException("ext4 block groupがmetadataを格納するには小さすぎます。");
            }

            groups.Add(new GroupLayout(
                number,
                start,
                blockCount,
                hasSuper,
                blockBitmap,
                inodeBitmap,
                inodeTable,
                cursor));
        }

        return groups;
    }

    private static byte[][] BuildGroupDescriptors(
        IReadOnlyList<GroupLayout> groups,
        uint blocksPerGroup,
        uint checksumSeed)
    {
        var descriptors = new byte[groups.Count][];
        foreach (var group in groups)
        {
            var descriptor = new byte[GroupDescriptorSize];
            BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(0x00, 4), group.BlockBitmap);
            BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(0x04, 4), group.InodeBitmap);
            BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(0x08, 4), group.InodeTable);
            WriteDescriptorCount(descriptor, group.FreeBlocks, 0x0c, 0x2c);
            WriteDescriptorCount(descriptor, checked(InodesPerGroup - group.AllocatedInodes), 0x0e, 0x2e);
            WriteDescriptorCount(descriptor, group.UsedDirectories, 0x10, 0x30);
            WriteDescriptorCount(
                descriptor,
                checked(InodesPerGroup - group.AllocatedInodes),
                0x1c,
                0x32);
            var blockBitmap = BuildBlockBitmap(group, blocksPerGroup);
            var inodeBitmap = BuildInodeBitmap(group);
            var blockChecksum = ComputeCrc32C(
                checksumSeed,
                blockBitmap.AsSpan(0, checked((int)DivideRoundUp(blocksPerGroup, 8))));
            var inodeChecksum = ComputeCrc32C(
                checksumSeed,
                inodeBitmap.AsSpan(0, checked((int)DivideRoundUp(InodesPerGroup, 8))));
            BinaryPrimitives.WriteUInt16LittleEndian(descriptor.AsSpan(0x18, 2), (ushort)blockChecksum);
            BinaryPrimitives.WriteUInt16LittleEndian(descriptor.AsSpan(0x1a, 2), (ushort)inodeChecksum);
            BinaryPrimitives.WriteUInt16LittleEndian(descriptor.AsSpan(0x38, 2), (ushort)(blockChecksum >> 16));
            BinaryPrimitives.WriteUInt16LittleEndian(descriptor.AsSpan(0x3a, 2), (ushort)(inodeChecksum >> 16));
            SetGroupDescriptorChecksum(group.Number, descriptor, checksumSeed);
            descriptors[group.Number] = descriptor;
        }

        return descriptors;
    }

    private static void WriteSuperBlocks(
        FileStream stream,
        IReadOnlyList<GroupLayout> groups,
        IReadOnlyList<byte[]> descriptors,
        uint totalBlocks,
        uint blocksPerGroup,
        uint inodeCount,
        uint freeBlocks,
        uint freeInodes,
        byte[] uuid,
        uint checksumSeed,
        string volumeLabel,
        uint now)
    {
        var descriptorBytes = new byte[checked((int)DivideRoundUp(
            checked((uint)descriptors.Count * GroupDescriptorSize),
            BlockSize) * BlockSize)];
        for (var index = 0; index < descriptors.Count; index++)
        {
            descriptors[index].CopyTo(descriptorBytes, index * GroupDescriptorSize);
        }

        foreach (var group in groups.Where(candidate => candidate.HasSuperBlock))
        {
            var superBlock = BuildSuperBlock(
                totalBlocks,
                blocksPerGroup,
                inodeCount,
                freeBlocks,
                freeInodes,
                uuid,
                checksumSeed,
                volumeLabel,
                now,
                checked((ushort)group.Number));
            var superOffset = group.Number == 0
                ? 1024L
                : checked((long)group.StartBlock * BlockSize);
            WriteAt(stream, superOffset, superBlock);
            var descriptorOffset = group.Number == 0
                ? BlockSize
                : checked((long)(group.StartBlock + 1) * BlockSize);
            WriteAt(stream, descriptorOffset, descriptorBytes);
        }
    }

    private static byte[] BuildSuperBlock(
        uint totalBlocks,
        uint blocksPerGroup,
        uint inodeCount,
        uint freeBlocks,
        uint freeInodes,
        byte[] uuid,
        uint checksumSeed,
        string volumeLabel,
        uint now,
        ushort blockGroupNumber)
    {
        var data = new byte[1024];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x00, 4), inodeCount);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x04, 4), totalBlocks);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x0c, 4), freeBlocks);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x10, 4), freeInodes);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x14, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x18, 4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x1c, 4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x20, 4), blocksPerGroup);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x24, 4), blocksPerGroup);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x28, 4), InodesPerGroup);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x30, 4), now);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x34, 2), 0);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(0x36, 2), -1);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x38, 2), 0xef53);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x3a, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x3c, 2), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x40, 4), now);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x48, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x4c, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x54, 4), FirstNonReservedInode);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x58, 2), InodeSize);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x5a, 2), blockGroupNumber);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x5c, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(
            data.AsSpan(0x60, 4),
            FileTypeIncompatFeature | ExtentsIncompatFeature | SixtyFourBitIncompatFeature | ChecksumSeedIncompatFeature);
        BinaryPrimitives.WriteUInt32LittleEndian(
            data.AsSpan(0x64, 4),
            SparseSuperReadOnlyFeature | LargeFileReadOnlyFeature | MetadataChecksumReadOnlyFeature);
        uuid.CopyTo(data, 0x68);
        var labelBytes = Encoding.UTF8.GetBytes(volumeLabel);
        labelBytes.AsSpan(0, Math.Min(labelBytes.Length, 16)).CopyTo(data.AsSpan(0x78, 16));
        Random.Shared.NextBytes(data.AsSpan(0xec, 16));
        data[0xfc] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0xfe, 2), GroupDescriptorSize);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x108, 4), now);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x15c, 2), 32);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x15e, 2), 32);
        data[0x175] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x270, 4), checksumSeed);
        var checksum = ComputeCrc32C(uint.MaxValue, data.AsSpan(0, 0x3fc));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x3fc, 4), checksum);
        return data;
    }

    private static void WriteAllocationMetadata(
        FileStream stream,
        IReadOnlyList<GroupLayout> groups,
        uint blocksPerGroup,
        uint checksumSeed,
        IReadOnlyList<byte[]> descriptors)
    {
        foreach (var group in groups)
        {
            var blockBitmap = BuildBlockBitmap(group, blocksPerGroup);
            var inodeBitmap = BuildInodeBitmap(group);
            WriteFileSystemBlock(stream, group.BlockBitmap, blockBitmap);
            WriteFileSystemBlock(stream, group.InodeBitmap, inodeBitmap);
        }
    }

    private static byte[] BuildBlockBitmap(GroupLayout group, uint blocksPerGroup)
    {
        var bitmap = new byte[BlockSize];
        foreach (var block in group.AllocatedBlocks)
        {
            SetBit(bitmap, checked(block - group.StartBlock));
        }

        for (var bit = group.BlockCount; bit < BlockSize * 8U; bit++)
        {
            SetBit(bitmap, bit);
        }

        return bitmap;
    }

    private static byte[] BuildInodeBitmap(GroupLayout group)
    {
        var bitmap = new byte[BlockSize];
        for (uint index = 0; index < group.AllocatedInodes; index++)
        {
            SetBit(bitmap, index);
        }

        for (var index = InodesPerGroup; index < BlockSize * 8U; index++)
        {
            SetBit(bitmap, index);
        }

        return bitmap;
    }

    private static byte[] BuildInode(
        uint inodeNumber,
        uint generation,
        ushort mode,
        ushort linkCount,
        ulong size,
        IReadOnlyList<Extent> extents,
        IReadOnlyList<uint> extentTreeBlocks,
        uint checksumSeed,
        uint now)
    {
        var data = new byte[InodeSize];
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x00, 2), mode);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x04, 4), checked((uint)size));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x08, 4), now);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x0c, 4), now);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x10, 4), now);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x1a, 2), linkCount);
        var allocatedBlocks = extents.Aggregate<Extent, ulong>(0, (total, extent) => total + extent.Length)
            + checked((ulong)extentTreeBlocks.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x1c, 4), checked((uint)(allocatedBlocks * 8)));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x20, 4), ExtentsInodeFlag);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x64, 4), generation);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x6c, 4), checked((uint)(size >> 32)));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x80, 2), 32);
        WriteExtentRoot(data.AsSpan(0x28, 60), extents, extentTreeBlocks);
        SetInodeChecksum(inodeNumber, generation, data, checksumSeed);
        return data;
    }

    private static void WriteExtentRoot(
        Span<byte> root,
        IReadOnlyList<Extent> extents,
        IReadOnlyList<uint> extentTreeBlocks)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(root[0..2], 0xf30a);
        if (extentTreeBlocks.Count == 0)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(root[2..4], checked((ushort)extents.Count));
            BinaryPrimitives.WriteUInt16LittleEndian(root[4..6], 4);
            BinaryPrimitives.WriteUInt16LittleEndian(root[6..8], 0);
            for (var index = 0; index < extents.Count; index++)
            {
                WriteExtent(root.Slice(12 + index * 12, 12), extents[index]);
            }

            return;
        }

        BinaryPrimitives.WriteUInt16LittleEndian(root[2..4], checked((ushort)extentTreeBlocks.Count));
        BinaryPrimitives.WriteUInt16LittleEndian(root[4..6], 4);
        BinaryPrimitives.WriteUInt16LittleEndian(root[6..8], 1);
        for (var index = 0; index < extentTreeBlocks.Count; index++)
        {
            var firstExtent = extents[index * 340];
            var entry = root.Slice(12 + index * 12, 12);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[0..4], firstExtent.LogicalBlock);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[4..8], extentTreeBlocks[index]);
        }
    }

    private static void WriteExtentTreeBlocks(
        FileStream stream,
        IReadOnlyList<Extent> extents,
        IReadOnlyList<uint> extentTreeBlocks)
    {
        for (var leafIndex = 0; leafIndex < extentTreeBlocks.Count; leafIndex++)
        {
            var leafExtents = extents.Skip(leafIndex * 340).Take(340).ToArray();
            var block = new byte[BlockSize];
            BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(0, 2), 0xf30a);
            BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(2, 2), checked((ushort)leafExtents.Length));
            BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(4, 2), 340);
            for (var index = 0; index < leafExtents.Length; index++)
            {
                WriteExtent(block.AsSpan(12 + index * 12, 12), leafExtents[index]);
            }

            WriteFileSystemBlock(stream, extentTreeBlocks[leafIndex], block);
        }
    }

    private static void WriteExtent(Span<byte> destination, Extent extent)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(destination[0..4], extent.LogicalBlock);
        BinaryPrimitives.WriteUInt16LittleEndian(
            destination[4..6],
            extent.Length == 32768 ? (ushort)0x8000 : checked((ushort)extent.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(destination[6..8], checked((ushort)(extent.PhysicalBlock >> 32)));
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..12], checked((uint)extent.PhysicalBlock));
    }

    private static async Task WriteFileContentAsync(
        FileStream destination,
        PlannedFile file,
        CancellationToken cancellationToken)
    {
        await using var source = file.OpenRead();
        var buffer = new byte[1024 * 1024];
        long remaining = file.Length;
        foreach (var extent in file.Extents)
        {
            destination.Position = checked((long)extent.PhysicalBlock * BlockSize);
            var extentBytes = checked((long)extent.Length * BlockSize);
            var toWrite = Math.Min(remaining, extentBytes);
            long written = 0;
            while (written < toWrite)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = checked((int)Math.Min(buffer.Length, toWrite - written));
                await source.ReadExactlyAsync(buffer.AsMemory(0, count), cancellationToken);
                await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                written += count;
                remaining -= count;
            }

            if (toWrite < extentBytes)
            {
                Array.Clear(buffer);
                var padding = extentBytes - toWrite;
                while (padding > 0)
                {
                    var count = checked((int)Math.Min(buffer.Length, padding));
                    await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    padding -= count;
                }
            }
        }

        if (remaining != 0 || source.ReadByte() != -1)
        {
            throw new InvalidDataException($"初期ファイルのサイズが処理中に変化しました: {file.Name}");
        }
    }

    private static List<byte[]> PackDirectoryEntries(IReadOnlyList<DirectoryEntry> entries)
    {
        var result = new List<byte[]>();
        var blockEntries = new List<DirectoryEntry>();
        var used = 0;
        var available = BlockSize - DirectoryTailSize;
        foreach (var entry in entries)
        {
            var length = Align4(8 + Encoding.UTF8.GetByteCount(entry.Name));
            if (length > available)
            {
                throw new NotSupportedException($"ext4ディレクトリ項目名が長すぎます: {entry.Name}");
            }

            if (used + length > available)
            {
                result.Add(BuildDirectoryBlock(blockEntries));
                blockEntries.Clear();
                used = 0;
            }

            blockEntries.Add(entry);
            used += length;
        }

        if (blockEntries.Count > 0)
        {
            result.Add(BuildDirectoryBlock(blockEntries));
        }

        return result;
    }

    private static byte[] BuildDirectoryBlock(IReadOnlyList<DirectoryEntry> entries)
    {
        var data = new byte[BlockSize];
        var entryBytes = BlockSize - DirectoryTailSize;
        var offset = 0;
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            var name = Encoding.UTF8.GetBytes(entry.Name);
            var minimumLength = Align4(8 + name.Length);
            var recordLength = index == entries.Count - 1 ? entryBytes - offset : minimumLength;
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset, 4), entry.InodeNumber);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset + 4, 2), checked((ushort)recordLength));
            data[offset + 6] = checked((byte)name.Length);
            data[offset + 7] = entry.FileType;
            name.CopyTo(data, offset + 8);
            offset += recordLength;
        }

        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(entryBytes + 4, 2), DirectoryTailSize);
        data[entryBytes + 7] = 0xde;
        return data;
    }

    private static void SetDirectoryChecksum(byte[] block, uint inodeNumber, uint generation, uint checksumSeed)
    {
        Span<byte> number = stackalloc byte[4];
        Span<byte> generationBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(number, inodeNumber);
        BinaryPrimitives.WriteUInt32LittleEndian(generationBytes, generation);
        var checksum = ComputeCrc32C(checksumSeed, number);
        checksum = ComputeCrc32C(checksum, generationBytes);
        checksum = ComputeCrc32C(checksum, block.AsSpan(0, BlockSize - DirectoryTailSize));
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(BlockSize - 4, 4), checksum);
    }

    private static void SetInodeChecksum(uint inodeNumber, uint generation, byte[] inode, uint checksumSeed)
    {
        inode.AsSpan(0x7c, 2).Clear();
        inode.AsSpan(0x82, 2).Clear();
        Span<byte> number = stackalloc byte[4];
        Span<byte> generationBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(number, inodeNumber);
        BinaryPrimitives.WriteUInt32LittleEndian(generationBytes, generation);
        var checksum = ComputeCrc32C(checksumSeed, number);
        checksum = ComputeCrc32C(checksum, generationBytes);
        checksum = ComputeCrc32C(checksum, inode);
        BinaryPrimitives.WriteUInt16LittleEndian(inode.AsSpan(0x7c, 2), (ushort)checksum);
        BinaryPrimitives.WriteUInt16LittleEndian(inode.AsSpan(0x82, 2), (ushort)(checksum >> 16));
    }

    private static void SetGroupDescriptorChecksum(uint group, byte[] descriptor, uint checksumSeed)
    {
        descriptor.AsSpan(0x1e, 2).Clear();
        Span<byte> groupBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(groupBytes, group);
        var checksum = ComputeCrc32C(checksumSeed, groupBytes);
        checksum = ComputeCrc32C(checksum, descriptor);
        BinaryPrimitives.WriteUInt16LittleEndian(descriptor.AsSpan(0x1e, 2), (ushort)checksum);
    }

    private static uint ComputeCrc32C(uint seed, ReadOnlySpan<byte> data)
    {
        const uint polynomial = 0x82f63b78;
        var checksum = seed;
        foreach (var value in data)
        {
            checksum ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                checksum = (checksum >> 1) ^ ((checksum & 1) == 0 ? 0U : polynomial);
            }
        }

        return checksum;
    }

    private static void WriteDescriptorCount(byte[] descriptor, uint value, int lowOffset, int highOffset)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(descriptor.AsSpan(lowOffset, 2), (ushort)value);
        BinaryPrimitives.WriteUInt16LittleEndian(descriptor.AsSpan(highOffset, 2), (ushort)(value >> 16));
    }

    private static void WriteInode(FileStream stream, GroupLayout group, uint inodeNumber, byte[] inode)
    {
        var index = inodeNumber - 1;
        var offset = checked((long)group.InodeTable * BlockSize + (long)index * InodeSize);
        WriteAt(stream, offset, inode);
    }

    private static void WriteFileSystemBlock(FileStream stream, ulong block, byte[] data)
    {
        if (data.Length != BlockSize)
        {
            throw new ArgumentException("ext4 blockは4 KiBである必要があります。", nameof(data));
        }

        WriteAt(stream, checked((long)block * BlockSize), data);
    }

    private static void WriteAt(FileStream stream, long offset, byte[] data)
    {
        stream.Position = offset;
        stream.Write(data);
    }

    private static ulong GetPhysicalBlock(IReadOnlyList<Extent> extents, uint logicalBlock)
    {
        var extent = extents.Single(candidate =>
            logicalBlock >= candidate.LogicalBlock
            && logicalBlock < candidate.LogicalBlock + candidate.Length);
        return checked(extent.PhysicalBlock + logicalBlock - extent.LogicalBlock);
    }

    private static IEnumerable<uint> ExpandBlocks(Extent extent)
    {
        for (uint index = 0; index < extent.Length; index++)
        {
            yield return checked((uint)(extent.PhysicalBlock + index));
        }
    }

    private static bool HasSparseSuper(uint group) =>
        group is 0 or 1 || IsPowerOf(group, 3) || IsPowerOf(group, 5) || IsPowerOf(group, 7);

    private static bool IsPowerOf(uint value, uint factor)
    {
        if (value < factor)
        {
            return false;
        }

        while (value % factor == 0)
        {
            value /= factor;
        }

        return value == 1;
    }

    private static uint NextGeneration()
    {
        Span<byte> bytes = stackalloc byte[4];
        Random.Shared.NextBytes(bytes);
        var value = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        return value == 0 ? 1U : value;
    }

    private static void SetBit(byte[] bitmap, uint bit) =>
        bitmap[checked((int)(bit >> 3))] |= checked((byte)(1 << checked((int)(bit & 7))));

    private static int Align4(int value) => checked((value + 3) & ~3);

    private static uint AlignUp(uint value, uint alignment) =>
        checked(DivideRoundUp(value, alignment) * alignment);

    private static uint DivideRoundUp(uint value, uint divisor) =>
        checked((value + divisor - 1) / divisor);

    private static uint DivideRoundUp(ulong value, uint divisor) =>
        checked((uint)((value + divisor - 1) / divisor));

    private static void EnsureSupported(VirtualDiskFileSystemKind fileSystem)
    {
        if (fileSystem != VirtualDiskFileSystemKind.Ext4)
        {
            throw new NotSupportedException(
                $"内部ext4フォーマッターは{VirtualDiskPartitionTableWriter.GetDisplayName(fileSystem)}に対応していません。");
        }
    }

    private sealed class BlockAllocator
    {
        private readonly IReadOnlyList<GroupLayout> _groups;
        private int _groupIndex;

        public BlockAllocator(IReadOnlyList<GroupLayout> groups)
        {
            _groups = groups;
        }

        public IReadOnlyList<Extent> Allocate(uint blockCount)
        {
            var result = new List<Extent>();
            uint logical = 0;
            var remaining = blockCount;
            while (remaining > 0)
            {
                while (_groupIndex < _groups.Count && _groups[_groupIndex].RemainingBlocks == 0)
                {
                    _groupIndex++;
                }

                if (_groupIndex >= _groups.Count)
                {
                    throw new IOException("ext4初期ファイルを格納する空きblockがありません。");
                }

                var group = _groups[_groupIndex];
                var count = Math.Min(remaining, Math.Min(group.RemainingBlocks, 32768U));
                var physical = group.Allocate(count);
                result.Add(new Extent(logical, count, physical));
                logical = checked(logical + count);
                remaining -= count;
            }

            return result;
        }
    }

    private sealed class GroupLayout
    {
        private uint _nextFreeBlock;

        public GroupLayout(
            uint number,
            uint startBlock,
            uint blockCount,
            bool hasSuperBlock,
            uint blockBitmap,
            uint inodeBitmap,
            uint inodeTable,
            uint firstDataBlock)
        {
            Number = number;
            StartBlock = startBlock;
            BlockCount = blockCount;
            HasSuperBlock = hasSuperBlock;
            BlockBitmap = blockBitmap;
            InodeBitmap = inodeBitmap;
            InodeTable = inodeTable;
            _nextFreeBlock = firstDataBlock;
            for (var block = startBlock; block < firstDataBlock; block++)
            {
                AllocatedBlocks.Add(block);
            }
        }

        public uint Number { get; }
        public uint StartBlock { get; }
        public uint BlockCount { get; }
        public bool HasSuperBlock { get; }
        public uint BlockBitmap { get; }
        public uint InodeBitmap { get; }
        public uint InodeTable { get; }
        public uint AllocatedInodes { get; set; }
        public uint UsedDirectories { get; set; }
        public HashSet<uint> AllocatedBlocks { get; } = [];
        public uint RemainingBlocks => checked(StartBlock + BlockCount - _nextFreeBlock);
        public uint FreeBlocks => checked(BlockCount - (uint)AllocatedBlocks.Count);

        public uint Allocate(uint count)
        {
            if (count == 0 || count > RemainingBlocks)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            var start = _nextFreeBlock;
            for (uint block = 0; block < count; block++)
            {
                AllocatedBlocks.Add(checked(start + block));
            }

            _nextFreeBlock = checked(_nextFreeBlock + count);
            return start;
        }
    }

    private sealed class PlannedFile
    {
        private PlannedFile(uint inodeNumber, string name, string? path, byte[]? content, long length)
        {
            InodeNumber = inodeNumber;
            Name = name;
            Path = path;
            Content = content;
            Length = length;
        }

        public uint InodeNumber { get; }
        public string Name { get; }
        public string? Path { get; }
        public byte[]? Content { get; }
        public long Length { get; }
        public uint Generation { get; set; }
        public List<Extent> Extents { get; } = [];
        public List<uint> ExtentTreeBlocks { get; } = [];

        public static PlannedFile FromBytes(uint inodeNumber, string name, byte[] content) =>
            new(inodeNumber, name, null, content, content.LongLength);

        public static PlannedFile FromPath(uint inodeNumber, string name, string path, long length) =>
            new(inodeNumber, name, path, null, length);

        public Stream OpenRead() => Content is not null
            ? new MemoryStream(Content, writable: false)
            : new FileStream(
                Path!,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    private sealed record Extent(uint LogicalBlock, uint Length, ulong PhysicalBlock);
    private sealed record DirectoryEntry(uint InodeNumber, string Name, byte FileType);
}
