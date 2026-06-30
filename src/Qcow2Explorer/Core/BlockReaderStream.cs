namespace Qcow2Explorer.Core;

public sealed class BlockReaderStream : Stream
{
    private readonly IBlockReader _reader;
    private long _position;

    public BlockReaderStream(IBlockReader reader)
    {
        _reader = reader;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _reader.Length;

    public override long Position
    {
        get => _position;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _position = value;
        }
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_position >= Length || count == 0)
        {
            return 0;
        }

        var toRead = (int)Math.Min(count, Length - _position);
        _reader.ReadAt(_position, buffer, offset, toRead);
        _position += toRead;
        return toRead;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var newPosition = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };

        ArgumentOutOfRangeException.ThrowIfNegative(newPosition);
        _position = newPosition;
        return _position;
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }
}
