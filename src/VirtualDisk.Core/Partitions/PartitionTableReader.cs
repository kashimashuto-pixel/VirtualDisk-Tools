using Qcow2Explorer.Core;

namespace Qcow2Explorer.Partitions;

public static class PartitionTableReader
{
    private static readonly HashSet<byte> ExtendedTypes = new() { 0x05, 0x0f, 0x85 };
    private const int MaxGptEntryCount = 4096;
    private const int MaxGptEntrySize = 4096;
    private const int MaxExtendedPartitionCount = 4096;

    public static IReadOnlyList<PartitionInfo> ReadPartitions(
        IBlockReader disk,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sectorSize = disk is ILogicalSectorReader sectorReader
            ? sectorReader.LogicalSectorSize
            : 512U;
        if (disk.Length < 512
            || sectorSize < 512
            || sectorSize > 65_536
            || (sectorSize & (sectorSize - 1)) != 0)
        {
            return Array.Empty<PartitionInfo>();
        }

        var mbr = EndianUtilities.ReadBytes(disk, 0, 512);
        if (mbr[510] != 0x55 || mbr[511] != 0xaa)
        {
            return Array.Empty<PartitionInfo>();
        }

        if (HasProtectiveMbr(mbr))
        {
            if (TryReadGpt(disk, sectorSize, 1, cancellationToken, out var gptPartitions))
            {
                return gptPartitions;
            }

            var totalSectors = (ulong)(disk.Length / sectorSize);
            if (totalSectors > 1
                && TryReadGpt(disk, sectorSize, totalSectors - 1, cancellationToken, out gptPartitions))
            {
                return gptPartitions;
            }

            return Array.Empty<PartitionInfo>();
        }

        var mbrPartitions = ReadMbrPartitions(disk, mbr, sectorSize, cancellationToken);
        return HasOverlappingPartitions(mbrPartitions)
            ? Array.Empty<PartitionInfo>()
            : mbrPartitions;
    }

    public static IReadOnlyList<PartitionInfo> ReadPartitionsWithWholeDiskFallback(
        IBlockReader disk,
        CancellationToken cancellationToken = default)
    {
        var partitions = ReadPartitions(disk, cancellationToken);
        return partitions.Count > 0 || disk.Length < 512
            ? partitions
            : [CreateWholeDiskPartition(disk)];
    }

    public static PartitionInfo CreateWholeDiskPartition(
        IBlockReader disk,
        int number = 1,
        string name = "Whole disk")
    {
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(number);
        var sectorSize = disk is ILogicalSectorReader sectorReader
            && sectorReader.LogicalSectorSize is >= 512 and <= 65_536
            && (sectorReader.LogicalSectorSize & (sectorReader.LogicalSectorSize - 1)) == 0
                ? sectorReader.LogicalSectorSize
                : 512U;
        return new PartitionInfo
        {
            Number = number,
            Scheme = "WholeDisk",
            Name = name,
            Type = "Unpartitioned",
            TypeId = "",
            StartLba = 0,
            SectorCount = checked((ulong)(disk.Length / sectorSize)),
            SectorSize = sectorSize,
            LengthOverrideBytes = disk.Length
        };
    }

    private static IReadOnlyList<PartitionInfo> ReadMbrPartitions(
        IBlockReader disk,
        byte[] mbr,
        uint sectorSize,
        CancellationToken cancellationToken)
    {
        var partitions = new List<PartitionInfo>();
        var number = 1;
        ulong? extendedBase = null;
        var totalSectors = (ulong)(disk.Length / sectorSize);

        for (var i = 0; i < 4; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entryOffset = 446 + i * 16;
            var type = mbr[entryOffset + 4];
            var start = EndianUtilities.ReadUInt32Little(mbr, entryOffset + 8);
            var count = EndianUtilities.ReadUInt32Little(mbr, entryOffset + 12);
            if (type == 0 || count == 0)
            {
                continue;
            }

            if (ExtendedTypes.Contains(type))
            {
                extendedBase = start;
                continue;
            }

            if (TryCreateMbrPartition(
                    number,
                    mbr,
                    entryOffset,
                    start,
                    count,
                    sectorSize,
                    totalSectors,
                    out var partition))
            {
                partitions.Add(partition);
                number++;
            }
        }

        if (extendedBase.HasValue)
        {
            ReadExtendedPartitions(disk, extendedBase.Value, partitions, ref number, sectorSize, cancellationToken);
        }

        return partitions;
    }

