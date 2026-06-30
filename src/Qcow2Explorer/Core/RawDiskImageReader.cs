namespace Qcow2Explorer.Core;

public sealed class RawDiskImageReader : IDiskImageReader
{
    private readonly FileStream _stream;
    private readonly object _sync = new();

    public RawDiskImageReader(string path, string formatName = "raw/dd")
    {
        Path = path;
        FormatName = formatName;
        _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        Length = _stream.Length;
    }

    public string Path { get; }
    public string FormatName { get; }
    public long Length { get; }

    public IReadOnlyList<KeyValuePair<string, string>> GetHeaderRows()
    {
        return new List<KeyValuePair<string, string>>
        {
            Row("ファイル", Path),
            Row("形式", FormatName),
            Row("仮想ディスクサイズ", $"{Length:N0} bytes")
        };

        static KeyValuePair<string, string> Row(string key, string value) => new(key, value);
    }

    public IReadOnlyList<string> GetWarnings() => Array.Empty<string>();

    public string DescribeOffset(long offset)
    {
        return $"raw offset 0x{offset:X}";
    }

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
        _stream.Dispose();
    }
}
