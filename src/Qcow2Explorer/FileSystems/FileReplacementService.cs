using System.Security.Cryptography;
using Qcow2Explorer.Core;
using Qcow2Explorer.Partitions;
using DiscExFatFileSystem = DiscUtils.ExFat.ExFatFileSystem;
using DiscFatFileSystem = DiscUtils.Fat.FatFileSystem;
using DiscNtfsFileSystem = DiscUtils.Ntfs.NtfsFileSystem;

namespace Qcow2Explorer.FileSystems;

public sealed record FileReplacementResult(
    string DestinationPath,
    long BytesReplaced,
    int ModifiedPageCount,
    byte[] Sha256);

public static class FileReplacementService
{
    private const int VerificationBufferSize = 1024 * 1024;

    public static bool CanReplaceToRaw(
        IDiskImageReader source,
        PartitionInfo partition,
        IReadOnlyFileSystem originalFileSystem,
        VfsNode file,
        long replacementLength,
        out string reason)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(partition);
        ArgumentNullException.ThrowIfNull(originalFileSystem);
        ArgumentNullException.ThrowIfNull(file);

        if (PhysicalDiskReader.IsPhysicalDiskPath(source.Path))
        {
            reason = "物理ディスクへの書き込みや、物理ディスクを元にした変更イメージ作成には対応していません。";
            return false;
        }

        if (partition.ReaderOverride is not null)
        {
            reason = "RAID、LVM、復号レイヤーなどの合成パーティションはまだ書き込めません。";
            return false;
        }

        if (file.IsDirectory)
        {
            reason = "通常ファイルだけを置換できます。";
            return false;
        }

