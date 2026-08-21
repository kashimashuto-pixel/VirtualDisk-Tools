using System.IO.Compression;
using System.Text;

namespace Qcow2Explorer.Core;

public sealed class EwfDiskImageReader : IDiskImageReader
{
    private const int FileHeaderSize = 13;
    private const int SectionDescriptorSize = 76;
    private const int TableHeaderSize = 24;
    private const int MaximumSectionCount = 1_000_000;
    private const int MaximumTableEntries = 1_000_000;
    private const uint MaximumChunkCount = 4_194_304;
    private const int MaximumChunkSize = 64 * 1024 * 1024;
    private static readonly byte[] EwfMagic = [(byte)'E', (byte)'V', (byte)'F', 0x09, 0x0d, 0x0a, 0xff, 0x00];

    private readonly List<EwfSegment> _segments = [];
    private readonly List<EwfChunk> _chunks = [];
    private readonly object _sync = new();
    private byte[]? _cachedChunk;
    private int _cachedChunkIndex = -1;
    private bool _disposed;

    private EwfDiskImageReader(string path, CancellationToken cancellationToken)
    {
        var segmentPaths = DiscoverSegmentPaths(path);
        try
        {
            foreach (var segmentPath in segmentPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _segments.Add(new EwfSegment(segmentPath));
            }

            Path = _segments[0].Path;
            Parse(cancellationToken);
        }
        catch
        {
            foreach (var segment in _segments)
            {
                segment.Dispose();
            }
            throw;
        }
    }

    public string Path { get; }
    public string FormatName => _segments.Count == 1 ? "EWF/E01" : $"EWF/E01 ({_segments.Count} segments)";
    public long Length { get; private set; }
    public int BytesPerSector { get; private set; }
    public int SectorsPerChunk { get; private set; }
    public int ChunkSize { get; private set; }
    public int SegmentCount => _segments.Count;
    public int ChunkCount => _chunks.Count;

    public static EwfDiskImageReader Open(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new EwfDiskImageReader(System.IO.Path.GetFullPath(path), cancellationToken);
    }

