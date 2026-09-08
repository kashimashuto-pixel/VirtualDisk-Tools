using System.Security.Cryptography;
using Qcow2Explorer.Core;
using Qcow2Explorer.Partitions;

namespace Qcow2Explorer.FileSystems;

public sealed record PendingFileEdit(
    FileEditOperationKind Operation,
    string VirtualPath,
    string? ContentPath = null,
    string? DestinationVirtualPath = null,
    FileAttributes? Attributes = null,
    DateTime? ModifiedUtc = null);

public sealed record FileEditBatchResult(
    string DestinationPath,
    int EditCount,
    int ModifiedPageCount,
    IReadOnlyList<FileEditResult> Edits,
    bool IsLogicalVolumeOutput = false);

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
            var editSource = FileEditService.ResolveEditableSource(source, partition);
            var overlay = new CopyOnWriteBlockDevice(editSource.Reader);
            var slice = new WritablePartitionSlice(overlay, editSource.Partition);
            using var writable = FileReplacementService.CreateWritableFileSystem(fileSystem.Name, slice, editSource.Partition);
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

        var editSource = FileEditService.ResolveEditableSource(source, partition);
        var effectivePartition = editSource.Partition;
        var overlay = new CopyOnWriteBlockDevice(editSource.Reader);
        var results = new List<FileEditResult>(edits.Count);
        for (var index = 0; index < edits.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var edit = NormalizeEdit(edits[index]);
            progress?.Report(new DiskImageProgress(
                $"変更を仮適用 ({index + 1:N0}/{edits.Count:N0}): {edit.VirtualPath}"));
            var result = await ApplyOneAsync(
                overlay,
                effectivePartition,
                originalFileSystem.Name,
                edit,
                destinationPath,
                cancellationToken);
            results.Add(result);
            VerifyOverlay(overlay, effectivePartition, result, cancellationToken);
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
            VerifyFinalImage(pendingPath, effectivePartition, originalFileSystem.Name, results, cancellationToken);
            File.Move(pendingPath, destinationPath);
        }
        catch
        {
            FileEditService.TryDelete(pendingPath);
            throw;
        }

        return new FileEditBatchResult(
            destinationPath,
            results.Count,
            overlay.ModifiedPageCount,
            results,
            editSource.IsLogicalOutput);
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
                ?? throw new NotSupportedException($"{fileSystemName}の編集にはまだ対応していません。");
            string virtualPath;
            string? destinationVirtualPath = null;
            long previousLength;
            long newLength;
            var isDirectory = false;
            FileAttributes? expectedAttributes = null;
            DateTime? expectedModifiedUtc = null;
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
                case FileEditOperationKind.CreateDirectory:
                    {
                        var name = VirtualPath.Split(edit.VirtualPath).LastOrDefault()
                            ?? throw new ArgumentException("作成するディレクトリ名がありません。", nameof(edit));
                        var directory = ResolveRequired(
                            writable.FileSystem,
                            VirtualPath.GetParent(edit.VirtualPath),
                            expectDirectory: true);
                        if (!editor.CanCreateDirectory(directory, name, out var reason))
                        {
                            throw new NotSupportedException(reason);
                        }

                        var created = editor.CreateDirectory(directory, name, cancellationToken);
                        virtualPath = string.IsNullOrWhiteSpace(created.VirtualPath)
                            ? edit.VirtualPath
                            : VirtualPath.Normalize(created.VirtualPath);
                        previousLength = 0;
                        newLength = 0;
                        isDirectory = true;
                        break;
                    }
                case FileEditOperationKind.DeleteDirectory:
                    {
                        var directory = ResolveRequired(writable.FileSystem, edit.VirtualPath, expectDirectory: true);
                        var parent = ResolveRequired(
                            writable.FileSystem,
                            VirtualPath.GetParent(edit.VirtualPath),
                            expectDirectory: true);
                        if (!editor.CanDeleteDirectory(parent, directory, out var reason))
                        {
                            throw new NotSupportedException(reason);
                        }

                        virtualPath = edit.VirtualPath;
                        previousLength = 0;
                        newLength = 0;
                        isDirectory = true;
                        editor.DeleteDirectory(parent, directory, cancellationToken);
                        break;
                    }
                case FileEditOperationKind.MoveEntry:
                    {
                        var moveDestinationPath = edit.DestinationVirtualPath
                            ?? throw new ArgumentException("移動先の仮想パスがありません。", nameof(edit));
                        var entry = ResolveRequiredEntry(writable.FileSystem, edit.VirtualPath);
                        var sourceDirectory = ResolveRequired(
                            writable.FileSystem,
                            VirtualPath.GetParent(edit.VirtualPath),
                            expectDirectory: true);
                        var destinationDirectory = ResolveRequired(
                            writable.FileSystem,
                            VirtualPath.GetParent(moveDestinationPath),
                            expectDirectory: true);
                        var destinationName = VirtualPath.Split(moveDestinationPath).LastOrDefault()
                            ?? throw new ArgumentException("移動先の名前がありません。", nameof(edit));
                        if (!editor.CanMoveEntry(
                                sourceDirectory,
                                entry,
                                destinationDirectory,
                                destinationName,
                                out var reason))
                        {
                            throw new NotSupportedException(reason);
                        }

                        if (!entry.IsDirectory)
                        {
                            contentHash = FileEditService.ComputeVirtualFileHash(
                                writable.FileSystem,
                                entry,
                                cancellationToken);
                        }

                        var moved = editor.MoveEntry(
                            sourceDirectory,
                            entry,
                            destinationDirectory,
                            destinationName,
                            cancellationToken);
                        virtualPath = edit.VirtualPath;
                        destinationVirtualPath = string.IsNullOrWhiteSpace(moved.VirtualPath)
                            ? moveDestinationPath
                            : VirtualPath.Normalize(moved.VirtualPath);
                        previousLength = entry.Size;
                        newLength = entry.Size;
                        isDirectory = entry.IsDirectory;
                        break;
                    }
                case FileEditOperationKind.SetAttributes:
                    {
                        var attributes = edit.Attributes
                            ?? throw new ArgumentException("設定する属性がありません。", nameof(edit));
                        var entry = ResolveRequiredEntry(writable.FileSystem, edit.VirtualPath);
                        if (!editor.CanSetAttributes(entry, attributes, out var reason))
                        {
                            throw new NotSupportedException(reason);
                        }

                        if (!entry.IsDirectory)
                        {
                            contentHash = FileEditService.ComputeVirtualFileHash(
                                writable.FileSystem,
                                entry,
                                cancellationToken);
                        }

                        virtualPath = edit.VirtualPath;
                        previousLength = entry.Size;
                        newLength = entry.Size;
                        isDirectory = entry.IsDirectory;
                        expectedAttributes = attributes;
                        editor.SetAttributes(entry, attributes, cancellationToken);
                        break;
                    }
                case FileEditOperationKind.SetLastWriteTimeUtc:
                    {
                        var modifiedUtc = edit.ModifiedUtc
                            ?? throw new ArgumentException("設定する更新日時がありません。", nameof(edit));
                        modifiedUtc = modifiedUtc.Kind == DateTimeKind.Utc
                            ? modifiedUtc
                            : modifiedUtc.ToUniversalTime();
                        var entry = ResolveRequiredEntry(writable.FileSystem, edit.VirtualPath);
                        if (!editor.CanSetLastWriteTimeUtc(entry, modifiedUtc, out var reason))
                        {
                            throw new NotSupportedException(reason);
                        }

                        if (!entry.IsDirectory)
                        {
                            contentHash = FileEditService.ComputeVirtualFileHash(
                                writable.FileSystem,
                                entry,
                                cancellationToken);
                        }

                        virtualPath = edit.VirtualPath;
                        previousLength = entry.Size;
                        newLength = entry.Size;
                        isDirectory = entry.IsDirectory;
                        expectedModifiedUtc = modifiedUtc;
                        editor.SetLastWriteTimeUtc(entry, modifiedUtc, cancellationToken);
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
                contentHash,
                destinationVirtualPath,
                isDirectory,
                expectedAttributes,
                expectedModifiedUtc);
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

        var destinationPath = edit.DestinationVirtualPath;
        if (edit.Operation == FileEditOperationKind.MoveEntry)
        {
            destinationPath = VirtualPath.Normalize(
                destinationPath
                    ?? throw new ArgumentException("移動先の仮想パスがありません。", nameof(edit)));
            if (destinationPath == "/")
            {
                throw new ArgumentException("ルートディレクトリ自体を移動先にはできません。", nameof(edit));
            }
        }

        return edit with { VirtualPath = path, DestinationVirtualPath = destinationPath };
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

    private static VfsNode ResolveRequiredEntry(IReadOnlyFileSystem fileSystem, string virtualPath)
    {
        if (!FileEditService.TryResolvePath(fileSystem, virtualPath, out var node))
        {
            throw new FileNotFoundException($"仮想パスが見つかりません: {virtualPath}");
        }

        return node;
    }

    private static void VerifyOverlay(
        CopyOnWriteBlockDevice overlay,
        PartitionInfo partition,
        FileEditResult result,
        CancellationToken cancellationToken)
    {
        var fileSystem = FileSystemDetector.TryOpen(overlay, partition, out var error)
            ?? throw new InvalidDataException($"仮適用後のファイルシステムを再オープンできません: {error}");
        try
        {
            FileEditService.ValidateReadOnlyFileSystem(fileSystem, "仮適用後");
            VerifyResult(fileSystem, result, cancellationToken);
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
            foreach (var expectation in BuildFinalExpectations(fileSystem, results).Values)
            {
                if (!expectation.Exists)
                {
                    if (FileEditService.TryResolvePath(fileSystem, expectation.VirtualPath, out _))
                    {
                        throw new InvalidDataException(
                            $"最終出力で削除・移動元パスが残っています: {expectation.VirtualPath}");
                    }

                    continue;
                }

                VerifyExistingEntry(
                    fileSystem,
                    expectation.VirtualPath,
                    expectation.Result,
                    cancellationToken);
            }
        }
        finally
        {
            (fileSystem as IDisposable)?.Dispose();
        }
    }

    private static void VerifyResult(
        IReadOnlyFileSystem fileSystem,
        FileEditResult result,
        CancellationToken cancellationToken)
    {
        switch (result.Operation)
        {
            case FileEditOperationKind.CreateDirectory:
                VerifyExistingEntry(fileSystem, result.VirtualPath, result, cancellationToken);
                return;
            case FileEditOperationKind.DeleteDirectory:
                if (FileEditService.TryResolvePath(fileSystem, result.VirtualPath, out _))
                {
                    throw new InvalidDataException("削除後も対象ディレクトリが残っています。");
                }

                return;
            case FileEditOperationKind.MoveEntry:
                if (FileEditService.TryResolvePath(fileSystem, result.VirtualPath, out _))
                {
                    throw new InvalidDataException("移動後も移動元のパスが残っています。");
                }

                VerifyExistingEntry(
                    fileSystem,
                    result.DestinationVirtualPath
                        ?? throw new InvalidDataException("移動結果に移動先パスがありません。"),
                    result,
                    cancellationToken);
                return;
            case FileEditOperationKind.SetAttributes:
            case FileEditOperationKind.SetLastWriteTimeUtc:
                VerifyExistingEntry(fileSystem, result.VirtualPath, result, cancellationToken);
                return;
            default:
                FileEditService.VerifyOperation(
                    fileSystem,
                    result.Operation,
                    result.VirtualPath,
                    result.NewLength,
                    result.Sha256,
                    cancellationToken);
                return;
        }
    }

    private static void VerifyExistingEntry(
        IReadOnlyFileSystem fileSystem,
        string virtualPath,
        FileEditResult result,
        CancellationToken cancellationToken)
    {
        if (!FileEditService.TryResolvePath(fileSystem, virtualPath, out var entry)
            || entry.IsDirectory != result.IsDirectory)
        {
            throw new InvalidDataException($"編集後の項目を再確認できません: {virtualPath}");
        }

        if (!entry.IsDirectory)
        {
            if (entry.Size != result.NewLength)
            {
                throw new InvalidDataException($"編集後ファイルのサイズが一致しません: {virtualPath}");
            }

            var actualHash = FileEditService.ComputeVirtualFileHash(fileSystem, entry, cancellationToken);
            if (result.Sha256 is not null
                && !CryptographicOperations.FixedTimeEquals(result.Sha256, actualHash))
            {
                throw new InvalidDataException($"編集後ファイルのSHA-256が一致しません: {virtualPath}");
            }
        }

        if (result.Attributes is FileAttributes expectedAttributes
            && entry.Attributes != expectedAttributes)
        {
            throw new InvalidDataException(
                $"編集後の属性が一致しません: {virtualPath} expected={expectedAttributes}, actual={entry.Attributes}");
        }

        if (result.ModifiedUtc is DateTime expectedModifiedUtc)
        {
            var tolerance = fileSystem.Name is "FAT16" or "FAT32" or "exFAT"
                ? TimeSpan.FromSeconds(2)
                : TimeSpan.FromMilliseconds(1);
            if (entry.ModifiedUtc is not DateTime actualModifiedUtc
                || (actualModifiedUtc - expectedModifiedUtc).Duration() > tolerance)
            {
                throw new InvalidDataException(
                    $"編集後の更新日時が一致しません: {virtualPath} expected={expectedModifiedUtc:O}, actual={entry.ModifiedUtc:O}");
            }
        }
    }

    private static Dictionary<string, FinalExpectation> BuildFinalExpectations(
        IReadOnlyFileSystem fileSystem,
        IReadOnlyList<FileEditResult> results)
    {
        var comparer = fileSystem.Name is "FAT16" or "FAT32" or "exFAT" or "NTFS"
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var expectations = new Dictionary<string, FinalExpectation>(comparer);
        foreach (var result in results)
        {
            var sourcePath = VirtualPath.Normalize(result.VirtualPath);
            switch (result.Operation)
            {
                case FileEditOperationKind.DeleteFile:
                case FileEditOperationKind.DeleteDirectory:
                    RemoveChildExpectations(expectations, sourcePath, comparer);
                    expectations[sourcePath] = new FinalExpectation(sourcePath, Exists: false, result);
                    break;
                case FileEditOperationKind.MoveEntry:
                    {
                        var destinationPath = VirtualPath.Normalize(
                            result.DestinationVirtualPath
                                ?? throw new InvalidDataException("移動結果に移動先パスがありません。"));
                        var remapped = expectations.Values
                            .Where(expectation => expectation.Exists
                                && IsSameOrChild(expectation.VirtualPath, sourcePath, comparer))
                            .ToArray();
                        foreach (var expectation in remapped)
                        {
                            expectations.Remove(expectation.VirtualPath);
                            var suffix = expectation.VirtualPath[sourcePath.Length..];
                            var remappedPath = destinationPath + suffix;
                            expectations[remappedPath] = expectation with { VirtualPath = remappedPath };
                        }

                        expectations[sourcePath] = new FinalExpectation(sourcePath, Exists: false, result);
                        var destinationResult = expectations.TryGetValue(destinationPath, out var movedExpectation)
                            ? MergeExpectedResult(movedExpectation.Result, result)
                            : result;
                        expectations[destinationPath] = new FinalExpectation(destinationPath, Exists: true, destinationResult);
                        break;
                    }
                default:
                    var expectedResult = expectations.TryGetValue(sourcePath, out var previousExpectation)
                        && previousExpectation.Exists
                            ? MergeExpectedResult(previousExpectation.Result, result)
                            : result;
                    expectations[sourcePath] = new FinalExpectation(sourcePath, Exists: true, expectedResult);
                    break;
            }
        }

        return expectations;
    }

    private static FileEditResult MergeExpectedResult(FileEditResult previous, FileEditResult current) =>
        current.Operation switch
        {
            FileEditOperationKind.WriteContent => current with
            {
                Attributes = previous.Attributes,
            },
            FileEditOperationKind.MoveEntry => current with
            {
                Attributes = previous.Attributes,
                ModifiedUtc = previous.ModifiedUtc,
            },
            FileEditOperationKind.SetAttributes => current with
            {
                ModifiedUtc = previous.ModifiedUtc,
            },
            FileEditOperationKind.SetLastWriteTimeUtc => current with
            {
                Attributes = previous.Attributes,
            },
            _ => current,
        };

    private static void RemoveChildExpectations(
        Dictionary<string, FinalExpectation> expectations,
        string directoryPath,
        StringComparer comparer)
    {
        foreach (var path in expectations.Keys
                     .Where(path => IsSameOrChild(path, directoryPath, comparer))
                     .ToArray())
        {
            expectations.Remove(path);
        }
    }

    private static bool IsSameOrChild(string path, string directoryPath, StringComparer comparer)
    {
        return comparer.Equals(path, directoryPath)
            || path.Length > directoryPath.Length
            && comparer.Equals(path[..directoryPath.Length], directoryPath)
            && path[directoryPath.Length] == '/';
    }

    private sealed record FinalExpectation(string VirtualPath, bool Exists, FileEditResult Result);
}
