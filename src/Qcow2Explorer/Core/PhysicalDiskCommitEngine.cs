using System.Security.Cryptography;

namespace Qcow2Explorer.Core;

internal sealed class PhysicalDiskCommitException : IOException
{
    public PhysicalDiskCommitException(
        string message,
        string recoveryJournalPath,
        bool rollbackSucceeded,
        Exception innerException)
        : base(message, innerException)
    {
        RecoveryJournalPath = recoveryJournalPath;
        RollbackSucceeded = rollbackSucceeded;
    }

    public string RecoveryJournalPath { get; }
    public bool RollbackSucceeded { get; }
}

internal static class PhysicalDiskCommitEngine
{
    public static void Apply(
        IBlockDevice target,
        PhysicalDiskTargetInfo targetInfo,
        IReadOnlyList<CopyOnWritePage> pages,
        string recoveryJournalPath,
        Action<IBlockReader>? finalVerifier = null,
        IProgress<DiskImageProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(targetInfo);
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentException.ThrowIfNullOrWhiteSpace(recoveryJournalPath);
        if (target.Length != targetInfo.Length)
        {
            throw new IOException("物理ディスクのサイズが確認時から変化しました。");
        }

        Preflight(target, pages, targetInfo.LogicalSectorSize, cancellationToken);
        PhysicalDiskRecoveryJournal.Create(recoveryJournalPath, targetInfo, pages);
        try
        {
            for (var index = 0; index < pages.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = pages[index];
                target.WriteAt(page.Offset, page.ModifiedData, 0, page.ModifiedData.Length);
                progress?.Report(new DiskImageProgress(
                    $"物理ディスクへ差分を書き込み ({index + 1:N0}/{pages.Count:N0})",
                    index + 1,
                    pages.Count));
            }

            target.Flush();
            VerifyPages(target, pages, useModifiedData: true, cancellationToken);
            finalVerifier?.Invoke(target);
            PhysicalDiskRecoveryJournal.SetState(recoveryJournalPath, PhysicalDiskRecoveryState.Committed);
        }
        catch (Exception ex)
        {
            try
            {
                RollBack(target, pages, progress);
                PhysicalDiskRecoveryJournal.SetState(recoveryJournalPath, PhysicalDiskRecoveryState.RolledBack);
            }
            catch (Exception rollbackException)
            {
                throw new PhysicalDiskCommitException(
                    $"物理ディスクへの書き込みに失敗し、自動復旧も完了できませんでした。復旧ジャーナルを保持してください: {recoveryJournalPath}",
                    recoveryJournalPath,
                    rollbackSucceeded: false,
                    new AggregateException(ex, rollbackException));
            }

            throw new PhysicalDiskCommitException(
                $"物理ディスクへの書き込みに失敗しましたが、変更前データへの自動復旧は完了しました。復旧ジャーナル: {recoveryJournalPath}",
                recoveryJournalPath,
                rollbackSucceeded: true,
                ex);
        }
    }

    public static void Restore(
        IBlockDevice target,
        PhysicalDiskTargetInfo targetInfo,
        string recoveryJournalPath,
        IProgress<DiskImageProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(targetInfo);
        var journal = PhysicalDiskRecoveryJournal.Read(recoveryJournalPath);
        ValidateTargetIdentity(journal.Target, targetInfo);
        var pages = journal.Pages
            .Select(page => new CopyOnWritePage(page.Offset, page.OriginalData, page.ModifiedData))
            .ToArray();
        PreflightRecovery(target, pages, targetInfo.LogicalSectorSize, cancellationToken);
        RollBack(target, pages, progress);
        PhysicalDiskRecoveryJournal.SetState(recoveryJournalPath, PhysicalDiskRecoveryState.RolledBack);
    }

