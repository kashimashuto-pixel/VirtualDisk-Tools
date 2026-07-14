using Qcow2Explorer.Core;
using Qcow2Explorer.Partitions;
using DiscExFatFileSystem = DiscUtils.ExFat.ExFatFileSystem;
using DiscNtfsFileSystem = DiscUtils.Ntfs.NtfsFileSystem;

namespace Qcow2Explorer.FileSystems;

public static class FileSystemDetector
{
    public static string Detect(IBlockReader disk, PartitionInfo partition)
    {
        if (partition.LengthBytes < 512)
        {
            return "";
        }

        var slice = new PartitionSliceReader(disk, partition);
        var boot = EndianUtilities.ReadBytes(slice, 0, 512);
        var oem = EndianUtilities.ReadAscii(boot, 3, 8);
        if (oem == "-FVE-FS-")
        {
            return "BitLocker/FVE";
        }

        if (oem == "NTFS")
        {
            return "NTFS";
        }

        if (oem == "EXFAT")
        {
            return "exFAT";
        }

        if (boot[0] == (byte)'X' && boot[1] == (byte)'F' && boot[2] == (byte)'S' && boot[3] == (byte)'B')
        {
            return "XFS";
        }

        var squashFs = DetectSquashFs(boot);
        if (!string.IsNullOrEmpty(squashFs))
        {
            return squashFs;
        }

        var fat = DetectFat(boot, partition.LengthBytes);
        if (!string.IsNullOrEmpty(fat))
        {
            return fat;
        }

        if (partition.LengthBytes > 2048)
        {
            var super = EndianUtilities.ReadBytes(slice, 1024, 1024);
            if (EndianUtilities.ReadUInt16Little(super, 0x38) == 0xef53)
            {
                var incompat = EndianUtilities.ReadUInt32Little(super, 0x60);
                return (incompat & 0x40) != 0 ? "ext4" : "ext2/ext3";
            }
        }

        var blockLayer = DetectBlockLayer(slice);
        if (!string.IsNullOrWhiteSpace(blockLayer))
        {
            return blockLayer;
        }

        return "";
    }

    private static string DetectFat(byte[] boot, long partitionLengthBytes)
    {
        var fat32 = EndianUtilities.ReadAscii(boot, 82, 8);
        var fat16 = EndianUtilities.ReadAscii(boot, 54, 8);
        var labelFat32 = fat32.StartsWith("FAT32", StringComparison.OrdinalIgnoreCase);
        var labelFat16 = fat16.StartsWith("FAT16", StringComparison.OrdinalIgnoreCase);
        var labelFat12 = fat16.StartsWith("FAT12", StringComparison.OrdinalIgnoreCase);
        if (!labelFat32 && !labelFat16 && !labelFat12)
        {
            return "";
        }

        if (!TryCalculateFatBits(boot, partitionLengthBytes, out var fatBits))
        {
            return "";
        }

        return fatBits switch
        {
            32 when labelFat32 => "FAT32",
            16 when labelFat16 => "FAT16",
            12 when labelFat12 => "FAT12 (検出のみ)",
            _ => ""
        };
    }

