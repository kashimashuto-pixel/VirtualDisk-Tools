namespace Qcow2Explorer.Creation;

public enum VirtualDiskContainerFormat
{
    Raw,
    Qcow2,
}

public sealed record VirtualDiskCreationRequest(
    string DestinationPath,
    long CapacityBytes,
    VirtualDiskContainerFormat ContainerFormat,
    VirtualDiskPartitionTableKind PartitionTable,
    IReadOnlyList<VirtualDiskPartitionDefinition> Partitions,
    IReadOnlyList<VirtualDiskInitialFile>? InitialFiles = null);

public sealed record VirtualDiskInitialFile(
    int PartitionNumber,
    string SourcePath,
    string DestinationName);

public sealed record VirtualDiskCreationResult(
    string DestinationPath,
    long CapacityBytes,
    VirtualDiskContainerFormat ContainerFormat,
    VirtualDiskPartitionTableKind PartitionTable,
    IReadOnlyList<VirtualDiskPartitionLayout> Partitions);
