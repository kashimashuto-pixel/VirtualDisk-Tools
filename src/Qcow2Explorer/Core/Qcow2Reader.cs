using System.IO.Compression;
using ZstdSharp;

namespace Qcow2Explorer.Core;

public sealed class Qcow2Reader : IDiskImageReader
{
    private const ulong OffsetMask = 0x00fffffffffffe00UL;
    private const ulong CompressedClusterBit = 1UL << 62;
    private const ulong ZeroClusterBit = 1UL;
    private const int MaxCompressedClusterCacheEntries = 512;

    private readonly FileStream _stream;
    private readonly object _sync = new();
    private ulong[] _l1Table;
    private readonly Dictionary<ulong, L2Entry[]> _l2Cache = new();
    private readonly Dictionary<ulong, byte[]> _compressedClusterCache = new();
    private readonly Queue<ulong> _compressedClusterCacheOrder = new();
    private readonly IDiskImageReader? _backingReader;
    private readonly FileStream? _externalDataStream;

    public Qcow2Header Header { get; }
    public IReadOnlyList<Qcow2Snapshot> Snapshots { get; }
    public int? ActiveSnapshotIndex { get; private set; }
    public string Path { get; }
    public string FormatName => "qcow2";
    public long Length { get; }
    public int L2Entries { get; }

    public Qcow2Reader(string path)
    {
        Path = path;
        _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        Header = Qcow2Header.Parse(_stream);
        if (Header.VirtualSize > long.MaxValue)
        {
            throw new NotSupportedException("この qcow2 の仮想サイズは .NET の long 範囲を超えています。");
        }

        Length = (long)Header.VirtualSize;
        L2Entries = checked((int)(Header.ClusterSize / (Header.UsesExtendedL2Entries ? 16 : 8)));
        _l1Table = ReadL1Table(Header.L1TableOffset, Header.L1Size);
        Snapshots = ReadSnapshots();
        _backingReader = OpenBackingReader(path, Header);
        _externalDataStream = OpenExternalDataFile(path, Header);
    }

    public IReadOnlyList<string> GetWarnings() => Header.GetReadWarnings();

    public IReadOnlyList<KeyValuePair<string, string>> GetHeaderRows()
    {
        var rows = new List<KeyValuePair<string, string>>
        {
            Row("ファイル", Path),
            Row("qcow2 version", Header.Version.ToString()),
            Row("仮想ディスクサイズ", $"{Header.VirtualSize:N0} bytes"),
            Row("cluster_bits", Header.ClusterBits.ToString()),
            Row("cluster size", $"{Header.ClusterSize:N0} bytes"),
            Row("L1 entries", Header.L1Size.ToString("N0")),
            Row("L1 table offset", $"0x{Header.L1TableOffset:X}"),
            Row("refcount table offset", $"0x{Header.RefcountTableOffset:X}"),
            Row("refcount table clusters", Header.RefcountTableClusters.ToString()),
            Row("snapshots", Header.SnapshotCount.ToString()),
            Row("incompatible features", $"0x{Header.IncompatibleFeatures:X}"),
            Row("compatible features", $"0x{Header.CompatibleFeatures:X}"),
            Row("autoclear features", $"0x{Header.AutoClearFeatures:X}"),
            Row("crypt_method", Header.CryptMethod.ToString()),
            Row("compression_type", Header.CompressionType.ToString()),
            Row("backing file", Header.BackingFileName ?? "(なし)")
        };

        foreach (var snapshot in Snapshots)
        {
            rows.Add(Row($"snapshot {snapshot.Index + 1}", $"{snapshot.Name} ({snapshot.Id}) {snapshot.TimestampUtc:yyyy-MM-dd HH:mm:ss} UTC"));
        }

        rows.Add(Row("active snapshot", ActiveSnapshotIndex.HasValue ? Snapshots[ActiveSnapshotIndex.Value].Name : "active image"));
        return rows;

        static KeyValuePair<string, string> Row(string key, string value) => new(key, value);
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

        if (!Header.CanReadStandardClusters)
        {
            var reason = string.Join(Environment.NewLine, Header.GetReadWarnings());
            throw new NotSupportedException(reason.Length == 0 ? "この qcow2 は未対応の機能を使用しています。" : reason);
        }

        var remaining = Math.Min(count, Length - offset);
        var outOffset = bufferOffset;
        var virtualOffset = (ulong)offset;
        var clusterSize = (ulong)Header.ClusterSize;

        while (remaining > 0)
        {
            var clusterOffset = (int)(virtualOffset % clusterSize);
            var allocationUnit = Header.UsesExtendedL2Entries ? clusterSize / 32UL : clusterSize;
            var allocationOffset = (int)(virtualOffset % allocationUnit);
            var chunk = (int)Math.Min(remaining, (long)allocationUnit - allocationOffset);
            var mapping = ResolveCluster(virtualOffset);
            if (mapping.Kind == ClusterKind.Standard)
            {
                if (_externalDataStream is not null)
                {
                    ReadExactAt(_externalDataStream, (long)(mapping.HostOffset + (ulong)clusterOffset), buffer, outOffset, chunk);
                }
                else
                {
                    ReadPhysical((long)(mapping.HostOffset + (ulong)clusterOffset), buffer, outOffset, chunk);
                }
            }
            else if (mapping.Kind == ClusterKind.Compressed)
            {
                var cluster = ReadCompressedCluster(mapping);
                Array.Copy(cluster, clusterOffset, buffer, outOffset, chunk);
            }
            else if (mapping.Kind == ClusterKind.Unallocated && _backingReader is not null)
            {
                _backingReader.ReadAt((long)virtualOffset, buffer, outOffset, chunk);
            }

            remaining -= chunk;
            virtualOffset += (ulong)chunk;
            outOffset += chunk;
        }
    }

