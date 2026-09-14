namespace Qcow2Explorer.Core;

public sealed class BlockReaderStream : Stream
{
    private readonly IBlockReader _reader;
    private readonly IBlockWriter? _writer;
    private long _position;

    public BlockReaderStream(IBlockReader reader)
    {
        _reader = reader;
        _writer = reader as IBlockWriter;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => _writer is not null;
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
        _writer?.Flush();
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
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset > buffer.Length - count)
        {
            throw new ArgumentException("バッファー範囲が不正です。", nameof(offset));
        }

        if (_writer is null)
        {
            throw new NotSupportedException("このブロックストリームは読み取り専用です。");
        }

        if (_position < 0 || _position > Length - count)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "ブロックストリームの末尾を超えています。");
        }

        _writer.WriteAt(_position, buffer, offset, count);
        _position += count;
    }
}
