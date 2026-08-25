using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Qcow2Explorer.Core;
using Qcow2Explorer.FileSystems;

namespace Qcow2Explorer.Partitions;

internal static class LvmStripedVolumeDiscoverer
{
    private const int SectorSize = 512;
    private const int MaximumMetadataBytes = 16 * 1024 * 1024;
    private const int MaximumConfigEntries = 100_000;
    private const int MaximumConfigDepth = 32;
    private const string LabelMagic = "LABELONE";
    private const string LabelType = "LVM2 001";
    private const string MetadataMagic = " LVM2 x[5A%r0N*>";

    public static LvmStripedDiscoveryResult Discover(
        IBlockReader defaultDisk,
        IReadOnlyList<PartitionInfo> partitions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(defaultDisk);
        ArgumentNullException.ThrowIfNull(partitions);

        var diagnostics = new List<LvmDiagnostic>();
        var physicalVolumes = new List<LvmPhysicalVolume>();
        foreach (var partition in partitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var physicalVolume = ReadPhysicalVolume(defaultDisk, partition, cancellationToken);
                physicalVolumes.Add(physicalVolume);
                diagnostics.AddRange(physicalVolume.MetadataErrors.Select(error => new LvmDiagnostic(
                    $"LVM2 PV #{partition.Number}: metadata copyを使用できません: {error}",
                    false)));
            }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or OverflowException)
            {
                diagnostics.Add(new LvmDiagnostic(
                    $"LVM2 PV #{partition.Number}: striped LV用メタデータを検証できませんでした: {ex.Message}",
                    true));
            }
        }

        var duplicatePvIds = physicalVolumes
            .GroupBy(volume => volume.Id, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicatePvIds.Length > 0)
        {
            diagnostics.Add(new LvmDiagnostic(
                $"LVM2: 同じPV UUIDの入力が重複しています: {string.Join(", ", duplicatePvIds)}",
                true));
            return new LvmStripedDiscoveryResult([], diagnostics, true, 0);
        }

        var metadataCandidates = new List<LvmVolumeGroup>();
        foreach (var physicalVolume in physicalVolumes)
        {
            foreach (var metadata in physicalVolume.MetadataCopies)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    metadataCandidates.AddRange(ParseVolumeGroups(metadata));
                }
                catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or OverflowException)
                {
                    diagnostics.Add(new LvmDiagnostic(
                        $"LVM2 PV #{physicalVolume.PartitionNumber}: 検証済みmetadata textを解釈できませんでした: {ex.Message}",
                        false));
                }
            }
        }

        var volumes = new List<LvmStripedVolume>();
        var requiresStripedReader = false;
        var logicalVolumeDefinitionCount = 0;
        var physicalVolumesById = physicalVolumes.ToDictionary(
            volume => volume.Id,
            StringComparer.OrdinalIgnoreCase);
        foreach (var group in metadataCandidates.GroupBy(
            candidate => candidate.Id,
            StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            requiresStripedReader |= group.Any(candidate => candidate.LogicalVolumes.Any(volume =>
                volume.Segments.Any(segment => segment.Stripes.Count > 1)));
            var sequence = group.Max(candidate => candidate.Sequence);
            var current = group.Where(candidate => candidate.Sequence == sequence).ToList();
            logicalVolumeDefinitionCount = checked(
                logicalVolumeDefinitionCount + current.Max(candidate => candidate.LogicalVolumes.Count));
            var distinctHashes = current
                .Select(candidate => candidate.SourceHash)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (distinctHashes.Length > 1)
            {
                diagnostics.Add(new LvmDiagnostic(
                    $"LVM2 VG {group.Key}: 同一seqno {sequence:N0}のmetadata内容が一致しません。",
                    true));
                continue;
            }

            var volumeGroup = current[0];
            var unsupportedVisibleTypes = volumeGroup.LogicalVolumes
                .Where(volume => volume.IsReadableVisible)
                .SelectMany(volume => volume.Segments)
                .Select(segment => segment.Type)
                .Where(type => !string.Equals(type, "striped", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(type => type, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (unsupportedVisibleTypes.Length > 0)
            {
                diagnostics.Add(new LvmDiagnostic(
                    $"LVM2 VG {volumeGroup.Name}: 未対応のvisible segment typeがあります: {string.Join(", ", unsupportedVisibleTypes)}",
                    true));
            }

            var segmentTypes = volumeGroup.LogicalVolumes
                .SelectMany(volume => volume.Segments)
                .Select(segment => segment.Type)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(type => type, StringComparer.OrdinalIgnoreCase);
            diagnostics.Add(new LvmDiagnostic(
                $"LVM2 VG {volumeGroup.Name}: seqno={volumeGroup.Sequence:N0}、"
                + $"PV定義={volumeGroup.PhysicalVolumes.Count:N0}、LV定義={volumeGroup.LogicalVolumes.Count:N0}、"
                + $"segment={string.Join(", ", segmentTypes)}",
                false));
            foreach (var logicalVolume in volumeGroup.LogicalVolumes.Where(volume => volume.IsSupportedStriped))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var reader = AssembleReader(volumeGroup, logicalVolume, physicalVolumesById);
                    volumes.Add(new LvmStripedVolume(
                        $"{volumeGroup.Name}/{logicalVolume.Name}",
                        logicalVolume.Id,
                        reader,
                        logicalVolume.Segments.Max(segment => segment.Stripes.Count)));
                }
                catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or OverflowException)
                {
                    diagnostics.Add(new LvmDiagnostic(
                        $"LVM2 LV {volumeGroup.Name}/{logicalVolume.Name}: striped LVを組み立てられませんでした: {ex.Message}",
                        true));
                }
            }
        }

        return new LvmStripedDiscoveryResult(
            volumes,
            diagnostics,
            requiresStripedReader,
            logicalVolumeDefinitionCount);
    }

    private static LvmPhysicalVolume ReadPhysicalVolume(
        IBlockReader defaultDisk,
        PartitionInfo partition,
        CancellationToken cancellationToken)
    {
        var reader = new PartitionSliceReader(partition.ReaderOverride ?? defaultDisk, partition);
        if (reader.Length < SectorSize * 4L)
        {
            throw new InvalidDataException("PVがLVM2 label探索範囲より短いです。");
        }

        byte[]? label = null;
        for (var sector = 0; sector < 4; sector++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = EndianUtilities.ReadBytes(reader, sector * SectorSize, SectorSize);
            if (!candidate.AsSpan(0, 8).SequenceEqual(Encoding.ASCII.GetBytes(LabelMagic)))
            {
                continue;
            }

            if (EndianUtilities.ReadUInt64Little(candidate, 8) != (ulong)sector)
            {
                throw new InvalidDataException("LVM2 labelのsector番号が実際の位置と一致しません。");
            }

            var expectedCrc = EndianUtilities.ReadUInt32Little(candidate, 16);
            var actualCrc = CalculateCrc(candidate.AsSpan(20));
            if (expectedCrc != actualCrc)
            {
                throw new InvalidDataException(
                    $"LVM2 label CRCが一致しません: expected=0x{expectedCrc:X8}, actual=0x{actualCrc:X8}");
            }

            if (!candidate.AsSpan(24, 8).SequenceEqual(Encoding.ASCII.GetBytes(LabelType)))
            {
                throw new NotSupportedException("LVM2以外のphysical volume labelです。");
            }

            label = candidate;
            break;
        }

        if (label is null)
        {
            throw new InvalidDataException("先頭4 sectorにLVM2 labelがありません。");
        }

        var headerOffset = EndianUtilities.ReadUInt32Little(label, 20);
        if (headerOffset < 32 || headerOffset > SectorSize - 48)
        {
            throw new InvalidDataException($"PV header offsetが不正です: {headerOffset:N0}");
        }

        var header = checked((int)headerOffset);
        var rawId = Encoding.ASCII.GetString(label, header, 32);
        var id = NormalizeId(rawId);
        var deviceSize = EndianUtilities.ReadUInt64Little(label, header + 32);
        if (deviceSize == 0 || deviceSize > (ulong)reader.Length)
        {
            throw new InvalidDataException(
                $"PV device sizeが入力範囲外です: recorded={deviceSize:N0}, actual={reader.Length:N0}");
        }

        var areaOffset = header + 40;
        var dataAreas = ReadDiskAreas(label, ref areaOffset, "data");
        var metadataAreas = ReadDiskAreas(label, ref areaOffset, "metadata");
        if (dataAreas.Count != 1)
        {
            throw new NotSupportedException(
                $"PV data area数 {dataAreas.Count:N0}は未対応です（1個が必要です）。");
        }

        var dataArea = dataAreas[0];
        var dataEnd = dataArea.Length == 0
            ? deviceSize
            : checked(dataArea.Offset + dataArea.Length);
        if (dataArea.Offset >= dataEnd || dataEnd > deviceSize)
        {
            throw new InvalidDataException("PV data areaがdevice範囲外です。");
        }

        var metadataCopies = new List<string>();
        var metadataErrors = new List<string>();
        foreach (var metadataArea in metadataAreas)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                metadataCopies.AddRange(ReadMetadataArea(reader, metadataArea));
            }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or OverflowException)
            {
                metadataErrors.Add(ex.Message);
            }
        }

        return new LvmPhysicalVolume(
            partition.Number,
            id,
            reader,
            dataArea.Offset,
            dataEnd,
            metadataCopies,
            metadataErrors);
    }

    private static IReadOnlyList<LvmDiskArea> ReadDiskAreas(byte[] label, ref int offset, string kind)
    {
        var result = new List<LvmDiskArea>();
        while (offset <= label.Length - 16)
        {
            var areaOffset = EndianUtilities.ReadUInt64Little(label, offset);
            var areaLength = EndianUtilities.ReadUInt64Little(label, offset + 8);
            offset += 16;
            if (areaOffset == 0 && areaLength == 0)
            {
                return result;
            }

            if (areaOffset == 0)
            {
                throw new InvalidDataException($"PV {kind} area offsetが0です。");
            }

            result.Add(new LvmDiskArea(areaOffset, areaLength));
        }

        throw new InvalidDataException($"PV {kind} area一覧の終端がありません。");
    }

    private static IReadOnlyList<string> ReadMetadataArea(IBlockReader reader, LvmDiskArea area)
    {
        if (area.Length < SectorSize
            || area.Length > (ulong)reader.Length
            || area.Offset > (ulong)reader.Length - area.Length)
        {
            throw new InvalidDataException("PV metadata areaが入力範囲外です。");
        }

        var header = EndianUtilities.ReadBytes(reader, checked((long)area.Offset), SectorSize);
        var expectedHeaderCrc = EndianUtilities.ReadUInt32Little(header, 0);
        var actualHeaderCrc = CalculateCrc(header.AsSpan(4));
        if (expectedHeaderCrc != actualHeaderCrc)
        {
            throw new InvalidDataException(
                $"LVM2 metadata header CRCが一致しません: expected=0x{expectedHeaderCrc:X8}, actual=0x{actualHeaderCrc:X8}");
        }

        if (!header.AsSpan(4, 16).SequenceEqual(Encoding.ASCII.GetBytes(MetadataMagic)))
        {
            throw new InvalidDataException("LVM2 metadata header magicが一致しません。");
        }

        var version = EndianUtilities.ReadUInt32Little(header, 20);
        var recordedStart = EndianUtilities.ReadUInt64Little(header, 24);
        var recordedLength = EndianUtilities.ReadUInt64Little(header, 32);
        if (version != 1 || recordedStart != area.Offset
            || recordedLength < SectorSize || recordedLength > area.Length)
        {
            throw new NotSupportedException(
                $"LVM2 metadata area geometryが未対応です: version={version}, start={recordedStart}, size={recordedLength}");
        }

        var result = new List<string>();
        for (var locationOffset = 40; locationOffset <= SectorSize - 24; locationOffset += 24)
        {
            var rawOffset = EndianUtilities.ReadUInt64Little(header, locationOffset);
            var rawLength = EndianUtilities.ReadUInt64Little(header, locationOffset + 8);
            var expectedCrc = EndianUtilities.ReadUInt32Little(header, locationOffset + 16);
            var flags = EndianUtilities.ReadUInt32Little(header, locationOffset + 20);
            if (rawOffset == 0 && rawLength == 0 && expectedCrc == 0 && flags == 0)
            {
                return result;
            }

            if ((flags & 1) != 0)
            {
                continue;
            }

            if ((flags & ~1U) != 0)
            {
                throw new NotSupportedException($"LVM2 raw metadata flags 0x{flags:X8}は未対応です。");
            }

            if (rawOffset < SectorSize || rawOffset >= recordedLength
                || rawLength == 0 || rawLength > MaximumMetadataBytes
                || rawLength > recordedLength - SectorSize)
            {
                throw new InvalidDataException(
                    $"LVM2 raw metadata範囲が不正です: offset={rawOffset:N0}, length={rawLength:N0}");
            }

            var metadata = ReadCircularMetadata(reader, area.Offset, recordedLength, rawOffset, rawLength);
            var actualCrc = CalculateCrc(metadata);
            if (expectedCrc != actualCrc)
            {
                throw new InvalidDataException(
                    $"LVM2 metadata text CRCが一致しません: expected=0x{expectedCrc:X8}, actual=0x{actualCrc:X8}");
            }

            var text = Encoding.ASCII.GetString(metadata).TrimEnd('\0');
            if (!text.Contains("contents = \"Text Format Volume Group\"", StringComparison.Ordinal))
            {
                throw new InvalidDataException("LVM2 metadata textのcontents宣言がありません。");
            }

            result.Add(text);
        }

        throw new InvalidDataException("LVM2 raw metadata location一覧の終端がありません。");
    }

    private static byte[] ReadCircularMetadata(
        IBlockReader reader,
        ulong areaStart,
        ulong areaLength,
        ulong rawOffset,
        ulong rawLength)
    {
        var result = new byte[checked((int)rawLength)];
        var firstLength = checked((int)Math.Min(rawLength, areaLength - rawOffset));
        reader.ReadAt(checked((long)(areaStart + rawOffset)), result, 0, firstLength);
        var remaining = result.Length - firstLength;
        if (remaining > 0)
        {
            if ((ulong)remaining > areaLength - SectorSize)
            {
                throw new InvalidDataException("循環LVM2 metadataがmetadata area容量を超えています。");
            }

            reader.ReadAt(checked((long)(areaStart + SectorSize)), result, firstLength, remaining);
        }

        return result;
    }

    private static IReadOnlyList<LvmVolumeGroup> ParseVolumeGroups(string metadata)
    {
        var document = new LvmConfigParser(metadata, MaximumConfigEntries, MaximumConfigDepth).Parse();
        if (!string.Equals(document.RequireScalar("contents"), "Text Format Volume Group", StringComparison.Ordinal)
            || document.RequireUInt64("version") != 1)
        {
            throw new NotSupportedException("LVM2 metadata textのcontentsまたはversionが未対応です。");
        }

        var result = new List<LvmVolumeGroup>();
        foreach (var (name, value) in document.Values)
        {
            if (value is not LvmConfigObject volumeGroupObject)
            {
                continue;
            }

            result.Add(ParseVolumeGroup(name, volumeGroupObject, metadata));
        }

        if (result.Count == 0)
        {
            throw new InvalidDataException("LVM2 metadata内にVolume Group定義がありません。");
        }

        return result;
    }

    private static LvmVolumeGroup ParseVolumeGroup(string name, LvmConfigObject config, string source)
    {
        var id = NormalizeId(config.RequireScalar("id"));
        var sequence = config.RequireUInt64("seqno");
        var extentSize = config.RequireUInt64("extent_size");
        if (extentSize == 0 || extentSize > ulong.MaxValue / SectorSize)
        {
            throw new InvalidDataException($"VG {name}のextent_sizeが不正です: {extentSize:N0}");
        }

        var physicalVolumes = new Dictionary<string, LvmPhysicalVolumeDefinition>(StringComparer.Ordinal);
        foreach (var (alias, value) in config.RequireObject("physical_volumes").Values)
        {
            if (value is not LvmConfigObject physicalVolume)
            {
                throw new InvalidDataException($"VG {name}のPV {alias}定義がobjectではありません。");
            }

            var definition = new LvmPhysicalVolumeDefinition(
                alias,
                NormalizeId(physicalVolume.RequireScalar("id")),
                physicalVolume.RequireUInt64("pe_start"),
                physicalVolume.RequireUInt64("pe_count"));
            if (definition.PeCount == 0 || !physicalVolumes.TryAdd(alias, definition))
            {
                throw new InvalidDataException($"VG {name}のPV {alias}定義が不正または重複しています。");
            }
        }

        var logicalVolumes = new List<LvmLogicalVolume>();
        foreach (var (logicalName, value) in config.RequireObject("logical_volumes").Values)
        {
            if (value is not LvmConfigObject logicalVolume)
            {
                throw new InvalidDataException($"VG {name}のLV {logicalName}定義がobjectではありません。");
            }

            var status = logicalVolume.RequireArray("status")
                .Select(item => item.RequireScalar())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var segmentCount = logicalVolume.RequireUInt64("segment_count");
            var segments = logicalVolume.Values
                .Where(item => item.Key.StartsWith("segment", StringComparison.Ordinal)
                    && item.Value is LvmConfigObject)
                .Select(item => ParseSegment(item.Key, (LvmConfigObject)item.Value))
                .OrderBy(segment => segment.StartExtent)
                .ToList();
            if ((ulong)segments.Count != segmentCount)
            {
                throw new InvalidDataException(
                    $"VG {name}のLV {logicalName}でsegment_countと定義数が一致しません。");
            }

            var logicalVolumeId = logicalVolume.RequireScalar("id");
            _ = NormalizeId(logicalVolumeId);
            logicalVolumes.Add(new LvmLogicalVolume(
                logicalName,
                logicalVolumeId,
                status.Contains("READ") && status.Contains("VISIBLE"),
                segments));
        }

        return new LvmVolumeGroup(
            name,
            id,
            sequence,
            extentSize,
            physicalVolumes,
            logicalVolumes,
            Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(source))));
    }

    private static LvmSegment ParseSegment(string segmentName, LvmConfigObject config)
    {
        var type = config.RequireScalar("type");
        if (!string.Equals(type, "striped", StringComparison.OrdinalIgnoreCase))
        {
            return new LvmSegment(
                config.RequireUInt64("start_extent"),
                config.RequireUInt64("extent_count"),
                type,
                0,
                []);
        }

        var stripeCount = config.RequireUInt64("stripe_count");
        if (stripeCount == 0 || stripeCount > 1024)
        {
            throw new InvalidDataException($"{segmentName}のstripe_countが不正です: {stripeCount:N0}");
        }

        var stripeSize = stripeCount == 1 ? 0 : config.RequireUInt64("stripe_size");
        if (stripeCount > 1 && (stripeSize < 8 || !IsPowerOfTwo(stripeSize)))
        {
            throw new InvalidDataException($"{segmentName}のstripe_sizeが不正です: {stripeSize:N0} sectors");
        }

        var stripeValues = config.RequireArray("stripes");
        if ((ulong)stripeValues.Count != stripeCount * 2)
        {
            throw new InvalidDataException(
                $"{segmentName}のstripes要素数がstripe_countと一致しません。");
        }

        var stripes = new List<LvmStripe>(checked((int)stripeCount));
        for (var index = 0; index < stripeValues.Count; index += 2)
        {
            stripes.Add(new LvmStripe(
                stripeValues[index].RequireScalar(),
                stripeValues[index + 1].RequireUInt64()));
        }

        return new LvmSegment(
            config.RequireUInt64("start_extent"),
            config.RequireUInt64("extent_count"),
            type,
            stripeSize,
            stripes);
    }

    private static LvmStripedReader AssembleReader(
        LvmVolumeGroup volumeGroup,
        LvmLogicalVolume logicalVolume,
        IReadOnlyDictionary<string, LvmPhysicalVolume> physicalVolumesById)
    {
        if (!logicalVolume.IsReadableVisible)
        {
            throw new NotSupportedException("READかつVISIBLEではないLVは表示しません。");
        }

        var extentSizeBytes = checked(volumeGroup.ExtentSizeSectors * SectorSize);
        var expectedStart = 0UL;
        var mappings = new List<LvmStripedSegment>();
        foreach (var segment in logicalVolume.Segments)
        {
            if (!string.Equals(segment.Type, "striped", StringComparison.OrdinalIgnoreCase))
            {
                throw new NotSupportedException($"segment type {segment.Type}はstriped LV readerで処理できません。");
            }

            if (segment.StartExtent != expectedStart || segment.ExtentCount == 0)
            {
                throw new InvalidDataException("LV segmentが連続していないか、長さが0です。");
            }

            var stripeCount = checked((ulong)segment.Stripes.Count);
            if (segment.ExtentCount % stripeCount != 0)
            {
                throw new InvalidDataException(
                    $"segment extent_count {segment.ExtentCount:N0}をstripe_count {stripeCount:N0}で等分できません。");
            }

            var areaExtents = segment.ExtentCount / stripeCount;
            var targets = new List<LvmStripeTarget>(segment.Stripes.Count);
            foreach (var stripe in segment.Stripes)
            {
                if (!volumeGroup.PhysicalVolumes.TryGetValue(stripe.PhysicalVolumeAlias, out var definition))
                {
                    throw new InvalidDataException($"PV alias {stripe.PhysicalVolumeAlias}がVGに定義されていません。");
                }

                if (!physicalVolumesById.TryGetValue(definition.Id, out var physicalVolume))
                {
                    throw new InvalidDataException($"必要なPV {definition.Id} ({stripe.PhysicalVolumeAlias})が入力されていません。");
                }

                var peStartBytes = checked(definition.PeStartSectors * SectorSize);
                if (peStartBytes != physicalVolume.DataStartBytes)
                {
                    throw new InvalidDataException(
                        $"PV {stripe.PhysicalVolumeAlias}のpe_startがPV headerのdata areaと一致しません。");
                }

                if (stripe.StartExtent > definition.PeCount
                    || areaExtents > definition.PeCount - stripe.StartExtent)
                {
                    throw new InvalidDataException(
                        $"PV {stripe.PhysicalVolumeAlias}のextent割り当てがpe_count範囲外です。");
                }

                var physicalStart = checked(peStartBytes + stripe.StartExtent * extentSizeBytes);
                var physicalEnd = checked(physicalStart + areaExtents * extentSizeBytes);
                if (physicalEnd > physicalVolume.DataEndBytes || physicalEnd > (ulong)physicalVolume.Reader.Length)
                {
                    throw new InvalidDataException(
                        $"PV {stripe.PhysicalVolumeAlias}のstripe範囲が入力data area外です。");
                }

                targets.Add(new LvmStripeTarget(physicalVolume.Reader, physicalStart));
            }

            var segmentLength = checked(segment.ExtentCount * extentSizeBytes);
            var stripeSize = segment.Stripes.Count == 1 ? segmentLength : checked(segment.StripeSizeSectors * SectorSize);
            var stripeRowLength = checked(stripeSize * checked((ulong)segment.Stripes.Count));
            if (stripeSize == 0 || stripeSize > segmentLength || segmentLength % stripeRowLength != 0)
            {
                throw new InvalidDataException(
                    $"segment length {segmentLength:N0} bytesがstripe row {stripeRowLength:N0} bytesと整合しません。");
            }

            mappings.Add(new LvmStripedSegment(
                checked(segment.StartExtent * extentSizeBytes),
                segmentLength,
                stripeSize,
                targets));
            expectedStart = checked(expectedStart + segment.ExtentCount);
        }

        return new LvmStripedReader(mappings, checked(expectedStart * extentSizeBytes));
    }

    private static string NormalizeId(string id)
    {
        var compact = id.Replace("-", "", StringComparison.Ordinal).Trim();
        if (compact.Length != 32 || compact.Any(ch => !char.IsAsciiLetterOrDigit(ch)))
        {
            throw new InvalidDataException($"LVM UUIDの形式が不正です: {id}");
        }

        return compact.ToUpperInvariant();
    }

    private static bool IsPowerOfTwo(ulong value) => value != 0 && (value & (value - 1)) == 0;

    private static uint CalculateCrc(ReadOnlySpan<byte> data)
    {
        ReadOnlySpan<uint> table =
        [
            0x00000000, 0x1db71064, 0x3b6e20c8, 0x26d930ac,
            0x76dc4190, 0x6b6b51f4, 0x4db26158, 0x5005713c,
            0xedb88320, 0xf00f9344, 0xd6d6a3e8, 0xcb61b38c,
            0x9b64c2b0, 0x86d3d2d4, 0xa00ae278, 0xbdbdf21c
        ];
        var crc = 0xf597a6cfu;
        foreach (var value in data)
        {
            crc ^= value;
            crc = table[(int)(crc & 0xf)] ^ (crc >> 4);
            crc = table[(int)(crc & 0xf)] ^ (crc >> 4);
        }

        return crc;
    }

    private sealed record LvmDiskArea(ulong Offset, ulong Length);

    private sealed record LvmPhysicalVolume(
        int PartitionNumber,
        string Id,
        IBlockReader Reader,
        ulong DataStartBytes,
        ulong DataEndBytes,
        IReadOnlyList<string> MetadataCopies,
        IReadOnlyList<string> MetadataErrors);

    private sealed record LvmVolumeGroup(
        string Name,
        string Id,
        ulong Sequence,
        ulong ExtentSizeSectors,
        IReadOnlyDictionary<string, LvmPhysicalVolumeDefinition> PhysicalVolumes,
        IReadOnlyList<LvmLogicalVolume> LogicalVolumes,
        string SourceHash);

    private sealed record LvmPhysicalVolumeDefinition(
        string Alias,
        string Id,
        ulong PeStartSectors,
        ulong PeCount);

    private sealed record LvmLogicalVolume(
        string Name,
        string Id,
        bool IsReadableVisible,
        IReadOnlyList<LvmSegment> Segments)
    {
        public bool IsSupportedStriped => IsReadableVisible
            && Segments.Count > 0
            && Segments.All(segment => string.Equals(segment.Type, "striped", StringComparison.OrdinalIgnoreCase));
    }

    private sealed record LvmSegment(
        ulong StartExtent,
        ulong ExtentCount,
        string Type,
        ulong StripeSizeSectors,
        IReadOnlyList<LvmStripe> Stripes);

    private sealed record LvmStripe(string PhysicalVolumeAlias, ulong StartExtent);
}