    public ClusterLookupResult LookupCluster(long virtualOffset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(virtualOffset);
        var mapping = ResolveCluster((ulong)virtualOffset);
        return new ClusterLookupResult(
            virtualOffset / Header.ClusterSize,
            mapping.HostOffset == 0 ? null : (long)mapping.HostOffset,
            mapping.Kind == ClusterKind.Zero,
            mapping.Kind == ClusterKind.Compressed,
            mapping.CompressedLength);
    }

    public string DescribeOffset(long offset)
    {
        var result = LookupCluster(offset);
        var host = result.HostClusterOffset.HasValue ? $"0x{result.HostClusterOffset.Value:X}" : "(未割当)";
        var compression = result.IsCompressed ? $", compressed={FormatBytes(result.CompressedLength)}" : "";
        return $"virtual cluster {result.VirtualClusterIndex:N0} -> {host}{Environment.NewLine}zero={result.ReadsAsZero}{compression}";
    }

    public void Dispose()
    {
        _backingReader?.Dispose();
        _externalDataStream?.Dispose();
        _stream.Dispose();
    }

    public void SelectSnapshot(int? index)
    {
        if (index is null)
        {
            _l1Table = ReadL1Table(Header.L1TableOffset, Header.L1Size);
            ActiveSnapshotIndex = null;
        }
        else
        {
            if (index < 0 || index >= Snapshots.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            var snapshot = Snapshots[index.Value];
            _l1Table = ReadL1Table(snapshot.L1TableOffset, snapshot.L1Size);
            ActiveSnapshotIndex = index;
        }

        _l2Cache.Clear();
        _compressedClusterCache.Clear();
        _compressedClusterCacheOrder.Clear();
    }

    private ulong[] ReadL1Table(ulong tableOffset, uint tableSize)
    {
        var entries = new ulong[tableSize];
        var buffer = new byte[checked((int)tableSize * 8)];
        ReadPhysical((long)tableOffset, buffer, 0, buffer.Length);
        for (var i = 0; i < entries.Length; i++)
        {
            entries[i] = EndianUtilities.ReadUInt64Big(buffer, i * 8);
        }

        return entries;
    }

    private IReadOnlyList<Qcow2Snapshot> ReadSnapshots()
    {
        if (Header.SnapshotCount == 0 || Header.SnapshotsOffset == 0)
        {
            return Array.Empty<Qcow2Snapshot>();
        }

        var result = new List<Qcow2Snapshot>();
        var offset = checked((long)Header.SnapshotsOffset);
        for (var index = 0; index < Header.SnapshotCount; index++)
        {
            var fixedPart = new byte[40];
            ReadPhysical(offset, fixedPart, 0, fixedPart.Length);
            var l1Offset = EndianUtilities.ReadUInt64Big(fixedPart, 0);
            var l1Size = EndianUtilities.ReadUInt32Big(fixedPart, 8);
            var idSize = EndianUtilities.ReadUInt16Big(fixedPart, 12);
            var nameSize = EndianUtilities.ReadUInt16Big(fixedPart, 14);
            var seconds = EndianUtilities.ReadUInt32Big(fixedPart, 16);
            var nanoseconds = EndianUtilities.ReadUInt32Big(fixedPart, 20);
            var extraSize = EndianUtilities.ReadUInt32Big(fixedPart, 36);
            var variableSize = checked((int)extraSize + idSize + nameSize);
            var variable = new byte[variableSize];
            ReadPhysical(offset + fixedPart.Length, variable, 0, variable.Length);
            var id = System.Text.Encoding.UTF8.GetString(variable, (int)extraSize, idSize);
            var name = System.Text.Encoding.UTF8.GetString(variable, (int)extraSize + idSize, nameSize);
            result.Add(new Qcow2Snapshot(
                index,
                id,
                name,
                DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime.AddTicks(nanoseconds / 100),
                l1Offset,
                l1Size));
            var entrySize = fixedPart.Length + variableSize;
            offset += (entrySize + 7) & ~7L;
        }

        return result;
    }

    private ClusterMapping ResolveCluster(ulong virtualOffset)
    {
        var guestCluster = virtualOffset / (ulong)Header.ClusterSize;
        var l1Index = guestCluster / (ulong)L2Entries;
        var l2Index = guestCluster % (ulong)L2Entries;
        if (l1Index >= (ulong)_l1Table.Length)
        {
            return ClusterMapping.Unallocated;
        }

        var l1Entry = _l1Table[l1Index];
        var l2Offset = l1Entry & OffsetMask;
        if (l2Offset == 0)
        {
            return ClusterMapping.Unallocated;
        }

        var l2 = GetL2Table(l2Offset);
        var l2Entry = l2[l2Index];
        if ((l2Entry.Descriptor & CompressedClusterBit) != 0)
        {
            return ParseCompressedCluster(l2Entry.Descriptor, guestCluster);
        }

        if (!Header.UsesExtendedL2Entries && (l2Entry.Descriptor & ZeroClusterBit) != 0)
        {
            return ClusterMapping.Zero;
        }

        var hostOffset = l2Entry.Descriptor & OffsetMask;
        if (Header.UsesExtendedL2Entries)
        {
            var subclusterSize = (ulong)Header.ClusterSize / 32UL;
            var subcluster = (int)((virtualOffset % (ulong)Header.ClusterSize) / subclusterSize);
            var allocated = (l2Entry.Bitmap & (1UL << subcluster)) != 0;
            var readsZero = (l2Entry.Bitmap & (1UL << (subcluster + 32))) != 0;
            if (readsZero)
            {
                return ClusterMapping.Zero;
            }

            if (!allocated)
            {
                return ClusterMapping.Unallocated;
            }
        }

        return hostOffset == 0 ? ClusterMapping.Unallocated : ClusterMapping.Standard(hostOffset);
    }

    private ClusterMapping ParseCompressedCluster(ulong l2Entry, ulong guestCluster)
    {
        if (Header.CompressionType is not (0 or 1))
        {
            throw new NotSupportedException($"qcow2 compression_type={Header.CompressionType} の圧縮クラスタは未対応です。");
        }

        var sectorCountBits = (int)Header.ClusterBits - 8;
        var offsetBits = 62 - sectorCountBits;
        if (sectorCountBits <= 0 || offsetBits <= 0 || offsetBits >= 64)
        {
            throw new NotSupportedException($"cluster_bits={Header.ClusterBits} の圧縮クラスタ descriptor は未対応です。");
        }

        var offsetMask = (1UL << offsetBits) - 1;
        var sectorMask = (1UL << sectorCountBits) - 1;
        var hostOffset = l2Entry & offsetMask;
        var additionalSectors = (l2Entry >> offsetBits) & sectorMask;
        var sectorOffset = hostOffset & 0x1ffUL;
        var compressedLength = checked((int)((additionalSectors + 1) * 512 - sectorOffset));

        return ClusterMapping.Compressed(guestCluster, hostOffset, compressedLength);
    }

    private byte[] ReadCompressedCluster(ClusterMapping mapping)
    {
        if (_compressedClusterCache.TryGetValue(mapping.GuestCluster, out var cached))
        {
            return cached;
        }

        var compressed = new byte[mapping.CompressedLength];
        ReadPhysical((long)mapping.HostOffset, compressed, 0, compressed.Length);

        var output = new byte[Header.ClusterSize];
        var total = 0;
        if (Header.CompressionType == 1)
        {
            using var decompressor = new Decompressor();
            var decompressed = decompressor.Unwrap(compressed);
            total = Math.Min(decompressed.Length, output.Length);
            decompressed[..total].CopyTo(output);
        }
        else
        {
            using var input = new MemoryStream(compressed, writable: false);
            using var deflate = new DeflateStream(input, CompressionMode.Decompress, leaveOpen: false);
            while (total < output.Length)
            {
                var read = deflate.Read(output, total, output.Length - total);
                if (read == 0)
                {
                    break;
                }

                total += read;
            }
        }

        if (total != output.Length)
        {
            throw new InvalidDataException($"圧縮クラスタの展開サイズが不足しています: {total:N0}/{output.Length:N0} bytes");
        }

        _compressedClusterCache[mapping.GuestCluster] = output;
        _compressedClusterCacheOrder.Enqueue(mapping.GuestCluster);
        while (_compressedClusterCacheOrder.Count > MaxCompressedClusterCacheEntries)
        {
            var oldKey = _compressedClusterCacheOrder.Dequeue();
            _compressedClusterCache.Remove(oldKey);
        }

        return output;
    }

    private L2Entry[] GetL2Table(ulong l2Offset)
    {
        if (_l2Cache.TryGetValue(l2Offset, out var cached))
        {
            return cached;
        }

        var buffer = new byte[Header.ClusterSize];
        ReadPhysical((long)l2Offset, buffer, 0, buffer.Length);
        var entries = new L2Entry[L2Entries];
        var entrySize = Header.UsesExtendedL2Entries ? 16 : 8;
        for (var i = 0; i < entries.Length; i++)
        {
            var offset = i * entrySize;
            entries[i] = new L2Entry(
                EndianUtilities.ReadUInt64Big(buffer, offset),
                Header.UsesExtendedL2Entries ? EndianUtilities.ReadUInt64Big(buffer, offset + 8) : 0);
        }

        _l2Cache[l2Offset] = entries;
        return entries;
    }

    private static IDiskImageReader? OpenBackingReader(string imagePath, Qcow2Header header)
    {
        if (!header.HasBackingFile || string.IsNullOrWhiteSpace(header.BackingFileName))
        {
            return null;
        }

        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(imagePath)) ?? ".";
        var backingPath = System.IO.Path.IsPathRooted(header.BackingFileName)
            ? header.BackingFileName
            : System.IO.Path.Combine(directory, header.BackingFileName);
        if (!File.Exists(backingPath))
        {
            throw new FileNotFoundException("qcow2 の backing file が見つかりません。", backingPath);
        }

        return DiskImageReaderFactory.Open(backingPath);
    }

