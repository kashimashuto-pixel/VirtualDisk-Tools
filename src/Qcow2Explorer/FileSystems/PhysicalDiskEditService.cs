using Qcow2Explorer.Core;
using Qcow2Explorer.Partitions;

namespace Qcow2Explorer.FileSystems;

public sealed record PhysicalDiskEditResult(
    PhysicalDiskTargetInfo Target,
    string RecoveryJournalPath,
    int EditCount,
    int ModifiedPageCount,
    long ModifiedBytes,
    IReadOnlyList<FileEditResult> Edits);

public static class PhysicalDiskEditService
{
    public static bool CanApply(
        IDiskImageReader source,
        PartitionInfo partition,
        IReadOnlyFileSystem fileSystem,
        out PhysicalDiskTargetInfo? target,
        out string reason)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(partition);
        ArgumentNullException.ThrowIfNull(fileSystem);
        target = null;
        if (!PhysicalDiskReader.IsPhysicalDiskPath(source.Path))
        {
            reason = "物理ディスクから開いたファイルシステムではありません。";
            return false;
        }

        if (partition.ReaderOverride is not null)
        {
            reason = "RAID、LVM、BitLocker／LUKS復号レイヤーは物理構成へ直接書き戻せません。平坦化した論理RAWへ保存してください。";
            return false;
        }

