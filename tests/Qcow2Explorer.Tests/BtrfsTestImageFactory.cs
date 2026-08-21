using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using ZstdSharp;

internal static class BtrfsTestImageFactory
{
    public const int DiskSize = 32 * 1024 * 1024;
    public const int PartitionStart = 1024 * 1024;
    public const int SuperblockLogicalOffset = 64 * 1024;
    public const int FileSystemTreeLogicalOffset = 1024 * 1024 + 2 * NodeSize;
    public const int RegularDataLogicalOffset = 2 * 1024 * 1024;
    public const string HelloText = "Hello from synthetic Btrfs\n";
    public const string SparseTail = "sparse-tail\n";
    public const int SparseTailOffset = 1024 * 1024;

    private const int SectorSize = 4096;
    private const int NodeSize = 4096;
    private const int BackupSuperblockLogicalOffset = 64 * 1024 * 1024;
    private const int TreeHeaderSize = 101;
    private const int ChunkTreeLogicalOffset = 1024 * 1024;
    private const int RootTreeLogicalOffset = ChunkTreeLogicalOffset + NodeSize;
    private const int ChecksumTreeLogicalOffset = ChunkTreeLogicalOffset + 3 * NodeSize;
    private const int SubvolumeTreeLogicalOffset = ChunkTreeLogicalOffset + 4 * NodeSize;
    private const int NestedSubvolumeTreeLogicalOffset = ChunkTreeLogicalOffset + 5 * NodeSize;
    private const int SnapshotTreeLogicalOffset = ChunkTreeLogicalOffset + 6 * NodeSize;
    private const int SparseDataLogicalOffset = RegularDataLogicalOffset + 2 * SectorSize;
    private const int ZlibDataLogicalOffset = RegularDataLogicalOffset + 3 * SectorSize;
    private const int LzoDataLogicalOffset = RegularDataLogicalOffset + 4 * SectorSize;
    private const int ZstdDataLogicalOffset = RegularDataLogicalOffset + 5 * SectorSize;
    private const ulong Generation = 1;

    public static byte[] RegularData { get; } = Enumerable.Range(0, 6000)
        .Select(index => checked((byte)((index * 73 + 19) & 0xff)))
        .ToArray();