    private static FileStream? OpenExternalDataFile(string imagePath, Qcow2Header header)
    {
        if (!header.UsesExternalDataFile || string.IsNullOrWhiteSpace(header.ExternalDataFileName))
        {
            return null;
        }

        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(imagePath)) ?? ".";
        var dataPath = System.IO.Path.IsPathRooted(header.ExternalDataFileName)
            ? header.ExternalDataFileName
            : System.IO.Path.Combine(directory, header.ExternalDataFileName);
        return new FileStream(dataPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    }

    private static void ReadExactAt(FileStream stream, long offset, byte[] buffer, int bufferOffset, int count)
    {
        lock (stream)
        {
            stream.Position = offset;
            var total = 0;
            while (total < count)
            {
                var read = stream.Read(buffer, bufferOffset + total, count - total);
                if (read == 0)
                {
                    throw new EndOfStreamException("qcow2 外部 data file の読み込み中にファイル終端へ到達しました。");
                }
                total += read;
            }
        }
    }

    private void ReadPhysical(long offset, byte[] buffer, int bufferOffset, int count)
    {
        lock (_sync)
        {
            _stream.Position = offset;
            var total = 0;
            while (total < count)
            {
                var read = _stream.Read(buffer, bufferOffset + total, count - total);
                if (read == 0)
                {
                    throw new EndOfStreamException("qcow2 ファイルの物理領域を読み込み中にファイル終端へ到達しました。");
                }

                total += read;
            }
        }
    }