    private static void ReadExtendedPartitions(
        IBlockReader disk,
        ulong extendedBase,
        List<PartitionInfo> partitions,
        ref int number,
        uint sectorSize,
        CancellationToken cancellationToken)
    {
        var currentEbr = extendedBase;
        var visited = new HashSet<ulong>();

        var totalSectors = (ulong)(disk.Length / sectorSize);
        while (currentEbr != 0
            && visited.Count < MaxExtendedPartitionCount
            && visited.Add(currentEbr)
            && currentEbr < totalSectors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sector = EndianUtilities.ReadBytes(disk, checked((long)(currentEbr * sectorSize)), 512);
            if (sector[510] != 0x55 || sector[511] != 0xaa)
            {
                return;
            }

            ulong nextEbr = 0;
            for (var i = 0; i < 4; i++)
            {
                var entryOffset = 446 + i * 16;
                var type = sector[entryOffset + 4];
                var relStart = EndianUtilities.ReadUInt32Little(sector, entryOffset + 8);
                var count = EndianUtilities.ReadUInt32Little(sector, entryOffset + 12);
                if (type == 0 || count == 0)
                {
                    continue;
                }

                if (ExtendedTypes.Contains(type))
                {
                    if (!TryAdd(extendedBase, relStart, out nextEbr) || nextEbr >= totalSectors)
                    {
                        nextEbr = 0;
                    }
                }
                else if (TryAdd(currentEbr, relStart, out var start)
                    && TryCreateMbrPartition(
                        number,
                        sector,
                        entryOffset,
                        start,
                        count,
                        sectorSize,
                        totalSectors,
                        out var partition))
                {
                    partitions.Add(partition);
                    number++;
                }
            }

            currentEbr = nextEbr;
        }
    }

    private static bool TryCreateMbrPartition(
        int number,
        byte[] sector,
        int entryOffset,
        ulong start,
        ulong count,
        uint sectorSize,
        ulong totalSectors,
        out PartitionInfo partition)
    {
        partition = null!;
        if (start == 0
            || start >= totalSectors
            || count == 0
            || !TryAdd(start, count, out var endExclusive)
            || endExclusive > totalSectors)
        {
            return false;
        }

        var type = sector[entryOffset + 4];
        partition = new PartitionInfo
        {
            Number = number,
            Scheme = "MBR",
            Name = $"Partition {number}",
            Type = GetMbrTypeName(type),
            TypeId = $"0x{type:X2}",
            Bootable = sector[entryOffset] == 0x80,
            StartLba = start,
            SectorCount = count,
            SectorSize = sectorSize
        };
        return true;
    }

