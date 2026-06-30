using System.Text;
using System.Globalization;
using Qcow2Explorer.Core;

namespace Qcow2Explorer.FileSystems;

internal sealed class XfsRawFileSystem
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
    private readonly XfsSuperBlock _superBlock;
    private readonly Dictionary<ulong, XfsInode> _inodeCache = new();
    private readonly Dictionary<ulong, IReadOnlyList<XfsDirectoryEntry>> _directoryCache = new();
    private readonly Encoding _fileNameEncoding;

    private XfsRawFileSystem(IBlockReader reader)
    {
        _reader = reader;
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
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentOutOfRangeException or NotSupportedException)
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

        var maxAvailable = Math.Min(count, inode.Length > long.MaxValue ? long.MaxValue : (long)inode.Length - offset);
        if (maxAvailable <= 0)
        {
            return Array.Empty<byte>();
        }

        return ReadContent(inode, offset, checked((int)maxAvailable));
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
        var incompatibleFeatures = sbVersion >= 5 ? EndianUtilities.ReadUInt32Big(buffer, 0xd8) : 0;
        var agOffsetBits = agBlocksLog2 + inodesPerBlockLog2;
        if (agOffsetBits <= 0 || agOffsetBits >= 63)
        {
            throw new NotSupportedException("Unsupported XFS inode geometry.");
        }

        return new XfsSuperBlock(
            blockSize,
            EndianUtilities.ReadUInt64Big(buffer, 0x08),
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
            sbVersion == 5 && (incompatibleFeatures & 0x20) != 0);
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
        var inodeOffset = checked((long)(allocationGroup * _superBlock.AgBlocks * _superBlock.BlockSize
            + agBlock * _superBlock.BlockSize
            + blockOffset * _superBlock.InodeSize));

        var buffer = EndianUtilities.ReadBytes(_reader, inodeOffset, _superBlock.InodeSize);
        if (ReadUInt16Big(buffer, 0) != InodeMagic)
        {
            throw new InvalidDataException($"Invalid XFS inode magic at inode {number}.");
        }

        var version = buffer[0x04];
        var dataForkOffset = version < 3 ? 0x64 : 0xb0;
        var forkOffset = buffer[0x52];
        var dataForkLength = forkOffset == 0
            ? buffer.Length - dataForkOffset
            : Math.Max(0, Math.Min(buffer.Length - dataForkOffset, forkOffset * 8));
        var dataFork = new byte[dataForkLength];
        Array.Copy(buffer, dataForkOffset, dataFork, 0, dataFork.Length);

        var inode = new XfsInode(
            number,
            relative,
            ReadUInt16Big(buffer, 0x02),
            buffer[0x05],
            ReadUInt32Big(buffer, 0x10),
            ReadTimestamp(buffer, 0x28),
            EndianUtilities.ReadUInt64Big(buffer, 0x38),
            EndianUtilities.ReadUInt64Big(buffer, 0x40),
            _superBlock.HasLargeExtentCounts
                ? EndianUtilities.ReadUInt64Big(buffer, 0x18)
                : EndianUtilities.ReadUInt32Big(buffer, 0x4c),
            forkOffset,
            dataFork);
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
        return inode.Format switch
        {
            2 => ReadInlineExtents(inode.DataFork, inode.ExtentCount),
            3 => ReadBtreeExtents(inode.DataFork),
            _ => Array.Empty<XfsExtent>()
        };
    }

    private IReadOnlyList<XfsExtent> ReadInlineExtents(byte[] dataFork, ulong extentCount)
    {
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
            throw new InvalidDataException("Invalid XFS extent B+tree magic.");
        }

        var level = ReadUInt16Big(block, 4);
        var records = ReadUInt16Big(block, 6);
        var headerSize = _superBlock.SbVersion == 5 ? 0x48 : 0x18;
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

        var maxRecords = Math.Max(0, (block.Length - headerSize) / 16);
        var pointerOffset = headerSize + maxRecords * 8;
        for (var i = 0; i < records; i++)
        {
            var offset = pointerOffset + i * 8;
            if (offset + 8 > block.Length)
            {
                break;
            }

            ReadBtreeBlock(EndianUtilities.ReadUInt64Big(block, offset), level, result);
        }
    }

    private XfsExtent ReadExtent(byte[] buffer, int offset)
    {
        var lower = EndianUtilities.ReadUInt64Big(buffer, offset + 8);
        var middle = ReadUInt64BigFromOffset(buffer, offset + 6);
        var upper = EndianUtilities.ReadUInt64Big(buffer, offset);
        return new XfsExtent(
            (uint)(lower & 0x001fffff),
            (middle >> 5) & 0x000fffffffffffff,
            (upper >> 9) & 0x003fffffffffffff);
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

        var result = new byte[count];
        foreach (var extent in GetExtents(inode))
        {
            var extentStart = checked((long)(extent.StartOffset * _superBlock.BlockSize));
            var extentLength = checked((long)(extent.BlockCount * _superBlock.BlockSize));
            var extentEnd = extentStart + extentLength;
            var readStart = Math.Max(offset, extentStart);
            var readEnd = Math.Min(offset + count, extentEnd);
            if (readEnd <= readStart)
            {
                continue;
            }

            var resultOffset = checked((int)(readStart - offset));
            var physicalOffset = ExtentToDiskOffset(extent.StartBlock) + (readStart - extentStart);
            var bytesToRead = checked((int)(readEnd - readStart));
            _reader.ReadAt(physicalOffset, result, resultOffset, bytesToRead);
        }

        return result;
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
            IsDirectory = kind == XfsRawNodeKind.Directory,
            Size = kind == XfsRawNodeKind.Directory ? 0 : (long)Math.Min(inode.Length, long.MaxValue),
            ModifiedUtc = inode.ModifiedUtc,
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
        return checked((long)((allocationGroup * _superBlock.AgBlocks + relativeBlock) * _superBlock.BlockSize));
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

    private static DateTime? ReadTimestamp(byte[] buffer, int offset)
    {
        var seconds = EndianUtilities.ReadUInt32Big(buffer, offset);
        var nanoseconds = EndianUtilities.ReadUInt32Big(buffer, offset + 4);
        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds).AddTicks(nanoseconds / 100).UtcDateTime;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
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

    private static ulong ReadUInt64BigFromOffset(byte[] buffer, int offset)
    {
        return ((ulong)buffer[offset] << 56)
            | ((ulong)buffer[offset + 1] << 48)
            | ((ulong)buffer[offset + 2] << 40)
            | ((ulong)buffer[offset + 3] << 32)
            | ((ulong)buffer[offset + 4] << 24)
            | ((ulong)buffer[offset + 5] << 16)
            | ((ulong)buffer[offset + 6] << 8)
            | buffer[offset + 7];
    }

    private sealed record XfsSuperBlock(
        uint BlockSize,
        ulong DataBlocks,
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
        bool HasLargeExtentCounts);

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
        byte ForkOffset,
        byte[] DataFork)
    {
        public int FileType => (Mode >> 12) & 0x0f;
        public bool IsDirectory => FileType == 4;
        public bool IsSymlink => FileType == 10;
    }

    private sealed record XfsDirectoryEntry(string Name, ulong Inode, byte FileType);

    private sealed record XfsExtent(uint BlockCount, ulong StartBlock, ulong StartOffset);
}

internal sealed record XfsNodeRef(string Path, ulong Inode, XfsRawNodeKind Kind);

internal enum XfsRawNodeKind
{
    Other,
    RegularFile,
    Directory,
    Symlink
}
