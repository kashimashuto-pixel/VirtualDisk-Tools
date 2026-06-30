namespace Qcow2Explorer.Core;

public sealed class StreamBlockReader : IBlockReader, IDisposable
{
    private readonly Stream _stream;
    private readonly object _sync = new();
    private readonly bool _leaveOpen;

    public StreamBlockReader(Stream stream, bool leaveOpen = false)
    {
        _stream = stream;
        _leaveOpen = leaveOpen;
    }

    public long Length => _stream.Length;

    public void ReadAt(long offset, byte[] buffer, int bufferOffset, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentNullException.ThrowIfNull(buffer);
        if (bufferOffset < 0 || count < 0 || bufferOffset + count > buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(bufferOffset));
        }

        Array.Clear(buffer, bufferOffset, count);
        if (count == 0 || offset >= Length)
        {
            return;
        }

        var remaining = checked((int)Math.Min(count, Length - offset));
        lock (_sync)
        {
            _stream.Position = offset;
            var total = 0;
            while (total < remaining)
            {
                var read = _stream.Read(buffer, bufferOffset + total, remaining - total);
                if (read == 0)
                {
                    break;
                }

                total += read;
            }
        }
    }

    public void Dispose()
    {
        if (!_leaveOpen)
        {
            _stream.Dispose();
        }
    }
}