    private static bool TryCalculateFatBits(byte[] boot, long partitionLengthBytes, out int fatBits)
    {
        fatBits = 0;
        if (boot[510] != 0x55 || boot[511] != 0xaa)
        {
            return false;
        }

        var bytesPerSector = EndianUtilities.ReadUInt16Little(boot, 11);
        var sectorsPerCluster = boot[13];
        var reservedSectors = EndianUtilities.ReadUInt16Little(boot, 14);
        var fatCount = boot[16];
        var rootEntryCount = EndianUtilities.ReadUInt16Little(boot, 17);
        var total16 = EndianUtilities.ReadUInt16Little(boot, 19);
        var totalSectors = total16 != 0 ? total16 : EndianUtilities.ReadUInt32Little(boot, 32);
        var fat16 = EndianUtilities.ReadUInt16Little(boot, 22);
        var fatSizeSectors = fat16 != 0 ? fat16 : EndianUtilities.ReadUInt32Little(boot, 36);

        if (!IsPowerOfTwo(bytesPerSector) || bytesPerSector < 512 || bytesPerSector > 4096)
        {
            return false;
        }

        if (!IsPowerOfTwo(sectorsPerCluster) || sectorsPerCluster > 128)
        {
            return false;
        }

        if (reservedSectors == 0 || fatCount == 0 || fatCount > 4 || totalSectors == 0 || fatSizeSectors == 0)
        {
            return false;
        }

        var partitionSectors = (ulong)partitionLengthBytes / bytesPerSector;
        if (partitionSectors != 0 && totalSectors > partitionSectors)
        {
            return false;
        }

        var rootDirSectors = ((ulong)rootEntryCount * 32 + (uint)bytesPerSector - 1) / bytesPerSector;
        var firstDataSector = (ulong)reservedSectors + (ulong)fatCount * fatSizeSectors + rootDirSectors;
        if (firstDataSector >= totalSectors)
        {
            return false;
        }

        var dataSectors = totalSectors - firstDataSector;
        var clusterCount = dataSectors / sectorsPerCluster;
        fatBits = clusterCount < 4085 ? 12 : clusterCount < 65525 ? 16 : 32;
        return fatBits switch
        {
            32 => rootEntryCount == 0,
            16 or 12 => rootEntryCount > 0,
            _ => false
        };
    }

    private static bool IsPowerOfTwo(int value)
    {
        return value > 0 && (value & (value - 1)) == 0;
    }

    private static string DetectSquashFs(byte[] boot)
    {
        if (EndianUtilities.ReadUInt32Little(boot, 0) != 0x73717368)
        {
            return "";
        }

        var compression = boot.Length >= 22
            ? EndianUtilities.ReadUInt16Little(boot, 20) switch
            {
                1 => "gzip",
                2 => "lzma",
                3 => "lzo",
                4 => "xz",
                5 => "lz4",
                6 => "zstd",
                _ => "unknown"
            }
            : "unknown";

        return $"SquashFS ({compression}, 検出のみ)";
    }

    private static string DetectBlockLayer(IBlockReader reader)
    {
        if (reader.Length >= 2048)
        {
            for (var sector = 0; sector < 4; sector++)
            {
                var label = EndianUtilities.ReadBytes(reader, sector * 512L, 512);
                if (EndianUtilities.ReadAscii(label, 0, 8) == "LABELONE"
                    && EndianUtilities.ReadAscii(label, 24, 8).StartsWith("LVM2", StringComparison.Ordinal))
                {
                    return "LVM2 PV (検出のみ)";
                }
            }
        }

        if (reader.Length >= 8192)
        {
            if (HasMdMagic(reader, 0) || HasMdMagic(reader, 4096) || HasMdMagic(reader, Math.Max(0, reader.Length - 4096)))
            {
                return "Linux md RAID (検出のみ)";
            }
        }

        return "";
    }

    private static bool HasMdMagic(IBlockReader reader, long offset)
    {
        if (offset < 0 || offset + 4 > reader.Length)
        {
            return false;
        }

        var buffer = EndianUtilities.ReadBytes(reader, offset, 4);
        return EndianUtilities.ReadUInt32Little(buffer, 0) == 0xa92b4efc;
    }

