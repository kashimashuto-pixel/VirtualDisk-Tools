using System.Buffers.Binary;
using System.Text;

namespace Qcow2Explorer.Creation;

/// <summary>
/// Creates a compact XFS v5 filesystem without invoking mkfs.xfs.  The initial
/// layout deliberately keeps every allocation-group btree at leaf level and
/// the root directory in short-form so that the experimental writer can edit
/// the resulting filesystem immediately.
/// </summary>
public sealed class ManagedXfsFileSystemFormatter : IVirtualDiskFileSystemFormatter
{
    private const int BlockSize = 4096;
    private const int SectorSize = 512;
    private const int InodeSize = 512;
    private const int InodesPerBlock = BlockSize / InodeSize;
    private const uint AllocationGroupCount = 4;
    private const uint LogAllocationGroup = 2;
    private const uint LogStartBlock = 6;
    private const uint LogBlocks = 16_384;
    private const uint InodeChunkStartBlock = 16;
    private const uint InodeChunkBlocks = 8;
    private const uint FirstDataBlock = InodeChunkStartBlock + InodeChunkBlocks;
    private const uint RootInode = InodeChunkStartBlock * InodesPerBlock;
    private const uint RealtimeBitmapInode = RootInode + 1;
    private const uint RealtimeSummaryInode = RootInode + 2;
    private const int InodeDataForkOffset = 0xb0;
    private const int ShortFormCapacity = InodeSize - InodeDataForkOffset;
    private const uint BnoBtreeMagicV5 = 0x41423342;
    private const uint CountBtreeMagicV5 = 0x41423343;
    private const uint InodeBtreeMagicV5 = 0x49414233;
    private const uint FreeInodeBtreeMagicV5 = 0x46494233;
    private const uint RmapBtreeMagicV5 = 0x524d4233;
    private const ulong FileSystemOwner = unchecked((ulong)-3L);
    private const ulong LogOwner = unchecked((ulong)-4L);
    private const ulong AllocationGroupOwner = unchecked((ulong)-5L);
    private const ulong InodeBtreeOwner = unchecked((ulong)-6L);
    private const ulong InodeChunkOwner = unchecked((ulong)-7L);
    private static readonly byte[] ReadmeContent =
        Encoding.UTF8.GetBytes("Created by Virtual Disk Explorer. This file may be deleted.\n");

    public string Name => "VirtualDisk.Core XFS";

