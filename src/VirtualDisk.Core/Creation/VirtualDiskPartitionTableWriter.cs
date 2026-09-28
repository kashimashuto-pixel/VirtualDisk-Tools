using System.Buffers.Binary;
using System.Text;

namespace Qcow2Explorer.Creation;

public enum VirtualDiskPartitionTableKind
{
    Mbr,
    Gpt,
}

public enum VirtualDiskFileSystemKind
{
    Xfs,
    Ext4,
    Ntfs,
}

public sealed record VirtualDiskPartitionDefinition(
    long SizeBytes,
    string Name,
    string VolumeLabel,
    VirtualDiskFileSystemKind FileSystem = VirtualDiskFileSystemKind.Xfs);

public sealed record VirtualDiskPartitionLayout(
    int Number,
    long OffsetBytes,
    long SizeBytes,
    string Name,
    string VolumeLabel,
    VirtualDiskFileSystemKind FileSystem);

internal static class VirtualDiskPartitionTableWriter
{
    internal const int SectorSize = 512;
    internal const long AlignmentBytes = 1024 * 1024;
    private const int GptEntryCount = 128;
    private const int GptEntrySize = 128;
    private static readonly Guid LinuxFileSystemType = new("0FC63DAF-8483-4772-8E79-3D69D8477DE4");
    private static readonly Guid MicrosoftBasicDataType = new("EBD0A0A2-B9E5-4433-87C0-68B6B72699C7");

    public static IReadOnlyList<VirtualDiskPartitionLayout> Plan(
        long diskSize,
        VirtualDiskPartitionTableKind tableKind,
        IReadOnlyList<VirtualDiskPartitionDefinition> definitions)
    {
        if (!Enum.IsDefined(tableKind))
        {
            throw new ArgumentOutOfRangeException(nameof(tableKind));
        }

        if (diskSize < 512L * 1024 * 1024 || diskSize % SectorSize != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(diskSize), "仮想ディスク容量は512 MiB以上かつ512-byte境界にしてください。");
        }

        ArgumentNullException.ThrowIfNull(definitions);
        var maximumPartitions = tableKind == VirtualDiskPartitionTableKind.Mbr ? 4 : GptEntryCount;
        if (definitions.Count == 0 || definitions.Count > maximumPartitions)
        {
            throw new ArgumentOutOfRangeException(
                nameof(definitions),
                $"{tableKind}では1～{maximumPartitions}個のパーティションを指定してください。");
        }

        if (tableKind == VirtualDiskPartitionTableKind.Mbr
            && diskSize / SectorSize - 1 > uint.MaxValue)
        {
            throw new NotSupportedException("2 TiBを超える仮想ディスクはMBRで表現できません。GPTを使用してください。");
        }

        var lastUsableExclusive = tableKind == VirtualDiskPartitionTableKind.Gpt
            ? checked(diskSize - 33L * SectorSize)
            : diskSize;
        var offset = AlignmentBytes;
        var result = new List<VirtualDiskPartitionLayout>(definitions.Count);
        for (var index = 0; index < definitions.Count; index++)
        {
            var definition = definitions[index];
            if (!Enum.IsDefined(definition.FileSystem))
            {
                throw new ArgumentOutOfRangeException(nameof(definitions), "未対応のファイルシステムです。");
            }

            var minimumSize = GetMinimumPartitionSizeBytes(definition.FileSystem);
            if (definition.SizeBytes < minimumSize
                || definition.SizeBytes % AlignmentBytes != 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(definitions),
                    $"{GetDisplayName(definition.FileSystem)}パーティション容量は"
                    + $"{minimumSize / AlignmentBytes:N0} MiB以上かつ1 MiB単位にしてください。");
            }

            if (tableKind == VirtualDiskPartitionTableKind.Mbr
                && (offset / SectorSize > uint.MaxValue
                    || definition.SizeBytes / SectorSize > uint.MaxValue))
            {
                throw new NotSupportedException("MBRの32-bit LBA範囲を超えるパーティションは作成できません。GPTを使用してください。");
            }

            var end = checked(offset + definition.SizeBytes);
            if (end > lastUsableExclusive)
            {
                throw new IOException("指定したパーティションが仮想ディスク容量に収まりません。");
            }

