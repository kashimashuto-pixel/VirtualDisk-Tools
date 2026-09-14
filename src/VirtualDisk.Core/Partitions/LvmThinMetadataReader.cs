using System.Buffers.Binary;
using Qcow2Explorer.Core;
using Qcow2Explorer.FileSystems;

namespace Qcow2Explorer.Partitions;

internal sealed class LvmThinPoolMetadata
{
    private const int MetadataBlockBytes = 4096;
    private const uint ThinSuperblockChecksumXor = 160774;
    private const uint BTreeChecksumXor = 121107;
    private const uint BitmapChecksumXor = 240779;
    private const uint IndexChecksumXor = 160478;
    private const ulong ThinSuperblockMagic = 27022010;
    private const uint SupportedVersion = 2;
    private const int EntriesPerBitmap = 16320;
    private const int MaximumMetadataBitmaps = 255;
    private const int MaximumTreeDepth = 64;
    private const int MaximumCachedNodes = 4096;
    private readonly IBlockReader _metadataReader;
    private readonly IBlockReader _dataReader;
    private readonly ulong _mappingRoot;
    private readonly ulong _detailsRoot;
    private readonly Dictionary<ulong, ThinBTreeNode> _nodeCache = [];
    private readonly Dictionary<ulong, byte[]> _bitmapCache = [];
    private readonly object _cacheLock = new();
    private readonly ThinSpaceMapIndexEntry[] _metadataIndex;

