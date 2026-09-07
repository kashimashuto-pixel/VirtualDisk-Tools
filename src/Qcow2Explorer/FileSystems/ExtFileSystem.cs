using System.Text;
using Qcow2Explorer.Core;
using Qcow2Explorer.Partitions;

namespace Qcow2Explorer.FileSystems;

public sealed class ExtFileSystem : IReadOnlyFileSystem, IFileContentWriter
{
    private const uint ExtentsFlag = 0x00080000;
    private const uint CompressionIncompatFlag = 0x00000001;
    private const uint InlineDataIncompatFlag = 0x00008000;
    private const uint EncryptionIncompatFlag = 0x00010000;
    private const uint NeedsRecoveryIncompatFlag = 0x00000004;
    private const uint VerityReadOnlyCompatibleFlag = 0x00008000;
    private const ushort ValidFileSystemState = 0x0001;
    private const int MaxDirectoryBytes = 64 * 1024 * 1024;

    private readonly IBlockReader _reader;
    private readonly IBlockWriter? _writer;
    private readonly uint _inodeCount;
    private readonly ulong _blockCount;
    private readonly uint _firstDataBlock;
    private readonly int _blockSize;
    private readonly uint _blocksPerGroup;
    private readonly uint _inodesPerGroup;
    private readonly int _inodeSize;
    private readonly int _groupDescriptorSize;
    private readonly long _groupDescriptorOffset;
    private readonly uint _incompatibleFeatures;
    private readonly uint _readOnlyCompatibleFeatures;
    private readonly ushort _fileSystemState;

    public ExtFileSystem(IBlockReader reader, PartitionInfo partition)
    {
        _reader = reader;
        _writer = reader as IBlockWriter;
        Partition = partition;
        var super = EndianUtilities.ReadBytes(reader, 1024, 1024);
        if (EndianUtilities.ReadUInt16Little(super, 0x38) != 0xef53)
        {
            throw new InvalidDataException("ext superblock ではありません。");
        }

        _inodeCount = EndianUtilities.ReadUInt32Little(super, 0x00);
        var blocksLo = EndianUtilities.ReadUInt32Little(super, 0x04);
        var blocksHi = super.Length > 0x154 ? EndianUtilities.ReadUInt32Little(super, 0x150) : 0;
        _blockCount = blocksLo | ((ulong)blocksHi << 32);
        _firstDataBlock = EndianUtilities.ReadUInt32Little(super, 0x14);
        var logBlockSize = EndianUtilities.ReadUInt32Little(super, 0x18);
        _blockSize = 1024 << (int)logBlockSize;
        _blocksPerGroup = EndianUtilities.ReadUInt32Little(super, 0x20);
        _inodesPerGroup = EndianUtilities.ReadUInt32Little(super, 0x28);
        var inodeSize = EndianUtilities.ReadUInt16Little(super, 0x58);
        _inodeSize = inodeSize == 0 ? 128 : inodeSize;
        var descSize = EndianUtilities.ReadUInt16Little(super, 0xfe);
        _groupDescriptorSize = Math.Max(32, descSize == 0 ? 32 : descSize);
        _groupDescriptorOffset = (long)(_firstDataBlock + 1) * _blockSize;

        _incompatibleFeatures = EndianUtilities.ReadUInt32Little(super, 0x60);
        _readOnlyCompatibleFeatures = EndianUtilities.ReadUInt32Little(super, 0x64);
        _fileSystemState = EndianUtilities.ReadUInt16Little(super, 0x3a);
        Name = (_incompatibleFeatures & 0x40) != 0 ? "ext4" : "ext2/ext3";
        Root = new VfsNode { Name = "", IsDirectory = true, Metadata = 2U };
    }

    public string Name { get; }
    public PartitionInfo Partition { get; }
    public VfsNode Root { get; }

    public IReadOnlyList<VfsNode> ListDirectory(VfsNode directory)
    {
        if (!directory.IsDirectory || directory.Metadata is not uint inodeNumber)
        {
            return Array.Empty<VfsNode>();
        }

        var inode = ReadInode(inodeNumber);
        if (!inode.IsDirectory)
        {
            return Array.Empty<VfsNode>();
        }

        if (inode.Size > MaxDirectoryBytes)
        {
            throw new NotSupportedException("大きすぎる ext ディレクトリはこの版では表示を制限しています。");
        }

        var data = ReadInodeData(inode, 0, checked((int)inode.Size));
        return ParseDirectory(data);
    }

    public byte[] ReadFile(VfsNode file, long offset, int count)
    {
        if (file.IsDirectory || file.Metadata is not uint inodeNumber)
        {
            return Array.Empty<byte>();
        }

        var inode = ReadInode(inodeNumber);
        if (offset < 0 || (ulong)offset >= inode.Size || count <= 0)
        {
            return Array.Empty<byte>();
        }

        var available = checked((int)Math.Min((ulong)count, inode.Size - (ulong)offset));
        return ReadInodeData(inode, offset, available);
    }

