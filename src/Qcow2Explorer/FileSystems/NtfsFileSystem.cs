using System.Text;
using Qcow2Explorer.Core;
using Qcow2Explorer.Partitions;

namespace Qcow2Explorer.FileSystems;

public sealed class NtfsFileSystem : IReadOnlyFileSystem
{
    private const ulong FileReferenceMask = 0x0000ffffffffffffUL;
    private const int MaxMftRecordsToScan = 250_000;

    private readonly IBlockReader _reader;
    private readonly int _bytesPerSector;
    private readonly int _clusterSize;
    private readonly int _fileRecordSize;
    private readonly long _mftLcn;
    private readonly List<NtfsDataRun> _mftRuns;
    private readonly long _mftSize;
    private readonly Dictionary<long, NtfsFileEntry> _entries = new();
    private readonly Dictionary<long, List<NtfsFileEntry>> _children = new();

    public NtfsFileSystem(IBlockReader reader, PartitionInfo partition)
    {
        _reader = reader;
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
        var clustersPerRecord = unchecked((sbyte)boot[64]);
        _fileRecordSize = clustersPerRecord > 0
            ? clustersPerRecord * _clusterSize
            : 1 << -clustersPerRecord;

        var mft0 = EndianUtilities.ReadBytes(reader, checked(_mftLcn * _clusterSize), _fileRecordSize);
        ApplyFixup(mft0);
        var mftEntry = ParseFileRecord(0, mft0);
        if (mftEntry?.Data is null || mftEntry.Data.Runs.Count == 0)
        {
            throw new NotSupportedException("NTFS $MFT の data runs を読み取れませんでした。");
        }

        _mftRuns = mftEntry.Data.Runs;
        _mftSize = mftEntry.Data.Size;
        Root = new VfsNode { Name = "", IsDirectory = true, Metadata = 5L };
        ScanMft();
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
            if (!_children.TryGetValue(entry.ParentId, out var list))
            {
                list = new List<NtfsFileEntry>();
                _children[entry.ParentId] = list;
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

    private NtfsFileEntry? ParseFileRecord(long id, byte[] record)
    {
        if (record.Length < 48 || Encoding.ASCII.GetString(record, 0, 4) != "FILE")
        {
            return null;
        }

        var flags = EndianUtilities.ReadUInt16Little(record, 22);
        if ((flags & 0x0001) == 0)
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
            IsDirectory = (flags & 0x0002) != 0
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
            if (attrType == 0x30)
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
                entry.Data = nonResident
                    ? ParseNonResidentData(record, attrOffset, (int)attrLength)
                    : ParseResidentData(record, attrOffset, (int)attrLength);
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

        return new NtfsDataAttribute(value.Length, value, new List<NtfsDataRun>());
    }

    private static NtfsDataAttribute? ParseNonResidentData(byte[] record, int attrOffset, int attrLength)
    {
        if (attrLength < 64)
        {
            return null;
        }

        var runOffset = EndianUtilities.ReadUInt16Little(record, attrOffset + 32);
        var realSize = EndianUtilities.ReadInt64Little(record, attrOffset + 48);
        if (runOffset >= attrLength)
        {
            return null;
        }

        var runData = new byte[attrLength - runOffset];
        Array.Copy(record, attrOffset + runOffset, runData, 0, runData.Length);
        return new NtfsDataAttribute(realSize, null, ParseDataRuns(runData));
    }

    private static List<NtfsDataRun> ParseDataRuns(byte[] runData)
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
                break;
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
            runs.Add(new NtfsDataRun(lcn, clusterCount));
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
                break;
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

    private static VfsNode ToNode(NtfsFileEntry entry)
    {
        return new VfsNode
        {
            Name = entry.Name,
            IsDirectory = entry.IsDirectory,
            Size = entry.IsDirectory ? 0 : entry.Data?.Size ?? entry.FileNameSize,
            ModifiedUtc = entry.ModifiedUtc,
            Metadata = entry.Id
        };
    }

    private sealed record NtfsFileName(long ParentId, string Name, byte Namespace, long Size, DateTime? ModifiedUtc);
    private sealed record NtfsDataRun(long Lcn, long ClusterCount);
    private sealed record NtfsDataAttribute(long Size, byte[]? ResidentData, List<NtfsDataRun> Runs);

    private sealed class NtfsFileEntry
    {
        public long Id { get; init; }
        public long ParentId { get; set; }
        public string Name { get; set; } = "";
        public bool IsDirectory { get; init; }
        public long FileNameSize { get; set; }
        public DateTime? ModifiedUtc { get; set; }
        public NtfsDataAttribute? Data { get; set; }
    }
}