    public LvmThinPoolMetadata(
        IBlockReader metadataReader,
        IBlockReader dataReader,
        ulong expectedTransactionId,
        ulong expectedDataBlockSizeSectors)
    {
        ArgumentNullException.ThrowIfNull(metadataReader);
        ArgumentNullException.ThrowIfNull(dataReader);
        if (metadataReader.Length < MetadataBlockBytes)
        {
            throw new InvalidDataException("LVM2 thin metadata LVがsuperblockより短いです。");
        }

        _metadataReader = metadataReader;
        _dataReader = dataReader;
        var superblock = EndianUtilities.ReadBytes(metadataReader, 0, MetadataBlockBytes);
        ValidateChecksum(superblock, ThinSuperblockChecksumXor, "thin superblock");

        var flags = ReadUInt32(superblock, 4);
        if ((flags & 1) != 0)
        {
            throw new InvalidDataException("LVM2 thin metadataにneeds-checkフラグがあります。");
        }

        if ((flags & ~1U) != 0)
        {
            throw new NotSupportedException($"LVM2 thin metadata flags 0x{flags:X8}は未対応です。");
        }

        if (ReadUInt64(superblock, 8) != 0)
        {
            throw new InvalidDataException("LVM2 thin superblockのblock numberが0ではありません。");
        }

        if (ReadUInt64(superblock, 32) != ThinSuperblockMagic)
        {
            throw new InvalidDataException("LVM2 thin superblock magicが一致しません。");
        }

        Version = ReadUInt32(superblock, 40);
        if (Version != SupportedVersion)
        {
            throw new NotSupportedException($"LVM2 thin metadata version {Version}は未対応です。");
        }

        TransactionId = ReadUInt64(superblock, 48);
        if (TransactionId != expectedTransactionId)
        {
            throw new InvalidDataException(
                $"LVM2 thin transaction IDがVG metadataと一致しません: pool={expectedTransactionId:N0}, superblock={TransactionId:N0}");
        }

        DataSpaceMap = ReadSpaceMapRoot(superblock.AsSpan(64, 128), "data");
        MetadataSpaceMap = ReadSpaceMapRoot(superblock.AsSpan(192, 128), "metadata");
        _mappingRoot = ReadUInt64(superblock, 320);
        _detailsRoot = ReadUInt64(superblock, 328);
        DataBlockSizeSectors = ReadUInt32(superblock, 336);
        var metadataBlockSizeSectors = ReadUInt32(superblock, 340);
        MetadataBlockCount = ReadUInt64(superblock, 344);
        var incompatibilityFlags = ReadUInt32(superblock, 360);

        if (DataBlockSizeSectors == 0 || DataBlockSizeSectors != expectedDataBlockSizeSectors)
        {
            throw new InvalidDataException(
                $"LVM2 thin data block sizeがVG metadataと一致しません: pool={expectedDataBlockSizeSectors:N0}, superblock={DataBlockSizeSectors:N0} sectors");
        }

        if (metadataBlockSizeSectors != MetadataBlockBytes / 512)
        {
            throw new NotSupportedException(
                $"LVM2 thin metadata block size {metadataBlockSizeSectors:N0} sectorsは未対応です。");
        }

        var availableMetadataBlocks = checked((ulong)metadataReader.Length / MetadataBlockBytes);
        if (MetadataBlockCount == 0 || MetadataBlockCount > availableMetadataBlocks)
        {
            throw new InvalidDataException(
                $"LVM2 thin metadata block数がLV範囲外です: recorded={MetadataBlockCount:N0}, available={availableMetadataBlocks:N0}");
        }

        if (incompatibilityFlags != 0)
        {
            throw new NotSupportedException(
                $"LVM2 thin metadata incompatibility flags 0x{incompatibilityFlags:X8}は未対応です。");
        }

        ValidateMetadataBlockReference(_mappingRoot, "mapping root");
        ValidateMetadataBlockReference(_detailsRoot, "device details root");
        ValidateSpaceMap(DataSpaceMap, false);
        ValidateSpaceMap(MetadataSpaceMap, true);

        _metadataIndex = ReadMetadataIndex();
        foreach (var entry in _metadataIndex)
        {
            ValidateAllocatedMetadataReference(entry.BitmapBlock, "metadata space-map bitmap");
        }

        ValidateAllocatedMetadataReference(MetadataSpaceMap.BitmapRoot, "metadata space-map index");
        ValidateAllocatedMetadataReference(MetadataSpaceMap.RefCountRoot, "metadata space-map ref-count root");
        ValidateAllocatedMetadataReference(DataSpaceMap.BitmapRoot, "data space-map bitmap root");
        ValidateAllocatedMetadataReference(DataSpaceMap.RefCountRoot, "data space-map ref-count root");
        ValidateAllocatedMetadataReference(_mappingRoot, "mapping root");
        ValidateAllocatedMetadataReference(_detailsRoot, "device details root");
        ValidateTreeRoot(DataSpaceMap.BitmapRoot, 16);
        ValidateTreeRoot(DataSpaceMap.RefCountRoot, sizeof(uint));
        ValidateTreeRoot(MetadataSpaceMap.RefCountRoot, sizeof(uint));
        ValidateTreeRoot(_mappingRoot, sizeof(ulong));
        ValidateTreeRoot(_detailsRoot, 24);

        DataBlockBytes = checked((ulong)DataBlockSizeSectors * 512);
        if ((ulong)dataReader.Length % DataBlockBytes != 0)
        {
            throw new InvalidDataException("LVM2 thin pool data LV長がdata block sizeの倍数ではありません。");
        }

        var availableDataBlocks = checked((ulong)dataReader.Length) / DataBlockBytes;
        if (DataSpaceMap.BlockCount == 0 || DataSpaceMap.BlockCount > availableDataBlocks)
        {
            throw new InvalidDataException(
                $"LVM2 thin data space mapがpool data LV範囲外です: recorded={DataSpaceMap.BlockCount:N0}, available={availableDataBlocks:N0}");
        }
    }

    public uint Version { get; }
    public ulong TransactionId { get; }
    public uint DataBlockSizeSectors { get; }
    public ulong DataBlockBytes { get; }
    public ulong MetadataBlockCount { get; }
    public ThinSpaceMapRoot DataSpaceMap { get; }
    public ThinSpaceMapRoot MetadataSpaceMap { get; }

