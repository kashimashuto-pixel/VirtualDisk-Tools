using System.Text;
using Qcow2Explorer.Core;
using Qcow2Explorer.Partitions;

namespace Qcow2Explorer.FileSystems;

public sealed class NtfsFileSystem : IReadOnlyFileSystem, IFileContentWriter
{
    private const ulong FileReferenceMask = 0x0000ffffffffffffUL;
    private const int MaxMftRecordsToScan = 250_000;
    private const ushort CompressedAttributeFlag = 0x0001;
    private const ushort EncryptedAttributeFlag = 0x4000;
    private const ushort SparseAttributeFlag = 0x8000;

    private readonly IBlockReader _reader;
    private readonly IBlockWriter? _writer;
    private readonly int _bytesPerSector;
    private readonly int _clusterSize;
    private readonly int _fileRecordSize;
    private readonly long _mftLcn;
    private readonly List<NtfsDataRun> _mftRuns;
    private readonly long _mftSize;
    private readonly Dictionary<long, NtfsFileEntry> _entries = new();
    private readonly Dictionary<long, List<NtfsFileEntry>> _children = new();
    private readonly bool _deletedOnly;
    private readonly long _volumeClusterCount;
    private readonly ushort? _volumeFlags;
    private readonly bool _mftRecoveredFromMirror;

    public NtfsFileSystem(IBlockReader reader, PartitionInfo partition, bool deletedOnly = false)
    {
        _reader = reader;
        _writer = reader as IBlockWriter;
        _deletedOnly = deletedOnly;
        Partition = partition;
        var boot = EndianUtilities.ReadBytes(reader, 0, 512);
        if (Encoding.ASCII.GetString(boot, 3, 8) != "NTFS    ")
        {
            throw new InvalidDataException("NTFS ブートセクタではありません。");
        }

        _bytesPerSector = EndianUtilities.ReadUInt16Little(boot, 11);
        var sectorsPerCluster = boot[13];
        _clusterSize = _bytesPerSector * sectorsPerCluster;
        _mftLcn = EndianUtilities.ReadInt64Little(boot, 48);
        var mftMirrorLcn = EndianUtilities.ReadInt64Little(boot, 56);
        var clustersPerRecord = unchecked((sbyte)boot[64]);
        _fileRecordSize = clustersPerRecord > 0
            ? clustersPerRecord * _clusterSize
            : 1 << -clustersPerRecord;
        var totalSectors = EndianUtilities.ReadInt64Little(boot, 40);
        if (_bytesPerSector is not (512 or 1024 or 2048 or 4096)
            || sectorsPerCluster == 0
            || (sectorsPerCluster & (sectorsPerCluster - 1)) != 0
            || _clusterSize <= 0
            || _fileRecordSize < _bytesPerSector
            || _fileRecordSize > 64 * 1024
            || totalSectors <= 0
            || totalSectors > reader.Length / _bytesPerSector)
        {
            throw new InvalidDataException("NTFS boot geometryが不正です。");
        }

        _volumeClusterCount = totalSectors / sectorsPerCluster;

        var mft0 = ReadMftRecordZero(mftMirrorLcn, out var recoveredFromMirror);
        _mftRecoveredFromMirror = recoveredFromMirror;
        // Record 0 describes $MFT itself and is always an in-use record. Parse it
        // regardless of the requested scan filter so deleted-only scans can first
        // discover the data runs that contain the remaining MFT records.
        var mftEntry = ParseFileRecord(0, mft0, applyDeletionFilter: false);
        if (mftEntry?.Data is null || mftEntry.Data.Runs.Count == 0)
        {
            throw new NotSupportedException("NTFS $MFT の data runs を読み取れませんでした。");
        }

        _mftRuns = mftEntry.Data.Runs;
        _mftSize = mftEntry.Data.Size;
        Root = new VfsNode
        {
            Name = deletedOnly ? "Deleted files" : "",
            VirtualPath = @"\",
            IsDirectory = true,
            Metadata = deletedOnly ? -1L : 5L
        };
        ScanMft();
        _volumeFlags = TryReadVolumeFlags();
        if (!deletedOnly && !_entries.ContainsKey(5))
        {
            throw new InvalidDataException(recoveredFromMirror
                ? "NTFS $MFT の先頭レコードは $MFTMirr から復旧できましたが、主 $MFT のルートレコードが欠落しています。元のディスクイメージまたはバックアップから再取得してください。"
                : "NTFS $MFT のルートレコードを読み取れませんでした。");
        }
    }

