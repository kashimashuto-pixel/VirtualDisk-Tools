using System.Text;
using Qcow2Explorer.Core;
using Qcow2Explorer.Partitions;

namespace Qcow2Explorer.FileSystems;

public sealed class FatFileSystem : IReadOnlyFileSystem
{
    private readonly IBlockReader _reader;
    private readonly int _bytesPerSector;
    private readonly int _sectorsPerCluster;
    private readonly int _reservedSectors;
    private readonly int _fatCount;
    private readonly int _rootEntryCount;
    private readonly uint _totalSectors;
    private readonly uint _fatSizeSectors;
    private readonly uint _rootDirSectors;
    private readonly uint _firstDataSector;
    private readonly uint _rootCluster;
    private readonly int _fatBits;

    public FatFileSystem(IBlockReader reader, PartitionInfo partition)
    {
        _reader = reader;
        Partition = partition;
        var boot = EndianUtilities.ReadBytes(reader, 0, 512);

        _bytesPerSector = EndianUtilities.ReadUInt16Little(boot, 11);
        _sectorsPerCluster = boot[13];
        _reservedSectors = EndianUtilities.ReadUInt16Little(boot, 14);
        _fatCount = boot[16];
        _rootEntryCount = EndianUtilities.ReadUInt16Little(boot, 17);
        var total16 = EndianUtilities.ReadUInt16Little(boot, 19);
        _totalSectors = total16 != 0 ? total16 : EndianUtilities.ReadUInt32Little(boot, 32);
        var fat16 = EndianUtilities.ReadUInt16Little(boot, 22);
        _fatSizeSectors = fat16 != 0 ? fat16 : EndianUtilities.ReadUInt32Little(boot, 36);
        _rootDirSectors = (uint)((_rootEntryCount * 32 + _bytesPerSector - 1) / _bytesPerSector);
        _firstDataSector = (uint)(_reservedSectors + _fatCount * _fatSizeSectors + _rootDirSectors);
        _rootCluster = EndianUtilities.ReadUInt32Little(boot, 44);

        var dataSectors = _totalSectors - _firstDataSector;
        var clusterCount = dataSectors / (uint)_sectorsPerCluster;
        _fatBits = clusterCount < 4085 ? 12 : clusterCount < 65525 ? 16 : 32;
        if (_fatBits == 12)
        {
            throw new NotSupportedException("FAT12 は検出のみ対応です。");
        }

        Root = new VfsNode
        {
            Name = "",
            IsDirectory = true,
            Metadata = new FatNodeMeta(_fatBits == 32 ? Math.Max(2U, _rootCluster) : 0)
        };
        Name = _fatBits == 32 ? "FAT32" : "FAT16";
    }

    public string Name { get; }
    public PartitionInfo Partition { get; }
    public VfsNode Root { get; }

    public IReadOnlyList<VfsNode> ListDirectory(VfsNode directory)
    {
        if (!directory.IsDirectory)
        {
            return Array.Empty<VfsNode>();
        }

        var meta = (FatNodeMeta)directory.Metadata!;
        byte[] data;
        if (_fatBits == 16 && meta.FirstCluster == 0)
        {
            var offset = (long)(_reservedSectors + _fatCount * _fatSizeSectors) * _bytesPerSector;
            data = EndianUtilities.ReadBytes(_reader, offset, checked((int)(_rootDirSectors * _bytesPerSector)));
        }
        else
        {
            data = ReadClusterChain(meta.FirstCluster, null);
        }

        return ParseDirectory(data);
    }

    public byte[] ReadFile(VfsNode file, long offset, int count)
    {
        if (file.IsDirectory)
        {
            return Array.Empty<byte>();
        }

        if (offset >= file.Size || count <= 0)
        {
            return Array.Empty<byte>();
        }

        var available = checked((int)Math.Min(count, file.Size - offset));
        var output = new byte[available];
        var meta = (FatNodeMeta)file.Metadata!;
        var clusterBytes = _bytesPerSector * _sectorsPerCluster;
        var written = 0;
        var skip = offset;

        foreach (var cluster in EnumerateClusterChain(meta.FirstCluster))
        {
            if (skip >= clusterBytes)
            {
                skip -= clusterBytes;
                continue;
            }

            var inCluster = (int)skip;
            var chunk = Math.Min(clusterBytes - inCluster, available - written);
            _reader.ReadAt(ClusterToOffset(cluster) + inCluster, output, written, chunk);
            written += chunk;
            skip = 0;
            if (written == available)
            {
                break;
            }
        }

        return output.Length == written ? output : output[..written];
    }

    private byte[] ReadClusterChain(uint firstCluster, int? maxBytes)
    {
        var clusterBytes = _bytesPerSector * _sectorsPerCluster;
        using var output = new MemoryStream();
        foreach (var cluster in EnumerateClusterChain(firstCluster))
        {
            var buffer = EndianUtilities.ReadBytes(_reader, ClusterToOffset(cluster), clusterBytes);
            var toWrite = maxBytes.HasValue
                ? Math.Min(buffer.Length, maxBytes.Value - (int)output.Length)
                : buffer.Length;
            if (toWrite <= 0)
            {
                break;
            }

            output.Write(buffer, 0, toWrite);
            if (maxBytes.HasValue && output.Length >= maxBytes.Value)
            {
                break;
            }
        }

        return output.ToArray();
    }

