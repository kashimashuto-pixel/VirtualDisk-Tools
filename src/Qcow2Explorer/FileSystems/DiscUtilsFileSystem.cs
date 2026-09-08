using DiscUtils;
using Qcow2Explorer.Core;
using Qcow2Explorer.Partitions;

namespace Qcow2Explorer.FileSystems;

public sealed class DiscUtilsFileSystem : IReadOnlyFileSystem, IFileContentWriter, IFileSystemEditor, IDisposable
{
    private readonly BlockReaderStream _stream;
    private readonly DiscFileSystem _reader;
    private readonly bool _canWrite;
    private readonly ushort _exFatVolumeFlags;
    private readonly Func<(bool IsValid, string Reason)>? _additionalWriteValidator;
    private readonly Action? _afterMutation;

    public DiscUtilsFileSystem(
        IBlockReader reader,
        PartitionInfo partition,
        Func<Stream, DiscFileSystem> openFileSystem,
        string name,
        Func<(bool IsValid, string Reason)>? additionalWriteValidator = null,
        Action? afterMutation = null)
    {
        Partition = partition;
        Name = name;
        _stream = new BlockReaderStream(reader);
        _canWrite = _stream.CanWrite;
        _additionalWriteValidator = additionalWriteValidator;
        _afterMutation = afterMutation;
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
        if (replacementLength < 0 || replacementLength != file.Size)
        {
            reason = $"現在は元ファイルと同じサイズ（{file.Size:N0} bytes）の置換だけに対応しています。";
            return false;
        }

        return CanWriteFile(file, replacementLength, out reason);
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
        FinalizeMutation();
    }