    public LvmThinReader OpenDevice(ulong deviceId, ulong expectedDeviceTransactionId, ulong lengthBytes)
    {
        if (deviceId > uint.MaxValue || lengthBytes == 0 || lengthBytes > long.MaxValue)
        {
            throw new InvalidDataException("LVM2 thin device IDまたは論理長が不正です。");
        }

        var deviceRootValue = Lookup(_mappingRoot, deviceId, sizeof(ulong));
        if (deviceRootValue is null)
        {
            throw new InvalidDataException($"LVM2 thin mapping treeにdevice ID {deviceId:N0}がありません。");
        }

        var deviceRoot = BinaryPrimitives.ReadUInt64LittleEndian(deviceRootValue);
        ValidateMetadataBlockReference(deviceRoot, $"thin device {deviceId:N0} mapping root");
        ValidateTreeRoot(deviceRoot, sizeof(ulong));

        var detailsValue = Lookup(_detailsRoot, deviceId, 24);
        if (detailsValue is null)
        {
            throw new InvalidDataException($"LVM2 thin device details treeにdevice ID {deviceId:N0}がありません。");
        }

        var mappedBlocks = BinaryPrimitives.ReadUInt64LittleEndian(detailsValue.AsSpan(0, 8));
        var deviceTransactionId = BinaryPrimitives.ReadUInt64LittleEndian(detailsValue.AsSpan(8, 8));
        var logicalBlocks = DivideRoundUp(lengthBytes, DataBlockBytes);
        if (mappedBlocks > logicalBlocks || mappedBlocks > DataSpaceMap.AllocatedBlockCount)
        {
            throw new InvalidDataException(
                $"LVM2 thin device {deviceId:N0}のmapped block数が不正です: mapped={mappedBlocks:N0}, logical={logicalBlocks:N0}");
        }

        if (deviceTransactionId != expectedDeviceTransactionId
            || deviceTransactionId > TransactionId)
        {
            throw new InvalidDataException(
                $"LVM2 thin device {deviceId:N0}のtransaction IDがVG metadataと一致しません: LV={expectedDeviceTransactionId:N0}, details={deviceTransactionId:N0}, pool={TransactionId:N0}");
        }

        return new LvmThinReader(this, _dataReader, deviceId, deviceRoot, lengthBytes);
    }

    private void ValidateTreeRoot(ulong root, uint expectedLeafValueSize)
    {
        var node = ReadNode(root);
        var expectedValueSize = node.IsLeaf ? expectedLeafValueSize : sizeof(ulong);
        if (node.ValueSize != expectedValueSize)
        {
            throw new InvalidDataException(
                $"LVM2 thin B-tree root value sizeが不正です: expected={expectedValueSize:N0}, actual={node.ValueSize:N0}");
        }
    }

    internal ulong? LookupDataBlock(ulong deviceId, ulong deviceRoot, ulong logicalBlock)
    {
        var value = Lookup(deviceRoot, logicalBlock, sizeof(ulong));
        if (value is null)
        {
            return null;
        }

        var packed = BinaryPrimitives.ReadUInt64LittleEndian(value);
        var dataBlock = packed >> 24;
        if (dataBlock >= DataSpaceMap.BlockCount)
        {
            throw new InvalidDataException(
                $"LVM2 thin device {deviceId:N0}のdata blockがspace map範囲外です: {dataBlock:N0}");
        }

        if (ReadDataReferenceCount(dataBlock) == 0)
        {
            throw new InvalidDataException(
                $"LVM2 thin device {deviceId:N0}のdata block {dataBlock:N0}がspace mapで未割当です。");
        }

        var dataEnd = checked((dataBlock + 1) * DataBlockBytes);
        if (dataEnd > (ulong)_dataReader.Length)
        {
            throw new InvalidDataException(
                $"LVM2 thin device {deviceId:N0}のdata blockがpool data LV範囲外です: {dataBlock:N0}");
        }

        return dataBlock;
    }