internal sealed class LvmStripedReader : IBlockReader, ILogicalSectorReader
{
    private readonly IReadOnlyList<LvmStripedSegment> _segments;

    public LvmStripedReader(IReadOnlyList<LvmStripedSegment> segments, ulong length)
    {
        if (segments.Count == 0 || length > long.MaxValue)
        {
            throw new InvalidDataException("LVM2 striped LVのsegmentまたは長さが不正です。");
        }

        _segments = segments;
        Length = checked((long)length);
    }

    public long Length { get; }
    public uint LogicalSectorSize => 512;

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
            var logicalOffset = checked((ulong)offset);
            var segment = _segments.First(item => logicalOffset < item.StartByte + item.LengthBytes);
            var segmentOffset = logicalOffset - segment.StartByte;
            var stripeNumber = segmentOffset / segment.StripeSizeBytes;
            var stripeIndex = checked((int)(stripeNumber % (ulong)segment.Targets.Count));
            var stripeRow = stripeNumber / (ulong)segment.Targets.Count;
            var offsetInStripe = segmentOffset % segment.StripeSizeBytes;
            var physicalOffset = checked(
                segment.Targets[stripeIndex].StartByte + stripeRow * segment.StripeSizeBytes + offsetInStripe);
            var stripeRemaining = segment.StripeSizeBytes - offsetInStripe;
            var segmentRemaining = segment.LengthBytes - segmentOffset;
            var toRead = checked((int)Math.Min((ulong)remaining, Math.Min(stripeRemaining, segmentRemaining)));
            segment.Targets[stripeIndex].Reader.ReadAt(
                checked((long)physicalOffset),
                buffer,
                bufferOffset,
                toRead);
            offset += toRead;
            bufferOffset += toRead;
            remaining -= toRead;
        }
    }
}