    public static BtrfsTestFixture Create(
        string path,
        bool corruptZlibPayload = false,
        bool corruptLzoPayload = false,
        bool corruptLzoHeader = false,
        bool corruptZstdPayload = false,
        bool corruptZstdPadding = false,
        bool includeSubvolumes = false,
        bool defaultSubvolume = false,
        bool corruptRootBackReference = false,
        bool corruptSubvolumeGeneration = false,
        bool includeBackupSuperblock = false,
        bool corruptPrimarySuperblock = false,
        bool corruptPrimaryMagic = false,
        bool corruptBackupSuperblock = false,
        bool newerBackupGeneration = false)
    {
        if (defaultSubvolume && !includeSubvolumes)
        {
            throw new ArgumentException("A default subvolume requires subvolume fixtures.", nameof(defaultSubvolume));
        }

        if ((corruptPrimarySuperblock || corruptPrimaryMagic || corruptBackupSuperblock || newerBackupGeneration)
            && !includeBackupSuperblock)
        {
            throw new ArgumentException("Backup corruption options require a backup superblock fixture.");
        }

        var diskSize = includeBackupSuperblock
            ? PartitionStart + BackupSuperblockLogicalOffset + 2 * 1024 * 1024
            : DiskSize;
        var disk = new byte[diskSize];
        var partitionLength = diskSize - PartitionStart;
        WriteMbr(disk, partitionLength);

        var fsid = Guid.Parse("8e0a61a3-fbcb-4a76-b16e-777579eecdc6").ToByteArray();
        var chunkUuid = Guid.Parse("f61c47e6-f8c5-44d8-971c-d48e90f5a325").ToByteArray();
        var deviceUuid = Guid.Parse("56ca9a70-b88c-4ba2-855a-e2ac5bec938c").ToByteArray();
        var chunk = CreateChunk(partitionLength, deviceId: 1, deviceUuid);
        var deviceItem = CreateDeviceItem(1, partitionLength, deviceUuid, fsid);

        var superblock = CreateSuperblock(
            partitionLength,
            partitionLength,
            numberOfDevices: 1,
            deviceId: 1,
            fsid,
            deviceUuid,
            chunk,
            defaultSubvolume);
        superblock.CopyTo(disk, PartitionStart + SuperblockLogicalOffset);

        var chunkTree = CreateLeaf(
            ChunkTreeLogicalOffset,
            owner: 3,
            fsid,
            chunkUuid,
            [
                (new BtrfsKey(1, 216, 1), deviceItem),
                (new BtrfsKey(256, 228, 0), chunk),
            ]);
        chunkTree.CopyTo(disk, PartitionStart + ChunkTreeLogicalOffset);

        var rootTreeItems = new List<(BtrfsKey Key, byte[] Data)>
        {
            (new BtrfsKey(5, 132, 0), CreateRootItem(256, FileSystemTreeLogicalOffset)),
            (new BtrfsKey(7, 132, 0), CreateRootItem(0, ChecksumTreeLogicalOffset)),
        };
        if (includeSubvolumes)
        {
            rootTreeItems.Add((new BtrfsKey(256, 132, 0), CreateRootItem(
                256,
                SubvolumeTreeLogicalOffset,
                corruptSubvolumeGeneration ? Generation + 1 : Generation)));
            rootTreeItems.Add((new BtrfsKey(257, 132, 0), CreateRootItem(256, NestedSubvolumeTreeLogicalOffset)));
            rootTreeItems.Add((new BtrfsKey(258, 132, Generation), CreateRootItem(256, SnapshotTreeLogicalOffset)));
            rootTreeItems.Add((new BtrfsKey(5, 156, 256), CreateRootReference(256, 3, "subvol")));
            rootTreeItems.Add((new BtrfsKey(256, 144, 5), CreateRootReference(
                256,
                3,
                corruptRootBackReference ? "broken" : "subvol")));
            rootTreeItems.Add((new BtrfsKey(256, 156, 257), CreateRootReference(256, 3, "nested-subvol")));
            rootTreeItems.Add((new BtrfsKey(257, 144, 256), CreateRootReference(256, 3, "nested-subvol")));
            rootTreeItems.Add((new BtrfsKey(5, 156, 258), CreateRootReference(256, 4, "snapshot")));
            rootTreeItems.Add((new BtrfsKey(258, 144, 5), CreateRootReference(256, 4, "snapshot")));
            if (defaultSubvolume)
            {
                rootTreeItems.Add((new BtrfsKey(6, 84, 0), CreateDirectoryEntries((256, 132, 2, "default"))));
            }
        }

        var rootTree = CreateLeaf(
            RootTreeLogicalOffset,
            owner: 1,
            fsid,
            chunkUuid,
            rootTreeItems);
        rootTree.CopyTo(disk, PartitionStart + RootTreeLogicalOffset);

        var helloBytes = Encoding.UTF8.GetBytes(HelloText);
        var sparseTailBytes = Encoding.UTF8.GetBytes(SparseTail);
        var inlineLzoData = CompressBtrfsLzoZeros(1024, padToSector: false);
        var inlineZstdData = CompressZstdZeros(1024, padToSector: false);
        var fileSystemTreeItems = new List<(BtrfsKey Key, byte[] Data)>
        {
                (new BtrfsKey(256, 1, 0), CreateInode(0, 0x41ed)),
                (new BtrfsKey(256, 96, 2), CreateDirectoryEntries(
                    (257, 1, 1, "hello.txt"),
                    (258, 1, 1, "regular.bin"),
                    (259, 1, 2, "nested"),
                    (261, 1, 1, "sparse.bin"),
                    (262, 1, 1, "zlib.bin"),
                    (263, 1, 1, "lzo.bin"),
                    (264, 1, 1, "inline-lzo.bin"),
                    (265, 1, 1, "zstd.bin"),
                    (266, 1, 1, "inline-zstd.bin"))),
                (new BtrfsKey(257, 1, 0), CreateInode(helloBytes.Length, 0x81a4)),
                (new BtrfsKey(257, 108, 0), CreateInlineExtent(helloBytes)),
                (new BtrfsKey(258, 1, 0), CreateInode(RegularData.Length, 0x81a4)),
                (new BtrfsKey(258, 108, 0), CreateRegularExtent(RegularDataLogicalOffset, 2 * SectorSize, RegularData.Length)),
                (new BtrfsKey(259, 1, 0), CreateInode(0, 0x41ed)),
                (new BtrfsKey(259, 96, 2), CreateDirectoryEntries((260, 1, 1, "inside.txt"))),
                (new BtrfsKey(260, 1, 0), CreateInode(helloBytes.Length, 0x81a4)),
                (new BtrfsKey(260, 108, 0), CreateInlineExtent(helloBytes)),
                (new BtrfsKey(261, 1, 0), CreateInode(SparseTailOffset + sparseTailBytes.Length, 0x81a4)),
                (new BtrfsKey(261, 108, SparseTailOffset), CreateRegularExtent(SparseDataLogicalOffset, SectorSize, sparseTailBytes.Length)),
                (new BtrfsKey(262, 1, 0), CreateInode(128 * 1024, 0x81a4)),
                (new BtrfsKey(262, 108, 0), CreateRegularExtent(
                    ZlibDataLogicalOffset,
                    SectorSize,
                    128 * 1024,
                    compression: 1,
                    ramBytes: 128 * 1024)),
                (new BtrfsKey(263, 1, 0), CreateInode(128 * 1024, 0x81a4)),
                (new BtrfsKey(263, 108, 0), CreateRegularExtent(
                    LzoDataLogicalOffset,
                    SectorSize,
                    128 * 1024,
                    compression: 2,
                    ramBytes: 128 * 1024)),
                (new BtrfsKey(264, 1, 0), CreateInode(1024, 0x81a4)),
                (new BtrfsKey(264, 108, 0), CreateCompressedInlineExtent(inlineLzoData, 1024, compression: 2)),
                (new BtrfsKey(265, 1, 0), CreateInode(128 * 1024, 0x81a4)),
                (new BtrfsKey(265, 108, 0), CreateRegularExtent(
                    ZstdDataLogicalOffset,
                    SectorSize,
                    128 * 1024,
                    compression: 3,
                    ramBytes: 128 * 1024)),
                (new BtrfsKey(266, 1, 0), CreateInode(1024, 0x81a4)),
                (new BtrfsKey(266, 108, 0), CreateCompressedInlineExtent(inlineZstdData, 1024, compression: 3))
        };
        if (includeSubvolumes)
        {
            fileSystemTreeItems.Add((new BtrfsKey(256, 96, 3), CreateDirectoryEntries((256, 132, 2, "subvol"))));
            fileSystemTreeItems.Add((new BtrfsKey(256, 96, 4), CreateDirectoryEntries((258, 132, 2, "snapshot"))));
        }

        var fileSystemTree = CreateLeaf(
            FileSystemTreeLogicalOffset,
            owner: 5,
            fsid,
            chunkUuid,
            fileSystemTreeItems);
        fileSystemTree.CopyTo(disk, PartitionStart + FileSystemTreeLogicalOffset);

        if (includeSubvolumes)
        {
            CreateSubvolumeTree(
                SubvolumeTreeLogicalOffset,
                owner: 256,
                fsid,
                chunkUuid,
                "subvolume.txt",
                "inside subvolume\n",
                nestedSubvolumeId: 257,
                nestedSubvolumeName: "nested-subvol")
                .CopyTo(disk, PartitionStart + SubvolumeTreeLogicalOffset);
            CreateSubvolumeTree(
                NestedSubvolumeTreeLogicalOffset,
                owner: 257,
                fsid,
                chunkUuid,
                "nested.txt",
                "inside nested subvolume\n")
                .CopyTo(disk, PartitionStart + NestedSubvolumeTreeLogicalOffset);
            CreateSubvolumeTree(
                SnapshotTreeLogicalOffset,
                owner: 258,
                fsid,
                chunkUuid,
                "snapshot.txt",
                "inside snapshot\n",
                nestedSubvolumeId: 257,
                nestedSubvolumeName: "nested-subvol")
                .CopyTo(disk, PartitionStart + SnapshotTreeLogicalOffset);
        }

        RegularData.CopyTo(disk, PartitionStart + RegularDataLogicalOffset);
        sparseTailBytes.CopyTo(disk, PartitionStart + SparseDataLogicalOffset);
        var zlibData = CompressZlib(new byte[128 * 1024]);
        if (corruptZlibPayload)
        {
            zlibData[0] ^= 1;
        }

        zlibData.CopyTo(disk, PartitionStart + ZlibDataLogicalOffset);
        var lzoData = CompressBtrfsLzoZeros(128 * 1024);
        if (corruptLzoPayload)
        {
            var firstSegmentLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(lzoData.AsSpan(4, 4)));
            lzoData[8 + firstSegmentLength - 1] ^= 1;
        }
        else if (corruptLzoHeader)
        {
            lzoData.AsSpan(0, sizeof(uint)).Clear();
        }

