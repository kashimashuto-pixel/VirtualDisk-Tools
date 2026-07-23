using System.Buffers;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace Qcow2Explorer.Core;

public sealed partial class PhysicalDiskReader : IDiskImageReader, ILogicalSectorReader
{
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint FileFlagRandomAccess = 0x10000000;
    private const uint IoctlDiskGetDriveGeometry = 0x00070000;
    private const uint IoctlDiskGetLengthInfo = 0x0007405C;
    private const int ErrorAccessDenied = 5;
    private const int ErrorInsufficientBuffer = 122;

    private readonly SafeFileHandle _handle;
    private readonly int _sectorSize;

    public PhysicalDiskReader(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("物理ディスクの直接読み取りはWindowsでのみ利用できます。");
        }

        if (!IsPhysicalDiskPath(path))
        {
            throw new ArgumentException("物理ディスクのパスが正しくありません。", nameof(path));
        }

        Path = NormalizePath(path);
        _handle = OpenHandle(Path, GenericRead);
        try
        {
            Length = GetLength(_handle);
            _sectorSize = GetSectorSize(_handle);
        }
        catch
        {
            _handle.Dispose();
            throw;
        }
    }

    public string Path { get; }
    public string FormatName => "Physical disk (read-only)";
    public long Length { get; }
    public uint LogicalSectorSize => checked((uint)_sectorSize);

    public static IReadOnlyList<PhysicalDiskInfo> Enumerate()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Array.Empty<PhysicalDiskInfo>();
        }

        var names = QueryDeviceNames();
        var disks = new List<PhysicalDiskInfo>();
        foreach (var name in names)
        {
            var match = PhysicalDriveNameRegex().Match(name);
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out var number))
            {
                continue;
            }

            var path = $@"\\.\PhysicalDrive{number}";
            long? length = null;
            try
            {
                using var handle = OpenHandle(path, 0);
                length = GetLength(handle);
            }
            catch
            {
                // Device names can be listed without permission to read their metadata.
            }

            disks.Add(new PhysicalDiskInfo(number, path, length));
        }

        return disks.OrderBy(disk => disk.Number).ToList();
    }

    public static bool IsPhysicalDiskPath(string path)
    {
        return PhysicalDrivePathRegex().IsMatch(path.Trim());
    }

    public IReadOnlyList<KeyValuePair<string, string>> GetHeaderRows()
    {
        return new List<KeyValuePair<string, string>>
        {
            new("デバイス", Path),
            new("形式", FormatName),
            new("ディスクサイズ", $"{Length:N0} bytes"),
            new("論理セクターサイズ", $"{_sectorSize:N0} bytes")
        };
    }

    public IReadOnlyList<string> GetWarnings()
    {
        return new[]
        {
            "物理ディスクを読み取り専用で直接参照しています。アプリからディスクへの書き込みは行いません。",
            "使用中のディスクは解析中にも内容が変化するため、一覧やファイル内容が一時的に整合しない場合があります。"
        };
    }

    public string DescribeOffset(long offset) => $"physical disk offset 0x{offset:X}";

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

        var available = checked((int)Math.Min(count, Length - offset));
        var alignedOffset = offset / _sectorSize * _sectorSize;
        var alignedEnd = Math.Min(Length, RoundUp(offset + available, _sectorSize));
        var alignedLength = checked((int)(alignedEnd - alignedOffset));

        if (alignedOffset == offset && alignedLength == available)
        {
            ReadExact(alignedOffset, buffer.AsSpan(bufferOffset, available));
            return;
        }

        var rented = ArrayPool<byte>.Shared.Rent(alignedLength);
        try
        {
            var aligned = rented.AsSpan(0, alignedLength);
            aligned.Clear();
            ReadExact(alignedOffset, aligned);
            aligned.Slice(checked((int)(offset - alignedOffset)), available)
                .CopyTo(buffer.AsSpan(bufferOffset, available));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public void Dispose() => _handle.Dispose();

    private void ReadExact(long offset, Span<byte> destination)
    {
        var total = 0;
        while (total < destination.Length)
        {
            var read = RandomAccess.Read(_handle, destination[total..], offset + total);
            if (read == 0)
            {
                break;
            }

            total += read;
        }
    }

    private static long RoundUp(long value, int alignment)
    {
        var remainder = value % alignment;
        return remainder == 0 ? value : checked(value + alignment - remainder);
    }

    private static SafeFileHandle OpenHandle(string path, uint desiredAccess)
    {
        var handle = CreateFileW(
            path,
            desiredAccess,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            FileAttributeNormal | FileFlagRandomAccess,
            IntPtr.Zero);
        if (!handle.IsInvalid)
        {
            return handle;
        }

        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        if (error == ErrorAccessDenied)
        {
            throw new UnauthorizedAccessException("物理ディスクの読み取りには管理者権限が必要です。");
        }

        throw new Win32Exception(error, $"物理ディスクを開けませんでした: {path}");
    }

    private static long GetLength(SafeFileHandle handle)
    {
        if (!DeviceIoControl(
                handle,
                IoctlDiskGetLengthInfo,
                IntPtr.Zero,
                0,
                out long length,
                sizeof(long),
                out _,
                IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "物理ディスクのサイズを取得できませんでした。");
        }

        return length;
    }

    private static int GetSectorSize(SafeFileHandle handle)
    {
        if (DeviceIoControl(
                handle,
                IoctlDiskGetDriveGeometry,
                IntPtr.Zero,
                0,
                out DiskGeometry geometry,
                Marshal.SizeOf<DiskGeometry>(),
                out _,
                IntPtr.Zero)
            && geometry.BytesPerSector is >= 512 and <= 65536)
        {
            return checked((int)geometry.BytesPerSector);
        }

        return 512;
    }

    private static IReadOnlyList<string> QueryDeviceNames()
    {
        var capacity = 32768;
        while (capacity <= 1024 * 1024)
        {
            var buffer = new char[capacity];
            var length = QueryDosDeviceW(null, buffer, buffer.Length);
            if (length > 0)
            {
                return new string(buffer, 0, checked((int)length))
                    .Split('\0', StringSplitOptions.RemoveEmptyEntries);
            }

            var error = Marshal.GetLastWin32Error();
            if (error != ErrorInsufficientBuffer)
            {
                throw new Win32Exception(error, "物理ディスクの一覧を取得できませんでした。");
            }

            capacity *= 2;
        }

        throw new InvalidOperationException("物理ディスクの一覧が大きすぎます。");
    }

    private static string NormalizePath(string path)
    {
        var match = PhysicalDrivePathRegex().Match(path.Trim());
        return $@"\\.\PhysicalDrive{match.Groups[1].Value}";
    }

    [GeneratedRegex(@"^PhysicalDrive(\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PhysicalDriveNameRegex();

    [GeneratedRegex(@"^\\\\\.\\PhysicalDrive(\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PhysicalDrivePathRegex();

    [StructLayout(LayoutKind.Sequential)]
    private struct DiskGeometry
    {
        public long Cylinders;
        public int MediaType;
        public uint TracksPerCylinder;
        public uint SectorsPerTrack;
        public uint BytesPerSector;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint controlCode,
        IntPtr inputBuffer,
        int inputBufferSize,
        out long outputBuffer,
        int outputBufferSize,
        out int bytesReturned,
        IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint controlCode,
        IntPtr inputBuffer,
        int inputBufferSize,
        out DiskGeometry outputBuffer,
        int outputBufferSize,
        out int bytesReturned,
        IntPtr overlapped);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDeviceW(string? deviceName, char[] targetPath, int maxLength);
}

public sealed record PhysicalDiskInfo(int Number, string DevicePath, long? Length)
{
    public override string ToString()
    {
        return Length.HasValue
            ? $"ディスク {Number}  ({FormatBytes(Length.Value)})  {DevicePath}"
            : $"ディスク {Number}  (サイズ取得には管理者権限が必要)  {DevicePath}";
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
