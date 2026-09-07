using DiscUtils;
using Qcow2Explorer.Core;
using Qcow2Explorer.Partitions;

namespace Qcow2Explorer.FileSystems;

public sealed class DiscUtilsFileSystem : IReadOnlyFileSystem, IFileContentWriter, IDisposable
{
    private readonly BlockReaderStream _stream;
    private readonly DiscFileSystem _reader;
    private readonly bool _canWrite;
    private readonly ushort _exFatVolumeFlags;

    public DiscUtilsFileSystem(IBlockReader reader, PartitionInfo partition, Func<Stream, DiscFileSystem> openFileSystem, string name)
    {
        Partition = partition;
        Name = name;
        _stream = new BlockReaderStream(reader);
        _canWrite = _stream.CanWrite;
        if (name.Equals("exFAT", StringComparison.OrdinalIgnoreCase))
        {
            var boot = EndianUtilities.ReadBytes(reader, 0, 512);
            if (System.Text.Encoding.ASCII.GetString(boot, 3, 8) != "EXFAT   ")
            {
                throw new InvalidDataException("exFAT boot sectorではありません。");
            }

            _exFatVolumeFlags = EndianUtilities.ReadUInt16Little(boot, 106);
        }
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

    public bool CanReplaceFile(VfsNode file, long replacementLength, out string reason)
    {
        if (!Name.Equals("exFAT", StringComparison.OrdinalIgnoreCase))
        {
            reason = $"{Name}のDiscUtils書き込み経路は有効にしていません。";
            return false;
        }

        if (!_canWrite)
        {
            reason = "変更を保持する書き込みオーバーレイがありません。";
            return false;
        }

        if (file.IsDirectory || file.Metadata is not string path)
        {
            reason = "通常ファイルだけを置換できます。";
            return false;
        }

        if (replacementLength < 0 || replacementLength != file.Size)
        {
            reason = $"現在は元ファイルと同じサイズ（{file.Size:N0} bytes）の置換だけに対応しています。";
            return false;
        }

        if ((_exFatVolumeFlags & 0x0002) != 0)
        {
            reason = "dirty状態のexFAT volumeは書き込めません。先に標準ツールで検査してください。";
            return false;
        }

        if ((_exFatVolumeFlags & 0x0004) != 0)
        {
            reason = "media failure状態が記録されたexFAT volumeは書き込めません。";
            return false;
        }

        try
        {
            if (!_reader.FileExists(path) || _reader.GetFileLength(path) != replacementLength)
            {
                reason = "exFAT上の置換対象またはサイズを再確認できません。";
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
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

        using var destination = _reader.OpenFile((string)file.Metadata!, FileMode.Open, FileAccess.Write);
        if (!destination.CanWrite || destination.Length != replacementLength)
        {
            throw new InvalidDataException("exFAT file streamのサイズまたは書き込み状態が一致しません。");
        }

        var buffer = new byte[1024 * 1024];
        long remaining = replacementLength;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = checked((int)Math.Min(buffer.Length, remaining));
            replacement.ReadExactly(buffer.AsSpan(0, count));
            destination.Write(buffer, 0, count);
            remaining -= count;
        }

        if (replacement.ReadByte() != -1 || destination.Position != replacementLength)
        {
            throw new InvalidDataException("置換元または置換後exFATファイルのサイズが一致しません。");
        }

        destination.Flush();
        _stream.Flush();
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