        lzoData.CopyTo(disk, PartitionStart + LzoDataLogicalOffset);
        var zstdData = CompressZstdZeros(128 * 1024, padToSector: true);
        if (corruptZstdPayload)
        {
            zstdData[0] ^= 1;
        }

        if (corruptZstdPadding)
        {
            zstdData[^1] = 1;
        }

        zstdData.CopyTo(disk, PartitionStart + ZstdDataLogicalOffset);
        var checksumBytes = new byte[6 * sizeof(uint)];
        for (var sector = 0; sector < 6; sector++)
        {
            var source = disk.AsSpan(PartitionStart + RegularDataLogicalOffset + sector * SectorSize, SectorSize);
            WriteU32(checksumBytes, sector * sizeof(uint), ComputeCrc32C(source));
        }

        var checksumTree = CreateLeaf(
            ChecksumTreeLogicalOffset,
            owner: 7,
            fsid,
            chunkUuid,
            [(new BtrfsKey(unchecked((ulong)-10L), 128, RegularDataLogicalOffset), checksumBytes)]);
        checksumTree.CopyTo(disk, PartitionStart + ChecksumTreeLogicalOffset);

        if (includeBackupSuperblock)
        {
            var backup = disk.AsSpan(
                PartitionStart + SuperblockLogicalOffset,
                NodeSize).ToArray();
            WriteU64(backup, 0x30, BackupSuperblockLogicalOffset);
            if (newerBackupGeneration)
            {
                WriteU64(backup, 0x48, Generation + 1);
            }

            WriteU32(backup, 0, ComputeCrc32C(backup.AsSpan(32)));
            backup.CopyTo(disk, PartitionStart + BackupSuperblockLogicalOffset);
            if (corruptBackupSuperblock)
            {
                disk[PartitionStart + BackupSuperblockLogicalOffset] ^= 1;
            }

            if (corruptPrimaryMagic)
            {
                disk[PartitionStart + SuperblockLogicalOffset + 0x40] ^= 1;
            }
            else if (corruptPrimarySuperblock)
            {
                disk[PartitionStart + SuperblockLogicalOffset] ^= 1;
            }
        }