    private byte[]? Lookup(ulong root, ulong key, uint expectedLeafValueSize)
    {
        var visited = new HashSet<ulong>();
        var blockNumber = root;
        for (var depth = 0; depth < MaximumTreeDepth; depth++)
        {
            if (!visited.Add(blockNumber))
            {
                throw new InvalidDataException("LVM2 thin B-treeに循環参照があります。");
            }

            var node = ReadNode(blockNumber);
            if (node.Keys.Length == 0)
            {
                return null;
            }

            var index = FindPrecedingKey(node.Keys, key);
            if (index < 0)
            {
                return null;
            }

            if (node.IsLeaf)
            {
                if (node.ValueSize != expectedLeafValueSize)
                {
                    throw new InvalidDataException(
                        $"LVM2 thin B-tree leaf value sizeが一致しません: expected={expectedLeafValueSize:N0}, actual={node.ValueSize:N0}");
                }

                if (node.Keys[index] != key)
                {
                    return null;
                }

                return node.Values.AsSpan(
                    checked(index * (int)node.ValueSize),
                    checked((int)node.ValueSize)).ToArray();
            }

            if (node.ValueSize != sizeof(ulong))
            {
                throw new InvalidDataException(
                    $"LVM2 thin B-tree internal value sizeが不正です: {node.ValueSize:N0}");
            }

            blockNumber = BinaryPrimitives.ReadUInt64LittleEndian(
                node.Values.AsSpan(index * sizeof(ulong), sizeof(ulong)));
            ValidateMetadataBlockReference(blockNumber, "B-tree child");
        }

        throw new InvalidDataException($"LVM2 thin B-treeの深さが上限{MaximumTreeDepth:N0}を超えています。");
    }

    private ThinBTreeNode ReadNode(ulong blockNumber)
    {
        ValidateMetadataBlockReference(blockNumber, "B-tree node");
        if (_metadataIndex.Length > 0 && ReadMetadataReferenceCount(blockNumber) == 0)
        {
            throw new InvalidDataException(
                $"LVM2 thin B-tree block {blockNumber:N0}がmetadata space mapで未割当です。");
        }

        lock (_cacheLock)
        {
            if (_nodeCache.TryGetValue(blockNumber, out var cached))
            {
                return cached;
            }
        }

        var offset = checked((long)(blockNumber * MetadataBlockBytes));
        var data = EndianUtilities.ReadBytes(_metadataReader, offset, MetadataBlockBytes);
        ValidateChecksum(data, BTreeChecksumXor, $"thin B-tree block {blockNumber:N0}");
        var flags = ReadUInt32(data, 4);
        if (flags is not 1 and not 2)
        {
            throw new InvalidDataException(
                $"LVM2 thin B-tree node flagsが不正です: block={blockNumber:N0}, flags=0x{flags:X8}");
        }

        if (ReadUInt64(data, 8) != blockNumber)
        {
            throw new InvalidDataException(
                $"LVM2 thin B-tree block numberが位置と一致しません: block={blockNumber:N0}");
        }

        var entryCount = ReadUInt32(data, 16);
        var maximumEntries = ReadUInt32(data, 20);
        var valueSize = ReadUInt32(data, 24);
        if (maximumEntries == 0
            || maximumEntries % 3 != 0
            || entryCount > maximumEntries
            || valueSize == 0
            || valueSize > MetadataBlockBytes)
        {
            throw new InvalidDataException(
                $"LVM2 thin B-tree node geometryが不正です: entries={entryCount:N0}/{maximumEntries:N0}, value={valueSize:N0}");
        }

        var maximumPayload = checked((ulong)maximumEntries * (sizeof(ulong) + valueSize));
        if (maximumPayload > MetadataBlockBytes - 32)
        {
            throw new InvalidDataException("LVM2 thin B-tree nodeのentry領域がblock範囲を超えています。");
        }

        var keys = new ulong[entryCount];
        for (var index = 0; index < keys.Length; index++)
        {
            keys[index] = ReadUInt64(data, checked(32 + index * sizeof(ulong)));
            if (index > 0 && keys[index - 1] >= keys[index])
            {
                throw new InvalidDataException("LVM2 thin B-treeのkeyが昇順ではありません。");
            }
        }

        var valuesOffset = checked(32 + (int)maximumEntries * sizeof(ulong));
        var valuesLength = checked((int)entryCount * (int)valueSize);
        if (valuesOffset > data.Length - valuesLength)
        {
            throw new InvalidDataException("LVM2 thin B-tree value領域がblock範囲を超えています。");
        }

        var node = new ThinBTreeNode(
            flags == 2,
            keys,
            valueSize,
            data.AsSpan(valuesOffset, valuesLength).ToArray());
        lock (_cacheLock)
        {
            if (_nodeCache.TryGetValue(blockNumber, out var cached))
            {
                return cached;
            }

            if (_nodeCache.Count < MaximumCachedNodes)
            {
                _nodeCache.Add(blockNumber, node);
            }
        }

        return node;
    }