    public bool CanWriteFile(VfsNode file, long contentLength, out string reason)
    {
        if (!TryValidateWriteAccess(out reason))
        {
            return false;
        }

        if (file.IsDirectory || file.Metadata is not string path)
        {
            reason = "通常ファイルだけを編集できます。";
            return false;
        }

        if (contentLength < 0)
        {
            reason = "ファイルサイズが不正です。";
            return false;
        }

        try
        {
            if (!_reader.FileExists(path) || _reader.GetFileLength(path) != file.Size)
            {
                reason = $"{Name}上の編集対象または現在のサイズを再確認できません。";
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

    public bool ValidateFileSystem(out string reason)
    {
        if (!TryValidateWriteAccess(out reason))
        {
            return false;
        }

        try
        {
            _ = _reader.GetFileSystemEntries(@"\").ToArray();
            reason = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
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

        using var destination = _reader.OpenFile((string)file.Metadata!, FileMode.Open, FileAccess.ReadWrite);
        destination.SetLength(contentLength);
        destination.Position = 0;
        CopyExact(content, destination, contentLength, cancellationToken);
        destination.Flush();
        _stream.Flush();
        FinalizeMutation();
    }

    public bool CanCreateFile(VfsNode directory, string name, long contentLength, out string reason)
    {
        if (!TryValidateWriteAccess(out reason))
        {
            return false;
        }

        if (!TryGetNewFilePath(directory, name, out var path, out reason))
        {
            return false;
        }

        if (contentLength < 0)
        {
            reason = "ファイルサイズが不正です。";
            return false;
        }

        try
        {
            if (_reader.FileExists(path) || _reader.DirectoryExists(path))
            {
                reason = $"同名のファイルまたはディレクトリが既に存在します: {name}";
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
            || !TryGetNewFilePath(directory, name, out var path, out reason))
        {
            throw new NotSupportedException(reason);
        }

        using (var destination = _reader.OpenFile(path, FileMode.CreateNew, FileAccess.ReadWrite))
        {
            destination.SetLength(contentLength);
            destination.Position = 0;
            CopyExact(content, destination, contentLength, cancellationToken);
            destination.Flush();
        }

        _stream.Flush();
        FinalizeMutation();
        return ToNodeSafe(path);
    }

    public bool CanDeleteFile(VfsNode directory, VfsNode file, out string reason)
    {
        if (!TryValidateWriteAccess(out reason))
        {
            return false;
        }

        if (!directory.IsDirectory || directory.Metadata is not string directoryPath)
        {
            reason = "削除元ディレクトリを再確認できません。";
            return false;
        }

        if (file.IsDirectory || file.Metadata is not string filePath)
        {
            reason = "最初の実験版では通常ファイルだけを削除できます。";
            return false;
        }

        var expectedParent = NormalizePath(directoryPath).TrimEnd('\\');
        var actualParent = GetParentPath(filePath).TrimEnd('\\');
        if (!string.Equals(expectedParent, actualParent, StringComparison.OrdinalIgnoreCase))
        {
            reason = "削除対象が指定ディレクトリの直下にありません。";
            return false;
        }

        try
        {
            if (!_reader.FileExists(filePath) || _reader.GetFileLength(filePath) != file.Size)
            {
                reason = $"{Name}上の削除対象または現在のサイズを再確認できません。";
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

    public void DeleteFile(VfsNode directory, VfsNode file, CancellationToken cancellationToken = default)
    {
        if (!CanDeleteFile(directory, file, out var reason))
        {
            throw new NotSupportedException(reason);
        }

        cancellationToken.ThrowIfCancellationRequested();
        _reader.DeleteFile((string)file.Metadata!);
        _stream.Flush();
        FinalizeMutation();
    }

    public void Dispose()
    {
        _reader.Dispose();
        _stream.Dispose();
    }

    internal bool TryResolvePath(string path, out VfsNode node)
    {
        path = NormalizePath(path);
        if (path == @"\")
        {
            node = Root;
            return true;
        }

        try
        {
            if (!_reader.FileExists(path) && !_reader.DirectoryExists(path))
            {
                node = null!;
                return false;
            }

            node = ToNodeSafe(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        {
            node = null!;
            return false;
        }
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

    private bool TryValidateWriteAccess(out string reason)
    {
        if (!_canWrite)
        {
            reason = "変更を保持する書き込みオーバーレイがありません。";
            return false;
        }

        if (Name.Equals("exFAT", StringComparison.OrdinalIgnoreCase))
        {
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
        }

        if (_additionalWriteValidator is not null)
        {
            var validation = _additionalWriteValidator();
            if (!validation.IsValid)
            {
                reason = validation.Reason;
                return false;
            }
        }

        reason = string.Empty;
        return true;
    }

    private bool TryGetNewFilePath(
        VfsNode directory,
        string name,
        out string path,
        out string reason)
    {
        path = string.Empty;
        if (!directory.IsDirectory || directory.Metadata is not string directoryPath)
        {
            reason = "追加先ディレクトリを再確認できません。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(name)
            || name is "." or ".."
            || name.Length > 255
            || name.IndexOfAny(['\\', '/', '\0']) >= 0
            || name.Any(char.IsControl))
        {
            reason = "ファイル名が不正です。区切り文字、制御文字、予約名は使用できません。";
            return false;
        }

        path = NormalizePath(directoryPath).TrimEnd('\\') + "\\" + name;
        reason = string.Empty;
        return true;
    }

    private static string GetParentPath(string path)
    {
        path = NormalizePath(path).TrimEnd('\\');
        var index = path.LastIndexOf('\\');
        return index <= 0 ? @"\" : path[..index];
    }

    private static void CopyExact(
        Stream source,
        Stream destination,
        long length,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[1024 * 1024];
        long remaining = length;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = checked((int)Math.Min(buffer.Length, remaining));
            source.ReadExactly(buffer.AsSpan(0, count));
            destination.Write(buffer, 0, count);
            remaining -= count;
        }

        if (source.ReadByte() != -1 || destination.Position != length)
        {
            throw new InvalidDataException("入力または書き込み後ファイルのサイズが指定値と一致しません。");
        }
    }

    private void FinalizeMutation()
    {
        _afterMutation?.Invoke();
        _stream.Flush();
    }
}
