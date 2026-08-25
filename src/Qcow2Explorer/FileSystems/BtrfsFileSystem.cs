using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Qcow2Explorer.Core;
using Qcow2Explorer.Partitions;
using ZstdSharp;

namespace Qcow2Explorer.FileSystems;

public sealed class BtrfsFileSystem : IReadOnlyFileSystem
{
    private const long SuperblockOffset = 64 * 1024;
    private static readonly long[] BackupSuperblockOffsets = [64L * 1024 * 1024, 256L * 1024 * 1024 * 1024];
    private const int SuperblockSize = 4096;
    private const int TreeHeaderSize = 101;
    private const int KeySize = 17;
    private const int LeafItemSize = 25;
    private const int KeyPointerSize = 33;
    private const int MaximumTreeLevel = 8;
    private const int MaximumTreeBlocks = 100_000;
    private const int MaximumTreeItems = 1_000_000;
    private const int MaximumDecodedExtentSize = 128 * 1024;

    private const ulong RootTreeObjectId = 1;
    private const ulong ChunkTreeObjectId = 3;
    private const ulong FileSystemTreeObjectId = 5;
    private const ulong RootTreeDirectoryObjectId = 6;
    private const ulong ChecksumTreeObjectId = 7;
    private const ulong FirstFreeObjectId = 256;
    private const ulong LastFreeObjectId = unchecked((ulong)-256L);
    private const ulong ExtentChecksumObjectId = unchecked((ulong)-10L);

    private const byte InodeItemKey = 1;
    private const byte DirectoryItemKey = 84;
    private const byte DirectoryIndexKey = 96;
    private const byte ExtentDataKey = 108;
    private const byte ExtentChecksumKey = 128;
    private const byte RootItemKey = 132;
    private const byte RootBackReferenceKey = 144;
    private const byte RootReferenceKey = 156;
    private const byte DeviceItemKey = 216;
    private const byte ChunkItemKey = 228;

    private const ulong ChunkTypeData = 1UL << 0;
    private const ulong ChunkTypeSystem = 1UL << 1;
    private const ulong ChunkTypeMetadata = 1UL << 2;
    private const ulong ChunkProfileRaid1 = 1UL << 4;
    private const ulong ChunkProfileRaid10 = 1UL << 6;
    private const ulong ChunkProfileRaid1C3 = 1UL << 9;
    private const ulong ChunkProfileRaid1C4 = 1UL << 10;
    private const ulong ChunkProfileMask = 0x7f8;

    private const uint DirectoryMode = 0x4000;
    private const uint RegularFileMode = 0x8000;
    private const uint FileTypeMask = 0xf000;

    private const ulong SupportedIncompatibilityFlags =
        (1UL << 0)   // MIXED_BACKREF
        | (1UL << 1) // DEFAULT_SUBVOL
        | (1UL << 2) // MIXED_GROUPS
        | (1UL << 3) // COMPRESS_LZO
        | (1UL << 4) // COMPRESS_ZSTD
        | (1UL << 5) // BIG_METADATA
        | (1UL << 6) // EXTENDED_IREF
        | (1UL << 8) // SKINNY_METADATA
        | (1UL << 9) // NO_HOLES
        | (1UL << 11); // RAID1C34

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly Dictionary<ulong, BtrfsDevice> _devices = [];
    private readonly Dictionary<ulong, BtrfsDeviceItem> _deviceItems = [];
    private readonly byte[] _fileSystemId;
    private readonly int _sectorSize;
    private readonly int _nodeSize;
    private readonly List<BtrfsChunk> _chunks = [];
    private readonly Dictionary<BtrfsObjectReference, BtrfsInode> _inodes = [];
    private readonly Dictionary<BtrfsObjectReference, List<BtrfsDirectoryEntry>> _directories = [];
    private readonly Dictionary<BtrfsObjectReference, List<BtrfsFileExtent>> _fileExtents = [];
    private readonly Dictionary<ulong, ulong> _rootDirectoryIds = [];
    private readonly HashSet<BtrfsSubvolumeLink> _subvolumeLinks = [];
    private readonly Dictionary<ulong, uint> _dataChecksums = [];
    private readonly Dictionary<BtrfsCompressedExtentKey, byte[]> _decodedExtentCache = [];
    private readonly object _decodedExtentCacheLock = new();
    private IReadOnlyList<ulong> _missingDeviceIds = [];

    public BtrfsFileSystem(IBlockReader reader, PartitionInfo partition)
        : this([reader], partition)
    {
    }

    public BtrfsFileSystem(IReadOnlyList<IBlockReader> readers, PartitionInfo partition)
    {
        ArgumentNullException.ThrowIfNull(readers);
        if (readers.Count == 0)
        {
            throw new ArgumentException("Btrfs device readerを1個以上指定してください。", nameof(readers));
        }

        Partition = partition;
        var selectedSuperblocks = readers
            .Select(reader => (Reader: reader, Superblock: SelectSuperblock(reader)))
            .ToArray();
        var superblock = selectedSuperblocks[0].Superblock;
        _fileSystemId = superblock.AsSpan(0x20, 16).ToArray();
        _sectorSize = checked((int)EndianUtilities.ReadUInt32Little(superblock, 0x90));
        _nodeSize = checked((int)EndianUtilities.ReadUInt32Little(superblock, 0x94));
        var numberOfDevices = EndianUtilities.ReadUInt64Little(superblock, 0x88);

        foreach (var candidate in selectedSuperblocks)
        {
            RegisterDevice(candidate.Reader, candidate.Superblock, numberOfDevices);
            if (!ReferenceEquals(candidate.Superblock, superblock)
                && !HaveMatchingSuperblockState(superblock, candidate.Superblock))
            {
                var deviceId = EndianUtilities.ReadUInt64Little(candidate.Superblock, 0xc9);
                throw new InvalidDataException(
                    $"Btrfs deviceのsuperblock tree stateが一致しません: devid={deviceId}");
            }
        }

        ParseSystemChunkArray(superblock);
        var superGeneration = EndianUtilities.ReadUInt64Little(superblock, 0x48);
        var chunkTreeRoot = EndianUtilities.ReadUInt64Little(superblock, 0x58);
        var chunkTreeLevel = superblock[0xc7];
        var chunkTreeGeneration = EndianUtilities.ReadUInt64Little(superblock, 0xa4);
        foreach (var item in ReadTreeItems(
            chunkTreeRoot,
            ChunkTreeObjectId,
            chunkTreeLevel,
            chunkTreeGeneration))
        {
            if (item.Key.Type == DeviceItemKey)
            {
                AddDeviceItem(ParseDeviceItem(item));
            }
            else if (item.Key.Type == ChunkItemKey)
            {
                AddChunk(ParseChunk(item.Key.Offset, item.Data));
            }
        }

        ValidateDeviceItems(numberOfDevices);
        ValidateChunkMappings();

        var rootTreeRoot = EndianUtilities.ReadUInt64Little(superblock, 0x50);
        var rootTreeLevel = superblock[0xc6];
        var rootItems = ReadTreeItems(rootTreeRoot, RootTreeObjectId, rootTreeLevel, superGeneration);
        var fileSystemRoot = ParseTreeRoot(rootItems, FileSystemTreeObjectId, "FS tree");
        var checksumRoot = TryParseTreeRoot(rootItems, ChecksumTreeObjectId);
        var fileSystemRoots = ParseFileSystemRoots(rootItems, fileSystemRoot);
        var rootReferences = ParseRootReferences(rootItems, fileSystemRoots);
        var reachableTreeIds = FindReachableFileSystemTrees(rootReferences);
        var hasDefaultSubvolumeFeature =
            (EndianUtilities.ReadUInt64Little(superblock, 0xbc) & (1UL << 1)) != 0;
        var defaultTreeId = ParseDefaultTreeId(
            rootItems,
            reachableTreeIds,
            hasDefaultSubvolumeFeature);

        foreach (var treeId in reachableTreeIds)
        {
            var treeRoot = fileSystemRoots[treeId];
            _rootDirectoryIds.Add(treeId, treeRoot.RootDirectoryId);
            var fileSystemItems = ReadTreeItems(
                treeRoot.Bytenr,
                treeId,
                treeRoot.Level,
                treeRoot.Generation);
            ParseFileSystemTree(fileSystemItems, treeId);
            var rootReference = new BtrfsObjectReference(treeId, treeRoot.RootDirectoryId);
            if (!_inodes.TryGetValue(rootReference, out var treeRootInode) || !treeRootInode.IsDirectory)
            {
                throw new InvalidDataException(
                    $"Btrfs FS treeのroot directory inodeが見つかりません: tree={treeId}");
            }
        }

        ValidateRootReferenceDirectoryEntries(rootReferences, reachableTreeIds, fileSystemRoots);

        if (checksumRoot is not null)
        {
            ParseChecksumTree(ReadTreeItems(
                checksumRoot.Bytenr,
                ChecksumTreeObjectId,
                checksumRoot.Level,
                checksumRoot.Generation));
        }

        var defaultRoot = fileSystemRoots[defaultTreeId];
        var defaultRootReference = new BtrfsObjectReference(defaultTreeId, defaultRoot.RootDirectoryId);
        var rootInode = _inodes[defaultRootReference];

        Root = new VfsNode
        {
            Name = "",
            IsDirectory = true,
            Attributes = FileAttributes.Directory,
            ModifiedUtc = rootInode.ModifiedUtc,
            Metadata = new BtrfsNodeReference(defaultRootReference)
        };
    }

    public string Name => "Btrfs";
    public PartitionInfo Partition { get; }
    public VfsNode Root { get; }
    public bool IsDegraded => _missingDeviceIds.Count > 0;
    public IReadOnlyList<ulong> MissingDeviceIds => _missingDeviceIds;

    public static BtrfsDeviceIdentity ReadDeviceIdentity(IBlockReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var superblock = SelectSuperblock(reader);
        return new BtrfsDeviceIdentity(
            Convert.ToHexString(superblock.AsSpan(0x20, 16)),
            EndianUtilities.ReadUInt64Little(superblock, 0xc9),
            Convert.ToHexString(superblock.AsSpan(0x10b, 16)),
            EndianUtilities.ReadUInt64Little(superblock, 0x88));
    }

