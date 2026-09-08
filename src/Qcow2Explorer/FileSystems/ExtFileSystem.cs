using System.Buffers.Binary;
using System.Text;
using Qcow2Explorer.Core;
using Qcow2Explorer.Partitions;

namespace Qcow2Explorer.FileSystems;

public sealed class ExtFileSystem : IReadOnlyFileSystem, IFileContentWriter, IFileSystemEditor
{
    private const uint ExtentsFlag = 0x00080000;
    private const uint CompressionIncompatFlag = 0x00000001;
    private const uint InlineDataIncompatFlag = 0x00008000;
    private const uint EncryptionIncompatFlag = 0x00010000;
    private const uint NeedsRecoveryIncompatFlag = 0x00000004;
    private const uint ExternalJournalIncompatFlag = 0x00000008;
    private const uint FileTypeIncompatFlag = 0x00000002;
    private const uint ChecksumSeedIncompatFlag = 0x00002000;
    private const uint SixtyFourBitIncompatFlag = 0x00000080;
    private const uint VerityReadOnlyCompatibleFlag = 0x00008000;
    private const uint OrphanPresentReadOnlyCompatibleFlag = 0x00010000;
    private const uint MetadataChecksumReadOnlyCompatibleFlag = 0x00000400;
    private const uint BigAllocReadOnlyCompatibleFlag = 0x00000200;
    private const uint ImmutableInodeFlag = 0x00000010;
    private const uint AppendOnlyInodeFlag = 0x00000020;
    private const uint InlineDataInodeFlag = 0x10000000;
    private const uint VerityInodeFlag = 0x00100000;
    private const uint DirectoryIndexInodeFlag = 0x00001000;
    private const ushort ValidFileSystemState = 0x0001;
    private const ushort InodeBitmapUninitializedGroupFlag = 0x0001;
    private const ushort BlockBitmapUninitializedGroupFlag = 0x0002;
    private const int MaxDirectoryBytes = 64 * 1024 * 1024;

    private readonly IBlockReader _reader;
    private readonly IBlockWriter? _writer;
    private readonly uint _inodeCount;
    private readonly ulong _blockCount;
    private readonly uint _firstDataBlock;
    private readonly int _blockSize;
    private readonly uint _blocksPerGroup;
    private readonly uint _inodesPerGroup;
    private readonly uint _groupCount;
    private readonly uint _firstInode;
    private readonly int _inodeSize;
    private readonly int _groupDescriptorSize;
    private readonly long _groupDescriptorOffset;
    private readonly uint _incompatibleFeatures;
    private readonly uint _readOnlyCompatibleFeatures;
    private readonly ushort _fileSystemState;
    private readonly byte[] _uuid;
    private readonly uint _checksumSeed;
    private readonly bool _hasMetadataChecksum;

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
        _firstInode = EndianUtilities.ReadUInt32Little(super, 0x54);
        var inodeSize = EndianUtilities.ReadUInt16Little(super, 0x58);
        _inodeSize = inodeSize == 0 ? 128 : inodeSize;
        var descSize = EndianUtilities.ReadUInt16Little(super, 0xfe);
        _groupDescriptorSize = Math.Max(32, descSize == 0 ? 32 : descSize);
        _groupDescriptorOffset = (long)(_firstDataBlock + 1) * _blockSize;

        _incompatibleFeatures = EndianUtilities.ReadUInt32Little(super, 0x60);
        _readOnlyCompatibleFeatures = EndianUtilities.ReadUInt32Little(super, 0x64);
        _fileSystemState = EndianUtilities.ReadUInt16Little(super, 0x3a);
        _uuid = super.AsSpan(0x68, 16).ToArray();
        _hasMetadataChecksum = (_readOnlyCompatibleFeatures & MetadataChecksumReadOnlyCompatibleFlag) != 0;
        _checksumSeed = (_incompatibleFeatures & ChecksumSeedIncompatFlag) != 0
            ? EndianUtilities.ReadUInt32Little(super, 0x270)
            : ComputeCrc32C(uint.MaxValue, _uuid);
        if (_blockCount <= _firstDataBlock || _blocksPerGroup == 0 || _inodesPerGroup == 0)
        {
            throw new InvalidDataException("ext block group geometryが不正です。");
        }

        _groupCount = checked((uint)((_blockCount - _firstDataBlock + _blocksPerGroup - 1) / _blocksPerGroup));
        Name = (_incompatibleFeatures & 0x40) != 0 ? "ext4" : "ext2/ext3";
        Root = new VfsNode { Name = "", VirtualPath = @"\", IsDirectory = true, Metadata = 2U };
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
        return ParseDirectory(data, directory.VirtualPath);
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

    public bool ValidateFileSystem(out string reason)
    {
        return ValidateAllocationMetadata(requireWriter: true, out reason);
    }

    internal bool ValidateReadOnlyIntegrity(out string reason)
    {
        return ValidateAllocationMetadata(requireWriter: false, out reason);
    }

    public bool CanWriteFile(VfsNode file, long contentLength, out string reason)
    {
        if (!TryValidateEditableFileSystem(requireWriter: true, out reason))
        {
            return false;
        }

        if (file.IsDirectory || file.Metadata is not uint inodeNumber || contentLength < 0)
        {
            reason = "通常ファイルと0以上のサイズを指定してください。";
            return false;
        }

        try
        {
            var inode = ReadInode(inodeNumber);
            if (!inode.IsRegularFile)
            {
                reason = "通常ファイルだけを編集できます。";
                return false;
            }

            if (inode.LinkCount != 1)
            {
                reason = "hard linkを持つextファイルの編集はまだ対応していません。";
                return false;
            }

            if ((inode.Flags & (ImmutableInodeFlag | AppendOnlyInodeFlag | InlineDataInodeFlag | VerityInodeFlag)) != 0)
            {
                reason = "immutable、append-only、inline data、fs-verity属性のextファイルは編集できません。";
                return false;
            }

            if (inode.Size > long.MaxValue)
            {
                reason = "扱える範囲を超えるextファイルサイズです。";
                return false;
            }

            var oldBlocks = GetRequiredBlocks((long)inode.Size);
            var newBlocks = GetRequiredBlocks(contentLength);
            if (oldBlocks != newBlocks && !CanChangeAllocation(inode, newBlocks, out reason))
            {
                return false;
            }

            _ = ValidateFullyAllocated(inode);
            ValidateInodeChecksum(inode);
            reason = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or OverflowException)
        {
            reason = ex.Message;
            return false;
        }
    }

    public void WriteFileContent(
        VfsNode file,
        Stream content,
        long contentLength,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!content.CanRead)
        {
            throw new ArgumentException("編集内容のストリームを読み取れません。", nameof(content));
        }

        if (!CanWriteFile(file, contentLength, out var reason))
        {
            throw new NotSupportedException(reason);
        }

        var inode = ReadInode((uint)file.Metadata!);
        var oldBlocks = GetRequiredBlocks(checked((long)inode.Size));
        var newBlocks = GetRequiredBlocks(contentLength);
        if (oldBlocks != newBlocks)
        {
            ReplaceFileAllocation(inode, content, contentLength, cancellationToken);
            _writer!.Flush();
            return;
        }

        WriteInodeContent(inode, content, contentLength, cancellationToken);
        var allocatedBytes = checked(GetRequiredBlocks(contentLength) * _blockSize);
        if (contentLength < allocatedBytes)
        {
            ZeroInodeRange(inode, contentLength, allocatedBytes - contentLength, cancellationToken);
        }

        WriteInodeSizeAndTimes(inode, contentLength);
        _writer!.Flush();
    }

    public bool CanCreateFile(VfsNode directory, string name, long contentLength, out string reason)
    {
        if (!TryValidateEditableFileSystem(requireWriter: true, out reason))
        {
            return false;
        }

        if (!TryValidateNewFile(directory, name, contentLength, out _, out reason))
        {
            return false;
        }

        reason = string.Empty;
        return true;
    }

    public VfsNode CreateFile(
        VfsNode directory,
        string name,
        Stream content,
        long contentLength,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!content.CanRead)
        {
            throw new ArgumentException("追加内容のストリームを読み取れません。", nameof(content));
        }