internal sealed record LvmStripedSegment(
    ulong StartByte,
    ulong LengthBytes,
    ulong StripeSizeBytes,
    IReadOnlyList<LvmStripeTarget> Targets);

internal sealed record LvmStripeTarget(IBlockReader Reader, ulong StartByte);

internal sealed record LvmStripedVolume(
    string Name,
    string LvmId,
    LvmStripedReader Reader,
    int StripeCount);

internal sealed record LvmStripedDiscoveryResult(
    IReadOnlyList<LvmStripedVolume> Volumes,
    IReadOnlyList<LvmDiagnostic> Diagnostics,
    bool RequiresStripedReader,
    int LogicalVolumeDefinitionCount);

internal abstract record LvmConfigValue
{
    public string RequireScalar()
    {
        return this is LvmConfigScalar scalar
            ? scalar.Value
            : throw new InvalidDataException("LVM2 metadata valueがscalarではありません。");
    }

    public ulong RequireUInt64()
    {
        var text = RequireScalar();
        return ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new InvalidDataException($"LVM2 metadata数値が不正です: {text}");
    }
}

internal sealed record LvmConfigScalar(string Value) : LvmConfigValue;

internal sealed record LvmConfigArray(IReadOnlyList<LvmConfigValue> Values) : LvmConfigValue;