    private byte[] ReadMftRecordZero(long mftMirrorLcn, out bool recoveredFromMirror)
    {
        recoveredFromMirror = false;
        var primary = EndianUtilities.ReadBytes(_reader, checked(_mftLcn * _clusterSize), _fileRecordSize);
        if (TryPrepareFileRecord(primary))
        {
            return primary;
        }

        if (mftMirrorLcn <= 0)
        {
            throw new InvalidDataException("NTFS $MFT の先頭レコードが破損しており、$MFTMirr の位置も不正です。");
        }

        var mirror = EndianUtilities.ReadBytes(_reader, checked(mftMirrorLcn * _clusterSize), _fileRecordSize);
        if (!TryPrepareFileRecord(mirror))
        {
            throw new InvalidDataException("NTFS $MFT と $MFTMirr の先頭レコードがどちらも破損しています。");
        }

        recoveredFromMirror = true;
        return mirror;
    }

    private bool TryPrepareFileRecord(byte[] record)
    {
        if (record.Length < 8 || Encoding.ASCII.GetString(record, 0, 4) != "FILE")
        {
            return false;
        }

        try
        {
            ApplyFixup(record);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    public string Name => "NTFS";
    public PartitionInfo Partition { get; }
    public VfsNode Root { get; }

    public IReadOnlyList<VfsNode> ListDirectory(VfsNode directory)
    {
        var parentId = (long)directory.Metadata!;
        if (!_children.TryGetValue(parentId, out var children))
        {
            return Array.Empty<VfsNode>();
        }

        return children
            .OrderByDescending(e => e.IsDirectory)
            .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(ToNode)
            .ToList();
    }

    public byte[] ReadFile(VfsNode file, long offset, int count)
    {
        if (file.IsDirectory || file.Metadata is not long id || !_entries.TryGetValue(id, out var entry) || entry.Data is null)
        {
            return Array.Empty<byte>();
        }

        if (offset >= entry.Data.Size || count <= 0)
        {
            return Array.Empty<byte>();
        }

        var available = checked((int)Math.Min(count, entry.Data.Size - offset));
        if (entry.Data.ResidentData is not null)
        {
            var residentAvailable = Math.Min(available, Math.Max(0, entry.Data.ResidentData.Length - (int)offset));
            var resident = new byte[residentAvailable];
            Array.Copy(entry.Data.ResidentData, (int)offset, resident, 0, residentAvailable);
            return resident;
        }

        var output = new byte[available];
        ReadFromRuns(entry.Data.Runs, entry.Data.Size, offset, output, 0, available);
        return output;
    }

    public bool TryResolvePath(string path, out VfsNode node)
    {
        node = Root;
        var parentId = 5L;
        foreach (var part in path.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!_children.TryGetValue(parentId, out var children))
            {
                return false;
            }

            var entry = children.SingleOrDefault(candidate =>
                string.Equals(candidate.Name, part, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                return false;
            }

            node = ToNode(entry);
            parentId = entry.Id;
        }

        return true;
    }

    internal (bool IsValid, string Reason) ValidateForEditing()
    {
        if (_deletedOnly)
        {
            return (false, "削除済みファイル走査ビューは編集できません。");
        }

        if (_mftRecoveredFromMirror)
        {
            return (false, "$MFTの主レコードが破損し、$MFTMirrから復旧されたvolumeには書き込めません。");
        }

        if (_volumeFlags is null)
        {
            return (false, "NTFS $Volumeの状態を確認できません。");
        }

        if (_volumeFlags != 0)
        {
            return (false, $"dirtyまたは保守状態のNTFS volumeは編集できません: flags=0x{_volumeFlags:X4}");
        }

        if (!_entries.TryGetValue(6, out var bitmapEntry) || bitmapEntry.Data is null)
        {
            return (false, "NTFS $Bitmapを読み取れません。");
        }

        var requiredBitmapBytes = checked((_volumeClusterCount + 7) / 8);
        if (bitmapEntry.Data.Size < requiredBitmapBytes)
        {
            return (false, "NTFS $Bitmapがvolume cluster数より短いです。");
        }

        try
        {
            foreach (var entry in _entries.Values)
            {
                var data = entry.Data;
                if (data is null || data.ResidentData is not null)
                {
                    continue;
                }

                if ((data.Flags & (CompressedAttributeFlag | EncryptedAttributeFlag | SparseAttributeFlag)) != 0)
                {
                    continue;
                }

                ValidateWritableRuns(data);
            }

            return (true, string.Empty);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or OverflowException)
        {
            return (false, ex.Message);
        }
    }

    public bool CanReplaceFile(VfsNode file, long replacementLength, out string reason)
    {
        if (_writer is null)
        {
            reason = "変更を保持する書き込みオーバーレイがありません。";
            return false;
        }

        if (_deletedOnly || file.IsDirectory || file.Metadata is not long id
            || !_entries.TryGetValue(id, out var entry) || entry.Data is null)
        {
            reason = "通常の使用中ファイルだけを置換できます。";
            return false;
        }

        if (replacementLength < 0 || replacementLength != entry.Data.Size)
        {
            reason = $"現在は元ファイルと同じサイズ（{entry.Data.Size:N0} bytes）の置換だけに対応しています。";
            return false;
        }

        if (_volumeFlags is null)
        {
            reason = "NTFS $Volumeの状態を確認できないため書き込めません。";
            return false;
        }

        if (_volumeFlags != 0)
        {
            reason = $"dirtyまたは保守状態のNTFS volumeは書き込めません: flags=0x{_volumeFlags:X4}";
            return false;
        }

        if (entry.HasAttributeList)
        {
            reason = "複数MFT recordにまたがるNTFS attribute listはまだ書き込めません。";
            return false;
        }

        var data = entry.Data;
        if (data.ResidentData is not null)
        {
            reason = "MFT内resident dataはまだ書き込めません。";
            return false;
        }

        if ((data.Flags & (CompressedAttributeFlag | EncryptedAttributeFlag | SparseAttributeFlag)) != 0)
        {
            reason = "圧縮、暗号化、またはsparse属性のNTFS dataはまだ書き込めません。";
            return false;
        }

        try
        {
            ValidateWritableRuns(data);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or OverflowException)
        {
            reason = ex.Message;
            return false;
        }

        reason = string.Empty;
        return true;
    }

    public void ReplaceFileContent(
        VfsNode file,
        Stream replacement,
        long replacementLength,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        if (!replacement.CanRead)
        {
            throw new ArgumentException("置換元ストリームを読み取れません。", nameof(replacement));
        }

        if (!CanReplaceFile(file, replacementLength, out var reason))
        {
            throw new NotSupportedException(reason);
        }

        var data = _entries[(long)file.Metadata!].Data!;
        var remaining = replacementLength;
        var buffer = new byte[1024 * 1024];
        foreach (var run in data.Runs)
        {
            var runBytes = checked(run.ClusterCount * _clusterSize);
            var bytesToWrite = Math.Min(remaining, runBytes);
            var physicalOffset = checked(run.Lcn * _clusterSize);
            long written = 0;
            while (written < bytesToWrite)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = checked((int)Math.Min(buffer.Length, bytesToWrite - written));
                replacement.ReadExactly(buffer.AsSpan(0, count));
                _writer!.WriteAt(physicalOffset + written, buffer, 0, count);
                written += count;
                remaining -= count;
            }

            if (remaining == 0)
            {
                break;
            }
        }

        if (remaining != 0 || replacement.ReadByte() != -1)
        {
            throw new InvalidDataException("置換元ファイルのサイズが指定値と一致しません。変更は破棄してください。");
        }

        _writer!.Flush();
    }

    private void ScanMft()
    {
        var recordCount = Math.Min(_mftSize / _fileRecordSize, MaxMftRecordsToScan);
        for (long i = 0; i < recordCount; i++)
        {
            try
            {
                var record = ReadMftRecord(i);
                var entry = ParseFileRecord(i, record);
                if (entry is null || string.IsNullOrWhiteSpace(entry.Name))
                {
                    continue;
                }

                _entries[entry.Id] = entry;
            }
            catch
            {
                // NTFS images often contain unused or partially overwritten MFT records.
            }
        }

        foreach (var entry in _entries.Values)
        {
            var parentId = _deletedOnly ? -1 : entry.ParentId;
            if (!_children.TryGetValue(parentId, out var list))
            {
                list = new List<NtfsFileEntry>();
                _children[parentId] = list;
            }

            if (entry.Id != entry.ParentId)
            {
                list.Add(entry);
            }
        }
    }

    private byte[] ReadMftRecord(long recordNumber)
    {
        var buffer = new byte[_fileRecordSize];
        ReadFromRuns(_mftRuns, _mftSize, recordNumber * _fileRecordSize, buffer, 0, buffer.Length);
        ApplyFixup(buffer);
        return buffer;
    }

    private NtfsFileEntry? ParseFileRecord(long id, byte[] record, bool applyDeletionFilter = true)
    {
        if (record.Length < 48 || Encoding.ASCII.GetString(record, 0, 4) != "FILE")
        {
            return null;
        }

        var flags = EndianUtilities.ReadUInt16Little(record, 22);
        var isDeleted = (flags & 0x0001) == 0;
        if (applyDeletionFilter && isDeleted != _deletedOnly)
        {
            return null;
        }

        var baseRecord = (long)(EndianUtilities.ReadUInt64Little(record, 32) & FileReferenceMask);
        if (baseRecord != 0)
        {
            return null;
        }

        var entry = new NtfsFileEntry
        {
            Id = id,
            IsDirectory = (flags & 0x0002) != 0,
            IsDeleted = isDeleted
        };

        var names = new List<NtfsFileName>();
        var attrOffset = (int)EndianUtilities.ReadUInt16Little(record, 20);
        while (attrOffset + 16 <= record.Length)
        {
            var attrType = EndianUtilities.ReadUInt32Little(record, attrOffset);
            if (attrType == 0xffffffff)
            {
                break;
            }

            var attrLength = EndianUtilities.ReadUInt32Little(record, attrOffset + 4);
            if (attrLength < 24 || attrOffset + attrLength > record.Length)
            {
                break;
            }

            var nonResident = record[attrOffset + 8] != 0;
            if (attrType == 0x10 && !nonResident)
            {
                var value = GetResidentValue(record, attrOffset, (int)attrLength);
                if (value is { Length: >= 36 })
                {
                    entry.Attributes = (FileAttributes)EndianUtilities.ReadUInt32Little(value, 32);
                }
            }
            else if (attrType == 0x30)
            {
                var value = GetResidentValue(record, attrOffset, (int)attrLength);
                if (value is not null)
                {
                    var name = ParseFileName(value);
                    if (name is not null)
                    {
                        names.Add(name);
                    }
                }
            }
            else if (attrType == 0x80)
            {
                var nameLength = record[attrOffset + 9];
                if (nameLength == 0 && entry.Data is null)
                {
                    entry.Data = nonResident
                        ? ParseNonResidentData(record, attrOffset, (int)attrLength)
                        : ParseResidentData(record, attrOffset, (int)attrLength);
                }
            }
            else if (attrType == 0x20)
            {
                entry.HasAttributeList = true;
            }

            attrOffset += (int)attrLength;
        }

        var selectedName = names
            .Where(n => n.Namespace != 2)
            .OrderBy(n => n.Namespace == 1 ? 0 : n.Namespace == 3 ? 1 : 2)
            .FirstOrDefault()
            ?? names.FirstOrDefault();
        if (selectedName is null)
        {
            return null;
        }

        entry.Name = selectedName.Name;
        entry.ParentId = selectedName.ParentId;
        entry.ModifiedUtc = selectedName.ModifiedUtc;
        entry.FileNameSize = selectedName.Size;
        return entry;
    }

    private static byte[]? GetResidentValue(byte[] record, int attrOffset, int attrLength)
    {
        if (attrLength < 24)
        {
            return null;
        }

        var valueLength = EndianUtilities.ReadUInt32Little(record, attrOffset + 16);
        var valueOffset = EndianUtilities.ReadUInt16Little(record, attrOffset + 20);
        if (valueOffset + valueLength > attrLength)
        {
            return null;
        }

        var value = new byte[valueLength];
        Array.Copy(record, attrOffset + valueOffset, value, 0, value.Length);
        return value;
    }

    private static NtfsDataAttribute? ParseResidentData(byte[] record, int attrOffset, int attrLength)
    {
        var value = GetResidentValue(record, attrOffset, attrLength);
        if (value is null)
        {
            return null;
        }

        return new NtfsDataAttribute(value.Length, value.Length, 0, 0, value, new List<NtfsDataRun>());
    }

    private static NtfsDataAttribute? ParseNonResidentData(byte[] record, int attrOffset, int attrLength)
    {
        if (attrLength < 64)
        {
            return null;
        }

        var runOffset = EndianUtilities.ReadUInt16Little(record, attrOffset + 32);
        var lowestVcn = EndianUtilities.ReadInt64Little(record, attrOffset + 16);
        var highestVcn = EndianUtilities.ReadInt64Little(record, attrOffset + 24);
        var allocatedSize = EndianUtilities.ReadInt64Little(record, attrOffset + 40);
        var realSize = EndianUtilities.ReadInt64Little(record, attrOffset + 48);
        if (runOffset >= attrLength)
        {
            return null;
        }

        var runData = new byte[attrLength - runOffset];
        Array.Copy(record, attrOffset + runOffset, runData, 0, runData.Length);
        return new NtfsDataAttribute(
            realSize,
            allocatedSize,
            EndianUtilities.ReadUInt16Little(record, attrOffset + 12),
            lowestVcn,
            null,
            ParseDataRuns(runData, highestVcn));
    }

    private static List<NtfsDataRun> ParseDataRuns(byte[] runData, long expectedHighestVcn = -1)
    {
        var runs = new List<NtfsDataRun>();
        long currentLcn = 0;
        var offset = 0;
        while (offset < runData.Length && runData[offset] != 0)
        {
            var header = runData[offset++];
            var lengthSize = header & 0x0f;
            var offsetSize = (header >> 4) & 0x0f;
            if (lengthSize == 0 || offset + lengthSize + offsetSize > runData.Length)
            {
                throw new InvalidDataException("NTFS data run headerが不正です。");
            }

            var clusterCount = (long)ReadVariableUInt(runData, offset, lengthSize);
            offset += lengthSize;
            long lcn = -1;
            if (offsetSize > 0)
            {
                var delta = EndianUtilities.SignExtend(ReadVariableUInt(runData, offset, offsetSize), offsetSize);
                currentLcn += delta;
                lcn = currentLcn;
            }

            offset += offsetSize;
            if (clusterCount <= 0)
            {
                throw new InvalidDataException("NTFS data runのcluster数が不正です。");
            }

            runs.Add(new NtfsDataRun(lcn, clusterCount));
        }

        if (offset >= runData.Length || runData[offset] != 0)
        {
            throw new InvalidDataException("NTFS data run terminatorがありません。");
        }

        if (expectedHighestVcn >= 0
            && runs.Sum(run => run.ClusterCount) != checked(expectedHighestVcn + 1))
        {
            throw new InvalidDataException("NTFS data runとVCN範囲が一致しません。");
        }

        return runs;
    }

    private static ulong ReadVariableUInt(byte[] data, int offset, int byteCount)
    {
        ulong result = 0;
        for (var i = 0; i < byteCount; i++)
        {
            result |= (ulong)data[offset + i] << (i * 8);
        }

        return result;
    }

    private static NtfsFileName? ParseFileName(byte[] value)
    {
        if (value.Length < 66)
        {
            return null;
        }

        var nameLength = value[64];
        var nameBytes = nameLength * 2;
        if (66 + nameBytes > value.Length)
        {
            return null;
        }

        var name = Encoding.Unicode.GetString(value, 66, nameBytes);
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        var parent = (long)(EndianUtilities.ReadUInt64Little(value, 0) & FileReferenceMask);
        var modified = ReadFileTime(value, 16);
        var realSize = EndianUtilities.ReadInt64Little(value, 48);
        return new NtfsFileName(parent, name, value[65], Math.Max(0, realSize), modified);
    }

    private void ReadFromRuns(IReadOnlyList<NtfsDataRun> runs, long dataSize, long offset, byte[] buffer, int bufferOffset, int count)
    {
        Array.Clear(buffer, bufferOffset, count);
        if (offset >= dataSize)
        {
            return;
        }

        var remaining = Math.Min(count, dataSize - offset);
        var written = 0;
        long logical = 0;

        foreach (var run in runs)
        {
            var runBytes = run.ClusterCount * _clusterSize;
            if (offset >= logical + runBytes)
            {
                logical += runBytes;
                continue;
            }

            var inRun = Math.Max(0, offset - logical);
            var chunk = checked((int)Math.Min(remaining, runBytes - inRun));
            if (run.Lcn >= 0)
            {
                _reader.ReadAt(run.Lcn * _clusterSize + inRun, buffer, bufferOffset + written, chunk);
            }

            written += chunk;
            remaining -= chunk;
            offset += chunk;
            logical += runBytes;
            if (remaining <= 0)
            {
                break;
            }
        }
    }

    private void ApplyFixup(byte[] record)
    {
        var usaOffset = EndianUtilities.ReadUInt16Little(record, 4);
        var usaCount = EndianUtilities.ReadUInt16Little(record, 6);
        if (usaOffset == 0 || usaOffset + usaCount * 2 > record.Length || usaCount < 2)
        {
            throw new InvalidDataException("NTFS FILE record の update sequence array が不正です。");
        }

        for (var i = 1; i < usaCount; i++)
        {
            var sectorEnd = i * _bytesPerSector - 2;
            if (sectorEnd + 1 >= record.Length)
            {
                throw new InvalidDataException("NTFS FILE record の update sequence array がレコード範囲を超えています。");
            }

            if (record[sectorEnd] != record[usaOffset] || record[sectorEnd + 1] != record[usaOffset + 1])
            {
                throw new InvalidDataException("NTFS FILE record の update sequence number が一致しません。");
            }

            record[sectorEnd] = record[usaOffset + i * 2];
            record[sectorEnd + 1] = record[usaOffset + i * 2 + 1];
        }
    }

    private static DateTime? ReadFileTime(byte[] data, int offset)
    {
        var value = EndianUtilities.ReadInt64Little(data, offset);
        if (value <= 0)
        {
            return null;
        }

        try
        {
            return DateTime.FromFileTimeUtc(value);
        }
        catch
        {
            return null;
        }
    }

    private VfsNode ToNode(NtfsFileEntry entry)
    {
        return new VfsNode
        {
            Name = entry.Name,
            VirtualPath = GetVirtualPath(entry),
            IsDirectory = entry.IsDirectory,
            Size = entry.IsDirectory ? 0 : entry.Data?.Size ?? entry.FileNameSize,
            ModifiedUtc = entry.ModifiedUtc,
            Attributes = entry.Attributes,
            Metadata = entry.Id
        };
    }

    private string GetVirtualPath(NtfsFileEntry entry)
    {
        var components = new Stack<string>();
        var visited = new HashSet<long>();
        var current = entry;
        while (current.Id != 5)
        {
            if (!visited.Add(current.Id))
            {
                throw new InvalidDataException("NTFS parent参照が循環しています。");
            }

            components.Push(current.Name);
            if (current.ParentId == 5)
            {
                break;
            }

            if (!_entries.TryGetValue(current.ParentId, out current))
            {
                break;
            }
        }

        return @"\" + string.Join(@"\", components);
    }

    private ushort? TryReadVolumeFlags()
    {
        try
        {
            var record = ReadMftRecord(3);
            var attrOffset = (int)EndianUtilities.ReadUInt16Little(record, 20);
            while (attrOffset + 24 <= record.Length)
            {
                var attrType = EndianUtilities.ReadUInt32Little(record, attrOffset);
                if (attrType == 0xffffffff)
                {
                    break;
                }

                var attrLength = EndianUtilities.ReadUInt32Little(record, attrOffset + 4);
                if (attrLength < 24 || attrOffset + attrLength > record.Length)
                {
                    return null;
                }

                if (attrType == 0x70 && record[attrOffset + 8] == 0)
                {
                    var value = GetResidentValue(record, attrOffset, checked((int)attrLength));
                    return value is { Length: >= 12 }
                        ? EndianUtilities.ReadUInt16Little(value, 10)
                        : null;
                }

                attrOffset += checked((int)attrLength);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OverflowException)
        {
        }

        return null;
    }

    private void ValidateWritableRuns(NtfsDataAttribute data)
    {
        if (data.LowestVcn != 0 || data.Size < 0 || data.AllocatedSize < data.Size || data.Runs.Count == 0)
        {
            throw new InvalidDataException("NTFS data attributeのsizeまたはVCNが不正です。");
        }

        long coveredBytes = 0;
        foreach (var run in data.Runs)
        {
            if (run.Lcn < 0)
            {
                throw new NotSupportedException("sparse data runを含むNTFSファイルはまだ書き込めません。");
            }

            var endLcn = checked(run.Lcn + run.ClusterCount);
            if (endLcn > _volumeClusterCount)
            {
                throw new InvalidDataException("NTFS data runがvolume範囲外です。");
            }

            coveredBytes = checked(coveredBytes + checked(run.ClusterCount * _clusterSize));
        }

        if (coveredBytes < data.Size || data.AllocatedSize > coveredBytes)
        {
            throw new InvalidDataException("NTFS data runがfile sizeを完全にカバーしていません。");
        }

        if (!_entries.TryGetValue(6, out var bitmapEntry) || bitmapEntry.Data is null)
        {
            throw new InvalidDataException("NTFS $Bitmapを読み取れないためcluster割り当てを検証できません。");
        }

        var bitmap = bitmapEntry.Data;
        var requiredBitmapBytes = checked((_volumeClusterCount + 7) / 8);
        if (bitmap.Size < requiredBitmapBytes)
        {
            throw new InvalidDataException("NTFS $Bitmapがvolume cluster数より短いです。");
        }

        foreach (var run in data.Runs)
        {
            ValidateBitmapAllocation(bitmap, run.Lcn, run.ClusterCount);
        }
    }

    private void ValidateBitmapAllocation(NtfsDataAttribute bitmap, long firstLcn, long clusterCount)
    {
        const int bitmapChunkBytes = 1024 * 1024;
        var currentLcn = firstLcn;
        var remainingClusters = clusterCount;
        while (remainingClusters > 0)
        {
            var firstBit = checked((int)(currentLcn & 7));
            var clustersInChunk = Math.Min(
                remainingClusters,
                checked((long)bitmapChunkBytes * 8 - firstBit));
            var byteOffset = currentLcn / 8;
            var byteCount = checked((int)((firstBit + clustersInChunk + 7) / 8));
            var bytes = ReadDataAttribute(bitmap, byteOffset, byteCount);
            if (bytes.Length != byteCount)
            {
                throw new InvalidDataException("NTFS $Bitmapの必要範囲を読み取れません。");
            }

            for (long index = 0; index < clustersInChunk; index++)
            {
                var bit = checked(firstBit + index);
                if ((bytes[checked((int)(bit / 8))] & (1 << (int)(bit & 7))) == 0)
                {
                    throw new InvalidDataException(
                        $"NTFS data runが未割り当てclusterを参照しています: LCN={currentLcn + index:N0}");
                }
            }

            currentLcn += clustersInChunk;
            remainingClusters -= clustersInChunk;
        }
    }

    private byte[] ReadDataAttribute(NtfsDataAttribute data, long offset, int count)
    {
        if (data.ResidentData is not null)
        {
            if (offset < 0 || offset > data.ResidentData.Length - count)
            {
                return Array.Empty<byte>();
            }

            return data.ResidentData.AsSpan(checked((int)offset), count).ToArray();
        }

        var result = new byte[count];
        ReadFromRuns(data.Runs, data.Size, offset, result, 0, count);
        return result;
    }

    private sealed record NtfsFileName(long ParentId, string Name, byte Namespace, long Size, DateTime? ModifiedUtc);
    private sealed record NtfsDataRun(long Lcn, long ClusterCount);
    private sealed record NtfsDataAttribute(
        long Size,
        long AllocatedSize,
        ushort Flags,
        long LowestVcn,
        byte[]? ResidentData,
        List<NtfsDataRun> Runs);

    private sealed class NtfsFileEntry
    {
        public long Id { get; init; }
        public long ParentId { get; set; }
        public string Name { get; set; } = "";
        public bool IsDirectory { get; init; }
        public bool IsDeleted { get; init; }
        public long FileNameSize { get; set; }
        public DateTime? ModifiedUtc { get; set; }
        public FileAttributes Attributes { get; set; }
        public NtfsDataAttribute? Data { get; set; }
        public bool HasAttributeList { get; set; }
    }
}