    private static bool HasProtectiveMbr(byte[] mbr)
    {
        for (var i = 0; i < 4; i++)
        {
            if (mbr[446 + i * 16 + 4] == 0xee)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryReadGpt(
        IBlockReader disk,
        uint sectorSize,
        ulong headerLba,
        CancellationToken cancellationToken,
        out IReadOnlyList<PartitionInfo> partitions)
    {
        partitions = Array.Empty<PartitionInfo>();
        var totalSectors = (ulong)(disk.Length / sectorSize);
        if (totalSectors < 2
            || headerLba >= totalSectors
            || !TryMultiply(headerLba, sectorSize, out var headerOffset))
        {
            return false;
        }

        var header = EndianUtilities.ReadBytes(disk, checked((long)headerOffset), checked((int)sectorSize));
        if (System.Text.Encoding.ASCII.GetString(header, 0, 8) != "EFI PART")
        {
            return false;
        }

        var headerSize = EndianUtilities.ReadUInt32Little(header, 12);
        if (headerSize < 92 || headerSize > sectorSize)
        {
            return false;
        }

        var storedHeaderCrc = EndianUtilities.ReadUInt32Little(header, 16);
        Array.Clear(header, 16, sizeof(uint));
        var actualHeaderCrc = ComputeCrc32(header.AsSpan(0, checked((int)headerSize)));
        if (actualHeaderCrc != storedHeaderCrc)
        {
            return false;
        }

        var currentLba = EndianUtilities.ReadUInt64Little(header, 24);
        var alternateLba = EndianUtilities.ReadUInt64Little(header, 32);
        var firstUsableLba = EndianUtilities.ReadUInt64Little(header, 40);
        var lastUsableLba = EndianUtilities.ReadUInt64Little(header, 48);
        var entryLba = EndianUtilities.ReadUInt64Little(header, 72);
        var entryCount = EndianUtilities.ReadUInt32Little(header, 80);
        var entrySize = EndianUtilities.ReadUInt32Little(header, 84);
        if (currentLba != headerLba
            || alternateLba >= totalSectors
            || alternateLba == currentLba
            || firstUsableLba > lastUsableLba
            || lastUsableLba >= totalSectors
            || entrySize < 128
            || entrySize > MaxGptEntrySize
            || entrySize % 8 != 0
            || entryCount == 0
            || entryCount > MaxGptEntryCount
            || !TryMultiply(entryCount, entrySize, out var entryBytes)
            || !TryMultiply(entryLba, sectorSize, out var entryOffset)
            || entryOffset > (ulong)disk.Length
            || entryBytes > (ulong)disk.Length - entryOffset)
        {
            return false;
        }

        var entries = EndianUtilities.ReadBytes(
            disk,
            checked((long)entryOffset),
            checked((int)entryBytes));
        var storedEntriesCrc = EndianUtilities.ReadUInt32Little(header, 88);
        if (ComputeCrc32(entries) != storedEntriesCrc)
        {
            return false;
        }

        var result = new List<PartitionInfo>();
        var number = 1;
        var emptyGuid = Guid.Empty;

        for (uint i = 0; i < entryCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var itemOffset = checked((int)(i * entrySize));
            var entry = entries.AsSpan(itemOffset, checked((int)entrySize));
            var typeGuid = new Guid(entry[..16]);
            if (typeGuid == emptyGuid)
            {
                continue;
            }

            var firstLba = ReadUInt64Little(entry, 32);
            var lastLba = ReadUInt64Little(entry, 40);
            if (lastLba < firstLba
                || firstLba < firstUsableLba
                || lastLba > lastUsableLba
                || lastLba >= totalSectors)
            {
                continue;
            }

            var nameBytes = Math.Min(72, entry.Length - 56);
            var name = ReadUtf16LeZ(entry, 56, nameBytes);
            result.Add(new PartitionInfo
            {
                Number = number++,
                Scheme = "GPT",
                Name = string.IsNullOrWhiteSpace(name) ? $"Partition {number - 1}" : name,
                Type = GetGptTypeName(typeGuid),
                TypeId = typeGuid.ToString("D"),
                Bootable = false,
                StartLba = firstLba,
                SectorCount = lastLba - firstLba + 1,
                SectorSize = sectorSize
            });
        }

        if (HasOverlappingPartitions(result))
        {
            return false;
        }

        partitions = result;
        return true;
    }

    private static bool HasOverlappingPartitions(IReadOnlyList<PartitionInfo> partitions)
    {
        ulong previousEnd = 0;
        var hasPrevious = false;
        foreach (var partition in partitions.OrderBy(partition => partition.StartLba))
        {
            if (hasPrevious && partition.StartLba < previousEnd)
            {
                return true;
            }

            previousEnd = checked(partition.StartLba + partition.SectorCount);
            hasPrevious = true;
        }

        return false;
    }

    private static ulong ReadUInt64Little(ReadOnlySpan<byte> buffer, int offset) =>
        System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(buffer[offset..]);

    private static string ReadUtf16LeZ(ReadOnlySpan<byte> buffer, int offset, int byteCount)
    {
        var value = buffer.Slice(offset, byteCount);
        var end = 0;
        while (end + 1 < value.Length && (value[end] != 0 || value[end + 1] != 0))
        {
            end += 2;
        }

        return System.Text.Encoding.Unicode.GetString(value[..end]);
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

    private static bool TryAdd(ulong left, ulong right, out ulong result)
    {
        result = left + right;
        return result >= left;
    }

    private static bool TryMultiply(ulong left, ulong right, out ulong result)
    {
        if (left != 0 && right > ulong.MaxValue / left)
        {
            result = 0;
            return false;
        }

        result = left * right;
        return true;
    }

    private static string GetMbrTypeName(byte type)
    {
        return type switch
        {
            0x01 => "FAT12",
            0x04 => "FAT16",
            0x05 => "Extended",
            0x06 => "FAT16",
            0x07 => "NTFS/exFAT/HPFS",
            0x0b => "FAT32",
            0x0c => "FAT32 LBA",
            0x0e => "FAT16 LBA",
            0x0f => "Extended LBA",
            0x27 => "Windows Recovery",
            0x82 => "Linux swap",
            0x83 => "Linux filesystem",
            0x85 => "Linux extended",
            0x8e => "Linux LVM",
            0xa5 => "FreeBSD",
            0xee => "GPT protective",
            0xef => "EFI System",
            _ => $"Unknown 0x{type:X2}"
        };
    }

    private static string GetGptTypeName(Guid guid)
    {
        var id = guid.ToString("D").ToLowerInvariant();
        return id switch
        {
            "c12a7328-f81f-11d2-ba4b-00a0c93ec93b" => "EFI System",
            "e3c9e316-0b5c-4db8-817d-f92df00215ae" => "Microsoft Reserved",
            "ebd0a0a2-b9e5-4433-87c0-68b6b72699c7" => "Microsoft Basic Data",
            "de94bba4-06d1-4d40-a16a-bfd50179d6ac" => "Windows Recovery",
            "0fc63daf-8483-4772-8e79-3d69d8477de4" => "Linux filesystem",
            "0657fd6d-a4ab-43c4-84e5-0933c84b4f4f" => "Linux swap",
            "e6d6d379-f507-44c2-a23c-238f2a3df928" => "Linux LVM",
            "933ac7e1-2eb4-4f13-b844-0e14e2aef915" => "Linux home",
            _ => "Unknown GPT type"
        };
    }
}