    public static bool HasMagic(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }
        Span<byte> magic = stackalloc byte[EwfMagic.Length];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return stream.Read(magic) == magic.Length && magic.SequenceEqual(EwfMagic);
    }

    public IReadOnlyList<KeyValuePair<string, string>> GetHeaderRows()
    {
        return
        [
            new("ファイル", Path),
            new("形式", FormatName),
            new("仮想ディスクサイズ", $"{Length:N0} bytes"),
            new("EWF segment数", SegmentCount.ToString("N0")),
            new("EWF chunk数", ChunkCount.ToString("N0")),
            new("chunkサイズ", $"{ChunkSize:N0} bytes"),
            new("sectorサイズ", $"{BytesPerSector:N0} bytes")
        ];
    }

    public IReadOnlyList<string> GetWarnings() => Array.Empty<string>();

    public string DescribeOffset(long offset)
    {
        if (offset < 0 || offset >= Length || ChunkSize <= 0)
        {
            return $"EWF logical offset 0x{offset:X}";
        }
        var index = checked((int)(offset / ChunkSize));
        var chunk = _chunks[index];
        return $"EWF chunk {index:N0}, segment {chunk.SegmentIndex + 1}, stored offset 0x{chunk.StoredOffset:X}";
    }

    public void ReadAt(long offset, byte[] buffer, int bufferOffset, int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
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

        lock (_sync)
        {
            var remaining = Math.Min(count, Length - offset);
            var currentOffset = offset;
            var outputOffset = bufferOffset;
            while (remaining > 0)
            {
                var chunkIndex = checked((int)(currentOffset / ChunkSize));
                var inChunkOffset = checked((int)(currentOffset % ChunkSize));
                var chunk = GetChunk(chunkIndex);
                var copyLength = checked((int)Math.Min(remaining, chunk.Length - inChunkOffset));
                if (copyLength <= 0)
                {
                    throw new InvalidDataException($"EWF chunk {chunkIndex}のlogical sizeが不正です。");
                }
                Array.Copy(chunk, inChunkOffset, buffer, outputOffset, copyLength);
                currentOffset += copyLength;
                outputOffset += copyLength;
                remaining -= copyLength;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _cachedChunk = null;
        foreach (var segment in _segments)
        {
            segment.Dispose();
        }
    }

    private void Parse(CancellationToken cancellationToken)
    {
        uint? declaredChunkCount = null;
        ulong? sectorCount = null;
        for (var segmentIndex = 0; segmentIndex < _segments.Count; segmentIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var segment = _segments[segmentIndex];
            var header = segment.Read(0, FileHeaderSize);
            if (!header.AsSpan(0, EwfMagic.Length).SequenceEqual(EwfMagic)
                || header[8] != 1
                || EndianUtilities.ReadUInt16Little(header, 9) != segmentIndex + 1
                || header[11] != 0
                || header[12] != 0)
            {
                throw new InvalidDataException($"EWF segment {segmentIndex + 1}のfile headerが不正です。");
            }

            var knownSections = new Dictionary<long, string>();
            var sectionOffset = (long)FileHeaderSize;
            var foundDone = false;
            for (var sectionIndex = 0; sectionIndex < MaximumSectionCount; sectionIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (sectionOffset > segment.Length - SectionDescriptorSize)
                {
                    throw new InvalidDataException($"EWF segment {segmentIndex + 1}のsection descriptorが切り詰められています。");
                }
                var descriptor = segment.Read(sectionOffset, SectionDescriptorSize);
                ValidateAdler32(descriptor.AsSpan(0, 72), EndianUtilities.ReadUInt32Little(descriptor, 72), "section descriptor");
                var sectionType = ReadSectionType(descriptor);
                var nextOffsetValue = EndianUtilities.ReadUInt64Little(descriptor, 16);
                var sectionSizeValue = EndianUtilities.ReadUInt64Little(descriptor, 24);
                if (nextOffsetValue > long.MaxValue || sectionSizeValue > long.MaxValue)
                {
                    throw new InvalidDataException("EWF section offsetまたはsizeが大きすぎます。");
                }
                var nextOffset = checked((long)nextOffsetValue);
                var sectionSize = checked((long)sectionSizeValue);
                knownSections.Add(sectionOffset, sectionType);

                if (sectionType is "done" or "next")
                {
                    if (nextOffset != sectionOffset || sectionSize != 0)
                    {
                        throw new InvalidDataException($"EWF {sectionType} sectionの終端値が不正です。");
                    }
                    foundDone = true;
                    break;
                }

                if (nextOffset <= sectionOffset || nextOffset > segment.Length
                    || sectionSize != nextOffset - sectionOffset || sectionSize < SectionDescriptorSize)
                {
                    throw new InvalidDataException($"EWF {sectionType} sectionの範囲が不正です。");
                }

                if (sectionType == "volume")
                {
                    if (segmentIndex != 0 || declaredChunkCount.HasValue)
                    {
                        throw new InvalidDataException("EWF volume sectionが重複または先頭segment以外にあります。");
                    }
                    ParseVolume(segment, sectionOffset, sectionSize, out var chunks, out var sectors);
                    declaredChunkCount = chunks;
                    sectorCount = sectors;
                }
                else if (sectionType == "table")
                {
                    ParseTable(segmentIndex, segment, sectionOffset, sectionSize, knownSections, cancellationToken);
                }

                sectionOffset = nextOffset;
            }
            if (!foundDone)
            {
                throw new InvalidDataException($"EWF segment {segmentIndex + 1}にdone/next sectionがありません。");
            }
        }

        if (!declaredChunkCount.HasValue || !sectorCount.HasValue || BytesPerSector <= 0 || SectorsPerChunk <= 0)
        {
            throw new InvalidDataException("EWF volume情報がありません。");
        }
        if (declaredChunkCount.Value > int.MaxValue || declaredChunkCount.Value != _chunks.Count)
        {
            throw new InvalidDataException(
                $"EWF chunk数がvolumeとtableで一致しません: volume={declaredChunkCount.Value}, table={_chunks.Count}");
        }
        Length = checked((long)sectorCount.Value * BytesPerSector);
        var expectedChunks = checked((Length + ChunkSize - 1) / ChunkSize);
        if (expectedChunks != _chunks.Count)
        {
            throw new InvalidDataException($"EWF media sizeとchunk数が一致しません: expected={expectedChunks}, actual={_chunks.Count}");
        }
    }

    private void ParseVolume(
        EwfSegment segment,
        long sectionOffset,
        long sectionSize,
        out uint chunkCount,
        out ulong sectorCount)
    {
        var dataSize = checked((int)(sectionSize - SectionDescriptorSize));
        if (dataSize != 1052)
        {
            throw new NotSupportedException($"未対応のEWF volume section sizeです: {dataSize} bytes");
        }
        var data = segment.Read(sectionOffset + SectionDescriptorSize, dataSize);
        ValidateAdler32(data.AsSpan(0, 1048), EndianUtilities.ReadUInt32Little(data, 1048), "volume section");
        chunkCount = EndianUtilities.ReadUInt32Little(data, 4);
        var sectorsPerChunkValue = EndianUtilities.ReadUInt32Little(data, 8);
        var bytesPerSectorValue = EndianUtilities.ReadUInt32Little(data, 12);
        sectorCount = EndianUtilities.ReadUInt64Little(data, 16);
        if (chunkCount == 0 || sectorsPerChunkValue == 0 || bytesPerSectorValue is < 128 or > 65536
            || sectorsPerChunkValue > int.MaxValue)
        {
            throw new InvalidDataException("EWF volumeのmedia geometryが不正です。");
        }
        if (chunkCount > MaximumChunkCount)
        {
            throw new NotSupportedException($"EWF chunk数が対応上限を超えています: {chunkCount:N0}");
        }
        BytesPerSector = checked((int)bytesPerSectorValue);
        SectorsPerChunk = checked((int)sectorsPerChunkValue);
        ChunkSize = checked(BytesPerSector * SectorsPerChunk);
        if (ChunkSize <= 0 || ChunkSize > MaximumChunkSize)
        {
            throw new NotSupportedException($"EWF chunk sizeが未対応です: {ChunkSize:N0} bytes");
        }
    }

    private void ParseTable(
        int segmentIndex,
        EwfSegment segment,
        long sectionOffset,
        long sectionSize,
        IReadOnlyDictionary<long, string> knownSections,
        CancellationToken cancellationToken)
    {
        if (sectionSize < SectionDescriptorSize + TableHeaderSize + 4)
        {
            throw new InvalidDataException("EWF table sectionが切り詰められています。");
        }
        var tableHeader = segment.Read(sectionOffset + SectionDescriptorSize, TableHeaderSize);
        var entryCountValue = EndianUtilities.ReadUInt32Little(tableHeader, 0);
        if (entryCountValue == 0 || entryCountValue > MaximumTableEntries)
        {
            throw new InvalidDataException($"EWF table entry数が不正です: {entryCountValue}");
        }
        var entryCount = checked((int)entryCountValue);
        if (_chunks.Count > MaximumChunkCount - entryCountValue)
        {
            throw new NotSupportedException($"EWF chunk数が対応上限を超えています: {MaximumChunkCount:N0}");
        }
        var expectedSize = checked((long)SectionDescriptorSize + TableHeaderSize + entryCount * 4L + 4);
        if (sectionSize != expectedSize)
        {
            throw new InvalidDataException($"EWF table section sizeがentry数と一致しません: {sectionSize}");
        }
        ValidateAdler32(tableHeader.AsSpan(0, 20), EndianUtilities.ReadUInt32Little(tableHeader, 20), "table header");
        var tableBaseValue = EndianUtilities.ReadUInt64Little(tableHeader, 8);
        if (tableBaseValue > long.MaxValue)
        {
            throw new InvalidDataException("EWF table base offsetが大きすぎます。");
        }
        var tableBase = checked((long)tableBaseValue);
        if (!knownSections.TryGetValue(tableBase, out var baseSectionType) || baseSectionType != "sectors")
        {
            throw new NotSupportedException("EWF tableが直前のsectors sectionを参照していません。");
        }

        var entryBytes = segment.Read(sectionOffset + SectionDescriptorSize + TableHeaderSize, checked(entryCount * 4));
        var footer = segment.Read(sectionOffset + sectionSize - 4, 4);
        ValidateAdler32(entryBytes, EndianUtilities.ReadUInt32Little(footer, 0), "table entries");
        var offsets = new long[entryCount];
        var compressed = new bool[entryCount];
        for (var index = 0; index < entryCount; index++)
        {
            if ((index & 4095) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            var raw = EndianUtilities.ReadUInt32Little(entryBytes, index * 4);
            compressed[index] = (raw & 0x80000000u) != 0;
            offsets[index] = checked(tableBase + (raw & 0x7fffffffu));
            if (offsets[index] < tableBase + SectionDescriptorSize || offsets[index] >= sectionOffset
                || (index > 0 && offsets[index] <= offsets[index - 1]))
            {
                throw new InvalidDataException($"EWF table entry {index}のchunk offsetが不正です。");
            }
        }
        for (var index = 0; index < entryCount; index++)
        {
            var end = index + 1 < entryCount ? offsets[index + 1] : sectionOffset;
            var storedLength = checked(end - offsets[index]);
            if (storedLength <= 0 || storedLength > ChunkSize + 1024 * 1024L)
            {
                throw new InvalidDataException($"EWF chunk {index}のstored sizeが不正です: {storedLength}");
            }
            _chunks.Add(new EwfChunk(segmentIndex, offsets[index], checked((int)storedLength), compressed[index]));
        }
    }

    private byte[] GetChunk(int index)
    {
        if (index == _cachedChunkIndex && _cachedChunk is not null)
        {
            return _cachedChunk;
        }
        if ((uint)index >= (uint)_chunks.Count)
        {
            throw new InvalidDataException($"EWF chunk indexが範囲外です: {index}");
        }
        var descriptor = _chunks[index];
        var expectedLength = checked((int)Math.Min(ChunkSize, Length - (long)index * ChunkSize));
        var stored = _segments[descriptor.SegmentIndex].Read(descriptor.StoredOffset, descriptor.StoredLength);
        byte[] result;
        if (descriptor.IsCompressed)
        {
            result = new byte[expectedLength];
            using var input = new MemoryStream(stored, writable: false);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress, leaveOpen: false);
            var total = 0;
            while (total < result.Length)
            {
                var read = zlib.Read(result, total, result.Length - total);
                if (read == 0)
                {
                    break;
                }
                total += read;
            }
            if (total != result.Length || zlib.ReadByte() != -1)
            {
                throw new InvalidDataException($"EWF compressed chunk {index}の展開sizeが不正です。");
            }
        }
        else
        {
            if (stored.Length != expectedLength + 4)
            {
                throw new InvalidDataException($"EWF uncompressed chunk {index}のsizeが不正です。");
            }
            result = stored.AsSpan(0, expectedLength).ToArray();
            ValidateAdler32(result, EndianUtilities.ReadUInt32Little(stored, expectedLength), $"chunk {index}");
        }
        _cachedChunk = result;
        _cachedChunkIndex = index;
        return result;
    }

    private static IReadOnlyList<string> DiscoverSegmentPaths(string inputPath)
    {
        if (!File.Exists(inputPath))
        {
            throw new FileNotFoundException("EWF segmentが見つかりません。", inputPath);
        }
        var directory = System.IO.Path.GetDirectoryName(inputPath) ?? ".";
        var stem = System.IO.Path.GetFileNameWithoutExtension(inputPath);
        var candidates = new Dictionary<ushort, string>();
        var header = new byte[FileHeaderSize];
        foreach (var candidate in Directory.EnumerateFiles(directory, $"{stem}.*", SearchOption.TopDirectoryOnly))
        {
            using var stream = new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Array.Clear(header);
            if (stream.Read(header) != header.Length || !header[..EwfMagic.Length].SequenceEqual(EwfMagic)
                || header[8] != 1 || header[11] != 0 || header[12] != 0)
            {
                continue;
            }
            var segmentNumber = (ushort)(header[9] | (header[10] << 8));
            if (segmentNumber == 0 || !candidates.TryAdd(segmentNumber, candidate))
            {
                throw new InvalidDataException($"EWF segment番号が不正または重複しています: {candidate}");
            }
        }
        if (candidates.Count == 0 || !candidates.ContainsKey(1))
        {
            throw new InvalidDataException("EWF segment 1が見つかりません。");
        }
        for (ushort number = 1; number <= candidates.Count; number++)
        {
            if (!candidates.ContainsKey(number))
            {
                throw new InvalidDataException($"EWF segment {number}が欠落しています。");
            }
        }
        return candidates.OrderBy(item => item.Key).Select(item => item.Value).ToList();
    }

    private static string ReadSectionType(byte[] descriptor)
    {
        var field = descriptor.AsSpan(0, 16);
        var terminator = field.IndexOf((byte)0);
        if (terminator <= 0)
        {
            throw new InvalidDataException("EWF section typeがnull-terminated ASCIIではありません。");
        }
        var value = field[..terminator];
        foreach (var item in value)
        {
            if (item is < 0x20 or > 0x7e)
            {
                throw new InvalidDataException("EWF section typeに非ASCII文字があります。");
            }
        }
        return Encoding.ASCII.GetString(value);
    }

    internal static uint ComputeAdler32(ReadOnlySpan<byte> data)
    {
        const uint modulus = 65521;
        uint a = 1;
        uint b = 0;
        foreach (var value in data)
        {
            a = (a + value) % modulus;
            b = (b + a) % modulus;
        }
        return (b << 16) | a;
    }

    private static void ValidateAdler32(ReadOnlySpan<byte> data, uint expected, string owner)
    {
        var actual = ComputeAdler32(data);
        if (actual != expected)
        {
            throw new InvalidDataException(
                $"EWF {owner}のAdler-32が一致しません: expected=0x{expected:X8}, actual=0x{actual:X8}");
        }
    }

    private sealed class EwfSegment : IDisposable
    {
        private readonly FileStream _stream;

        public EwfSegment(string path)
        {
            Path = path;
            _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Length = _stream.Length;
        }

        public string Path { get; }
        public long Length { get; }

        public byte[] Read(long offset, int count)
        {
            if (offset < 0 || count < 0 || offset > Length - count)
            {
                throw new InvalidDataException($"EWF segment readが範囲外です: offset={offset}, count={count}");
            }
            var result = new byte[count];
            _stream.Position = offset;
            var total = 0;
            while (total < count)
            {
                var read = _stream.Read(result, total, count - total);
                if (read == 0)
                {
                    throw new EndOfStreamException("EWF segmentが切り詰められています。");
                }
                total += read;
            }
            return result;
        }

        public void Dispose() => _stream.Dispose();
    }

    private sealed record EwfChunk(int SegmentIndex, long StoredOffset, int StoredLength, bool IsCompressed);
}