        try
        {
            target = PhysicalDiskWriteSession.Inspect(source.Path);
            if (target.IsSystemDisk)
            {
                reason = "実行中Windowsのシステムディスクは対象外です。別のWindows環境からオフラインディスクとして開いてください。";
                return false;
            }

            if (string.IsNullOrWhiteSpace(target.SerialNumber)
                && string.IsNullOrWhiteSpace(target.IdentityToken))
            {
                reason = "この物理ディスクはシリアル番号またはStorage Device IDを取得できないため、安全に再識別できず書き込み対象にできません。";
                return false;
            }

            if (source.Length != target.Length)
            {
                reason = "読取時と現在の物理ディスクサイズが一致しません。";
                return false;
            }

            if (partition.StartOffset < 0
                || partition.LengthBytes <= 0
                || partition.StartOffset > target.Length - partition.LengthBytes)
            {
                reason = "編集対象パーティションが物理ディスク範囲内にありません。";
                return false;
            }

            if (!FileEditBatchService.CanEdit(source, partition, fileSystem, out reason))
            {
                return false;
            }

            reason = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is IOException
                                   or InvalidDataException
                                   or UnauthorizedAccessException
                                   or NotSupportedException
                                   or ArgumentException
                                   or OverflowException)
        {
            reason = ex.Message;
            target = null;
            return false;
        }
    }

    public static string GetDefaultRecoveryJournalPath(PhysicalDiskTargetInfo target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
        {
            throw new InvalidOperationException("復旧ジャーナル用のローカル保存先を取得できません。");
        }

        return Path.Combine(
            localData,
            "VirtualDiskTools",
            "Recovery",
            $"PhysicalDrive{target.DiskNumber}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.vdt-recovery");
    }

    public static async Task<PhysicalDiskEditResult> ApplyAsync(
        IDiskImageReader source,
        PartitionInfo partition,
        IReadOnlyFileSystem originalFileSystem,
        IReadOnlyList<PendingFileEdit> edits,
        PhysicalDiskTargetInfo expectedTarget,
        string confirmationText,
        string recoveryJournalPath,
        IProgress<DiskImageProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(partition);
        ArgumentNullException.ThrowIfNull(originalFileSystem);
        ArgumentNullException.ThrowIfNull(edits);
        ArgumentNullException.ThrowIfNull(expectedTarget);
        ArgumentException.ThrowIfNullOrWhiteSpace(recoveryJournalPath);
        if (!string.Equals(confirmationText, expectedTarget.ConfirmationText, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("物理ディスク書き込みの確認文字列が一致しません。");
        }

        if (!CanApply(source, partition, originalFileSystem, out var currentTarget, out var reason)
            || currentTarget is null)
        {
            throw new NotSupportedException(reason);
        }

        ValidateTargetIdentity(expectedTarget, currentTarget);
        recoveryJournalPath = Path.GetFullPath(recoveryJournalPath);
        var recoveryDirectory = Path.GetDirectoryName(recoveryJournalPath)
            ?? throw new ArgumentException("復旧ジャーナルの保存先フォルダーを取得できません。", nameof(recoveryJournalPath));
        Directory.CreateDirectory(recoveryDirectory);
        if (File.Exists(recoveryJournalPath) || Directory.Exists(recoveryJournalPath))
        {
            throw new IOException($"復旧ジャーナルの保存先は既に存在します: {recoveryJournalPath}");
        }

        if (PhysicalDiskWriteSession.IsPathOnDisk(recoveryJournalPath, currentTarget.DiskNumber))
        {
            throw new IOException("復旧ジャーナルを書き込み対象の物理ディスク上には保存できません。");
        }

        var staged = await StageContentFilesAsync(edits, recoveryDirectory, progress, cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new DiskImageProgress("物理ディスク上の全ボリュームを排他ロック"));
            using var session = PhysicalDiskWriteSession.Open(currentTarget);
            progress?.Report(new DiskImageProgress("ロック済み物理ディスク上で変更を仮適用・検証"));
            var prepared = await FileEditBatchService.PrepareAsync(
                session,
                partition,
                originalFileSystem,
                staged.Edits,
                currentTarget.DevicePath,
                progress,
                cancellationToken);
            if (prepared.IsLogicalVolumeOutput)
            {
                throw new NotSupportedException("合成・復号レイヤーは物理ディスクへ直接適用できません。");
            }

            var pages = prepared.Overlay.GetModifiedPages();
            if (pages.Count == 0)
            {
                throw new InvalidOperationException("物理ディスクへ適用する実差分がありません。");
            }

            PhysicalDiskCommitEngine.Apply(
                session,
                session.Target,
                pages,
                recoveryJournalPath,
                finalVerifier: reader => VerifyFinalFileSystem(
                    reader,
                    prepared.EffectivePartition,
                    prepared.FileSystemName,
                    prepared.Results,
                    cancellationToken),
                progress,
                cancellationToken);
            session.RefreshDiskProperties();
            return new PhysicalDiskEditResult(
                session.Target,
                recoveryJournalPath,
                prepared.Results.Count,
                pages.Count,
                pages.Sum(page => (long)page.ModifiedData.Length),
                prepared.Results);
        }
        finally
        {
            TryDeleteStagingDirectory(staged.DirectoryPath, recoveryDirectory);
        }
    }

    public static Task RestoreAsync(
        string recoveryJournalPath,
        string confirmationText,
        IProgress<DiskImageProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recoveryJournalPath);
        recoveryJournalPath = Path.GetFullPath(recoveryJournalPath);
        var journal = PhysicalDiskRecoveryJournal.Read(recoveryJournalPath);
        var currentTarget = PhysicalDiskWriteSession.Inspect(journal.Target.DevicePath);
        ValidateTargetIdentity(journal.Target, currentTarget);
        if (!string.Equals(confirmationText, currentTarget.ConfirmationText, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("物理ディスク復旧の確認文字列が一致しません。");
        }

        if (PhysicalDiskWriteSession.IsPathOnDisk(recoveryJournalPath, currentTarget.DiskNumber))
        {
            throw new IOException("復旧ジャーナルが復旧対象の物理ディスク上にあるため使用できません。");
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var session = PhysicalDiskWriteSession.Open(currentTarget);
        PhysicalDiskCommitEngine.Restore(
            session,
            session.Target,
            recoveryJournalPath,
            progress,
            cancellationToken);
        session.RefreshDiskProperties();
        return Task.CompletedTask;
    }

    private static void VerifyFinalFileSystem(
        IBlockReader reader,
        PartitionInfo partition,
        string expectedFileSystemName,
        IReadOnlyList<FileEditResult> results,
        CancellationToken cancellationToken)
    {
        var fileSystem = FileSystemDetector.TryOpen(reader, partition, out var error)
            ?? throw new InvalidDataException($"書き込み後のファイルシステムを再オープンできません: {error}");
        if (!string.Equals(fileSystem.Name, expectedFileSystemName, StringComparison.Ordinal))
        {
            (fileSystem as IDisposable)?.Dispose();
            throw new InvalidDataException(
                $"書き込み後のファイルシステム形式が変化しました: {fileSystem.Name}");
        }

        FileEditBatchService.VerifyFinalFileSystem(fileSystem, results, cancellationToken);
    }

    private static void ValidateTargetIdentity(
        PhysicalDiskTargetInfo expected,
        PhysicalDiskTargetInfo actual)
    {
        if (expected.DiskNumber != actual.DiskNumber
            || !string.Equals(expected.DevicePath, actual.DevicePath, StringComparison.OrdinalIgnoreCase)
            || expected.Length != actual.Length
            || expected.LogicalSectorSize != actual.LogicalSectorSize
            || expected.IsRemovable != actual.IsRemovable
            || (!string.IsNullOrWhiteSpace(expected.Model)
                && !string.Equals(expected.Model, actual.Model, StringComparison.Ordinal))
            || (!string.IsNullOrWhiteSpace(expected.BusType)
                && !string.Equals(expected.BusType, actual.BusType, StringComparison.Ordinal))
            || (!string.IsNullOrWhiteSpace(expected.SerialNumber)
                && !string.Equals(expected.SerialNumber, actual.SerialNumber, StringComparison.Ordinal))
            || (!string.IsNullOrWhiteSpace(expected.IdentityToken)
                && !string.Equals(expected.IdentityToken, actual.IdentityToken, StringComparison.Ordinal)))
        {
            throw new IOException("物理ディスクの識別情報が確認時から変化しました。誤書き込み防止のため中止します。");
        }
    }

    private static async Task<StagedEdits> StageContentFilesAsync(
        IReadOnlyList<PendingFileEdit> edits,
        string recoveryDirectory,
        IProgress<DiskImageProgress>? progress,
        CancellationToken cancellationToken)
    {
        var contentEdits = edits
            .Select((edit, index) => (Edit: edit, Index: index))
            .Where(item => item.Edit.ContentPath is not null)
            .ToArray();
        if (contentEdits.Length == 0)
        {
            return new StagedEdits(null, edits.ToArray());
        }

        var stagingDirectory = Path.Combine(
            recoveryDirectory,
            $".vdt-physical-staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDirectory);
        var stagedEdits = edits.ToArray();
        try
        {
            for (var itemIndex = 0; itemIndex < contentEdits.Length; itemIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = contentEdits[itemIndex];
                var sourcePath = Path.GetFullPath(item.Edit.ContentPath!);
                var destinationPath = Path.Combine(stagingDirectory, $"{itemIndex:D6}.bin");
                progress?.Report(new DiskImageProgress(
                    $"書き込み内容を安全な一時領域へ退避 ({itemIndex + 1:N0}/{contentEdits.Length:N0})"));
                await using var input = new FileStream(
                    sourcePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    1024 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using var output = new FileStream(
                    destinationPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    1024 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await input.CopyToAsync(output, 1024 * 1024, cancellationToken);
                await output.FlushAsync(cancellationToken);
                output.Flush(flushToDisk: true);
                stagedEdits[item.Index] = item.Edit with { ContentPath = destinationPath };
            }

            return new StagedEdits(stagingDirectory, stagedEdits);
        }
        catch
        {
            TryDeleteStagingDirectory(stagingDirectory, recoveryDirectory);
            throw;
        }
    }

    private static void TryDeleteStagingDirectory(string? stagingDirectory, string recoveryDirectory)
    {
        if (string.IsNullOrWhiteSpace(stagingDirectory))
        {
            return;
        }

        var fullStagingPath = Path.GetFullPath(stagingDirectory);
        var fullRecoveryPath = Path.GetFullPath(recoveryDirectory).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!fullStagingPath.StartsWith(fullRecoveryPath, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(fullStagingPath).StartsWith(".vdt-physical-staging-", StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            Directory.Delete(fullStagingPath, recursive: true);
        }
        catch
        {
            // The staged copy contains only user-provided replacement bytes and can be removed manually.
        }
    }

    private sealed record StagedEdits(string? DirectoryPath, IReadOnlyList<PendingFileEdit> Edits);
}