    public IReadOnlyList<VfsNode> ListDirectory(VfsNode directory)
    {
        if (!directory.IsDirectory || directory.Metadata is not BtrfsNodeReference nodeReference)
        {
            return Array.Empty<VfsNode>();
        }

        if (!_directories.TryGetValue(nodeReference.Object, out var entries))
        {
            return Array.Empty<VfsNode>();
        }

        var nodes = new List<VfsNode>(entries.Count);
        foreach (var entry in entries)
        {
            BtrfsObjectReference target;
            if (entry.LocationType == InodeItemKey)
            {
                target = new BtrfsObjectReference(nodeReference.Object.TreeId, entry.ObjectId);
            }
            else if (entry.LocationType == RootItemKey)
            {
                var subvolumeLink = new BtrfsSubvolumeLink(
                    nodeReference.Object.TreeId,
                    nodeReference.Object.InodeNumber,
                    entry.ObjectId,
                    entry.Sequence,
                    entry.Name);
                if (!_subvolumeLinks.Contains(subvolumeLink))
                {
                    nodes.Add(new VfsNode
                    {
                        Name = entry.Name,
                        IsDirectory = true,
                        Attributes = FileAttributes.Directory,
                        Metadata = new BtrfsSnapshotBoundaryReference()
                    });
                    continue;
                }

                if (entry.FileType != 2 || !_rootDirectoryIds.TryGetValue(entry.ObjectId, out var rootDirectoryId))
                {
                    throw new InvalidDataException($"Btrfs subvolume directory entryが不正です: {entry.Name}");
                }

                target = new BtrfsObjectReference(entry.ObjectId, rootDirectoryId);
            }
            else
            {
                throw new NotSupportedException($"Btrfs特殊directory entryには未対応です: {entry.Name}");
            }

            if (!_inodes.TryGetValue(target, out var inode))
            {
                throw new InvalidDataException(
                    $"Btrfs directory entryが存在しないinodeを参照しています: "
                    + $"tree={target.TreeId}, inode={target.InodeNumber}");
            }

            nodes.Add(new VfsNode
            {
                Name = entry.Name,
                IsDirectory = inode.IsDirectory,
                Size = inode.IsDirectory ? 0 : ToLongSize(inode.Size),
                ModifiedUtc = inode.ModifiedUtc,
                Attributes = inode.IsDirectory ? FileAttributes.Directory : FileAttributes.Normal,
                Metadata = new BtrfsNodeReference(target)
            });
        }

        return nodes
            .OrderByDescending(node => node.IsDirectory)
            .ThenBy(node => node.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public byte[] ReadFile(VfsNode file, long offset, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (file.IsDirectory || file.Metadata is not BtrfsNodeReference nodeReference || count == 0)
        {
            return Array.Empty<byte>();
        }

        if (!_inodes.TryGetValue(nodeReference.Object, out var inode))
        {
            throw new InvalidDataException(
                $"Btrfs inodeが見つかりません: "
                + $"tree={nodeReference.Object.TreeId}, inode={nodeReference.Object.InodeNumber}");
        }

        if (!inode.IsRegularFile)
        {
            throw new NotSupportedException("Btrfs MVPでは通常ファイルの読み取りだけに対応しています。");
        }

        if ((ulong)offset >= inode.Size)
        {
            return Array.Empty<byte>();
        }

        var available = checked((int)Math.Min((ulong)count, inode.Size - (ulong)offset));
        var output = new byte[available];
        if (!_fileExtents.TryGetValue(nodeReference.Object, out var extents))
        {
            return output;
        }

        var requestStart = (ulong)offset;
        var requestEnd = checked(requestStart + (ulong)available);
        foreach (var extent in extents)
        {
            var extentStart = extent.FileOffset;
            var extentEnd = checked(extentStart + extent.Length);
            var overlapStart = Math.Max(requestStart, extentStart);
            var overlapEnd = Math.Min(requestEnd, extentEnd);
            if (overlapStart >= overlapEnd)
            {
                continue;
            }

            if (extent.Encryption != 0 || extent.OtherEncoding != 0)
            {
                throw new NotSupportedException(
                    $"Btrfs暗号化・encoding付きextentには未対応です: encryption={extent.Encryption}, "
                    + $"encoding={extent.OtherEncoding}");
            }

            var destinationOffset = checked((int)(overlapStart - requestStart));
            var sourceOffset = overlapStart - extentStart;
            var length = checked((int)(overlapEnd - overlapStart));
            if (extent.Type == BtrfsFileExtentType.Inline)
            {
                if (extent.Compression is not 0 and not 1 and not 2 and not 3)
                {
                    throw new NotSupportedException($"未対応のBtrfs inline圧縮方式です: {extent.Compression}");
                }

                extent.InlineData!.AsSpan(checked((int)sourceOffset), length)
                    .CopyTo(output.AsSpan(destinationOffset, length));
            }
            else if (extent.Type == BtrfsFileExtentType.Regular && extent.DiskBytenr != 0)
            {
                if (extent.Compression == 0)
                {
                    var logical = checked(extent.DiskBytenr + extent.ExtentOffset + sourceOffset);
                    ReadVerifiedData(logical, output, destinationOffset, length);
                }
                else if (extent.Compression is 1 or 2 or 3)
                {
                    var decoded = ReadCompressedExtent(extent);
                    var decodedOffset = checked((int)(extent.ExtentOffset + sourceOffset));
                    decoded.AsSpan(decodedOffset, length).CopyTo(output.AsSpan(destinationOffset, length));
                }
                else
                {
                    throw new NotSupportedException($"未対応のBtrfs圧縮方式です: {extent.Compression}");
                }
            }
        }

        return output;
    }

    private static byte[] SelectSuperblock(IBlockReader reader)
    {
        InvalidDataException? primaryError = null;
        if (reader.Length >= SuperblockOffset + SuperblockSize)
        {
            var primary = EndianUtilities.ReadBytes(reader, SuperblockOffset, SuperblockSize);
            try
            {
                ValidateSuperblock(primary, reader.Length, SuperblockOffset);
                return primary;
            }
            catch (InvalidDataException ex)
            {
                primaryError = ex;
            }
        }
        else
        {
            primaryError = new InvalidDataException("Btrfs primary superblockが収まらないボリュームです。");
        }

        var backups = new List<(long Offset, byte[] Data)>();
        foreach (var offset in BackupSuperblockOffsets)
        {
            if (offset > reader.Length - SuperblockSize)
            {
                continue;
            }

            var backup = EndianUtilities.ReadBytes(reader, offset, SuperblockSize);
            try
            {
                ValidateSuperblock(backup, reader.Length, offset);
                backups.Add((offset, backup));
            }
            catch (InvalidDataException)
            {
                // Try the next official mirror location.
            }
        }

        if (backups.Count == 0)
        {
            throw new InvalidDataException(
                $"Btrfs primary/backup superblockを検証できませんでした: {primaryError!.Message}",
                primaryError);
        }

        var firstFileSystemId = backups[0].Data.AsSpan(0x20, 16).ToArray();
        if (backups.Any(candidate => !candidate.Data.AsSpan(0x20, 16).SequenceEqual(firstFileSystemId)))
        {
            throw new InvalidDataException("Btrfs backup superblockのFSIDが相互に一致しません。");
        }

        var maximumGeneration = backups.Max(candidate => EndianUtilities.ReadUInt64Little(candidate.Data, 0x48));
        var newest = backups
            .Where(candidate => EndianUtilities.ReadUInt64Little(candidate.Data, 0x48) == maximumGeneration)
            .OrderBy(candidate => candidate.Offset)
            .ToArray();
        if (newest.Skip(1).Any(candidate => !HaveMatchingSuperblockState(newest[0].Data, candidate.Data)))
        {
            throw new InvalidDataException(
                $"Btrfs backup superblockの同一世代でtree stateが一致しません: generation={maximumGeneration}");
        }

        return newest[0].Data;
    }

    private static bool HaveMatchingSuperblockState(byte[] left, byte[] right)
    {
        return EndianUtilities.ReadUInt64Little(left, 0x48) == EndianUtilities.ReadUInt64Little(right, 0x48)
            && EndianUtilities.ReadUInt64Little(left, 0x50) == EndianUtilities.ReadUInt64Little(right, 0x50)
            && EndianUtilities.ReadUInt64Little(left, 0x58) == EndianUtilities.ReadUInt64Little(right, 0x58)
            && EndianUtilities.ReadUInt64Little(left, 0x70) == EndianUtilities.ReadUInt64Little(right, 0x70)
            && EndianUtilities.ReadUInt64Little(left, 0x88) == EndianUtilities.ReadUInt64Little(right, 0x88)
            && EndianUtilities.ReadUInt32Little(left, 0x90) == EndianUtilities.ReadUInt32Little(right, 0x90)
            && EndianUtilities.ReadUInt32Little(left, 0x94) == EndianUtilities.ReadUInt32Little(right, 0x94)
            && EndianUtilities.ReadUInt32Little(left, 0xa0) == EndianUtilities.ReadUInt32Little(right, 0xa0)
            && EndianUtilities.ReadUInt64Little(left, 0xa4) == EndianUtilities.ReadUInt64Little(right, 0xa4)
            && EndianUtilities.ReadUInt64Little(left, 0xac) == EndianUtilities.ReadUInt64Little(right, 0xac)
            && EndianUtilities.ReadUInt64Little(left, 0xb4) == EndianUtilities.ReadUInt64Little(right, 0xb4)
            && EndianUtilities.ReadUInt64Little(left, 0xbc) == EndianUtilities.ReadUInt64Little(right, 0xbc)
            && left[0xc6] == right[0xc6]
            && left[0xc7] == right[0xc7]
            && left.AsSpan(0x32b, checked((int)EndianUtilities.ReadUInt32Little(left, 0xa0)))
                .SequenceEqual(right.AsSpan(0x32b, checked((int)EndianUtilities.ReadUInt32Little(right, 0xa0))));
    }

    private static void ValidateSuperblock(byte[] superblock, long readerLength, long expectedOffset)
    {
        if (EndianUtilities.ReadAscii(superblock, 0x40, 8) != "_BHRfS_M")
        {
            throw new InvalidDataException("Btrfs superblock magicが一致しません。");
        }

        var checksumType = EndianUtilities.ReadUInt16Little(superblock, 0xc4);
        if (checksumType != 0)
        {
            throw new NotSupportedException($"Btrfs superblock checksum typeには未対応です: {checksumType}");
        }

        VerifyChecksum(superblock, "Btrfs superblock");
        if (EndianUtilities.ReadUInt64Little(superblock, 0x30) != (ulong)expectedOffset)
        {
            throw new InvalidDataException(
                $"Btrfs superblockの物理位置が一致しません: expected={expectedOffset:N0}");
        }

        var totalBytes = EndianUtilities.ReadUInt64Little(superblock, 0x70);
        if (totalBytes < SuperblockSize
            || (ulong)expectedOffset > totalBytes - SuperblockSize)
        {
            throw new InvalidDataException($"Btrfs total_bytesが不正です: {totalBytes:N0}");
        }

        var numberOfDevices = EndianUtilities.ReadUInt64Little(superblock, 0x88);
        if (numberOfDevices == 0 || numberOfDevices > 256)
        {
            throw new InvalidDataException($"Btrfs device数が不正です: devices={numberOfDevices}");
        }

        var deviceId = EndianUtilities.ReadUInt64Little(superblock, 0xc9);
        var deviceTotalBytes = EndianUtilities.ReadUInt64Little(superblock, 0xd1);
        var deviceSectorSize = EndianUtilities.ReadUInt32Little(superblock, 0xe9);
        var deviceFileSystemId = superblock.AsSpan(0x11b, 16);
        if (deviceId == 0)
        {
            throw new InvalidDataException("Btrfs superblockのdevidが0です。");
        }

        if (deviceTotalBytes < SuperblockSize
            || deviceTotalBytes > (ulong)readerLength
            || deviceTotalBytes > totalBytes
            || (ulong)expectedOffset > deviceTotalBytes - SuperblockSize)
        {
            throw new InvalidDataException(
                $"Btrfs superblockのdevice total_bytesがボリューム範囲外です: "
                + $"devid={deviceId}, bytes={deviceTotalBytes:N0}");
        }

        if (!deviceFileSystemId.SequenceEqual(superblock.AsSpan(0x20, 16)))
        {
            throw new InvalidDataException($"Btrfs superblockのdevice FSIDが一致しません: devid={deviceId}");
        }

        if (superblock.AsSpan(0x10b, 16).IndexOfAnyExcept((byte)0) < 0)
        {
            throw new InvalidDataException($"Btrfs superblockのdevice UUIDが空です: devid={deviceId}");
        }

        var sectorSize = EndianUtilities.ReadUInt32Little(superblock, 0x90);
        var nodeSize = EndianUtilities.ReadUInt32Little(superblock, 0x94);
        if (!IsPowerOfTwo(sectorSize) || sectorSize < 512 || sectorSize > 64 * 1024)
        {
            throw new InvalidDataException($"Btrfs sectorsizeが不正です: {sectorSize}");
        }

        if (!IsPowerOfTwo(nodeSize) || nodeSize < 4096 || nodeSize > 64 * 1024 || nodeSize < sectorSize)
        {
            throw new InvalidDataException($"Btrfs nodesizeが不正です: {nodeSize}");
        }

        if (superblock[0xc6] > MaximumTreeLevel || superblock[0xc7] > MaximumTreeLevel)
        {
            throw new InvalidDataException("Btrfs tree levelが上限を超えています。");
        }

        var incompatibilityFlags = EndianUtilities.ReadUInt64Little(superblock, 0xbc);
        var unsupportedFlags = incompatibilityFlags & ~SupportedIncompatibilityFlags;
        if (unsupportedFlags != 0)
        {
            throw new NotSupportedException($"未対応のBtrfs incompat featureがあります: 0x{unsupportedFlags:X}");
        }

        if (deviceSectorSize != sectorSize)
        {
            throw new InvalidDataException(
                $"Btrfs superblockのdevice sector sizeが一致しません: "
                + $"devid={deviceId}, sector={deviceSectorSize}");
        }

        var systemChunkArraySize = EndianUtilities.ReadUInt32Little(superblock, 0xa0);
        if (systemChunkArraySize == 0
            || systemChunkArraySize > 2048
            || 0x32b + systemChunkArraySize > superblock.Length)
        {
            throw new InvalidDataException(
                $"Btrfs system chunk array sizeが不正です: {systemChunkArraySize}");
        }
    }

    private void RegisterDevice(IBlockReader reader, byte[] superblock, ulong expectedDeviceCount)
    {
        var fileSystemId = superblock.AsSpan(0x20, 16);
        var deviceFileSystemId = superblock.AsSpan(0x11b, 16);
        var deviceId = EndianUtilities.ReadUInt64Little(superblock, 0xc9);
        if (!fileSystemId.SequenceEqual(_fileSystemId) || !deviceFileSystemId.SequenceEqual(_fileSystemId))
        {
            throw new InvalidDataException(
                $"別のBtrfsファイルシステムのdeviceが指定されました: devid={deviceId}");
        }

        if (EndianUtilities.ReadUInt64Little(superblock, 0x88) != expectedDeviceCount)
        {
            throw new InvalidDataException(
                $"Btrfs device数がsuperblock間で一致しません: devid={deviceId}");
        }

        if (EndianUtilities.ReadUInt32Little(superblock, 0x90) != (uint)_sectorSize
            || EndianUtilities.ReadUInt32Little(superblock, 0x94) != (uint)_nodeSize)
        {
            throw new InvalidDataException(
                $"Btrfs deviceのsector/node sizeが一致しません: devid={deviceId}");
        }

        var device = new BtrfsDevice(
            deviceId,
            reader,
            superblock.AsSpan(0x10b, 16).ToArray(),
            EndianUtilities.ReadUInt64Little(superblock, 0xd1));
        if (!_devices.TryAdd(deviceId, device))
        {
            throw new InvalidDataException($"Btrfs devidが重複しています: devid={deviceId}");
        }
    }

    private void ParseSystemChunkArray(byte[] superblock)
    {
        const int arrayOffset = 0x32b;
        var arraySize = EndianUtilities.ReadUInt32Little(superblock, 0xa0);
        if (arraySize == 0 || arraySize > 2048 || arrayOffset + arraySize > superblock.Length)
        {
            throw new InvalidDataException($"Btrfs system chunk array sizeが不正です: {arraySize}");
        }

        var offset = arrayOffset;
        var end = checked(arrayOffset + (int)arraySize);
        while (offset < end)
        {
            if (offset + KeySize + 48 > end)
            {
                throw new InvalidDataException("Btrfs system chunk arrayが途中で終了しています。");
            }

            var key = ReadKey(superblock, offset);
            offset += KeySize;
            if (key.Type != ChunkItemKey)
            {
                throw new InvalidDataException($"Btrfs system chunk arrayに未知のitemがあります: type={key.Type}");
            }

            var stripeCount = EndianUtilities.ReadUInt16Little(superblock, offset + 44);
            var itemSize = checked(48 + stripeCount * 32);
            if (stripeCount == 0 || offset + itemSize > end)
            {
                throw new InvalidDataException("Btrfs system chunk itemの長さが不正です。");
            }

            AddChunk(ParseChunk(key.Offset, superblock.AsSpan(offset, itemSize).ToArray()));
            offset += itemSize;
        }
    }

    private BtrfsChunk ParseChunk(ulong logicalStart, byte[] data)
    {
        if (data.Length < 80)
        {
            throw new InvalidDataException("Btrfs chunk itemが短すぎます。");
        }

        var length = EndianUtilities.ReadUInt64Little(data, 0);
        var stripeLength = EndianUtilities.ReadUInt64Little(data, 16);
        var type = EndianUtilities.ReadUInt64Little(data, 24);
        var sectorSize = EndianUtilities.ReadUInt32Little(data, 40);
        var stripeCount = EndianUtilities.ReadUInt16Little(data, 44);
        var subStripeCount = EndianUtilities.ReadUInt16Little(data, 46);
        if (length == 0 || stripeLength == 0 || sectorSize == 0)
        {
            throw new InvalidDataException("Btrfs chunk itemにゼロのサイズがあります。");
        }

        if ((type & (ChunkTypeData | ChunkTypeSystem | ChunkTypeMetadata)) == 0)
        {
            throw new InvalidDataException($"Btrfs chunk allocation typeが不正です: 0x{type:X}");
        }

        var profile = type & ChunkProfileMask;
        var expectedStripeCount = profile switch
        {
            0 => 1,
            ChunkProfileRaid1 => 2,
            ChunkProfileRaid10 => stripeCount,
            ChunkProfileRaid1C3 => 3,
            ChunkProfileRaid1C4 => 4,
            _ => 0,
        };
        if (expectedStripeCount == 0)
        {
            throw new NotSupportedException(
                $"未対応のBtrfs chunk profileです: "
                + $"type=0x{type:X}, stripes={stripeCount}, sub_stripes={subStripeCount}");
        }

        var validStripeLayout = profile == ChunkProfileRaid10
            ? stripeCount >= 2 && stripeCount % 2 == 0 && subStripeCount == 2
            : stripeCount == expectedStripeCount && subStripeCount is 0 or 1;
        if (!validStripeLayout)
        {
            var profileName = GetProfileName(profile);
            throw new InvalidDataException(
                $"Btrfs {profileName} chunkのstripe数が不正です: "
                + $"stripes={stripeCount}, sub_stripes={subStripeCount}");
        }

        if (data.Length != 48 + stripeCount * 32)
        {
            throw new InvalidDataException("Btrfs chunk item sizeがstripe数と一致しません。");
        }

        if (sectorSize != (uint)_sectorSize
            || logicalStart % (ulong)_sectorSize != 0
            || length % (ulong)_sectorSize != 0
            || stripeLength % (ulong)_sectorSize != 0)
        {
            throw new InvalidDataException(
                $"Btrfs chunkのsector sizeまたはalignmentが不正です: sector={sectorSize}, "
                + $"logical={logicalStart}, length={length}, stripe_length={stripeLength}");
        }

        var stripes = new BtrfsStripe[stripeCount];
        for (var index = 0; index < stripeCount; index++)
        {
            var stripeOffset = 48 + index * 32;
            var deviceId = EndianUtilities.ReadUInt64Little(data, stripeOffset);
            var physicalStart = EndianUtilities.ReadUInt64Little(data, stripeOffset + 8);
            var stripeUuid = data.AsSpan(stripeOffset + 16, 16);
            if (stripeUuid.IndexOfAnyExcept((byte)0) < 0)
            {
                throw new InvalidDataException(
                    $"Btrfs chunk stripeのdevice UUIDが空です: devid={deviceId}");
            }

            if (_devices.TryGetValue(deviceId, out var device)
                && !stripeUuid.SequenceEqual(device.Uuid))
            {
                throw new InvalidDataException(
                    $"Btrfs chunk stripeのdevice UUIDが一致しません: devid={deviceId}");
            }

            if (physicalStart % (ulong)_sectorSize != 0)
            {
                throw new InvalidDataException(
                    $"Btrfs chunk stripeの物理位置がsector境界に揃っていません: "
                    + $"devid={deviceId}, physical={physicalStart}");
            }

            stripes[index] = new BtrfsStripe(
                deviceId,
                physicalStart,
                Convert.ToHexString(stripeUuid));
        }

        if (IsMirroredProfile(profile)
            && stripes.Select(stripe => stripe.DeviceId).Distinct().Count() != stripes.Length)
        {
            throw new InvalidDataException(
                $"Btrfs {GetProfileName(profile)} chunkのstripeが異なるdeviceを参照していません。");
        }

        return new BtrfsChunk(logicalStart, length, stripeLength, type, subStripeCount, stripes);
    }

    private BtrfsDeviceItem ParseDeviceItem(BtrfsLeafItem item)
    {
        if (item.Key.ObjectId != 1 || item.Data.Length != 98)
        {
            throw new InvalidDataException(
                $"Btrfs DEVICE_ITEMのkeyまたはsizeが不正です: "
                + $"objectid={item.Key.ObjectId}, size={item.Data.Length}");
        }

        var deviceId = EndianUtilities.ReadUInt64Little(item.Data, 0);
        var totalBytes = EndianUtilities.ReadUInt64Little(item.Data, 8);
        var sectorSize = EndianUtilities.ReadUInt32Little(item.Data, 32);
        if (deviceId == 0 || item.Key.Offset != deviceId)
        {
            throw new InvalidDataException(
                $"Btrfs DEVICE_ITEMのdevidがkeyと一致しません: key={item.Key.Offset}, devid={deviceId}");
        }

        if (totalBytes < SuperblockSize || sectorSize != (uint)_sectorSize)
        {
            throw new InvalidDataException(
                $"Btrfs DEVICE_ITEMの容量またはsector sizeが不正です: "
                + $"devid={deviceId}, bytes={totalBytes:N0}, sector={sectorSize}");
        }

        if (!item.Data.AsSpan(82, 16).SequenceEqual(_fileSystemId))
        {
            throw new InvalidDataException($"Btrfs DEVICE_ITEMのFSIDが一致しません: devid={deviceId}");
        }

        var uuid = item.Data.AsSpan(66, 16).ToArray();
        if (uuid.AsSpan().IndexOfAnyExcept((byte)0) < 0)
        {
            throw new InvalidDataException($"Btrfs DEVICE_ITEMのdevice UUIDが空です: devid={deviceId}");
        }

        return new BtrfsDeviceItem(deviceId, totalBytes, uuid);
    }

    private void AddDeviceItem(BtrfsDeviceItem deviceItem)
    {
        if (!_deviceItems.TryAdd(deviceItem.DeviceId, deviceItem))
        {
            throw new InvalidDataException(
                $"Btrfs chunk treeに重複したDEVICE_ITEMがあります: devid={deviceItem.DeviceId}");
        }
    }

    private void ValidateDeviceItems(ulong expectedDeviceCount)
    {
        if ((ulong)_deviceItems.Count != expectedDeviceCount)
        {
            throw new InvalidDataException(
                $"Btrfs DEVICE_ITEM数がsuperblockと一致しません: "
                + $"expected={expectedDeviceCount}, actual={_deviceItems.Count}");
        }

        var extra = _devices.Keys
            .Where(deviceId => !_deviceItems.ContainsKey(deviceId))
            .Order()
            .ToArray();
        if (extra.Length > 0)
        {
            throw new InvalidDataException(
                $"Btrfs chunk treeに存在しないdeviceが指定されました: devid={string.Join(",", extra)}");
        }

        foreach (var item in _deviceItems.Values)
        {
            if (!_devices.TryGetValue(item.DeviceId, out var device))
            {
                continue;
            }

            if (!item.Uuid.SequenceEqual(device.Uuid))
            {
                throw new InvalidDataException(
                    $"Btrfs DEVICE_ITEMとsuperblockのdevice UUIDが一致しません: devid={item.DeviceId}");
            }

            if (item.TotalBytes > (ulong)device.Reader.Length)
            {
                throw new InvalidDataException(
                    $"Btrfs DEVICE_ITEMのdevice容量がreader範囲外です: "
                    + $"devid={item.DeviceId}, bytes={item.TotalBytes:N0}");
            }
        }

        _missingDeviceIds = _deviceItems.Keys
            .Where(deviceId => !_devices.ContainsKey(deviceId))
            .Order()
            .ToArray();
    }

    private void AddChunk(BtrfsChunk chunk)
    {
        var existing = _chunks.FirstOrDefault(item => item.LogicalStart == chunk.LogicalStart);
        if (existing is not null)
        {
            if (!HaveMatchingChunkMapping(existing, chunk))
            {
                throw new InvalidDataException($"Btrfs chunk mappingが競合しています: {chunk.LogicalStart:N0}");
            }

            return;
        }

        _chunks.Add(chunk);
    }

    private static bool HaveMatchingChunkMapping(BtrfsChunk left, BtrfsChunk right)
    {
        return left.LogicalStart == right.LogicalStart
            && left.Length == right.Length
            && left.StripeLength == right.StripeLength
            && left.Type == right.Type
            && left.SubStripeCount == right.SubStripeCount
            && left.Stripes.SequenceEqual(right.Stripes);
    }

    private void ValidateChunkMappings()
    {
        _chunks.Sort((left, right) => left.LogicalStart.CompareTo(right.LogicalStart));
        for (var index = 0; index < _chunks.Count; index++)
        {
            var chunk = _chunks[index];
            var logicalEnd = checked(chunk.LogicalStart + chunk.Length);
            for (var stripeIndex = 0; stripeIndex < chunk.Stripes.Count; stripeIndex++)
            {
                var stripe = chunk.Stripes[stripeIndex];
                if (!_deviceItems.TryGetValue(stripe.DeviceId, out var deviceItem))
                {
                    throw new InvalidDataException(
                        $"Btrfs chunkが未知のdeviceを参照しています: devid={stripe.DeviceId}");
                }

                if (!string.Equals(
                    stripe.DeviceUuid,
                    Convert.ToHexString(deviceItem.Uuid),
                    StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"Btrfs chunk stripeとDEVICE_ITEMのUUIDが一致しません: devid={stripe.DeviceId}");
                }

                if (!_devices.TryGetValue(stripe.DeviceId, out var device))
                {
                    continue;
                }

                var physicalEnd = checked(
                    stripe.PhysicalStart + GetStripePhysicalLength(chunk, stripeIndex));
                if (physicalEnd > (ulong)device.Reader.Length || physicalEnd > device.TotalBytes)
                {
                    throw new InvalidDataException(
                        $"Btrfs chunk stripeの物理範囲がdevice外です: "
                        + $"devid={stripe.DeviceId}, end={physicalEnd:N0}");
                }
            }

            var profile = chunk.Type & ChunkProfileMask;
            var hasEnoughDevices = profile == ChunkProfileRaid10
                ? Enumerable.Range(0, chunk.Stripes.Count / chunk.SubStripeCount)
                    .All(groupIndex => chunk.Stripes
                        .Skip(groupIndex * chunk.SubStripeCount)
                        .Take(chunk.SubStripeCount)
                        .Any(stripe => _devices.ContainsKey(stripe.DeviceId)))
                : IsMirroredProfile(profile)
                    ? chunk.Stripes.Any(stripe => _devices.ContainsKey(stripe.DeviceId))
                    : chunk.Stripes.All(stripe => _devices.ContainsKey(stripe.DeviceId));
            if (!hasEnoughDevices)
            {
                var missing = chunk.Stripes
                    .Where(stripe => !_devices.ContainsKey(stripe.DeviceId))
                    .Select(stripe => stripe.DeviceId)
                    .Distinct()
                    .Order();
                throw new InvalidDataException(
                    $"Btrfs chunkを読み取るdeviceが不足しています: "
                    + $"logical={chunk.LogicalStart:N0}, devid={string.Join(",", missing)}");
            }

            if (index > 0)
            {
                var previous = _chunks[index - 1];
                var previousEnd = checked(previous.LogicalStart + previous.Length);
                if (chunk.LogicalStart < previousEnd)
                {
                    throw new InvalidDataException("Btrfs chunkの論理範囲が重複しています。");
                }
            }

            _ = logicalEnd;
        }
    }

    private List<BtrfsLeafItem> ReadTreeItems(
        ulong rootBytenr,
        ulong expectedOwner,
        byte expectedLevel,
        ulong? expectedGeneration = null)
    {
        if (rootBytenr == 0 || expectedLevel > MaximumTreeLevel)
        {
            throw new InvalidDataException("Btrfs tree rootが不正です。");
        }

        var items = new List<BtrfsLeafItem>();
        var visited = new HashSet<ulong>();
        var pending = new Stack<BtrfsTreePointer>();
        pending.Push(new BtrfsTreePointer(rootBytenr, expectedLevel, expectedGeneration));
        while (pending.Count > 0)
        {
            if (visited.Count >= MaximumTreeBlocks)
            {
                throw new NotSupportedException("Btrfs tree block数が安全上限を超えています。");
            }

            var pointer = pending.Pop();
            if (!visited.Add(pointer.Bytenr))
            {
                throw new InvalidDataException($"Btrfs treeに循環または重複参照があります: {pointer.Bytenr:N0}");
            }

            var block = ReadVerifiedTreeBlock(pointer, expectedOwner);

            var itemCount = EndianUtilities.ReadUInt32Little(block, 0x60);
            var level = block[0x64];

            if (level == 0)
            {
                ParseLeaf(block, itemCount, items);
                if (items.Count > MaximumTreeItems)
                {
                    throw new NotSupportedException("Btrfs tree item数が安全上限を超えています。");
                }
            }
            else
            {
                var maximumPointers = (_nodeSize - TreeHeaderSize) / KeyPointerSize;
                if (itemCount == 0 || itemCount > maximumPointers)
                {
                    throw new InvalidDataException($"Btrfs internal node item数が不正です: {itemCount}");
                }

                BtrfsKey? previousKey = null;
                for (var index = checked((int)itemCount) - 1; index >= 0; index--)
                {
                    var offset = TreeHeaderSize + index * KeyPointerSize;
                    var key = ReadKey(block, offset);
                    if (previousKey is not null && CompareKeys(key, previousKey) >= 0)
                    {
                        throw new InvalidDataException("Btrfs internal nodeのkey順序が不正です。");
                    }

                    previousKey = key;
                    var childBytenr = EndianUtilities.ReadUInt64Little(block, offset + KeySize);
                    var childGeneration = EndianUtilities.ReadUInt64Little(block, offset + KeySize + 8);
                    if (childBytenr == 0 || childBytenr % (ulong)_nodeSize != 0)
                    {
                        throw new InvalidDataException("Btrfs internal nodeのchild pointerが不正です。");
                    }

                    pending.Push(new BtrfsTreePointer(childBytenr, checked((byte)(level - 1)), childGeneration));
                }
            }
        }

        items.Sort((left, right) => CompareKeys(left.Key, right.Key));
        return items;
    }

    private byte[] ReadVerifiedTreeBlock(BtrfsTreePointer pointer, ulong expectedOwner)
    {
        var chunk = FindChunk(pointer.Bytenr);
        var chunkEnd = checked(chunk.LogicalStart + chunk.Length);
        if (pointer.Bytenr > chunkEnd || (ulong)_nodeSize > chunkEnd - pointer.Bytenr)
        {
            throw new InvalidDataException(
                $"Btrfs tree blockがchunk境界をまたいでいます: {pointer.Bytenr:N0}");
        }

        var mirrorCount = GetMirrorCount(chunk);
        var errors = new List<string>(mirrorCount);
        Exception? lastError = null;
        for (var mirrorIndex = 0; mirrorIndex < mirrorCount; mirrorIndex++)
        {
            var block = new byte[_nodeSize];
            try
            {
                ReadLogical(pointer.Bytenr, block, 0, block.Length, mirrorIndex);
                VerifyChecksum(block, $"Btrfs tree block {pointer.Bytenr:N0}");
                ValidateTreeBlockHeader(block, pointer, expectedOwner);
                return block;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                lastError = ex;
                errors.Add($"mirror {mirrorIndex + 1}: {ex.Message}");
            }
        }

        throw new InvalidDataException(
            $"Btrfs tree block {pointer.Bytenr:N0}の全mirror検証に失敗しました: "
            + string.Join(" | ", errors),
            lastError);
    }

    private void ValidateTreeBlockHeader(
        byte[] block,
        BtrfsTreePointer pointer,
        ulong expectedOwner)
    {
        if (!block.AsSpan(0x20, 16).SequenceEqual(_fileSystemId))
        {
            throw new InvalidDataException("Btrfs tree blockのFSIDがsuperblockと一致しません。");
        }

        if (EndianUtilities.ReadUInt64Little(block, 0x30) != pointer.Bytenr)
        {
            throw new InvalidDataException("Btrfs tree blockのbytenrが参照先と一致しません。");
        }

        var generation = EndianUtilities.ReadUInt64Little(block, 0x50);
        if (pointer.Generation is ulong pointerGeneration && generation != pointerGeneration)
        {
            throw new InvalidDataException(
                $"Btrfs tree block generationが一致しません: "
                + $"expected={pointerGeneration}, actual={generation}");
        }

        var owner = EndianUtilities.ReadUInt64Little(block, 0x58);
        if (owner != expectedOwner)
        {
            throw new InvalidDataException(
                $"Btrfs tree block ownerが一致しません: expected={expectedOwner}, actual={owner}");
        }

        var level = block[0x64];
        if (level != pointer.Level || level > MaximumTreeLevel)
        {
            throw new InvalidDataException(
                $"Btrfs tree block levelが一致しません: expected={pointer.Level}, actual={level}");
        }
    }

    private void ParseLeaf(byte[] block, uint itemCountValue, List<BtrfsLeafItem> output)
    {
        var maximumItems = (_nodeSize - TreeHeaderSize) / LeafItemSize;
        if (itemCountValue > maximumItems)
        {
            throw new InvalidDataException($"Btrfs leaf item数が不正です: {itemCountValue}");
        }

        var itemCount = checked((int)itemCountValue);
        var itemAreaEnd = checked(TreeHeaderSize + itemCount * LeafItemSize);
        var ranges = new List<(int Start, int End)>(itemCount);
        BtrfsKey? previousKey = null;
        for (var index = 0; index < itemCount; index++)
        {
            var offset = TreeHeaderSize + index * LeafItemSize;
            var key = ReadKey(block, offset);
            if (previousKey is not null && CompareKeys(previousKey, key) >= 0)
            {
                throw new InvalidDataException("Btrfs leafのkey順序が不正です。");
            }

            previousKey = key;
            var dataOffset = EndianUtilities.ReadUInt32Little(block, offset + KeySize);
            var dataSize = EndianUtilities.ReadUInt32Little(block, offset + KeySize + 4);
            var absoluteOffset = checked(TreeHeaderSize + (int)dataOffset);
            var absoluteEnd = checked(absoluteOffset + (int)dataSize);
            if (absoluteOffset < itemAreaEnd || absoluteEnd > block.Length)
            {
                throw new InvalidDataException("Btrfs leaf itemのdata範囲が不正です。");
            }

            ranges.Add((absoluteOffset, absoluteEnd));
            output.Add(new BtrfsLeafItem(key, block.AsSpan(absoluteOffset, checked((int)dataSize)).ToArray()));
        }

        ranges.Sort((left, right) => left.Start.CompareTo(right.Start));
        for (var index = 1; index < ranges.Count; index++)
        {
            if (ranges[index].Start < ranges[index - 1].End)
            {
                throw new InvalidDataException("Btrfs leaf itemのdata範囲が重複しています。");
            }
        }
    }

    private void ParseFileSystemTree(IReadOnlyList<BtrfsLeafItem> items, ulong treeId)
    {
        foreach (var item in items.Where(item => item.Key.Type == InodeItemKey))
        {
            if (item.Data.Length < 160)
            {
                throw new InvalidDataException("Btrfs inode itemが短すぎます。");
            }

            var size = EndianUtilities.ReadUInt64Little(item.Data, 16);
            var mode = EndianUtilities.ReadUInt32Little(item.Data, 52);
            var reference = new BtrfsObjectReference(treeId, item.Key.ObjectId);
            _inodes.Add(reference, new BtrfsInode(item.Key.ObjectId, size, mode, ReadTimestamp(item.Data, 136)));
        }

        foreach (var item in items.Where(item => item.Key.Type == DirectoryIndexKey))
        {
            var entries = ParseDirectoryEntries(item.Data, item.Key.Offset);
            var reference = new BtrfsObjectReference(treeId, item.Key.ObjectId);
            if (!_directories.TryGetValue(reference, out var directoryEntries))
            {
                directoryEntries = [];
                _directories.Add(reference, directoryEntries);
            }

            directoryEntries.AddRange(entries);
        }

        foreach (var item in items.Where(item => item.Key.Type == ExtentDataKey))
        {
            var extent = ParseFileExtent(item);
            var reference = new BtrfsObjectReference(treeId, item.Key.ObjectId);
            if (!_fileExtents.TryGetValue(reference, out var extents))
            {
                extents = [];
                _fileExtents.Add(reference, extents);
            }

            extents.Add(extent);
        }

        foreach (var pair in _fileExtents)
        {
            pair.Value.Sort((left, right) => left.FileOffset.CompareTo(right.FileOffset));
            ulong previousEnd = 0;
            for (var index = 0; index < pair.Value.Count; index++)
            {
                var extent = pair.Value[index];
                if (index > 0 && extent.FileOffset < previousEnd)
                {
                    throw new InvalidDataException(
                        $"Btrfs tree {pair.Key.TreeId} inode {pair.Key.InodeNumber}のfile extentが重複しています。");
                }

                previousEnd = checked(extent.FileOffset + extent.Length);
            }
        }
    }

    private static IReadOnlyList<BtrfsDirectoryEntry> ParseDirectoryEntries(byte[] data, ulong sequence)
    {
        var entries = new List<BtrfsDirectoryEntry>();
        var offset = 0;
        while (offset < data.Length)
        {
            if (offset + 30 > data.Length)
            {
                throw new InvalidDataException("Btrfs directory itemが途中で終了しています。");
            }

            var location = ReadKey(data, offset);
            var dataLength = EndianUtilities.ReadUInt16Little(data, offset + 25);
            var nameLength = EndianUtilities.ReadUInt16Little(data, offset + 27);
            var fileType = data[offset + 29];
            var totalLength = checked(30 + nameLength + dataLength);
            if (nameLength == 0 || nameLength > 255 || offset + totalLength > data.Length)
            {
                throw new InvalidDataException("Btrfs directory entryの長さが不正です。");
            }

            var name = ReadName(data, offset + 30, nameLength, "Btrfs directory entry");

            entries.Add(new BtrfsDirectoryEntry(
                location.ObjectId,
                location.Type,
                fileType,
                name,
                sequence));
            offset += totalLength;
        }

        return entries;
    }

    private static string ReadName(byte[] data, int offset, int length, string label)
    {
        string name;
        try
        {
            name = StrictUtf8.GetString(data, offset, length);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException($"{label}名がUTF-8ではありません。", ex);
        }

        if (name is "." or ".." || name.IndexOfAny(['/', '\\', '\0']) >= 0)
        {
            throw new InvalidDataException($"{label}名が不正です: {name}");
        }

        return name;
    }

    private BtrfsFileExtent ParseFileExtent(BtrfsLeafItem item)
    {
        var data = item.Data;
        if (data.Length < 21)
        {
            throw new InvalidDataException("Btrfs file extent itemが短すぎます。");
        }

        var ramBytes = EndianUtilities.ReadUInt64Little(data, 8);
        var compression = data[16];
        var encryption = data[17];
        var otherEncoding = EndianUtilities.ReadUInt16Little(data, 18);
        var type = data[20];
        if (type == 0)
        {
            var inlineLength = data.Length - 21;
            if (compression == 0 && ramBytes != (ulong)inlineLength)
            {
                throw new InvalidDataException(
                    $"Btrfs inline extent sizeが一致しません: ram={ramBytes}, data={inlineLength}");
            }

            var inlineData = data.AsSpan(21).ToArray();
            if (compression == 1)
            {
                inlineData = DecodeZlib(inlineData, ramBytes, "Btrfs inline zlib extent");
            }
            else if (compression == 2)
            {
                inlineData = DecodeLzo(inlineData, ramBytes, "Btrfs inline LZO extent", allowSectorPadding: false);
            }
            else if (compression == 3)
            {
                inlineData = DecodeZstd(inlineData, ramBytes, "Btrfs inline zstd extent", allowSectorPadding: false);
            }

            return new BtrfsFileExtent(
                item.Key.Offset,
                ramBytes,
                ramBytes,
                BtrfsFileExtentType.Inline,
                compression,
                encryption,
                otherEncoding,
                0,
                0,
                0,
                inlineData);
        }

        if (type is not 1 and not 2 || data.Length != 53)
        {
            throw new NotSupportedException($"未対応のBtrfs file extent typeまたはsizeです: type={type}, size={data.Length}");
        }

        var diskBytenr = EndianUtilities.ReadUInt64Little(data, 21);
        var diskBytes = EndianUtilities.ReadUInt64Little(data, 29);
        var extentOffset = EndianUtilities.ReadUInt64Little(data, 37);
        var numberOfBytes = EndianUtilities.ReadUInt64Little(data, 45);
        var decodedBytes = compression == 0 ? diskBytes : ramBytes;
        if (numberOfBytes == 0
            || (diskBytenr != 0 && (extentOffset > decodedBytes || numberOfBytes > decodedBytes - extentOffset)))
        {
            throw new InvalidDataException("Btrfs regular extentのdata範囲が不正です。");
        }

        if (compression != 0
            && (type != 1 || diskBytenr == 0 || diskBytes == 0
                || diskBytes > MaximumDecodedExtentSize || ramBytes == 0 || ramBytes > MaximumDecodedExtentSize))
        {
            throw new InvalidDataException(
                $"Btrfs圧縮extentのサイズまたはtypeが不正です: disk={diskBytes}, ram={ramBytes}, type={type}");
        }

        return new BtrfsFileExtent(
            item.Key.Offset,
            numberOfBytes,
            ramBytes,
            type == 1 ? BtrfsFileExtentType.Regular : BtrfsFileExtentType.Preallocated,
            compression,
            encryption,
            otherEncoding,
            diskBytenr,
            diskBytes,
            extentOffset,
            null);
    }

    private void ParseChecksumTree(IReadOnlyList<BtrfsLeafItem> items)
    {
        foreach (var item in items.Where(item =>
                     item.Key.ObjectId == ExtentChecksumObjectId && item.Key.Type == ExtentChecksumKey))
        {
            if (item.Key.Offset % (ulong)_sectorSize != 0 || item.Data.Length == 0 || item.Data.Length % sizeof(uint) != 0)
            {
                throw new InvalidDataException("Btrfs extent checksum itemの範囲が不正です。");
            }

            for (var offset = 0; offset < item.Data.Length; offset += sizeof(uint))
            {
                var logical = checked(item.Key.Offset + (ulong)(offset / sizeof(uint)) * (ulong)_sectorSize);
                var checksum = EndianUtilities.ReadUInt32Little(item.Data, offset);
                if (!_dataChecksums.TryAdd(logical, checksum))
                {
                    throw new InvalidDataException($"Btrfs data checksumが重複しています: {logical:N0}");
                }
            }
        }
    }

    private void ReadVerifiedData(ulong logical, byte[] destination, int destinationOffset, int count)
    {
        var remaining = count;
        while (remaining > 0)
        {
            var sectorStart = logical / (ulong)_sectorSize * (ulong)_sectorSize;
            var withinSector = checked((int)(logical - sectorStart));
            var copyLength = Math.Min(remaining, _sectorSize - withinSector);
            var chunk = FindChunk(sectorStart);
            var chunkEnd = checked(chunk.LogicalStart + chunk.Length);
            if (sectorStart > chunkEnd || (ulong)_sectorSize > chunkEnd - sectorStart)
            {
                throw new InvalidDataException(
                    $"Btrfs data sectorがchunk境界をまたいでいます: logical={sectorStart:N0}");
            }

            var mirrorCount = GetMirrorCount(chunk);
            var errors = new List<string>(mirrorCount);
            Exception? lastError = null;
            byte[]? verifiedSector = null;
            var hasChecksum = _dataChecksums.TryGetValue(sectorStart, out var expectedChecksum);
            for (var mirrorIndex = 0; mirrorIndex < mirrorCount; mirrorIndex++)
            {
                var candidate = new byte[_sectorSize];
                try
                {
                    ReadLogical(sectorStart, candidate, 0, candidate.Length, mirrorIndex);
                    if (hasChecksum)
                    {
                        var actualChecksum = BtrfsCrc32C.Compute(candidate);
                        if (actualChecksum != expectedChecksum)
                        {
                            errors.Add(
                                $"mirror {mirrorIndex + 1}: expected=0x{expectedChecksum:X8}, "
                                + $"actual=0x{actualChecksum:X8}");
                            continue;
                        }
                    }

                    verifiedSector = candidate;
                    break;
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException)
                {
                    lastError = ex;
                    errors.Add($"mirror {mirrorIndex + 1}: {ex.Message}");
                }
            }

            if (verifiedSector is null)
            {
                throw new InvalidDataException(
                    (hasChecksum
                        ? $"Btrfs data checksumが全mirrorで一致しません: logical={sectorStart:N0}; "
                        : $"Btrfs dataを全mirrorから読み取れません: logical={sectorStart:N0}; ")
                    + string.Join(" | ", errors),
                    lastError);
            }

            verifiedSector.AsSpan(withinSector, copyLength)
                .CopyTo(destination.AsSpan(destinationOffset, copyLength));
            logical += (ulong)copyLength;
            destinationOffset += copyLength;
            remaining -= copyLength;
        }
    }

    private byte[] ReadCompressedExtent(BtrfsFileExtent extent)
    {
        var key = new BtrfsCompressedExtentKey(
            extent.DiskBytenr,
            extent.DiskBytes,
            extent.RamBytes,
            extent.Compression);
        lock (_decodedExtentCacheLock)
        {
            if (_decodedExtentCache.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }

        var compressed = new byte[checked((int)extent.DiskBytes)];
        ReadVerifiedData(extent.DiskBytenr, compressed, 0, compressed.Length);
        var decoded = extent.Compression switch
        {
            1 => DecodeZlib(compressed, extent.RamBytes, "Btrfs zlib extent"),
            2 => DecodeLzo(compressed, extent.RamBytes, "Btrfs LZO extent", allowSectorPadding: true),
            3 => DecodeZstd(compressed, extent.RamBytes, "Btrfs zstd extent", allowSectorPadding: true),
            _ => throw new NotSupportedException($"未対応のBtrfs圧縮方式です: {extent.Compression}")
        };
        lock (_decodedExtentCacheLock)
        {
            if (_decodedExtentCache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            _decodedExtentCache.Add(key, decoded);
            return decoded;
        }
    }

    private static byte[] DecodeZlib(byte[] compressed, ulong decodedLength, string label)
    {
        if (decodedLength == 0 || decodedLength > MaximumDecodedExtentSize)
        {
            throw new InvalidDataException($"{label}の展開後サイズが不正です: {decodedLength:N0}");
        }

        var output = new byte[checked((int)decodedLength)];
        try
        {
            using var input = new MemoryStream(compressed, writable: false);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress, leaveOpen: false);
            var total = 0;
            while (total < output.Length)
            {
                var read = zlib.Read(output, total, output.Length - total);
                if (read == 0)
                {
                    throw new InvalidDataException(
                        $"{label}が途中で終了しました: expected={output.Length:N0}, actual={total:N0}");
                }

                total += read;
            }

            if (zlib.ReadByte() != -1)
            {
                throw new InvalidDataException($"{label}の展開結果が宣言サイズを超えています。");
            }
        }
        catch (InvalidDataException ex) when (!ex.Message.StartsWith(label, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{label}を展開できませんでした: {ex.Message}", ex);
        }

        return output;
    }

    private byte[] DecodeLzo(
        byte[] compressed,
        ulong decodedLength,
        string label,
        bool allowSectorPadding)
    {
        if (decodedLength == 0 || decodedLength > MaximumDecodedExtentSize)
        {
            throw new InvalidDataException($"{label}の展開後サイズが不正です: {decodedLength:N0}");
        }

        if (compressed.Length < 11)
        {
            throw new InvalidDataException($"{label}が短すぎます。");
        }

        var totalLengthValue = EndianUtilities.ReadUInt32Little(compressed, 0);
        if (totalLengthValue < 11 || totalLengthValue > compressed.Length)
        {
            throw new InvalidDataException(
                $"{label}のtotal lengthが不正です: total={totalLengthValue:N0}, input={compressed.Length:N0}");
        }

        var totalLength = checked((int)totalLengthValue);
        if (allowSectorPadding)
        {
            var allocatedLength = checked((totalLength + _sectorSize - 1) / _sectorSize * _sectorSize);
            if (allocatedLength != compressed.Length)
            {
                throw new InvalidDataException(
                    $"{label}の割当サイズが不正です: total={totalLength:N0}, allocated={compressed.Length:N0}");
            }
        }
        else if (totalLength != compressed.Length)
        {
            throw new InvalidDataException(
                $"{label}のinline sizeが一致しません: total={totalLength:N0}, input={compressed.Length:N0}");
        }

        if (compressed.AsSpan(totalLength).IndexOfAnyExcept((byte)0) >= 0)
        {
            throw new InvalidDataException($"{label}の割当末尾paddingがゼロではありません。");
        }

        var output = new byte[checked((int)decodedLength)];
        var inputOffset = sizeof(uint);
        var outputOffset = 0;
        var segmentCount = 0;
        var maximumSegmentLength = checked(_sectorSize + _sectorSize / 16 + 67);
        while (inputOffset < totalLength)
        {
            var withinSector = inputOffset % _sectorSize;
            if (withinSector > _sectorSize - sizeof(uint) || inputOffset > totalLength - sizeof(uint))
            {
                throw new InvalidDataException($"{label}のsegment header位置が不正です: {inputOffset:N0}");
            }

            var segmentLengthValue = EndianUtilities.ReadUInt32Little(compressed, inputOffset);
            inputOffset += sizeof(uint);
            if (segmentLengthValue == 0
                || segmentLengthValue > maximumSegmentLength
                || segmentLengthValue > totalLength - inputOffset)
            {
                throw new InvalidDataException(
                    $"{label}のsegment lengthが不正です: {segmentLengthValue:N0}");
            }

            var segmentLength = checked((int)segmentLengthValue);
            var expectedOutputLength = Math.Min(_sectorSize, output.Length - outputOffset);
            if (expectedOutputLength <= 0)
            {
                throw new InvalidDataException($"{label}のsegment数が展開後サイズを超えています。");
            }

            try
            {
                var written = Lzo1xDecoder.Decompress(
                    compressed.AsSpan(inputOffset, segmentLength),
                    output.AsSpan(outputOffset, expectedOutputLength));
                if (written != expectedOutputLength)
                {
                    throw new InvalidDataException(
                        $"{label}のsegment展開サイズが一致しません: "
                        + $"expected={expectedOutputLength:N0}, actual={written:N0}");
                }
            }
            catch (InvalidDataException ex) when (!ex.Message.StartsWith(label, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"{label}のsegmentを展開できませんでした: {ex.Message}", ex);
            }

            inputOffset += segmentLength;
            outputOffset += expectedOutputLength;
            segmentCount++;
            if (inputOffset >= totalLength)
            {
                break;
            }

            var bytesLeftInSector = _sectorSize - inputOffset % _sectorSize;
            if (bytesLeftInSector is > 0 and < sizeof(uint))
            {
                if (inputOffset > totalLength - bytesLeftInSector
                    || compressed.AsSpan(inputOffset, bytesLeftInSector).IndexOfAnyExcept((byte)0) >= 0)
                {
                    throw new InvalidDataException($"{label}のsegment paddingが不正です。");
                }

                inputOffset += bytesLeftInSector;
            }
        }

        if (inputOffset != totalLength || outputOffset != output.Length)
        {
            throw new InvalidDataException(
                $"{label}の最終サイズが一致しません: input={inputOffset:N0}/{totalLength:N0}, "
                + $"output={outputOffset:N0}/{output.Length:N0}");
        }

        if (!allowSectorPadding && segmentCount != 1)
        {
            throw new InvalidDataException($"{label}には複数segmentを格納できません。");
        }

        return output;
    }

    private static byte[] DecodeZstd(
        byte[] compressed,
        ulong decodedLength,
        string label,
        bool allowSectorPadding)
    {
        if (decodedLength == 0 || decodedLength > MaximumDecodedExtentSize)
        {
            throw new InvalidDataException($"{label}の展開後サイズが不正です: {decodedLength:N0}");
        }

        var frameLength = GetZstdFrameLength(compressed, decodedLength, label);
        var trailing = compressed.AsSpan(frameLength);
        if (!allowSectorPadding && trailing.Length != 0)
        {
            throw new InvalidDataException($"{label}のinline入力末尾に余分なデータがあります。");
        }

        if (trailing.IndexOfAnyExcept((byte)0) >= 0)
        {
            throw new InvalidDataException($"{label}の割当末尾paddingがゼロではありません。");
        }

        var decodeBuffer = new byte[checked((int)decodedLength + 1)];
        int consumed;
        int written;
        OperationStatus status;
        try
        {
            using var decompressor = new Decompressor();
            status = decompressor.UnwrapStream(
                compressed.AsSpan(0, frameLength),
                decodeBuffer,
                out consumed,
                out written);
        }
        catch (ZstdException ex)
        {
            throw new InvalidDataException($"{label}を展開できませんでした: {ex.Message}", ex);
        }

        if (status != OperationStatus.Done || written != (int)decodedLength)
        {
            throw new InvalidDataException(
                $"{label}の展開結果が不正です: status={status}, "
                + $"expected={decodedLength:N0}, actual={written:N0}");
        }

        if (consumed != frameLength)
        {
            throw new InvalidDataException(
                $"{label}の入力消費量が不正です: consumed={consumed:N0}, frame={frameLength:N0}");
        }

        return decodeBuffer.AsSpan(0, written).ToArray();
    }

    private static int GetZstdFrameLength(ReadOnlySpan<byte> input, ulong decodedLength, string label)
    {
        const uint zstdMagic = 0xFD2FB528;

        var offset = 0;
        EnsureZstdAvailable(input.Length, offset, sizeof(uint), label, "magic");
        if (BinaryPrimitives.ReadUInt32LittleEndian(input) != zstdMagic)
        {
            throw new InvalidDataException($"{label}のmagicが不正です。");
        }

        offset += sizeof(uint);
        EnsureZstdAvailable(input.Length, offset, sizeof(byte), label, "frame header descriptor");
        var descriptor = input[offset++];
        if ((descriptor & 0x08) != 0)
        {
            throw new InvalidDataException($"{label}のreserved bitが設定されています。");
        }

        var contentSizeFlag = descriptor >> 6;
        var singleSegment = (descriptor & 0x20) != 0;
        var hasChecksum = (descriptor & 0x04) != 0;
        var dictionaryIdSize = (descriptor & 0x03) switch
        {
            0 => 0,
            1 => 1,
            2 => 2,
            _ => 4,
        };
        var contentSizeFieldSize = contentSizeFlag switch
        {
            0 when singleSegment => 1,
            0 => 0,
            1 => 2,
            2 => 4,
            _ => 8,
        };

        ulong windowSize;
        if (singleSegment)
        {
            windowSize = 0;
        }
        else
        {
            EnsureZstdAvailable(input.Length, offset, sizeof(byte), label, "window descriptor");
            var windowDescriptor = input[offset++];
            var windowLog = 10 + (windowDescriptor >> 3);
            var windowBase = 1UL << windowLog;
            windowSize = checked(windowBase + windowBase / 8 * (ulong)(windowDescriptor & 0x07));
            if (windowSize > MaximumDecodedExtentSize)
            {
                throw new InvalidDataException($"{label}のwindowが128 KiBを超えています: {windowSize:N0}");
            }
        }

        EnsureZstdAvailable(input.Length, offset, dictionaryIdSize, label, "dictionary ID");
        var dictionaryId = ReadUnsignedLittleEndian(input.Slice(offset, dictionaryIdSize));
        offset += dictionaryIdSize;
        if (dictionaryId != 0)
        {
            throw new InvalidDataException($"{label}は外部dictionaryを要求しています: {dictionaryId}");
        }

        if (contentSizeFieldSize == 0)
        {
            throw new InvalidDataException($"{label}に展開後サイズが記録されていません。");
        }

        EnsureZstdAvailable(input.Length, offset, contentSizeFieldSize, label, "frame content size");
        var frameContentSize = ReadUnsignedLittleEndian(input.Slice(offset, contentSizeFieldSize));
        offset += contentSizeFieldSize;
        if (contentSizeFieldSize == 2)
        {
            frameContentSize = checked(frameContentSize + 256);
        }

        if (frameContentSize != decodedLength)
        {
            throw new InvalidDataException(
                $"{label}の展開後サイズがextent情報と一致しません: "
                + $"frame={frameContentSize:N0}, extent={decodedLength:N0}");
        }

        if (singleSegment)
        {
            windowSize = frameContentSize;
            if (windowSize > MaximumDecodedExtentSize)
            {
                throw new InvalidDataException($"{label}のwindowが128 KiBを超えています: {windowSize:N0}");
            }
        }

        var blockMaximumSize = Math.Min(windowSize, (ulong)MaximumDecodedExtentSize);
        var blockCount = 0;
        while (true)
        {
            EnsureZstdAvailable(input.Length, offset, 3, label, "block header");
            var blockHeader = input[offset]
                | input[offset + 1] << 8
                | input[offset + 2] << 16;
            offset += 3;

            var lastBlock = (blockHeader & 1) != 0;
            var blockType = (blockHeader >> 1) & 0x03;
            var blockSize = (ulong)(blockHeader >> 3);
            if (blockType == 3)
            {
                throw new InvalidDataException($"{label}にreserved block typeが含まれています。");
            }

            if (blockSize > blockMaximumSize)
            {
                throw new InvalidDataException(
                    $"{label}のblockがwindowを超えています: block={blockSize:N0}, max={blockMaximumSize:N0}");
            }

            var storedSize = blockType == 1 ? 1 : checked((int)blockSize);
            EnsureZstdAvailable(input.Length, offset, storedSize, label, "block content");
            offset += storedSize;
            blockCount++;
            if (lastBlock)
            {
                break;
            }
        }

        if (blockCount == 0)
        {
            throw new InvalidDataException($"{label}にblockがありません。");
        }

        if (hasChecksum)
        {
            EnsureZstdAvailable(input.Length, offset, sizeof(uint), label, "content checksum");
            offset += sizeof(uint);
        }

        return offset;
    }

    private static void EnsureZstdAvailable(
        int inputLength,
        int offset,
        int length,
        string label,
        string field)
    {
        if (length < 0 || offset < 0 || offset > inputLength - length)
        {
            throw new InvalidDataException($"{label}の{field}が入力範囲外です。");
        }
    }

    private static ulong ReadUnsignedLittleEndian(ReadOnlySpan<byte> value)
    {
        return value.Length switch
        {
            0 => 0,
            1 => value[0],
            2 => BinaryPrimitives.ReadUInt16LittleEndian(value),
            4 => BinaryPrimitives.ReadUInt32LittleEndian(value),
            8 => BinaryPrimitives.ReadUInt64LittleEndian(value),
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };
    }

    private void ReadLogical(
        ulong logical,
        byte[] destination,
        int destinationOffset,
        int count,
        int mirrorIndex = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(mirrorIndex);
        var remaining = count;
        while (remaining > 0)
        {
            var chunk = FindChunk(logical);
            var mirrorCount = GetMirrorCount(chunk);
            if (mirrorIndex >= mirrorCount)
            {
                throw new InvalidDataException(
                    $"Btrfs chunkに要求されたmirrorがありません: "
                    + $"logical={logical:N0}, mirror={mirrorIndex + 1}");
            }

            var withinChunk = logical - chunk.LogicalStart;
            var available = chunk.Length - withinChunk;
            var mapping = MapStripe(chunk, withinChunk, mirrorIndex);
            var readLength = checked((int)Math.Min(
                (ulong)remaining,
                Math.Min(available, mapping.AvailableLength)));
            var stripe = mapping.Stripe;
            var physical = mapping.Physical;
            if (!_devices.TryGetValue(stripe.DeviceId, out var device))
            {
                throw new IOException($"Btrfs deviceがありません: devid={stripe.DeviceId}");
            }

            var physicalEnd = checked(physical + (ulong)readLength);
            if (physicalEnd > (ulong)device.Reader.Length
                || physicalEnd > device.TotalBytes)
            {
                throw new InvalidDataException(
                    $"Btrfs logical mappingがdevice外を参照しています: devid={stripe.DeviceId}");
            }

            device.Reader.ReadAt(checked((long)physical), destination, destinationOffset, readLength);
            logical += (ulong)readLength;
            destinationOffset += readLength;
            remaining -= readLength;
        }
    }

    private static BtrfsStripeMapping MapStripe(
        BtrfsChunk chunk,
        ulong withinChunk,
        int mirrorIndex)
    {
        var profile = chunk.Type & ChunkProfileMask;
        if (profile != ChunkProfileRaid10)
        {
            return new BtrfsStripeMapping(
                chunk.Stripes[mirrorIndex],
                checked(chunk.Stripes[mirrorIndex].PhysicalStart + withinChunk),
                chunk.Length - withinChunk);
        }

        var dataStripeCount = chunk.Stripes.Count / chunk.SubStripeCount;
        var logicalStripeNumber = withinChunk / chunk.StripeLength;
        var withinStripe = withinChunk % chunk.StripeLength;
        var groupIndex = checked((int)(logicalStripeNumber % (ulong)dataStripeCount));
        var stripeIndex = groupIndex * chunk.SubStripeCount + mirrorIndex;
        var physicalStripeNumber = logicalStripeNumber / (ulong)dataStripeCount;
        var stripe = chunk.Stripes[stripeIndex];
        var physical = checked(
            stripe.PhysicalStart
            + physicalStripeNumber * chunk.StripeLength
            + withinStripe);
        return new BtrfsStripeMapping(
            stripe,
            physical,
            chunk.StripeLength - withinStripe);
    }

    private static ulong GetStripePhysicalLength(BtrfsChunk chunk, int stripeIndex)
    {
        if ((chunk.Type & ChunkProfileMask) != ChunkProfileRaid10)
        {
            return chunk.Length;
        }

        var dataStripeCount = chunk.Stripes.Count / chunk.SubStripeCount;
        var groupIndex = stripeIndex / chunk.SubStripeCount;
        var logicalStripeCount = checked(
            (chunk.Length + chunk.StripeLength - 1) / chunk.StripeLength);
        if ((ulong)groupIndex >= logicalStripeCount)
        {
            return 0;
        }

        var assignedStripeCount = checked(
            (logicalStripeCount - 1 - (ulong)groupIndex) / (ulong)dataStripeCount + 1);
        var lastLogicalStripe = checked(
            (ulong)groupIndex + (assignedStripeCount - 1) * (ulong)dataStripeCount);
        var lastStripeLength = lastLogicalStripe == logicalStripeCount - 1
            ? chunk.Length - lastLogicalStripe * chunk.StripeLength
            : chunk.StripeLength;
        return checked((assignedStripeCount - 1) * chunk.StripeLength + lastStripeLength);
    }

    private static int GetMirrorCount(BtrfsChunk chunk)
    {
        return (chunk.Type & ChunkProfileMask) == ChunkProfileRaid10
            ? chunk.SubStripeCount
            : IsMirroredProfile(chunk.Type & ChunkProfileMask)
                ? chunk.Stripes.Count
                : 1;
    }

    private BtrfsChunk FindChunk(ulong logical)
    {
        var left = 0;
        var right = _chunks.Count - 1;
        while (left <= right)
        {
            var middle = left + (right - left) / 2;
            var chunk = _chunks[middle];
            if (logical < chunk.LogicalStart)
            {
                right = middle - 1;
            }
            else if (logical >= checked(chunk.LogicalStart + chunk.Length))
            {
                left = middle + 1;
            }
            else
            {
                return chunk;
            }
        }

        throw new InvalidDataException($"Btrfs logical addressに対応するchunkがありません: {logical:N0}");
    }

    private static Dictionary<ulong, BtrfsTreeRoot> ParseFileSystemRoots(
        IReadOnlyList<BtrfsLeafItem> rootItems,
        BtrfsTreeRoot fileSystemRoot)
    {
        var roots = new Dictionary<ulong, BtrfsTreeRoot>
        {
            [FileSystemTreeObjectId] = fileSystemRoot,
        };
        var subvolumeIds = rootItems
            .Where(item => item.Key.Type == RootItemKey
                && item.Key.ObjectId >= FirstFreeObjectId
                && item.Key.ObjectId <= LastFreeObjectId)
            .Select(item => item.Key.ObjectId)
            .Distinct()
            .ToArray();
        foreach (var subvolumeId in subvolumeIds)
        {
            var root = ParseTreeRoot(rootItems, subvolumeId, $"subvolume {subvolumeId}");
            if (root.RootDirectoryId < FirstFreeObjectId || root.Generation == 0)
            {
                throw new InvalidDataException($"Btrfs subvolume ROOT_ITEMが不正です: tree={subvolumeId}");
            }

            roots.Add(subvolumeId, root);
        }

        return roots;
    }

    private static IReadOnlyList<BtrfsRootReference> ParseRootReferences(
        IReadOnlyList<BtrfsLeafItem> rootItems,
        IReadOnlyDictionary<ulong, BtrfsTreeRoot> roots)
    {
        var forwardReferences = rootItems
            .Where(item => item.Key.Type == RootReferenceKey)
            .Select(item => ParseRootReference(item, isBackReference: false))
            .ToArray();
        var backReferences = rootItems
            .Where(item => item.Key.Type == RootBackReferenceKey)
            .Select(item => ParseRootReference(item, isBackReference: true))
            .ToArray();
        if (forwardReferences.Length != backReferences.Length
            || !forwardReferences.ToHashSet().SetEquals(backReferences))
        {
            throw new InvalidDataException("Btrfs ROOT_REFとROOT_BACKREFが一致しません。");
        }

        var parentByChild = new Dictionary<ulong, ulong>();
        foreach (var reference in forwardReferences)
        {
            if (reference.ParentTreeId == reference.ChildTreeId
                || reference.ChildTreeId == FileSystemTreeObjectId
                || !roots.ContainsKey(reference.ParentTreeId)
                || !roots.ContainsKey(reference.ChildTreeId))
            {
                throw new InvalidDataException(
                    $"Btrfs root referenceのtree IDが不正です: "
                    + $"parent={reference.ParentTreeId}, child={reference.ChildTreeId}");
            }

            if (!parentByChild.TryAdd(reference.ChildTreeId, reference.ParentTreeId))
            {
                throw new InvalidDataException(
                    $"Btrfs subvolumeが複数の親から参照されています: {reference.ChildTreeId}");
            }
        }

        foreach (var child in parentByChild.Keys)
        {
            var path = new HashSet<ulong>();
            var current = child;
            while (parentByChild.TryGetValue(current, out var parent))
            {
                if (!path.Add(current))
                {
                    throw new InvalidDataException($"Btrfs root referenceに循環があります: tree={current}");
                }

                current = parent;
            }
        }

        return forwardReferences;
    }

    private static BtrfsRootReference ParseRootReference(BtrfsLeafItem item, bool isBackReference)
    {
        if (item.Data.Length < 18)
        {
            throw new InvalidDataException("Btrfs root referenceが短すぎます。");
        }

        var directoryId = EndianUtilities.ReadUInt64Little(item.Data, 0);
        var sequence = EndianUtilities.ReadUInt64Little(item.Data, 8);
        var nameLength = EndianUtilities.ReadUInt16Little(item.Data, 16);
        if (directoryId < FirstFreeObjectId
            || sequence < 2
            || nameLength == 0
            || nameLength > 255
            || item.Data.Length != 18 + nameLength)
        {
            throw new InvalidDataException("Btrfs root referenceのdirectory情報が不正です。");
        }

        var name = ReadName(item.Data, 18, nameLength, "Btrfs root reference");
        return isBackReference
            ? new BtrfsRootReference(item.Key.Offset, item.Key.ObjectId, directoryId, sequence, name)
            : new BtrfsRootReference(item.Key.ObjectId, item.Key.Offset, directoryId, sequence, name);
    }

    private static HashSet<ulong> FindReachableFileSystemTrees(
        IReadOnlyList<BtrfsRootReference> rootReferences)
    {
        var referencesByParent = rootReferences.ToLookup(reference => reference.ParentTreeId);
        var reachable = new HashSet<ulong> { FileSystemTreeObjectId };
        var pending = new Queue<ulong>();
        pending.Enqueue(FileSystemTreeObjectId);
        while (pending.Count > 0)
        {
            var parent = pending.Dequeue();
            foreach (var reference in referencesByParent[parent])
            {
                if (reachable.Add(reference.ChildTreeId))
                {
                    pending.Enqueue(reference.ChildTreeId);
                }
            }
        }

        return reachable;
    }

    private static ulong ParseDefaultTreeId(
        IReadOnlyList<BtrfsLeafItem> rootItems,
        IReadOnlySet<ulong> reachableTreeIds,
        bool hasDefaultSubvolumeFeature)
    {
        var defaultEntries = rootItems
            .Where(item => item.Key.ObjectId == RootTreeDirectoryObjectId
                && item.Key.Type == DirectoryItemKey)
            .SelectMany(item => ParseDirectoryEntries(item.Data, sequence: 0))
            .Where(entry => entry.Name == "default")
            .ToArray();
        if (defaultEntries.Length == 0)
        {
            if (hasDefaultSubvolumeFeature)
            {
                throw new InvalidDataException("Btrfs default subvolumeのdirectory entryがありません。");
            }

            return FileSystemTreeObjectId;
        }

        if (defaultEntries.Length != 1)
        {
            throw new InvalidDataException("Btrfs default subvolumeのdirectory entryが重複しています。");
        }

        var entry = defaultEntries[0];
        if (entry.LocationType != RootItemKey
            || entry.FileType != 2
            || !reachableTreeIds.Contains(entry.ObjectId)
            || (entry.ObjectId != FileSystemTreeObjectId && !hasDefaultSubvolumeFeature))
        {
            throw new InvalidDataException(
                $"Btrfs default subvolumeの参照先が不正です: tree={entry.ObjectId}");
        }

        return entry.ObjectId;
    }

    private void ValidateRootReferenceDirectoryEntries(
        IReadOnlyList<BtrfsRootReference> rootReferences,
        IReadOnlySet<ulong> reachableTreeIds,
        IReadOnlyDictionary<ulong, BtrfsTreeRoot> roots)
    {
        foreach (var reference in rootReferences.Where(
            reference => reachableTreeIds.Contains(reference.ParentTreeId)))
        {
            _subvolumeLinks.Add(new BtrfsSubvolumeLink(
                reference.ParentTreeId,
                reference.DirectoryId,
                reference.ChildTreeId,
                reference.Sequence,
                reference.Name));
            var directory = new BtrfsObjectReference(reference.ParentTreeId, reference.DirectoryId);
            if (!_directories.TryGetValue(directory, out var entries)
                || entries.Count(entry =>
                    entry.LocationType == RootItemKey
                    && entry.ObjectId == reference.ChildTreeId
                    && entry.FileType == 2
                    && entry.Sequence == reference.Sequence
                    && entry.Name == reference.Name) != 1)
            {
                throw new InvalidDataException(
                    $"Btrfs root referenceに対応するdirectory entryがありません: "
                    + $"parent={reference.ParentTreeId}, child={reference.ChildTreeId}, name={reference.Name}");
            }
        }

        foreach (var pair in _directories.Where(pair => reachableTreeIds.Contains(pair.Key.TreeId)))
        {
            foreach (var entry in pair.Value.Where(entry => entry.LocationType == RootItemKey))
            {
                var hasRootReference = _subvolumeLinks.Contains(new BtrfsSubvolumeLink(
                    pair.Key.TreeId,
                    pair.Key.InodeNumber,
                    entry.ObjectId,
                    entry.Sequence,
                    entry.Name));
                if (!hasRootReference
                    && (!roots[pair.Key.TreeId].IsSnapshot
                        || entry.FileType != 2
                        || !roots.ContainsKey(entry.ObjectId)))
                {
                    throw new InvalidDataException(
                        $"Btrfs subvolume directory entryに対応するROOT_REFがありません: {entry.Name}");
                }
            }
        }
    }

    private static BtrfsTreeRoot ParseTreeRoot(
        IReadOnlyList<BtrfsLeafItem> rootItems,
        ulong objectId,
        string label)
    {
        return TryParseTreeRoot(rootItems, objectId)
            ?? throw new InvalidDataException($"Btrfs root treeに{label}のROOT_ITEMがありません。");
    }

    private static BtrfsTreeRoot? TryParseTreeRoot(IReadOnlyList<BtrfsLeafItem> rootItems, ulong objectId)
    {
        var item = rootItems
            .Where(candidate => candidate.Key.ObjectId == objectId && candidate.Key.Type == RootItemKey)
            .OrderByDescending(candidate => candidate.Key.Offset)
            .FirstOrDefault();
        if (item is null)
        {
            return null;
        }

        if (item.Data.Length < 239)
        {
            throw new InvalidDataException($"Btrfs ROOT_ITEMが短すぎます: objectid={objectId}");
        }

        var generation = EndianUtilities.ReadUInt64Little(item.Data, 160);
        var rootDirectoryId = EndianUtilities.ReadUInt64Little(item.Data, 168);
        var bytenr = EndianUtilities.ReadUInt64Little(item.Data, 176);
        var level = item.Data[238];
        if (generation == 0 || bytenr == 0 || level > MaximumTreeLevel)
        {
            throw new InvalidDataException($"Btrfs ROOT_ITEMのtree rootが不正です: objectid={objectId}");
        }

        var isSnapshot = item.Key.Offset != 0;
        if (item.Data.Length >= 279)
        {
            var generationV2 = EndianUtilities.ReadUInt64Little(item.Data, 239);
            if (generationV2 == generation)
            {
                isSnapshot = item.Data.AsSpan(263, 16).IndexOfAnyExcept((byte)0) >= 0;
            }
        }

        return new BtrfsTreeRoot(rootDirectoryId, bytenr, level, generation, isSnapshot);
    }

    private static DateTime? ReadTimestamp(byte[] data, int offset)
    {
        var seconds = EndianUtilities.ReadInt64Little(data, offset);
        var nanoseconds = EndianUtilities.ReadUInt32Little(data, offset + 8);
        if (nanoseconds >= 1_000_000_000)
        {
            throw new InvalidDataException($"Btrfs timestampのnanosecondsが不正です: {nanoseconds}");
        }

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds).AddTicks(nanoseconds / 100).UtcDateTime;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static BtrfsKey ReadKey(byte[] data, int offset)
    {
        return new BtrfsKey(
            EndianUtilities.ReadUInt64Little(data, offset),
            data[offset + 8],
            EndianUtilities.ReadUInt64Little(data, offset + 9));
    }

    private static int CompareKeys(BtrfsKey left, BtrfsKey right)
    {
        var objectIdComparison = left.ObjectId.CompareTo(right.ObjectId);
        if (objectIdComparison != 0)
        {
            return objectIdComparison;
        }

        var typeComparison = left.Type.CompareTo(right.Type);
        return typeComparison != 0 ? typeComparison : left.Offset.CompareTo(right.Offset);
    }

    private static void VerifyChecksum(byte[] data, string label)
    {
        var expected = EndianUtilities.ReadUInt32Little(data, 0);
        var actual = BtrfsCrc32C.Compute(data.AsSpan(32));
        if (expected != actual)
        {
            throw new InvalidDataException(
                $"{label}のCRC32Cが一致しません: expected=0x{expected:X8}, actual=0x{actual:X8}");
        }
    }

    private static bool IsPowerOfTwo(uint value) => value != 0 && (value & (value - 1)) == 0;

    private static bool IsMirroredProfile(ulong profile)
    {
        return profile is ChunkProfileRaid1 or ChunkProfileRaid10 or ChunkProfileRaid1C3 or ChunkProfileRaid1C4;
    }

    private static string GetProfileName(ulong profile)
    {
        return profile switch
        {
            ChunkProfileRaid1 => "RAID1",
            ChunkProfileRaid10 => "RAID10",
            ChunkProfileRaid1C3 => "RAID1C3",
            ChunkProfileRaid1C4 => "RAID1C4",
            _ => "single",
        };
    }

    private static long ToLongSize(ulong value) => value > long.MaxValue ? long.MaxValue : (long)value;

    private sealed record BtrfsKey(ulong ObjectId, byte Type, ulong Offset);
    private sealed record BtrfsLeafItem(BtrfsKey Key, byte[] Data);
    private sealed record BtrfsTreePointer(ulong Bytenr, byte Level, ulong? Generation);
    private sealed record BtrfsTreeRoot(
        ulong RootDirectoryId,
        ulong Bytenr,
        byte Level,
        ulong Generation,
        bool IsSnapshot);
    private sealed record BtrfsDevice(ulong DeviceId, IBlockReader Reader, byte[] Uuid, ulong TotalBytes);
    private sealed record BtrfsDeviceItem(ulong DeviceId, ulong TotalBytes, byte[] Uuid);
    private readonly record struct BtrfsStripe(
        ulong DeviceId,
        ulong PhysicalStart,
        string DeviceUuid);
    private readonly record struct BtrfsStripeMapping(
        BtrfsStripe Stripe,
        ulong Physical,
        ulong AvailableLength);
    private sealed record BtrfsChunk(
        ulong LogicalStart,
        ulong Length,
        ulong StripeLength,
        ulong Type,
        ushort SubStripeCount,
        IReadOnlyList<BtrfsStripe> Stripes);
    private readonly record struct BtrfsObjectReference(ulong TreeId, ulong InodeNumber);
    private sealed record BtrfsNodeReference(BtrfsObjectReference Object);
    private sealed record BtrfsSnapshotBoundaryReference;
    private sealed record BtrfsDirectoryEntry(
        ulong ObjectId,
        byte LocationType,
        byte FileType,
        string Name,
        ulong Sequence);
    private sealed record BtrfsRootReference(
        ulong ParentTreeId,
        ulong ChildTreeId,
        ulong DirectoryId,
        ulong Sequence,
        string Name);
    private sealed record BtrfsSubvolumeLink(
        ulong ParentTreeId,
        ulong DirectoryId,
        ulong ChildTreeId,
        ulong Sequence,
        string Name);
    private readonly record struct BtrfsCompressedExtentKey(
        ulong DiskBytenr,
        ulong DiskBytes,
        ulong DecodedLength,
        byte Compression);
    private sealed record BtrfsInode(ulong Number, ulong Size, uint Mode, DateTime? ModifiedUtc)
    {
        public bool IsDirectory => (Mode & FileTypeMask) == DirectoryMode;
        public bool IsRegularFile => (Mode & FileTypeMask) == RegularFileMode;
    }

    private sealed record BtrfsFileExtent(
        ulong FileOffset,
        ulong Length,
        ulong RamBytes,
        BtrfsFileExtentType Type,
        byte Compression,
        byte Encryption,
        ushort OtherEncoding,
        ulong DiskBytenr,
        ulong DiskBytes,
        ulong ExtentOffset,
        byte[]? InlineData);

    private enum BtrfsFileExtentType
    {
        Inline,
        Regular,
        Preallocated
    }
}

public sealed record BtrfsDeviceIdentity(
    string FileSystemId,
    ulong DeviceId,
    string DeviceUuid,
    ulong NumberOfDevices);

internal static class BtrfsCrc32C
{
    private const uint Polynomial = 0x82f63b78;

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var checksum = uint.MaxValue;
        foreach (var value in data)
        {
            checksum ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                checksum = (checksum >> 1) ^ ((checksum & 1) == 0 ? 0 : Polynomial);
            }
        }

        return ~checksum;
    }
}
