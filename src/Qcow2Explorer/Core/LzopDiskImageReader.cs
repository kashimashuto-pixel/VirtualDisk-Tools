using System.Buffers.Binary;
using System.Text;

namespace Qcow2Explorer.Core;

public sealed class LzopDiskImageReader : IDiskImageReader
{
    private static readonly byte[] Magic = [0x89, 0x4c, 0x5a, 0x4f, 0x00, 0x0d, 0x0a, 0x1a, 0x0a];

    private const uint FlagAdler32Data = 0x00000001;
    private const uint FlagAdler32Compressed = 0x00000002;
    private const uint FlagExtraField = 0x00000040;
    private const uint FlagCrc32Data = 0x00000100;
    private const uint FlagCrc32Compressed = 0x00000200;
    private const uint FlagMultipart = 0x00000400;
    private const uint FlagHeaderFilter = 0x00000800;
    private const uint FlagHeaderCrc32 = 0x00001000;
    private const uint KnownFlagMask = 0xfff03fff;
    private const uint MaximumBlockSize = 64 * 1024 * 1024;

    private readonly FileStream _stream;
    private readonly object _sync = new();
    private readonly List<LzopBlock> _blocks = [];
    private readonly long _compressedLength;
    private byte[]? _cachedData;
    private int _cachedBlockIndex = -1;

    public LzopDiskImageReader(string path)
    {
        Path = path;
        FormatName = "raw/dd (lzop LZO1X)";
        _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        _compressedLength = _stream.Length;

        try
        {
            ReadHeader();
            BuildBlockIndex();
        }
        catch
        {
            _stream.Dispose();
            throw;
        }
    }

    public string Path { get; }
    public string FormatName { get; }
    public long Length { get; private set; }
    public ushort Version { get; private set; }
    public byte Method { get; private set; }
    public byte Level { get; private set; }
    public uint Flags { get; private set; }
    public string OriginalName { get; private set; } = "";

    public IReadOnlyList<KeyValuePair<string, string>> GetHeaderRows()
    {
        return
        [
            Row("ファイル", Path),
            Row("形式", FormatName),
            Row("lzopバージョン", $"0x{Version:X4}"),
            Row("LZOメソッド", $"{Method} / level {Level}"),
            Row("格納名", string.IsNullOrWhiteSpace(OriginalName) ? "(なし)" : OriginalName),
            Row("圧縮ファイルサイズ", $"{_compressedLength:N0} bytes"),
            Row("仮想ディスクサイズ", $"{Length:N0} bytes"),
            Row("lzopブロック数", $"{_blocks.Count:N0}")
        ];

        static KeyValuePair<string, string> Row(string key, string value) => new(key, value);
    }

    public IReadOnlyList<string> GetWarnings()
    {
        return ["LZOブロックを必要な範囲だけオンデマンド展開しています。元ファイルへの書き込みは行いません。"];
    }

    public string DescribeOffset(long offset)
    {
        if (offset < 0 || offset >= Length || _blocks.Count == 0)
        {
            return $"lzop logical offset 0x{offset:X}";
        }

        var index = FindBlock(offset);
        var block = _blocks[index];
        return $"lzop block {index:N0}, compressed offset 0x{block.DataOffset:X}";
    }