    private IEnumerable<uint> EnumerateClusterChain(uint firstCluster)
    {
        if (firstCluster < 2)
        {
            yield break;
        }

        var current = firstCluster;
        var visited = new HashSet<uint>();
        while (current >= 2 && !IsEndOfChain(current) && visited.Add(current))
        {
            yield return current;
            current = ReadFatEntry(current);
        }
    }

    private uint ReadFatEntry(uint cluster)
    {
        var fatOffset = _fatBits == 32 ? cluster * 4 : cluster * 2;
        var offset = (long)_reservedSectors * _bytesPerSector + fatOffset;
        var buffer = EndianUtilities.ReadBytes(_reader, offset, _fatBits == 32 ? 4 : 2);
        return _fatBits == 32
            ? EndianUtilities.ReadUInt32Little(buffer, 0) & 0x0fffffff
            : EndianUtilities.ReadUInt16Little(buffer, 0);
    }

    private bool IsEndOfChain(uint value)
    {
        return _fatBits == 32 ? value >= 0x0ffffff8 : value >= 0xfff8;
    }

    private long ClusterToOffset(uint cluster)
    {
        var sector = _firstDataSector + (cluster - 2) * (uint)_sectorsPerCluster;
        return checked((long)sector * _bytesPerSector);
    }

    private IReadOnlyList<VfsNode> ParseDirectory(byte[] data)
    {
        var nodes = new List<VfsNode>();
        var lfnParts = new List<string>();

        for (var offset = 0; offset + 32 <= data.Length; offset += 32)
        {
            var first = data[offset];
            if (first == 0x00)
            {
                break;
            }

            if (first == 0xe5)
            {
                lfnParts.Clear();
                continue;
            }

            var attr = data[offset + 11];
            if (attr == 0x0f)
            {
                lfnParts.Insert(0, ReadLongFileNamePart(data, offset));
                continue;
            }

            if ((attr & 0x08) != 0)
            {
                lfnParts.Clear();
                continue;
            }

            var shortName = ReadShortName(data, offset);
            var name = lfnParts.Count > 0 ? string.Concat(lfnParts).TrimEnd('\0', '\uffff') : shortName;
            lfnParts.Clear();
            if (name is "." or ".." || string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var high = EndianUtilities.ReadUInt16Little(data, offset + 20);
            var low = EndianUtilities.ReadUInt16Little(data, offset + 26);
            var cluster = (uint)((high << 16) | low);
            var size = EndianUtilities.ReadUInt32Little(data, offset + 28);
            var isDirectory = (attr & 0x10) != 0;

            nodes.Add(new VfsNode
            {
                Name = name,
                IsDirectory = isDirectory,
                Size = isDirectory ? 0 : size,
                ModifiedUtc = ReadFatDateTime(data, offset + 22, offset + 24),
                Attributes = ToFileAttributes(attr),
                Metadata = new FatNodeMeta(cluster)
            });
        }

        return nodes
            .OrderByDescending(n => n.IsDirectory)
            .ThenBy(n => n.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static string ReadLongFileNamePart(byte[] data, int offset)
    {
        Span<char> chars = stackalloc char[13];
        var index = 0;
        ReadChars(data, offset + 1, 5, chars, ref index);
        ReadChars(data, offset + 14, 6, chars, ref index);
        ReadChars(data, offset + 28, 2, chars, ref index);
        return new string(chars[..index]);

        static void ReadChars(byte[] data, int offset, int count, Span<char> chars, ref int index)
        {
            for (var i = 0; i < count; i++)
            {
                var ch = EndianUtilities.ReadUInt16Little(data, offset + i * 2);
                if (ch == 0 || ch == 0xffff)
                {
                    return;
                }

                chars[index++] = (char)ch;
            }
        }
    }

    private static string ReadShortName(byte[] data, int offset)
    {
        var name = Encoding.ASCII.GetString(data, offset, 8).TrimEnd();
        var ext = Encoding.ASCII.GetString(data, offset + 8, 3).TrimEnd();
        if (string.IsNullOrEmpty(ext))
        {
            return name;
        }

        return $"{name}.{ext}";
    }

    private static DateTime? ReadFatDateTime(byte[] data, int timeOffset, int dateOffset)
    {
        var time = EndianUtilities.ReadUInt16Little(data, timeOffset);
        var date = EndianUtilities.ReadUInt16Little(data, dateOffset);
        if (date == 0)
        {
            return null;
        }

        var year = 1980 + ((date >> 9) & 0x7f);
        var month = (date >> 5) & 0x0f;
        var day = date & 0x1f;
        var hour = (time >> 11) & 0x1f;
        var minute = (time >> 5) & 0x3f;
        var second = (time & 0x1f) * 2;
        try
        {
            return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Local).ToUniversalTime();
        }
        catch
        {
            return null;
        }
    }

    private static FileAttributes ToFileAttributes(byte attributes)
    {
        var result = (FileAttributes)0;
        if ((attributes & 0x01) != 0) result |= FileAttributes.ReadOnly;
        if ((attributes & 0x02) != 0) result |= FileAttributes.Hidden;
        if ((attributes & 0x04) != 0) result |= FileAttributes.System;
        if ((attributes & 0x10) != 0) result |= FileAttributes.Directory;
        if ((attributes & 0x20) != 0) result |= FileAttributes.Archive;
        return result;
    }

    private sealed record FatNodeMeta(uint FirstCluster);
}
