using System.Text;
using DiscUtils;
using DiscNtfsFileSystem = DiscUtils.Ntfs.NtfsFileSystem;

namespace Qcow2Explorer.Creation;

public sealed class ManagedNtfsFileSystemFormatter : IVirtualDiskFileSystemFormatter
{
    private static readonly byte[] ReadmeContent =
        Encoding.UTF8.GetBytes("Created by Virtual Disk Explorer. This file may be deleted.\n");

    public string Name => "DiscUtils NTFS";

    public bool Supports(VirtualDiskFileSystemKind fileSystem) =>
        fileSystem == VirtualDiskFileSystemKind.Ntfs;

    public ValueTask VerifyAvailableAsync(
        VirtualDiskFileSystemKind fileSystem,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureSupported(fileSystem);
        return ValueTask.CompletedTask;
    }

    public async Task FormatAsync(
        string imagePath,
        VirtualDiskPartitionLayout layout,
        IReadOnlyList<VirtualDiskInitialFile> initialFiles,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(initialFiles);
        EnsureSupported(layout.FileSystem);
        cancellationToken.ThrowIfCancellationRequested();

        await using var stream = new FileStream(
            imagePath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        if (stream.Length != layout.SizeBytes)
        {
            throw new InvalidDataException("NTFSフォーマット対象の容量がパーティション定義と一致しません。");
        }

        using var fileSystem = DiscNtfsFileSystem.Format(
            stream,
            layout.VolumeLabel,
            Geometry.FromCapacity(layout.SizeBytes, VirtualDiskPartitionTableWriter.SectorSize),
            firstSector: 0,
            sectorCount: layout.SizeBytes / VirtualDiskPartitionTableWriter.SectorSize);
        await WriteFileAsync(fileSystem, @"\VDT-README.txt", ReadmeContent, cancellationToken);
        foreach (var initialFile in initialFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var source = new FileStream(
                initialFile.SourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var destination = fileSystem.OpenFile(
                @"\" + initialFile.DestinationName,
                FileMode.CreateNew,
                FileAccess.ReadWrite);
            await source.CopyToAsync(destination, 1024 * 1024, cancellationToken);
            await destination.FlushAsync(cancellationToken);
        }

        fileSystem.UpdateBiosGeometry(Geometry.FromCapacity(
            layout.SizeBytes,
            VirtualDiskPartitionTableWriter.SectorSize));
        stream.Flush(flushToDisk: true);
    }

    private static async Task WriteFileAsync(
        DiscNtfsFileSystem fileSystem,
        string path,
        byte[] content,
        CancellationToken cancellationToken)
    {
        await using var destination = fileSystem.OpenFile(path, FileMode.CreateNew, FileAccess.ReadWrite);
        await destination.WriteAsync(content, cancellationToken);
        await destination.FlushAsync(cancellationToken);
    }

    private static void EnsureSupported(VirtualDiskFileSystemKind fileSystem)
    {
        if (fileSystem != VirtualDiskFileSystemKind.Ntfs)
        {
            throw new NotSupportedException(
                $"DiscUtils NTFSフォーマッターは{VirtualDiskPartitionTableWriter.GetDisplayName(fileSystem)}に対応していません。");
        }
    }
}