    private ThinSpaceMapIndexEntry[] ReadMetadataIndex()
    {
        var bitmapCount = checked((int)DivideRoundUp(MetadataBlockCount, EntriesPerBitmap));
        if (bitmapCount == 0 || bitmapCount > MaximumMetadataBitmaps)
        {
            throw new InvalidDataException(
                $"LVM2 thin metadata bitmap数が不正です: {bitmapCount:N0}");
        }

        var data = ReadMetadataBlock(MetadataSpaceMap.BitmapRoot);
        ValidateChecksum(data, IndexChecksumXor, "thin metadata space-map index");
        if (ReadUInt64(data, 8) != MetadataSpaceMap.BitmapRoot)
        {
            throw new InvalidDataException("LVM2 thin metadata space-map indexのblock numberが一致しません。");
        }

        var result = new ThinSpaceMapIndexEntry[bitmapCount];
        for (var index = 0; index < result.Length; index++)
        {
            result[index] = ReadIndexEntry(data.AsSpan(16 + index * 16, 16));
            ValidateIndexEntry(result[index], "metadata");
        }

        return result;
    }

    private uint ReadMetadataReferenceCount(ulong blockNumber)
    {
        var bitmapIndex = checked((int)(blockNumber / EntriesPerBitmap));
        if (bitmapIndex >= _metadataIndex.Length)
        {
            throw new InvalidDataException("LVM2 thin metadata blockのbitmap indexが範囲外です。");
        }

        return ReadBitmapReferenceCount(
            _metadataIndex[bitmapIndex].BitmapBlock,
            checked((int)(blockNumber % EntriesPerBitmap)));
    }

    private uint ReadDataReferenceCount(ulong blockNumber)
    {
        var bitmapIndex = blockNumber / EntriesPerBitmap;
        var value = Lookup(DataSpaceMap.BitmapRoot, bitmapIndex, 16);
        if (value is null)
        {
            throw new InvalidDataException(
                $"LVM2 thin data space mapにbitmap index {bitmapIndex:N0}がありません。");
        }

        var indexEntry = ReadIndexEntry(value);
        ValidateIndexEntry(indexEntry, "data");
        ValidateAllocatedMetadataReference(indexEntry.BitmapBlock, "data space-map bitmap");
        return ReadBitmapReferenceCount(
            indexEntry.BitmapBlock,
            checked((int)(blockNumber % EntriesPerBitmap)));
    }