    public bool Supports(VirtualDiskFileSystemKind fileSystem) =>
        fileSystem == VirtualDiskFileSystemKind.Xfs;

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
        if (layout.SizeBytes < 320L * 1024 * 1024 || layout.SizeBytes % BlockSize != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(layout), "XFS容量は320 MiB以上かつ4 KiB境界にしてください。");
        }

        var totalBlocks = checked((ulong)(layout.SizeBytes / BlockSize));
        if (totalBlocks % AllocationGroupCount != 0 || totalBlocks / AllocationGroupCount > uint.MaxValue)
        {
            throw new NotSupportedException("内部XFSフォーマッターは4等分できる最大64 TiBの容量に対応しています。");
        }

        var agBlocks = checked((uint)(totalBlocks / AllocationGroupCount));
        var logEndBlock = checked(LogStartBlock + LogBlocks);
        var logFreeStart = checked(logEndBlock + 6U);
        if (agBlocks <= logFreeStart)
        {
            throw new ArgumentOutOfRangeException(nameof(layout), "内部XFS logとallocation group metadataを格納できる容量がありません。");
        }

        var agBlocksLog2 = CeilingLog2(agBlocks);
        var uuid = Convert.FromHexString(Guid.NewGuid().ToString("N"));
        var files = PlanFiles(initialFiles);
        var rootDirectory = BuildShortFormDirectory(files);
        var allocatedInodes = checked(3 + files.Count);
        if (allocatedInodes > 64)
        {
            throw new NotSupportedException("初期ファイル数が内部XFSフォーマッターのinode chunk上限を超えています。");
        }

        var nextDataBlock = FirstDataBlock;
        foreach (var file in files)
        {
            file.BlockCount = file.Length == 0
                ? 0
                : checked((uint)(((ulong)file.Length + BlockSize - 1) / BlockSize));
            if (file.BlockCount > 0x1fffff)
            {
                throw new NotSupportedException("内部XFSフォーマッターは初期ファイルごとに最大2,097,151 blocksまで対応します。");
            }

            file.StartBlock = file.BlockCount == 0 ? 0 : nextDataBlock;
            nextDataBlock = checked(nextDataBlock + file.BlockCount);
        }

        if (nextDataBlock >= agBlocks)
        {
            throw new NotSupportedException("初期ファイルをallocation group 0の連続領域へ格納できません。初期ファイルを減らしてください。");
        }

        var groups = BuildAllocationGroups(agBlocks, nextDataBlock, files);
        var freeBlocks = groups.Aggregate<GroupLayout, ulong>(0, (sum, group) =>
            checked(sum + group.FreeExtents.Aggregate<FreeExtent, ulong>(0, (value, extent) => value + extent.BlockCount) + 6));
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
            throw new InvalidDataException("XFSフォーマット対象の容量がパーティション定義と一致しません。");
        }

        var superBlock = BuildSuperBlock(
            totalBlocks,
            agBlocks,
            agBlocksLog2,
            uuid,
            layout.VolumeLabel,
            64,
            checked((ulong)(64 - allocatedInodes)),
            freeBlocks);
        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var baseOffset = checked((long)group.Number * agBlocks * BlockSize);
            WriteAt(stream, baseOffset, superBlock);
            WriteAt(stream, baseOffset + SectorSize, BuildAgf(group, uuid));
            WriteAt(stream, baseOffset + 2L * SectorSize, BuildAgi(group, uuid, allocatedInodes));
            WriteAt(stream, baseOffset + 3L * SectorSize, BuildAgfl(group, uuid));
            WriteAt(stream, baseOffset + 1L * BlockSize, BuildFreeSpaceTree(group, uuid, agBlocks, BnoBtreeMagicV5, byLength: false));
            WriteAt(stream, baseOffset + 2L * BlockSize, BuildFreeSpaceTree(group, uuid, agBlocks, CountBtreeMagicV5, byLength: true));
            WriteAt(stream, baseOffset + 3L * BlockSize, BuildInodeTree(group, uuid, agBlocks, InodeBtreeMagicV5, allocatedInodes));
            WriteAt(stream, baseOffset + 4L * BlockSize, BuildInodeTree(group, uuid, agBlocks, FreeInodeBtreeMagicV5, allocatedInodes));
            WriteAt(stream, baseOffset + 5L * BlockSize, BuildRmapTree(group, uuid, agBlocks));
        }

        WriteAt(stream, checked((long)InodeChunkStartBlock * BlockSize), BuildInodeChunk(uuid, files, rootDirectory, now));
        WriteAt(
            stream,
            checked(((long)LogAllocationGroup * agBlocks + LogStartBlock) * BlockSize),
            BuildCleanLog(uuid));

        foreach (var file in files.Where(candidate => candidate.BlockCount > 0))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await WriteFileContentAsync(stream, file, cancellationToken);
        }

        await stream.FlushAsync(cancellationToken);
        stream.Flush(flushToDisk: true);
    }

    private static List<PlannedFile> PlanFiles(IReadOnlyList<VirtualDiskInitialFile> initialFiles)
    {
        var result = new List<PlannedFile>(initialFiles.Count + 1)
        {
            PlannedFile.FromBytes(RootInode + 3, "VDT-README.txt", ReadmeContent),
        };
        for (var index = 0; index < initialFiles.Count; index++)
        {
            var initialFile = initialFiles[index];
            var info = new FileInfo(initialFile.SourcePath);
            if (!info.Exists)
            {
                throw new FileNotFoundException("初期配置するファイルが見つかりません。", info.FullName);
            }

            result.Add(PlannedFile.FromPath(
                checked(RootInode + 4U + (uint)index),
                initialFile.DestinationName,
                info.FullName,
                info.Length));
        }

        return result;
    }

    private static byte[] BuildShortFormDirectory(IReadOnlyList<PlannedFile> files)
    {
        var data = new byte[ShortFormCapacity];
        data[0] = checked((byte)files.Count);
        data[1] = 0;
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(2, 4), RootInode);
        var cursor = 6;
        ushort dataOffset = 0x60;
        foreach (var file in files)
        {
            var name = Encoding.UTF8.GetBytes(file.Name);
            var required = checked(3 + name.Length + 1 + 4);
            if (name.Length is 0 or > 255 || cursor + required > data.Length)
            {
                throw new NotSupportedException("初期ファイル名をXFS short-form root directoryへ格納できません。ファイル数または名前を減らしてください。");
            }

            data[cursor] = checked((byte)name.Length);
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(cursor + 1, 2), dataOffset);
            name.CopyTo(data, cursor + 3);
            var entryCursor = cursor + 3 + name.Length;
            data[entryCursor++] = 1;
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(entryCursor, 4), file.InodeNumber);
            cursor += required;
            dataOffset = checked((ushort)(dataOffset + AlignUp(checked(12 + name.Length), 8)));
        }

        Array.Resize(ref data, cursor);
        return data;
    }

    private static GroupLayout[] BuildAllocationGroups(
        uint agBlocks,
        uint nextDataBlock,
        IReadOnlyList<PlannedFile> files)
    {
        var result = new GroupLayout[AllocationGroupCount];
        for (uint number = 0; number < AllocationGroupCount; number++)
        {
            var freeExtents = number switch
            {
                0 => new[] { new FreeExtent(12, 4), new FreeExtent(nextDataBlock, agBlocks - nextDataBlock) },
                LogAllocationGroup => new[] { new FreeExtent(LogStartBlock + LogBlocks + 6, agBlocks - LogStartBlock - LogBlocks - 6) },
                _ => new[] { new FreeExtent(12, agBlocks - 12) },
            };
            var agflStart = number == LogAllocationGroup ? LogStartBlock + LogBlocks : 6U;
            var rmap = new List<RmapRecord>
            {
                new(0, 1, FileSystemOwner, 0),
                new(1, 2, AllocationGroupOwner, 0),
                new(3, 2, InodeBtreeOwner, 0),
            };
            if (number == LogAllocationGroup)
            {
                rmap.Add(new RmapRecord(5, 1, AllocationGroupOwner, 0));
                rmap.Add(new RmapRecord(LogStartBlock, LogBlocks, LogOwner, 0));
                rmap.Add(new RmapRecord(agflStart, 6, AllocationGroupOwner, 0));
            }
            else
            {
                rmap.Add(new RmapRecord(5, 7, AllocationGroupOwner, 0));
            }

            if (number == 0)
            {
                rmap.Add(new RmapRecord(InodeChunkStartBlock, InodeChunkBlocks, InodeChunkOwner, 0));
                rmap.AddRange(files
                    .Where(file => file.BlockCount > 0)
                    .Select(file => new RmapRecord(file.StartBlock, file.BlockCount, file.InodeNumber, 0)));
            }

            result[number] = new GroupLayout(number, agBlocks, agflStart, freeExtents, rmap);
        }

        return result;
    }

    private static byte[] BuildSuperBlock(
        ulong totalBlocks,
        uint agBlocks,
        byte agBlocksLog2,
        byte[] uuid,
        string volumeLabel,
        ulong inodeCount,
        ulong freeInodes,
        ulong freeBlocks)
    {
        var data = new byte[SectorSize];
        WriteUInt32Big(data, 0x00, 0x58465342);
        WriteUInt32Big(data, 0x04, BlockSize);
        WriteUInt64Big(data, 0x08, totalBlocks);
        uuid.CopyTo(data, 0x20);
        WriteUInt64Big(data, 0x30, ((ulong)LogAllocationGroup << agBlocksLog2) | LogStartBlock);
        WriteUInt64Big(data, 0x38, RootInode);
        WriteUInt64Big(data, 0x40, RealtimeBitmapInode);
        WriteUInt64Big(data, 0x48, RealtimeSummaryInode);
        WriteUInt32Big(data, 0x50, 1);
        WriteUInt32Big(data, 0x54, agBlocks);
        WriteUInt32Big(data, 0x58, AllocationGroupCount);
        WriteUInt32Big(data, 0x60, LogBlocks);
        WriteUInt16Big(data, 0x64, 0xb4a5);
        WriteUInt16Big(data, 0x66, SectorSize);
        WriteUInt16Big(data, 0x68, InodeSize);
        WriteUInt16Big(data, 0x6a, InodesPerBlock);
        var label = Encoding.UTF8.GetBytes(volumeLabel);
        label.AsSpan(0, Math.Min(label.Length, 12)).CopyTo(data.AsSpan(0x6c, 12));
        data[0x78] = 12;
        data[0x79] = 9;
        data[0x7a] = 9;
        data[0x7b] = 3;
        data[0x7c] = agBlocksLog2;
        data[0x7f] = 25;
        WriteUInt64Big(data, 0x80, inodeCount);
        WriteUInt64Big(data, 0x88, freeInodes);
        WriteUInt64Big(data, 0x90, freeBlocks);
        WriteUInt32Big(data, 0xb4, 8);
        WriteUInt32Big(data, 0xc4, 1);
        WriteUInt32Big(data, 0xc8, 0x0000018a);
        WriteUInt32Big(data, 0xcc, 0x0000018a);
        WriteUInt32Big(data, 0xd4, 0x0000000b);
        WriteUInt32Big(data, 0xd8, 0x00000003);
        WriteUInt32Big(data, 0xe4, 4);
        UpdateChecksum(data, 0xe0);
        return data;
    }

    private static byte[] BuildAgf(GroupLayout group, byte[] uuid)
    {
        var data = new byte[SectorSize];
        WriteUInt32Big(data, 0x00, 0x58414746);
        WriteUInt32Big(data, 0x04, 1);
        WriteUInt32Big(data, 0x08, group.Number);
        WriteUInt32Big(data, 0x0c, group.BlockCount);
        WriteUInt32Big(data, 0x10, 1);
        WriteUInt32Big(data, 0x14, 2);
        WriteUInt32Big(data, 0x18, 5);
        WriteUInt32Big(data, 0x1c, 1);
        WriteUInt32Big(data, 0x20, 1);
        WriteUInt32Big(data, 0x24, 1);
        WriteUInt32Big(data, 0x28, 1);
        WriteUInt32Big(data, 0x2c, 6);
        WriteUInt32Big(data, 0x30, 6);
        var freeBlocks = group.FreeExtents.Aggregate<FreeExtent, ulong>(0, (sum, extent) => sum + extent.BlockCount);
        WriteUInt32Big(data, 0x34, checked((uint)freeBlocks));
        WriteUInt32Big(data, 0x38, group.FreeExtents.Max(extent => extent.BlockCount));
        uuid.CopyTo(data, 0x40);
        WriteUInt32Big(data, 0x50, 1);
        UpdateChecksum(data, 0xd8);
        return data;
    }

    private static byte[] BuildAgi(GroupLayout group, byte[] uuid, int allocatedInodes)
    {
        var data = new byte[SectorSize];
        WriteUInt32Big(data, 0x00, 0x58414749);
        WriteUInt32Big(data, 0x04, 1);
        WriteUInt32Big(data, 0x08, group.Number);
        WriteUInt32Big(data, 0x0c, group.BlockCount);
        WriteUInt32Big(data, 0x10, group.Number == 0 ? 64U : 0U);
        WriteUInt32Big(data, 0x14, 3);
        WriteUInt32Big(data, 0x18, 1);
        WriteUInt32Big(data, 0x1c, group.Number == 0 ? checked((uint)(64 - allocatedInodes)) : 0U);
        WriteUInt32Big(data, 0x20, group.Number == 0 ? RootInode : uint.MaxValue);
        WriteUInt32Big(data, 0x24, uint.MaxValue);
        data.AsSpan(0x28, 64 * 4).Fill(0xff);
        uuid.CopyTo(data, 0x128);
        WriteUInt32Big(data, 0x148, 4);
        WriteUInt32Big(data, 0x14c, 1);
        WriteUInt32Big(data, 0x150, 1);
        WriteUInt32Big(data, 0x154, 1);
        UpdateChecksum(data, 0x138);
        return data;
    }

    private static byte[] BuildAgfl(GroupLayout group, byte[] uuid)
    {
        var data = new byte[SectorSize];
        data.AsSpan().Fill(0xff);
        WriteUInt32Big(data, 0x00, 0x5841464c);
        WriteUInt32Big(data, 0x04, group.Number);
        uuid.CopyTo(data, 0x08);
        data.AsSpan(0x18, 8).Clear();
        data.AsSpan(0x20, 4).Clear();
        WriteUInt32Big(data, 0x24, uint.MaxValue);
        for (uint index = 0; index < 6; index++)
        {
            WriteUInt32Big(data, checked(0x28 + (int)index * 4), checked(group.AgflStartBlock + index));
        }

        UpdateChecksum(data, 0x20);
        return data;
    }

    private static byte[] BuildFreeSpaceTree(
        GroupLayout group,
        byte[] uuid,
        uint agBlocks,
        uint magic,
        bool byLength)
    {
        var records = byLength
            ? group.FreeExtents.OrderBy(extent => extent.BlockCount).ThenBy(extent => extent.StartBlock)
            : group.FreeExtents.OrderBy(extent => extent.StartBlock);
        var data = BuildBtreeHeader(group.Number, byLength ? 2U : 1U, uuid, agBlocks, magic, checked((ushort)group.FreeExtents.Count));
        var offset = 56;
        foreach (var record in records)
        {
            WriteUInt32Big(data, offset, record.StartBlock);
            WriteUInt32Big(data, offset + 4, record.BlockCount);
            offset += 8;
        }

        UpdateChecksum(data, 0x34);
        return data;
    }

    private static byte[] BuildInodeTree(
        GroupLayout group,
        byte[] uuid,
        uint agBlocks,
        uint magic,
        int allocatedInodes)
    {
        var recordCount = group.Number == 0 ? (ushort)1 : (ushort)0;
        var data = BuildBtreeHeader(group.Number, magic == InodeBtreeMagicV5 ? 3U : 4U, uuid, agBlocks, magic, recordCount);
        if (group.Number == 0)
        {
            var freeCount = checked((byte)(64 - allocatedInodes));
            var freeMask = allocatedInodes == 64 ? 0UL : ulong.MaxValue << allocatedInodes;
            WriteUInt32Big(data, 0x38, RootInode);
            WriteUInt16Big(data, 0x3c, 0);
            data[0x3e] = 64;
            data[0x3f] = freeCount;
            WriteUInt64Big(data, 0x40, freeMask);
        }

        UpdateChecksum(data, 0x34);
        return data;
    }

    private static byte[] BuildRmapTree(GroupLayout group, byte[] uuid, uint agBlocks)
    {
        var records = group.RmapRecords
            .OrderBy(record => record.StartBlock)
            .ThenBy(record => record.Owner)
            .ThenBy(record => record.Offset)
            .ToArray();
        var data = BuildBtreeHeader(group.Number, 5, uuid, agBlocks, RmapBtreeMagicV5, checked((ushort)records.Length));
        var offset = 56;
        foreach (var record in records)
        {
            WriteUInt32Big(data, offset, record.StartBlock);
            WriteUInt32Big(data, offset + 4, record.BlockCount);
            WriteUInt64Big(data, offset + 8, record.Owner);
            WriteUInt64Big(data, offset + 16, record.Offset);
            offset += 24;
        }

        UpdateChecksum(data, 0x34);
        return data;
    }

    private static byte[] BuildBtreeHeader(
        uint agNumber,
        uint agBlock,
        byte[] uuid,
        uint agBlocks,
        uint magic,
        ushort recordCount)
    {
        var data = new byte[BlockSize];
        WriteUInt32Big(data, 0x00, magic);
        WriteUInt16Big(data, 0x04, 0);
        WriteUInt16Big(data, 0x06, recordCount);
        WriteUInt32Big(data, 0x08, uint.MaxValue);
        WriteUInt32Big(data, 0x0c, uint.MaxValue);
        WriteUInt64Big(data, 0x10, checked(((ulong)agNumber * agBlocks + agBlock) * (BlockSize / SectorSize)));
        uuid.CopyTo(data, 0x20);
        WriteUInt32Big(data, 0x30, agNumber);
        return data;
    }

    private static byte[] BuildInodeChunk(
        byte[] uuid,
        IReadOnlyList<PlannedFile> files,
        byte[] rootDirectory,
        uint now)
    {
        var data = new byte[64 * InodeSize];
        for (uint slot = 0; slot < 64; slot++)
        {
            var inodeNumber = checked(RootInode + slot);
            var inode = BuildFreeInode(inodeNumber, uuid);
            inode.CopyTo(data, checked((int)slot * InodeSize));
        }

        BuildRootInode(uuid, rootDirectory, now).CopyTo(data, 0);
        BuildRealtimeInode(RealtimeBitmapInode, uuid, now, isBitmap: true).CopyTo(data, InodeSize);
        BuildRealtimeInode(RealtimeSummaryInode, uuid, now, isBitmap: false).CopyTo(data, 2 * InodeSize);
        foreach (var file in files)
        {
            var inode = BuildFileInode(file, uuid, now);
            inode.CopyTo(data, checked((int)(file.InodeNumber - RootInode) * InodeSize));
        }

        return data;
    }

    private static byte[] BuildFreeInode(uint inodeNumber, byte[] uuid)
    {
        var data = new byte[InodeSize];
        WriteUInt16Big(data, 0x00, 0x494e);
        data[0x04] = 3;
        WriteUInt32Big(data, 0x60, uint.MaxValue);
        WriteUInt64Big(data, 0x98, inodeNumber);
        uuid.CopyTo(data, 0xa0);
        UpdateChecksum(data, 0x64);
        return data;
    }

    private static byte[] BuildRootInode(byte[] uuid, byte[] directory, uint now)
    {
        var data = BuildAllocatedInode(RootInode, uuid, 0x41ed, format: 1, linkCount: 2, now);
        WriteUInt64Big(data, 0x38, checked((ulong)directory.Length));
        WriteUInt64Big(data, 0x68, 2);
        directory.CopyTo(data, InodeDataForkOffset);
        UpdateChecksum(data, 0x64);
        return data;
    }

    private static byte[] BuildRealtimeInode(uint inodeNumber, byte[] uuid, uint now, bool isBitmap)
    {
        var data = BuildAllocatedInode(inodeNumber, uuid, 0x8000, format: 2, linkCount: 1, now);
        if (isBitmap)
        {
            WriteUInt16Big(data, 0x5a, 0x0004);
        }

        UpdateChecksum(data, 0x64);
        return data;
    }

    private static byte[] BuildFileInode(PlannedFile file, byte[] uuid, uint now)
    {
        var data = BuildAllocatedInode(file.InodeNumber, uuid, 0x81a4, format: 2, linkCount: 1, now);
        WriteUInt64Big(data, 0x38, checked((ulong)file.Length));
        WriteUInt64Big(data, 0x40, file.BlockCount);
        WriteUInt32Big(data, 0x4c, file.BlockCount == 0 ? 0U : 1U);
        if (file.BlockCount > 0)
        {
            WriteExtent(data.AsSpan(InodeDataForkOffset, 16), file.StartBlock, file.BlockCount);
        }

        UpdateChecksum(data, 0x64);
        return data;
    }

    private static byte[] BuildAllocatedInode(
        uint inodeNumber,
        byte[] uuid,
        ushort mode,
        byte format,
        uint linkCount,
        uint now)
    {
        var data = new byte[InodeSize];
        WriteUInt16Big(data, 0x00, 0x494e);
        WriteUInt16Big(data, 0x02, mode);
        data[0x04] = 3;
        data[0x05] = format;
        WriteUInt32Big(data, 0x10, linkCount);
        WriteTimestamp(data, 0x20, now);
        WriteTimestamp(data, 0x28, now);
        WriteTimestamp(data, 0x30, now);
        data[0x53] = 2;
        WriteUInt32Big(data, 0x5c, NextGeneration());
        WriteUInt32Big(data, 0x60, uint.MaxValue);
        WriteUInt64Big(data, 0x68, 1);
        WriteTimestamp(data, 0x90, now);
        WriteUInt64Big(data, 0x98, inodeNumber);
        uuid.CopyTo(data, 0xa0);
        return data;
    }

    private static byte[] BuildCleanLog(byte[] uuid)
    {
        var data = new byte[2 * SectorSize];
        WriteUInt32Big(data, 0x00, 0xfeedbabe);
        WriteUInt32Big(data, 0x04, 1);
        WriteUInt32Big(data, 0x08, 2);
        WriteUInt32Big(data, 0x0c, SectorSize);
        WriteUInt64Big(data, 0x10, 0x0000000100000000);
        WriteUInt64Big(data, 0x18, 0x0000000100000000);
        WriteUInt32Big(data, 0x24, uint.MaxValue);
        WriteUInt32Big(data, 0x28, 1);
        WriteUInt32Big(data, 0x2c, 0xb0c0d0d0);
        WriteUInt32Big(data, 0x12c, 1);
        uuid.CopyTo(data, 0x130);
        WriteUInt32Big(data, 0x140, 32 * 1024);
        WriteUInt32Big(data, SectorSize + 0x00, 1);
        WriteUInt32Big(data, SectorSize + 0x04, 8);
        data[SectorSize + 0x08] = 0xaa;
        data[SectorSize + 0x09] = 0x20;
        WriteUInt16Big(data, SectorSize + 0x0c, 0x6e55);
        return data;
    }

    private static async Task WriteFileContentAsync(
        FileStream destination,
        PlannedFile file,
        CancellationToken cancellationToken)
    {
        var offset = checked((long)file.StartBlock * BlockSize);
        if (file.Bytes is not null)
        {
            await RandomAccess.WriteAsync(destination.SafeFileHandle, file.Bytes, offset, cancellationToken);
            return;
        }

        await using var source = new FileStream(
            file.SourcePath!,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[1024 * 1024];
        long written = 0;
        while (written < file.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = checked((int)Math.Min(buffer.Length, file.Length - written));
            await source.ReadExactlyAsync(buffer.AsMemory(0, count), cancellationToken);
            await RandomAccess.WriteAsync(
                destination.SafeFileHandle,
                buffer.AsMemory(0, count),
                checked(offset + written),
                cancellationToken);
            written += count;
        }
    }

    private static void WriteExtent(Span<byte> destination, uint agBlock, uint blockCount)
    {
        var startBlock = agBlock;
        if (blockCount == 0 || blockCount > 0x1fffff)
        {
            throw new ArgumentOutOfRangeException(nameof(blockCount));
        }

        var high = startBlock >> 43;
        var low = (startBlock << 21) | blockCount;
        BinaryPrimitives.WriteUInt64BigEndian(destination[..8], high);
        BinaryPrimitives.WriteUInt64BigEndian(destination[8..16], low);
    }

    private static void WriteTimestamp(byte[] data, int offset, uint seconds)
    {
        WriteUInt32Big(data, offset, seconds);
        WriteUInt32Big(data, offset + 4, 0);
    }

    private static uint NextGeneration()
    {
        Span<byte> bytes = stackalloc byte[4];
        Random.Shared.NextBytes(bytes);
        var value = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        return value == 0 ? 1U : value;
    }

    private static byte CeilingLog2(uint value)
    {
        byte result = 0;
        var candidate = value - 1;
        while (candidate > 0)
        {
            result++;
            candidate >>= 1;
        }

        return result;
    }

    private static int AlignUp(int value, int alignment) =>
        checked((value + alignment - 1) / alignment * alignment);

    private static void UpdateChecksum(byte[] data, int checksumOffset)
    {
        data.AsSpan(checksumOffset, 4).Clear();
        var checksum = ~ComputeCrc32C(uint.MaxValue, data);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(checksumOffset, 4), checksum);
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
                checksum = (checksum >> 1) ^ ((checksum & 1) == 0 ? 0 : polynomial);
            }
        }

        return checksum;
    }

    private static void WriteAt(FileStream stream, long offset, ReadOnlySpan<byte> data) =>
        RandomAccess.Write(stream.SafeFileHandle, data, offset);

    private static void WriteUInt16Big(byte[] data, int offset, ushort value) =>
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset, 2), value);

    private static void WriteUInt32Big(byte[] data, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset, 4), value);

    private static void WriteUInt64Big(byte[] data, int offset, ulong value) =>
        BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(offset, 8), value);

    private static void EnsureSupported(VirtualDiskFileSystemKind fileSystem)
    {
        if (fileSystem != VirtualDiskFileSystemKind.Xfs)
        {
            throw new NotSupportedException("内部XFSフォーマッターはXFSだけに対応します。");
        }
    }

    private sealed record FreeExtent(uint StartBlock, uint BlockCount);

    private sealed record RmapRecord(uint StartBlock, uint BlockCount, ulong Owner, ulong Offset);

    private sealed record GroupLayout(
        uint Number,
        uint BlockCount,
        uint AgflStartBlock,
        IReadOnlyList<FreeExtent> FreeExtents,
        IReadOnlyList<RmapRecord> RmapRecords);

    private sealed class PlannedFile
    {
        private PlannedFile(uint inodeNumber, string name, byte[]? bytes, string? sourcePath, long length)
        {
            InodeNumber = inodeNumber;
            Name = name;
            Bytes = bytes;
            SourcePath = sourcePath;
            Length = length;
        }

        public uint InodeNumber { get; }
        public string Name { get; }
        public byte[]? Bytes { get; }
        public string? SourcePath { get; }
        public long Length { get; }
        public uint StartBlock { get; set; }
        public uint BlockCount { get; set; }

        public static PlannedFile FromBytes(uint inodeNumber, string name, byte[] bytes) =>
            new(inodeNumber, name, bytes, null, bytes.LongLength);

        public static PlannedFile FromPath(uint inodeNumber, string name, string sourcePath, long length) =>
            new(inodeNumber, name, null, sourcePath, length);
    }
}
