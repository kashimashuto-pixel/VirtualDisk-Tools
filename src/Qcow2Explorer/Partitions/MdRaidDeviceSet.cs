using Qcow2Explorer.Core;
using Qcow2Explorer.FileSystems;

namespace Qcow2Explorer.Partitions;

public static class MdRaidDeviceSet
{
    private const uint Magic = 0xa92b4efc;
    private const uint FeatureBitmapOffset = 1;
    private const uint SupportedFeatureMask = FeatureBitmapOffset;
    private const ushort SpareRole = 0xffff;
    private const ushort FaultyRole = 0xfffe;
    private const int SuperblockSize = 4096;

    public static MdRaidDiscoveryResult Discover(
        IReadOnlyList<IBlockReader> disks,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(disks);
        var components = new List<MdRaidComponent>();
        var diagnostics = new List<string>();
        foreach (var disk in disks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var partitions = PartitionTableReader.ReadPartitions(disk, cancellationToken).ToList();
            if (partitions.Count == 0 && disk.Length >= SuperblockSize)
            {
                partitions.Add(CreateWholeDiskPartition(disk));
            }

            foreach (var partition in partitions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var slice = new PartitionSliceReader(disk, partition);
                if (TryReadMetadata(slice, out var metadata, out var error) && metadata is not null)
                {
                    components.Add(new MdRaidComponent(disk, partition, slice, metadata));
                }
                else if (!string.IsNullOrWhiteSpace(error))
                {
                    diagnostics.Add($"Linux md component #{partition.Number}: {error}");
                }
            }
        }

        var arrays = new List<MdRaidArray>();
        foreach (var group in components.GroupBy(
            component => component.Metadata.SetUuid,
            StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                arrays.Add(AssembleRaid1(group.ToList(), diagnostics));
            }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or OverflowException)
            {
                diagnostics.Add($"Linux md {group.Key}: {ex.Message}");
            }
        }

