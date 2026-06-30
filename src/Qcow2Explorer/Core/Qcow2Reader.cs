using System.IO.Compression;

namespace Qcow2Explorer.Core;

public sealed class Qcow2Reader : IBlockReader, IDisposable
{
    private const ulong OffsetMask = 0x00fffffffffffe00UL;
    private const ulong CompressedClusterBit = 1UL << 62;
    private const ulong ZeroClusterBit = 1UL;
    private const int MaxCompressedClusterCacheEntries = 512;

    private readonly FileStream _stream;
    private readonly object _sync = new();
    private readonly ulong[] _l1Table;
    private readonly Dictionary<ulong, ulong[]> _l2Cache = new();
    private readonly Dictionary<ulong, byte[]> _compressedClusterCache = new();
    private readonly Queue<ulong> _compressedClusterCacheOrder = new();

    public Qcow2Header Header { get; }
    public string Path { get; }
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
        L2Entries = checked((int)(Header.ClusterSize / 8));
        _l1Table = ReadL1Table();
    }

    public IReadOnlyList<string> GetWarnings() => Header.GetReadWarnings();

    public IReadOnlyList<KeyValuePair<string, string>> GetHeaderRows()
    {
        return new List<KeyValuePair<string, string>>
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
            var chunk = (int)Math.Min(remaining, (long)clusterSize - clusterOffset);
            var mapping = ResolveCluster(virtualOffset);
            if (mapping.Kind == ClusterKind.Standard)
            {
                ReadPhysical((long)(mapping.HostOffset + (ulong)clusterOffset), buffer, outOffset, chunk);
            }
            else if (mapping.Kind == ClusterKind.Compressed)
            {
                var cluster = ReadCompressedCluster(mapping);
                Array.Copy(cluster, clusterOffset, buffer, outOffset, chunk);
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

    public void Dispose()
    {
        _stream.Dispose();
    }

    private ulong[] ReadL1Table()
    {
        var entries = new ulong[Header.L1Size];
        var buffer = new byte[checked((int)Header.L1Size * 8)];
        ReadPhysical((long)Header.L1TableOffset, buffer, 0, buffer.Length);
        for (var i = 0; i < entries.Length; i++)
        {
            entries[i] = EndianUtilities.ReadUInt64Big(buffer, i * 8);
        }

        return entries;
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
        if ((l2Entry & CompressedClusterBit) != 0)
        {
            return ParseCompressedCluster(l2Entry, guestCluster);
        }

        if ((l2Entry & ZeroClusterBit) != 0)
        {
            return ClusterMapping.Zero;
        }

        var hostOffset = l2Entry & OffsetMask;
        return hostOffset == 0 ? ClusterMapping.Unallocated : ClusterMapping.Standard(hostOffset);
    }

    private ClusterMapping ParseCompressedCluster(ulong l2Entry, ulong guestCluster)
    {
        if (Header.CompressionType != 0)
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
        using var input = new MemoryStream(compressed, writable: false);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress, leaveOpen: false);
        var total = 0;
        while (total < output.Length)
        {
            var read = deflate.Read(output, total, output.Length - total);
            if (read == 0)
            {
                break;
            }

            total += read;
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

    private ulong[] GetL2Table(ulong l2Offset)
    {
        if (_l2Cache.TryGetValue(l2Offset, out var cached))
        {
            return cached;
        }

        var buffer = new byte[Header.ClusterSize];
        ReadPhysical((long)l2Offset, buffer, 0, buffer.Length);
        var entries = new ulong[L2Entries];
        for (var i = 0; i < entries.Length; i++)
        {
            entries[i] = EndianUtilities.ReadUInt64Big(buffer, i * 8);
        }

        _l2Cache[l2Offset] = entries;
        return entries;
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