            result.Add(new VirtualDiskPartitionLayout(
                index + 1,
                offset,
                definition.SizeBytes,
                NormalizeName(definition.Name, index + 1, definition.FileSystem),
                NormalizeLabel(definition.VolumeLabel, index + 1, definition.FileSystem),
                definition.FileSystem));
            offset = AlignUp(end, AlignmentBytes);
        }

        return result;
    }

    public static void Write(
        Stream disk,
        long diskSize,
        VirtualDiskPartitionTableKind tableKind,
        IReadOnlyList<VirtualDiskPartitionLayout> partitions,
        Guid? diskGuid = null)
    {
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentNullException.ThrowIfNull(partitions);
        if (!disk.CanWrite || !disk.CanSeek || disk.Length != diskSize)
        {
            throw new ArgumentException("パーティション表の出力先が書き込み可能な指定容量のseekable streamではありません。", nameof(disk));
        }

        if (tableKind == VirtualDiskPartitionTableKind.Mbr)
        {
            WriteMbr(disk, diskSize, partitions);
        }
        else
        {
            WriteGpt(disk, diskSize, partitions, diskGuid ?? Guid.NewGuid());
        }

        disk.Flush();
    }

    private static void WriteMbr(
        Stream disk,
        long diskSize,
        IReadOnlyList<VirtualDiskPartitionLayout> partitions)
    {
        if (partitions.Count > 4 || diskSize / SectorSize - 1 > uint.MaxValue)
        {
            throw new NotSupportedException("この容量またはパーティション数はMBRで表現できません。GPTを使用してください。");
        }

        var sector = new byte[SectorSize];
        Random.Shared.NextBytes(sector.AsSpan(0x1b8, 4));
        for (var index = 0; index < partitions.Count; index++)
        {
            var partition = partitions[index];
            var entry = sector.AsSpan(0x1be + index * 16, 16);
            entry[0] = 0;
            entry[1] = 0xfe;
            entry[2] = 0xff;
            entry[3] = 0xff;
            entry[4] = partition.FileSystem == VirtualDiskFileSystemKind.Ntfs ? (byte)0x07 : (byte)0x83;
            entry[5] = 0xfe;
            entry[6] = 0xff;
            entry[7] = 0xff;
            BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], checked((uint)(partition.OffsetBytes / SectorSize)));
            BinaryPrimitives.WriteUInt32LittleEndian(entry[12..], checked((uint)(partition.SizeBytes / SectorSize)));
        }

        sector[510] = 0x55;
        sector[511] = 0xaa;
        disk.Position = 0;
        disk.Write(sector);
    }

    private static void WriteGpt(
        Stream disk,
        long diskSize,
        IReadOnlyList<VirtualDiskPartitionLayout> partitions,
        Guid diskGuid)
    {
        var totalSectors = checked((ulong)(diskSize / SectorSize));
        var lastLba = totalSectors - 1;
        var entryArraySectors = checked((ulong)(GptEntryCount * GptEntrySize / SectorSize));
        const ulong primaryEntryLba = 2;
        var backupEntryLba = checked(lastLba - entryArraySectors);
        var firstUsableLba = checked(primaryEntryLba + entryArraySectors);
        var lastUsableLba = checked(backupEntryLba - 1);

        var protectiveMbr = new byte[SectorSize];
        var protectiveEntry = protectiveMbr.AsSpan(0x1be, 16);
        protectiveEntry[0] = 0;
        protectiveEntry[1] = 0;
        protectiveEntry[2] = 2;
        protectiveEntry[3] = 0;
        protectiveEntry[4] = 0xee;
        protectiveEntry[5] = 0xff;
        protectiveEntry[6] = 0xff;
        protectiveEntry[7] = 0xff;
        BinaryPrimitives.WriteUInt32LittleEndian(protectiveEntry[8..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(
            protectiveEntry[12..],
            checked((uint)Math.Min(totalSectors - 1, uint.MaxValue)));
        protectiveMbr[510] = 0x55;
        protectiveMbr[511] = 0xaa;
        disk.Position = 0;
        disk.Write(protectiveMbr);

        var entries = new byte[GptEntryCount * GptEntrySize];
        for (var index = 0; index < partitions.Count; index++)
        {
            var partition = partitions[index];
            var entry = entries.AsSpan(index * GptEntrySize, GptEntrySize);
            var partitionType = partition.FileSystem == VirtualDiskFileSystemKind.Ntfs
                ? MicrosoftBasicDataType
                : LinuxFileSystemType;
            partitionType.ToByteArray().CopyTo(entry);
            Guid.NewGuid().ToByteArray().CopyTo(entry[16..]);
            var firstLba = checked((ulong)(partition.OffsetBytes / SectorSize));
            var sectorCount = checked((ulong)(partition.SizeBytes / SectorSize));
            var finalLba = checked(firstLba + sectorCount - 1);
            if (firstLba < firstUsableLba || finalLba > lastUsableLba)
            {
                throw new IOException("GPTの利用可能LBA範囲外にパーティションがあります。");
            }

            BinaryPrimitives.WriteUInt64LittleEndian(entry[32..], firstLba);
            BinaryPrimitives.WriteUInt64LittleEndian(entry[40..], finalLba);
            var nameBytes = Encoding.Unicode.GetBytes(partition.Name);
            nameBytes.AsSpan(0, Math.Min(nameBytes.Length, 72)).CopyTo(entry[56..]);
        }

        var entriesCrc = ComputeCrc32(entries);
        disk.Position = checked((long)primaryEntryLba * SectorSize);
        disk.Write(entries);
        disk.Position = checked((long)backupEntryLba * SectorSize);
        disk.Write(entries);

        var primaryHeader = CreateGptHeader(
            currentLba: 1,
            alternateLba: lastLba,
            firstUsableLba,
            lastUsableLba,
            diskGuid,
            primaryEntryLba,
            entriesCrc);
        var backupHeader = CreateGptHeader(
            currentLba: lastLba,
            alternateLba: 1,
            firstUsableLba,
            lastUsableLba,
            diskGuid,
            backupEntryLba,
            entriesCrc);
        disk.Position = SectorSize;
        disk.Write(primaryHeader);
        disk.Position = checked((long)lastLba * SectorSize);
        disk.Write(backupHeader);
    }

    private static byte[] CreateGptHeader(
        ulong currentLba,
        ulong alternateLba,
        ulong firstUsableLba,
        ulong lastUsableLba,
        Guid diskGuid,
        ulong entryLba,
        uint entriesCrc)
    {
        var header = new byte[SectorSize];
        "EFI PART"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), 0x00010000);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), 92);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(24), currentLba);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(32), alternateLba);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(40), firstUsableLba);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(48), lastUsableLba);
        diskGuid.ToByteArray().CopyTo(header, 56);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(72), entryLba);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(80), GptEntryCount);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(84), GptEntrySize);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(88), entriesCrc);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), ComputeCrc32(header.AsSpan(0, 92)));
        return header;
    }

    private static uint ComputeCrc32(ReadOnlySpan<byte> data)
    {
        var crc = uint.MaxValue;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xedb88320U : crc >> 1;
            }
        }

        return ~crc;
    }

    private static long AlignUp(long value, long alignment) =>
        checked((value + alignment - 1) / alignment * alignment);

    internal static long GetMinimumPartitionSizeBytes(VirtualDiskFileSystemKind fileSystem) => fileSystem switch
    {
        VirtualDiskFileSystemKind.Xfs => 320L * 1024 * 1024,
        VirtualDiskFileSystemKind.Ext4 => 64L * 1024 * 1024,
        VirtualDiskFileSystemKind.Ntfs => 64L * 1024 * 1024,
        _ => throw new ArgumentOutOfRangeException(nameof(fileSystem)),
    };

    internal static string GetDisplayName(VirtualDiskFileSystemKind fileSystem) => fileSystem switch
    {
        VirtualDiskFileSystemKind.Xfs => "XFS",
        VirtualDiskFileSystemKind.Ext4 => "ext4",
        VirtualDiskFileSystemKind.Ntfs => "NTFS",
        _ => throw new ArgumentOutOfRangeException(nameof(fileSystem)),
    };

    private static string NormalizeName(string name, int number, VirtualDiskFileSystemKind fileSystem)
    {
        var normalized = string.IsNullOrWhiteSpace(name)
            ? $"{GetDisplayName(fileSystem)} partition {number}"
            : name.Trim();
        return normalized.Length <= 36 ? normalized : normalized[..36];
    }

    private static string NormalizeLabel(string label, int number, VirtualDiskFileSystemKind fileSystem)
    {
        var normalized = string.IsNullOrWhiteSpace(label)
            ? $"VDT_{GetDisplayName(fileSystem).ToUpperInvariant()}_{number}"
            : label.Trim();
        if (normalized.Any(character => character == '\0' || char.IsControl(character)))
        {
            throw new ArgumentException("ボリュームラベルにNULまたは制御文字は使用できません。", nameof(label));
        }

        if (fileSystem == VirtualDiskFileSystemKind.Xfs
            && (normalized.Contains('/') || Encoding.UTF8.GetByteCount(normalized) > 12))
        {
            throw new ArgumentException("XFSボリュームラベルは'/'を含まないUTF-8で12 bytes以内にしてください。", nameof(label));
        }

        if (fileSystem == VirtualDiskFileSystemKind.Ext4
            && (normalized.Contains('/') || Encoding.UTF8.GetByteCount(normalized) > 16))
        {
            throw new ArgumentException("ext4ボリュームラベルは'/'を含まないUTF-8で16 bytes以内にしてください。", nameof(label));
        }

        if (fileSystem == VirtualDiskFileSystemKind.Ntfs)
        {
            ReadOnlySpan<char> invalid = ['"', '*', '/', ':', '<', '>', '?', '\\', '|'];
            if (normalized.Length > 32 || normalized.AsSpan().IndexOfAny(invalid) >= 0)
            {
                throw new ArgumentException("NTFSボリュームラベルは予約文字を含まない32文字以内にしてください。", nameof(label));
            }
        }

        return normalized;
    }
}