    public bool CanReplaceFile(VfsNode file, long replacementLength, out string reason)
    {
        if (_writer is null)
        {
            reason = "変更を保持する書き込みオーバーレイがありません。";
            return false;
        }

        if (file.IsDirectory || file.Metadata is not uint inodeNumber)
        {
            reason = "通常ファイルだけを置換できます。";
            return false;
        }

        if (replacementLength < 0)
        {
            reason = "置換ファイルのサイズが不正です。";
            return false;
        }

        ExtInode inode;
        try
        {
            inode = ReadInode(inodeNumber);
        }
        catch (Exception ex)
        {
            reason = $"inodeを読み取れません: {ex.Message}";
            return false;
        }

        if (!inode.IsRegularFile)
        {
            reason = "通常ファイルだけを置換できます。";
            return false;
        }

        if (inode.Size > long.MaxValue || replacementLength != (long)inode.Size)
        {
            reason = $"現在は元ファイルと同じサイズ（{inode.Size:N0} bytes）の置換だけに対応しています。";
            return false;
        }

        if ((_fileSystemState & ValidFileSystemState) == 0
            || (_incompatibleFeatures & NeedsRecoveryIncompatFlag) != 0)
        {
            reason = "journal replayが必要なdirty状態のextファイルシステムは書き込めません。先にe2fsckで検査してください。";
            return false;
        }

        var unsafeIncompat = CompressionIncompatFlag | InlineDataIncompatFlag | EncryptionIncompatFlag;
        if ((_incompatibleFeatures & unsafeIncompat) != 0
            || (_readOnlyCompatibleFeatures & VerityReadOnlyCompatibleFlag) != 0)
        {
            reason = "圧縮、inline data、暗号化、fs-verityを使うextファイルシステムはまだ書き込めません。";
            return false;
        }

        try
        {
            ValidateFullyAllocated(inode);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or OverflowException)
        {
            reason = ex.Message;
            return false;
        }

        reason = string.Empty;
        return true;
    }

    public void ReplaceFileContent(
        VfsNode file,
        Stream replacement,
        long replacementLength,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        if (!replacement.CanRead)
        {
            throw new ArgumentException("置換元ストリームを読み取れません。", nameof(replacement));
        }

        if (!CanReplaceFile(file, replacementLength, out var reason))
        {
            throw new NotSupportedException(reason);
        }

        var inode = ReadInode((uint)file.Metadata!);
        var extents = ValidateFullyAllocated(inode);
        var remaining = replacementLength;
        var buffer = new byte[1024 * 1024];
        foreach (var extent in extents)
        {
            var extentBytes = checked((long)extent.BlockCount * _blockSize);
            var bytesToWrite = Math.Min(remaining, extentBytes);
            var physicalOffset = checked((long)extent.PhysicalBlock * _blockSize);
            long writtenToExtent = 0;
            while (writtenToExtent < bytesToWrite)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = checked((int)Math.Min(buffer.Length, bytesToWrite - writtenToExtent));
                replacement.ReadExactly(buffer.AsSpan(0, count));
                _writer!.WriteAt(physicalOffset + writtenToExtent, buffer, 0, count);
                writtenToExtent += count;
                remaining -= count;
            }

            if (remaining == 0)
            {
                break;
            }
        }

        if (remaining != 0 || replacement.ReadByte() != -1)
        {
            throw new InvalidDataException("置換元ファイルのサイズが指定値と一致しません。変更は破棄してください。");
        }

