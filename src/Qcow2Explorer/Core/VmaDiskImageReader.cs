using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace Qcow2Explorer.Core;

public sealed class VmaDiskImageReader : IDiskImageReader
{
    private static ReadOnlySpan<byte> HeaderMagic => "VMA\0"u8;
    private static ReadOnlySpan<byte> ExtentMagic => "VMAE"u8;

    private const int FixedHeaderSize = 12 * 1024;
    private const int ExtentHeaderSize = 512;
    private const int BlocksPerExtent = 59;
    private const int BlockSize = 4 * 1024;
    private const int ClusterSize = 64 * 1024;

    private readonly IDiskImageReader _source;
    private readonly IProgress<DiskImageProgress>? _progress;
    private readonly Dictionary<byte, Dictionary<uint, VmaClusterMap>> _clusterMaps = [];
    private readonly List<string> _warnings = [];
    private long _extentCount;
    private long _storedBlockCount;
    private int _activeDeviceIndex;

    public VmaDiskImageReader(IDiskImageReader source, IProgress<DiskImageProgress>? progress = null)
    {
        _source = source;
        _progress = progress;
        Path = source.Path;
        FormatName = source switch
        {
            TemporaryLzopDiskImageReader => "Proxmox VMA (lzop高速モード)",
            LzopDiskImageReader => "Proxmox VMA (lzop LZO1X)",
            _ => "Proxmox VMA"
        };

        try
        {
            ReadHeader();
            BuildExtentIndex();
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    public string Path { get; }
    public string FormatName { get; }
    public long Length => ActiveDevice.Size;
    public uint Version { get; private set; }
    public Guid ArchiveUuid { get; private set; }
    public ulong CreationTimeUnix { get; private set; }
    public uint HeaderSize { get; private set; }
    public IReadOnlyList<VmaDevice> Devices { get; private set; } = [];
    public int ActiveDeviceIndex => _activeDeviceIndex;
    public VmaDevice ActiveDevice => Devices[_activeDeviceIndex];

    public static bool HasMagic(IBlockReader reader)
    {
        if (reader.Length < HeaderMagic.Length)
        {
            return false;
        }

        var magic = new byte[HeaderMagic.Length];
        reader.ReadAt(0, magic, 0, magic.Length);
        return magic.AsSpan().SequenceEqual(HeaderMagic);
    }

    public void SelectDevice(int index)
    {
        if ((uint)index >= (uint)Devices.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        _activeDeviceIndex = index;
    }

    public IReadOnlyList<KeyValuePair<string, string>> GetHeaderRows()
    {
        return
        [
            Row("ファイル", Path),
            Row("形式", FormatName),
            Row("VMA version", Version.ToString()),
            Row("VMA UUID", ArchiveUuid.ToString()),
            Row("バックアップ日時", FormatCreationTime()),
            Row("VMAヘッダーサイズ", $"{HeaderSize:N0} bytes"),
            Row("格納ディスク数", $"{Devices.Count:N0}"),
            Row("選択中ディスク", $"{ActiveDevice.Name} (device {ActiveDevice.Id})"),
            Row("選択中ディスクサイズ", $"{Length:N0} bytes"),
            Row("VMAエクステント数", $"{_extentCount:N0}"),
            Row("格納済み4 KiBブロック数", $"{_storedBlockCount:N0}")
        ];

        static KeyValuePair<string, string> Row(string key, string value) => new(key, value);
    }

    public IReadOnlyList<string> GetWarnings()
    {
        return _source.GetWarnings()
            .Concat(_warnings)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    public string DescribeOffset(long offset)
    {
        if (offset < 0 || offset >= Length)
        {
            return $"VMA device {ActiveDevice.Id} offset 0x{offset:X}";
        }

        var clusterNumber = checked((uint)(offset / ClusterSize));
        var blockIndex = checked((int)((offset % ClusterSize) / BlockSize));
        if (!TryGetStoredBlock(ActiveDevice.Id, clusterNumber, blockIndex, out var sourceOffset))
        {
            return $"VMA device {ActiveDevice.Id}, cluster {clusterNumber:N0}, sparse zero block";
        }

        return $"VMA device {ActiveDevice.Id}, cluster {clusterNumber:N0}, archive offset 0x{sourceOffset:X}";
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
        while (remaining > 0)
        {
            var clusterNumber = checked((uint)(offset / ClusterSize));
            var inCluster = checked((int)(offset % ClusterSize));
            var blockIndex = inCluster / BlockSize;
            var inBlock = inCluster % BlockSize;
            var chunk = Math.Min(remaining, BlockSize - inBlock);

            if (TryGetStoredBlock(ActiveDevice.Id, clusterNumber, blockIndex, out var sourceOffset))
            {
                _source.ReadAt(sourceOffset + inBlock, buffer, bufferOffset, chunk);
            }

            offset += chunk;
            bufferOffset += chunk;
            remaining -= chunk;
        }
    }

    public void Dispose()
    {
        _source.Dispose();
    }

    private void ReadHeader()
    {
        if (_source.Length < FixedHeaderSize)
        {
            throw new InvalidDataException("VMAヘッダーが途中で終了しています。");
        }

        var prefix = ReadExactAt(0, 60);
        if (!prefix.AsSpan(0, 4).SequenceEqual(HeaderMagic))
        {
            throw new InvalidDataException("VMAマジックが一致しません。");
        }

        Version = BinaryPrimitives.ReadUInt32BigEndian(prefix.AsSpan(4, 4));
        if (Version != 1)
        {
            throw new NotSupportedException($"VMA version {Version} は未対応です。");
        }

        ArchiveUuid = new Guid(prefix.AsSpan(8, 16), bigEndian: true);
        CreationTimeUnix = BinaryPrimitives.ReadUInt64BigEndian(prefix.AsSpan(24, 8));
        var blobBufferOffset = BinaryPrimitives.ReadUInt32BigEndian(prefix.AsSpan(48, 4));
        var blobBufferSize = BinaryPrimitives.ReadUInt32BigEndian(prefix.AsSpan(52, 4));
        HeaderSize = BinaryPrimitives.ReadUInt32BigEndian(prefix.AsSpan(56, 4));

        if (HeaderSize < FixedHeaderSize || HeaderSize > _source.Length || HeaderSize % 512 != 0)
        {
            throw new InvalidDataException($"VMAヘッダーサイズが不正です: {HeaderSize:N0} bytes");
        }

        if (blobBufferOffset < FixedHeaderSize
            || blobBufferOffset > HeaderSize
            || blobBufferSize > HeaderSize - blobBufferOffset)
        {
            throw new InvalidDataException(
                $"VMA blob bufferの範囲が不正です: offset={blobBufferOffset:N0}, size={blobBufferSize:N0}");
        }

        var header = ReadExactAt(0, checked((int)HeaderSize));
        VerifyMd5(header, 32, "VMAヘッダー");

        var devices = new List<VmaDevice>();
        for (var deviceId = 1; deviceId < 256; deviceId++)
        {
            var infoOffset = 4096 + deviceId * 32;
            var namePointer = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(infoOffset, 4));
            var sizeValue = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(infoOffset + 8, 8));
            if (namePointer == 0 && sizeValue == 0)
            {
                continue;
            }

            if (namePointer == 0 || sizeValue == 0 || sizeValue > long.MaxValue)
            {
                throw new InvalidDataException($"VMA device {deviceId} の名前またはサイズが不正です。");
            }

            var name = ReadBlobString(header, blobBufferOffset, blobBufferSize, namePointer);
            if (string.Equals(name, "vmstate", StringComparison.Ordinal))
            {
                _warnings.Add($"VMA device {deviceId} のVMメモリ状態 (vmstate) はディスク一覧から除外しました。");
                continue;
            }

            var device = new VmaDevice(checked((byte)deviceId), name, checked((long)sizeValue));
            devices.Add(device);
            _clusterMaps.Add(device.Id, []);
        }

        if (devices.Count == 0)
        {
            throw new InvalidDataException("VMA内に読み取り可能な仮想ディスクがありません。");
        }

        Devices = devices;
        _activeDeviceIndex = devices
            .Select((device, index) => (device, index))
            .MaxBy(item => item.device.Size)
            .index;
    }

    private void BuildExtentIndex()
    {
        var position = (long)HeaderSize;
        var lastPercentage = -1;
        ReportProgress(force: true);

        while (position < _source.Length)
        {
            if (_source.Length - position < ExtentHeaderSize)
            {
                throw new InvalidDataException($"VMA末尾に不完全なエクステントがあります: offset=0x{position:X}");
            }

            var header = ReadExactAt(position, ExtentHeaderSize);
            if (!header.AsSpan(0, 4).SequenceEqual(ExtentMagic))
            {
                throw new InvalidDataException($"VMAエクステントマジックが一致しません: offset=0x{position:X}");
            }

            if (!header.AsSpan(8, 16).SequenceEqual(ArchiveUuid.ToByteArray(bigEndian: true)))
            {
                throw new InvalidDataException($"VMAエクステントのUUIDが一致しません: offset=0x{position:X}");
            }

            VerifyMd5(header, 24, $"VMAエクステント #{_extentCount + 1:N0}");
            var expectedBlockCount = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(6, 2));
            var dataOffset = position + ExtentHeaderSize;
            var actualBlockCount = 0;

            for (var entryIndex = 0; entryIndex < BlocksPerExtent; entryIndex++)
            {
                var infoOffset = 40 + entryIndex * 8;
                var mask = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(infoOffset, 2));
                var deviceId = header[infoOffset + 3];
                var clusterNumber = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(infoOffset + 4, 4));
                var storedBlocks = BitOperations.PopCount(mask);

                if (deviceId == 0)
                {
                    if (mask != 0 || clusterNumber != 0)
                    {
                        throw new InvalidDataException(
                            $"VMAエクステント #{_extentCount + 1:N0} にdevice IDなしのブロック情報があります。");
                    }

                    continue;
                }

                if (!_clusterMaps.TryGetValue(deviceId, out var deviceMap))
                {
                    throw new InvalidDataException(
                        $"VMAエクステント #{_extentCount + 1:N0} が未定義のdevice {deviceId}を参照しています。");
                }

                var device = Devices.First(item => item.Id == deviceId);
                if ((long)clusterNumber * ClusterSize >= device.Size)
                {
                    throw new InvalidDataException(
                        $"VMA device {deviceId} のクラスタ{clusterNumber:N0}がディスク末尾を超えています。");
                }

                if (deviceMap.ContainsKey(clusterNumber))
                {
                    throw new InvalidDataException(
                        $"VMA device {deviceId} のクラスタ{clusterNumber:N0}が重複しています。");
                }

                deviceMap.Add(clusterNumber, new VmaClusterMap(mask, dataOffset));
                actualBlockCount += storedBlocks;
                dataOffset = checked(dataOffset + (long)storedBlocks * BlockSize);
            }

            if (actualBlockCount != expectedBlockCount)
            {
                throw new InvalidDataException(
                    $"VMAエクステント #{_extentCount + 1:N0} のブロック数が一致しません。"
                    + $" expected={expectedBlockCount:N0}, actual={actualBlockCount:N0}");
            }

            if (dataOffset > _source.Length)
            {
                throw new EndOfStreamException($"VMAエクステント #{_extentCount + 1:N0} のデータが途中で終了しています。");
            }

            position = dataOffset;
            _extentCount++;
            _storedBlockCount += actualBlockCount;
            ReportProgress(force: false);
        }

