namespace Qcow2Explorer.Core;

public interface IBlockReader
{
    long Length { get; }

    void ReadAt(long offset, byte[] buffer, int bufferOffset, int count);
}

public interface IBlockWriter
{
    void WriteAt(long offset, byte[] buffer, int bufferOffset, int count);

    void Flush();
}

public interface IBlockDevice : IBlockReader, IBlockWriter
{
}

public interface ILogicalSectorReader
{
    uint LogicalSectorSize { get; }
}