    public void ReadAt(long offset, byte[] buffer, int bufferOffset, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentNullException.ThrowIfNull(buffer);
        if (bufferOffset < 0 || count < 0 || bufferOffset > buffer.Length - count)
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
            while (remaining > 0)
            {
                var index = FindBlock(offset);
                var block = _blocks[index];
                var data = GetBlock(index);
                var inBlock = checked((int)(offset - block.UncompressedOffset));
                var chunk = Math.Min(remaining, data.Length - inBlock);
                Array.Copy(data, inBlock, buffer, bufferOffset, chunk);
                offset += chunk;
                bufferOffset += chunk;
                remaining -= chunk;
            }
        }
    }

    public void Dispose()
    {
        _stream.Dispose();
    }

    private void ReadHeader()
    {
        var magic = ReadExact(Magic.Length);
        if (!magic.AsSpan().SequenceEqual(Magic))
        {
            throw new InvalidDataException(".dd.lzoのlzopマジックが一致しません。");
        }

        var headerStart = _stream.Position;
        Version = ReadUInt16BigEndian();
        _ = ReadUInt16BigEndian();
        if (Version < 0x0900)
        {
            throw new NotSupportedException($"lzopバージョン0x{Version:X4}は古すぎるため対応できません。");
        }

        if (Version >= 0x0940)
        {
            _ = ReadUInt16BigEndian();
        }

        Method = ReadByte();
        if (Method is < 1 or > 3)
        {
            throw new NotSupportedException($"LZOメソッド{Method}には対応していません。LZO1Xのmethod 1～3のみ対応します。");
        }

        Level = Version >= 0x0940 ? ReadByte() : (byte)0;
        Flags = ReadUInt32BigEndian();
        if ((Flags & ~KnownFlagMask) != 0)
        {
            throw new NotSupportedException($"lzopヘッダーに未知のフラグ0x{Flags & ~KnownFlagMask:X8}があります。");
        }

        if ((Flags & FlagHeaderFilter) != 0)
        {
            throw new NotSupportedException("lzopの変換フィルター付きデータには対応していません。");
        }

        if ((Flags & FlagMultipart) != 0)
        {
            throw new NotSupportedException("複数パートのlzopストリームには対応していません。単一の.dd.lzoを指定してください。");
        }

        _ = ReadUInt32BigEndian();
        _ = ReadUInt32BigEndian();
        if (Version >= 0x0940)
        {
            _ = ReadUInt32BigEndian();
        }

        var nameLength = ReadByte();
        OriginalName = Encoding.UTF8.GetString(ReadExact(nameLength));
        var checksumOffset = _stream.Position;
        var expectedHeaderChecksum = ReadUInt32BigEndian();
        var headerBytes = ReadRange(headerStart, checked((int)(checksumOffset - headerStart)));
        var actualHeaderChecksum = (Flags & FlagHeaderCrc32) != 0
            ? LzopChecksums.Crc32(headerBytes)
            : LzopChecksums.Adler32(headerBytes);
        if (actualHeaderChecksum != expectedHeaderChecksum)
        {
            throw new InvalidDataException(
                $"lzopヘッダーチェックサムが一致しません。expected=0x{expectedHeaderChecksum:X8}, actual=0x{actualHeaderChecksum:X8}");
        }

        if ((Flags & FlagExtraField) != 0)
        {
            var extraStart = _stream.Position;
            var extraLength = ReadUInt32BigEndian();
            if (extraLength > int.MaxValue || extraLength > _stream.Length - _stream.Position - sizeof(uint))
            {
                throw new InvalidDataException("lzop追加ヘッダーの長さが不正です。");
            }

            _stream.Position += extraLength;
            var expectedExtraChecksum = ReadUInt32BigEndian();
            var extraBytes = ReadRange(extraStart, checked((int)(sizeof(uint) + extraLength)));
            var actualExtraChecksum = (Flags & FlagHeaderCrc32) != 0
                ? LzopChecksums.Crc32(extraBytes)
                : LzopChecksums.Adler32(extraBytes);
            if (actualExtraChecksum != expectedExtraChecksum)
            {
                throw new InvalidDataException("lzop追加ヘッダーのチェックサムが一致しません。");
            }
        }
    }

    private void BuildBlockIndex()
    {
        long uncompressedOffset = 0;
        while (true)
        {
            var uncompressedSize = ReadUInt32BigEndian();
            if (uncompressedSize == 0)
            {
                break;
            }

            if (uncompressedSize == uint.MaxValue)
            {
                throw new NotSupportedException("分割されたlzopデータブロックには対応していません。");
            }

            if (uncompressedSize > MaximumBlockSize)
            {
                throw new InvalidDataException($"lzopブロックの展開サイズ{uncompressedSize:N0} bytesが上限を超えています。");
            }

            var compressedSize = ReadUInt32BigEndian();
            if (compressedSize == 0 || compressedSize > uncompressedSize)
            {
                throw new InvalidDataException(
                    $"lzopブロック#{_blocks.Count}の圧縮サイズが不正です: compressed={compressedSize:N0}, uncompressed={uncompressedSize:N0}");
            }

            uint? dataAdler = (Flags & FlagAdler32Data) != 0 ? ReadUInt32BigEndian() : null;
            uint? dataCrc = (Flags & FlagCrc32Data) != 0 ? ReadUInt32BigEndian() : null;
            uint? compressedAdler = null;
            uint? compressedCrc = null;
            if (compressedSize < uncompressedSize)
            {
                compressedAdler = (Flags & FlagAdler32Compressed) != 0 ? ReadUInt32BigEndian() : null;
                compressedCrc = (Flags & FlagCrc32Compressed) != 0 ? ReadUInt32BigEndian() : null;
            }

            if (compressedSize > _stream.Length - _stream.Position)
            {
                throw new EndOfStreamException($"lzopブロック#{_blocks.Count}の圧縮データが途中で終了しています。");
            }

            _blocks.Add(new LzopBlock(
                uncompressedOffset,
                checked((int)uncompressedSize),
                checked((int)compressedSize),
                _stream.Position,
                dataAdler,
                dataCrc,
                compressedAdler,
                compressedCrc));
            uncompressedOffset = checked(uncompressedOffset + uncompressedSize);
            _stream.Position += compressedSize;
        }

        Length = uncompressedOffset;
        if (_blocks.Count == 0)
        {
            throw new InvalidDataException("lzopストリームにディスクデータブロックがありません。");
        }
    }

    private byte[] GetBlock(int index)
    {
        if (_cachedBlockIndex == index && _cachedData is not null)
        {
            return _cachedData;
        }

        var block = _blocks[index];
        _stream.Position = block.DataOffset;
        var compressed = ReadExact(block.CompressedSize);
        VerifyChecksum(index, "圧縮データ", compressed, block.CompressedAdler32, block.CompressedCrc32);

        byte[] data;
        if (block.CompressedSize == block.UncompressedSize)
        {
            data = compressed;
        }
        else
        {
            data = new byte[block.UncompressedSize];
            int written;
            try
            {
                written = Lzo1xDecoder.Decompress(compressed, data);
            }
            catch (Exception ex) when (ex is InvalidDataException or OverflowException)
            {
                throw new InvalidDataException($"lzopブロック#{index}のLZO1X展開に失敗しました: {ex.Message}", ex);
            }

            if (written != data.Length)
            {
                throw new InvalidDataException(
                    $"lzopブロック#{index}の展開サイズが一致しません。expected={data.Length:N0}, actual={written:N0}");
            }
        }

        VerifyChecksum(index, "展開データ", data, block.DataAdler32, block.DataCrc32);
        _cachedBlockIndex = index;
        _cachedData = data;
        return data;
    }

    private static void VerifyChecksum(
        int index,
        string label,
        byte[] data,
        uint? expectedAdler,
        uint? expectedCrc)
    {
        if (expectedAdler is uint adler)
        {
            var actual = LzopChecksums.Adler32(data);
            if (actual != adler)
            {
                throw new InvalidDataException(
                    $"lzopブロック#{index}の{label}Adler-32が一致しません。expected=0x{adler:X8}, actual=0x{actual:X8}");
            }
        }

        if (expectedCrc is uint crc)
        {
            var actual = LzopChecksums.Crc32(data);
            if (actual != crc)
            {
                throw new InvalidDataException(
                    $"lzopブロック#{index}の{label}CRC-32が一致しません。expected=0x{crc:X8}, actual=0x{actual:X8}");
            }
        }
    }

    private int FindBlock(long offset)
    {
        var low = 0;
        var high = _blocks.Count - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            var block = _blocks[middle];
            if (offset < block.UncompressedOffset)
            {
                high = middle - 1;
            }
            else if (offset >= block.UncompressedOffset + block.UncompressedSize)
            {
                low = middle + 1;
            }
            else
            {
                return middle;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(offset));
    }

    private byte[] ReadRange(long offset, int count)
    {
        var returnPosition = _stream.Position;
        try
        {
            _stream.Position = offset;
            return ReadExact(count);
        }
        finally
        {
            _stream.Position = returnPosition;
        }
    }

    private byte[] ReadExact(int count)
    {
        var data = new byte[count];
        var total = 0;
        while (total < data.Length)
        {
            var read = _stream.Read(data, total, data.Length - total);
            if (read == 0)
            {
                throw new EndOfStreamException("lzopファイルが途中で終了しています。");
            }

            total += read;
        }

        return data;
    }

    private byte ReadByte()
    {
        var value = _stream.ReadByte();
        if (value < 0)
        {
            throw new EndOfStreamException("lzopファイルが途中で終了しています。");
        }

        return (byte)value;
    }

    private ushort ReadUInt16BigEndian()
    {
        return BinaryPrimitives.ReadUInt16BigEndian(ReadExact(sizeof(ushort)));
    }

    private uint ReadUInt32BigEndian()
    {
        return BinaryPrimitives.ReadUInt32BigEndian(ReadExact(sizeof(uint)));
    }

    private sealed record LzopBlock(
        long UncompressedOffset,
        int UncompressedSize,
        int CompressedSize,
        long DataOffset,
        uint? DataAdler32,
        uint? DataCrc32,
        uint? CompressedAdler32,
        uint? CompressedCrc32);
}

internal static class LzopChecksums
{
    private static readonly uint[] CrcTable = CreateCrcTable();

    public static uint Adler32(ReadOnlySpan<byte> data)
    {
        const uint prime = 65521;
        uint first = 1;
        uint second = 0;
        while (!data.IsEmpty)
        {
            var chunkLength = Math.Min(data.Length, 5552);
            foreach (var value in data[..chunkLength])
            {
                first += value;
                second += first;
            }

            first %= prime;
            second %= prime;
            data = data[chunkLength..];
        }

        return (second << 16) | first;
    }

    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = uint.MaxValue;
        foreach (var value in data)
        {
            crc = CrcTable[(crc ^ value) & 0xff] ^ (crc >> 8);
        }

        return ~crc;
    }

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            var value = index;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? 0xedb88320u ^ (value >> 1) : value >> 1;
            }

            table[index] = value;
        }

        return table;
    }
}
