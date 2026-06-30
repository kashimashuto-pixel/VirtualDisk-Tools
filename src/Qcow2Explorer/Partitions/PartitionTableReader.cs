using Qcow2Explorer.Core;

namespace Qcow2Explorer.Partitions;

public static class PartitionTableReader
{
    private static readonly HashSet<byte> ExtendedTypes = new() { 0x05, 0x0f, 0x85 };

    public static IReadOnlyList<PartitionInfo> ReadPartitions(IBlockReader disk)
    {
        var mbr = EndianUtilities.ReadBytes(disk, 0, 512);
        if (mbr[510] != 0x55 || mbr[511] != 0xaa)
        {
            return Array.Empty<PartitionInfo>();
        }

        if (HasProtectiveMbr(mbr) && TryReadGpt(disk, out var gptPartitions))
        {
            return gptPartitions;
        }

        return ReadMbrPartitions(disk, mbr);
    }

    private static IReadOnlyList<PartitionInfo> ReadMbrPartitions(IBlockReader disk, byte[] mbr)
    {
        var partitions = new List<PartitionInfo>();
        var number = 1;
        ulong? extendedBase = null;

        for (var i = 0; i < 4; i++)
        {
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

            partitions.Add(CreateMbrPartition(number++, mbr, entryOffset, start, count));
        }

        if (extendedBase.HasValue)
        {
            ReadExtendedPartitions(disk, extendedBase.Value, partitions, ref number);
        }

        return partitions;
    }

    private static void ReadExtendedPartitions(IBlockReader disk, ulong extendedBase, List<PartitionInfo> partitions, ref int number)
    {
        var currentEbr = extendedBase;
        var visited = new HashSet<ulong>();

        while (currentEbr != 0 && visited.Add(currentEbr) && currentEbr < (ulong)(disk.Length / 512))
        {
            var sector = EndianUtilities.ReadBytes(disk, checked((long)(currentEbr * 512)), 512);
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
                    nextEbr = extendedBase + relStart;
                }
                else
                {
                    partitions.Add(CreateMbrPartition(number++, sector, entryOffset, currentEbr + relStart, count));
                }
            }

            currentEbr = nextEbr;
        }
    }

    private static PartitionInfo CreateMbrPartition(int number, byte[] sector, int entryOffset, ulong start, ulong count)
    {
        var type = sector[entryOffset + 4];
        return new PartitionInfo
        {
            Number = number,
            Scheme = "MBR",
            Name = $"Partition {number}",
            Type = GetMbrTypeName(type),
            TypeId = $"0x{type:X2}",
            Bootable = sector[entryOffset] == 0x80,
            StartLba = start,
            SectorCount = count
        };
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

    private static bool TryReadGpt(IBlockReader disk, out IReadOnlyList<PartitionInfo> partitions)
    {
        partitions = Array.Empty<PartitionInfo>();
        if (disk.Length < 1024)
        {
            return false;
        }

        var header = EndianUtilities.ReadBytes(disk, 512, 512);
        if (System.Text.Encoding.ASCII.GetString(header, 0, 8) != "EFI PART")
        {
            return false;
        }

        var entryLba = EndianUtilities.ReadUInt64Little(header, 72);
        var entryCount = EndianUtilities.ReadUInt32Little(header, 80);
        var entrySize = EndianUtilities.ReadUInt32Little(header, 84);
        if (entrySize < 128 || entrySize > 4096 || entryCount == 0)
        {
            return false;
        }

        var result = new List<PartitionInfo>();
        var number = 1;
        var emptyGuid = Guid.Empty;
        var entriesToRead = Math.Min(entryCount, 4096);

        for (uint i = 0; i < entriesToRead; i++)
        {
            var entryOffset = checked((long)(entryLba * 512 + i * entrySize));
            var entry = EndianUtilities.ReadBytes(disk, entryOffset, checked((int)entrySize));
            var typeGuid = new Guid(entry.AsSpan(0, 16));
            if (typeGuid == emptyGuid)
            {
                continue;
            }

            var firstLba = EndianUtilities.ReadUInt64Little(entry, 32);
            var lastLba = EndianUtilities.ReadUInt64Little(entry, 40);
            if (lastLba < firstLba)
            {
                continue;
            }

            var name = EndianUtilities.ReadUtf16LeZ(entry, 56, Math.Min(72, entry.Length - 56));
            result.Add(new PartitionInfo
            {
                Number = number++,
                Scheme = "GPT",
                Name = string.IsNullOrWhiteSpace(name) ? $"Partition {number - 1}" : name,
                Type = GetGptTypeName(typeGuid),
                TypeId = typeGuid.ToString("D"),
                Bootable = false,
                StartLba = firstLba,
                SectorCount = lastLba - firstLba + 1
            });
        }

        partitions = result;
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
