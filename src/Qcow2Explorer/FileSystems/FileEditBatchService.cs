using System.Security.Cryptography;
using Qcow2Explorer.Core;
using Qcow2Explorer.Partitions;

namespace Qcow2Explorer.FileSystems;

public sealed record PendingFileEdit(
    FileEditOperationKind Operation,
    string VirtualPath,
    string? ContentPath = null);

public sealed record FileEditBatchResult(
    string DestinationPath,
    int EditCount,
    int ModifiedPageCount,
    IReadOnlyList<FileEditResult> Edits);

public static class FileEditBatchService
{
    public static bool CanEdit(
        IDiskImageReader source,
        PartitionInfo partition,
        IReadOnlyFileSystem fileSystem,
        out string reason)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(partition);
        ArgumentNullException.ThrowIfNull(fileSystem);
        try
        {
            FileEditService.ValidateSource(source, partition);
            var overlay = new CopyOnWriteBlockDevice(source);
            var slice = new WritablePartitionSlice(overlay, partition);
            using var writable = FileReplacementService.CreateWritableFileSystem(fileSystem.Name, slice, partition);
            if (writable.Editor is null)
            {
                reason = $"{fileSystem.Name}のサイズ変更・追加・削除はまだ対応していません。";
                return false;
            }

            return writable.Editor.ValidateFileSystem(out reason);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or NotSupportedException or OverflowException)
        {
            reason = ex.Message;
            return false;
        }
    }

    public static async Task<FileEditBatchResult> ApplyToRawAsync(
        IDiskImageReader source,
        PartitionInfo partition,
        IReadOnlyFileSystem originalFileSystem,
        IReadOnlyList<PendingFileEdit> edits,
        string destinationPath,
        IProgress<DiskImageProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(partition);
        ArgumentNullException.ThrowIfNull(originalFileSystem);
        ArgumentNullException.ThrowIfNull(edits);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        if (edits.Count == 0)
        {
            throw new ArgumentException("保存する変更がありません。", nameof(edits));
        }

        FileEditService.ValidateSource(source, partition);
        destinationPath = Path.GetFullPath(destinationPath);
        if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
        {
            throw new IOException($"出力先は既に存在します: {destinationPath}");
        }

        if (string.Equals(Path.GetFullPath(source.Path), destinationPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("原本と同じパスには保存できません。");
        }

        var overlay = new CopyOnWriteBlockDevice(source);
        var results = new List<FileEditResult>(edits.Count);
        for (var index = 0; index < edits.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var edit = NormalizeEdit(edits[index]);
            progress?.Report(new DiskImageProgress(
                $"変更を仮適用 ({index + 1:N0}/{edits.Count:N0}): {edit.VirtualPath}"));
            var result = await ApplyOneAsync(
                overlay,
                partition,
                originalFileSystem.Name,
                edit,
                destinationPath,
                cancellationToken);
            results.Add(result);
            VerifyOverlay(overlay, partition, edit.Operation, result, cancellationToken);
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
            VerifyFinalImage(pendingPath, partition, originalFileSystem.Name, results, cancellationToken);
            File.Move(pendingPath, destinationPath);
        }
        catch
        {
            FileEditService.TryDelete(pendingPath);
            throw;
        }

        return new FileEditBatchResult(destinationPath, results.Count, overlay.ModifiedPageCount, results);
    }

    private static async Task<FileEditResult> ApplyOneAsync(
        CopyOnWriteBlockDevice overlay,
        PartitionInfo partition,
        string fileSystemName,
        PendingFileEdit edit,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        FileInfo? contentInfo = null;
        FileStream? content = null;
        byte[]? contentHash = null;
        try
        {
            if (edit.Operation is FileEditOperationKind.WriteContent or FileEditOperationKind.CreateFile)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(edit.ContentPath);
                var contentPath = Path.GetFullPath(edit.ContentPath);
                if (string.Equals(contentPath, destinationPath, StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("編集内容と同じパスには出力できません。");
                }

                contentInfo = new FileInfo(contentPath);
                if (!contentInfo.Exists)
                {
                    throw new FileNotFoundException("編集内容のファイルが見つかりません。", contentPath);
                }

                content = FileEditService.OpenContent(contentPath);
                contentHash = await SHA256.HashDataAsync(content, cancellationToken);
                content.Position = 0;
            }

            var slice = new WritablePartitionSlice(overlay, partition);
            using var writable = FileReplacementService.CreateWritableFileSystem(fileSystemName, slice, partition);
            var editor = writable.Editor
                ?? throw new NotSupportedException($"{fileSystemName}のサイズ変更・追加・削除はまだ対応していません。");
            string virtualPath;
            long previousLength;
            long newLength;
            switch (edit.Operation)
            {
                case FileEditOperationKind.WriteContent:
                    {
                        var file = ResolveRequired(writable.FileSystem, edit.VirtualPath, expectDirectory: false);
                        if (!editor.CanWriteFile(file, contentInfo!.Length, out var reason))
                        {
                            throw new NotSupportedException(reason);
                        }

                        previousLength = file.Size;
                        newLength = contentInfo.Length;
                        virtualPath = edit.VirtualPath;
                        editor.WriteFileContent(file, content!, newLength, cancellationToken);
                        break;
                    }
                case FileEditOperationKind.CreateFile:
                    {
                        var name = VirtualPath.Split(edit.VirtualPath).LastOrDefault()
                            ?? throw new ArgumentException("作成するファイル名がありません。", nameof(edit));
                        var directory = ResolveRequired(
                            writable.FileSystem,
                            VirtualPath.GetParent(edit.VirtualPath),
                            expectDirectory: true);
                        if (!editor.CanCreateFile(directory, name, contentInfo!.Length, out var reason))
                        {
                            throw new NotSupportedException(reason);
                        }

                        var created = editor.CreateFile(
                            directory,
                            name,
                            content!,
                            contentInfo.Length,
                            cancellationToken);
                        virtualPath = string.IsNullOrWhiteSpace(created.VirtualPath)
                            ? edit.VirtualPath
                            : VirtualPath.Normalize(created.VirtualPath);
                        previousLength = 0;
                        newLength = contentInfo.Length;
                        break;
                    }
                case FileEditOperationKind.DeleteFile:
                    {
                        var file = ResolveRequired(writable.FileSystem, edit.VirtualPath, expectDirectory: false);
                        var directory = ResolveRequired(
                            writable.FileSystem,
                            VirtualPath.GetParent(edit.VirtualPath),
                            expectDirectory: true);
                        if (!editor.CanDeleteFile(directory, file, out var reason))
                        {
                            throw new NotSupportedException(reason);
                        }

                        virtualPath = edit.VirtualPath;
                        previousLength = file.Size;
                        newLength = 0;
                        editor.DeleteFile(directory, file, cancellationToken);
                        break;
                    }
                default:
                    throw new ArgumentOutOfRangeException(nameof(edit));
            }

            if (!editor.ValidateFileSystem(out var validationReason))
            {
                throw new InvalidDataException($"仮適用後のファイルシステム検証に失敗しました: {validationReason}");
            }

            return new FileEditResult(
                destinationPath,
                edit.Operation,
                virtualPath,
                previousLength,
                newLength,
                overlay.ModifiedPageCount,
                contentHash);
        }
        finally
        {
            if (content is not null)
            {
                await content.DisposeAsync();
            }
        }
    }

    private static PendingFileEdit NormalizeEdit(PendingFileEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        var path = VirtualPath.Normalize(edit.VirtualPath);
        if (path == "/")
        {
            throw new ArgumentException("ルートディレクトリ自体は編集できません。", nameof(edit));
        }

        return edit with { VirtualPath = path };
    }

    private static VfsNode ResolveRequired(
        IReadOnlyFileSystem fileSystem,
        string virtualPath,
        bool expectDirectory)
    {
        if (!FileEditService.TryResolvePath(fileSystem, virtualPath, out var node))
        {
            throw new FileNotFoundException($"仮想パスが見つかりません: {virtualPath}");
        }

        if (node.IsDirectory != expectDirectory)
        {
            throw new InvalidDataException(
                expectDirectory
                    ? $"仮想パスはディレクトリではありません: {virtualPath}"
                    : $"仮想パスは通常ファイルではありません: {virtualPath}");
        }

        return node;
    }

    private static void VerifyOverlay(
        CopyOnWriteBlockDevice overlay,
        PartitionInfo partition,
        FileEditOperationKind operation,
        FileEditResult result,
        CancellationToken cancellationToken)
    {
        var fileSystem = FileSystemDetector.TryOpen(overlay, partition, out var error)
            ?? throw new InvalidDataException($"仮適用後のファイルシステムを再オープンできません: {error}");
        try
        {
            FileEditService.ValidateReadOnlyFileSystem(fileSystem, "仮適用後");
            FileEditService.VerifyOperation(
                fileSystem,
                operation,
                result.VirtualPath,
                result.NewLength,
                result.Sha256,
                cancellationToken);
        }
        finally
        {
            (fileSystem as IDisposable)?.Dispose();
        }
    }

    private static void VerifyFinalImage(
        string imagePath,
        PartitionInfo partition,
        string fileSystemName,
        IReadOnlyList<FileEditResult> results,
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
            FileSystem = fileSystemName,
        };
        var fileSystem = FileSystemDetector.TryOpen(reader, outputPartition, out var error)
            ?? throw new InvalidDataException($"出力RAWのファイルシステムを再オープンできません: {error}");
        try
        {
            FileEditService.ValidateReadOnlyFileSystem(fileSystem, "出力");
            var comparison = fileSystem.Name is "FAT16" or "FAT32" or "exFAT" or "NTFS"
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;
            var finalResults = new Dictionary<string, FileEditResult>(comparison);
            foreach (var result in results)
            {
                finalResults[result.VirtualPath] = result;
            }

            foreach (var result in finalResults.Values)
            {
                FileEditService.VerifyOperation(
                    fileSystem,
                    result.Operation,
                    result.VirtualPath,
                    result.NewLength,
                    result.Sha256,
                    cancellationToken);
            }
        }
        finally
        {
            (fileSystem as IDisposable)?.Dispose();
        }
    }
}
