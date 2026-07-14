using DiscUtils;
using Qcow2Explorer.Core;
using Qcow2Explorer.Partitions;

namespace Qcow2Explorer.FileSystems;

public sealed class DiscUtilsFileSystem : IReadOnlyFileSystem, IDisposable
{
    private readonly BlockReaderStream _stream;
    private readonly DiscFileSystem _reader;

    public DiscUtilsFileSystem(IBlockReader reader, PartitionInfo partition, Func<Stream, DiscFileSystem> openFileSystem, string name)
    {
        Partition = partition;
        Name = name;
        _stream = new BlockReaderStream(reader);
        try
        {
            _reader = openFileSystem(_stream);
        }
        catch
        {
            _stream.Dispose();
            throw;
        }
        Root = new VfsNode
        {
            Name = "",
            IsDirectory = true,
            Metadata = @"\"
        };
    }

    public string Name { get; }
    public PartitionInfo Partition { get; }
    public VfsNode Root { get; }

    public IReadOnlyList<VfsNode> ListDirectory(VfsNode directory)
    {
        if (!directory.IsDirectory || directory.Metadata is not string path)
        {
            return Array.Empty<VfsNode>();
        }

        try
        {
            return _reader.GetFileSystemEntries(path)
                .Select(ToNodeSafe)
                .Where(node => !string.IsNullOrWhiteSpace(node.Name))
                .OrderByDescending(n => n.IsDirectory)
                .ThenBy(n => n.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch
        {
            return Array.Empty<VfsNode>();
        }
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

    private VfsNode ToNodeSafe(string path)
    {
        try
        {
            var normalized = NormalizePath(path);
            var attributes = _reader.GetAttributes(normalized);
            var isDirectory = (attributes & FileAttributes.Directory) != 0;
            return new VfsNode
            {
                Name = GetDisplayName(normalized),
                IsDirectory = isDirectory,
                Size = isDirectory ? 0 : TryGetFileLength(normalized),
                ModifiedUtc = TryGetLastWriteTimeUtc(normalized),
                Attributes = attributes,
                Metadata = normalized
            };
        }
        catch
        {
            var normalized = NormalizePath(path);
            return new VfsNode
            {
                Name = GetDisplayName(normalized),
                IsDirectory = false,
                Size = 0,
                Metadata = normalized
            };
        }
    }

    private long TryGetFileLength(string path)
    {
        try
        {
            return _reader.GetFileLength(path);
        }
        catch
        {
            return 0;
        }
    }

    private DateTime? TryGetLastWriteTimeUtc(string path)
    {
        try
        {
            return _reader.GetLastWriteTimeUtc(path);
        }
        catch
        {
            return null;
        }
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
