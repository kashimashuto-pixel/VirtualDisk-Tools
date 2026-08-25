using Qcow2Explorer.Core;
using Qcow2Explorer.Partitions;

namespace Qcow2Explorer.FileSystems;

public static class BtrfsDeviceSet
{
    public static IReadOnlyList<BtrfsDevicePartition> Discover(
        IReadOnlyList<IBlockReader> disks,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(disks);
        var devices = new List<BtrfsDevicePartition>();
        foreach (var disk in disks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var partitions = PartitionTableReader.ReadPartitions(disk, cancellationToken).ToList();
            if (partitions.Count == 0 && disk.Length >= 512)
            {
                partitions.Add(CreateWholeDiskPartition(disk));
            }

            foreach (var partition in partitions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                partition.FileSystem = FileSystemDetector.Detect(disk, partition, cancellationToken);
                if (!string.Equals(partition.FileSystem, "Btrfs", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var slice = new PartitionSliceReader(disk, partition);
                devices.Add(new BtrfsDevicePartition(
                    disk,
                    partition,
                    BtrfsFileSystem.ReadDeviceIdentity(slice)));
            }
        }

        return devices;
    }

    public static BtrfsFileSystem? TryOpen(
        IBlockReader primaryDisk,
        PartitionInfo primaryPartition,
        IReadOnlyList<BtrfsDevicePartition> devices,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(primaryDisk);
        ArgumentNullException.ThrowIfNull(primaryPartition);
        ArgumentNullException.ThrowIfNull(devices);
        error = "";
        try
        {
            var primarySlice = new PartitionSliceReader(primaryDisk, primaryPartition);
            var primaryIdentity = BtrfsFileSystem.ReadDeviceIdentity(primarySlice);
            var matching = new List<IBlockReader> { primarySlice };
            matching.AddRange(devices
                .Where(item => string.Equals(
                    item.Identity.FileSystemId,
                    primaryIdentity.FileSystemId,
                    StringComparison.OrdinalIgnoreCase))
                .Where(item => !(ReferenceEquals(item.Disk, primaryDisk)
                    && item.Partition.StartOffset == primaryPartition.StartOffset
                    && item.Partition.LengthBytes == primaryPartition.LengthBytes))
                .OrderBy(item => item.Identity.DeviceId)
                .Select(item => (IBlockReader)new PartitionSliceReader(item.Disk, item.Partition)));

            return new BtrfsFileSystem(matching, primaryPartition);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException)
        {
            error = ex.Message;
            return null;
        }
    }

    private static PartitionInfo CreateWholeDiskPartition(IBlockReader disk)
    {
        return new PartitionInfo
        {
            Number = 1,
            Scheme = "WholeDisk",
            Name = "Whole disk",
            Type = "Unpartitioned",
            TypeId = "",
            StartLba = 0,
            SectorCount = checked((ulong)(disk.Length / 512))
        };
    }
}

public sealed record BtrfsDevicePartition(
    IBlockReader Disk,
    PartitionInfo Partition,
    BtrfsDeviceIdentity Identity);