        _writer!.Flush();
    }

    private IReadOnlyList<VfsNode> ParseDirectory(byte[] data)
    {
        var nodes = new Dictionary<(uint Inode, string Name), VfsNode>();
        for (var blockOffset = 0; blockOffset < data.Length; blockOffset += _blockSize)
        {
            var blockLength = Math.Min(_blockSize, data.Length - blockOffset);
            foreach (var node in ParseDirectoryBlock(data, blockOffset, blockLength))
            {
                if (node.Metadata is uint inodeNumber)
                {
                    nodes[(inodeNumber, node.Name)] = node;
                }
            }
        }

        return nodes.Values
            .OrderByDescending(n => n.IsDirectory)
            .ThenBy(n => n.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private IEnumerable<VfsNode> ParseDirectoryBlock(byte[] data, int blockOffset, int blockLength)
    {
        var offset = blockOffset;
        var blockEnd = blockOffset + blockLength;
        while (offset + 8 <= blockEnd)
        {
            var inodeNumber = EndianUtilities.ReadUInt32Little(data, offset);
            var recLen = EndianUtilities.ReadUInt16Little(data, offset + 4);
            var nameLen = data[offset + 6];
            var fileType = data[offset + 7];
            if (recLen < 8 || (recLen % 4) != 0 || offset + recLen > blockEnd || nameLen > recLen - 8)
            {
                yield break;
            }

            if (inodeNumber != 0 && nameLen > 0)
            {
                var name = Encoding.UTF8.GetString(data, offset + 8, nameLen);
                if (name is not "." and not "..")
                {
                    ExtInode inode;
                    try
                    {
                        inode = ReadInode(inodeNumber);
                    }
                    catch
                    {
                        offset += recLen;
                        continue;
                    }

                    var isDirectory = fileType == 2 || inode.IsDirectory;
                    yield return new VfsNode
                    {
                        Name = name,
                        IsDirectory = isDirectory,
                        Size = isDirectory ? 0 : ToLongSize(inode.Size),
                        ModifiedUtc = inode.ModifiedUtc,
                        Metadata = inodeNumber
                    };
                }
            }

            offset += recLen;
        }
    }

    private ExtInode ReadInode(uint inodeNumber)
    {
        if (inodeNumber == 0 || inodeNumber > _inodeCount)
        {
            throw new InvalidDataException("ext inode 番号が不正です。");
        }

        var group = (inodeNumber - 1) / _inodesPerGroup;
        var index = (inodeNumber - 1) % _inodesPerGroup;
        var inodeTable = ReadInodeTableBlock(group);
        var inodeOffset = checked((long)inodeTable * _blockSize + (long)index * _inodeSize);
        var data = EndianUtilities.ReadBytes(_reader, inodeOffset, _inodeSize);
        var mode = EndianUtilities.ReadUInt16Little(data, 0);
        ulong size = EndianUtilities.ReadUInt32Little(data, 4);
        if (_inodeSize > 128)
        {
            var sizeHigh = EndianUtilities.ReadUInt32Little(data, 108);
            size |= (ulong)sizeHigh << 32;
        }

        var iBlock = new byte[60];
        Array.Copy(data, 40, iBlock, 0, iBlock.Length);
        return new ExtInode(
            inodeNumber,
            mode,
            size,
            EndianUtilities.ReadUInt32Little(data, 32),
            ReadUnixTime(data, 16),
            iBlock);
    }

    private ulong ReadInodeTableBlock(uint group)
    {
        var offset = _groupDescriptorOffset + group * _groupDescriptorSize;
        var descriptor = EndianUtilities.ReadBytes(_reader, offset, _groupDescriptorSize);
        var lo = EndianUtilities.ReadUInt32Little(descriptor, 8);
        ulong hi = 0;
        if (_groupDescriptorSize >= 64)
        {
            hi = EndianUtilities.ReadUInt32Little(descriptor, 0x28);
        }

        return lo | (hi << 32);
    }

    private byte[] ReadInodeData(ExtInode inode, long offset, int count)
    {
        var output = new byte[count];
        if (count == 0)
        {
            return output;
        }

        var extents = GetDataExtents(inode);
        long remaining = Math.Min((long)count, checked((long)(inode.Size - (ulong)offset)));
        var written = 0;
        var logicalBlock = offset / _blockSize;
        var inBlock = (int)(offset % _blockSize);

        while (remaining > 0)
        {
            var extent = extents.FirstOrDefault(e => logicalBlock >= e.LogicalBlock && logicalBlock < e.LogicalBlock + e.BlockCount);
            var chunk = checked((int)Math.Min(remaining, _blockSize - inBlock));
            if (extent is not null && extent.Initialized)
            {
                var physicalBlock = extent.PhysicalBlock + (ulong)(logicalBlock - extent.LogicalBlock);
                _reader.ReadAt(checked((long)physicalBlock * _blockSize + inBlock), output, written, chunk);
            }

            remaining -= chunk;
            written += chunk;
            logicalBlock++;
            inBlock = 0;
        }

        return output;
    }

    private IReadOnlyList<ExtExtent> GetDataExtents(ExtInode inode)
    {
        if ((inode.Flags & ExtentsFlag) != 0)
        {
            return ParseExtentNode(inode.BlockBytes);
        }

        return ParseLegacyBlockPointers(inode.BlockBytes);
    }

    private IReadOnlyList<ExtExtent> ParseExtentNode(byte[] node)
    {
        var extents = new List<ExtExtent>();
        if (node.Length < 12 || EndianUtilities.ReadUInt16Little(node, 0) != 0xf30a)
        {
            return extents;
        }

        var entries = EndianUtilities.ReadUInt16Little(node, 2);
        var depth = EndianUtilities.ReadUInt16Little(node, 6);
        var maxEntries = Math.Min(entries, (node.Length - 12) / 12);

        if (depth == 0)
        {
            for (var i = 0; i < maxEntries; i++)
            {
                var entryOffset = 12 + i * 12;
                var logical = EndianUtilities.ReadUInt32Little(node, entryOffset);
                var lengthRaw = EndianUtilities.ReadUInt16Little(node, entryOffset + 4);
                var startHi = EndianUtilities.ReadUInt16Little(node, entryOffset + 6);
                var startLo = EndianUtilities.ReadUInt32Little(node, entryOffset + 8);
                var initialized = lengthRaw <= 0x8000;
                var length = initialized ? (uint)lengthRaw : (uint)(lengthRaw - 0x8000);
                var physical = startLo | ((ulong)startHi << 32);
                extents.Add(new ExtExtent(logical, length, physical, initialized));
            }
        }
        else
        {
            for (var i = 0; i < maxEntries; i++)
            {
                var entryOffset = 12 + i * 12;
                var leafLo = EndianUtilities.ReadUInt32Little(node, entryOffset + 4);
                var leafHi = EndianUtilities.ReadUInt16Little(node, entryOffset + 8);
                var leafBlock = leafLo | ((ulong)leafHi << 32);
                var child = EndianUtilities.ReadBytes(_reader, checked((long)leafBlock * _blockSize), _blockSize);
                extents.AddRange(ParseExtentNode(child));
            }
        }

        return extents;
    }

    private IReadOnlyList<ExtExtent> ParseLegacyBlockPointers(byte[] blockBytes)
    {
        var extents = new List<ExtExtent>();
        uint logical = 0;
        for (var i = 0; i < 12; i++, logical++)
        {
            var block = EndianUtilities.ReadUInt32Little(blockBytes, i * 4);
            if (block != 0)
            {
                extents.Add(new ExtExtent(logical, 1, block, true));
            }
        }

        var indirectBlock = EndianUtilities.ReadUInt32Little(blockBytes, 12 * 4);
        if (indirectBlock != 0)
        {
            var table = EndianUtilities.ReadBytes(_reader, checked((long)indirectBlock * _blockSize), _blockSize);
            for (var offset = 0; offset + 4 <= table.Length; offset += 4, logical++)
            {
                var block = EndianUtilities.ReadUInt32Little(table, offset);
                if (block != 0)
                {
                    extents.Add(new ExtExtent(logical, 1, block, true));
                }
            }
        }

        return extents;
    }

    private static DateTime? ReadUnixTime(byte[] data, int offset)
    {
        var seconds = EndianUtilities.ReadUInt32Little(data, offset);
        if (seconds == 0)
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
        }
        catch
        {
            return null;
        }
    }

    private static long ToLongSize(ulong value)
    {
        return value > long.MaxValue ? long.MaxValue : (long)value;
    }

    private IReadOnlyList<ExtExtent> ValidateFullyAllocated(ExtInode inode)
    {
        var extents = GetDataExtents(inode)
            .OrderBy(extent => extent.LogicalBlock)
            .ToArray();
        var requiredBlocks = inode.Size == 0
            ? 0UL
            : checked((inode.Size + (ulong)_blockSize - 1) / (ulong)_blockSize);
        ulong nextLogicalBlock = 0;
        foreach (var extent in extents)
        {
            if (extent.BlockCount == 0)
            {
                throw new InvalidDataException("長さ0のext extentが含まれています。");
            }

            if (!extent.Initialized)
            {
                throw new NotSupportedException("未初期化extentを含むファイルはまだ置換できません。");
            }

            if (extent.LogicalBlock != nextLogicalBlock)
            {
                throw new NotSupportedException("スパースファイルはまだ置換できません。");
            }

            var extentEnd = checked(extent.PhysicalBlock + extent.BlockCount);
            if (extent.PhysicalBlock < _firstDataBlock || extentEnd > _blockCount)
            {
                throw new InvalidDataException("ext extentがファイルシステム範囲外です。");
            }

            nextLogicalBlock = checked(nextLogicalBlock + extent.BlockCount);
            if (nextLogicalBlock >= requiredBlocks)
            {
                break;
            }
        }

        if (nextLogicalBlock < requiredBlocks)
        {
            throw new NotSupportedException("未割り当て領域を含むファイルはまだ置換できません。");
        }

        return extents;
    }

    private sealed record ExtExtent(uint LogicalBlock, uint BlockCount, ulong PhysicalBlock, bool Initialized);

    private sealed record ExtInode(uint Number, ushort Mode, ulong Size, uint Flags, DateTime? ModifiedUtc, byte[] BlockBytes)
    {
        public bool IsDirectory => (Mode & 0xf000) == 0x4000;
        public bool IsRegularFile => (Mode & 0xf000) == 0x8000;
    }
}