internal sealed record LvmConfigObject(IReadOnlyDictionary<string, LvmConfigValue> Values) : LvmConfigValue
{
    public string RequireScalar(string name) => Require(name).RequireScalar();

    public ulong RequireUInt64(string name) => Require(name).RequireUInt64();

    public LvmConfigObject RequireObject(string name)
    {
        return Require(name) is LvmConfigObject value
            ? value
            : throw new InvalidDataException($"LVM2 metadataの{name}がobjectではありません。");
    }

    public IReadOnlyList<LvmConfigValue> RequireArray(string name)
    {
        return Require(name) is LvmConfigArray value
            ? value.Values
            : throw new InvalidDataException($"LVM2 metadataの{name}がarrayではありません。");
    }

    private LvmConfigValue Require(string name)
    {
        return Values.TryGetValue(name, out var value)
            ? value
            : throw new InvalidDataException($"LVM2 metadataに{name}がありません。");
    }
}

internal sealed class LvmConfigParser
{
    private readonly LvmConfigLexer _lexer;
    private readonly int _maximumEntries;
    private readonly int _maximumDepth;
    private LvmConfigToken _current;
    private int _entries;

    public LvmConfigParser(string text, int maximumEntries, int maximumDepth)
    {
        _lexer = new LvmConfigLexer(text);
        _maximumEntries = maximumEntries;
        _maximumDepth = maximumDepth;
        _current = _lexer.Next();
    }

