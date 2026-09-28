using System.Text;
using Qcow2Explorer.Core;
using Qcow2Explorer.FileSystems;
using Qcow2Explorer.Partitions;

namespace Qcow2Explorer.Creation;

public static class VirtualDiskCreationService
{
    private const int CopyBufferSize = 4 * 1024 * 1024;

    public static Task<VirtualDiskCreationResult> CreateAsync(
        VirtualDiskCreationRequest request,
        IProgress<DiskImageProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        CreateAsync(request, VirtualDiskFormatterRegistry.ManagedOnly, progress, cancellationToken);

    public static async Task<VirtualDiskCreationResult> CreateAsync(
        VirtualDiskCreationRequest request,
        VirtualDiskFormatterRegistry formatters,
        IProgress<DiskImageProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(formatters);

        var destinationPath = ValidateRequest(request);
        var layouts = VirtualDiskPartitionTableWriter.Plan(
            request.CapacityBytes,
            request.PartitionTable,
            request.Partitions);
        var destinationDirectory = Path.GetDirectoryName(destinationPath)
            ?? throw new ArgumentException("出力先フォルダーを取得できません。", nameof(request));
        Directory.CreateDirectory(destinationDirectory);
        var rawBuildPath = Path.Combine(
            destinationDirectory,
            $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.build.raw");
        var completed = false;
        var destinationCreatedByThisOperation = false;
        try
        {
            await formatters.VerifyAvailableAsync(
                request.Partitions.Select(partition => partition.FileSystem).Distinct(),
                cancellationToken);
            await using (var raw = new FileStream(
                rawBuildPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                CopyBufferSize,
                FileOptions.Asynchronous | FileOptions.RandomAccess))
            {
                raw.SetLength(request.CapacityBytes);
                VirtualDiskPartitionTableWriter.Write(
                    raw,
                    request.CapacityBytes,
                    request.PartitionTable,
                    layouts);
                for (var index = 0; index < layouts.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var layout = layouts[index];
                    var partitionImagePath = Path.Combine(
                        destinationDirectory,
                        $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.partition");
                    try
                    {
                        var fileSystemName = VirtualDiskPartitionTableWriter.GetDisplayName(layout.FileSystem);
                        progress?.Report(new DiskImageProgress(
                            $"{fileSystemName}を初期化 ({index + 1:N0}/{layouts.Count:N0}): {layout.VolumeLabel}"));
                        await CreatePartitionImageAsync(
                            partitionImagePath,
                            layout,
                            formatters.Resolve(layout.FileSystem),
                            request.InitialFiles?
                                .Where(file => file.PartitionNumber == layout.Number)
                                .ToArray()
                                ?? [],
                            cancellationToken);
                        await CopyPartitionAsync(
                            raw,
                            partitionImagePath,
                            layout,
                            index,
                            layouts.Count,
                            progress,
                            cancellationToken);
                    }
                    finally
                    {
                        TryDelete(partitionImagePath);
                    }
                }

                await raw.FlushAsync(cancellationToken);
            }

            progress?.Report(new DiskImageProgress("RAW buildの整合性を検証中..."));
            cancellationToken.ThrowIfCancellationRequested();
            VerifyRawBuild(
                rawBuildPath,
                request.PartitionTable,
                layouts,
                request.InitialFiles ?? [],
                progress,
                cancellationToken);
            if (request.ContainerFormat == VirtualDiskContainerFormat.Raw)
            {
                File.Move(rawBuildPath, destinationPath);
                destinationCreatedByThisOperation = true;
            }
            else
            {
                await Qcow2SparseWriter.WriteFromRawAsync(
                    rawBuildPath,
                    destinationPath,
                    progress,
                    cancellationToken);
                destinationCreatedByThisOperation = true;
                TryDelete(rawBuildPath);
            }

            progress?.Report(new DiskImageProgress("最終イメージの整合性を検証中..."));
            cancellationToken.ThrowIfCancellationRequested();
            VerifyFinalImage(destinationPath, request, layouts, progress, cancellationToken);
            completed = true;
            progress?.Report(new DiskImageProgress("仮想ディスクを作成しました", request.CapacityBytes, request.CapacityBytes));
            return new VirtualDiskCreationResult(
                destinationPath,
                request.CapacityBytes,
                request.ContainerFormat,
                request.PartitionTable,
                layouts);
        }
        finally
        {
            TryDelete(rawBuildPath);
            if (!completed && destinationCreatedByThisOperation)
            {
                TryDelete(destinationPath);
            }
        }
    }

    private static string ValidateRequest(VirtualDiskCreationRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DestinationPath);
        ArgumentNullException.ThrowIfNull(request.Partitions);
        if (!Enum.IsDefined(request.ContainerFormat))
        {
            throw new ArgumentOutOfRangeException(nameof(request), "未対応の仮想ディスク形式です。");
        }

        if (!Enum.IsDefined(request.PartitionTable))
        {
            throw new ArgumentOutOfRangeException(nameof(request), "未対応のパーティション表形式です。");
        }
        var destinationPath = Path.GetFullPath(request.DestinationPath);
        if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
        {
            throw new IOException($"出力先は既に存在します: {destinationPath}");
        }

        var extension = Path.GetExtension(destinationPath);
        if (request.ContainerFormat == VirtualDiskContainerFormat.Qcow2
            && !extension.Equals(".qcow2", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".qcow", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("QCOW2出力には.qcow2または.qcow拡張子を指定してください。", nameof(request));
        }

        if (request.ContainerFormat == VirtualDiskContainerFormat.Raw
            && extension.Equals(".qcow2", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("RAW出力に.qcow2拡張子は使用できません。", nameof(request));
        }

        var initialFiles = request.InitialFiles ?? [];
        foreach (var initialFile in initialFiles)
        {
            if (initialFile.PartitionNumber < 1 || initialFile.PartitionNumber > request.Partitions.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(request), "初期ファイルのパーティション番号が範囲外です。");
            }

            var sourcePath = Path.GetFullPath(initialFile.SourcePath);
            if (!File.Exists(sourcePath))
            {
                throw new FileNotFoundException("初期配置するファイルが見つかりません。", sourcePath);
            }

            ValidateInitialFileName(
                initialFile.DestinationName,
                request.Partitions[initialFile.PartitionNumber - 1].FileSystem);
        }

        foreach (var group in initialFiles.GroupBy(file => file.PartitionNumber))
        {
            var fileSystem = request.Partitions[group.Key - 1].FileSystem;
            var nameComparer = fileSystem == VirtualDiskFileSystemKind.Ntfs
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;
            var duplicate = group
                .GroupBy(file => file.DestinationName, nameComparer)
                .FirstOrDefault(names => names.Count() > 1);
            if (duplicate is not null)
            {
                throw new ArgumentException(
                    $"パーティション#{group.Key}の初期ファイル名が重複しています: {duplicate.Key}",
                    nameof(request));
            }

            if (group.Any(file => string.Equals(file.DestinationName, "VDT-README.txt", StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException("VDT-README.txtは初期案内ファイル用の予約名です。", nameof(request));
            }
        }

        return destinationPath;
    }

    private static async Task CreatePartitionImageAsync(
        string path,
        VirtualDiskPartitionLayout layout,
        IVirtualDiskFileSystemFormatter formatter,
        IReadOnlyList<VirtualDiskInitialFile> initialFiles,
        CancellationToken cancellationToken)
    {
        await using (var file = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.ReadWrite,
            4096,
            FileOptions.Asynchronous | FileOptions.RandomAccess))
        {
            file.SetLength(layout.SizeBytes);
            await file.FlushAsync(cancellationToken);
        }

        await formatter.FormatAsync(path, layout, initialFiles, cancellationToken);
    }

    private static async Task CopyPartitionAsync(
        FileStream raw,
        string partitionImagePath,
        VirtualDiskPartitionLayout layout,
        int partitionIndex,
        int partitionCount,
        IProgress<DiskImageProgress>? progress,
        CancellationToken cancellationToken)
    {
        await using var partition = new FileStream(
            partitionImagePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (partition.Length != layout.SizeBytes)
        {
            throw new InvalidDataException("フォーマット出力の容量が指定したパーティション容量と一致しません。");
        }

        var buffer = new byte[CopyBufferSize];
        long copied = 0;
        while (copied < partition.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = checked((int)Math.Min(buffer.Length, partition.Length - copied));
            await partition.ReadExactlyAsync(buffer.AsMemory(0, count), cancellationToken);
            await RandomAccess.WriteAsync(
                raw.SafeFileHandle,
                buffer.AsMemory(0, count),
                checked(layout.OffsetBytes + copied),
                cancellationToken);
            copied += count;
            var fileSystemName = VirtualDiskPartitionTableWriter.GetDisplayName(layout.FileSystem);
            progress?.Report(new DiskImageProgress(
                $"{fileSystemName}パーティションを書き込み ({partitionIndex + 1:N0}/{partitionCount:N0})",
                copied,
                partition.Length));
        }
    }

    private static void VerifyRawBuild(
        string rawPath,
        VirtualDiskPartitionTableKind tableKind,
        IReadOnlyList<VirtualDiskPartitionLayout> expectedLayouts,
        IReadOnlyList<VirtualDiskInitialFile> initialFiles,
        IProgress<DiskImageProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var reader = new RawDiskImageReader(rawPath);
        VerifyReader(reader, tableKind, expectedLayouts, initialFiles, progress, cancellationToken);
    }

    private static void VerifyFinalImage(
        string path,
        VirtualDiskCreationRequest request,
        IReadOnlyList<VirtualDiskPartitionLayout> expectedLayouts,
        IProgress<DiskImageProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var reader = DiskImageReaderFactory.Open(path, cancellationToken: cancellationToken);
        if (reader.Length != request.CapacityBytes)
        {
            throw new InvalidDataException("作成後の仮想ディスク容量が指定値と一致しません。");
        }

        VerifyReader(
            reader,
            request.PartitionTable,
            expectedLayouts,
            request.InitialFiles ?? [],
            progress,
            cancellationToken);
    }

    private static void VerifyReader(
        IDiskImageReader reader,
        VirtualDiskPartitionTableKind tableKind,
        IReadOnlyList<VirtualDiskPartitionLayout> expectedLayouts,
        IReadOnlyList<VirtualDiskInitialFile> initialFiles,
        IProgress<DiskImageProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var partitions = PartitionTableReader.ReadPartitions(reader, cancellationToken);
        if (partitions.Count != expectedLayouts.Count)
        {
            throw new InvalidDataException("作成後のパーティション数が指定値と一致しません。");
        }

        for (var index = 0; index < partitions.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var actual = partitions[index];
            var expected = expectedLayouts[index];
            var expectedScheme = tableKind == VirtualDiskPartitionTableKind.Gpt ? "GPT" : "MBR";
            if (!actual.Scheme.Equals(expectedScheme, StringComparison.OrdinalIgnoreCase)
                || actual.StartOffset != expected.OffsetBytes
                || actual.LengthBytes != expected.SizeBytes)
            {
                throw new InvalidDataException($"作成後のパーティション#{index + 1}レイアウトが指定値と一致しません。");
            }

            actual.FileSystem = FileSystemDetector.Detect(reader, actual, cancellationToken);
            var expectedFileSystemName = VirtualDiskPartitionTableWriter.GetDisplayName(expected.FileSystem);
            if (!actual.FileSystem.Equals(expectedFileSystemName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"作成後のパーティション#{index + 1}を{expectedFileSystemName}として検出できません。"
                    + $" 検出結果: {actual.FileSystem}");
            }

            var readable = FileSystemDetector.TryOpen(reader, actual, out var error, cancellationToken);
            if (readable is null)
            {
                throw new InvalidDataException(
                    $"作成後の{expectedFileSystemName}パーティション#{index + 1}を開けません: {error}");
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entries = readable.ListDirectory(readable.Root);
                VerifyInitialFiles(
                    readable,
                    entries,
                    index + 1,
                    expected.FileSystem,
                    initialFiles.Where(file => file.PartitionNumber == index + 1),
                    progress,
                    cancellationToken);
                var readmeComparison = expected.FileSystem == VirtualDiskFileSystemKind.Ntfs
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal;
                if (!entries.Any(entry =>
                        !entry.IsDirectory
                        && string.Equals(entry.Name, "VDT-README.txt", readmeComparison)))
                {
                    throw new InvalidDataException(
                        $"作成後のパーティション#{index + 1}に初期案内ファイルがありません。");
                }

                if (!FileEditBatchService.CanEdit(reader, actual, readable, out var reason))
                {
                    throw new NotSupportedException(
                        $"作成後の{expectedFileSystemName}パーティション#{index + 1}は"
                        + $"編集可能レイアウトではありません: {reason}");
                }
            }
            finally
            {
                (readable as IDisposable)?.Dispose();
            }
        }
    }

    private static void VerifyInitialFiles(
        IReadOnlyFileSystem fileSystem,
        IReadOnlyList<VfsNode> rootEntries,
        int partitionNumber,
        VirtualDiskFileSystemKind fileSystemKind,
        IEnumerable<VirtualDiskInitialFile> initialFiles,
        IProgress<DiskImageProgress>? progress,
        CancellationToken cancellationToken)
    {
        const int bufferSize = 1024 * 1024;
        var expectedBuffer = new byte[bufferSize];
        foreach (var initialFile in initialFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var comparison = fileSystemKind == VirtualDiskFileSystemKind.Ntfs
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            var node = rootEntries.SingleOrDefault(entry =>
                string.Equals(entry.Name, initialFile.DestinationName, comparison));
            if (node is null || node.IsDirectory)
            {
                throw new InvalidDataException(
                    $"作成後のパーティション#{partitionNumber}に初期ファイルがありません: {initialFile.DestinationName}");
            }

            using var expected = new FileStream(
                initialFile.SourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize,
                FileOptions.SequentialScan);
            if (node.Size != expected.Length)
            {
                throw new InvalidDataException(
                    $"初期ファイルのサイズが一致しません: {initialFile.DestinationName}");
            }

            long offset = 0;
            while (offset < expected.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = checked((int)Math.Min(expectedBuffer.Length, expected.Length - offset));
                expected.ReadExactly(expectedBuffer.AsSpan(0, count));
                var actual = fileSystem.ReadFile(node, offset, count);
                if (actual.Length != count
                    || !actual.AsSpan().SequenceEqual(expectedBuffer.AsSpan(0, count)))
                {
                    throw new InvalidDataException(
                        $"初期ファイルの内容が一致しません: {initialFile.DestinationName} offset=0x{offset:X}");
                }

                offset += count;
                progress?.Report(new DiskImageProgress(
                    $"初期ファイルを読み戻し検証中: {initialFile.DestinationName}",
                    offset,
                    expected.Length));
            }
        }
    }

    private static void ValidateInitialFileName(string name, VirtualDiskFileSystemKind fileSystem)
    {
        if (string.IsNullOrWhiteSpace(name)
            || name is "." or ".."
            || name.IndexOfAny(['/', '\\', '\0']) >= 0
            || name.Any(char.IsControl)
            || Encoding.UTF8.GetByteCount(name) > 255)
        {
            throw new ArgumentException($"初期ファイル名が不正です: {name}", nameof(name));
        }

        if (fileSystem == VirtualDiskFileSystemKind.Ntfs)
        {
            ReadOnlySpan<char> invalid = ['"', '*', ':', '<', '>', '?', '|'];
            var baseName = name.Split('.')[0];
            var reserved = baseName.Equals("CON", StringComparison.OrdinalIgnoreCase)
                || baseName.Equals("PRN", StringComparison.OrdinalIgnoreCase)
                || baseName.Equals("AUX", StringComparison.OrdinalIgnoreCase)
                || baseName.Equals("NUL", StringComparison.OrdinalIgnoreCase)
                || (baseName.Length == 4
                    && (baseName.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                        || baseName.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                    && baseName[3] is >= '1' and <= '9');
            if (name.AsSpan().IndexOfAny(invalid) >= 0
                || name.EndsWith(' ')
                || name.EndsWith('.')
                || reserved)
            {
                throw new ArgumentException($"NTFSで使用できない初期ファイル名です: {name}", nameof(name));
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Preserve the original creation/verification failure.
        }
    }

}
