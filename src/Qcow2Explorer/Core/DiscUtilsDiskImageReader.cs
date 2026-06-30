using DiscUtils;
using DiscUtils.Setup;

namespace Qcow2Explorer.Core;

public sealed class DiscUtilsDiskImageReader : IDiskImageReader
{
    private static int _registered;

    private readonly VirtualDisk _disk;
    private readonly Stream _content;
    private readonly object _sync = new();

    private DiscUtilsDiskImageReader(string path, string formatName, VirtualDisk disk)
    {
        Path = path;
        FormatName = formatName;
        _disk = disk;
        _content = disk.Content;
        Length = disk.Capacity;
    }

    public string Path { get; }
    public string FormatName { get; }
    public long Length { get; }

    public static DiscUtilsDiskImageReader Open(string path)
    {
        EnsureRegistered();
        var disk = VirtualDisk.OpenDisk(path, FileAccess.Read);
        if (disk is null)
        {
            throw new InvalidDataException("仮想ディスクを開けませんでした。");
        }

        return new DiscUtilsDiskImageReader(path, DetectFormatName(path), disk);
    }

    public IReadOnlyList<KeyValuePair<string, string>> GetHeaderRows()
    {
        return new List<KeyValuePair<string, string>>
        {
            Row("ファイル", Path),
            Row("形式", FormatName),
            Row("仮想ディスクサイズ", $"{Length:N0} bytes"),
            Row("sector size", _disk.SectorSize.ToString("N0")),
            Row("block size", _disk.BlockSize.ToString("N0")),
            Row("disk class", _disk.DiskClass.ToString()),
            Row("disk type", _disk.DiskTypeInfo?.Name ?? "(不明)")
        };

        static KeyValuePair<string, string> Row(string key, string value) => new(key, value);
    }

    public IReadOnlyList<string> GetWarnings() => Array.Empty<string>();

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
    }

    private static string DetectFormatName(string path)
    {
        var extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".vhd" => "VHD",
            ".vhdx" => "VHDX",
            ".vmdk" => "VMDK",
            _ => "DiscUtils virtual disk"
        };
    }
}