        File.WriteAllBytes(path, disk);
        return new BtrfsTestFixture(
            PartitionStart + SuperblockLogicalOffset,
            PartitionStart + FileSystemTreeLogicalOffset,
            PartitionStart + RegularDataLogicalOffset);
    }

    public static BtrfsMultiDeviceTestFixture CreateMultiDevice(
        string firstPath,
        string secondPath,
        bool differentSecondFileSystem = false,
        bool duplicateDeviceId = false,
        bool corruptDataStripeUuid = false)
    {
        _ = Create(firstPath);
        var firstDisk = File.ReadAllBytes(firstPath);
        var secondDisk = new byte[DiskSize];
        var partitionLength = DiskSize - PartitionStart;
        var fileSystemLength = checked(partitionLength * 2);
        WriteMbr(secondDisk, partitionLength);

        var fsid = Guid.Parse("8e0a61a3-fbcb-4a76-b16e-777579eecdc6").ToByteArray();
        var secondFsid = differentSecondFileSystem
            ? Guid.Parse("f20795a2-6ac6-499e-ac81-6062a3cc63b3").ToByteArray()
            : fsid;
        var chunkUuid = Guid.Parse("f61c47e6-f8c5-44d8-971c-d48e90f5a325").ToByteArray();
        var firstDeviceUuid = Guid.Parse("56ca9a70-b88c-4ba2-855a-e2ac5bec938c").ToByteArray();
        var secondDeviceUuid = Guid.Parse("1bf62a80-1b07-451c-9f20-d51717f2ecb3").ToByteArray();
        var stripeUuid = corruptDataStripeUuid
            ? Guid.Parse("cb46b413-15cd-4c47-a10e-0a67f1cc469d").ToByteArray()
            : secondDeviceUuid;
        var secondDeviceId = duplicateDeviceId ? 1UL : 2UL;

        var metadataChunk = CreateChunk(
            RegularDataLogicalOffset,
            deviceId: 1,
            firstDeviceUuid,
            type: 0x6); // SYSTEM | METADATA
        var dataChunk = CreateChunk(
            4 * 1024 * 1024,
            deviceId: 2,
            stripeUuid,
            physicalStart: RegularDataLogicalOffset,
            type: 0x1); // DATA
        var firstDeviceItem = CreateDeviceItem(1, partitionLength, firstDeviceUuid, fsid);
        var secondDeviceItem = CreateDeviceItem(2, partitionLength, secondDeviceUuid, fsid);

        var firstSuperblock = CreateSuperblock(
            fileSystemLength,
            partitionLength,
            numberOfDevices: 2,
            deviceId: 1,
            fsid,
            firstDeviceUuid,
            metadataChunk,
            defaultSubvolume: false);
        firstSuperblock.CopyTo(firstDisk, PartitionStart + SuperblockLogicalOffset);

        var secondSuperblock = CreateSuperblock(
            fileSystemLength,
            partitionLength,
            numberOfDevices: 2,
            deviceId: secondDeviceId,
            secondFsid,
            secondDeviceUuid,
            metadataChunk,
            defaultSubvolume: false);
        secondSuperblock.CopyTo(secondDisk, PartitionStart + SuperblockLogicalOffset);

        var chunkTree = CreateLeaf(
            ChunkTreeLogicalOffset,
            owner: 3,
            fsid,
            chunkUuid,
            [
                (new BtrfsKey(1, 216, 1), firstDeviceItem),
                (new BtrfsKey(1, 216, 2), secondDeviceItem),
                (new BtrfsKey(256, 228, 0), metadataChunk),
                (new BtrfsKey(256, 228, RegularDataLogicalOffset), dataChunk),
            ]);
        chunkTree.CopyTo(firstDisk, PartitionStart + ChunkTreeLogicalOffset);

        firstDisk.AsSpan(
            PartitionStart + RegularDataLogicalOffset,
            4 * 1024 * 1024).CopyTo(secondDisk.AsSpan(PartitionStart + RegularDataLogicalOffset));
        firstDisk.AsSpan(PartitionStart + RegularDataLogicalOffset, 4 * 1024 * 1024).Clear();

        File.WriteAllBytes(firstPath, firstDisk);
        File.WriteAllBytes(secondPath, secondDisk);
        return new BtrfsMultiDeviceTestFixture(firstPath, secondPath);
    }

    private static byte[] CreateSuperblock(
        int fileSystemLength,
        int deviceLength,
        ulong numberOfDevices,
        ulong deviceId,
        byte[] fsid,
        byte[] deviceUuid,
        byte[] chunk,
        bool defaultSubvolume)
    {
        var superblock = new byte[4096];
        fsid.CopyTo(superblock, 0x20);
        WriteU64(superblock, 0x30, SuperblockLogicalOffset);
        Encoding.ASCII.GetBytes("_BHRfS_M").CopyTo(superblock, 0x40);
        WriteU64(superblock, 0x48, Generation);
        WriteU64(superblock, 0x50, RootTreeLogicalOffset);
        WriteU64(superblock, 0x58, ChunkTreeLogicalOffset);
        WriteU64(superblock, 0x70, fileSystemLength);
        WriteU64(superblock, 0x78, 4 * NodeSize + 3 * SectorSize);
        WriteU64(superblock, 0x88, numberOfDevices);
        WriteU32(superblock, 0x90, SectorSize);
        WriteU32(superblock, 0x94, NodeSize);
        WriteU32(superblock, 0x98, NodeSize);
        WriteU32(superblock, 0x9c, SectorSize);
        WriteU32(superblock, 0xa0, 17 + chunk.Length);
        WriteU64(superblock, 0xa4, Generation);
        WriteU64(
            superblock,
            0xbc,
            defaultSubvolume ? 0x7 : 0x5); // MIXED_BACKREF | DEFAULT_SUBVOL | MIXED_GROUPS
        CreateDeviceItem(deviceId, deviceLength, deviceUuid, fsid).CopyTo(superblock, 0xc9);
        WriteKey(superblock, 0x32b, new BtrfsKey(256, 228, 0));
        chunk.CopyTo(superblock, 0x32b + 17);
        WriteU32(superblock, 0, ComputeCrc32C(superblock.AsSpan(32)));
        return superblock;
    }

    private static byte[] CreateChunk(
        int chunkLength,
        ulong deviceId,
        byte[] deviceUuid,
        ulong physicalStart = 0,
        ulong type = 0x7)
    {
        var chunk = new byte[80];
        WriteU64(chunk, 0, chunkLength);
        WriteU64(chunk, 8, 2);
        WriteU64(chunk, 16, 64 * 1024);
        WriteU64(chunk, 24, type);
        WriteU32(chunk, 32, SectorSize);
        WriteU32(chunk, 36, SectorSize);
        WriteU32(chunk, 40, SectorSize);
        WriteU16(chunk, 44, 1);
        WriteU16(chunk, 46, 1);
        WriteU64(chunk, 48, deviceId);
        WriteU64(chunk, 56, physicalStart);
        deviceUuid.CopyTo(chunk, 64);
        return chunk;
    }

    private static byte[] CreateDeviceItem(
        ulong deviceId,
        int deviceLength,
        byte[] deviceUuid,
        byte[] fsid)
    {
        var item = new byte[98];
        WriteU64(item, 0, deviceId);
        WriteU64(item, 8, deviceLength);
        WriteU64(item, 16, deviceLength / 2);
        WriteU32(item, 24, SectorSize);
        WriteU32(item, 28, SectorSize);
        WriteU32(item, 32, SectorSize);
        WriteU64(item, 44, Generation);
        item[64] = 100;
        item[65] = 100;
        deviceUuid.CopyTo(item, 66);
        fsid.CopyTo(item, 82);
        return item;
    }

    private static byte[] CreateRootItem(
        ulong rootDirectoryId,
        ulong bytenr,
        ulong generation = Generation)
    {
        var item = new byte[239];
        WriteU64(item, 160, generation);
        WriteU64(item, 168, rootDirectoryId);
        WriteU64(item, 176, bytenr);
        WriteU64(item, 184, 1);
        item[238] = 0;
        return item;
    }

    private static byte[] CreateRootReference(ulong directoryId, ulong sequence, string nameValue)
    {
        var name = Encoding.UTF8.GetBytes(nameValue);
        var item = new byte[18 + name.Length];
        WriteU64(item, 0, directoryId);
        WriteU64(item, 8, sequence);
        WriteU16(item, 16, name.Length);
        name.CopyTo(item, 18);
        return item;
    }

    private static byte[] CreateSubvolumeTree(
        ulong bytenr,
        ulong owner,
        byte[] fsid,
        byte[] chunkUuid,
        string fileName,
        string contents,
        ulong? nestedSubvolumeId = null,
        string? nestedSubvolumeName = null)
    {
        var contentBytes = Encoding.UTF8.GetBytes(contents);
        var items = new List<(BtrfsKey Key, byte[] Data)>
        {
            (new BtrfsKey(256, 1, 0), CreateInode(0, 0x41ed)),
            (new BtrfsKey(256, 96, 2), CreateDirectoryEntries((257, 1, 1, fileName))),
            (new BtrfsKey(257, 1, 0), CreateInode(contentBytes.Length, 0x81a4)),
            (new BtrfsKey(257, 108, 0), CreateInlineExtent(contentBytes)),
        };
        if (nestedSubvolumeId is ulong childId && nestedSubvolumeName is not null)
        {
            items.Add((new BtrfsKey(256, 96, 3), CreateDirectoryEntries((childId, 132, 2, nestedSubvolumeName))));
        }

        return CreateLeaf(bytenr, owner, fsid, chunkUuid, items);
    }

    private static byte[] CreateInode(int size, uint mode)
    {
        var inode = new byte[160];
        WriteU64(inode, 0, Generation);
        WriteU64(inode, 8, Generation);
        WriteU64(inode, 16, size);
        WriteU32(inode, 40, mode == 0x41ed ? 2u : 1u);
        WriteU32(inode, 52, mode);
        WriteI64(inode, 136, 2_400_000_000);
        WriteU32(inode, 144, 123_456_700);
        return inode;
    }

    private static byte[] CreateDirectoryEntries(params (ulong Inode, byte LocationType, byte FileType, string Name)[] entries)
    {
        var output = new List<byte>();
        foreach (var entry in entries)
        {
            var name = Encoding.UTF8.GetBytes(entry.Name);
            var item = new byte[30 + name.Length];
            WriteKey(item, 0, new BtrfsKey(entry.Inode, entry.LocationType, 0));
            WriteU64(item, 17, Generation);
            WriteU16(item, 25, 0);
            WriteU16(item, 27, name.Length);
            item[29] = entry.FileType;
            name.CopyTo(item, 30);
            output.AddRange(item);
        }

        return output.ToArray();
    }

    private static byte[] CreateInlineExtent(byte[] data)
    {
        var extent = new byte[21 + data.Length];
        WriteU64(extent, 0, Generation);
        WriteU64(extent, 8, data.Length);
        data.CopyTo(extent, 21);
        return extent;
    }

    private static byte[] CreateCompressedInlineExtent(byte[] data, int decodedLength, byte compression)
    {
        var extent = new byte[21 + data.Length];
        WriteU64(extent, 0, Generation);
        WriteU64(extent, 8, decodedLength);
        extent[16] = compression;
        data.CopyTo(extent, 21);
        return extent;
    }

    private static byte[] CreateRegularExtent(
        ulong diskBytenr,
        int diskBytes,
        int numberOfBytes,
        byte compression = 0,
        int? ramBytes = null)
    {
        var extent = new byte[53];
        WriteU64(extent, 0, Generation);
        WriteU64(extent, 8, ramBytes ?? numberOfBytes);
        extent[16] = compression;
        extent[20] = 1;
        WriteU64(extent, 21, diskBytenr);
        WriteU64(extent, 29, diskBytes);
        WriteU64(extent, 45, numberOfBytes);
        return extent;
    }

    private static byte[] CompressZlib(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            zlib.Write(data);
        }

        return output.ToArray();
    }

    private static byte[] CompressBtrfsLzoZeros(int decodedLength, bool padToSector = true)
    {
        if (decodedLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(decodedLength));
        }

        var output = new List<byte>(SectorSize) { 0, 0, 0, 0 };
        for (var offset = 0; offset < decodedLength; offset += SectorSize)
        {
            var segmentDecodedLength = Math.Min(SectorSize, decodedLength - offset);
            var compressed = EncodeLzoZeroBlock(segmentDecodedLength);
            AppendU32Little(output, checked((uint)compressed.Length));
            output.AddRange(compressed);
            var bytesLeftInSector = SectorSize - output.Count % SectorSize;
            if (bytesLeftInSector is > 0 and < sizeof(uint))
            {
                output.AddRange(new byte[bytesLeftInSector]);
            }
        }

        var totalLength = output.Count;
        while (padToSector && output.Count % SectorSize != 0)
        {
            output.Add(0);
        }

        var result = output.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(
            result.AsSpan(0, sizeof(uint)),
            checked((uint)totalLength));
        return result;
    }

    private static byte[] CompressZstdZeros(int decodedLength, bool padToSector)
    {
        using var compressor = new Compressor(3);
        var compressed = compressor.Wrap(new byte[decodedLength]).ToArray();
        if (!padToSector)
        {
            return compressed;
        }

        var allocatedLength = (compressed.Length + SectorSize - 1) / SectorSize * SectorSize;
        Array.Resize(ref compressed, allocatedLength);
        return compressed;
    }

    private static byte[] EncodeLzoZeroBlock(int length)
    {
        if (length < 37)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        var compressed = new List<byte> { 21, 0, 0, 0, 0, 32 };
        var extended = length - 4 - 33;
        while (extended > 255)
        {
            compressed.Add(0);
            extended -= 255;
        }

        compressed.Add(extended == 0 ? (byte)255 : checked((byte)extended));
        compressed.Add(0);
        compressed.Add(0);
        compressed.Add(17);
        compressed.Add(0);
        compressed.Add(0);
        return compressed.ToArray();
    }

    private static void AppendU32Little(List<byte> data, uint value)
    {
        data.Add((byte)value);
        data.Add((byte)(value >> 8));
        data.Add((byte)(value >> 16));
        data.Add((byte)(value >> 24));
    }

    private static byte[] CreateLeaf(
        ulong bytenr,
        ulong owner,
        byte[] fsid,
        byte[] chunkUuid,
        IReadOnlyList<(BtrfsKey Key, byte[] Data)> sourceItems)
    {
        var items = sourceItems
            .OrderBy(item => item.Key.ObjectId)
            .ThenBy(item => item.Key.Type)
            .ThenBy(item => item.Key.Offset)
            .ToArray();
        var block = new byte[NodeSize];
        fsid.CopyTo(block, 0x20);
        WriteU64(block, 0x30, bytenr);
        chunkUuid.CopyTo(block, 0x40);
        WriteU64(block, 0x50, Generation);
        WriteU64(block, 0x58, owner);
        WriteU32(block, 0x60, items.Length);

        var dataCursor = block.Length;
        for (var index = 0; index < items.Length; index++)
        {
            var item = items[index];
            dataCursor -= item.Data.Length;
            var descriptorOffset = TreeHeaderSize + index * 25;
            if (dataCursor < TreeHeaderSize + items.Length * 25)
            {
                throw new InvalidOperationException("Synthetic Btrfs leaf is too large.");
            }

            item.Data.CopyTo(block, dataCursor);
            WriteKey(block, descriptorOffset, item.Key);
            WriteU32(block, descriptorOffset + 17, dataCursor - TreeHeaderSize);
            WriteU32(block, descriptorOffset + 21, item.Data.Length);
        }

        WriteU32(block, 0, ComputeCrc32C(block.AsSpan(32)));
        return block;
    }

    private static void WriteMbr(byte[] disk, int partitionLength)
    {
        const int entry = 446;
        disk[entry + 4] = 0x83;
        WriteU32(disk, entry + 8, PartitionStart / 512);
        WriteU32(disk, entry + 12, partitionLength / 512);
        disk[510] = 0x55;
        disk[511] = 0xaa;
    }

    private static void WriteKey(byte[] data, int offset, BtrfsKey key)
    {
        WriteU64(data, offset, key.ObjectId);
        data[offset + 8] = key.Type;
        WriteU64(data, offset + 9, key.Offset);
    }

    private static uint ComputeCrc32C(ReadOnlySpan<byte> data)
    {
        const uint polynomial = 0x82f63b78;
        var checksum = uint.MaxValue;
        foreach (var value in data)
        {
            checksum ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                checksum = (checksum >> 1) ^ ((checksum & 1) == 0 ? 0 : polynomial);
            }
        }

        return ~checksum;
    }

    private static void WriteU16(byte[] data, int offset, int value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset, sizeof(ushort)), checked((ushort)value));

    private static void WriteU32(byte[] data, int offset, int value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset, sizeof(uint)), checked((uint)value));

    private static void WriteU32(byte[] data, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset, sizeof(uint)), value);

    private static void WriteU64(byte[] data, int offset, long value) =>
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(offset, sizeof(ulong)), checked((ulong)value));

    private static void WriteU64(byte[] data, int offset, ulong value) =>
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(offset, sizeof(ulong)), value);

    private static void WriteI64(byte[] data, int offset, long value) =>
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(offset, sizeof(long)), value);

    private sealed record BtrfsKey(ulong ObjectId, byte Type, ulong Offset);
}

internal sealed record BtrfsTestFixture(
    int SuperblockPhysicalOffset,
    int FileSystemTreePhysicalOffset,
    int RegularDataPhysicalOffset);

internal sealed record BtrfsMultiDeviceTestFixture(string FirstPath, string SecondPath);
