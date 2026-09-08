using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Qcow2Explorer.Core;

public sealed record PhysicalDiskTargetInfo(
    int DiskNumber,
    string DevicePath,
    long Length,
    uint LogicalSectorSize,
    bool IsRemovable,
    bool IsSystemDisk,
    string Model,
    string SerialNumber,
    string BusType,
    string IdentityToken = "")
{
    public string ConfirmationText => IsRemovable
        ? $"WRITE PHYSICAL DISK {DiskNumber}"
        : $"WRITE FIXED DISK {DiskNumber}";

    public string DisplayName => string.IsNullOrWhiteSpace(Model)
        ? $"PhysicalDrive{DiskNumber}"
        : Model;
}

internal sealed class PhysicalDiskWriteSession : IDiskImageReader, IBlockDevice
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint FileFlagRandomAccess = 0x10000000;
    private const uint FileFlagWriteThrough = 0x80000000;
    private const uint IoctlDiskGetDriveGeometry = 0x00070000;
    private const uint IoctlDiskGetLengthInfo = 0x0007405C;
    private const uint IoctlDiskIsWritable = 0x00070024;
    private const uint IoctlDiskUpdateProperties = 0x00070140;
    private const uint IoctlStorageQueryProperty = 0x002D1400;
    private const uint IoctlStorageGetHotplugInfo = 0x002D0C14;
    private const uint IoctlVolumeGetVolumeDiskExtents = 0x00560000;
    private const uint FsctlLockVolume = 0x00090018;
    private const uint FsctlUnlockVolume = 0x0009001C;
    private const uint FsctlDismountVolume = 0x00090020;
    private const int ErrorNoMoreFiles = 18;
    private const int ErrorMoreData = 234;
    private const int ErrorAccessDenied = 5;
    private const uint StorageDeviceIdProperty = 2;
    private const uint StorageDeviceUniqueIdProperty = 3;

    private readonly SafeFileHandle _diskHandle;
    private readonly List<LockedVolume> _lockedVolumes;
    private bool _disposed;

    private PhysicalDiskWriteSession(
        SafeFileHandle diskHandle,
        PhysicalDiskTargetInfo target,
        List<LockedVolume> lockedVolumes)
    {
        _diskHandle = diskHandle;
        Target = target;
        _lockedVolumes = lockedVolumes;
    }

    public PhysicalDiskTargetInfo Target { get; }
    public string Path => Target.DevicePath;
    public string FormatName => "Locked physical disk";
    public long Length => Target.Length;
    public IReadOnlyList<string> LockedVolumes => _lockedVolumes.Select(volume => volume.Path).ToArray();

    public IReadOnlyList<KeyValuePair<string, string>> GetHeaderRows() =>
    [
        new("デバイス", Target.DevicePath),
        new("形式", FormatName),
        new("ディスクサイズ", $"{Target.Length:N0} bytes"),
        new("論理セクターサイズ", $"{Target.LogicalSectorSize:N0} bytes"),
    ];

    public IReadOnlyList<string> GetWarnings() =>
        ["物理ディスク上の全所属ボリュームを排他ロックした書き込みセッションです。"];

    public string DescribeOffset(long offset) => $"physical disk offset 0x{offset:X}";

    public static PhysicalDiskTargetInfo Inspect(string devicePath)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("物理ディスクの書き込みはWindowsでのみ利用できます。");
        }

        if (!PhysicalDiskReader.IsPhysicalDiskPath(devicePath))
        {
            throw new ArgumentException("物理ディスクのパスが正しくありません。", nameof(devicePath));
        }

        var normalizedPath = NormalizeDiskPath(devicePath, out var diskNumber);
        using var handle = OpenDevice(normalizedPath, 0, writeThrough: false, "情報取得");
        var length = GetLength(handle);
        var sectorSize = GetSectorSize(handle);
        var descriptor = GetStorageDescriptor(handle);
        var hotplug = GetHotplugInfo(handle);
        var isRemovable = descriptor.RemovableMedia || hotplug.MediaRemovable || hotplug.DeviceHotplug;
        return new PhysicalDiskTargetInfo(
            diskNumber,
            normalizedPath,
            length,
            checked((uint)sectorSize),
            isRemovable,
            IsSystemDisk(diskNumber),
            descriptor.Model,
            descriptor.SerialNumber,
            descriptor.BusType,
            GetStorageIdentityToken(handle));
    }

    public static PhysicalDiskWriteSession Open(PhysicalDiskTargetInfo expectedTarget)
    {
        ArgumentNullException.ThrowIfNull(expectedTarget);
        var inspected = Inspect(expectedTarget.DevicePath);
        ValidateIdentity(expectedTarget, inspected);
        if (inspected.IsSystemDisk)
        {
            throw new NotSupportedException(
                "実行中Windowsのシステムディスクには書き込めません。別のWindows環境からオフラインディスクとして開いてください。");
        }

        var lockedVolumes = LockTargetVolumes(inspected.DiskNumber);
        SafeFileHandle? diskHandle = null;
        try
        {
            diskHandle = OpenDevice(
                inspected.DevicePath,
                GenericRead | GenericWrite,
                writeThrough: true,
                "書き込み");
            if (!DeviceIoControl(
                    diskHandle,
                    IoctlDiskIsWritable,
                    IntPtr.Zero,
                    0,
                    IntPtr.Zero,
                    0,
                    out _,
                    IntPtr.Zero))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "選択した物理ディスクはWindowsから書き込み可能と確認できませんでした。");
            }

            var lockedIdentity = InspectHandle(diskHandle, inspected);
            ValidateIdentity(inspected, lockedIdentity);
            return new PhysicalDiskWriteSession(diskHandle, lockedIdentity, lockedVolumes);
        }
        catch
        {
            diskHandle?.Dispose();
            ReleaseLocks(lockedVolumes);
            throw;
        }
    }

    public static bool IsPathOnDisk(string path, int diskNumber)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = System.IO.Path.GetFullPath(path);
        var directory = Directory.Exists(fullPath)
            ? fullPath
            : System.IO.Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException("復旧ジャーナルの保存先フォルダーが見つかりません。");
        }

        var volumePath = new StringBuilder(1024);
        if (!GetVolumePathNameW(directory, volumePath, volumePath.Capacity))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "復旧ジャーナルの保存先ボリュームを特定できませんでした。");
        }

        var volumeName = new StringBuilder(1024);
        if (!GetVolumeNameForVolumeMountPointW(volumePath.ToString(), volumeName, volumeName.Capacity))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "復旧ジャーナルの保存先デバイスを特定できませんでした。");
        }

        using var handle = OpenDevice(volumeName.ToString().TrimEnd('\\'), 0, writeThrough: false, "保存先確認");
        return GetVolumeDiskNumbers(handle).Contains(diskNumber);
    }

    public void ReadAt(long offset, byte[] buffer, int bufferOffset, int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateRange(offset, buffer, bufferOffset, count);
        var total = 0;
        while (total < count)
        {
            var read = RandomAccess.Read(
                _diskHandle,
                buffer.AsSpan(bufferOffset + total, count - total),
                offset + total);
            if (read == 0)
            {
                throw new EndOfStreamException($"物理ディスクの読み取りが途中で終了しました: 0x{offset + total:X}");
            }

            total += read;
        }
    }

    public void WriteAt(long offset, byte[] buffer, int bufferOffset, int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateRange(offset, buffer, bufferOffset, count);
        if (offset % Target.LogicalSectorSize != 0 || count % Target.LogicalSectorSize != 0)
        {
            throw new ArgumentException("物理ディスクへの書き込みは論理セクター境界に揃える必要があります。");
        }

        RandomAccess.Write(_diskHandle, buffer.AsSpan(bufferOffset, count), offset);
    }

    public void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RandomAccess.FlushToDisk(_diskHandle);
    }

    public void RefreshDiskProperties()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _ = DeviceIoControl(
            _diskHandle,
            IoctlDiskUpdateProperties,
            IntPtr.Zero,
            0,
            IntPtr.Zero,
            0,
            out _,
            IntPtr.Zero);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _diskHandle.Dispose();
        ReleaseLocks(_lockedVolumes);
    }

    private static PhysicalDiskTargetInfo InspectHandle(
        SafeFileHandle handle,
        PhysicalDiskTargetInfo knownTarget)
    {
        var descriptor = GetStorageDescriptor(handle);
        var hotplug = GetHotplugInfo(handle);
        return knownTarget with
        {
            Length = GetLength(handle),
            LogicalSectorSize = checked((uint)GetSectorSize(handle)),
            IsRemovable = descriptor.RemovableMedia || hotplug.MediaRemovable || hotplug.DeviceHotplug,
            Model = descriptor.Model,
            SerialNumber = descriptor.SerialNumber,
            BusType = descriptor.BusType,
            IdentityToken = GetStorageIdentityToken(handle),
        };
    }

    private static void ValidateIdentity(PhysicalDiskTargetInfo expected, PhysicalDiskTargetInfo actual)
    {
        if (expected.DiskNumber != actual.DiskNumber
            || !string.Equals(expected.DevicePath, actual.DevicePath, StringComparison.OrdinalIgnoreCase)
            || expected.Length != actual.Length
            || expected.LogicalSectorSize != actual.LogicalSectorSize
            || expected.IsRemovable != actual.IsRemovable
            || (!string.IsNullOrWhiteSpace(expected.Model)
                && !string.Equals(expected.Model, actual.Model, StringComparison.Ordinal))
            || (!string.IsNullOrWhiteSpace(expected.BusType)
                && !string.Equals(expected.BusType, actual.BusType, StringComparison.Ordinal))
            || (!string.IsNullOrWhiteSpace(expected.SerialNumber)
                && !string.Equals(expected.SerialNumber, actual.SerialNumber, StringComparison.Ordinal))
            || (!string.IsNullOrWhiteSpace(expected.IdentityToken)
                && !string.Equals(expected.IdentityToken, actual.IdentityToken, StringComparison.Ordinal)))
        {
            throw new IOException("選択後に物理ディスクの識別情報が変化しました。誤書き込み防止のため中止します。");
        }
    }

    private static List<LockedVolume> LockTargetVolumes(int diskNumber)
    {
        var locked = new List<LockedVolume>();
        try
        {
            foreach (var volumePath in EnumerateVolumePaths())
            {
                using var queryHandle = OpenDevice(volumePath, 0, writeThrough: false, "ボリューム確認");
                if (!GetVolumeDiskNumbers(queryHandle).Contains(diskNumber))
                {
                    continue;
                }

                var handle = OpenDevice(
                    volumePath,
                    GenericRead | GenericWrite,
                    writeThrough: true,
                    "ボリュームロック");
                if (!DeviceIoControl(
                        handle,
                        FsctlLockVolume,
                        IntPtr.Zero,
                        0,
                        IntPtr.Zero,
                        0,
                        out _,
                        IntPtr.Zero))
                {
                    var error = Marshal.GetLastWin32Error();
                    handle.Dispose();
                    throw new Win32Exception(
                        error,
                        $"物理ディスク上のボリュームを排他ロックできませんでした: {volumePath}");
                }

                locked.Add(new LockedVolume(volumePath, handle));
                if (!DeviceIoControl(
                        handle,
                        FsctlDismountVolume,
                        IntPtr.Zero,
                        0,
                        IntPtr.Zero,
                        0,
                        out _,
                        IntPtr.Zero))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        $"物理ディスク上のボリュームをアンマウントできませんでした: {volumePath}");
                }
            }

            return locked;
        }
        catch
        {
            ReleaseLocks(locked);
            throw;
        }
    }

    private static void ReleaseLocks(List<LockedVolume> volumes)
    {
        for (var index = volumes.Count - 1; index >= 0; index--)
        {
            var volume = volumes[index];
            try
            {
                _ = DeviceIoControl(
                    volume.Handle,
                    FsctlUnlockVolume,
                    IntPtr.Zero,
                    0,
                    IntPtr.Zero,
                    0,
                    out _,
                    IntPtr.Zero);
            }
            finally
            {
                volume.Handle.Dispose();
            }
        }

        volumes.Clear();
    }

    private static bool IsSystemDisk(int diskNumber)
    {
        var root = System.IO.Path.GetPathRoot(Environment.SystemDirectory);
        if (string.IsNullOrWhiteSpace(root) || root.Length < 2)
        {
            throw new InvalidOperationException("Windowsシステムボリュームを特定できません。");
        }

        var systemVolumePath = $@"\\.\{root[..2]}";
        using var handle = OpenDevice(systemVolumePath, 0, writeThrough: false, "システムボリューム確認");
        return GetVolumeDiskNumbers(handle).Contains(diskNumber);
    }

    private static IReadOnlySet<int> GetVolumeDiskNumbers(SafeFileHandle volumeHandle)
    {
        var size = 4096;
        while (size <= 1024 * 1024)
        {
            var output = new byte[size];
            if (DeviceIoControl(
                    volumeHandle,
                    IoctlVolumeGetVolumeDiskExtents,
                    null,
                    0,
                    output,
                    output.Length,
                    out var returned,
                    IntPtr.Zero))
            {
                if (returned < sizeof(uint))
                {
                    throw new InvalidDataException("ボリュームのディスク範囲情報が切れています。");
                }

                var count = BinaryPrimitives.ReadUInt32LittleEndian(output);
                var extentOffset = Marshal.OffsetOf<VolumeDiskExtents>(nameof(VolumeDiskExtents.FirstExtent)).ToInt32();
                var extentSize = Marshal.SizeOf<DiskExtent>();
                var diskNumberOffset = Marshal.OffsetOf<DiskExtent>(nameof(DiskExtent.DiskNumber)).ToInt32();
                if (count > 4096 || extentOffset + (long)count * extentSize > returned)
                {
                    throw new InvalidDataException("ボリュームのディスク範囲情報が不正です。");
                }

                var result = new HashSet<int>();
                for (var index = 0; index < count; index++)
                {
                    var offset = checked(extentOffset + index * extentSize + diskNumberOffset);
                    result.Add(checked((int)BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(offset))));
                }

                return result;
            }

            var error = Marshal.GetLastWin32Error();
            if (error != ErrorMoreData)
            {
                throw new Win32Exception(error, "ボリュームが属する物理ディスクを確認できませんでした。");
            }

            size *= 2;
        }

        throw new InvalidDataException("ボリュームのディスク範囲が多すぎます。");
    }

    private static IReadOnlyList<string> EnumerateVolumePaths()
    {
        var buffer = new StringBuilder(1024);
        var findHandle = FindFirstVolumeW(buffer, buffer.Capacity);
        if (findHandle == new IntPtr(-1))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windowsボリュームを列挙できませんでした。");
        }

        var volumes = new List<string>();
        try
        {
            while (true)
            {
                volumes.Add(buffer.ToString().TrimEnd('\\'));
                buffer.Clear();
                if (FindNextVolumeW(findHandle, buffer, buffer.Capacity))
                {
                    continue;
                }

                var error = Marshal.GetLastWin32Error();
                if (error != ErrorNoMoreFiles)
                {
                    throw new Win32Exception(error, "Windowsボリュームの列挙中にエラーが発生しました。");
                }

                return volumes;
            }
        }
        finally
        {
            _ = FindVolumeClose(findHandle);
        }
    }

    private static SafeFileHandle OpenDevice(
        string path,
        uint desiredAccess,
        bool writeThrough,
        string operation)
    {
        var flags = FileAttributeNormal | FileFlagRandomAccess;
        if (writeThrough)
        {
            flags |= FileFlagWriteThrough;
        }

        var handle = CreateFileW(
            path,
            desiredAccess,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            flags,
            IntPtr.Zero);
        if (!handle.IsInvalid)
        {
            return handle;
        }

        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        if (error == ErrorAccessDenied)
        {
            throw new UnauthorizedAccessException($"物理ディスクの{operation}には管理者権限が必要です。");
        }

        throw new Win32Exception(error, $"デバイスを開けませんでした: {path}");
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

        throw new Win32Exception(Marshal.GetLastWin32Error(), "物理ディスクの論理セクターサイズを取得できませんでした。");
    }

    private static StorageDescriptor GetStorageDescriptor(SafeFileHandle handle)
    {
        var input = new byte[8];
        var output = new byte[4096];
        if (!DeviceIoControl(
                handle,
                IoctlStorageQueryProperty,
                input,
                input.Length,
                output,
                output.Length,
                out var returned,
                IntPtr.Zero)
            || returned < 36)
        {
            return new StorageDescriptor(string.Empty, string.Empty, "Unknown", false);
        }

        var vendor = ReadDescriptorString(output, returned, BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(12)));
        var product = ReadDescriptorString(output, returned, BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(16)));
        var serial = ReadDescriptorString(output, returned, BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(24)));
        var busTypeValue = BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(28));
        var model = string.Join(' ', new[] { vendor, product }.Where(value => !string.IsNullOrWhiteSpace(value)));
        return new StorageDescriptor(model, serial, FormatBusType(busTypeValue), output[10] != 0);
    }

    private static HotplugInfo GetHotplugInfo(SafeFileHandle handle)
    {
        var output = new byte[8];
        if (!DeviceIoControl(
                handle,
                IoctlStorageGetHotplugInfo,
                null,
                0,
                output,
                output.Length,
                out var returned,
                IntPtr.Zero)
            || returned < output.Length)
        {
            return default;
        }

        return new HotplugInfo(output[4] != 0, output[5] != 0, output[6] != 0);
    }

    private static string GetStorageIdentityToken(SafeFileHandle handle)
    {
        foreach (var propertyId in new[] { StorageDeviceUniqueIdProperty, StorageDeviceIdProperty })
        {
            var input = new byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(input, propertyId);
            var output = new byte[64 * 1024];
            if (DeviceIoControl(
                    handle,
                    IoctlStorageQueryProperty,
                    input,
                    input.Length,
                    output,
                    output.Length,
                    out var returned,
                    IntPtr.Zero)
                && returned > 8)
            {
                return Convert.ToHexString(SHA256.HashData(output.AsSpan(0, returned)));
            }
        }

        return string.Empty;
    }

    private static string ReadDescriptorString(byte[] data, int length, uint offset)
    {
        if (offset == 0 || offset >= length)
        {
            return string.Empty;
        }

        var start = checked((int)offset);
        var end = start;
        while (end < length && data[end] != 0)
        {
            end++;
        }

        return Encoding.ASCII.GetString(data, start, end - start).Trim();
    }

    private static string FormatBusType(uint busType) => busType switch
    {
        1 => "SCSI",
        3 => "ATA",
        7 => "USB",
        8 => "RAID",
        10 => "SAS",
        11 => "SATA",
        12 => "SD",
        13 => "MMC",
        17 => "NVMe",
        18 => "SCM",
        19 => "UFS",
        _ => $"BusType {busType}",
    };

    private static string NormalizeDiskPath(string path, out int diskNumber)
    {
        var suffix = path.Trim()[@"\\.\PhysicalDrive".Length..];
        diskNumber = int.Parse(suffix, System.Globalization.CultureInfo.InvariantCulture);
        return $@"\\.\PhysicalDrive{diskNumber}";
    }

    private void ValidateRange(long offset, byte[] buffer, int bufferOffset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(bufferOffset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (bufferOffset > buffer.Length - count || offset > Length - count)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "物理ディスクの範囲外です。");
        }
    }

    private sealed record LockedVolume(string Path, SafeFileHandle Handle);
    private readonly record struct StorageDescriptor(
        string Model,
        string SerialNumber,
        string BusType,
        bool RemovableMedia);
    private readonly record struct HotplugInfo(bool MediaRemovable, bool MediaHotplug, bool DeviceHotplug);

    [StructLayout(LayoutKind.Sequential)]
    private struct VolumeDiskExtents
    {
        public uint NumberOfDiskExtents;
        public DiskExtent FirstExtent;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DiskExtent
    {
        public uint DiskNumber;
        public long StartingOffset;
        public long ExtentLength;
    }

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
        IntPtr outputBuffer,
        int outputBufferSize,
        out int bytesReturned,
        IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint controlCode,
        byte[]? inputBuffer,
        int inputBufferSize,
        byte[] outputBuffer,
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
    private static extern IntPtr FindFirstVolumeW(StringBuilder volumeName, int bufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextVolumeW(IntPtr findVolume, StringBuilder volumeName, int bufferLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindVolumeClose(IntPtr findVolume);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathNameW(
        string fileName,
        StringBuilder volumePathName,
        int bufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPointW(
        string volumeMountPoint,
        StringBuilder volumeName,
        int bufferLength);
}