    private static string FormatBytes(long value)
    {
        string[] suffixes = { "B", "KB", "MB", "GB", "TB", "PB" };
        double size = value;
        var suffix = 0;
        while (size >= 1024 && suffix < suffixes.Length - 1)
        {
            size /= 1024;
            suffix++;
        }

        return $"{size:0.##} {suffixes[suffix]}";
    }
}

public sealed record ClusterLookupResult(
    long VirtualClusterIndex,
    long? HostClusterOffset,
    bool ReadsAsZero,
    bool IsCompressed = false,
    int CompressedLength = 0);

internal enum ClusterKind
{
    Unallocated,
    Standard,
    Zero,
    Compressed
}

internal sealed record ClusterMapping(ClusterKind Kind, ulong GuestCluster, ulong HostOffset, int CompressedLength)
{
    public static ClusterMapping Unallocated { get; } = new(ClusterKind.Unallocated, 0, 0, 0);
    public static ClusterMapping Zero { get; } = new(ClusterKind.Zero, 0, 0, 0);
    public static ClusterMapping Standard(ulong hostOffset) => new(ClusterKind.Standard, 0, hostOffset, 0);
    public static ClusterMapping Compressed(ulong guestCluster, ulong hostOffset, int compressedLength) =>
        new(ClusterKind.Compressed, guestCluster, hostOffset, compressedLength);
}

internal readonly record struct L2Entry(ulong Descriptor, ulong Bitmap);

public sealed record Qcow2Snapshot(int Index, string Id, string Name, DateTime TimestampUtc, ulong L1TableOffset, uint L1Size);
