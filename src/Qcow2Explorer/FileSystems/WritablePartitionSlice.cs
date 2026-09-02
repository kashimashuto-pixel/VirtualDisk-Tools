using Qcow2Explorer.Core;
using Qcow2Explorer.Partitions;

namespace Qcow2Explorer.FileSystems;

public sealed class WritablePartitionSlice : IBlockDevice
{
    private readonly IBlockDevice _disk;

    public WritablePartitionSlice(IBlockDevice disk, PartitionInfo partition)
    {
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentNullException.ThrowIfNull(partition);
        if (partition.ReaderOverride is not null)
        {
            throw new NotSupportedException("RAID、LVM、復号レイヤーなどの合成パーティションはまだ書き込めません。");
        }

        if (partition.StartOffset < 0 || partition.LengthBytes < 0
            || partition.StartOffset > disk.Length - partition.LengthBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(partition), "パーティション範囲がディスク外です。");
        }

        _disk = disk;
        Partition = partition;
    }

    public PartitionInfo Partition { get; }
    public long Length => Partition.LengthBytes;

    public void ReadAt(long offset, byte[] buffer, int bufferOffset, int count)
    {
        ValidateRange(offset, count);
        _disk.ReadAt(checked(Partition.StartOffset + offset), buffer, bufferOffset, count);
    }

    public void WriteAt(long offset, byte[] buffer, int bufferOffset, int count)
    {
        ValidateRange(offset, count);
        _disk.WriteAt(checked(Partition.StartOffset + offset), buffer, bufferOffset, count);
    }

    public void Flush() => _disk.Flush();

    private void ValidateRange(long offset, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset > Length - count)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "パーティションの末尾を超えています。");
        }
    }
}
