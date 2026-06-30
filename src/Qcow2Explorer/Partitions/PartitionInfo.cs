namespace Qcow2Explorer.Partitions;

using Qcow2Explorer.Core;

public sealed class PartitionInfo
{
    public int Number { get; init; }
    public string Scheme { get; init; } = "";
    public string Name { get; init; } = "";
    public string Type { get; init; } = "";
    public string TypeId { get; init; } = "";
    public bool Bootable { get; init; }
    public ulong StartLba { get; init; }
    public ulong SectorCount { get; init; }
    public uint SectorSize { get; init; } = 512;
    public string FileSystem { get; set; } = "";
    public IBlockReader? ReaderOverride { get; init; }
    public long? LengthOverrideBytes { get; init; }

    public long StartOffset => ReaderOverride is not null ? 0 : checked((long)(StartLba * SectorSize));
    public long LengthBytes => LengthOverrideBytes ?? checked((long)(SectorCount * SectorSize));

    public override string ToString()
    {
        var label = string.IsNullOrWhiteSpace(Name) ? Type : Name;
        return $"{Number}: {label} ({StartLba:N0} + {SectorCount:N0} sectors)";
    }
}
