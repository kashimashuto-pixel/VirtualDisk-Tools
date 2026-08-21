using Qcow2Explorer.Core;
using Qcow2Explorer.Partitions;

namespace Qcow2Explorer.FileSystems;

public sealed class Luks2FileSystem : IReadOnlyFileSystem, IDisposable
{
    private readonly IReadOnlyFileSystem _inner;

    public Luks2FileSystem(
        IReadOnlyFileSystem inner,
        IBlockReader decryptedReader,
        PartitionInfo encryptedPartition,
        PartitionInfo decryptedPartition)
    {
        _inner = inner;
        DecryptedReader = decryptedReader;
        DecryptedPartition = decryptedPartition;
        Partition = encryptedPartition;
    }

    public string Name => $"LUKS2 -> {_inner.Name}";
    public PartitionInfo Partition { get; }
    public PartitionInfo DecryptedPartition { get; }
    public IBlockReader DecryptedReader { get; }
    public VfsNode Root => _inner.Root;

    public IReadOnlyList<VfsNode> ListDirectory(VfsNode directory) => _inner.ListDirectory(directory);

    public byte[] ReadFile(VfsNode file, long offset, int count) => _inner.ReadFile(file, offset, count);

    public void Dispose()
    {
        try
        {
            if (_inner is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
        finally
        {
            if (DecryptedReader is IDisposable readerDisposable)
            {
                readerDisposable.Dispose();
            }
        }
    }
}
