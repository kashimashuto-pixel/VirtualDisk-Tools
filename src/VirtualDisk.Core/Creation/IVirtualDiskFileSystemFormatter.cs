namespace Qcow2Explorer.Creation;

public interface IVirtualDiskFileSystemFormatter
{
    string Name { get; }

    bool Supports(VirtualDiskFileSystemKind fileSystem);

    ValueTask VerifyAvailableAsync(
        VirtualDiskFileSystemKind fileSystem,
        CancellationToken cancellationToken = default);

    Task FormatAsync(
        string imagePath,
        VirtualDiskPartitionLayout layout,
        IReadOnlyList<VirtualDiskInitialFile> initialFiles,
        CancellationToken cancellationToken = default);
}

public sealed class VirtualDiskFormatterRegistry
{
    private readonly IReadOnlyList<IVirtualDiskFileSystemFormatter> _formatters;

    public VirtualDiskFormatterRegistry(IEnumerable<IVirtualDiskFileSystemFormatter> formatters)
    {
        ArgumentNullException.ThrowIfNull(formatters);
        _formatters = formatters.ToArray();
        if (_formatters.Count == 0)
        {
            throw new ArgumentException("少なくとも1つのフォーマッターが必要です。", nameof(formatters));
        }
    }

    public static VirtualDiskFormatterRegistry ManagedOnly { get; } = new(
        [
            new ManagedNtfsFileSystemFormatter(),
            new ManagedExt4FileSystemFormatter(),
            new ManagedXfsFileSystemFormatter(),
        ]);

    public IVirtualDiskFileSystemFormatter Resolve(VirtualDiskFileSystemKind fileSystem)
    {
        var formatter = _formatters.FirstOrDefault(candidate => candidate.Supports(fileSystem));
        return formatter ?? throw new NotSupportedException(
            $"{VirtualDiskPartitionTableWriter.GetDisplayName(fileSystem)}を作成できるフォーマッターが登録されていません。");
    }

    public async Task VerifyAvailableAsync(
        IEnumerable<VirtualDiskFileSystemKind> fileSystems,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileSystems);
        foreach (var fileSystem in fileSystems.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Resolve(fileSystem).VerifyAvailableAsync(fileSystem, cancellationToken);
        }
    }
}
