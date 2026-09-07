using Qcow2Explorer.Core;
using Qcow2Explorer.Partitions;
using DiscXfsFileSystem = DiscUtils.Xfs.XfsFileSystem;
using System.Diagnostics;

namespace Qcow2Explorer.FileSystems;

public sealed class XfsFileSystem : IReadOnlyFileSystem, IFileContentWriter, IDisposable
{
    /// <summary>Set to false to compare DiscUtils.Xfs behavior without the raw reader.</summary>
    public static bool UseRawXfsReader { get; set; } = true;

    private readonly BlockReaderStream _stream;
    private readonly DiscXfsFileSystem _reader;
    private readonly XfsRawFileSystem? _rawReader;

    public XfsFileSystem(IBlockReader reader, PartitionInfo partition)
    {
        Partition = partition;
        _stream = new BlockReaderStream(reader);
        _reader = new DiscXfsFileSystem(_stream);
        _rawReader = UseRawXfsReader ? XfsRawFileSystem.TryOpen(reader) : null;
        DiagnosticLog.Write($"XFS reader initialized: rawReaderEnabled={UseRawXfsReader}, rawReaderAvailable={_rawReader is not null}, partition={partition}");
        Root = new VfsNode
        {
            Name = "",
            IsDirectory = true,
            Metadata = _rawReader is null ? @"\" : _rawReader.RootRef
        };
    }

    public string Name => "XFS";
    public PartitionInfo Partition { get; }
    public VfsNode Root { get; }

    public IReadOnlyList<VfsNode> ListDirectory(VfsNode directory)
    {
        if (_rawReader is not null && directory.Metadata is XfsNodeRef nodeRef)
        {
            try
            {
                return _rawReader.ListDirectory(nodeRef);
            }
            catch (Exception ex)
            {
                WriteRawReadFailure(directory, nodeRef, 0, 0, ex);
            }
        }

        if (!directory.IsDirectory || !TryGetPath(directory.Metadata, out var path))
        {
            return Array.Empty<VfsNode>();
        }

        return _reader.GetFileSystemEntries(path)
            .Select(ToNodeSafe)
            .OrderByDescending(n => n.IsDirectory)
            .ThenBy(n => n.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public byte[] ReadFile(VfsNode file, long offset, int count)
    {
        if (_rawReader is not null && file.Metadata is XfsNodeRef nodeRef)
        {
            try
            {
                return _rawReader.ReadFile(nodeRef, offset, count);
            }
            catch (Exception ex)
            {
                WriteRawReadFailure(file, nodeRef, offset, count, ex);
            }
        }

        if (file.IsDirectory || !TryGetPath(file.Metadata, out var path) || offset >= file.Size || count <= 0)
        {
            return Array.Empty<byte>();
        }

        var available = checked((int)Math.Min(count, file.Size - offset));
        DiagnosticLog.Write($"XFS DiscUtils read: file={file.Name}, path={path}, offset={offset}, count={available}");
        var buffer = new byte[available];
        try
        {
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
        catch (Exception ex)
        {
            DiagnosticLog.Write($"XFS DiscUtils read failed: file={file.Name}, path={path}, offset={offset}, count={available}, error={ex}");
            throw;
        }
    }

    public string DescribeNode(VfsNode node)
    {
        if (_rawReader is not null && node.Metadata is XfsNodeRef nodeRef)
        {
            return _rawReader.Describe(nodeRef);
        }

        return node.Metadata?.ToString() ?? "";
    }

    public bool CanReplaceFile(VfsNode file, long replacementLength, out string reason)
    {
        if (_rawReader is null || file.Metadata is not XfsNodeRef nodeRef)
        {
            reason = "このXFSレイアウトは書き込み解析に対応していません。";
            return false;
        }

        return _rawReader.CanReplaceFile(nodeRef, replacementLength, out reason);
    }

    public void ReplaceFileContent(
        VfsNode file,
        Stream replacement,
        long replacementLength,
        CancellationToken cancellationToken = default)
    {
        if (_rawReader is null || file.Metadata is not XfsNodeRef nodeRef)
        {
            throw new NotSupportedException("このXFSレイアウトは書き込み解析に対応していません。");
        }

        _rawReader.ReplaceFileContent(nodeRef, replacement, replacementLength, cancellationToken);
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
            Size = isDirectory ? 0 : TryGetFileLength(path),
            ModifiedUtc = TryGetLastWriteTimeUtc(path),
            Attributes = attributes,
            Metadata = NormalizePath(path)
        };
    }

    private VfsNode ToNodeSafe(string path)
    {
        try
        {
            return ToNode(path);
        }
        catch
        {
            return new VfsNode
            {
                Name = GetDisplayName(path),
                IsDirectory = false,
                Size = 0,
                Metadata = NormalizePath(path)
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

    private static bool TryGetPath(object? metadata, out string path)
    {
        path = metadata switch
        {
            string value => value,
            XfsNodeRef nodeRef => nodeRef.Path,
            _ => ""
        };
        return !string.IsNullOrWhiteSpace(path);
    }

    private static void WriteRawReadFailure(VfsNode file, XfsNodeRef nodeRef, long offset, int count, Exception exception)
    {
        var message = $"XFS raw read failed; falling back to DiscUtils: file={file.Name}, inode={nodeRef.Inode}, offset={offset}, count={count}, error={exception}";
        DiagnosticLog.Write(message);
    }
}
