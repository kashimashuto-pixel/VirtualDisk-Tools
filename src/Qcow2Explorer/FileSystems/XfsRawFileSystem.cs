using System.Text;
using System.Globalization;
using System.Diagnostics;
using System.Buffers.Binary;
using Qcow2Explorer.Core;

namespace Qcow2Explorer.FileSystems;

internal sealed partial class XfsRawFileSystem
{
    private const uint SuperBlockMagic = 0x58465342;
    private const ushort InodeMagic = 0x494e;
    private const uint BlockDirectoryMagic = 0x58443242;
    private const uint DataDirectoryMagic = 0x58443244;
    private const uint BlockDirectoryMagicV5 = 0x58444233;
    private const uint DataDirectoryMagicV5 = 0x58444433;
    private const uint BmapMagic = 0x424d4150;
    private const uint BmapMagicV5 = 0x424d4133;
    private const ulong DirectoryLeafOffsetBytes = 1UL << 35;
    private const int MaxSymlinkDepth = 12;

    private readonly IBlockReader _reader;
    private readonly IBlockWriter? _writer;
    private readonly XfsSuperBlock _superBlock;
    private readonly Dictionary<ulong, XfsInode> _inodeCache = new();
    private readonly Dictionary<ulong, IReadOnlyList<XfsExtent>> _extentCache = new();
    private readonly Dictionary<ulong, IReadOnlyList<XfsDirectoryEntry>> _directoryCache = new();
    private readonly HashSet<ulong> _diagnosedInodes = [];
    private readonly HashSet<ulong> _diagnosedOutOfDataDeviceInodes = [];
    private readonly Encoding _fileNameEncoding;
    private bool? _hasCleanLog;

    private XfsRawFileSystem(IBlockReader reader)
    {
        _reader = reader;
        _writer = reader as IBlockWriter;
        _superBlock = ReadSuperBlock(reader);
        _fileNameEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
    }

    public ulong RootInode => _superBlock.RootInode;

