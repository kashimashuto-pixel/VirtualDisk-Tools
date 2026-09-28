namespace Qcow2Explorer.Core;

internal sealed record PreparedPhysicalDiskRecoveryJournal(
    string Path,
    DateTime CreatedUtc,
    PhysicalDiskTargetInfo Target);

internal sealed record UnreadablePhysicalDiskRecoveryJournal(
    string Path,
    string Error);

internal sealed record PhysicalDiskRecoveryJournalScanResult(
    IReadOnlyList<PreparedPhysicalDiskRecoveryJournal> Prepared,
    IReadOnlyList<UnreadablePhysicalDiskRecoveryJournal> Unreadable);

internal static class PhysicalDiskRecoveryJournalLocator
{
    public static string GetDefaultDirectory()
    {
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
        {
            throw new InvalidOperationException("復旧ジャーナル用のローカル保存先を取得できません。");
        }

        return Path.Combine(localData, "VirtualDiskTools", "Recovery");
    }

    public static PhysicalDiskRecoveryJournalScanResult Scan(
        string? directory = null)
    {
        directory = Path.GetFullPath(directory ?? GetDefaultDirectory());
        if (!Directory.Exists(directory))
        {
            return new PhysicalDiskRecoveryJournalScanResult(
                Array.Empty<PreparedPhysicalDiskRecoveryJournal>(),
                Array.Empty<UnreadablePhysicalDiskRecoveryJournal>());
        }

        var results = new List<PreparedPhysicalDiskRecoveryJournal>();
        var unreadable = new List<UnreadablePhysicalDiskRecoveryJournal>();
        foreach (var path in Directory.EnumerateFiles(
                     directory,
                     "*.vdt-recovery",
                     SearchOption.TopDirectoryOnly))
        {
            try
            {
                var journal = PhysicalDiskRecoveryJournal.Read(path);
                if (journal.State == PhysicalDiskRecoveryState.Prepared)
                {
                    results.Add(new PreparedPhysicalDiskRecoveryJournal(
                        Path.GetFullPath(path),
                        journal.CreatedUtc,
                        journal.Target));
                }
            }
            catch (Exception ex) when (ex is IOException
                                       or InvalidDataException
                                       or UnauthorizedAccessException
                                       or ArgumentException
                                       or OverflowException)
            {
                unreadable.Add(new UnreadablePhysicalDiskRecoveryJournal(
                    Path.GetFullPath(path),
                    ex.Message));
            }
        }

        return new PhysicalDiskRecoveryJournalScanResult(
            results.OrderByDescending(result => result.CreatedUtc).ToArray(),
            unreadable.OrderBy(result => result.Path, StringComparer.OrdinalIgnoreCase).ToArray());
    }
}