    public LvmConfigObject Parse()
    {
        var result = ParseObject(0, false);
        Require(LvmConfigTokenKind.End);
        return result;
    }

    private LvmConfigObject ParseObject(int depth, bool requireClosingBrace)
    {
        if (depth > _maximumDepth)
        {
            throw new InvalidDataException("LVM2 metadataの入れ子が深すぎます。");
        }

        var values = new Dictionary<string, LvmConfigValue>(StringComparer.Ordinal);
        while (_current.Kind != LvmConfigTokenKind.End
            && _current.Kind != LvmConfigTokenKind.RightBrace)
        {
            var name = RequireName();
            LvmConfigValue value;
            if (_current.Kind == LvmConfigTokenKind.Equals)
            {
                Advance();
                value = ParseValue(depth + 1);
            }
            else if (_current.Kind == LvmConfigTokenKind.LeftBrace)
            {
                Advance();
                value = ParseObject(depth + 1, true);
            }
            else
            {
                throw new InvalidDataException($"LVM2 metadataの{name}に=または{{がありません。");
            }

            _entries++;
            if (_entries > _maximumEntries || !values.TryAdd(name, value))
            {
                throw new InvalidDataException($"LVM2 metadataの要素数超過または重複keyです: {name}");
            }
        }

        if (requireClosingBrace)
        {
            Require(LvmConfigTokenKind.RightBrace);
            Advance();
        }

        return new LvmConfigObject(values);
    }