        _progress?.Report(new DiskImageProgress(
            $"VMA索引作成完了: {_extentCount:N0}エクステント / {Devices.Count:N0}ディスク",
            _source.Length,
            _source.Length));

        void ReportProgress(bool force)
        {
            if (_progress is null)
            {
                return;
            }

            var percentage = _source.Length == 0
                ? 100
                : (int)Math.Clamp((double)position / _source.Length * 100, 0, 100);
            if (!force && percentage == lastPercentage)
            {
                return;
            }

            lastPercentage = percentage;
            _progress.Report(new DiskImageProgress(
                $"VMA索引作成中: {_extentCount:N0}エクステント",
                position,
                _source.Length));
        }
    }

    private bool TryGetStoredBlock(byte deviceId, uint clusterNumber, int blockIndex, out long sourceOffset)
    {
        if (_clusterMaps[deviceId].TryGetValue(clusterNumber, out var cluster)
            && (cluster.Mask & (1 << blockIndex)) != 0)
        {
            var precedingMask = (uint)cluster.Mask & ((1u << blockIndex) - 1);
            sourceOffset = cluster.DataOffset + (long)BitOperations.PopCount(precedingMask) * BlockSize;
            return true;
        }

        sourceOffset = 0;
        return false;
    }

    private byte[] ReadExactAt(long offset, int count)
    {
        if (offset < 0 || count < 0 || offset > _source.Length - count)
        {
            throw new EndOfStreamException($"VMAデータが途中で終了しています: offset=0x{offset:X}, length={count:N0}");
        }

        var data = new byte[count];
        _source.ReadAt(offset, data, 0, count);
        return data;
    }

    private static string ReadBlobString(
        byte[] header,
        uint blobBufferOffset,
        uint blobBufferSize,
        uint pointer)
    {
        var blobOffset = checked((long)blobBufferOffset + pointer);
        var blobEnd = checked((long)blobBufferOffset + blobBufferSize);
        if (pointer >= blobBufferSize || blobOffset > blobEnd - 2)
        {
            throw new InvalidDataException($"VMA blob pointerが範囲外です: {pointer:N0}");
        }

        var size = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(checked((int)blobOffset), 2));
        if (blobOffset + 2 + size > blobEnd)
        {
            throw new InvalidDataException($"VMA blobが途中で終了しています: pointer={pointer:N0}, size={size:N0}");
        }

        return Encoding.UTF8.GetString(header, checked((int)blobOffset + 2), size).TrimEnd('\0');
    }

    private static void VerifyMd5(byte[] data, int checksumOffset, string label)
    {
        var expected = data.AsSpan(checksumOffset, 16).ToArray();
        Array.Clear(data, checksumOffset, 16);
        var actual = MD5.HashData(data);
        expected.CopyTo(data, checksumOffset);
        if (!actual.AsSpan().SequenceEqual(expected))
        {
            throw new InvalidDataException($"{label}のMD5チェックサムが一致しません。");
        }
    }

    private string FormatCreationTime()
    {
        if (CreationTimeUnix > long.MaxValue)
        {
            return $"{CreationTimeUnix:N0} (Unix time)";
        }

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds((long)CreationTimeUnix)
                .ToLocalTime()
                .ToString("yyyy-MM-dd HH:mm:ss zzz");
        }
        catch (ArgumentOutOfRangeException)
        {
            return $"{CreationTimeUnix:N0} (Unix time)";
        }
    }

    private sealed record VmaClusterMap(ushort Mask, long DataOffset);
}

public sealed record VmaDevice(byte Id, string Name, long Size)
{
    public override string ToString()
    {
        return $"{Name} (device {Id}, {Size:N0} bytes)";
    }
}
