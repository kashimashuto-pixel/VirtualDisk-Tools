using DiscUtils;
using System.Collections;
using System.Reflection;
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
            VirtualPath = @"\",
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
        PrepareDestinationLength(destination, contentLength);
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

        RepairFatDirectoryAllocator((string)directory.Metadata!);
        using (var destination = _reader.OpenFile(path, FileMode.CreateNew, FileAccess.ReadWrite))
        {
            PrepareDestinationLength(destination, contentLength);
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

    public bool CanCreateDirectory(VfsNode directory, string name, out string reason)
    {
        if (!TryValidateWriteAccess(out reason)
            || !TryGetNewFilePath(directory, name, out var path, out reason))
        {
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

    public VfsNode CreateDirectory(
        VfsNode directory,
        string name,
        CancellationToken cancellationToken = default)
    {
        if (!CanCreateDirectory(directory, name, out var reason)
            || !TryGetNewFilePath(directory, name, out var path, out reason))
        {
            throw new NotSupportedException(reason);
        }

        cancellationToken.ThrowIfCancellationRequested();
        RepairFatDirectoryAllocator((string)directory.Metadata!);
        _reader.CreateDirectory(path);
        PadExFatDirectory(path);
        FinalizeMutation();
        return ToNodeSafe(path);
    }

    public bool CanDeleteDirectory(VfsNode parentDirectory, VfsNode directory, out string reason)
    {
        if (!TryValidateWriteAccess(out reason))
        {
            return false;
        }

        if (!TryGetEntryPath(parentDirectory, expectDirectory: true, out var parentPath, out reason)
            || !TryGetEntryPath(directory, expectDirectory: true, out var directoryPath, out reason)
            || directoryPath == @"\")
        {
            reason = string.IsNullOrEmpty(reason) ? "ルートディレクトリは削除できません。" : reason;
            return false;
        }

        if (!string.Equals(
                GetParentPath(directoryPath).TrimEnd('\\'),
                parentPath.TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase))
        {
            reason = "削除対象が指定ディレクトリの直下にありません。";
            return false;
        }

        try
        {
            if (!_reader.DirectoryExists(directoryPath))
            {
                reason = $"削除対象ディレクトリを再確認できません: {directory.Name}";
                return false;
            }

            if (_reader.GetFileSystemEntries(directoryPath).Any())
            {
                reason = "安全のため空のディレクトリだけを削除できます。先に内容を削除してください。";
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

    public void DeleteDirectory(
        VfsNode parentDirectory,
        VfsNode directory,
        CancellationToken cancellationToken = default)
    {
        if (!CanDeleteDirectory(parentDirectory, directory, out var reason))
        {
            throw new NotSupportedException(reason);
        }

        cancellationToken.ThrowIfCancellationRequested();
        _reader.DeleteDirectory((string)directory.Metadata!, recursive: false);
        FinalizeMutation();
    }

    public bool CanMoveEntry(
        VfsNode sourceDirectory,
        VfsNode entry,
        VfsNode destinationDirectory,
        string destinationName,
        out string reason)
    {
        if (!TryValidateWriteAccess(out reason)
            || !TryGetEntryPath(sourceDirectory, expectDirectory: true, out var sourceDirectoryPath, out reason)
            || !TryGetEntryPath(destinationDirectory, expectDirectory: true, out var destinationDirectoryPath, out reason)
            || !TryGetEntryPath(entry, entry.IsDirectory, out var sourcePath, out reason)
            || !TryGetNewFilePath(destinationDirectory, destinationName, out var destinationPath, out reason))
        {
            return false;
        }

        if (!string.Equals(
                GetParentPath(sourcePath).TrimEnd('\\'),
                sourceDirectoryPath.TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase))
        {
            reason = "移動対象が指定した移動元ディレクトリの直下にありません。";
            return false;
        }

        if (string.Equals(sourcePath, destinationPath, StringComparison.Ordinal))
        {
            reason = "移動元と移動先が同じです。";
            return false;
        }

        if (entry.IsDirectory
            && (string.Equals(destinationDirectoryPath, sourcePath, StringComparison.OrdinalIgnoreCase)
                || destinationDirectoryPath.StartsWith(
                    sourcePath.TrimEnd('\\') + "\\",
                    StringComparison.OrdinalIgnoreCase)))
        {
            reason = "ディレクトリを自分自身の配下へ移動できません。";
            return false;
        }

        try
        {
            if ((entry.IsDirectory && !_reader.DirectoryExists(sourcePath))
                || (!entry.IsDirectory && !_reader.FileExists(sourcePath)))
            {
                reason = $"移動対象を再確認できません: {entry.Name}";
                return false;
            }

            if (_reader.FileExists(destinationPath) || _reader.DirectoryExists(destinationPath))
            {
                reason = $"移動先には同名の項目が既に存在します: {destinationName}";
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

    public VfsNode MoveEntry(
        VfsNode sourceDirectory,
        VfsNode entry,
        VfsNode destinationDirectory,
        string destinationName,
        CancellationToken cancellationToken = default)
    {
        if (!CanMoveEntry(sourceDirectory, entry, destinationDirectory, destinationName, out var reason)
            || !TryGetNewFilePath(destinationDirectory, destinationName, out var destinationPath, out reason))
        {
            throw new NotSupportedException(reason);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var sourcePath = (string)entry.Metadata!;
        RepairFatDirectoryAllocator((string)destinationDirectory.Metadata!);
        if (entry.IsDirectory && Name is "FAT16" or "FAT32")
        {
            MoveFatDirectory(
                sourcePath,
                (string)destinationDirectory.Metadata!,
                destinationName);
        }
        else if (entry.IsDirectory)
        {
            _reader.MoveDirectory(sourcePath, destinationPath);
        }
        else
        {
            _reader.MoveFile(sourcePath, destinationPath, overwrite: false);
        }

        FinalizeMutation();
        return ToNodeSafe(destinationPath);
    }

    public bool CanSetAttributes(VfsNode entry, FileAttributes attributes, out string reason)
    {
        if (!TryValidateWriteAccess(out reason)
            || !TryGetEntryPath(entry, entry.IsDirectory, out var path, out reason))
        {
            return false;
        }

        const FileAttributes editable = FileAttributes.ReadOnly
            | FileAttributes.Hidden
            | FileAttributes.System
            | FileAttributes.Archive;
        var unsupported = attributes & ~(editable | FileAttributes.Directory | FileAttributes.Normal);
        if (unsupported != 0
            || (attributes.HasFlag(FileAttributes.Normal) && (attributes & ~FileAttributes.Normal) != 0)
            || attributes.HasFlag(FileAttributes.Directory) != entry.IsDirectory)
        {
            reason = "ReadOnly、Hidden、System、Archive属性だけを編集でき、Directory属性は変更できません。";
            return false;
        }

        try
        {
            if ((entry.IsDirectory && !_reader.DirectoryExists(path))
                || (!entry.IsDirectory && !_reader.FileExists(path)))
            {
                reason = $"属性編集対象を再確認できません: {entry.Name}";
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

    public void SetAttributes(
        VfsNode entry,
        FileAttributes attributes,
        CancellationToken cancellationToken = default)
    {
        if (!CanSetAttributes(entry, attributes, out var reason))
        {
            throw new NotSupportedException(reason);
        }

        cancellationToken.ThrowIfCancellationRequested();
        _reader.SetAttributes((string)entry.Metadata!, attributes);
        FinalizeMutation();
    }

    public bool CanSetLastWriteTimeUtc(VfsNode entry, DateTime modifiedUtc, out string reason)
    {
        if (!TryValidateWriteAccess(out reason)
            || !TryGetEntryPath(entry, entry.IsDirectory, out var path, out reason))
        {
            return false;
        }

        modifiedUtc = modifiedUtc.Kind == DateTimeKind.Utc ? modifiedUtc : modifiedUtc.ToUniversalTime();
        if (Name is "FAT16" or "FAT32" or "exFAT"
            && (modifiedUtc.Year < 1980 || modifiedUtc.Year > 2107))
        {
            reason = "FAT系ファイルシステムの更新日時は1980～2107年の範囲で指定してください。";
            return false;
        }

        try
        {
            if ((entry.IsDirectory && !_reader.DirectoryExists(path))
                || (!entry.IsDirectory && !_reader.FileExists(path)))
            {
                reason = $"更新日時の編集対象を再確認できません: {entry.Name}";
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

    public void SetLastWriteTimeUtc(
        VfsNode entry,
        DateTime modifiedUtc,
        CancellationToken cancellationToken = default)
    {
        if (!CanSetLastWriteTimeUtc(entry, modifiedUtc, out var reason))
        {
            throw new NotSupportedException(reason);
        }

        cancellationToken.ThrowIfCancellationRequested();
        modifiedUtc = modifiedUtc.Kind == DateTimeKind.Utc ? modifiedUtc : modifiedUtc.ToUniversalTime();
        _reader.SetLastWriteTimeUtc((string)entry.Metadata!, modifiedUtc);
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
                VirtualPath = normalized,
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
                VirtualPath = normalized,
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

    private static bool TryGetEntryPath(
        VfsNode entry,
        bool expectDirectory,
        out string path,
        out string reason)
    {
        path = string.Empty;
        if (entry.IsDirectory != expectDirectory || entry.Metadata is not string metadataPath)
        {
            reason = expectDirectory
                ? "ディレクトリを再確認できません。"
                : "通常ファイルを再確認できません。";
            return false;
        }

        path = NormalizePath(metadataPath);
        reason = string.Empty;
        return true;
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

    private void PrepareDestinationLength(Stream destination, long contentLength)
    {
        // DiscUtils FAT resolves the cluster containing the requested end position while growing.
        // At an exact cluster boundary that reserves one cluster too many. Let the final write grow
        // the logical length by one byte so the allocated chain still has the required cluster count.
        var preparedLength = Name is "FAT16" or "FAT32" && contentLength > 0
            ? contentLength - 1
            : contentLength;
        destination.SetLength(preparedLength);
    }

    private void RepairFatDirectoryAllocator(string directoryPath)
    {
        if (Name is not ("FAT16" or "FAT32"))
        {
            return;
        }

        // DiscUtils 1.0.88 can combine deleted directory slots separated by one live
        // slot while rebuilding its private free-entry table. Reconstruct that table
        // from its parsed live entries before any operation that allocates a new slot.
        // This prevents a create/move operation from overwriting an existing entry.
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
        try
        {
            var fileSystemType = _reader.GetType();
            var getDirectory = fileSystemType.GetMethod(
                "GetDirectory",
                instance,
                binder: null,
                types: [typeof(string)],
                modifiers: null)
                ?? throw new MissingMethodException(fileSystemType.FullName, "GetDirectory(string)");
            var directory = getDirectory.Invoke(_reader, [directoryPath])
                ?? throw new DirectoryNotFoundException($"FATディレクトリを再確認できません: {directoryPath}");
            var directoryType = directory.GetType();
            var entriesField = directoryType.GetField("_entries", instance)
                ?? throw new MissingFieldException(directoryType.FullName, "_entries");
            var endField = directoryType.GetField("_endOfEntries", instance)
                ?? throw new MissingFieldException(directoryType.FullName, "_endOfEntries");
            var freeTableField = directoryType.GetField("_freeDirectoryEntryTable", instance)
                ?? throw new MissingFieldException(directoryType.FullName, "_freeDirectoryEntryTable");
            var selfLocationField = directoryType.GetField("_selfEntryLocation", instance)
                ?? throw new MissingFieldException(directoryType.FullName, "_selfEntryLocation");
            var parentLocationField = directoryType.GetField("_parentEntryLocation", instance)
                ?? throw new MissingFieldException(directoryType.FullName, "_parentEntryLocation");

            const int entrySize = 32;
            var end = (long)(endField.GetValue(directory)
                ?? throw new InvalidDataException("FATディレクトリ終端を取得できません。"));
            if (end < 0 || end % entrySize != 0 || end / entrySize > int.MaxValue)
            {
                throw new InvalidDataException("FATディレクトリエントリ範囲が不正です。");
            }

            var occupied = new bool[checked((int)(end / entrySize))];
            if (entriesField.GetValue(directory) is not IDictionary entries)
            {
                throw new InvalidDataException("FATディレクトリエントリ一覧を取得できません。");
            }

            foreach (DictionaryEntry item in entries)
            {
                var position = Convert.ToInt64(item.Key, System.Globalization.CultureInfo.InvariantCulture);
                var entry = item.Value
                    ?? throw new InvalidDataException("FATディレクトリエントリが不正です。");
                var entryCountProperty = entry.GetType().GetProperty("EntryCount", BindingFlags.Instance | BindingFlags.Public)
                    ?? throw new MissingMemberException(entry.GetType().FullName, "EntryCount");
                var entryCount = (int)(entryCountProperty.GetValue(entry)
                    ?? throw new InvalidDataException("FATディレクトリエントリ長を取得できません。"));
                MarkOccupied(position, entryCount);
            }

            MarkSpecialEntry(selfLocationField);
            MarkSpecialEntry(parentLocationField);

            var freeTable = Activator.CreateInstance(freeTableField.FieldType)
                ?? throw new InvalidOperationException("FAT空きエントリ表を初期化できません。");
            var addFreeRange = freeTableField.FieldType.GetMethod(
                "AddFreeRange",
                BindingFlags.Instance | BindingFlags.Public,
                binder: null,
                types: [typeof(long), typeof(int)],
                modifiers: null)
                ?? throw new MissingMethodException(freeTableField.FieldType.FullName, "AddFreeRange(long, int)");
            for (var index = 0; index < occupied.Length;)
            {
                if (occupied[index])
                {
                    index++;
                    continue;
                }

                var start = index;
                while (index < occupied.Length && !occupied[index])
                {
                    index++;
                }

                _ = addFreeRange.Invoke(freeTable, [(long)start * entrySize, index - start]);
            }

            freeTableField.SetValue(directory, freeTable);

            void MarkSpecialEntry(FieldInfo field)
            {
                var position = (long)(field.GetValue(directory) ?? -1L);
                if (position >= 0)
                {
                    MarkOccupied(position, 1);
                }
            }

            void MarkOccupied(long position, int count)
            {
                if (position < 0 || position % entrySize != 0 || count <= 0)
                {
                    throw new InvalidDataException("FATディレクトリエントリ位置が不正です。");
                }

                var first = checked((int)(position / entrySize));
                if (first > occupied.Length - count)
                {
                    throw new InvalidDataException("FATディレクトリエントリが終端を超えています。");
                }

                Array.Fill(occupied, true, first, count);
            }
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw new NotSupportedException(
                "FATディレクトリの空き領域を安全に再確認できません。",
                ex.InnerException);
        }
        catch (Exception ex) when (ex is MissingMemberException or InvalidCastException or OverflowException)
        {
            throw new NotSupportedException(
                "このDiscUtils版ではFATディレクトリの安全な追加処理を利用できません。",
                ex);
        }
    }

    private void MoveFatDirectory(
        string sourcePath,
        string destinationDirectoryPath,
        string destinationName)
    {
        // DiscUtils 1.0.88 resolves the destination parent from the full path but then
        // passes that same full path as the leaf name. Invoke its directory primitives
        // with the already validated leaf name so nested moves and renames stay valid.
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
        try
        {
            var fileSystemType = _reader.GetType();
            var getDirectory = fileSystemType.GetMethod(
                "GetDirectory",
                instance,
                binder: null,
                types: [typeof(string)],
                modifiers: null)
                ?? throw new MissingMethodException(fileSystemType.FullName, "GetDirectory(string)");
            var sourceDirectory = getDirectory.Invoke(_reader, [sourcePath])
                ?? throw new DirectoryNotFoundException($"移動元FATディレクトリを再確認できません: {sourcePath}");
            var sourceParentPath = GetParentPath(sourcePath);
            var sourceParent = getDirectory.Invoke(_reader, [sourceParentPath])
                ?? throw new DirectoryNotFoundException($"移動元FAT親ディレクトリを再確認できません: {sourceParentPath}");
            var destinationParent = getDirectory.Invoke(_reader, [destinationDirectoryPath])
                ?? throw new DirectoryNotFoundException(
                    $"移動先FATディレクトリを再確認できません: {destinationDirectoryPath}");
            var directoryType = sourceParent.GetType();
            var findEntry = directoryType.GetMethod(
                "FindEntry",
                instance,
                binder: null,
                types: [typeof(string)],
                modifiers: null)
                ?? throw new MissingMethodException(directoryType.FullName, "FindEntry(string)");
            var attachChild = directoryType.GetMethod(
                "AttachChildDirectory",
                instance,
                binder: null,
                types: [typeof(string), directoryType],
                modifiers: null)
                ?? throw new MissingMethodException(directoryType.FullName, "AttachChildDirectory(string, Directory)");
            var deleteEntry = directoryType.GetMethod(
                "DeleteEntry",
                instance,
                binder: null,
                types: [typeof(long), typeof(bool)],
                modifiers: null)
                ?? throw new MissingMethodException(directoryType.FullName, "DeleteEntry(long, bool)");
            var sourceName = GetDisplayName(sourcePath);
            var sourceId = (long)(findEntry.Invoke(sourceParent, [sourceName])
                ?? throw new InvalidDataException("移動元FATディレクトリエントリを取得できません。"));
            if (sourceId < 0)
            {
                throw new DirectoryNotFoundException($"移動元FATディレクトリを再確認できません: {sourcePath}");
            }

            _ = attachChild.Invoke(destinationParent, [destinationName, sourceDirectory]);
            _ = deleteEntry.Invoke(sourceParent, [sourceId, false]);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw new NotSupportedException("FATディレクトリの移動に失敗しました。", ex.InnerException);
        }
        catch (Exception ex) when (ex is MissingMemberException or InvalidCastException or OverflowException)
        {
            throw new NotSupportedException(
                "このDiscUtils版ではFATディレクトリの安全な移動処理を利用できません。",
                ex);
        }
    }

    private void PadExFatDirectory(string directoryPath)
    {
        if (!Name.Equals("exFAT", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // The bundled exFAT writer closes a newly created directory at its logical
        // entry byte count (32/96/...), while exFAT requires directory data lengths
        // to be cluster aligned. Extend it through the writer's own ClusterStream so
        // allocation metadata, bitmap, stream entry, and checksums are all updated.
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
        Stream? directoryStream = null;
        try
        {
            var boot = new byte[512];
            _stream.Position = 0;
            _stream.ReadExactly(boot);
            var shift = checked(boot[108] + boot[109]);
            if (shift is < 9 or > 25)
            {
                throw new InvalidDataException("exFAT cluster sizeが不正です。");
            }

            var clusterSize = 1L << shift;
            var fileSystemType = _reader.GetType();
            var pathFileSystemField = fileSystemType.GetField("_filesystem", instance)
                ?? throw new MissingFieldException(fileSystemType.FullName, "_filesystem");
            var pathFileSystem = pathFileSystemField.GetValue(_reader)
                ?? throw new InvalidDataException("exFAT path filesystemを取得できません。");
            var pathFileSystemType = pathFileSystem.GetType();
            var getSafeNode = pathFileSystemType.GetMethod(
                "GetSafeNode",
                instance,
                binder: null,
                types: [typeof(string)],
                modifiers: null)
                ?? throw new MissingMethodException(pathFileSystemType.FullName, "GetSafeNode(string)");
            var node = getSafeNode.Invoke(pathFileSystem, [directoryPath])
                ?? throw new DirectoryNotFoundException($"exFATディレクトリを再確認できません: {directoryPath}");
            var entryProperty = node.GetType().GetProperty("Entry", BindingFlags.Instance | BindingFlags.Public)
                ?? throw new MissingMemberException(node.GetType().FullName, "Entry");
            var entry = entryProperty.GetValue(node)
                ?? throw new InvalidDataException("exFATディレクトリエントリを取得できません。");
            var entryFileSystemField = pathFileSystemType.GetField("_entryFilesystem", instance)
                ?? throw new MissingFieldException(pathFileSystemType.FullName, "_entryFilesystem");
            var entryFileSystem = entryFileSystemField.GetValue(pathFileSystem)
                ?? throw new InvalidDataException("exFAT entry filesystemを取得できません。");
            var openData = entryFileSystem.GetType().GetMethod(
                "OpenData",
                instance,
                binder: null,
                types: [entry.GetType(), typeof(FileAccess)],
                modifiers: null)
                ?? throw new MissingMethodException(entryFileSystem.GetType().FullName, "OpenData");
            directoryStream = openData.Invoke(entryFileSystem, [entry, FileAccess.ReadWrite]) as Stream
                ?? throw new InvalidDataException("exFATディレクトリストリームを取得できません。");
            var alignedLength = checked(Math.Max(clusterSize, (directoryStream.Length + clusterSize - 1) / clusterSize * clusterSize));
            if (directoryStream.Length != alignedLength)
            {
                directoryStream.SetLength(alignedLength);
            }
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw new NotSupportedException("exFATディレクトリのallocation調整に失敗しました。", ex.InnerException);
        }
        catch (Exception ex) when (ex is MissingMemberException or InvalidCastException or OverflowException)
        {
            throw new NotSupportedException(
                "このDiscUtils版ではexFATディレクトリを安全に作成できません。",
                ex);
        }
        finally
        {
            directoryStream?.Dispose();
        }
    }

    private void FinalizeMutation()
    {
        _afterMutation?.Invoke();
        _stream.Flush();
    }
}