    private LvmConfigValue ParseValue(int depth)
    {
        if (depth > _maximumDepth)
        {
            throw new InvalidDataException("LVM2 metadataの入れ子が深すぎます。");
        }

        if (_current.Kind is LvmConfigTokenKind.Atom or LvmConfigTokenKind.String)
        {
            var value = new LvmConfigScalar(_current.Text);
            Advance();
            return value;
        }

        if (_current.Kind != LvmConfigTokenKind.LeftBracket)
        {
            throw new InvalidDataException("LVM2 metadataの値形式が不正です。");
        }

        Advance();
        var values = new List<LvmConfigValue>();
        while (_current.Kind != LvmConfigTokenKind.RightBracket)
        {
            if (_current.Kind == LvmConfigTokenKind.End)
            {
                throw new InvalidDataException("LVM2 metadata arrayが閉じられていません。");
            }

            if (_current.Kind == LvmConfigTokenKind.Comma)
            {
                Advance();
                continue;
            }

            values.Add(ParseValue(depth + 1));
            if (values.Count > _maximumEntries)
            {
                throw new InvalidDataException("LVM2 metadata arrayの要素数が多すぎます。");
            }
        }

        Advance();
        return new LvmConfigArray(values);
    }

    private string RequireName()
    {
        if (_current.Kind != LvmConfigTokenKind.Atom)
        {
            throw new InvalidDataException("LVM2 metadataのkeyが不正です。");
        }

        var result = _current.Text;
        Advance();
        return result;
    }

