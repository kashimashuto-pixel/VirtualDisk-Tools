namespace Qcow2Explorer.Core;

public interface IBlockReader
{
    long Length { get; }

    void ReadAt(long offset, byte[] buffer, int bufferOffset, int count);
}