    private static void Preflight(
        IBlockReader target,
        IReadOnlyList<CopyOnWritePage> pages,
        uint sectorSize,
        CancellationToken cancellationToken)
    {
        ValidateRanges(target, pages, sectorSize);
        var current = Array.Empty<byte>();
        foreach (var page in pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (current.Length != page.OriginalData.Length)
            {
                current = new byte[page.OriginalData.Length];
            }

            target.ReadAt(page.Offset, current, 0, current.Length);
            if (!CryptographicOperations.FixedTimeEquals(current, page.OriginalData))
            {
                throw new IOException(
                    $"仮編集後に物理ディスクの内容が変化しました。競合を検出したため書き込みを開始しません: 0x{page.Offset:X}");
            }
        }
    }

    private static void PreflightRecovery(
        IBlockReader target,
        IReadOnlyList<CopyOnWritePage> pages,
        uint sectorSize,
        CancellationToken cancellationToken)
    {
        ValidateRanges(target, pages, sectorSize);
        foreach (var page in pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = new byte[page.OriginalData.Length];
            target.ReadAt(page.Offset, current, 0, current.Length);
            for (var offset = 0; offset < current.Length; offset += checked((int)sectorSize))
            {
                var currentSector = current.AsSpan(offset, checked((int)sectorSize));
                if (!currentSector.SequenceEqual(page.OriginalData.AsSpan(offset, checked((int)sectorSize)))
                    && !currentSector.SequenceEqual(page.ModifiedData.AsSpan(offset, checked((int)sectorSize))))
                {
                    throw new IOException(
                        $"復旧対象セクターが変更前・変更後のどちらとも一致しません。別の書き込み後には復旧できません: 0x{page.Offset + offset:X}");
                }
            }
        }
    }

    private static void RollBack(
        IBlockDevice target,
        IReadOnlyList<CopyOnWritePage> pages,
        IProgress<DiskImageProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        for (var index = pages.Count - 1; index >= 0; index--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = pages[index];
            target.WriteAt(page.Offset, page.OriginalData, 0, page.OriginalData.Length);
            progress?.Report(new DiskImageProgress(
                $"物理ディスクを変更前へ復旧 ({pages.Count - index:N0}/{pages.Count:N0})",
                pages.Count - index,
                pages.Count));
        }

        target.Flush();
        VerifyPages(target, pages, useModifiedData: false, cancellationToken);
    }

    private static void VerifyPages(
        IBlockReader target,
        IReadOnlyList<CopyOnWritePage> pages,
        bool useModifiedData,
        CancellationToken cancellationToken)
    {
        foreach (var page in pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var expected = useModifiedData ? page.ModifiedData : page.OriginalData;
            var current = new byte[expected.Length];
            target.ReadAt(page.Offset, current, 0, current.Length);
            if (!CryptographicOperations.FixedTimeEquals(current, expected))
            {
                throw new IOException(
                    $"物理ディスクの読み戻し検証に失敗しました: 0x{page.Offset:X}");
            }
        }
    }

    private static void ValidateRanges(
        IBlockReader target,
        IReadOnlyList<CopyOnWritePage> pages,
        uint sectorSize)
    {
        if (pages.Count == 0)
        {
            throw new InvalidDataException("物理ディスクへ適用する実差分がありません。");
        }

        long previousEnd = 0;
        foreach (var page in pages.OrderBy(page => page.Offset))
        {
            if (page.OriginalData.Length == 0
                || page.OriginalData.Length != page.ModifiedData.Length
                || page.Offset < previousEnd
                || page.Offset % sectorSize != 0
                || page.OriginalData.Length % sectorSize != 0
                || page.Offset > target.Length - page.OriginalData.Length)
            {
                throw new InvalidDataException("物理ディスクの差分ページ範囲が不正です。");
            }

            previousEnd = checked(page.Offset + page.OriginalData.Length);
        }
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
            throw new IOException("復旧ジャーナルの物理ディスク識別情報が現在の対象と一致しません。");
        }
    }
}