    private uint ReadBitmapReferenceCount(ulong bitmapBlock, int entry)
    {
        lock (_cacheLock)
        {
            if (_bitmapCache.TryGetValue(bitmapBlock, out var cached))
            {
                return DecodeBitmapReferenceCount(cached, entry);
            }
        }

        var data = ReadMetadataBlock(bitmapBlock);
        ValidateChecksum(data, BitmapChecksumXor, $"thin space-map bitmap {bitmapBlock:N0}");
        if (ReadUInt64(data, 8) != bitmapBlock)
        {
            throw new InvalidDataException(
                $"LVM2 thin space-map bitmapのblock numberが一致しません: {bitmapBlock:N0}");
        }

        lock (_cacheLock)
        {
            if (_bitmapCache.TryGetValue(bitmapBlock, out var cached))
            {
                data = cached;
            }
            else
            {
                if (_bitmapCache.Count < MaximumCachedNodes)
                {
                    _bitmapCache.Add(bitmapBlock, data);
                }
            }
        }

        return DecodeBitmapReferenceCount(data, entry);
    }

    private static uint DecodeBitmapReferenceCount(byte[] data, int entry)
    {
        var encoded = (data[16 + entry / 4] >> (entry % 4 * 2)) & 3;
        return encoded switch
        {
            0 => 0,
            1 => 2,
            2 => 1,
            _ => 3
        };
    }

    private byte[] ReadMetadataBlock(ulong blockNumber)
    {
        ValidateMetadataBlockReference(blockNumber, "metadata block");
        return EndianUtilities.ReadBytes(
            _metadataReader,
            checked((long)(blockNumber * MetadataBlockBytes)),
            MetadataBlockBytes);
    }

    private void ValidateAllocatedMetadataReference(ulong blockNumber, string label)
    {
        if (ReadMetadataReferenceCount(blockNumber) == 0)
        {
            throw new InvalidDataException(
                $"LVM2 thin {label}がmetadata space mapで未割当です: block={blockNumber:N0}");
        }
    }

    private void ValidateIndexEntry(ThinSpaceMapIndexEntry entry, string label)
    {
        ValidateMetadataBlockReference(entry.BitmapBlock, $"{label} space-map bitmap");
        if (entry.FreeCount > EntriesPerBitmap || entry.NoneFreeBefore > EntriesPerBitmap)
        {
            throw new InvalidDataException(
                $"LVM2 thin {label} space-map index entryが不正です。");
        }
    }

    private static ThinSpaceMapIndexEntry ReadIndexEntry(ReadOnlySpan<byte> data) =>
        new(
            BinaryPrimitives.ReadUInt64LittleEndian(data),
            BinaryPrimitives.ReadUInt32LittleEndian(data[8..]),
            BinaryPrimitives.ReadUInt32LittleEndian(data[12..]));

    private void ValidateSpaceMap(ThinSpaceMapRoot root, bool metadata)
    {
        if (root.BlockCount == 0 || root.AllocatedBlockCount > root.BlockCount)
        {
            throw new InvalidDataException(
                $"LVM2 thin {(metadata ? "metadata" : "data")} space mapのblock数が不正です。");
        }

        ValidateMetadataBlockReference(root.BitmapRoot, "space-map bitmap root");
        ValidateMetadataBlockReference(root.RefCountRoot, "space-map ref-count root");
        if (metadata && root.BlockCount != MetadataBlockCount)
        {
            throw new InvalidDataException(
                $"LVM2 thin metadata space mapのblock数がsuperblockと一致しません: space-map={root.BlockCount:N0}, superblock={MetadataBlockCount:N0}");
        }
    }

    private void ValidateMetadataBlockReference(ulong blockNumber, string label)
    {
        if (blockNumber == 0 || blockNumber >= MetadataBlockCount)
        {
            throw new InvalidDataException(
                $"LVM2 thin {label}がmetadata LV範囲外です: block={blockNumber:N0}, count={MetadataBlockCount:N0}");
        }
    }