    public static XfsRawFileSystem? TryOpen(IBlockReader reader)
    {
        try
        {
            return new XfsRawFileSystem(reader);
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    public XfsNodeRef RootRef => new(@"\", RootInode, XfsRawNodeKind.Directory);

    public IReadOnlyList<VfsNode> ListDirectory(XfsNodeRef directory)
    {
        var inode = ReadInode(directory.Inode);
        if (!inode.IsDirectory)
        {
            return Array.Empty<VfsNode>();
        }

        var nodes = new List<VfsNode>();
        foreach (var entry in ReadDirectoryEntries(inode))
        {
            if (entry.Name is "." or "..")
            {
                continue;
            }

            var childPath = CombinePath(directory.Path, entry.Name);
            if (TryCreateNode(entry.Inode, entry.Name, childPath, directory.Path, 0, out var node))
            {
                nodes.Add(node);
            }
        }

        return nodes
            .OrderByDescending(n => n.IsDirectory)
            .ThenBy(n => n.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public byte[] ReadFile(XfsNodeRef file, long offset, int count)
    {
        if (offset < 0 || count <= 0)
        {
            return Array.Empty<byte>();
        }

        var inode = ReadInode(file.Inode);
        if (inode.IsDirectory)
        {
            return Array.Empty<byte>();
        }

        if (inode.IsSymlink && file.Kind == XfsRawNodeKind.Symlink)
        {
            var target = Encoding.UTF8.GetBytes(ReadSymlinkTarget(inode));
            if (offset >= target.Length)
            {
                return Array.Empty<byte>();
            }

            var available = Math.Min(count, target.Length - (int)offset);
            var result = new byte[available];
            Array.Copy(target, (int)offset, result, 0, available);
            return result;
        }

        try
        {
            WriteFileDiagnostics(file, inode);

            var maxAvailable = Math.Min(count, inode.Length > long.MaxValue ? long.MaxValue : (long)inode.Length - offset);
            if (maxAvailable <= 0)
            {
                return Array.Empty<byte>();
            }

            return ReadContent(inode, offset, checked((int)maxAvailable));
        }
        catch (Exception ex) when (ex is OverflowException or IOException or InvalidDataException or ArgumentOutOfRangeException)
        {
            throw new IOException(
                $"XFS raw file read failed: path={file.Path}, inode={inode.Number}, offset={offset}, count={count}, format={inode.Format}, length={inode.Length}.",
                ex);
        }
    }

    public bool TryResolvePath(string path, out XfsNodeRef node)
    {
        return TryResolvePath(@"\", path, 0, out node);
    }

    public string Describe(XfsNodeRef node)
    {
        var builder = new StringBuilder();
        var inode = ReadInode(node.Inode);
        builder.Append(CultureInvariant($"inode={inode.Number}, kind={node.Kind}, mode=0x{inode.Mode:X4}, fileType={inode.FileType}, format={inode.Format}, len={inode.Length}, blocks={inode.BlockCount}, extents={inode.ExtentCount}, forkoff={inode.ForkOffset}, df={inode.DataFork.Length}"));
        if (!inode.IsDirectory && !inode.IsSymlink)
        {
            return builder.ToString();
        }

        if (inode.Format is 2 or 3)
        {
            var extents = GetExtents(inode).OrderBy(e => e.StartOffset).Take(12).ToList();
            builder.Append(CultureInvariant($", parsedExtents={extents.Count}"));
            var leafOffsetBlocks = DirectoryLeafOffsetBytes / _superBlock.BlockSize;
            foreach (var extent in extents)
            {
                var magic = extent.StartOffset < leafOffsetBlocks && extent.BlockCount > 0
                    ? EndianUtilities.ReadUInt32Big(ReadBytesAtFileSystemBlock(extent.StartBlock, 4), 0)
                    : 0;
                builder.Append(CultureInvariant($" | off={extent.StartOffset}, start={extent.StartBlock}, count={extent.BlockCount}, magic=0x{magic:X8}"));
            }
        }

        return builder.ToString();
    }

    public bool CanReplaceFile(XfsNodeRef file, long replacementLength, out string reason)
    {
        if (_writer is null)
        {
            reason = "変更を保持する書き込みオーバーレイがありません。";
            return false;
        }

        if (file.Kind != XfsRawNodeKind.RegularFile || replacementLength < 0)
        {
            reason = "通常ファイルだけを置換できます。";
            return false;
        }

        XfsInode inode;
        try
        {
            inode = ReadInode(file.Inode);
        }
        catch (Exception ex)
        {
            reason = $"inodeを読み取れません: {ex.Message}";
            return false;
        }

        if (inode.Length > long.MaxValue || replacementLength != (long)inode.Length)
        {
            reason = $"現在は元ファイルと同じサイズ（{inode.Length:N0} bytes）の置換だけに対応しています。";
            return false;
        }

        if (_superBlock.InProgress)
        {
            reason = "mkfsまたはgrowfsが完了していないXFSファイルシステムは書き込めません。";
            return false;
        }

        if (!HasCleanUnmountRecord())
        {
            reason = "clean unmount recordを確認できないXFS logは書き込めません。Linuxでlog replayと正常unmountを完了してください。";
            return false;
        }

        if ((inode.Flags & 0x0001) != 0)
        {
            reason = "realtime device上のXFSファイルはまだ書き込めません。";
            return false;
        }

        if ((inode.Flags2 & 0x0002) != 0)
        {
            reason = "共有reflink extentを持つXFSファイルはまだ書き込めません。";
            return false;
        }

        if (inode.Format is not (2 or 3))
        {
            reason = "local形式のXFSファイルはまだ書き込めません。";
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
        XfsNodeRef file,
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

        var inode = ReadInode(file.Inode);
        var extents = ValidateFullyAllocated(inode);
        var remaining = replacementLength;
        var buffer = new byte[1024 * 1024];
        foreach (var extent in extents)
        {
            var extentBytes = checked((long)extent.BlockCount * _superBlock.BlockSize);
            var bytesToWrite = Math.Min(remaining, extentBytes);
            var physicalOffset = ExtentToDiskOffset(extent.StartBlock);
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

    private static XfsSuperBlock ReadSuperBlock(IBlockReader reader)
    {
        if (reader.Length < 512)
        {
            throw new InvalidDataException("XFS superblock is too small.");
        }

        var buffer = EndianUtilities.ReadBytes(reader, 0, 512);
        if (EndianUtilities.ReadUInt32Big(buffer, 0) != SuperBlockMagic)
        {
            throw new InvalidDataException("Invalid XFS superblock magic.");
        }

        var blockSize = EndianUtilities.ReadUInt32Big(buffer, 0x04);
        var version = ReadUInt16Big(buffer, 0x64);
        var sbVersion = (ushort)(version & 0x000f);
        var inodeSize = ReadUInt16Big(buffer, 0x68);
        var inodesPerBlock = ReadUInt16Big(buffer, 0x6a);
        var blockSizeLog2 = buffer[0x78];
        var inodeSizeLog2 = buffer[0x7a];
        var inodesPerBlockLog2 = buffer[0x7b];
        var agBlocksLog2 = buffer[0x7c];
        var dirBlockLog2 = buffer[0xc0];
        var features2 = EndianUtilities.ReadUInt32Big(buffer, 0xc8);
        var compatibleFeatures = sbVersion >= 5 ? EndianUtilities.ReadUInt32Big(buffer, 0xd0) : 0;
        var readOnlyCompatibleFeatures = sbVersion >= 5 ? EndianUtilities.ReadUInt32Big(buffer, 0xd4) : 0;
        var incompatibleFeatures = sbVersion >= 5 ? EndianUtilities.ReadUInt32Big(buffer, 0xd8) : 0;
        var logIncompatibleFeatures = sbVersion >= 5 ? EndianUtilities.ReadUInt32Big(buffer, 0xdc) : 0;
        var logStart = EndianUtilities.ReadUInt64Big(buffer, 0x30);
        var logBlocks = EndianUtilities.ReadUInt32Big(buffer, 0x60);
        var agOffsetBits = agBlocksLog2 + inodesPerBlockLog2;
        if (agOffsetBits <= 0 || agOffsetBits >= 63)
        {
            throw new NotSupportedException("Unsupported XFS inode geometry.");
        }

        return new XfsSuperBlock(
            blockSize,
            EndianUtilities.ReadUInt64Big(buffer, 0x08),
            EndianUtilities.ReadUInt64Big(buffer, 0x10),
            EndianUtilities.ReadUInt64Big(buffer, 0x38),
            EndianUtilities.ReadUInt32Big(buffer, 0x54),
            EndianUtilities.ReadUInt32Big(buffer, 0x58),
            sbVersion,
            inodeSize,
            inodesPerBlock,
            blockSizeLog2,
            inodeSizeLog2,
            inodesPerBlockLog2,
            agBlocksLog2,
            dirBlockLog2,
            ((ulong)1 << agOffsetBits) - 1,
            blockSize << dirBlockLog2,
            sbVersion == 5 && (incompatibleFeatures & 0x1) != 0 || (version & 0x8000) != 0 && (features2 & 0x0200) != 0,
            sbVersion == 5 && (incompatibleFeatures & 0x08) != 0,
            sbVersion == 5 && (incompatibleFeatures & 0x20) != 0,
            buffer[0x7e] != 0,
            logStart,
            logBlocks,
            buffer.AsSpan(0x20, 16).ToArray(),
            ReadUInt16Big(buffer, 0x66),
            compatibleFeatures,
            readOnlyCompatibleFeatures,
            incompatibleFeatures,
            logIncompatibleFeatures,
            EndianUtilities.ReadUInt16Big(buffer, 0xb0));
    }

    private XfsInode ReadInode(ulong number)
    {
        if (_inodeCache.TryGetValue(number, out var cached))
        {
            return cached;
        }

        var relative = number & _superBlock.RelativeInodeMask;
        var allocationGroup = number >> (_superBlock.AgBlocksLog2 + _superBlock.InodesPerBlockLog2);
        var agBlock = (number >> _superBlock.InodesPerBlockLog2) & ((1UL << _superBlock.AgBlocksLog2) - 1);
        var blockOffset = number & ((1UL << _superBlock.InodesPerBlockLog2) - 1);
        var inodeOffset = checked((long)checked(
            checked(checked(allocationGroup * _superBlock.AgBlocks) * _superBlock.BlockSize)
            + checked(checked(agBlock * _superBlock.BlockSize) + checked(blockOffset * _superBlock.InodeSize))));

        var buffer = EndianUtilities.ReadBytes(_reader, inodeOffset, _superBlock.InodeSize);
        if (ReadUInt16Big(buffer, 0) != InodeMagic)
        {
            throw new InvalidDataException($"Invalid XFS inode magic at inode {number}.");
        }

        var version = buffer[0x04];
        if (version >= 3)
        {
            ValidateChecksum(buffer, 0x64, $"XFS inode {number}");
        }

        var dataForkOffset = version < 3 ? 0x64 : 0xb0;
        var flags2 = version >= 3 && buffer.Length >= 0x80
            ? EndianUtilities.ReadUInt64Big(buffer, 0x78)
            : 0;
        var hasBigTime = _superBlock.HasBigTime && (flags2 & 0x08) != 0;
        var forkOffset = buffer[0x52];
        var dataForkLength = forkOffset == 0
            ? buffer.Length - dataForkOffset
            : Math.Max(0, Math.Min(buffer.Length - dataForkOffset, forkOffset * 8 - dataForkOffset));
        var dataFork = new byte[dataForkLength];
        Array.Copy(buffer, dataForkOffset, dataFork, 0, dataFork.Length);

        var inode = new XfsInode(
            number,
            relative,
            ReadUInt16Big(buffer, 0x02),
            buffer[0x05],
            ReadUInt32Big(buffer, 0x10),
            XfsTimestampDecoder.Decode(buffer.AsSpan(0x28, 8), hasBigTime),
            EndianUtilities.ReadUInt64Big(buffer, 0x38),
            EndianUtilities.ReadUInt64Big(buffer, 0x40),
            _superBlock.HasLargeExtentCounts
                ? EndianUtilities.ReadUInt64Big(buffer, 0x18)
                : EndianUtilities.ReadUInt32Big(buffer, 0x4c),
            ReadUInt16Big(buffer, 0x5a),
            flags2,
            forkOffset,
            dataFork,
            version,
            inodeOffset,
            buffer);
        _inodeCache[number] = inode;
        return inode;
    }

    private IReadOnlyList<XfsDirectoryEntry> ReadDirectoryEntries(XfsInode inode)
    {
        if (_directoryCache.TryGetValue(inode.Number, out var cached))
        {
            return cached;
        }

        var entries = inode.Format switch
        {
            1 => ReadShortFormDirectory(inode),
            2 or 3 => ReadExtentDirectory(inode),
            _ => Array.Empty<XfsDirectoryEntry>()
        };
        _directoryCache[inode.Number] = entries;
        return entries;
    }

    private IReadOnlyList<XfsDirectoryEntry> ReadShortFormDirectory(XfsInode inode)
    {
        var data = inode.DataFork;
        if (data.Length < 6)
        {
            return Array.Empty<XfsDirectoryEntry>();
        }

        var count = data[0];
        var useShortInode = data[1] == 0;
        var offset = 2 + (useShortInode ? 4 : 8);
        var result = new List<XfsDirectoryEntry>(count);
        for (var i = 0; i < count && offset + 3 < data.Length; i++)
        {
            var nameLength = data[offset];
            var entryOffset = offset + 3;
            if (nameLength == 0 || entryOffset + nameLength > data.Length)
            {
                break;
            }

            var name = DecodeName(data, entryOffset, nameLength);
            entryOffset += nameLength;
            var ftype = (byte)0;
            if (_superBlock.HasFType && entryOffset < data.Length)
            {
                ftype = data[entryOffset++];
            }

            if (entryOffset + (useShortInode ? 4 : 8) > data.Length)
            {
                break;
            }

            var childInode = useShortInode
                ? EndianUtilities.ReadUInt32Big(data, entryOffset)
                : EndianUtilities.ReadUInt64Big(data, entryOffset);
            offset = entryOffset + (useShortInode ? 4 : 8);
            result.Add(new XfsDirectoryEntry(name, childInode, ftype));
        }

        return result;
    }

    private IReadOnlyList<XfsDirectoryEntry> ReadExtentDirectory(XfsInode inode)
    {
        var result = new Dictionary<string, XfsDirectoryEntry>(StringComparer.Ordinal);
        var leafOffsetBlocks = DirectoryLeafOffsetBytes / _superBlock.BlockSize;
        var dirBlockFsBlocks = Math.Max(1UL, _superBlock.DirectoryBlockSize / _superBlock.BlockSize);

        foreach (var extent in GetExtents(inode).OrderBy(e => e.StartOffset))
        {
            if (extent.StartOffset >= leafOffsetBlocks)
            {
                continue;
            }

            var dataBlockCount = Math.Min(extent.BlockCount, leafOffsetBlocks - extent.StartOffset);
            for (var blockOffset = 0UL; blockOffset < dataBlockCount; blockOffset += dirBlockFsBlocks)
            {
                var remainingBlocks = dataBlockCount - blockOffset;
                var bytesToRead = checked((int)Math.Min(_superBlock.DirectoryBlockSize, remainingBlocks * _superBlock.BlockSize));
                if (bytesToRead < 16)
                {
                    continue;
                }

                var block = ReadBytesAtFileSystemBlock(extent.StartBlock + blockOffset, bytesToRead);
                foreach (var entry in ParseDirectoryDataBlock(block))
                {
                    if (entry.Name is "." or "..")
                    {
                        continue;
                    }

                    result[entry.Name] = entry;
                }
            }
        }

        return result.Values.ToList();
    }

    private IEnumerable<XfsDirectoryEntry> ParseDirectoryDataBlock(byte[] block)
    {
        if (block.Length < 16)
        {
            yield break;
        }

        var magic = EndianUtilities.ReadUInt32Big(block, 0);
        var isV5 = magic is BlockDirectoryMagicV5 or DataDirectoryMagicV5;
        var isBlockDirectory = magic is BlockDirectoryMagic or BlockDirectoryMagicV5;
        if (magic is not (BlockDirectoryMagic or DataDirectoryMagic or BlockDirectoryMagicV5 or DataDirectoryMagicV5))
        {
            yield break;
        }

        var offset = isV5 ? 0x40 : 0x10;
        var eof = block.Length;
        if (isBlockDirectory && block.Length >= 8)
        {
            var leafCount = EndianUtilities.ReadUInt32Big(block, block.Length - 8);
            var tailLength = 8L + leafCount * 8L;
            if (tailLength > 0 && tailLength < block.Length)
            {
                eof = checked((int)(block.Length - tailLength));
            }
        }

        while (offset + 8 < eof)
        {
            if (block[offset] == 0xff && block[offset + 1] == 0xff)
            {
                var length = ReadUInt16Big(block, offset + 2);
                if (length < 8 || offset + length > eof)
                {
                    yield break;
                }

                offset += length;
                continue;
            }

            var inode = EndianUtilities.ReadUInt64Big(block, offset);
            var nameLength = block[offset + 8];
            var fixedSize = 8 + 1 + nameLength + (_superBlock.HasFType ? 1 : 0) + 2;
            var entrySize = Align(fixedSize, 8);
            if (inode == 0 || nameLength == 0 || nameLength > 255 || offset + entrySize > eof)
            {
                yield break;
            }

            var tag = ReadUInt16Big(block, offset + entrySize - 2);
            if (tag != offset)
            {
                yield break;
            }

            var name = DecodeName(block, offset + 9, nameLength);
            var ftype = _superBlock.HasFType ? block[offset + 9 + nameLength] : (byte)0;
            if (IsValidName(name))
            {
                yield return new XfsDirectoryEntry(name, inode, ftype);
            }

            offset += entrySize;
        }
    }

    private IReadOnlyList<XfsExtent> GetExtents(XfsInode inode)
    {
        if (_extentCache.TryGetValue(inode.Number, out var cached))
        {
            return cached;
        }

        var extents = inode.Format switch
        {
            2 => ReadInlineExtents(inode.Number, inode.DataFork, inode.ExtentCount),
            3 => ReadBtreeExtents(inode.DataFork),
            _ => Array.Empty<XfsExtent>()
        };
        _extentCache[inode.Number] = extents;
        return extents;
    }

    private IReadOnlyList<XfsExtent> ReadInlineExtents(ulong inodeNumber, byte[] dataFork, ulong extentCount)
    {
        if (extentCount > (ulong)(dataFork.Length / 16))
        {
            throw new InvalidDataException($"XFS inode {inodeNumber} contains more inline extents than its data fork can hold.");
        }

        var result = new List<XfsExtent>(checked((int)Math.Min(extentCount, (ulong)(dataFork.Length / 16))));
        var offset = 0;
        for (ulong i = 0; i < extentCount && offset + 16 <= dataFork.Length; i++, offset += 16)
        {
            result.Add(ReadExtent(dataFork, offset));
        }

        return result;
    }

    private IReadOnlyList<XfsExtent> ReadBtreeExtents(byte[] dataFork)
    {
        if (dataFork.Length < 4)
        {
            return Array.Empty<XfsExtent>();
        }

        var level = ReadUInt16Big(dataFork, 0);
        var records = ReadUInt16Big(dataFork, 2);
        if (records == 0)
        {
            return Array.Empty<XfsExtent>();
        }

        var maxRecords = Math.Max(0, (dataFork.Length - 4) / 16);
        if (records > maxRecords)
        {
            throw new InvalidDataException("XFS extent B+tree root has more records than its data fork can hold.");
        }

        if (level == 0)
        {
            var inlineExtents = new List<XfsExtent>(records);
            for (var index = 0; index < records; index++)
            {
                inlineExtents.Add(ReadExtent(dataFork, 4 + index * 16));
            }

            return inlineExtents;
        }

        var pointerOffset = 4 + maxRecords * 8;
        var result = new List<XfsExtent>();
        for (var i = 0; i < records; i++)
        {
            var offset = pointerOffset + i * 8;
            if (offset + 8 > dataFork.Length)
            {
                break;
            }

            var pointer = EndianUtilities.ReadUInt64Big(dataFork, offset);
            DiagnosticLog.Write($"XFS B+tree root child: index={i}, rootLevel={level}, rootRecords={records}, maxRecords={maxRecords}, pointerOffset={offset}, fsBlock={pointer}, partitionOffset={ExtentToDiskOffset(pointer)}");
            ReadBtreeBlock(pointer, level, result);
        }

        return result;
    }

    private void ReadBtreeBlock(ulong fileSystemBlock, ushort parentLevel, List<XfsExtent> result)
    {
        var block = ReadBytesAtFileSystemBlock(fileSystemBlock, checked((int)_superBlock.BlockSize));
        var magic = EndianUtilities.ReadUInt32Big(block, 0);
        if (magic != (_superBlock.SbVersion == 5 ? BmapMagicV5 : BmapMagic))
        {
            throw new InvalidDataException(
                $"Invalid XFS extent B+tree magic: fsBlock={fileSystemBlock}, partitionOffset={ExtentToDiskOffset(fileSystemBlock)}, actual=0x{magic:X8}, expected=0x{(_superBlock.SbVersion == 5 ? BmapMagicV5 : BmapMagic):X8}, parentLevel={parentLevel}, firstBytes={Convert.ToHexString(block.AsSpan(0, Math.Min(32, block.Length)))}.");
        }

        var level = ReadUInt16Big(block, 4);
        var records = ReadUInt16Big(block, 6);
        var headerSize = _superBlock.SbVersion == 5 ? 0x48 : 0x18;
        var maxRecords = Math.Max(0, (block.Length - headerSize) / 16);
        if (records > maxRecords)
        {
            throw new InvalidDataException("XFS extent B+tree block has more records than it can hold.");
        }
        if (level == 0)
        {
            var offset = headerSize;
            for (var i = 0; i < records && offset + 16 <= block.Length; i++, offset += 16)
            {
                result.Add(ReadExtent(block, offset));
            }

            return;
        }

        if (parentLevel > 0 && level >= parentLevel)
        {
            throw new InvalidDataException("Invalid XFS extent B+tree level.");
        }

        var pointerOffset = headerSize + maxRecords * 8;
        for (var i = 0; i < records; i++)
        {
            var offset = pointerOffset + i * 8;
            if (offset + 8 > block.Length)
            {
                break;
            }

            var pointer = EndianUtilities.ReadUInt64Big(block, offset);
            DiagnosticLog.Write($"XFS B+tree internal child: parentFsBlock={fileSystemBlock}, index={i}, level={level}, records={records}, pointerOffset={offset}, fsBlock={pointer}, partitionOffset={ExtentToDiskOffset(pointer)}");
            ReadBtreeBlock(pointer, level, result);
        }
    }

    internal static XfsExtent ReadExtent(byte[] buffer, int offset)
    {
        var high = EndianUtilities.ReadUInt64Big(buffer, offset);
        var low = EndianUtilities.ReadUInt64Big(buffer, offset + 8);
        return new XfsExtent(
            (uint)(low & 0x001fffff),
            ((high & 0x1ff) << 43) | (low >> 21),
            (high >> 9) & 0x003fffffffffffff,
            (high & (1UL << 63)) != 0);
    }

    private byte[] ReadContent(XfsInode inode, long offset, int count)
    {
        if (inode.Format == 1)
        {
            if (offset >= inode.DataFork.Length)
            {
                return Array.Empty<byte>();
            }

            var available = Math.Min(count, inode.DataFork.Length - (int)offset);
            var localResult = new byte[available];
            Array.Copy(inode.DataFork, (int)offset, localResult, 0, available);
            return localResult;
        }

        var extents = GetExtents(inode).OrderBy(extent => extent.StartOffset).ToList();
        ValidateExtents(inode, extents);
        var result = new byte[count];
        for (var extentIndex = 0; extentIndex < extents.Count; extentIndex++)
        {
            var extent = extents[extentIndex];
            try
            {
                var extentStart = checked((long)checked(extent.StartOffset * (ulong)_superBlock.BlockSize));
                var extentLength = GetExtentByteLength(extent.BlockCount, _superBlock.BlockSize);
                var extentEnd = checked(extentStart + extentLength);
                var readStart = Math.Max(offset, extentStart);
                var readEnd = Math.Min(checked(offset + count), extentEnd);
                if (readEnd <= readStart || extent.IsUnwritten)
                {
                    continue;
                }

                var resultOffset = checked((int)(readStart - offset));
                var physicalOffset = checked(ExtentToDiskOffset(extent.StartBlock) + (readStart - extentStart));
                var bytesToRead = checked((int)(readEnd - readStart));
                _reader.ReadAt(physicalOffset, result, resultOffset, bytesToRead);
            }
            catch (Exception ex)
            {
                throw new IOException(
                    $"XFS extent read failed: inode={inode.Number}, index={extentIndex}, logicalBlock={extent.StartOffset}, physicalBlock={extent.StartBlock}, blockCount={extent.BlockCount}.",
                    ex);
            }
        }

        return result;
    }

    internal static long GetExtentByteLength(uint blockCount, uint blockSize) =>
        checked((long)checked((ulong)blockCount * blockSize));

    internal static bool IsExtentWithinAllocationGroup(
        ulong fileSystemBlock,
        uint blockCount,
        uint agBlocks,
        uint agCount,
        ulong dataBlocks,
        byte agBlockLog)
    {
        if (blockCount == 0 || agBlocks == 0 || agCount == 0 || agBlockLog >= 64)
        {
            return false;
        }

        var allocationGroup = fileSystemBlock >> agBlockLog;
        var relativeBlock = fileSystemBlock & ((1UL << agBlockLog) - 1);
        if (allocationGroup >= agCount)
        {
            return false;
        }

        var precedingBlocks = checked((ulong)agBlocks * (agCount - 1));
        if (dataBlocks < precedingBlocks)
        {
            return false;
        }

        var allocationGroupBlocks = allocationGroup == agCount - 1
            ? dataBlocks - precedingBlocks
            : agBlocks;
        return relativeBlock < allocationGroupBlocks
            && blockCount <= allocationGroupBlocks - relativeBlock;
    }

    private void ValidateExtents(XfsInode inode, IReadOnlyList<XfsExtent> extents)
    {
        ulong previousEnd = 0;
        var readableBeyondSuperBlockCount = 0;
        XfsExtent? firstReadableBeyondSuperBlock = null;
        foreach (var extent in extents)
        {
            if (extent.BlockCount == 0)
            {
                throw new InvalidDataException($"XFS inode {inode.Number} contains a zero-length extent.");
            }

            var exceedsSuperBlockDataDevice = extent.StartBlock >= _superBlock.DataBlocks
                || extent.BlockCount > _superBlock.DataBlocks - extent.StartBlock;
            var validAllocationGroupExtent = IsExtentWithinAllocationGroup(
                extent.StartBlock,
                extent.BlockCount,
                _superBlock.AgBlocks,
                _superBlock.AgCount,
                _superBlock.DataBlocks,
                _superBlock.AgBlocksLog2);
            var physicalOffset = ExtentToDiskOffset(extent.StartBlock);
            var physicalLength = GetExtentByteLength(extent.BlockCount, _superBlock.BlockSize);
            var fitsReader = physicalOffset >= 0
                && physicalLength <= _reader.Length - physicalOffset;
            if (!validAllocationGroupExtent || !fitsReader)
            {
                throw new InvalidDataException(
                    $"XFS inode {inode.Number} extent is outside the readable XFS allocation group or device: startBlock={extent.StartBlock}, blockCount={extent.BlockCount}, agBlocks={_superBlock.AgBlocks}, agCount={_superBlock.AgCount}, dataBlocks={_superBlock.DataBlocks}, readerLength={_reader.Length}.");
            }

            if (exceedsSuperBlockDataDevice)
            {
                readableBeyondSuperBlockCount++;
                firstReadableBeyondSuperBlock ??= extent;
            }

            var end = checked(extent.StartOffset + extent.BlockCount);
            if (extent.StartOffset < previousEnd)
            {
                throw new InvalidDataException($"XFS inode {inode.Number} contains overlapping or unsorted extents.");
            }

            previousEnd = end;
        }

        if (firstReadableBeyondSuperBlock is not null
            && _diagnosedOutOfDataDeviceInodes.Add(inode.Number))
        {
            DiagnosticLog.Write($"XFS extents beyond sb_dblocks are readable from the supplied device: inode={inode.Number}, count={readableBeyondSuperBlockCount}, first={firstReadableBeyondSuperBlock}, dataBlocks={_superBlock.DataBlocks}, readerLength={_reader.Length}");
        }

        // Logical gaps, including a hole at EOF, are valid sparse-file extents.
        // ReadContent initializes its result with zeroes, so those gaps are read correctly.
    }

    private void WriteFileDiagnostics(XfsNodeRef file, XfsInode inode)
    {
        if (!_diagnosedInodes.Add(inode.Number))
        {
            return;
        }

        if (inode.Format is not (2 or 3))
        {
            DiagnosticLog.Write($"XFS raw read: path={file.Path}, inode={inode.Number}, format={inode.Format}, size={inode.Length}, extents=0, blockSize={_superBlock.BlockSize}");
            return;
        }

        var extents = GetExtents(inode).OrderBy(extent => extent.StartOffset).ToList();
        var first = extents.FirstOrDefault();
        var last = extents.LastOrDefault();
        DiagnosticLog.Write($"XFS raw read: path={file.Path}, inode={inode.Number}, format={inode.Format}, size={inode.Length}, extents={extents.Count}, blockSize={_superBlock.BlockSize}, first={first}, last={last}");
    }

    private bool TryCreateNode(ulong inodeNumber, string name, string path, string parentPath, int symlinkDepth, out VfsNode node)
    {
        node = default!;
        XfsInode inode;
        try
        {
            inode = ReadInode(inodeNumber);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentOutOfRangeException)
        {
            return false;
        }

        var resolvedPath = path;
        var kind = GetNodeKind(inode);
        if (kind == XfsRawNodeKind.Symlink && symlinkDepth < MaxSymlinkDepth)
        {
            var target = ReadSymlinkTarget(inode);
            if (TryResolvePath(parentPath, target, symlinkDepth + 1, out var resolved))
            {
                inode = ReadInode(resolved.Inode);
                kind = resolved.Kind;
                resolvedPath = resolved.Path;
            }
        }

        node = new VfsNode
        {
            Name = name,
            VirtualPath = path,
            IsDirectory = kind == XfsRawNodeKind.Directory,
            Size = kind == XfsRawNodeKind.Directory ? 0 : (long)Math.Min(inode.Length, long.MaxValue),
            ModifiedUtc = inode.ModifiedUtc,
            Attributes = (kind == XfsRawNodeKind.Directory ? FileAttributes.Directory : (FileAttributes)0)
                | ((inode.Mode & 0x92) == 0 ? FileAttributes.ReadOnly : (FileAttributes)0),
            Metadata = new XfsNodeRef(resolvedPath, inode.Number, kind)
        };
        return true;
    }

    private bool TryResolvePath(string basePath, string target, int depth, out XfsNodeRef node)
    {
        node = default!;
        if (depth > MaxSymlinkDepth || string.IsNullOrWhiteSpace(target))
        {
            return false;
        }

        var normalized = NormalizePath(basePath, target);
        var parts = SplitPath(normalized);
        var currentInode = RootInode;
        var currentPath = @"\";
        var currentKind = XfsRawNodeKind.Directory;
        foreach (var part in parts)
        {
            var dirInode = ReadInode(currentInode);
            if (!dirInode.IsDirectory)
            {
                return false;
            }

            var entry = ReadDirectoryEntries(dirInode).FirstOrDefault(e => e.Name == part);
            if (entry is null)
            {
                return false;
            }

            var childPath = CombinePath(currentPath, part);
            var childInode = ReadInode(entry.Inode);
            currentKind = GetNodeKind(childInode);
            if (currentKind == XfsRawNodeKind.Symlink)
            {
                var linkTarget = ReadSymlinkTarget(childInode);
                if (TryResolvePath(currentPath, linkTarget, depth + 1, out var resolved))
                {
                    currentInode = resolved.Inode;
                    currentPath = resolved.Path;
                    currentKind = resolved.Kind;
                    continue;
                }
            }

            currentInode = childInode.Number;
            currentPath = childPath;
        }

        node = new XfsNodeRef(currentPath, currentInode, currentKind);
        return true;
    }

    private string ReadSymlinkTarget(XfsInode inode)
    {
        var length = checked((int)Math.Min(inode.Length, 64 * 1024));
        var bytes = ReadContent(inode, 0, length);
        return _fileNameEncoding.GetString(bytes).TrimEnd('\0');
    }

    private byte[] ReadBytesAtFileSystemBlock(ulong fileSystemBlock, int count)
    {
        var result = new byte[count];
        _reader.ReadAt(ExtentToDiskOffset(fileSystemBlock), result, 0, count);
        return result;
    }

    private long ExtentToDiskOffset(ulong fileSystemBlock)
    {
        var allocationGroup = fileSystemBlock >> _superBlock.AgBlocksLog2;
        var relativeBlock = fileSystemBlock & ((1UL << _superBlock.AgBlocksLog2) - 1);
        var diskBlock = checked(checked(allocationGroup * _superBlock.AgBlocks) + relativeBlock);
        return checked((long)checked(diskBlock * _superBlock.BlockSize));
    }

    private bool HasCleanUnmountRecord()
    {
        if (_hasCleanLog is bool cached)
        {
            return cached;
        }

        const uint logHeaderMagic = 0xfeedbabe;
        const int basicBlockSize = 512;
        const int scanBufferSize = 4 * 1024 * 1024;
        if (_superBlock.LogStart == 0 || _superBlock.LogBlocks == 0)
        {
            return (_hasCleanLog = false).Value;
        }

        var logOffset = checked((long)checked(_superBlock.LogStart * _superBlock.BlockSize));
        var logLength = checked((long)checked((ulong)_superBlock.LogBlocks * _superBlock.BlockSize));
        if (logOffset < 0 || logLength < basicBlockSize || logOffset > _reader.Length - logLength)
        {
            return (_hasCleanLog = false).Value;
        }

        if ((logLength % basicBlockSize) != 0 || logLength / basicBlockSize > int.MaxValue)
        {
            return (_hasCleanLog = false).Value;
        }

        var logBasicBlocks = checked((int)(logLength / basicBlockSize));
        var cycles = new uint[logBasicBlocks];
        var headers = new List<XfsLogRecordHeader>();
        var buffer = new byte[scanBufferSize];
        for (long chunkOffset = 0; chunkOffset < logLength; chunkOffset += buffer.Length)
        {
            var count = checked((int)Math.Min(buffer.Length, logLength - chunkOffset));
            _reader.ReadAt(logOffset + chunkOffset, buffer, 0, count);
            for (var offset = 0; offset + basicBlockSize <= count; offset += basicBlockSize)
            {
                var logBlock = checked((int)((chunkOffset + offset) / basicBlockSize));
                var firstWord = EndianUtilities.ReadUInt32Big(buffer, offset);
                cycles[logBlock] = firstWord == logHeaderMagic
                    ? EndianUtilities.ReadUInt32Big(buffer, offset + 4)
                    : firstWord;
                if (firstWord != logHeaderMagic)
                {
                    continue;
                }

                var version = EndianUtilities.ReadUInt32Big(buffer, offset + 8);
                var recordLength = EndianUtilities.ReadUInt32Big(buffer, offset + 12);
                var lsn = EndianUtilities.ReadUInt64Big(buffer, offset + 16);
                if (version is not (1U or 2U)
                    || recordLength == 0
                    || recordLength > 256 * 1024
                    || (recordLength & 7) != 0
                    || (uint)(lsn >> 32) != cycles[logBlock]
                    || (uint)lsn != (uint)logBlock
                    || !buffer.AsSpan(offset + 304, 16).SequenceEqual(_superBlock.Uuid))
                {
                    continue;
                }

                headers.Add(new XfsLogRecordHeader(
                    logBlock,
                    version,
                    recordLength,
                    EndianUtilities.ReadUInt32Big(buffer, offset + 320),
                    EndianUtilities.ReadUInt32Big(buffer, offset + 40)));
            }
        }

        var firstCycle = cycles[0];
        var lastCycle = cycles[^1];
        int head;
        if (firstCycle == lastCycle)
        {
            if (cycles.Any(cycle => cycle != firstCycle))
            {
                return (_hasCleanLog = false).Value;
            }

            head = 0;
        }
        else
        {
            if (firstCycle != unchecked(lastCycle + 1))
            {
                return (_hasCleanLog = false).Value;
            }

            head = Array.FindIndex(cycles, cycle => cycle == lastCycle);
            if (head <= 0
                || cycles.AsSpan(0, head).ContainsAnyExcept(firstCycle)
                || cycles.AsSpan(head).ContainsAnyExcept(lastCycle))
            {
                return (_hasCleanLog = false).Value;
            }
        }

        var latest = head == 0
            ? headers.MaxBy(header => header.Block)
            : headers.Where(header => header.Block < head).MaxBy(header => header.Block);
        if (latest is null)
        {
            return (_hasCleanLog = false).Value;
        }

        if (latest.Version == 2
            && (latest.RecordSize < 32 * 1024
                || latest.RecordSize > 256 * 1024
                || (latest.RecordSize % (32 * 1024)) != 0))
        {
            return (_hasCleanLog = false).Value;
        }

        var headerBlocks = latest.Version == 2 ? latest.RecordSize / (32 * 1024) : 1L;
        if (headerBlocks > 8)
        {
            return (_hasCleanLog = false).Value;
        }

        var dataBlocks = (latest.RecordLength + basicBlockSize - 1L) / basicBlockSize;
        if ((latest.Block + headerBlocks + dataBlocks) % logBasicBlocks != head
            || latest.NumLogOperations != 1)
        {
            return (_hasCleanLog = false).Value;
        }

        var operationBlock = (latest.Block + headerBlocks) % logBasicBlocks;
        var operation = ReadWrappedLog(logOffset, logLength, operationBlock * basicBlockSize, 12);
        var operationLength = EndianUtilities.ReadUInt32Big(operation, 4);
        var client = operation[8];
        var flags = operation[9];
        _hasCleanLog = client == 0xaa && flags == 0x20 && operationLength == 0;
        return _hasCleanLog.Value;
    }

    private byte[] ReadWrappedLog(long logOffset, long logLength, long relativeOffset, int count)
    {
        var result = new byte[count];
        var firstCount = checked((int)Math.Min(count, logLength - relativeOffset));
        _reader.ReadAt(logOffset + relativeOffset, result, 0, firstCount);
        if (firstCount < count)
        {
            _reader.ReadAt(logOffset, result, firstCount, count - firstCount);
        }

        return result;
    }

    private IReadOnlyList<XfsExtent> ValidateFullyAllocated(XfsInode inode)
    {
        var extents = GetExtents(inode)
            .OrderBy(extent => extent.StartOffset)
            .ToArray();
        var requiredBlocks = inode.Length == 0
            ? 0UL
            : checked((inode.Length + _superBlock.BlockSize - 1) / _superBlock.BlockSize);
        ulong nextLogicalBlock = 0;
        foreach (var extent in extents)
        {
            if (extent.BlockCount == 0)
            {
                throw new InvalidDataException("長さ0のXFS extentが含まれています。");
            }

            if (extent.IsUnwritten)
            {
                throw new NotSupportedException("未書き込みextentを含むXFSファイルはまだ置換できません。");
            }

            if (extent.StartOffset != nextLogicalBlock)
            {
                throw new NotSupportedException("スパースXFSファイルはまだ置換できません。");
            }

            var physicalOffset = ExtentToDiskOffset(extent.StartBlock);
            var extentBytes = checked((long)extent.BlockCount * _superBlock.BlockSize);
            if (physicalOffset < 0 || physicalOffset > _reader.Length - extentBytes)
            {
                throw new InvalidDataException("XFS extentがファイルシステム範囲外です。");
            }

            nextLogicalBlock = checked(nextLogicalBlock + extent.BlockCount);
            if (nextLogicalBlock >= requiredBlocks)
            {
                break;
            }
        }

        if (nextLogicalBlock < requiredBlocks)
        {
            throw new NotSupportedException("未割り当て領域を含むXFSファイルはまだ置換できません。");
        }

        return extents;
    }

    private static XfsRawNodeKind GetNodeKind(XfsInode inode)
    {
        return inode.FileType switch
        {
            4 => XfsRawNodeKind.Directory,
            8 => XfsRawNodeKind.RegularFile,
            10 => XfsRawNodeKind.Symlink,
            _ => XfsRawNodeKind.Other
        };
    }

    private string DecodeName(byte[] buffer, int offset, int length)
    {
        return _fileNameEncoding.GetString(buffer, offset, length);
    }

    private static string NormalizePath(string basePath, string target)
    {
        var parts = target.StartsWith('/') || target.StartsWith('\\')
            ? new List<string>()
            : SplitPath(basePath).ToList();

        foreach (var part in target.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (parts.Count > 0)
                {
                    parts.RemoveAt(parts.Count - 1);
                }

                continue;
            }

            parts.Add(part);
        }

        return parts.Count == 0 ? @"\" : @"\" + string.Join('\\', parts);
    }

    private static IReadOnlyList<string> SplitPath(string path)
    {
        return path.Replace('/', '\\')
            .Split('\\', StringSplitOptions.RemoveEmptyEntries);
    }

    private static string CombinePath(string parent, string name)
    {
        return parent == @"\" ? @"\" + name : parent.TrimEnd('\\') + @"\" + name;
    }

    private static bool IsValidName(string name)
    {
        return name.Length > 0
            && !name.Contains('\0')
            && !name.Contains('/')
            && !name.Contains('\\');
    }

    private static int Align(int value, int alignment)
    {
        var remainder = value % alignment;
        return remainder == 0 ? value : value + alignment - remainder;
    }

    private static ushort ReadUInt16Big(byte[] buffer, int offset)
    {
        return (ushort)((buffer[offset] << 8) | buffer[offset + 1]);
    }

    private static string CultureInvariant(FormattableString value)
    {
        return value.ToString(CultureInfo.InvariantCulture);
    }

    private static uint ReadUInt32Big(byte[] buffer, int offset)
    {
        return EndianUtilities.ReadUInt32Big(buffer, offset);
    }

    private sealed record XfsSuperBlock(
        uint BlockSize,
        ulong DataBlocks,
        ulong RealtimeBlocks,
        ulong RootInode,
        uint AgBlocks,
        uint AgCount,
        ushort SbVersion,
        ushort InodeSize,
        ushort InodesPerBlock,
        byte BlockSizeLog2,
        byte InodeSizeLog2,
        byte InodesPerBlockLog2,
        byte AgBlocksLog2,
        byte DirBlockLog2,
        ulong RelativeInodeMask,
        uint DirectoryBlockSize,
        bool HasFType,
        bool HasBigTime,
        bool HasLargeExtentCounts,
        bool InProgress,
        ulong LogStart,
        uint LogBlocks,
        byte[] Uuid,
        ushort SectorSize,
        uint CompatibleFeatures,
        uint ReadOnlyCompatibleFeatures,
        uint IncompatibleFeatures,
        uint LogIncompatibleFeatures,
        ushort QuotaFlags);

    private sealed record XfsInode(
        ulong Number,
        ulong RelativeNumber,
        ushort Mode,
        byte Format,
        uint LinkCount,
        DateTime? ModifiedUtc,
        ulong Length,
        ulong BlockCount,
        ulong ExtentCount,
        ushort Flags,
        ulong Flags2,
        byte ForkOffset,
        byte[] DataFork,
        byte Version,
        long DiskOffset,
        byte[] RawData)
    {
        public int FileType => (Mode >> 12) & 0x0f;
        public bool IsDirectory => FileType == 4;
        public bool IsSymlink => FileType == 10;
    }

    private sealed record XfsDirectoryEntry(string Name, ulong Inode, byte FileType);

    private sealed record XfsLogRecordHeader(
        int Block,
        uint Version,
        uint RecordLength,
        uint RecordSize,
        uint NumLogOperations);

    internal sealed record XfsExtent(uint BlockCount, ulong StartBlock, ulong StartOffset, bool IsUnwritten);
}

internal sealed record XfsNodeRef(string Path, ulong Inode, XfsRawNodeKind Kind);

internal enum XfsRawNodeKind
{
    Other,
    RegularFile,
    Directory,
    Symlink
}