    private void Require(LvmConfigTokenKind kind)
    {
        if (_current.Kind != kind)
        {
            throw new InvalidDataException($"LVM2 metadata tokenが不正です: expected={kind}, actual={_current.Kind}");
        }
    }

    private void Advance() => _current = _lexer.Next();
}

internal sealed class LvmConfigLexer
{
    private readonly string _text;
    private int _offset;

    public LvmConfigLexer(string text)
    {
        _text = text;
    }

    public LvmConfigToken Next()
    {
        SkipWhitespaceAndComments();
        if (_offset >= _text.Length)
        {
            return new LvmConfigToken(LvmConfigTokenKind.End, "");
        }

        var ch = _text[_offset++];
        return ch switch
        {
            '{' => new LvmConfigToken(LvmConfigTokenKind.LeftBrace, "{"),
            '}' => new LvmConfigToken(LvmConfigTokenKind.RightBrace, "}"),
            '[' => new LvmConfigToken(LvmConfigTokenKind.LeftBracket, "["),
            ']' => new LvmConfigToken(LvmConfigTokenKind.RightBracket, "]"),
            '=' => new LvmConfigToken(LvmConfigTokenKind.Equals, "="),
            ',' => new LvmConfigToken(LvmConfigTokenKind.Comma, ","),
            '"' => new LvmConfigToken(LvmConfigTokenKind.String, ReadString()),
            _ => new LvmConfigToken(LvmConfigTokenKind.Atom, ReadAtom(ch))
        };
    }

