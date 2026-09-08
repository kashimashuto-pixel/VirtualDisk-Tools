using DiscUtils;
using DiscUtils.Setup;
using VhdxDisk = DiscUtils.Vhdx.Disk;
using VhdxLayer = DiscUtils.Vhdx.DiskImageFile;

namespace Qcow2Explorer.Core;

public sealed class DiscUtilsDiskImageReader : IDiskImageReader
{
    private static int _registered;

    private readonly VirtualDisk _disk;
    private readonly Stream _content;
    private readonly IReadOnlyList<string> _layerPaths;
    private readonly IReadOnlyList<string> _warnings;
    private readonly object _sync = new();

    private DiscUtilsDiskImageReader(string path, string formatName, VirtualDisk disk)
    {
        Path = path;
        FormatName = formatName;
        _disk = disk;
        _content = disk.Content;
        Length = disk.Capacity;
        if (disk is VhdxDisk vhdx)
        {
            var layers = vhdx.Layers.Cast<VhdxLayer>().ToArray();
            ValidateVhdxChain(layers);
            _layerPaths = layers
                .Select(layer => string.IsNullOrWhiteSpace(layer.FullPath) ? "(path unavailable)" : layer.FullPath)
                .ToArray();
            _warnings = layers.Length > 1
                ? [$"VHDX差分チェーンを{layers.Length:N0}層で読み取っています。親ディスクを移動・変更すると、このスナップショットを正しく読めなくなります。"]
                : [];
        }
        else
        {
            _layerPaths = [];
            _warnings = [];
        }
    }

    public string Path { get; }
    public string FormatName { get; }
    public long Length { get; }

    public static DiscUtilsDiskImageReader Open(string path)
    {
        EnsureRegistered();
        var extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
        VirtualDisk? disk = null;
        try
        {
            disk = extension is ".vhdx" or ".avhdx"
                ? new VhdxDisk(System.IO.Path.GetFullPath(path), FileAccess.Read)
                : VirtualDisk.OpenDisk(path, FileAccess.Read);

            if (disk is null)
            {
                throw new InvalidDataException("仮想ディスクを開けませんでした。");
            }

            return new DiscUtilsDiskImageReader(path, DetectFormatName(path), disk);
        }
        catch (Exception ex) when (extension == ".avhdx" && ex is IOException or InvalidDataException)
        {
            disk?.Dispose();
            throw new InvalidDataException(
                "AVHDX差分ディスクを開けませんでした。親VHDX/AVHDXが元の相対位置または記録されたパスにあり、チェーン全体が揃っているか確認してください。",
                ex);
        }
        catch
        {
            disk?.Dispose();
            throw;
        }
    }

    public IReadOnlyList<KeyValuePair<string, string>> GetHeaderRows()
    {
        var rows = new List<KeyValuePair<string, string>>
        {
            Row("ファイル", Path),
            Row("形式", FormatName),
            Row("仮想ディスクサイズ", $"{Length:N0} bytes"),
            Row("sector size", _disk.SectorSize.ToString("N0")),
            Row("block size", _disk.BlockSize.ToString("N0")),
            Row("disk class", _disk.DiskClass.ToString()),
            Row("disk type", _disk.DiskTypeInfo?.Name ?? "(不明)")
        };

        if (_layerPaths.Count > 0)
        {
            rows.Add(Row("VHDXレイヤー数", _layerPaths.Count.ToString("N0")));
            for (var index = 0; index < _layerPaths.Count; index++)
            {
                rows.Add(Row($"レイヤー #{index + 1}", _layerPaths[index]));
            }
        }

        return rows;

        static KeyValuePair<string, string> Row(string key, string value) => new(key, value);
    }

    public IReadOnlyList<string> GetWarnings() => _warnings;

    public string DescribeOffset(long offset)
    {
        return $"{FormatName} virtual offset 0x{offset:X}";
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
            _content.Position = offset;
            var total = 0;
            while (total < remaining)
            {
                var read = _content.Read(buffer, bufferOffset + total, remaining - total);
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
        _disk.Dispose();
    }

    private static void EnsureRegistered()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1)
        {
            return;
        }

        SetupHelper.RegisterAssembly(typeof(DiscUtils.Vhd.Disk).Assembly);
        SetupHelper.RegisterAssembly(typeof(DiscUtils.Vhdx.Disk).Assembly);
        SetupHelper.RegisterAssembly(typeof(DiscUtils.Vmdk.Disk).Assembly);
        SetupHelper.RegisterAssembly(typeof(DiscUtils.Vdi.Disk).Assembly);
    }

    private static string DetectFormatName(string path)
    {
        var extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".vhd" => "VHD",
            ".vhdx" => "VHDX",
            ".avhdx" => "AVHDX (VHDX differencing)",
            ".vmdk" => "VMDK",
            ".vdi" => "VDI",
            _ => "DiscUtils virtual disk"
        };
    }

    private static void ValidateVhdxChain(IReadOnlyList<VhdxLayer> layers)
    {
        if (layers.Count == 0)
        {
            throw new InvalidDataException("VHDXレイヤーがありません。");
        }

        if (layers.Count > 64)
        {
            throw new InvalidDataException("VHDX差分チェーンが64層を超えています。");
        }

        var ids = new HashSet<Guid>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var capacity = layers[0].Capacity;
        var logicalSectorSize = layers[0].LogicalSectorSize;
        for (var index = 0; index < layers.Count; index++)
        {
            var layer = layers[index];
            if (layer.Capacity != capacity || layer.LogicalSectorSize != logicalSectorSize)
            {
                throw new InvalidDataException("VHDX差分チェーンの容量または論理sector sizeが一致しません。");
            }

            if (!string.IsNullOrWhiteSpace(layer.FullPath)
                && !paths.Add(System.IO.Path.GetFullPath(layer.FullPath)))
            {
                throw new InvalidDataException("VHDX差分チェーンで同じファイルが複数回参照されています。");
            }

            if (!ids.Add(layer.UniqueId))
            {
                throw new InvalidDataException("VHDX差分チェーンで循環または重複したUnique IDを検出しました。");
            }

            if (index + 1 < layers.Count)
            {
                if (!layer.NeedsParent || layer.ParentUniqueId != layers[index + 1].UniqueId)
                {
                    throw new InvalidDataException("VHDX差分チェーンの親Unique IDが一致しません。");
                }
            }
            else if (layer.NeedsParent)
            {
                throw new InvalidDataException("VHDX差分チェーンの最終親が見つかりません。");
            }
        }
    }
}