        if (!CanCreateFile(directory, name, contentLength, out var reason)
            || !TryValidateNewFile(directory, name, contentLength, out var parent, out reason))
        {
            throw new NotSupportedException(reason);
        }

        return CreateRegularFile(parent, directory.VirtualPath, name, content, contentLength, cancellationToken);
    }

    public bool CanDeleteFile(VfsNode directory, VfsNode file, out string reason)
    {
        if (!TryValidateEditableFileSystem(requireWriter: true, out reason))
        {
            return false;
        }

        if (!directory.IsDirectory
            || directory.Metadata is not uint parentInodeNumber
            || file.IsDirectory
            || file.Metadata is not uint fileInodeNumber)
        {
            reason = "通常ファイルとその親ディレクトリを指定してください。";
            return false;
        }

        try
        {
            var parent = ReadInode(parentInodeNumber);
            var inode = ReadInode(fileInodeNumber);
            if (!parent.IsDirectory || !inode.IsRegularFile || inode.LinkCount != 1)
            {
                reason = "hard linkを持たない通常ファイルだけを削除できます。";
                return false;
            }

            if ((inode.Flags & (ImmutableInodeFlag | AppendOnlyInodeFlag | InlineDataInodeFlag | VerityInodeFlag)) != 0)
            {
                reason = "保護属性または未対応属性を持つextファイルは削除できません。";
                return false;
            }

            if (!IsDirectChild(directory.VirtualPath, file.VirtualPath))
            {
                reason = "削除対象が指定ディレクトリの直下にありません。";
                return false;
            }

            var extents = GetOwnedInlineExtents(inode);
            EnsureBlocksExclusivelyOwned(inode.Number, extents);
            _ = FindDirectoryEntry(parent, file.Name, inode.Number);
            ValidateInodeChecksum(inode);
            ValidateInodeChecksum(parent);
            reason = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or OverflowException)
        {
            reason = ex.Message;
            return false;
        }
    }

    public void DeleteFile(VfsNode directory, VfsNode file, CancellationToken cancellationToken = default)
    {
        if (!CanDeleteFile(directory, file, out var reason))
        {
            throw new NotSupportedException(reason);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var parent = ReadInode((uint)directory.Metadata!);
        var inode = ReadInode((uint)file.Metadata!);
        var extents = GetOwnedInlineExtents(inode);
        var mutation = new ExtMutationContext(this);
        foreach (var extent in extents)
        {
            mutation.SetBlocksAllocated(extent.PhysicalBlock, extent.BlockCount, allocated: false);
        }

        mutation.SetInodeAllocated(inode.Number, allocated: false);
        RemoveDirectoryEntry(parent, file.Name, inode.Number);
        WriteDeletedInode(inode);
        mutation.Commit();
        _writer!.Flush();
    }

    private IReadOnlyList<VfsNode> ParseDirectory(byte[] data, string parentPath)
    {
        var nodes = new Dictionary<(uint Inode, string Name), VfsNode>();
        for (var blockOffset = 0; blockOffset < data.Length; blockOffset += _blockSize)
        {
            var blockLength = Math.Min(_blockSize, data.Length - blockOffset);
            foreach (var node in ParseDirectoryBlock(data, blockOffset, blockLength, parentPath))
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

    private IEnumerable<VfsNode> ParseDirectoryBlock(
        byte[] data,
        int blockOffset,
        int blockLength,
        string parentPath)
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
                        VirtualPath = parentPath.TrimEnd('\\') + "\\" + name,
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
        var inode = new ExtInode(
            inodeNumber,
            mode,
            size,
            EndianUtilities.ReadUInt32Little(data, 32),
            ReadUnixTime(data, 16),
            iBlock,
            EndianUtilities.ReadUInt16Little(data, 26),
            EndianUtilities.ReadUInt32Little(data, 100),
            inodeOffset,
            data);
        ValidateInodeChecksum(inode);
        return inode;
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

    private bool CanChangeAllocation(ExtInode inode, long newBlocks, out string reason)
    {
        if (!_hasMetadataChecksum
            || (_readOnlyCompatibleFeatures & BigAllocReadOnlyCompatibleFlag) != 0
            || (_incompatibleFeatures & FileTypeIncompatFlag) == 0)
        {
            reason = "ext4の割り当て変更にはmetadata_csum、filetype、通常block allocationが必要です。";
            return false;
        }

        if (newBlocks > 0x8000)
        {
            reason = "現在は1 extent（最大32,768 blocks）に収まるext4ファイルだけサイズ変更できます。";
            return false;
        }

        try
        {
            var oldExtents = GetOwnedInlineExtents(inode);
            EnsureBlocksExclusivelyOwned(inode.Number, oldExtents);
            if (newBlocks > 0)
            {
                var probe = new ExtMutationContext(this);
                _ = probe.FindContiguousBlocks(checked((uint)newBlocks));
            }

            reason = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or OverflowException)
        {
            reason = ex.Message;
            return false;
        }
    }

    private bool TryValidateNewFile(
        VfsNode directory,
        string name,
        long contentLength,
        out ExtInode parent,
        out string reason)
    {
        parent = null!;
        if (!_hasMetadataChecksum
            || (_readOnlyCompatibleFeatures & BigAllocReadOnlyCompatibleFlag) != 0
            || (_incompatibleFeatures & FileTypeIncompatFlag) == 0)
        {
            reason = "ext4のファイル追加にはmetadata_csum、filetype、通常block allocationが必要です。";
            return false;
        }

        if (!directory.IsDirectory || directory.Metadata is not uint parentInodeNumber)
        {
            reason = "追加先ディレクトリを再確認できません。";
            return false;
        }

        if (contentLength < 0 || GetRequiredBlocks(contentLength) > 0x8000)
        {
            reason = "追加ファイルは1 extent（最大32,768 blocks）に収めてください。";
            return false;
        }

        if (string.IsNullOrEmpty(name))
        {
            reason = "ext4ファイル名を指定してください。";
            return false;
        }

        var nameBytes = Encoding.UTF8.GetBytes(name);
        if (nameBytes.Length > 255
            || name is "." or ".."
            || name.IndexOfAny(['/', '\\', '\0']) >= 0
            || name.Any(char.IsControl))
        {
            reason = "ext4ファイル名が不正です。UTF-8で255 bytes以内にしてください。";
            return false;
        }

        try
        {
            parent = ReadInode(parentInodeNumber);
            if (!parent.IsDirectory
                || (parent.Flags & (InlineDataInodeFlag | DirectoryIndexInodeFlag | ImmutableInodeFlag | AppendOnlyInodeFlag)) != 0)
            {
                reason = "inline、indexed、immutable、append-only directoryへの追加はまだ対応していません。";
                return false;
            }

            ValidateInodeChecksum(parent);
            if (ListDirectory(directory).Any(node => string.Equals(node.Name, name, StringComparison.Ordinal)))
            {
                reason = $"同名の項目が既に存在します: {name}";
                return false;
            }

            _ = FindDirectoryInsertion(parent, checked((ushort)Align4(8 + nameBytes.Length)));
            var probe = new ExtMutationContext(this);
            _ = probe.FindFreeInode((parent.Number - 1) / _inodesPerGroup);
            var blocks = GetRequiredBlocks(contentLength);
            if (blocks > 0)
            {
                _ = probe.FindContiguousBlocks(checked((uint)blocks));
            }

            reason = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or OverflowException)
        {
            reason = ex.Message;
            return false;
        }
    }

    private VfsNode CreateRegularFile(
        ExtInode parent,
        string parentPath,
        string name,
        Stream content,
        long contentLength,
        CancellationToken cancellationToken)
    {
        var mutation = new ExtMutationContext(this);
        var inodeNumber = mutation.FindFreeInode((parent.Number - 1) / _inodesPerGroup);
        var blocks = checked((uint)GetRequiredBlocks(contentLength));
        var startBlock = blocks == 0 ? 0UL : mutation.FindContiguousBlocks(blocks);
        mutation.SetInodeAllocated(inodeNumber, allocated: true);
        if (blocks > 0)
        {
            mutation.SetBlocksAllocated(startBlock, blocks, allocated: true);
        }

        WriteContiguousContent(startBlock, blocks, content, contentLength, cancellationToken);
        var generation = checked((uint)Random.Shared.Next(1, int.MaxValue));
        WriteNewRegularInode(inodeNumber, parent, generation, startBlock, blocks, contentLength);
        InsertDirectoryEntry(parent, name, inodeNumber);
        mutation.Commit();
        _writer!.Flush();
        return new VfsNode
        {
            Name = name,
            VirtualPath = parentPath.TrimEnd('\\') + "\\" + name,
            IsDirectory = false,
            Size = contentLength,
            ModifiedUtc = DateTime.UtcNow,
            Metadata = inodeNumber,
        };
    }

    private void ReplaceFileAllocation(
        ExtInode inode,
        Stream content,
        long contentLength,
        CancellationToken cancellationToken)
    {
        var oldExtents = GetOwnedInlineExtents(inode);
        EnsureBlocksExclusivelyOwned(inode.Number, oldExtents);
        var mutation = new ExtMutationContext(this);
        var blocks = checked((uint)GetRequiredBlocks(contentLength));
        var startBlock = blocks == 0 ? 0UL : mutation.FindContiguousBlocks(blocks);
        if (blocks > 0)
        {
            mutation.SetBlocksAllocated(startBlock, blocks, allocated: true);
        }

        WriteContiguousContent(startBlock, blocks, content, contentLength, cancellationToken);
        foreach (var extent in oldExtents)
        {
            mutation.SetBlocksAllocated(extent.PhysicalBlock, extent.BlockCount, allocated: false);
        }

        WriteExistingInodeAllocation(inode, startBlock, blocks, contentLength);
        mutation.Commit();
    }

    private IReadOnlyList<ExtExtent> GetOwnedInlineExtents(ExtInode inode)
    {
        if ((inode.Flags & ExtentsFlag) == 0
            || inode.BlockBytes.Length < 12
            || EndianUtilities.ReadUInt16Little(inode.BlockBytes, 0) != 0xf30a
            || EndianUtilities.ReadUInt16Little(inode.BlockBytes, 6) != 0)
        {
            throw new NotSupportedException("inline extent leaf以外のext4 allocation変更はまだ対応していません。");
        }

        var extents = ValidateFullyAllocated(inode)
            .OrderBy(extent => extent.LogicalBlock)
            .ToArray();
        var requiredBlocks = GetRequiredBlocks(checked((long)inode.Size));
        var representedBlocks = extents.Aggregate<ExtExtent, long>(0, (current, extent) =>
            checked(current + extent.BlockCount));
        if (representedBlocks != requiredBlocks || extents.Length > 4)
        {
            throw new NotSupportedException("preallocationまたは4個を超えるextentを持つext4ファイルは変更できません。");
        }

        return extents;
    }

    private void EnsureBlocksExclusivelyOwned(uint ownerInode, IReadOnlyList<ExtExtent> ownedExtents)
    {
        if (ownedExtents.Count == 0)
        {
            return;
        }

        var ownedBlocks = new HashSet<ulong>();
        foreach (var extent in ownedExtents)
        {
            for (uint index = 0; index < extent.BlockCount; index++)
            {
                ownedBlocks.Add(checked(extent.PhysicalBlock + index));
            }
        }

        for (uint group = 0; group < _groupCount; group++)
        {
            var state = LoadGroupState(group, validateBitmaps: true);
            if ((state.Flags & InodeBitmapUninitializedGroupFlag) != 0)
            {
                continue;
            }

            var validInodes = GetValidInodesInGroup(group);
            for (uint bit = 0; bit < validInodes; bit++)
            {
                if (!IsBitSet(state.InodeBitmap, bit))
                {
                    continue;
                }

                var inodeNumber = checked(group * _inodesPerGroup + bit + 1);
                if (inodeNumber == ownerInode)
                {
                    continue;
                }

                var inode = ReadInode(inodeNumber);
                foreach (var extent in GetDataExtents(inode))
                {
                    for (uint index = 0; index < extent.BlockCount; index++)
                    {
                        if (ownedBlocks.Contains(checked(extent.PhysicalBlock + index)))
                        {
                            throw new InvalidDataException(
                                $"ext4 blockがinode {ownerInode:N0}と{inodeNumber:N0}で重複しています。");
                        }
                    }
                }
            }
        }
    }

    private static bool IsDirectChild(string parentPath, string childPath)
    {
        var normalizedParent = parentPath.Replace('/', '\\').TrimEnd('\\');
        var normalizedChild = childPath.Replace('/', '\\').TrimEnd('\\');
        var separator = normalizedChild.LastIndexOf('\\');
        var actualParent = separator <= 0 ? string.Empty : normalizedChild[..separator];
        return string.Equals(normalizedParent, actualParent, StringComparison.Ordinal);
    }

    private static int Align4(int value) => checked((value + 3) & ~3);

    private ExtDirectoryLocation FindDirectoryInsertion(ExtInode directory, ushort requiredLength)
    {
        foreach (var block in ReadDirectoryBlocks(directory))
        {
            var offset = 0;
            while (offset + 8 <= block.EntryBytes)
            {
                var inodeNumber = EndianUtilities.ReadUInt32Little(block.Data, offset);
                var recordLength = EndianUtilities.ReadUInt16Little(block.Data, offset + 4);
                var nameLength = block.Data[offset + 6];
                if (recordLength < 8 || (recordLength & 3) != 0 || offset + recordLength > block.EntryBytes)
                {
                    throw new InvalidDataException("ext4 directory entryの長さが不正です。");
                }

                if (inodeNumber == 0 && recordLength >= requiredLength)
                {
                    return new ExtDirectoryLocation(block, offset, PreviousOffset: -1, SplitOffset: -1);
                }

                var actualLength = Align4(8 + nameLength);
                if (inodeNumber != 0 && actualLength >= 8 && recordLength - actualLength >= requiredLength)
                {
                    return new ExtDirectoryLocation(
                        block,
                        offset + actualLength,
                        PreviousOffset: offset,
                        SplitOffset: actualLength);
                }

                offset += recordLength;
            }
        }

        throw new NotSupportedException("既存directory block内に新しいentryを置く空きがありません。");
    }

    private ExtDirectoryLocation FindDirectoryEntry(ExtInode directory, string name, uint inodeNumber)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        foreach (var block in ReadDirectoryBlocks(directory))
        {
            var offset = 0;
            var previousOffset = -1;
            while (offset + 8 <= block.EntryBytes)
            {
                var candidateInode = EndianUtilities.ReadUInt32Little(block.Data, offset);
                var recordLength = EndianUtilities.ReadUInt16Little(block.Data, offset + 4);
                var nameLength = block.Data[offset + 6];
                if (recordLength < 8 || (recordLength & 3) != 0 || offset + recordLength > block.EntryBytes)
                {
                    throw new InvalidDataException("ext4 directory entryの長さが不正です。");
                }

                if (candidateInode == inodeNumber
                    && nameLength == nameBytes.Length
                    && block.Data.AsSpan(offset + 8, nameLength).SequenceEqual(nameBytes))
                {
                    return new ExtDirectoryLocation(block, offset, previousOffset, SplitOffset: -1);
                }

                previousOffset = offset;

                offset += recordLength;
            }
        }

        throw new FileNotFoundException($"ext4 directory entryを再確認できません: {name}");
    }

    private IReadOnlyList<ExtDirectoryBlock> ReadDirectoryBlocks(ExtInode directory)
    {
        if (!directory.IsDirectory || directory.Size == 0 || (directory.Size % (ulong)_blockSize) != 0)
        {
            throw new NotSupportedException("block境界に揃ったext4 directoryだけを編集できます。");
        }

        var extents = ValidateFullyAllocated(directory);
        var result = new List<ExtDirectoryBlock>();
        for (uint logicalBlock = 0; logicalBlock < directory.Size / (ulong)_blockSize; logicalBlock++)
        {
            var extent = extents.Single(candidate =>
                logicalBlock >= candidate.LogicalBlock
                && logicalBlock < candidate.LogicalBlock + candidate.BlockCount);
            var physicalBlock = checked(extent.PhysicalBlock + logicalBlock - extent.LogicalBlock);
            var data = EndianUtilities.ReadBytes(_reader, checked((long)physicalBlock * _blockSize), _blockSize);
            var entryBytes = _blockSize;
            if (_hasMetadataChecksum)
            {
                var tailOffset = _blockSize - 12;
                if (EndianUtilities.ReadUInt32Little(data, tailOffset) != 0
                    || EndianUtilities.ReadUInt16Little(data, tailOffset + 4) != 12
                    || data[tailOffset + 6] != 0
                    || data[tailOffset + 7] != 0xde)
                {
                    throw new InvalidDataException("ext4 directory checksum tailが見つかりません。");
                }

                var expected = EndianUtilities.ReadUInt32Little(data, tailOffset + 8);
                var actual = ComputeDirectoryChecksum(directory, data.AsSpan(0, tailOffset));
                if (expected != actual)
                {
                    throw new InvalidDataException(
                        $"ext4 directory inode {directory.Number:N0} checksumが一致しません。");
                }

                entryBytes = tailOffset;
            }

            result.Add(new ExtDirectoryBlock(physicalBlock, data, entryBytes));
        }

        return result;
    }

    private void InsertDirectoryEntry(ExtInode parent, string name, uint inodeNumber)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var requiredLength = checked((ushort)Align4(8 + nameBytes.Length));
        var location = FindDirectoryInsertion(parent, requiredLength);
        var data = location.Block.Data;
        ushort newRecordLength;
        if (location.SplitOffset >= 0)
        {
            var originalLength = EndianUtilities.ReadUInt16Little(data, location.PreviousOffset + 4);
            BinaryPrimitives.WriteUInt16LittleEndian(
                data.AsSpan(location.PreviousOffset + 4, 2),
                checked((ushort)location.SplitOffset));
            newRecordLength = checked((ushort)(originalLength - location.SplitOffset));
        }
        else
        {
            newRecordLength = EndianUtilities.ReadUInt16Little(data, location.Offset + 4);
        }

        data.AsSpan(location.Offset, newRecordLength).Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(location.Offset, 4), inodeNumber);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(location.Offset + 4, 2), newRecordLength);
        data[location.Offset + 6] = checked((byte)nameBytes.Length);
        data[location.Offset + 7] = 1;
        nameBytes.CopyTo(data.AsSpan(location.Offset + 8));
        WriteDirectoryBlock(parent, location.Block);
        WriteInodeTimes(parent);
    }

    private void RemoveDirectoryEntry(ExtInode parent, string name, uint inodeNumber)
    {
        var location = FindDirectoryEntry(parent, name, inodeNumber);
        var data = location.Block.Data;
        var recordLength = EndianUtilities.ReadUInt16Little(data, location.Offset + 4);
        if (location.PreviousOffset >= 0)
        {
            var previousLength = EndianUtilities.ReadUInt16Little(data, location.PreviousOffset + 4);
            BinaryPrimitives.WriteUInt16LittleEndian(
                data.AsSpan(location.PreviousOffset + 4, 2),
                checked((ushort)(previousLength + recordLength)));
            data.AsSpan(location.Offset, recordLength).Clear();
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(location.Offset, 4), 0);
            data[location.Offset + 6] = 0;
            data[location.Offset + 7] = 0;
            data.AsSpan(location.Offset + 8, recordLength - 8).Clear();
        }

        WriteDirectoryBlock(parent, location.Block);
        WriteInodeTimes(parent);
    }

    private void WriteDirectoryBlock(ExtInode directory, ExtDirectoryBlock block)
    {
        if (_hasMetadataChecksum)
        {
            var checksum = ComputeDirectoryChecksum(directory, block.Data.AsSpan(0, block.EntryBytes));
            BinaryPrimitives.WriteUInt32LittleEndian(block.Data.AsSpan(block.EntryBytes + 8, 4), checksum);
        }

        _writer!.WriteAt(checked((long)block.PhysicalBlock * _blockSize), block.Data, 0, block.Data.Length);
    }

    private uint ComputeDirectoryChecksum(ExtInode directory, ReadOnlySpan<byte> entries)
    {
        Span<byte> number = stackalloc byte[4];
        Span<byte> generation = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(number, directory.Number);
        BinaryPrimitives.WriteUInt32LittleEndian(generation, directory.Generation);
        var checksum = ComputeCrc32C(_checksumSeed, number);
        checksum = ComputeCrc32C(checksum, generation);
        return ComputeCrc32C(checksum, entries);
    }

    private void WriteContiguousContent(
        ulong startBlock,
        uint blockCount,
        Stream content,
        long contentLength,
        CancellationToken cancellationToken)
    {
        var allocatedLength = checked((long)blockCount * _blockSize);
        var buffer = new byte[Math.Min(1024 * 1024, Math.Max(_blockSize, 4096))];
        long written = 0;
        while (written < contentLength)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = checked((int)Math.Min(buffer.Length, contentLength - written));
            content.ReadExactly(buffer.AsSpan(0, count));
            _writer!.WriteAt(checked((long)startBlock * _blockSize + written), buffer, 0, count);
            written += count;
        }

        Array.Clear(buffer);
        while (written < allocatedLength)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = checked((int)Math.Min(buffer.Length, allocatedLength - written));
            _writer!.WriteAt(checked((long)startBlock * _blockSize + written), buffer, 0, count);
            written += count;
        }

        if (content.ReadByte() != -1)
        {
            throw new InvalidDataException("追加内容のサイズが指定値と一致しません。");
        }
    }

    private void WriteNewRegularInode(
        uint inodeNumber,
        ExtInode parent,
        uint generation,
        ulong startBlock,
        uint blockCount,
        long size)
    {
        var data = new byte[_inodeSize];
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0, 2), 0x81a4);
        parent.RawData.AsSpan(2, 2).CopyTo(data.AsSpan(2, 2));
        parent.RawData.AsSpan(24, 2).CopyTo(data.AsSpan(24, 2));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(26, 2), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(32, 4), ExtentsFlag);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(100, 4), generation);
        if (_inodeSize > 128)
        {
            var desiredExtraSize = EndianUtilities.ReadUInt16Little(ReadSuperBlockForWrite(), 0x15e);
            var extraSize = Math.Min(desiredExtraSize, _inodeSize - 128);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x80, 2), checked((ushort)extraSize));
            if (HasLargeInodeField(data, 0x78, 4))
            {
                parent.RawData.AsSpan(0x78, 4).CopyTo(data.AsSpan(0x78, 4));
            }
        }

        SetInodeAllocationFields(data, startBlock, blockCount, size);
        SetAllInodeTimes(data);
        SetInodeChecksum(inodeNumber, generation, data);
        var inodeOffset = GetInodeDiskOffset(inodeNumber);
        _writer!.WriteAt(inodeOffset, data, 0, data.Length);
    }

    private void WriteExistingInodeAllocation(ExtInode inode, ulong startBlock, uint blockCount, long size)
    {
        var data = (byte[])inode.RawData.Clone();
        SetInodeAllocationFields(data, startBlock, blockCount, size);
        SetModificationTimes(data);
        SetInodeChecksum(inode.Number, inode.Generation, data);
        _writer!.WriteAt(inode.DiskOffset, data, 0, data.Length);
    }

    private void SetInodeAllocationFields(byte[] data, ulong startBlock, uint blockCount, long size)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4, 4), checked((uint)(ulong)size));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(108, 4), checked((uint)((ulong)size >> 32)));
        var sectors = checked((ulong)blockCount * (ulong)_blockSize / 512UL);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(28, 4), checked((uint)sectors));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x74, 2), checked((ushort)(sectors >> 32)));
        data.AsSpan(40, 60).Clear();
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(40, 2), 0xf30a);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(42, 2), blockCount == 0 ? (ushort)0 : (ushort)1);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(44, 2), 4);
        if (blockCount > 0)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(52, 4), 0);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(56, 2), checked((ushort)blockCount));
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(58, 2), checked((ushort)(startBlock >> 32)));
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(60, 4), checked((uint)startBlock));
        }
    }

    private void WriteDeletedInode(ExtInode inode)
    {
        var data = (byte[])inode.RawData.Clone();
        var now = checked((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(20, 4), now);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(26, 2), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(28, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(108, 4), 0);
        data.AsSpan(40, 60).Clear();
        SetInodeChecksum(inode.Number, inode.Generation, data);
        _writer!.WriteAt(inode.DiskOffset, data, 0, data.Length);
    }

    private void WriteInodeTimes(ExtInode inode)
    {
        var data = (byte[])inode.RawData.Clone();
        SetModificationTimes(data);
        SetInodeChecksum(inode.Number, inode.Generation, data);
        _writer!.WriteAt(inode.DiskOffset, data, 0, data.Length);
    }

    private static void SetModificationTimes(byte[] data)
    {
        var now = checked((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12, 4), now);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16, 4), now);
        if (data.Length >= 0x8c)
        {
            data.AsSpan(0x84, 8).Clear();
        }
    }

    private static void SetAllInodeTimes(byte[] data)
    {
        SetModificationTimes(data);
        var now = checked((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8, 4), now);
        if (data.Length >= 0x98)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x90, 4), now);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x94, 4), 0);
        }
    }

    private byte[] ReadSuperBlockForWrite()
    {
        return EndianUtilities.ReadBytes(_reader, 1024, 1024);
    }

    private long GetInodeDiskOffset(uint inodeNumber)
    {
        var group = (inodeNumber - 1) / _inodesPerGroup;
        var index = (inodeNumber - 1) % _inodesPerGroup;
        var inodeTable = ReadInodeTableBlock(group);
        return checked((long)inodeTable * _blockSize + (long)index * _inodeSize);
    }

    private ExtGroupState LoadGroupState(uint group, bool validateBitmaps)
    {
        if (group >= _groupCount)
        {
            throw new ArgumentOutOfRangeException(nameof(group));
        }

        var descriptorOffset = checked(_groupDescriptorOffset + (long)group * _groupDescriptorSize);
        var descriptor = EndianUtilities.ReadBytes(_reader, descriptorOffset, _groupDescriptorSize);
        ValidateGroupDescriptorChecksum(group, descriptor);
        var flags = EndianUtilities.ReadUInt16Little(descriptor, 0x12);

        var blockBitmapBlock = ReadDescriptorBlock(descriptor, 0x00, 0x20);
        var inodeBitmapBlock = ReadDescriptorBlock(descriptor, 0x04, 0x24);
        ValidateFileSystemBlock(blockBitmapBlock, $"group {group:N0} block bitmap");
        ValidateFileSystemBlock(inodeBitmapBlock, $"group {group:N0} inode bitmap");
        var blockBitmap = EndianUtilities.ReadBytes(
            _reader,
            checked((long)blockBitmapBlock * _blockSize),
            _blockSize);
        var inodeBitmap = EndianUtilities.ReadBytes(
            _reader,
            checked((long)inodeBitmapBlock * _blockSize),
            _blockSize);
        var state = new ExtGroupState(
            group,
            descriptorOffset,
            descriptor,
            blockBitmapBlock,
            inodeBitmapBlock,
            blockBitmap,
            inodeBitmap,
            ReadDescriptorCount(descriptor, 0x0c, 0x2c),
            ReadDescriptorCount(descriptor, 0x0e, 0x2e),
            flags);
        if (validateBitmaps)
        {
            ValidateGroupBitmaps(state);
        }

        return state;
    }

    private void ValidateGroupBitmaps(ExtGroupState state)
    {
        if (_hasMetadataChecksum && (state.Flags & BlockBitmapUninitializedGroupFlag) == 0)
        {
            var blockChecksum = ComputeCrc32C(
                _checksumSeed,
                state.BlockBitmap.AsSpan(0, checked((int)((_blocksPerGroup + 7) / 8))));
            ValidateBitmapChecksum(state.Descriptor, blockChecksum, 0x18, 0x38, "block");
        }

        if (_hasMetadataChecksum && (state.Flags & InodeBitmapUninitializedGroupFlag) == 0)
        {
            var inodeChecksum = ComputeCrc32C(
                _checksumSeed,
                state.InodeBitmap.AsSpan(0, checked((int)((_inodesPerGroup + 7) / 8))));
            ValidateBitmapChecksum(state.Descriptor, inodeChecksum, 0x1a, 0x3a, "inode");
        }

        if ((state.Flags & BlockBitmapUninitializedGroupFlag) == 0)
        {
            var actualFreeBlocks = CountClearBits(state.BlockBitmap, GetValidBlocksInGroup(state.Number));
            if (actualFreeBlocks != state.FreeBlocks)
            {
                throw new InvalidDataException(
                    $"ext4 group {state.Number:N0}のfree block countとbitmapが一致しません。"
                    + $" blocks={state.FreeBlocks:N0}/{actualFreeBlocks:N0}");
            }
        }

        if ((state.Flags & InodeBitmapUninitializedGroupFlag) == 0)
        {
            var actualFreeInodes = CountClearBits(state.InodeBitmap, GetValidInodesInGroup(state.Number));
            if (actualFreeInodes != state.FreeInodes)
            {
                throw new InvalidDataException(
                    $"ext4 group {state.Number:N0}のfree inode countとbitmapが一致しません。"
                    + $" inodes={state.FreeInodes:N0}/{actualFreeInodes:N0}");
            }
        }
    }

    private void ValidateBitmapChecksum(
        byte[] descriptor,
        uint actual,
        int lowOffset,
        int highOffset,
        string label)
    {
        uint expected = EndianUtilities.ReadUInt16Little(descriptor, lowOffset);
        if (_groupDescriptorSize >= highOffset + 2)
        {
            expected |= (uint)EndianUtilities.ReadUInt16Little(descriptor, highOffset) << 16;
        }
        else
        {
            actual &= 0xffff;
        }

        if (expected != actual)
        {
            throw new InvalidDataException($"ext4 {label} bitmap checksumが一致しません。");
        }
    }

    private void ValidateGroupDescriptorChecksum(uint group, byte[] descriptor)
    {
        if (!_hasMetadataChecksum)
        {
            return;
        }

        var expected = EndianUtilities.ReadUInt16Little(descriptor, 0x1e);
        var copy = (byte[])descriptor.Clone();
        copy.AsSpan(0x1e, 2).Clear();
        Span<byte> groupBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(groupBytes, group);
        var checksum = ComputeCrc32C(_checksumSeed, groupBytes);
        checksum = ComputeCrc32C(checksum, copy);
        if (expected != (ushort)checksum)
        {
            throw new InvalidDataException($"ext4 group descriptor {group:N0} checksumが一致しません。");
        }
    }

    private void SetGroupDescriptorChecksum(uint group, byte[] descriptor)
    {
        descriptor.AsSpan(0x1e, 2).Clear();
        if (!_hasMetadataChecksum)
        {
            return;
        }

        Span<byte> groupBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(groupBytes, group);
        var checksum = ComputeCrc32C(_checksumSeed, groupBytes);
        checksum = ComputeCrc32C(checksum, descriptor);
        BinaryPrimitives.WriteUInt16LittleEndian(descriptor.AsSpan(0x1e, 2), (ushort)checksum);
    }

    private void SetBitmapChecksums(ExtGroupState state)
    {
        if (!_hasMetadataChecksum)
        {
            return;
        }

        if (state.BlockBitmapDirty)
        {
            var blockChecksum = ComputeCrc32C(
                _checksumSeed,
                state.BlockBitmap.AsSpan(0, checked((int)((_blocksPerGroup + 7) / 8))));
            BinaryPrimitives.WriteUInt16LittleEndian(state.Descriptor.AsSpan(0x18, 2), (ushort)blockChecksum);
            if (_groupDescriptorSize >= 0x3a)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(
                    state.Descriptor.AsSpan(0x38, 2),
                    (ushort)(blockChecksum >> 16));
            }
        }

        if (state.InodeBitmapDirty)
        {
            var inodeChecksum = ComputeCrc32C(
                _checksumSeed,
                state.InodeBitmap.AsSpan(0, checked((int)((_inodesPerGroup + 7) / 8))));
            BinaryPrimitives.WriteUInt16LittleEndian(state.Descriptor.AsSpan(0x1a, 2), (ushort)inodeChecksum);
            if (_groupDescriptorSize >= 0x3c)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(
                    state.Descriptor.AsSpan(0x3a, 2),
                    (ushort)(inodeChecksum >> 16));
            }
        }
    }

    private ulong ReadDescriptorBlock(byte[] descriptor, int lowOffset, int highOffset)
    {
        ulong value = EndianUtilities.ReadUInt32Little(descriptor, lowOffset);
        if (_groupDescriptorSize >= highOffset + 4)
        {
            value |= (ulong)EndianUtilities.ReadUInt32Little(descriptor, highOffset) << 32;
        }

        return value;
    }

    private uint ReadDescriptorCount(byte[] descriptor, int lowOffset, int highOffset)
    {
        uint value = EndianUtilities.ReadUInt16Little(descriptor, lowOffset);
        if (_groupDescriptorSize >= highOffset + 2)
        {
            value |= (uint)EndianUtilities.ReadUInt16Little(descriptor, highOffset) << 16;
        }

        return value;
    }

    private void WriteDescriptorCount(byte[] descriptor, uint value, int lowOffset, int highOffset)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(descriptor.AsSpan(lowOffset, 2), (ushort)value);
        if (_groupDescriptorSize >= highOffset + 2)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(descriptor.AsSpan(highOffset, 2), (ushort)(value >> 16));
        }
        else if (value > ushort.MaxValue)
        {
            throw new OverflowException("ext4 group descriptor countが16-bit範囲を超えています。");
        }
    }

    private uint GetValidBlocksInGroup(uint group)
    {
        var groupStart = checked((ulong)_firstDataBlock + (ulong)group * _blocksPerGroup);
        if (groupStart >= _blockCount)
        {
            return 0;
        }

        return checked((uint)Math.Min((ulong)_blocksPerGroup, _blockCount - groupStart));
    }

    private uint GetValidInodesInGroup(uint group)
    {
        var first = checked((ulong)group * _inodesPerGroup);
        if (first >= _inodeCount)
        {
            return 0;
        }

        return checked((uint)Math.Min((ulong)_inodesPerGroup, _inodeCount - first));
    }

    private static uint CountClearBits(byte[] bitmap, uint count)
    {
        uint free = 0;
        for (uint bit = 0; bit < count; bit++)
        {
            if (!IsBitSet(bitmap, bit))
            {
                free++;
            }
        }

        return free;
    }

    private static bool IsBitSet(byte[] bitmap, uint bit)
    {
        return (bitmap[checked((int)(bit >> 3))] & (1 << checked((int)(bit & 7)))) != 0;
    }

    private static void SetBit(byte[] bitmap, uint bit, bool value)
    {
        var index = checked((int)(bit >> 3));
        var mask = checked((byte)(1 << checked((int)(bit & 7))));
        bitmap[index] = value
            ? checked((byte)(bitmap[index] | mask))
            : checked((byte)(bitmap[index] & ~mask));
    }

    private void ValidateFileSystemBlock(ulong block, string label)
    {
        if (block < _firstDataBlock || block >= _blockCount)
        {
            throw new InvalidDataException($"ext4 {label}がファイルシステム範囲外です。");
        }
    }

    private bool TryValidateEditableFileSystem(bool requireWriter, out string reason)
    {
        if (requireWriter && _writer is null)
        {
            reason = "変更を保持する書き込みオーバーレイがありません。";
            return false;
        }

        if ((_fileSystemState & ValidFileSystemState) == 0
            || (_incompatibleFeatures & NeedsRecoveryIncompatFlag) != 0
            || (_readOnlyCompatibleFeatures & OrphanPresentReadOnlyCompatibleFlag) != 0)
        {
            reason = "journal replayまたはorphan処理が必要なdirty状態のextファイルシステムは編集できません。";
            return false;
        }

        var unsafeIncompat = CompressionIncompatFlag
            | InlineDataIncompatFlag
            | EncryptionIncompatFlag
            | ExternalJournalIncompatFlag;
        if ((_incompatibleFeatures & unsafeIncompat) != 0
            || (_readOnlyCompatibleFeatures & VerityReadOnlyCompatibleFlag) != 0)
        {
            reason = "圧縮、inline data、暗号化、外部journal、fs-verityを使うextファイルシステムは編集できません。";
            return false;
        }

        try
        {
            ValidateSuperBlockChecksum();
        }
        catch (InvalidDataException ex)
        {
            reason = ex.Message;
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private bool ValidateAllocationMetadata(bool requireWriter, out string reason)
    {
        if (!TryValidateEditableFileSystem(requireWriter, out reason))
        {
            return false;
        }

        // Allocation-changing operations are intentionally disabled without metadata_csum.
        // Legacy/synthetic images can still use their allocation-preserving write path.
        if (!_hasMetadataChecksum)
        {
            reason = string.Empty;
            return true;
        }

        try
        {
            _ = new ExtMutationContext(this);
            for (uint group = 0; group < _groupCount; group++)
            {
                _ = LoadGroupState(group, validateBitmaps: true);
            }

            reason = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or OverflowException)
        {
            reason = ex.Message;
            return false;
        }
    }

    private long GetRequiredBlocks(long size)
    {
        return size == 0 ? 0 : checked((size + _blockSize - 1L) / _blockSize);
    }

    private void WriteInodeContent(
        ExtInode inode,
        Stream content,
        long contentLength,
        CancellationToken cancellationToken)
    {
        var extents = ValidateFullyAllocated(inode);
        var remaining = contentLength;
        var buffer = new byte[1024 * 1024];
        foreach (var extent in extents)
        {
            var extentBytes = checked((long)extent.BlockCount * _blockSize);
            var bytesToWrite = Math.Min(remaining, extentBytes);
            var physicalOffset = checked((long)extent.PhysicalBlock * _blockSize);
            long written = 0;
            while (written < bytesToWrite)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = checked((int)Math.Min(buffer.Length, bytesToWrite - written));
                content.ReadExactly(buffer.AsSpan(0, count));
                _writer!.WriteAt(physicalOffset + written, buffer, 0, count);
                written += count;
                remaining -= count;
            }

            if (remaining == 0)
            {
                break;
            }
        }

        if (remaining != 0 || content.ReadByte() != -1)
        {
            throw new InvalidDataException("編集内容のサイズが指定値と一致しません。");
        }
    }

    private void ZeroInodeRange(
        ExtInode inode,
        long offset,
        long count,
        CancellationToken cancellationToken)
    {
        var extents = GetDataExtents(inode).OrderBy(extent => extent.LogicalBlock).ToArray();
        var zeros = new byte[Math.Min(_blockSize, 1024 * 1024)];
        var remaining = count;
        var logicalOffset = offset;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var logicalBlock = checked((uint)(logicalOffset / _blockSize));
            var blockOffset = checked((int)(logicalOffset % _blockSize));
            var extent = extents.Single(candidate =>
                logicalBlock >= candidate.LogicalBlock
                && logicalBlock < candidate.LogicalBlock + candidate.BlockCount);
            var physicalBlock = checked(extent.PhysicalBlock + logicalBlock - extent.LogicalBlock);
            var writeCount = checked((int)Math.Min(remaining, _blockSize - blockOffset));
            _writer!.WriteAt(
                checked((long)physicalBlock * _blockSize + blockOffset),
                zeros,
                0,
                writeCount);
            logicalOffset += writeCount;
            remaining -= writeCount;
        }
    }

    private void WriteInodeSizeAndTimes(ExtInode inode, long size)
    {
        var data = (byte[])inode.RawData.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4, 4), checked((uint)(ulong)size));
        if (_inodeSize > 128)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(108, 4), checked((uint)((ulong)size >> 32)));
        }

        var now = checked((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12, 4), now);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16, 4), now);
        if (HasLargeInodeField(data, 0x84, 8))
        {
            data.AsSpan(0x84, 8).Clear();
        }

        SetInodeChecksum(inode.Number, inode.Generation, data);
        _writer!.WriteAt(inode.DiskOffset, data, 0, data.Length);
    }

    private void ValidateSuperBlockChecksum()
    {
        if (!_hasMetadataChecksum)
        {
            return;
        }

        var superBlock = ReadSuperBlockForWrite();
        if (superBlock[0x175] != 1)
        {
            throw new InvalidDataException("未対応のext4 metadata checksum方式です。");
        }

        var expected = EndianUtilities.ReadUInt32Little(superBlock, 0x3fc);
        var actual = ComputeCrc32C(uint.MaxValue, superBlock.AsSpan(0, 0x3fc));
        if (expected != actual)
        {
            throw new InvalidDataException(
                $"ext4 superblock checksumが一致しません: expected=0x{expected:X8}, actual=0x{actual:X8}");
        }
    }

    private void ValidateInodeChecksum(ExtInode inode)
    {
        if (!_hasMetadataChecksum)
        {
            return;
        }

        var expected = ReadInodeChecksum(inode.RawData);
        var data = (byte[])inode.RawData.Clone();
        SetInodeChecksum(inode.Number, inode.Generation, data);
        var actual = ReadInodeChecksum(data);
        if (expected != actual)
        {
            throw new InvalidDataException(
                $"ext4 inode {inode.Number:N0} checksumが一致しません: expected=0x{expected:X8}, actual=0x{actual:X8}");
        }
    }

    private uint ReadInodeChecksum(byte[] data)
    {
        uint checksum = EndianUtilities.ReadUInt16Little(data, 0x7c);
        if (HasLargeInodeField(data, 0x82, 2))
        {
            checksum |= (uint)EndianUtilities.ReadUInt16Little(data, 0x82) << 16;
        }

        return checksum;
    }

    private void SetInodeChecksum(uint inodeNumber, uint generation, byte[] data)
    {
        if (!_hasMetadataChecksum)
        {
            return;
        }

        data.AsSpan(0x7c, 2).Clear();
        var hasHighChecksum = HasLargeInodeField(data, 0x82, 2);
        if (hasHighChecksum)
        {
            data.AsSpan(0x82, 2).Clear();
        }

        Span<byte> numberBytes = stackalloc byte[4];
        Span<byte> generationBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(numberBytes, inodeNumber);
        BinaryPrimitives.WriteUInt32LittleEndian(generationBytes, generation);
        var checksum = ComputeCrc32C(_checksumSeed, numberBytes);
        checksum = ComputeCrc32C(checksum, generationBytes);
        checksum = ComputeCrc32C(checksum, data);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x7c, 2), (ushort)checksum);
        if (hasHighChecksum)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x82, 2), (ushort)(checksum >> 16));
        }
    }

    private bool HasLargeInodeField(byte[] data, int offset, int length)
    {
        if (_inodeSize <= 128 || data.Length < 0x82)
        {
            return false;
        }

        var extraSize = EndianUtilities.ReadUInt16Little(data, 0x80);
        return offset + length <= 128 + extraSize && offset + length <= data.Length;
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

    private sealed class ExtMutationContext
    {
        private readonly ExtFileSystem _fileSystem;
        private readonly Dictionary<uint, ExtGroupState> _states = new();
        private readonly ulong _initialFreeBlocks;
        private readonly uint _initialFreeInodes;
        private long _freeBlockDelta;
        private long _freeInodeDelta;
        private bool _committed;

        public ExtMutationContext(ExtFileSystem fileSystem)
        {
            _fileSystem = fileSystem;
            var superBlock = fileSystem.ReadSuperBlockForWrite();
            fileSystem.ValidateSuperBlockChecksum();
            _initialFreeBlocks = EndianUtilities.ReadUInt32Little(superBlock, 0x0c)
                | ((ulong)EndianUtilities.ReadUInt32Little(superBlock, 0x158) << 32);
            _initialFreeInodes = EndianUtilities.ReadUInt32Little(superBlock, 0x10);

            ulong descriptorFreeBlocks = 0;
            ulong descriptorFreeInodes = 0;
            for (uint group = 0; group < fileSystem._groupCount; group++)
            {
                var descriptorOffset = checked(
                    fileSystem._groupDescriptorOffset + (long)group * fileSystem._groupDescriptorSize);
                var descriptor = EndianUtilities.ReadBytes(
                    fileSystem._reader,
                    descriptorOffset,
                    fileSystem._groupDescriptorSize);
                fileSystem.ValidateGroupDescriptorChecksum(group, descriptor);
                descriptorFreeBlocks = checked(
                    descriptorFreeBlocks + fileSystem.ReadDescriptorCount(descriptor, 0x0c, 0x2c));
                descriptorFreeInodes = checked(
                    descriptorFreeInodes + fileSystem.ReadDescriptorCount(descriptor, 0x0e, 0x2e));
            }

            if (descriptorFreeBlocks != _initialFreeBlocks || descriptorFreeInodes != _initialFreeInodes)
            {
                throw new InvalidDataException(
                    "ext4 superblockとgroup descriptorのfree countが一致しません。"
                    + $" blocks={_initialFreeBlocks:N0}/{descriptorFreeBlocks:N0},"
                    + $" inodes={_initialFreeInodes:N0}/{descriptorFreeInodes:N0}");
            }
        }

        public ulong FindContiguousBlocks(uint count)
        {
            if (count == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            for (uint group = 0; group < _fileSystem._groupCount; group++)
            {
                var state = GetState(group);
                if ((state.Flags & BlockBitmapUninitializedGroupFlag) != 0
                    || state.FreeBlocks < count)
                {
                    continue;
                }

                var validBlocks = _fileSystem.GetValidBlocksInGroup(group);
                uint runStart = 0;
                uint runLength = 0;
                for (uint bit = 0; bit < validBlocks; bit++)
                {
                    if (!IsBitSet(state.BlockBitmap, bit))
                    {
                        if (runLength == 0)
                        {
                            runStart = bit;
                        }

                        runLength++;
                        if (runLength == count)
                        {
                            return checked(
                                (ulong)_fileSystem._firstDataBlock
                                + (ulong)group * _fileSystem._blocksPerGroup
                                + runStart);
                        }
                    }
                    else
                    {
                        runLength = 0;
                    }
                }
            }

            throw new NotSupportedException(
                $"{count:N0}個の連続した空きblockをext4内に確保できません。");
        }

        public uint FindFreeInode(uint preferredGroup)
        {
            for (uint distance = 0; distance < _fileSystem._groupCount; distance++)
            {
                var group = checked((preferredGroup + distance) % _fileSystem._groupCount);
                var state = GetState(group);
                if ((state.Flags & InodeBitmapUninitializedGroupFlag) != 0
                    || state.FreeInodes == 0)
                {
                    continue;
                }

                var validInodes = _fileSystem.GetValidInodesInGroup(group);
                for (uint bit = 0; bit < validInodes; bit++)
                {
                    var inodeNumber = checked(group * _fileSystem._inodesPerGroup + bit + 1);
                    if (inodeNumber >= _fileSystem._firstInode && !IsBitSet(state.InodeBitmap, bit))
                    {
                        return inodeNumber;
                    }
                }
            }

            throw new NotSupportedException("ext4内に空きinodeがありません。");
        }

        public void SetBlocksAllocated(ulong startBlock, uint count, bool allocated)
        {
            for (uint index = 0; index < count; index++)
            {
                var block = checked(startBlock + index);
                _fileSystem.ValidateFileSystemBlock(block, "allocation block");
                var relative = checked(block - _fileSystem._firstDataBlock);
                var group = checked((uint)(relative / _fileSystem._blocksPerGroup));
                var bit = checked((uint)(relative % _fileSystem._blocksPerGroup));
                var state = GetState(group);
                if ((state.Flags & BlockBitmapUninitializedGroupFlag) != 0)
                {
                    throw new NotSupportedException(
                        $"block bitmapが未初期化のext4 group {group:N0}には割り当てできません。");
                }

                var current = IsBitSet(state.BlockBitmap, bit);
                if (current == allocated)
                {
                    throw new InvalidDataException(
                        $"ext4 block {block:N0}のallocation状態が想定と異なります。" );
                }

                SetBit(state.BlockBitmap, bit, allocated);
                if (allocated)
                {
                    if (state.FreeBlocks == 0)
                    {
                        throw new InvalidDataException("ext4 groupのfree block countが不足しています。");
                    }

                    state.FreeBlocks--;
                    _freeBlockDelta--;
                }
                else
                {
                    state.FreeBlocks = checked(state.FreeBlocks + 1);
                    _freeBlockDelta++;
                }

                state.BlockBitmapDirty = true;
                state.DescriptorDirty = true;
            }
        }

        public void SetInodeAllocated(uint inodeNumber, bool allocated)
        {
            if (inodeNumber == 0 || inodeNumber > _fileSystem._inodeCount)
            {
                throw new ArgumentOutOfRangeException(nameof(inodeNumber));
            }

            var group = (inodeNumber - 1) / _fileSystem._inodesPerGroup;
            var bit = (inodeNumber - 1) % _fileSystem._inodesPerGroup;
            var state = GetState(group);
            if ((state.Flags & InodeBitmapUninitializedGroupFlag) != 0)
            {
                throw new NotSupportedException(
                    $"inode bitmapが未初期化のext4 group {group:N0}にはinodeを割り当てできません。");
            }

            var current = IsBitSet(state.InodeBitmap, bit);
            if (current == allocated)
            {
                throw new InvalidDataException(
                    $"ext4 inode {inodeNumber:N0}のallocation状態が想定と異なります。" );
            }

            SetBit(state.InodeBitmap, bit, allocated);
            if (allocated)
            {
                if (state.FreeInodes == 0)
                {
                    throw new InvalidDataException("ext4 groupのfree inode countが不足しています。");
                }

                state.FreeInodes--;
                _freeInodeDelta--;

                var unused = _fileSystem.ReadDescriptorCount(state.Descriptor, 0x1c, 0x32);
                var firstUnusedBit = _fileSystem._inodesPerGroup - Math.Min(unused, _fileSystem._inodesPerGroup);
                if (bit >= firstUnusedBit)
                {
                    var newUnused = checked(_fileSystem._inodesPerGroup - bit - 1);
                    _fileSystem.WriteDescriptorCount(state.Descriptor, newUnused, 0x1c, 0x32);
                }
            }
            else
            {
                state.FreeInodes = checked(state.FreeInodes + 1);
                _freeInodeDelta++;
            }

            state.InodeBitmapDirty = true;
            state.DescriptorDirty = true;
        }

        public void Commit()
        {
            if (_committed)
            {
                throw new InvalidOperationException("ext4 mutationは既にcommit済みです。");
            }

            foreach (var state in _states.Values.OrderBy(candidate => candidate.Number))
            {
                if (state.BlockBitmapDirty)
                {
                    _fileSystem._writer!.WriteAt(
                        checked((long)state.BlockBitmapBlock * _fileSystem._blockSize),
                        state.BlockBitmap,
                        0,
                        state.BlockBitmap.Length);
                }

                if (state.InodeBitmapDirty)
                {
                    _fileSystem._writer!.WriteAt(
                        checked((long)state.InodeBitmapBlock * _fileSystem._blockSize),
                        state.InodeBitmap,
                        0,
                        state.InodeBitmap.Length);
                }

                if (state.DescriptorDirty)
                {
                    _fileSystem.WriteDescriptorCount(state.Descriptor, state.FreeBlocks, 0x0c, 0x2c);
                    _fileSystem.WriteDescriptorCount(state.Descriptor, state.FreeInodes, 0x0e, 0x2e);
                    _fileSystem.SetBitmapChecksums(state);
                    _fileSystem.SetGroupDescriptorChecksum(state.Number, state.Descriptor);
                    _fileSystem._writer!.WriteAt(
                        state.DescriptorOffset,
                        state.Descriptor,
                        0,
                        state.Descriptor.Length);
                }
            }

            var freeBlocks = AddDelta(_initialFreeBlocks, _freeBlockDelta, "free block");
            var freeInodes64 = AddDelta(_initialFreeInodes, _freeInodeDelta, "free inode");
            if (freeInodes64 > uint.MaxValue)
            {
                throw new OverflowException("ext4 free inode countが32-bit範囲を超えています。");
            }

            var superBlock = _fileSystem.ReadSuperBlockForWrite();
            BinaryPrimitives.WriteUInt32LittleEndian(superBlock.AsSpan(0x0c, 4), (uint)freeBlocks);
            BinaryPrimitives.WriteUInt32LittleEndian(superBlock.AsSpan(0x158, 4), (uint)(freeBlocks >> 32));
            BinaryPrimitives.WriteUInt32LittleEndian(superBlock.AsSpan(0x10, 4), (uint)freeInodes64);
            BinaryPrimitives.WriteUInt32LittleEndian(
                superBlock.AsSpan(0x30, 4),
                checked((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
            if (_fileSystem._hasMetadataChecksum)
            {
                superBlock.AsSpan(0x3fc, 4).Clear();
                var checksum = ComputeCrc32C(uint.MaxValue, superBlock.AsSpan(0, 0x3fc));
                BinaryPrimitives.WriteUInt32LittleEndian(superBlock.AsSpan(0x3fc, 4), checksum);
            }

            _fileSystem._writer!.WriteAt(1024, superBlock, 0, superBlock.Length);
            _committed = true;
        }

        private ExtGroupState GetState(uint group)
        {
            if (!_states.TryGetValue(group, out var state))
            {
                state = _fileSystem.LoadGroupState(group, validateBitmaps: true);
                _states.Add(group, state);
            }

            return state;
        }

        private static ulong AddDelta(ulong value, long delta, string label)
        {
            if (delta >= 0)
            {
                return checked(value + (ulong)delta);
            }

            var magnitude = checked((ulong)-delta);
            if (magnitude > value)
            {
                throw new InvalidDataException($"ext4 {label} countが負になります。");
            }

            return value - magnitude;
        }
    }

    private sealed class ExtGroupState(
        uint number,
        long descriptorOffset,
        byte[] descriptor,
        ulong blockBitmapBlock,
        ulong inodeBitmapBlock,
        byte[] blockBitmap,
        byte[] inodeBitmap,
        uint freeBlocks,
        uint freeInodes,
        ushort flags)
    {
        public uint Number { get; } = number;
        public long DescriptorOffset { get; } = descriptorOffset;
        public byte[] Descriptor { get; } = descriptor;
        public ulong BlockBitmapBlock { get; } = blockBitmapBlock;
        public ulong InodeBitmapBlock { get; } = inodeBitmapBlock;
        public byte[] BlockBitmap { get; } = blockBitmap;
        public byte[] InodeBitmap { get; } = inodeBitmap;
        public uint FreeBlocks { get; set; } = freeBlocks;
        public uint FreeInodes { get; set; } = freeInodes;
        public ushort Flags { get; } = flags;
        public bool BlockBitmapDirty { get; set; }
        public bool InodeBitmapDirty { get; set; }
        public bool DescriptorDirty { get; set; }
    }

    private sealed record ExtDirectoryBlock(ulong PhysicalBlock, byte[] Data, int EntryBytes);

    private sealed record ExtDirectoryLocation(
        ExtDirectoryBlock Block,
        int Offset,
        int PreviousOffset,
        int SplitOffset);

    private sealed record ExtExtent(uint LogicalBlock, uint BlockCount, ulong PhysicalBlock, bool Initialized);

    private sealed record ExtInode(
        uint Number,
        ushort Mode,
        ulong Size,
        uint Flags,
        DateTime? ModifiedUtc,
        byte[] BlockBytes,
        ushort LinkCount,
        uint Generation,
        long DiskOffset,
        byte[] RawData)
    {
        public bool IsDirectory => (Mode & 0xf000) == 0x4000;
        public bool IsRegularFile => (Mode & 0xf000) == 0x8000;
    }
}
