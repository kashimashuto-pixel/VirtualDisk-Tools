using System.Security.Cryptography;
using Qcow2Explorer.Core;
using Qcow2Explorer.Partitions;

namespace Qcow2Explorer.FileSystems;

public enum FileEditOperationKind
{
    WriteContent,
    CreateFile,
    DeleteFile,
}

public sealed record FileEditResult(
    string DestinationPath,
    FileEditOperationKind Operation,
    string VirtualPath,
    long PreviousLength,
    long NewLength,
    int ModifiedPageCount,
    byte[]? Sha256);

public static class FileEditService
{
    private const int BufferSize = 1024 * 1024;

    public static Task<FileEditResult> WriteFileToRawAsync(
        IDiskImageReader source,
        PartitionInfo partition,
        IReadOnlyFileSystem originalFileSystem,
        VfsNode file,
        string contentPath,
        string destinationPath,
        IProgress<DiskImageProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            source,
            partition,
            originalFileSystem,
            FileEditOperationKind.WriteContent,
            directory: null,
            file,
            name: null,
            contentPath,
            destinationPath,
            progress,
            cancellationToken);
    }

    public static Task<FileEditResult> CreateFileToRawAsync(
        IDiskImageReader source,
        PartitionInfo partition,
        IReadOnlyFileSystem originalFileSystem,
        VfsNode directory,
        string name,
        string contentPath,
        string destinationPath,
        IProgress<DiskImageProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            source,
            partition,
            originalFileSystem,
            FileEditOperationKind.CreateFile,
            directory,
            file: null,
            name,
            contentPath,
            destinationPath,
            progress,
            cancellationToken);
    }

    public static Task<FileEditResult> DeleteFileToRawAsync(
        IDiskImageReader source,
        PartitionInfo partition,
        IReadOnlyFileSystem originalFileSystem,
        VfsNode directory,
        VfsNode file,
        string destinationPath,
        IProgress<DiskImageProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            source,
            partition,
            originalFileSystem,
            FileEditOperationKind.DeleteFile,
            directory,
            file,
            name: null,
            contentPath: null,
            destinationPath,
            progress,
            cancellationToken);
    }

    private static async Task<FileEditResult> ExecuteAsync(
        IDiskImageReader source,
        PartitionInfo partition,
        IReadOnlyFileSystem originalFileSystem,
        FileEditOperationKind operation,
        VfsNode? directory,
        VfsNode? file,
        string? name,
        string? contentPath,
        string destinationPath,
        IProgress<DiskImageProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(partition);
        ArgumentNullException.ThrowIfNull(originalFileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ValidateSource(source, partition);

        destinationPath = Path.GetFullPath(destinationPath);
        if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
        {
            throw new IOException($"出力先は既に存在します: {destinationPath}");
        }

        if (string.Equals(Path.GetFullPath(source.Path), destinationPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("原本と同じパスには保存できません。");
        }

        FileInfo? contentInfo = null;
        byte[]? contentHash = null;
        if (operation is FileEditOperationKind.WriteContent or FileEditOperationKind.CreateFile)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(contentPath);
            contentPath = Path.GetFullPath(contentPath);
            contentInfo = new FileInfo(contentPath);
            if (!contentInfo.Exists)
            {
                throw new FileNotFoundException("編集内容のファイルが見つかりません。", contentPath);
            }

            if (string.Equals(contentPath, destinationPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("編集内容と同じパスには出力できません。");
            }

            await using var hashInput = OpenContent(contentPath);
            contentHash = await SHA256.HashDataAsync(hashInput, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var overlay = new CopyOnWriteBlockDevice(source);
        var slice = new WritablePartitionSlice(overlay, partition);
        string virtualPath;
        long previousLength;
        long newLength;
        using (var writable = FileReplacementService.CreateWritableFileSystem(
                   originalFileSystem.Name,
                   slice,
                   partition))
        {
            var editor = writable.Editor
                ?? throw new NotSupportedException(
                    $"{originalFileSystem.Name}のサイズ変更・追加・削除はまだ対応していません。");
            switch (operation)
            {
                case FileEditOperationKind.WriteContent:
                    {
                        ArgumentNullException.ThrowIfNull(file);
                        var writableFile = writable.MapFile(file);
                        if (!editor.CanWriteFile(writableFile, contentInfo!.Length, out var reason))
                        {
                            throw new NotSupportedException(reason);
                        }

                        virtualPath = GetVirtualPath(writableFile);
                        previousLength = writableFile.Size;
                        newLength = contentInfo.Length;
                        await using var content = OpenContent(contentInfo.FullName);
                        editor.WriteFileContent(writableFile, content, newLength, cancellationToken);
                        break;
                    }
                case FileEditOperationKind.CreateFile:
                    {
                        ArgumentNullException.ThrowIfNull(directory);
                        ArgumentException.ThrowIfNullOrWhiteSpace(name);
                        var writableDirectory = writable.MapFile(directory);
                        if (!editor.CanCreateFile(writableDirectory, name, contentInfo!.Length, out var reason))
                        {
                            throw new NotSupportedException(reason);
                        }

                        await using var content = OpenContent(contentInfo.FullName);
                        var created = editor.CreateFile(
                            writableDirectory,
                            name,
                            content,
                            contentInfo.Length,
                            cancellationToken);
                        virtualPath = GetVirtualPath(created);
                        previousLength = 0;
                        newLength = contentInfo.Length;
                        break;
                    }
                case FileEditOperationKind.DeleteFile:
                    {
                        ArgumentNullException.ThrowIfNull(directory);
                        ArgumentNullException.ThrowIfNull(file);
                        var writableDirectory = writable.MapFile(directory);
                        var writableFile = writable.MapFile(file);
                        if (!editor.CanDeleteFile(writableDirectory, writableFile, out var reason))
                        {
                            throw new NotSupportedException(reason);
                        }

                        virtualPath = GetVirtualPath(writableFile);
                        previousLength = writableFile.Size;
                        newLength = 0;
                        editor.DeleteFile(writableDirectory, writableFile, cancellationToken);
                        break;
                    }
                default:
                    throw new ArgumentOutOfRangeException(nameof(operation));
            }

            if (!editor.ValidateFileSystem(out var validationReason))
            {
                throw new InvalidDataException($"仮適用後のファイルシステム検証に失敗しました: {validationReason}");
            }
        }

        var overlayFileSystem = FileSystemDetector.TryOpen(overlay, partition, out var overlayError)
            ?? throw new InvalidDataException($"仮適用後のファイルシステムを再オープンできません: {overlayError}");
        try
        {
            ValidateReadOnlyFileSystem(overlayFileSystem, "仮適用後");
            VerifyOperation(
                overlayFileSystem,
                operation,
                virtualPath,
                newLength,
                contentHash,
                cancellationToken);
        }
        finally
        {
            (overlayFileSystem as IDisposable)?.Dispose();
        }

        var destinationDirectory = Path.GetDirectoryName(destinationPath)
            ?? throw new ArgumentException("出力先フォルダーを取得できません。", nameof(destinationPath));
        Directory.CreateDirectory(destinationDirectory);
        var pendingPath = Path.Combine(
            destinationDirectory,
            $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.vdt-partial");
        try
        {
            await overlay.ExportRawAsync(pendingPath, progress, cancellationToken);
            VerifyExportedImage(
                pendingPath,
                partition,
                operation,
                virtualPath,
                newLength,
                contentHash,
                cancellationToken);
            File.Move(pendingPath, destinationPath);
        }
        catch
        {
            TryDelete(pendingPath);
            throw;
        }

        return new FileEditResult(
            destinationPath,
            operation,
            virtualPath,
            previousLength,
            newLength,
            overlay.ModifiedPageCount,
            contentHash);
    }

    internal static void ValidateSource(IDiskImageReader source, PartitionInfo partition)
    {
        if (PhysicalDiskReader.IsPhysicalDiskPath(source.Path))
        {
            throw new NotSupportedException("物理ディスクは編集できません。");
        }

        if (partition.ReaderOverride is not null)
        {
            throw new NotSupportedException("RAID、LVM、復号レイヤーなどの合成パーティションは編集できません。");
        }
    }

    internal static FileStream OpenContent(string path)
    {
        return new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    private static string GetVirtualPath(VfsNode node)
    {
        return !string.IsNullOrWhiteSpace(node.VirtualPath)
            ? node.VirtualPath
            : node.Metadata as string
            ?? throw new InvalidDataException($"編集対象の仮想パスを取得できません: {node.Name}");
    }

    internal static void VerifyOperation(
        IReadOnlyFileSystem fileSystem,
        FileEditOperationKind operation,
        string virtualPath,
        long expectedLength,
        byte[]? expectedHash,
        CancellationToken cancellationToken)
    {
        var found = TryResolvePath(fileSystem, virtualPath, out var node);
        if (operation == FileEditOperationKind.DeleteFile)
        {
            if (found)
            {
                throw new InvalidDataException("削除後も対象ファイルがディレクトリに残っています。");
            }

            return;
        }

        if (!found || node.IsDirectory || node.Size != expectedLength)
        {
            throw new InvalidDataException("編集後ファイルのパスまたはサイズを再確認できません。");
        }

        var actualHash = ComputeVirtualFileHash(fileSystem, node, cancellationToken);
        if (expectedHash is null || !CryptographicOperations.FixedTimeEquals(expectedHash, actualHash))
        {
            throw new InvalidDataException("編集後ファイルのSHA-256が入力内容と一致しません。");
        }
    }

    private static void VerifyExportedImage(
        string imagePath,
        PartitionInfo partition,
        FileEditOperationKind operation,
        string virtualPath,
        long expectedLength,
        byte[]? expectedHash,
        CancellationToken cancellationToken)
    {
        using var reader = new RawDiskImageReader(imagePath);
        var outputPartition = new PartitionInfo
        {
            Number = partition.Number,
            Scheme = partition.Scheme,
            Type = partition.Type,
            TypeId = partition.TypeId,
            Name = partition.Name,
            Bootable = partition.Bootable,
            StartLba = partition.StartLba,
            SectorCount = partition.SectorCount,
            SectorSize = partition.SectorSize,
            LengthOverrideBytes = partition.LengthOverrideBytes,
            FileSystem = partition.FileSystem,
        };
        var fileSystem = FileSystemDetector.TryOpen(reader, outputPartition, out var error)
            ?? throw new InvalidDataException($"出力RAWのファイルシステムを再オープンできません: {error}");
        try
        {
            ValidateReadOnlyFileSystem(fileSystem, "出力");

            VerifyOperation(
                fileSystem,
                operation,
                virtualPath,
                expectedLength,
                expectedHash,
                cancellationToken);
        }
        finally
        {
            (fileSystem as IDisposable)?.Dispose();
        }
    }

    internal static void ValidateReadOnlyFileSystem(IReadOnlyFileSystem fileSystem, string stage)
    {
        if (fileSystem is FatFileSystem fat)
        {
            var validation = fat.ValidateForEditing();
            if (!validation.IsValid)
            {
                throw new InvalidDataException($"{stage}FATの割り当て検証に失敗しました: {validation.Reason}");
            }
        }

        if (fileSystem is ExtFileSystem ext && !ext.ValidateReadOnlyIntegrity(out var reason))
        {
            throw new InvalidDataException($"{stage}ext4の割り当て検証に失敗しました: {reason}");
        }
    }

    internal static bool TryResolvePath(IReadOnlyFileSystem fileSystem, string path, out VfsNode node)
    {
        node = fileSystem.Root;
        var comparison = fileSystem.Name is "FAT16" or "FAT32" or "exFAT" or "NTFS"
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        foreach (var part in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var next = fileSystem.ListDirectory(node)
                .SingleOrDefault(candidate => string.Equals(candidate.Name, part, comparison));
            if (next is null)
            {
                node = null!;
                return false;
            }

            node = next;
        }

        return true;
    }

    private static byte[] ComputeVirtualFileHash(
        IReadOnlyFileSystem fileSystem,
        VfsNode file,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long offset = 0;
        while (offset < file.Size)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = checked((int)Math.Min(BufferSize, file.Size - offset));
            var data = fileSystem.ReadFile(file, offset, count);
            if (data.Length != count)
            {
                throw new InvalidDataException("編集後ファイルを最後まで読み戻せませんでした。");
            }

            hash.AppendData(data);
            offset += count;
        }

        return hash.GetHashAndReset();
    }

    internal static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Preserve the edit or validation failure. The uniquely named partial can be removed manually.
        }
    }
}