    private string ReadString()
    {
        var result = new StringBuilder();
        while (_offset < _text.Length)
        {
            var ch = _text[_offset++];
            if (ch == '"')
            {
                return result.ToString();
            }

            if (ch == '\\')
            {
                if (_offset >= _text.Length)
                {
                    break;
                }

                var escaped = _text[_offset++];
                result.Append(escaped switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    _ => escaped
                });
            }
            else
            {
                result.Append(ch);
            }
        }

        throw new InvalidDataException("LVM2 metadata stringが閉じられていません。");
    }

    private string ReadAtom(char first)
    {
        var start = _offset - 1;
        while (_offset < _text.Length)
        {
            var ch = _text[_offset];
            if (char.IsWhiteSpace(ch) || ch is '{' or '}' or '[' or ']' or '=' or ',' or '#' or '"')
            {
                break;
            }

            _offset++;
        }

        if (_offset == start + 1 && first is '#' or '"')
        {
            throw new InvalidDataException("LVM2 metadata atomが不正です。");
        }

        return _text[start.._offset];
    }

    private void SkipWhitespaceAndComments()
    {
        while (_offset < _text.Length)
        {
            if (char.IsWhiteSpace(_text[_offset]))
            {
                _offset++;
                continue;
            }

            if (_text[_offset] != '#')
            {
                return;
            }

            while (_offset < _text.Length && _text[_offset] is not '\r' and not '\n')
            {
                _offset++;
            }
        }
    }
}

internal enum LvmConfigTokenKind
{
    End,
    Atom,
    String,
    LeftBrace,
    RightBrace,
    LeftBracket,
    RightBracket,
    Equals,
    Comma
}

internal sealed record LvmConfigToken(LvmConfigTokenKind Kind, string Text);
