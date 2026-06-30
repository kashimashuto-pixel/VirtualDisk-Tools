using Qcow2Explorer.Core;
using Qcow2Explorer.Partitions;

namespace Qcow2Explorer.FileSystems;

public sealed class PartitionSliceReader : IBlockReader
{
    private readonly IBlockReader _disk;

    public PartitionSliceReader(IBlockReader disk, PartitionInfo partition)
    {
        _disk = disk;
        Partition = partition;
    }

    public PartitionInfo Partition { get; }
    public long Length => Partition.LengthBytes;

    public void ReadAt(long offset, byte[] buffer, int bufferOffset, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (offset + count > Length)
        {
            var available = Math.Max(0, Length - offset);
            if (available > 0)
            {
                _disk.ReadAt(Partition.StartOffset + offset, buffer, bufferOffset, checked((int)available));
            }

            Array.Clear(buffer, bufferOffset + (int)available, count - (int)available);
            return;
        }

        _disk.ReadAt(Partition.StartOffset + offset, buffer, bufferOffset, count);
    }
}
