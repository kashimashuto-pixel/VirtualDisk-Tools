using Qcow2Explorer.Partitions;

namespace Qcow2Explorer.FileSystems;

public interface IReadOnlyFileSystem
{
    string Name { get; }
    PartitionInfo Partition { get; }
    VfsNode Root { get; }

    IReadOnlyList<VfsNode> ListDirectory(VfsNode directory);
    byte[] ReadFile(VfsNode file, long offset, int count);
}