        return new MdRaidDiscoveryResult(arrays, components, diagnostics);
    }

    public static bool TryReadMetadata(
        IBlockReader component,
        out MdRaidMetadata? metadata,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(component);
        metadata = null;
        error = "";
        if (component.Length < SuperblockSize)
        {
            return false;
        }

        var sectors = component.Length / 512;
        var version10Sector = sectors >= 16 ? (sectors - 16) & ~7L : -1;
        long[] candidateOffsets = [4096, 0, version10Sector < 0 ? -1 : version10Sector * 512];
        foreach (var offset in candidateOffsets.Distinct())
        {
            if (offset < 0 || offset > component.Length - SuperblockSize)
            {
                continue;
            }

            var data = EndianUtilities.ReadBytes(component, offset, SuperblockSize);
            if (EndianUtilities.ReadUInt32Little(data, 0) != Magic)
            {
                continue;
            }

            try
            {
                metadata = ParseMetadata(data, checked((ulong)(offset / 512)), component.Length);
                return true;
            }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or OverflowException)
            {
                error = ex.Message;
                return false;
            }
        }

        return false;
    }

    private static MdRaidMetadata ParseMetadata(byte[] data, ulong actualSuperSector, long componentLength)
    {
        if (EndianUtilities.ReadUInt32Little(data, 4) != 1)
        {
            throw new NotSupportedException("metadata major version 1以外は未対応です。");
        }

        var featureMap = EndianUtilities.ReadUInt32Little(data, 8);
        var unsupportedFeatures = featureMap & ~SupportedFeatureMask;
        if (unsupportedFeatures != 0)
        {
            throw new NotSupportedException(
                $"reshape・recovery・bad-block等の未対応featureがあります: 0x{unsupportedFeatures:X8}");
        }

        var level = EndianUtilities.ReadInt32Little(data, 72);
        if (level != 1)
        {
            throw new NotSupportedException($"RAID level {level}は未対応です（現在はRAID1のみ）。");
        }

        var sizeSectors = EndianUtilities.ReadUInt64Little(data, 80);
        var chunkSectors = EndianUtilities.ReadUInt32Little(data, 88);
        var raidDisks = EndianUtilities.ReadUInt32Little(data, 92);
        var dataOffsetSectors = EndianUtilities.ReadUInt64Little(data, 128);
        var dataSizeSectors = EndianUtilities.ReadUInt64Little(data, 136);
        var superOffsetSectors = EndianUtilities.ReadUInt64Little(data, 144);
        var deviceNumber = EndianUtilities.ReadUInt32Little(data, 160);
        var events = EndianUtilities.ReadUInt64Little(data, 200);
        var resyncOffsetSectors = EndianUtilities.ReadUInt64Little(data, 208);
        var expectedChecksum = EndianUtilities.ReadUInt32Little(data, 216);
        var maximumDevices = EndianUtilities.ReadUInt32Little(data, 220);
        var recordedLogicalBlockSize = EndianUtilities.ReadUInt32Little(data, 224);
        var logicalBlockSize = recordedLogicalBlockSize == 0 ? 512U : recordedLogicalBlockSize;
        if (sizeSectors == 0 || raidDisks < 2 || raidDisks > 1024)
        {
            throw new InvalidDataException(
                $"array sizeまたはdevice数が不正です: size={sizeSectors}, raid_disks={raidDisks}");
        }

        if (maximumDevices == 0 || maximumDevices > (SuperblockSize - 256) / 2
            || deviceNumber >= maximumDevices)
        {
            throw new InvalidDataException(
                $"device role tableが不正です: dev_number={deviceNumber}, max_dev={maximumDevices}");
        }

        if (superOffsetSectors != actualSuperSector)
        {
            throw new InvalidDataException(
                $"superblock位置が一致しません: recorded={superOffsetSectors}, actual={actualSuperSector}");
        }

        var checksumLength = checked(256 + (int)maximumDevices * 2);
        var actualChecksum = CalculateChecksum(data, checksumLength);
        if (actualChecksum != expectedChecksum)
        {
            throw new InvalidDataException(
                $"superblock checksumが一致しません: expected=0x{expectedChecksum:X8}, actual=0x{actualChecksum:X8}");
        }

        if (dataSizeSectors < sizeSectors)
        {
            throw new InvalidDataException(
                $"component data sizeがarray sizeより小さいです: data={dataSizeSectors}, array={sizeSectors}");
        }

        if (resyncOffsetSectors < sizeSectors)
        {
            throw new NotSupportedException(
                $"resync未完了のmemberは使用できません: resync={resyncOffsetSectors}, size={sizeSectors}");
        }

        var dataEnd = checked((dataOffsetSectors + sizeSectors) * 512UL);
        if (dataEnd > (ulong)componentLength)
        {
            throw new InvalidDataException(
                $"component data範囲が入力外です: end={dataEnd:N0}, length={componentLength:N0}");
        }

        if (logicalBlockSize is not 512 and not 4096)
        {
            throw new NotSupportedException($"logical block size {recordedLogicalBlockSize}は未対応です。");
        }

        var role = EndianUtilities.ReadUInt16Little(data, checked(256 + (int)deviceNumber * 2));
        if (role >= raidDisks && role is not SpareRole and not FaultyRole)
        {
            throw new InvalidDataException($"device roleが不正です: role={role}, raid_disks={raidDisks}");
        }

        var setUuidBytes = data.AsSpan(16, 16);
        var deviceUuidBytes = data.AsSpan(168, 16);
        if (setUuidBytes.IndexOfAnyExcept((byte)0) < 0
            || deviceUuidBytes.IndexOfAnyExcept((byte)0) < 0)
        {
            throw new InvalidDataException("array UUIDまたはdevice UUIDが空です。");
        }

        return new MdRaidMetadata(
            Convert.ToHexString(setUuidBytes),
            EndianUtilities.ReadAscii(data, 32, 32),
            level,
            EndianUtilities.ReadUInt32Little(data, 76),
            sizeSectors,
            chunkSectors,
            raidDisks,
            dataOffsetSectors,
            dataSizeSectors,
            superOffsetSectors,
            deviceNumber,
            Convert.ToHexString(deviceUuidBytes),
            events,
            role,
            logicalBlockSize,
            featureMap);
    }

    private static uint CalculateChecksum(byte[] data, int length)
    {
        var copy = data.AsSpan(0, length).ToArray();
        copy.AsSpan(216, sizeof(uint)).Clear();
        ulong sum = 0;
        var offset = 0;
        while (length - offset >= sizeof(uint))
        {
            sum += EndianUtilities.ReadUInt32Little(copy, offset);
            offset += sizeof(uint);
        }

        if (length - offset == sizeof(ushort))
        {
            sum += EndianUtilities.ReadUInt16Little(copy, offset);
        }

        return unchecked((uint)sum + (uint)(sum >> 32));
    }

    private static MdRaidArray AssembleRaid1(
        IReadOnlyList<MdRaidComponent> candidates,
        List<string> diagnostics)
    {
        var reference = candidates.OrderByDescending(item => item.Metadata.Events).First().Metadata;
        foreach (var candidate in candidates)
        {
            var metadata = candidate.Metadata;
            if (metadata.Level != reference.Level
                || metadata.Layout != reference.Layout
                || metadata.SizeSectors != reference.SizeSectors
                || metadata.ChunkSectors != reference.ChunkSectors
                || metadata.RaidDisks != reference.RaidDisks
                || metadata.LogicalBlockSize != reference.LogicalBlockSize)
            {
                throw new InvalidDataException("同じarray UUID内でRAID geometryが一致しません。");
            }
        }

        var duplicateDeviceNumbers = candidates
            .GroupBy(item => item.Metadata.DeviceNumber)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicateDeviceNumbers.Length > 0)
        {
            throw new InvalidDataException(
                $"重複したdevice numberがあります: {string.Join(",", duplicateDeviceNumbers)}");
        }

        var duplicateDeviceUuids = candidates
            .GroupBy(item => item.Metadata.DeviceUuid, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicateDeviceUuids.Length > 0)
        {
            throw new InvalidDataException("重複したdevice UUIDがあります。");
        }

        var current = candidates
            .Where(item => item.Metadata.Events == reference.Events)
            .Where(item => item.Metadata.Role < reference.RaidDisks)
            .OrderBy(item => item.Metadata.Role)
            .ToList();
        var duplicateRoles = current
            .GroupBy(item => item.Metadata.Role)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicateRoles.Length > 0)
        {
            throw new InvalidDataException($"重複したactive roleがあります: {string.Join(",", duplicateRoles)}");
        }

        if (current.Count == 0)
        {
            throw new InvalidDataException("現在世代のactive RAID1 memberがありません。");
        }

        var staleCount = candidates.Count - current.Count;
        if (staleCount > 0)
        {
            diagnostics.Add(
                $"Linux md {reference.SetUuid}: spare/faultyまたは旧eventのmember {staleCount:N0}個を読み取り対象から除外しました。");
        }

        var reader = new MdRaid1Reader(current, reference.SizeSectors, reference.LogicalBlockSize);
        return new MdRaidArray(
            reference.SetUuid,
            reference.SetName,
            reference.RaidDisks,
            reference.Events,
            current,
            reader);
    }

    private static PartitionInfo CreateWholeDiskPartition(IBlockReader disk)
    {
        return new PartitionInfo
        {
            Number = 1,
            Scheme = "WholeDisk",
            Name = "Whole disk",
            Type = "Unpartitioned",
            StartLba = 0,
            SectorCount = checked((ulong)(disk.Length / 512))
        };
    }
}