        try
        {
            var overlay = new CopyOnWriteBlockDevice(source);
            var slice = new WritablePartitionSlice(overlay, partition);
            using var writableFileSystem = CreateWritableFileSystem(originalFileSystem.Name, slice, partition);
            var writableFile = writableFileSystem.MapFile(file);
            return writableFileSystem.Writer.CanReplaceFile(writableFile, replacementLength, out reason);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or NotSupportedException or OverflowException)
        {
            reason = ex.Message;
            return false;
        }
    }

    public static async Task<FileReplacementResult> ReplaceToRawAsync(
        IDiskImageReader source,
        PartitionInfo partition,
        IReadOnlyFileSystem originalFileSystem,
        VfsNode file,
        string replacementPath,
        string destinationPath,
        IProgress<DiskImageProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(replacementPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        replacementPath = Path.GetFullPath(replacementPath);
        destinationPath = Path.GetFullPath(destinationPath);
        var replacementInfo = new FileInfo(replacementPath);
        if (!replacementInfo.Exists)
        {
            throw new FileNotFoundException("置換元ファイルが見つかりません。", replacementPath);
        }

        if (string.Equals(source.Path, destinationPath, StringComparison.OrdinalIgnoreCase)
            || string.Equals(replacementPath, destinationPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("原本または置換元と同じパスには保存できません。");
        }

        if (!CanReplaceToRaw(
                source,
                partition,
                originalFileSystem,
                file,
                replacementInfo.Length,
                out var reason))
        {
            throw new NotSupportedException(reason);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var overlay = new CopyOnWriteBlockDevice(source);
        var slice = new WritablePartitionSlice(overlay, partition);
        using var writableFileSystem = CreateWritableFileSystem(originalFileSystem.Name, slice, partition);
        var writableFile = writableFileSystem.MapFile(file);
        byte[] sourceHash;
        await using (var replacement = new FileStream(
            replacementPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            VerificationBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            progress?.Report(new DiskImageProgress("ファイル内容をオーバーレイへ反映", 0, replacementInfo.Length));
            writableFileSystem.Writer.ReplaceFileContent(
                writableFile,
                replacement,
                replacementInfo.Length,
                cancellationToken);
            replacement.Position = 0;
            sourceHash = await SHA256.HashDataAsync(replacement, cancellationToken);
        }

        var writtenHash = ComputeVirtualFileHash(
            writableFileSystem.FileSystem,
            writableFile,
            replacementInfo.Length,
            progress,
            cancellationToken);
        if (!CryptographicOperations.FixedTimeEquals(sourceHash, writtenHash))
        {
            throw new InvalidDataException("置換後の内容検証に失敗しました。変更は保存されていません。");
        }

        await overlay.ExportRawAsync(destinationPath, progress, cancellationToken);
        return new FileReplacementResult(
            destinationPath,
            replacementInfo.Length,
            overlay.ModifiedPageCount,
            writtenHash);
    }

    private static byte[] ComputeVirtualFileHash(
        IReadOnlyFileSystem fileSystem,
        VfsNode file,
        long length,
        IProgress<DiskImageProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long offset = 0;
        while (offset < length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = checked((int)Math.Min(VerificationBufferSize, length - offset));
            var data = fileSystem.ReadFile(file, offset, count);
            if (data.Length != count)
            {
                throw new InvalidDataException("置換後ファイルの読み戻しサイズが一致しません。");
            }

            hash.AppendData(data);
            offset += count;
            progress?.Report(new DiskImageProgress("置換後の内容を検証", offset, length));
        }

        return hash.GetHashAndReset();
    }

    internal static WritableFileSystemHandle CreateWritableFileSystem(
        string fileSystemName,
        WritablePartitionSlice slice,
        PartitionInfo partition)
    {
        if (fileSystemName.StartsWith("ext", StringComparison.OrdinalIgnoreCase))
        {
            var fileSystem = new ExtFileSystem(slice, partition);
            return new WritableFileSystemHandle(fileSystem, fileSystem, disposable: null, fileSystem);
        }

        if (fileSystemName.Equals("XFS", StringComparison.OrdinalIgnoreCase))
        {
            var fileSystem = new XfsFileSystem(slice, partition);
            return new WritableFileSystemHandle(fileSystem, fileSystem, fileSystem);
        }

        if (fileSystemName is "FAT16" or "FAT32")
        {
            var validator = new FatFileSystem(slice, partition);
            var fileSystem = new DiscUtilsFileSystem(
                slice,
                partition,
                stream => new DiscFatFileSystem(stream),
                fileSystemName,
                validator.ValidateForEditing,
                validator.SynchronizeFsInfo);
            return new WritableFileSystemHandle(
                fileSystem,
                fileSystem,
                fileSystem,
                fileSystem,
                original => MapFatNode(validator, fileSystem, original));
        }

        if (fileSystemName.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
        {
            (bool IsValid, string Reason) ValidateNtfs()
            {
                try
                {
                    var validator = new NtfsFileSystem(slice, partition);
                    return validator.ValidateForEditing();
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or OverflowException)
                {
                    return (false, ex.Message);
                }
            }

            var fileSystem = new DiscUtilsFileSystem(
                slice,
                partition,
                OpenNtfs,
                "NTFS",
                ValidateNtfs);
            return new WritableFileSystemHandle(
                fileSystem,
                fileSystem,
                fileSystem,
                fileSystem,
                original => MapDiscUtilsNode(fileSystem, original));
        }

        if (fileSystemName.Equals("exFAT", StringComparison.OrdinalIgnoreCase))
        {
            var fileSystem = new DiscUtilsFileSystem(
                slice,
                partition,
                stream => new DiscExFatFileSystem(stream, ['\\', '/']),
                "exFAT");
            return new WritableFileSystemHandle(fileSystem, fileSystem, fileSystem, fileSystem);
        }

        throw new NotSupportedException($"{fileSystemName}の書き込みにはまだ対応していません。");
    }

    private static DiscNtfsFileSystem OpenNtfs(Stream stream)
    {
        var ntfs = new DiscNtfsFileSystem(stream);
        ntfs.NtfsOptions.HideHiddenFiles = false;
        ntfs.NtfsOptions.HideSystemFiles = false;
        ntfs.NtfsOptions.HideMetafiles = false;
        return ntfs;
    }

    private static VfsNode MapDiscUtilsNode(DiscUtilsFileSystem fileSystem, VfsNode original)
    {
        var path = original.Metadata as string;
        if (string.IsNullOrWhiteSpace(path))
        {
            path = original.VirtualPath;
        }

        if (!string.IsNullOrWhiteSpace(path) && fileSystem.TryResolvePath(path, out var mapped))
        {
            return mapped;
        }

        throw new FileNotFoundException($"{fileSystem.Name}上で編集対象を再解決できません: {original.Name}");
    }

    private static VfsNode MapFatNode(
        FatFileSystem validator,
        DiscUtilsFileSystem fileSystem,
        VfsNode original)
    {
        if (original.Metadata is string path && fileSystem.TryResolvePath(path, out var mapped))
        {
            return mapped;
        }

        if (validator.TryGetPath(original, out path) && fileSystem.TryResolvePath(path, out mapped))
        {
            return mapped;
        }

        throw new FileNotFoundException($"FAT上で編集対象を再解決できません: {original.Name}");
    }

    internal sealed class WritableFileSystemHandle(
        IReadOnlyFileSystem fileSystem,
        IFileContentWriter writer,
        IDisposable? disposable,
        IFileSystemEditor? editor = null,
        Func<VfsNode, VfsNode>? mapFile = null) : IDisposable
    {
        public IReadOnlyFileSystem FileSystem { get; } = fileSystem;
        public IFileContentWriter Writer { get; } = writer;
        public IFileSystemEditor? Editor { get; } = editor;

        public VfsNode MapFile(VfsNode original) => mapFile?.Invoke(original) ?? original;

        public void Dispose() => disposable?.Dispose();
    }
}
