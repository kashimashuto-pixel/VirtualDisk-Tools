using Qcow2Explorer.Core;
using Qcow2Explorer.FileSystems;

namespace Qcow2Explorer.Partitions;

public static class MdRaidDeviceSet
{
    private const uint Magic = 0xa92b4efc;
    private const uint FeatureBitmapOffset = 1;
    private const uint FeatureRaid0Layout = 4096;
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
                arrays.Add(AssembleArray(group.ToList(), diagnostics));
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

        var level = EndianUtilities.ReadInt32Little(data, 72);
        var featureMap = EndianUtilities.ReadUInt32Little(data, 8);
        var supportedFeatures = FeatureBitmapOffset | (level == 0 ? FeatureRaid0Layout : 0);
        var unsupportedFeatures = featureMap & ~supportedFeatures;
        if (unsupportedFeatures != 0)
        {
            throw new NotSupportedException(
                $"reshape・recovery・bad-block等の未対応featureがあります: 0x{unsupportedFeatures:X8}");
        }

        if (level is not 0 and not 1 and not 10)
        {
            throw new NotSupportedException($"RAID level {level}は未対応です（現在はRAID0／RAID1／RAID10のみ）。");
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
        if ((level is 1 or 10 && sizeSectors == 0)
            || (level == 0 && dataSizeSectors == 0)
            || raidDisks < 2
            || raidDisks > 1024)
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

        if (level is 1 or 10 && dataSizeSectors < sizeSectors)
        {
            throw new InvalidDataException(
                $"component data sizeがarray sizeより小さいです: data={dataSizeSectors}, array={sizeSectors}");
        }

        if (level is 1 or 10 && resyncOffsetSectors < sizeSectors)
        {
            throw new NotSupportedException(
                $"resync未完了のmemberは使用できません: resync={resyncOffsetSectors}, size={sizeSectors}");
        }

        if (level == 0
            && (chunkSectors == 0 || chunkSectors % (logicalBlockSize / 512) != 0))
        {
            throw new InvalidDataException(
                $"RAID0 chunk sizeが不正です: chunk={chunkSectors}, logical_block={logicalBlockSize}");
        }

        if (level == 10
            && (chunkSectors < 8
                || (chunkSectors & (chunkSectors - 1)) != 0
                || chunkSectors % (logicalBlockSize / 512) != 0))
        {
            throw new InvalidDataException(
                $"RAID10 chunk sizeが不正です: chunk={chunkSectors}, logical_block={logicalBlockSize}");
        }

        var requiredDataSectors = level == 0 ? dataSizeSectors : sizeSectors;
        var dataEnd = checked((dataOffsetSectors + requiredDataSectors) * 512UL);
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

    private static MdRaidArray AssembleArray(
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
            throw new InvalidDataException($"現在世代のactive RAID{reference.Level} memberがありません。");
        }

        var staleCount = candidates.Count - current.Count;
        if (staleCount > 0)
        {
            diagnostics.Add(
                $"Linux md {reference.SetUuid}: spare/faultyまたは旧eventのmember {staleCount:N0}個を読み取り対象から除外しました。");
        }

        IMdRaidReader reader;
        if (reference.Level == 0)
        {
            if (current.Count != reference.RaidDisks
                || !current.Select(item => (uint)item.Metadata.Role)
                    .SequenceEqual(Enumerable.Range(0, checked((int)reference.RaidDisks)).Select(index => (uint)index)))
            {
                throw new InvalidDataException(
                    $"RAID0には全active roleが必要です: available={current.Count:N0}/{reference.RaidDisks:N0}");
            }

            reader = new MdRaid0Reader(
                current,
                reference.ChunkSectors,
                reference.Layout,
                reference.FeatureMap,
                reference.LogicalBlockSize);
        }
        else if (reference.Level == 1)
        {
            reader = new MdRaid1Reader(current, reference.SizeSectors, reference.LogicalBlockSize);
        }
        else
        {
            reader = new MdRaid10Reader(
                current,
                reference.SizeSectors,
                reference.ChunkSectors,
                reference.Layout,
                reference.LogicalBlockSize);
        }

        return new MdRaidArray(
            reference.SetUuid,
            reference.SetName,
            reference.Level,
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

public interface IMdRaidReader : IBlockReader, ILogicalSectorReader
{
    bool IsDegraded { get; }
    IReadOnlyList<ushort> AvailableRoles { get; }
}

public sealed class MdRaid0Reader : IMdRaidReader
{
    private const uint OriginalLayout = 1;
    private const uint AlternateMultiZoneLayout = 2;
    private const uint Raid0LayoutFeature = 4096;
    private readonly uint _chunkSectors;
    private readonly uint _layout;
    private readonly IReadOnlyList<MdRaid0Zone> _zones;

    internal MdRaid0Reader(
        IReadOnlyList<MdRaidComponent> members,
        uint chunkSectors,
        uint recordedLayout,
        uint featureMap,
        uint logicalSectorSize)
    {
        _chunkSectors = chunkSectors;
        LogicalSectorSize = logicalSectorSize;
        AvailableRoles = members.Select(member => member.Metadata.Role).ToArray();

        var memberSizes = members
            .Select(member => new MdRaid0MemberSize(
                member,
                member.Metadata.DataSizeSectors / chunkSectors * chunkSectors))
            .ToList();
        if (memberSizes.Any(item => item.SizeSectors == 0))
        {
            throw new InvalidDataException("RAID0 memberのdata sizeがchunk sizeより小さいです。");
        }

        var zones = new List<MdRaid0Zone>();
        ulong zoneStart = 0;
        ulong deviceStart = 0;
        foreach (var nextDeviceEnd in memberSizes
            .Select(item => item.SizeSectors)
            .Distinct()
            .Order())
        {
            var zoneMembers = memberSizes
                .Where(item => item.SizeSectors >= nextDeviceEnd)
                .Select(item => item.Component)
                .ToList();
            var zoneSectors = checked((nextDeviceEnd - deviceStart) * (ulong)zoneMembers.Count);
            if (zoneSectors == 0)
            {
                continue;
            }

            zones.Add(new MdRaid0Zone(
                zoneStart,
                checked(zoneStart + zoneSectors),
                deviceStart,
                zoneMembers));
            zoneStart += zoneSectors;
            deviceStart = nextDeviceEnd;
        }

        if (zones.Count == 0)
        {
            throw new InvalidDataException("RAID0 striping zoneを構築できませんでした。");
        }

        if (zones.Count == 1 || zones[1].Members.Count == 1)
        {
            _layout = OriginalLayout;
        }
        else if ((featureMap & Raid0LayoutFeature) == 0)
        {
            throw new NotSupportedException(
                "複数zone RAID0ですがlayout featureがなく、original／alternateを安全に判定できません。");
        }
        else
        {
            _layout = recordedLayout is OriginalLayout or AlternateMultiZoneLayout
                ? recordedLayout
                : throw new NotSupportedException(
                    $"複数zone RAID0 layout {recordedLayout}は未対応です（1または2が必要です）。");
        }
        _zones = zones;
        Length = checked((long)(zones[^1].EndSector * 512UL));
    }

    public long Length { get; }
    public uint LogicalSectorSize { get; }
    public bool IsDegraded => false;
    public IReadOnlyList<ushort> AvailableRoles { get; }

    public void ReadAt(long offset, byte[] buffer, int bufferOffset, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(bufferOffset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset > Length - count || bufferOffset > buffer.Length - count)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        var remaining = count;
        while (remaining > 0)
        {
            var logicalSector = checked((ulong)(offset / 512));
            var byteInSector = checked((int)(offset % 512));
            var zone = _zones.First(item => logicalSector < item.EndSector);
            var zoneSector = logicalSector - zone.StartSector;
            var mappingSector = _layout == OriginalLayout ? logicalSector : zoneSector;
            var chunkOffset = mappingSector % _chunkSectors;
            var memberIndex = checked((int)((mappingSector / _chunkSectors) % (ulong)zone.Members.Count));
            var deviceChunk = zoneSector / checked((ulong)_chunkSectors * (ulong)zone.Members.Count);
            var deviceSector = checked(zone.DeviceStartSector + deviceChunk * _chunkSectors + chunkOffset);
            var member = zone.Members[memberIndex];
            var physicalOffset = checked((long)((member.Metadata.DataOffsetSectors + deviceSector) * 512UL) + byteInSector);
            var chunkRemaining = checked((long)((_chunkSectors - chunkOffset) * 512UL) - byteInSector);
            var zoneRemaining = checked((long)((zone.EndSector - logicalSector) * 512UL) - byteInSector);
            var toRead = checked((int)Math.Min(remaining, Math.Min(chunkRemaining, zoneRemaining)));
            member.Reader.ReadAt(physicalOffset, buffer, bufferOffset, toRead);
            offset += toRead;
            bufferOffset += toRead;
            remaining -= toRead;
        }
    }

    private sealed record MdRaid0MemberSize(MdRaidComponent Component, ulong SizeSectors);
    private sealed record MdRaid0Zone(
        ulong StartSector,
        ulong EndSector,
        ulong DeviceStartSector,
        IReadOnlyList<MdRaidComponent> Members);
}

public sealed class MdRaid1Reader : IMdRaidReader
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

public sealed class MdRaid10Reader : IMdRaidReader
{
    private readonly IReadOnlyDictionary<ushort, MdRaidComponent> _membersByRole;
    private readonly uint _raidDisks;
    private readonly uint _nearCopies;
    private readonly uint _chunkSectors;

    internal MdRaid10Reader(
        IReadOnlyList<MdRaidComponent> members,
        ulong sizeSectors,
        uint chunkSectors,
        uint layout,
        uint logicalSectorSize)
    {
        _raidDisks = members[0].Metadata.RaidDisks;
        _nearCopies = layout & 0xff;
        var farCopies = (layout >> 8) & 0xff;
        var layoutFlags = layout >> 16;
        if (_nearCopies < 2
            || farCopies != 1
            || layoutFlags != 0
            || _nearCopies > _raidDisks)
        {
            throw new NotSupportedException(
                $"RAID10 layout 0x{layout:X8}は未対応です。現在はnear copies 2以上、far copies 1のlayoutに対応します。");
        }

        _chunkSectors = chunkSectors;
        _membersByRole = members.ToDictionary(member => member.Metadata.Role);
        AvailableRoles = members.Select(member => member.Metadata.Role).Order().ToArray();
        var sizeChunks = sizeSectors / chunkSectors;
        var arrayChunks = checked(sizeChunks * _raidDisks / _nearCopies);
        if (arrayChunks == 0)
        {
            throw new InvalidDataException("RAID10 array sizeがchunk sizeより小さいです。");
        }

        Length = checked((long)(arrayChunks * chunkSectors * 512UL));
        LogicalSectorSize = logicalSectorSize;
        ValidatePhysicalRanges(sizeSectors, arrayChunks);
        ValidateReadableMirrorGroups();
    }

    public long Length { get; }
    public uint LogicalSectorSize { get; }
    public bool IsDegraded => _membersByRole.Count < _raidDisks;
    public IReadOnlyList<ushort> AvailableRoles { get; }

    public void ReadAt(long offset, byte[] buffer, int bufferOffset, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(bufferOffset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset > Length - count || bufferOffset > buffer.Length - count)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        var remaining = count;
        while (remaining > 0)
        {
            var logicalSector = checked((ulong)(offset / 512));
            var byteInSector = checked((int)(offset % 512));
            var chunkOffset = logicalSector % _chunkSectors;
            var chunkRemaining = checked((long)((_chunkSectors - chunkOffset) * 512UL) - byteInSector);
            var toRead = checked((int)Math.Min(remaining, chunkRemaining));
            var mappings = MapCopies(logicalSector);
            byte[]? verified = null;
            Exception? lastError = null;
            foreach (var mapping in mappings)
            {
                if (!_membersByRole.TryGetValue(mapping.Role, out var member))
                {
                    continue;
                }

                var candidate = new byte[toRead];
                try
                {
                    var physicalOffset = checked(
                        (long)((member.Metadata.DataOffsetSectors + mapping.DeviceSector) * 512UL)
                        + byteInSector);
                    member.Reader.ReadAt(physicalOffset, candidate, 0, toRead);
                    if (verified is not null && !candidate.AsSpan().SequenceEqual(verified))
                    {
                        throw new InvalidDataException(
                            $"Linux md RAID10 mirror内容が一致しません: offset={offset:N0}, count={toRead:N0}");
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
                throw new IOException(
                    $"Linux md RAID10の利用可能なmirrorがありません: offset={offset:N0}",
                    lastError);
            }

            verified.CopyTo(buffer, bufferOffset);
            offset += toRead;
            bufferOffset += toRead;
            remaining -= toRead;
        }
    }

    private IReadOnlyList<MdRaid10Mapping> MapCopies(ulong logicalSector)
    {
        var chunk = logicalSector / _chunkSectors;
        var sectorInChunk = logicalSector % _chunkSectors;
        var scaledChunk = checked(chunk * _nearCopies);
        var stripe = scaledChunk / _raidDisks;
        var role = scaledChunk % _raidDisks;
        var deviceSector = checked(stripe * _chunkSectors + sectorInChunk);
        var mappings = new List<MdRaid10Mapping>(checked((int)_nearCopies));
        for (var copy = 0U; copy < _nearCopies; copy++)
        {
            mappings.Add(new MdRaid10Mapping(checked((ushort)role), deviceSector));
            role++;
            if (role == _raidDisks)
            {
                role = 0;
                deviceSector = checked(deviceSector + _chunkSectors);
            }
        }

        return mappings;
    }

    private void ValidateReadableMirrorGroups()
    {
        for (var chunk = 0UL; chunk < _raidDisks; chunk++)
        {
            var logicalSector = checked(chunk * _chunkSectors);
            if (!MapCopies(logicalSector).Any(mapping => _membersByRole.ContainsKey(mapping.Role)))
            {
                var roles = string.Join(",", MapCopies(logicalSector).Select(mapping => mapping.Role));
                throw new InvalidDataException(
                    $"RAID10 mirror groupに利用可能なmemberがありません: roles={roles}");
            }
        }
    }

    private void ValidatePhysicalRanges(ulong sizeSectors, ulong arrayChunks)
    {
        var firstChunk = arrayChunks > _raidDisks ? arrayChunks - _raidDisks : 0;
        for (var chunk = firstChunk; chunk < arrayChunks; chunk++)
        {
            var logicalSector = checked(chunk * _chunkSectors);
            foreach (var mapping in MapCopies(logicalSector))
            {
                if (mapping.DeviceSector > sizeSectors - _chunkSectors)
                {
                    throw new InvalidDataException(
                        $"RAID10 mappingがmember data範囲外です: role={mapping.Role}, sector={mapping.DeviceSector}, size={sizeSectors}");
                }
            }
        }
    }

    private sealed record MdRaid10Mapping(ushort Role, ulong DeviceSector);
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
    int Level,
    uint ExpectedDeviceCount,
    ulong Events,
    IReadOnlyList<MdRaidComponent> Components,
    IMdRaidReader Reader)
{
    public string LevelName => $"RAID{Level}";
}

public sealed record MdRaidDiscoveryResult(
    IReadOnlyList<MdRaidArray> Arrays,
    IReadOnlyList<MdRaidComponent> Components,
    IReadOnlyList<string> Diagnostics);