public sealed class MdRaid1Reader : IBlockReader, ILogicalSectorReader
{
    private readonly IReadOnlyList<MdRaidComponent> _members;

    internal MdRaid1Reader(
        IReadOnlyList<MdRaidComponent> members,
        ulong sizeSectors,
        uint logicalSectorSize)
    {
        _members = members;
        Length = checked((long)(sizeSectors * 512UL));
        LogicalSectorSize = logicalSectorSize;
    }

    public long Length { get; }
    public uint LogicalSectorSize { get; }
    public bool IsDegraded => _members.Count < _members[0].Metadata.RaidDisks;
    public IReadOnlyList<ushort> AvailableRoles => _members.Select(member => member.Metadata.Role).ToArray();

    public void ReadAt(long offset, byte[] buffer, int bufferOffset, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(bufferOffset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset > Length - count || bufferOffset > buffer.Length - count)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        byte[]? verified = null;
        Exception? lastError = null;
        foreach (var member in _members)
        {
            var candidate = new byte[count];
            try
            {
                var physical = checked(
                    (long)(member.Metadata.DataOffsetSectors * 512UL) + offset);
                member.Reader.ReadAt(physical, candidate, 0, count);
                if (verified is not null && !candidate.AsSpan().SequenceEqual(verified))
                {
                    throw new InvalidDataException(
                        $"Linux md RAID1 mirror内容が一致しません: offset={offset:N0}, count={count:N0}");
                }

                verified ??= candidate;
            }
            catch (IOException ex)
            {
                lastError = ex;
            }
        }

        if (verified is null)
        {
            throw new IOException("Linux md RAID1のすべてのmirror読み取りに失敗しました。", lastError);
        }

        verified.CopyTo(buffer, bufferOffset);
    }
}

public sealed record MdRaidMetadata(
    string SetUuid,
    string SetName,
    int Level,
    uint Layout,
    ulong SizeSectors,
    uint ChunkSectors,
    uint RaidDisks,
    ulong DataOffsetSectors,
    ulong DataSizeSectors,
    ulong SuperOffsetSectors,
    uint DeviceNumber,
    string DeviceUuid,
    ulong Events,
    ushort Role,
    uint LogicalBlockSize,
    uint FeatureMap);

public sealed record MdRaidComponent(
    IBlockReader Disk,
    PartitionInfo Partition,
    IBlockReader Reader,
    MdRaidMetadata Metadata);

public sealed record MdRaidArray(
    string SetUuid,
    string SetName,
    uint ExpectedDeviceCount,
    ulong Events,
    IReadOnlyList<MdRaidComponent> Components,
    MdRaid1Reader Reader);

public sealed record MdRaidDiscoveryResult(
    IReadOnlyList<MdRaidArray> Arrays,
    IReadOnlyList<MdRaidComponent> Components,
    IReadOnlyList<string> Diagnostics);
