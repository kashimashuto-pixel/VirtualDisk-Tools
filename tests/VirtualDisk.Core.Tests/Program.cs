using Qcow2Explorer.Core;
using Qcow2Explorer.Creation;
using Qcow2Explorer.FileSystems;
using Qcow2Explorer.Partitions;

var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"virtualdisk-core-tests-{Guid.NewGuid():N}");
Directory.CreateDirectory(temporaryDirectory);
try
{
    TestRawAndQcow2RoundTrip(temporaryDirectory);
    TestPartitionTables(temporaryDirectory);
    TestReaderFactory(temporaryDirectory);
    TestManagedNtfsCreation(temporaryDirectory);
    TestManagedExt4Creation(temporaryDirectory);
    TestManagedXfsCreation(temporaryDirectory);
    Qcow2RobustnessTests.Run(temporaryDirectory);
    PartitionTableRobustnessTests.Run(temporaryDirectory);
    CreationRobustnessTests.Run(temporaryDirectory);
    Console.WriteLine("All cross-platform core checks passed.");
}
finally
{
    Directory.Delete(temporaryDirectory, recursive: true);
}

static void TestRawAndQcow2RoundTrip(string directory)
{
    var rawPath = Path.Combine(directory, "roundtrip.raw");
    var qcow2Path = Path.Combine(directory, "roundtrip.qcow2");
    const int length = 8 * 1024 * 1024;
    var expectedStart = Enumerable.Range(0, 64 * 1024)
        .Select(index => checked((byte)((index * 29 + 7) & 0xff)))
        .ToArray();
    var expectedEnd = Enumerable.Range(0, 96 * 1024)
        .Select(index => checked((byte)((index * 17 + 11) & 0xff)))
        .ToArray();

    using (var raw = new FileStream(rawPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
    {
        raw.SetLength(length);
        raw.Position = 0;
        raw.Write(expectedStart);
        raw.Position = length - expectedEnd.Length;
        raw.Write(expectedEnd);
    }

    Qcow2SparseWriter.WriteFromRawAsync(rawPath, qcow2Path).GetAwaiter().GetResult();
    using var reader = new Qcow2Reader(qcow2Path);
    Assert(reader.Length == length, "QCOW2 virtual size");
    Assert(Read(reader, 0, expectedStart.Length).SequenceEqual(expectedStart), "QCOW2 leading data");
    Assert(Read(reader, length - expectedEnd.Length, expectedEnd.Length).SequenceEqual(expectedEnd), "QCOW2 trailing data");
    Assert(Read(reader, length / 2, 4096).All(value => value == 0), "QCOW2 sparse zero range");
}

static void TestPartitionTables(string directory)
{
    foreach (var table in new[] { VirtualDiskPartitionTableKind.Mbr, VirtualDiskPartitionTableKind.Gpt })
    {
        var path = Path.Combine(directory, $"partition-{table}.raw");
        const long diskLength = 512L * 1024 * 1024;
        var definitions = new[]
        {
            new VirtualDiskPartitionDefinition(
                64L * 1024 * 1024,
                "Cross-platform data",
                "VDT_CORE",
                VirtualDiskFileSystemKind.Ext4),
        };
        var planned = VirtualDiskPartitionTableWriter.Plan(diskLength, table, definitions);
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            stream.SetLength(diskLength);
            VirtualDiskPartitionTableWriter.Write(stream, diskLength, table, planned);
        }

        using var reader = new RawDiskImageReader(path);
        var actual = PartitionTableReader.ReadPartitions(reader);
        Assert(actual.Count == 1, $"{table} partition count");
        Assert(actual[0].StartOffset == planned[0].OffsetBytes, $"{table} partition offset");
        Assert(actual[0].LengthBytes == planned[0].SizeBytes, $"{table} partition size");
    }
}

static void TestReaderFactory(string directory)
{
    var path = Path.Combine(directory, "factory.raw");
    File.WriteAllBytes(path, new byte[4096]);
    using var reader = DiskImageReaderFactory.Open(path);
    Assert(reader is RawDiskImageReader, "RAW reader factory selection");
    Assert(reader.Length == 4096, "RAW reader factory length");
}

static void TestManagedNtfsCreation(string directory)
{
    var destinationPath = Path.Combine(directory, "managed-ntfs.raw");
    var initialFilePath = Path.Combine(directory, "initial-content.bin");
    var initialContent = Enumerable.Range(0, 128 * 1024)
        .Select(index => checked((byte)((index * 13 + 19) & 0xff)))
        .ToArray();
    File.WriteAllBytes(initialFilePath, initialContent);

    var result = VirtualDiskCreationService.CreateAsync(
            new VirtualDiskCreationRequest(
                destinationPath,
                512L * 1024 * 1024,
                VirtualDiskContainerFormat.Raw,
                VirtualDiskPartitionTableKind.Mbr,
                [
                    new VirtualDiskPartitionDefinition(
                        64L * 1024 * 1024,
                        "Managed NTFS",
                        "VDT_NTFS",
                        VirtualDiskFileSystemKind.Ntfs),
                ],
                InitialFiles: [new VirtualDiskInitialFile(1, initialFilePath, "SEEDED.BIN")]))
        .GetAwaiter()
        .GetResult();
    Assert(result.Partitions.Count == 1, "managed NTFS result partition count");

    using var reader = DiskImageReaderFactory.Open(destinationPath);
    var partition = PartitionTableReader.ReadPartitions(reader).Single();
    partition.FileSystem = FileSystemDetector.Detect(reader, partition);
    Assert(partition.FileSystem == "NTFS", "managed NTFS detection");
    using var fileSystem = FileSystemDetector.TryOpen(reader, partition, out var error) as IDisposable
        ?? throw new InvalidDataException(error);
    var readable = (IReadOnlyFileSystem)fileSystem;
    var entries = readable.ListDirectory(readable.Root);
    var readme = entries.Single(entry => entry.Name.Equals("VDT-README.txt", StringComparison.OrdinalIgnoreCase));
    var seeded = entries.Single(entry => entry.Name.Equals("SEEDED.BIN", StringComparison.OrdinalIgnoreCase));
    Assert(readme.Size > 0, "managed NTFS README");
    Assert(
        readable.ReadFile(seeded, 0, initialContent.Length).SequenceEqual(initialContent),
        "managed NTFS initial file content");
}

static void TestManagedExt4Creation(string directory)
{
    var destinationPath = Path.Combine(directory, "managed-ext4.raw");
    var initialFilePath = Path.Combine(directory, "ext4-initial-content.bin");
    var initialContent = Enumerable.Range(0, 192 * 1024)
        .Select(index => checked((byte)((index * 31 + 23) & 0xff)))
        .ToArray();
    File.WriteAllBytes(initialFilePath, initialContent);

    var result = VirtualDiskCreationService.CreateAsync(
            new VirtualDiskCreationRequest(
                destinationPath,
                512L * 1024 * 1024,
                VirtualDiskContainerFormat.Raw,
                VirtualDiskPartitionTableKind.Gpt,
                [
                    new VirtualDiskPartitionDefinition(
                        64L * 1024 * 1024,
                        "Managed ext4",
                        "VDT_EXT4",
                        VirtualDiskFileSystemKind.Ext4),
                ],
                InitialFiles: [new VirtualDiskInitialFile(1, initialFilePath, "seeded.bin")]))
        .GetAwaiter()
        .GetResult();
    Assert(result.Partitions.Count == 1, "managed ext4 result partition count");

    using var reader = DiskImageReaderFactory.Open(destinationPath);
    var partition = PartitionTableReader.ReadPartitions(reader).Single();
    partition.FileSystem = FileSystemDetector.Detect(reader, partition);
    Assert(partition.FileSystem == "ext4", "managed ext4 detection");
    var fileSystem = FileSystemDetector.TryOpen(reader, partition, out var error)
        ?? throw new InvalidDataException(error);
    try
    {
        var entries = fileSystem.ListDirectory(fileSystem.Root);
        var readme = entries.Single(entry => entry.Name == "VDT-README.txt");
        var seeded = entries.Single(entry => entry.Name == "seeded.bin");
        Assert(readme.Size > 0, "managed ext4 README");
        Assert(
            fileSystem.ReadFile(seeded, 0, initialContent.Length).SequenceEqual(initialContent),
            "managed ext4 initial file content");
    }
    finally
    {
        (fileSystem as IDisposable)?.Dispose();
    }
}

static void TestManagedXfsCreation(string directory)
{
    var destinationPath = Path.Combine(directory, "managed-xfs.raw");
    var initialFilePath = Path.Combine(directory, "xfs-initial-content.bin");
    var initialContent = Enumerable.Range(0, 224 * 1024)
        .Select(index => checked((byte)((index * 37 + 29) & 0xff)))
        .ToArray();
    File.WriteAllBytes(initialFilePath, initialContent);

    var result = VirtualDiskCreationService.CreateAsync(
            new VirtualDiskCreationRequest(
                destinationPath,
                512L * 1024 * 1024,
                VirtualDiskContainerFormat.Raw,
                VirtualDiskPartitionTableKind.Gpt,
                [
                    new VirtualDiskPartitionDefinition(
                        320L * 1024 * 1024,
                        "Managed XFS",
                        "VDT_XFS",
                        VirtualDiskFileSystemKind.Xfs),
                ],
                InitialFiles: [new VirtualDiskInitialFile(1, initialFilePath, "seeded.bin")]))
        .GetAwaiter()
        .GetResult();
    Assert(result.Partitions.Count == 1, "managed XFS result partition count");

    using var reader = DiskImageReaderFactory.Open(destinationPath);
    var partition = PartitionTableReader.ReadPartitions(reader).Single();
    partition.FileSystem = FileSystemDetector.Detect(reader, partition);
    Assert(partition.FileSystem == "XFS", "managed XFS detection");
    var fileSystem = FileSystemDetector.TryOpen(reader, partition, out var error)
        ?? throw new InvalidDataException(error);
    try
    {
        var entries = fileSystem.ListDirectory(fileSystem.Root);
        var readme = entries.Single(entry => entry.Name == "VDT-README.txt");
        var seeded = entries.Single(entry => entry.Name == "seeded.bin");
        Assert(readme.Size > 0, "managed XFS README");
        Assert(
            fileSystem.ReadFile(seeded, 0, initialContent.Length).SequenceEqual(initialContent),
            "managed XFS initial file content");
    }
    finally
    {
        (fileSystem as IDisposable)?.Dispose();
    }
}

static byte[] Read(IBlockReader reader, long offset, int count)
{
    var buffer = new byte[count];
    reader.ReadAt(offset, buffer, 0, count);
    return buffer;
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException($"Assertion failed: {message}");
    }
}