    private static ThinSpaceMapRoot ReadSpaceMapRoot(ReadOnlySpan<byte> data, string label)
    {
        if (data.Length < 32)
        {
            throw new InvalidDataException($"LVM2 thin {label} space map rootが切り詰められています。");
        }

        return new ThinSpaceMapRoot(
            BinaryPrimitives.ReadUInt64LittleEndian(data),
            BinaryPrimitives.ReadUInt64LittleEndian(data[8..]),
            BinaryPrimitives.ReadUInt64LittleEndian(data[16..]),
            BinaryPrimitives.ReadUInt64LittleEndian(data[24..]));
    }

    private static void ValidateChecksum(byte[] block, uint salt, string label)
    {
        var expected = ReadUInt32(block, 0);
        var actual = BtrfsCrc32C.Compute(block.AsSpan(sizeof(uint))) ^ uint.MaxValue ^ salt;
        if (expected != actual)
        {
            throw new InvalidDataException(
                $"LVM2 {label} CRC32Cが一致しません: expected=0x{expected:X8}, actual=0x{actual:X8}");
        }
    }

    private static int FindPrecedingKey(ulong[] keys, ulong key)
    {
        var low = 0;
        var high = keys.Length - 1;
        var result = -1;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            if (keys[middle] <= key)
            {
                result = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return result;
    }

    private static ulong DivideRoundUp(ulong value, ulong divisor) =>
        value / divisor + (value % divisor == 0 ? 0UL : 1UL);

    private static uint ReadUInt32(byte[] data, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, sizeof(uint)));

    private static ulong ReadUInt64(byte[] data, int offset) =>
        BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset, sizeof(ulong)));

    private sealed record ThinBTreeNode(bool IsLeaf, ulong[] Keys, uint ValueSize, byte[] Values);

    private sealed record ThinSpaceMapIndexEntry(ulong BitmapBlock, uint FreeCount, uint NoneFreeBefore);
}

internal sealed class LvmThinReader : IBlockReader, ILogicalSectorReader
{
    private readonly LvmThinPoolMetadata _metadata;
    private readonly IBlockReader _dataReader;
    private readonly ulong _deviceId;
    private readonly ulong _deviceRoot;

    public LvmThinReader(
        LvmThinPoolMetadata metadata,
        IBlockReader dataReader,
        ulong deviceId,
        ulong deviceRoot,
        ulong lengthBytes)
    {
        _metadata = metadata;
        _dataReader = dataReader;
        _deviceId = deviceId;
        _deviceRoot = deviceRoot;
        Length = checked((long)lengthBytes);
    }

    public long Length { get; }
    public uint LogicalSectorSize => 512;

    public void ReadAt(long offset, byte[] buffer, int bufferOffset, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(bufferOffset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset > Length - count || bufferOffset > buffer.Length - count)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        var remaining = count;
        while (remaining > 0)
        {
            var logicalOffset = checked((ulong)offset);
            var logicalBlock = logicalOffset / _metadata.DataBlockBytes;
            var offsetInBlock = logicalOffset % _metadata.DataBlockBytes;
            var toRead = checked((int)Math.Min(
                (ulong)remaining,
                _metadata.DataBlockBytes - offsetInBlock));
            var dataBlock = _metadata.LookupDataBlock(_deviceId, _deviceRoot, logicalBlock);
            if (dataBlock.HasValue)
            {
                var physicalOffset = checked(dataBlock.Value * _metadata.DataBlockBytes + offsetInBlock);
                _dataReader.ReadAt(checked((long)physicalOffset), buffer, bufferOffset, toRead);
            }
            else
            {
                buffer.AsSpan(bufferOffset, toRead).Clear();
            }

            offset += toRead;
            bufferOffset += toRead;
            remaining -= toRead;
        }
    }
}

internal sealed record ThinSpaceMapRoot(
    ulong BlockCount,
    ulong AllocatedBlockCount,
    ulong BitmapRoot,
    ulong RefCountRoot);
