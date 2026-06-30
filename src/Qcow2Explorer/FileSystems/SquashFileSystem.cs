using DiscUtils.SquashFs;
using Qcow2Explorer.Core;
using Qcow2Explorer.Partitions;

namespace Qcow2Explorer.FileSystems;

public sealed class SquashFileSystem : IReadOnlyFileSystem, IDisposable
{
    private readonly BlockReaderStream _stream;
    private readonly SquashFileSystemReader _reader;

    public SquashFileSystem(IBlockReader reader, PartitionInfo partition)
    {
        Partition = partition;
        _stream = new BlockReaderStream(reader);
        _reader = new SquashFileSystemReader(_stream);
        Root = new VfsNode
        {
            Name = "",
            IsDirectory = true,
            Metadata = @"\"
        };
    }

    public string Name => "SquashFS";
    public PartitionInfo Partition { get; }
    public VfsNode Root { get; }

    public IReadOnlyList<VfsNode> ListDirectory(VfsNode directory)
    {
        if (!directory.IsDirectory || directory.Metadata is not string path)
        {
            return Array.Empty<VfsNode>();
        }

        return _reader.GetFileSystemEntries(path)
            .Select(ToNode)
            .OrderByDescending(n => n.IsDirectory)
            .ThenBy(n => n.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public byte[] ReadFile(VfsNode file, long offset, int count)
    {
        if (file.IsDirectory || file.Metadata is not string path || offset >= file.Size || count <= 0)
        {
            return Array.Empty<byte>();
        }

        var available = checked((int)Math.Min(count, file.Size - offset));
        var buffer = new byte[available];
        using var stream = _reader.OpenFile(path, FileMode.Open, FileAccess.Read);
        stream.Position = offset;
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer, total, buffer.Length - total);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total == buffer.Length ? buffer : buffer[..total];
    }

    public void Dispose()
    {
        _reader.Dispose();
        _stream.Dispose();
    }

    private VfsNode ToNode(string path)
    {
        var attributes = _reader.GetAttributes(path);
        var isDirectory = (attributes & FileAttributes.Directory) != 0;
        return new VfsNode
        {
            Name = GetDisplayName(path),
            IsDirectory = isDirectory,
            Size = isDirectory ? 0 : _reader.GetFileLength(path),
            ModifiedUtc = _reader.GetLastWriteTimeUtc(path),
            Metadata = NormalizePath(path)
        };
    }

    private static string GetDisplayName(string path)
    {
        path = NormalizePath(path).TrimEnd('\\');
        var index = path.LastIndexOf('\\');
        return index >= 0 ? path[(index + 1)..] : path;
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return @"\";
        }

        return path.Replace('/', '\\');
    }
}