    public static IReadOnlyFileSystem? TryOpen(IBlockReader disk, PartitionInfo partition, out string error)
    {
        error = "";
        try
        {
            var detected = string.IsNullOrWhiteSpace(partition.FileSystem)
                ? Detect(disk, partition)
                : partition.FileSystem;
            partition.FileSystem = detected;
            var slice = new PartitionSliceReader(disk, partition);

            var fileSystem = OpenSupportedFileSystem(disk, partition, detected);
            if (fileSystem is not null)
            {
                return fileSystem;
            }

            if (detected.StartsWith("BitLocker/FVE", StringComparison.OrdinalIgnoreCase))
            {
                if (BitLockerMetadataReader.TryRead(slice, out var metadata, out var metadataError) && metadata is not null)
                {
                    if (BitLockerUnlock.TryCreateReaderWithClearKey(slice, metadata, out var decryptedReader, out var unlockError)
                        && decryptedReader is not null)
                    {
                        var innerPartition = new PartitionInfo
                        {
                            Number = partition.Number,
                            Scheme = "BitLocker",
                            Name = partition.Name,
                            Type = partition.Type,
                            TypeId = partition.TypeId,
                            StartLba = 0,
                            SectorCount = checked((ulong)(decryptedReader.Length / 512)),
                            ReaderOverride = decryptedReader,
                            LengthOverrideBytes = decryptedReader.Length
                        };
                        innerPartition.FileSystem = Detect(decryptedReader, innerPartition);
                        var innerFileSystem = OpenSupportedFileSystem(decryptedReader, innerPartition, innerPartition.FileSystem);
                        if (innerFileSystem is not null)
                        {
                            partition.FileSystem = $"BitLocker/FVE -> {innerPartition.FileSystem}";
                            return new BitLockerFileSystem(innerFileSystem, decryptedReader, partition, innerPartition);
                        }

                        error = $"BitLocker/FVE のクリアキー復号は成功しましたが、内部ファイルシステムを開けませんでした: {innerPartition.FileSystem}";
                        return null;
                    }

                    error = string.Join(Environment.NewLine, new[]
                    {
                        "BitLocker/FVE ボリュームです。",
                        $"暗号方式: {metadata.EncryptionMethodName}",
                        $"キー保護子: {string.Join(", ", metadata.KeyProtectors.Select(p => p.ProtectionName).Distinct())}",
                        string.IsNullOrWhiteSpace(unlockError) ? "" : $"復号試行: {unlockError}",
                        BitLockerUnlock.GetUnlockStatus(metadata)
                    }.Where(s => !string.IsNullOrWhiteSpace(s)));
                    return null;
                }

                error = $"BitLocker/FVE ボリュームですが、メタデータを読めませんでした: {metadataError}";
                return null;
            }

            error = string.IsNullOrWhiteSpace(detected)
                ? "対応ファイルシステムを検出できませんでした。"
                : $"{detected} は検出のみで、ファイル一覧表示は未対応です。";
            return null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or ArgumentOutOfRangeException)
        {
            error = ex.Message;
            return null;
        }
    }

    private static IReadOnlyFileSystem? OpenSupportedFileSystem(IBlockReader disk, PartitionInfo partition, string detected)
    {
        var slice = new PartitionSliceReader(disk, partition);

        if (detected is "FAT32" or "FAT16")
        {
            return new FatFileSystem(slice, partition);
        }

        if (detected == "NTFS")
        {
            try
            {
                return new DiscUtilsFileSystem(slice, partition, stream =>
                {
                    var ntfs = new DiscNtfsFileSystem(stream);
                    ntfs.NtfsOptions.HideHiddenFiles = false;
                    ntfs.NtfsOptions.HideSystemFiles = false;
                    ntfs.NtfsOptions.HideMetafiles = false;
                    return ntfs;
                }, "NTFS");
            }
            catch
            {
                return new NtfsFileSystem(slice, partition);
            }
        }

        if (detected == "exFAT")
        {
            return new DiscUtilsFileSystem(slice, partition, stream => new DiscExFatFileSystem(stream, ['\\', '/']), "exFAT");
        }

        if (detected is "ext4" or "ext2/ext3")
        {
            return new ExtFileSystem(slice, partition);
        }

        if (detected.StartsWith("SquashFS", StringComparison.OrdinalIgnoreCase))
        {
            return new SquashFileSystem(slice, partition);
        }

        if (detected == "XFS")
        {
            return new XfsFileSystem(slice, partition);
        }

        return null;
    }
}
