using System.Globalization;
using System.IO.Compression;
using System.Text;
using Qcow2Explorer.Core;
using Qcow2Explorer.FileSystems;
using Qcow2Explorer.Mounting;
using Qcow2Explorer.Partitions;
using DiscUtils.Streams;
using DiscXfsFileSystem = DiscUtils.Xfs.XfsFileSystem;
using VdiDisk = DiscUtils.Vdi.Disk;

if (args.Length > 0)
{
    InspectImage(args[0], args.Any(a => string.Equals(a, "--copy-smoke", StringComparison.OrdinalIgnoreCase)));
    return;
}

RunGeneratedImageTests();

static void RunGeneratedImageTests()
{
    var imagePath = Path.Combine(AppContext.BaseDirectory, "sample-fat16.qcow2");
    TestImageFactory.CreateFat16Qcow2(imagePath);

    using var reader = new Qcow2Reader(imagePath);
    Assert(reader.Header.Version == 3, "qcow2 version");
    Assert(reader.Length == TestImageFactory.VirtualSize, "virtual size");

    var mbr = new byte[512];
    reader.ReadAt(0, mbr, 0, mbr.Length);
    Assert(mbr[510] == 0x55 && mbr[511] == 0xaa, "MBR signature");

    var partitions = PartitionTableReader.ReadPartitions(reader);
    Assert(partitions.Count == 1, "partition count");
    var partition = partitions[0];
    partition.FileSystem = FileSystemDetector.Detect(reader, partition);
    Assert(partition.FileSystem == "FAT16", "FAT16 detection");

    var fs = FileSystemDetector.TryOpen(reader, partition, out var error);
    Assert(fs is not null, error);

    var root = fs!.ListDirectory(fs.Root);
    var hello = root.Single(n => n.Name == "HELLO.TXT");
    var docs = root.Single(n => n.Name == "DOCS");
    Assert(!hello.IsDirectory && docs.IsDirectory, "root entries");

    var helloText = Encoding.ASCII.GetString(fs.ReadFile(hello, 0, (int)hello.Size));
    Assert(helloText == TestImageFactory.HelloText, "HELLO.TXT content");

    var docsEntries = fs.ListDirectory(docs);
    var readme = docsEntries.Single(n => n.Name == "README.TXT");
    var readmeText = Encoding.ASCII.GetString(fs.ReadFile(readme, 0, (int)readme.Size));
    Assert(readmeText == TestImageFactory.ReadmeText, "README.TXT content");

    var copyDirectory = Path.Combine(AppContext.BaseDirectory, "copy-output");
    if (Directory.Exists(copyDirectory))
    {
        Directory.Delete(copyDirectory, recursive: true);
    }

    var copyResult = FileSystemExporter.CopyNodes(fs, new[] { hello, docs }, copyDirectory);
    Assert(copyResult.FilesCopied == 2, "copied file count");
    Assert(copyResult.Errors.Count == 0, "copy errors");
    Assert(copyResult.Manifest.Count == 2, "SHA-256 manifest entries");
    Assert(File.Exists(Path.Combine(copyDirectory, "VirtualDiskExplorer.sha256")), "SHA-256 manifest file");
    Assert(File.ReadAllText(Path.Combine(copyDirectory, "HELLO.TXT"), Encoding.ASCII) == TestImageFactory.HelloText, "copied HELLO.TXT");
    Assert(File.ReadAllText(Path.Combine(copyDirectory, "DOCS", "README.TXT"), Encoding.ASCII) == TestImageFactory.ReadmeText, "copied README.TXT");

    var searchResults = FileSystemSearch.Search(fs, "readme");
    Assert(searchResults.Count == 1 && searchResults[0].Path == "/DOCS/README.TXT", "recursive file search");

    Console.WriteLine("All qcow2 reader checks passed.");
    Console.WriteLine(imagePath);

    var compressedImagePath = Path.Combine(AppContext.BaseDirectory, "sample-fat16-compressed.qcow2");
    TestImageFactory.CreateFat16Qcow2(compressedImagePath, compressKeyClusters: true);
    using var compressedReader = new Qcow2Reader(compressedImagePath);
    var compressedPartitions = PartitionTableReader.ReadPartitions(compressedReader);
    Assert(compressedPartitions.Count == 1, "compressed partition count");
    var compressedPartition = compressedPartitions[0];
    compressedPartition.FileSystem = FileSystemDetector.Detect(compressedReader, compressedPartition);
    var compressedFs = FileSystemDetector.TryOpen(compressedReader, compressedPartition, out var compressedError);
    Assert(compressedFs is not null, compressedError);
    var compressedRoot = compressedFs!.ListDirectory(compressedFs.Root);
    var compressedHello = compressedRoot.Single(n => n.Name == "HELLO.TXT");
    Assert(Encoding.ASCII.GetString(compressedFs.ReadFile(compressedHello, 0, (int)compressedHello.Size)) == TestImageFactory.HelloText, "compressed HELLO.TXT content");
    Console.WriteLine(compressedImagePath);

    var rawImagePath = Path.Combine(AppContext.BaseDirectory, "sample-fat16.img");
    TestImageFactory.CreateRawFat16Disk(rawImagePath);
    using var rawReader = DiskImageReaderFactory.Open(rawImagePath);
    Assert(rawReader.FormatName.StartsWith("raw", StringComparison.OrdinalIgnoreCase), "raw image factory");
    var rawFs = AssertFat16Readable(rawReader, "raw");
    Console.WriteLine(rawImagePath);

    var vdiImagePath = Path.Combine(AppContext.BaseDirectory, "sample-fat16.vdi");
    TestImageFactory.CreateFat16Vdi(vdiImagePath);
    using (var vdiReader = DiskImageReaderFactory.Open(vdiImagePath))
    {
        Assert(vdiReader.FormatName == "VDI", "VDI image factory");
        AssertFat16Readable(vdiReader, "VDI");
    }

    Console.WriteLine(vdiImagePath);

    var hddImagePath = Path.Combine(AppContext.BaseDirectory, "sample-fat16.hdd");
    TestImageFactory.CreateParallelsHdd(hddImagePath);
    using (var hddReader = DiskImageReaderFactory.Open(hddImagePath))
    {
        Assert(hddReader.FormatName.StartsWith("Parallels HDD", StringComparison.OrdinalIgnoreCase), "Parallels HDD image factory");
        AssertFat16Readable(hddReader, "Parallels HDD");
    }

    Console.WriteLine(hddImagePath);

    using (var hdsReader = DiskImageReaderFactory.Open(Path.Combine(hddImagePath, "disk.hds")))
    {
        Assert(hdsReader.FormatName == "Parallels HDD (.hds)", "Parallels HDS image factory");
        AssertFat16Readable(hdsReader, "Parallels HDS");
    }

    RunProjFsRemountSmoke(rawFs);
}

static IReadOnlyFileSystem AssertFat16Readable(IDiskImageReader reader, string label)
{
    var partitions = PartitionTableReader.ReadPartitions(reader);
    Assert(partitions.Count == 1, $"{label} partition count");
    var partition = partitions[0];
    partition.FileSystem = FileSystemDetector.Detect(reader, partition);
    Assert(partition.FileSystem == "FAT16", $"{label} FAT16 detection");
    var fs = FileSystemDetector.TryOpen(reader, partition, out var error);
    Assert(fs is not null, error);
    var hello = fs!.ListDirectory(fs.Root).Single(n => n.Name == "HELLO.TXT");
    Assert(Encoding.ASCII.GetString(fs.ReadFile(hello, 0, (int)hello.Size)) == TestImageFactory.HelloText, $"{label} HELLO.TXT content");
    return fs;
}

static void InspectImage(string imagePath, bool copySmoke)
{
    using var reader = DiskImageReaderFactory.Open(imagePath);
    Console.WriteLine(imagePath);
    Console.WriteLine($"format: {reader.FormatName}");
    Console.WriteLine($"virtual size: {FormatBytes(reader.Length)}");
    foreach (var warning in reader.GetWarnings())
    {
        Console.WriteLine($"warning: {warning}");
    }

    var partitions = PartitionTableReader.ReadPartitions(reader).ToList();
    Console.WriteLine($"partitions: {partitions.Count}");
    foreach (var partition in partitions)
    {
        partition.FileSystem = FileSystemDetector.Detect(reader, partition);
        Console.WriteLine();
        Console.WriteLine($"#{partition.Number} {partition.Scheme} {partition.Name}");
        Console.WriteLine($"  type: {partition.Type} ({partition.TypeId})");
        Console.WriteLine($"  start: {partition.StartLba:N0}, sectors: {partition.SectorCount:N0}, size: {FormatBytes(partition.LengthBytes)}");
        Console.WriteLine($"  fs: {partition.FileSystem}");
        if (string.IsNullOrWhiteSpace(partition.FileSystem))
        {
            var signature = new byte[64];
            reader.ReadAt(partition.StartOffset, signature, 0, signature.Length);
            Console.WriteLine($"  signature: {FormatSignature(signature)}");
        }

        var fs = FileSystemDetector.TryOpen(reader, partition, out var error);
        if (fs is null)
        {
            Console.WriteLine($"  open: {error}");
            continue;
        }

        try
        {
            var root = fs.ListDirectory(fs.Root);
            Console.WriteLine($"  root entries: {root.Count}");
            DumpNodes(fs, root, "    ", depth: 1);
            if (fs.Name == "XFS")
            {
                AuditXfsPaths(fs, reader, partition);
            }
            else if (fs.Name == "NTFS")
            {
                AuditNtfsPaths(fs);
            }

            if (copySmoke && TryFindSmallFile(fs, fs.Root, maxDepth: 3, out var fileToCopy))
            {
                var copyRoot = Path.Combine(AppContext.BaseDirectory, "inspect-copy-output", $"partition-{partition.Number}");
                if (Directory.Exists(copyRoot))
                {
                    Directory.Delete(copyRoot, recursive: true);
                }

                var result = FileSystemExporter.CopyNode(fs, fileToCopy, copyRoot);
                var copiedPath = Directory.EnumerateFiles(copyRoot, "*", SearchOption.AllDirectories).FirstOrDefault();
                Console.WriteLine($"  copied: {fileToCopy.Name} ({FormatBytes(result.BytesCopied)}) -> {copiedPath}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  list failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}

static void AuditNtfsPaths(IReadOnlyFileSystem fs)
{
    foreach (var path in new[] { "Windows", "Windows/System32", "Windows/System32/cmd.exe" })
    {
        if (!TryFindPath(fs, fs.Root, path.Split('/'), out var node))
        {
            Console.WriteLine($"  audit /{path}: not found");
            continue;
        }

        Console.WriteLine($"  audit /{path}: {(node.IsDirectory ? "dir" : "file")}, size={FormatBytes(node.Size)}, meta={node.Metadata}");
        if (!node.IsDirectory && node.Size >= 2)
        {
            var signature = fs.ReadFile(node, 0, 2);
            Console.WriteLine($"      signature: {FormatSignature(signature)}");
        }

        if (node.IsDirectory)
        {
            var children = fs.ListDirectory(node);
            Console.WriteLine($"      entries={children.Count}");
            foreach (var child in children.Take(8))
            {
                Console.WriteLine($"      {(child.IsDirectory ? "<DIR>" : FormatBytes(child.Size)),10} {child.Name}");
            }
        }
    }
}

static void RunProjFsRemountSmoke(IReadOnlyFileSystem fs)
{
    if (!ProjectedFileSystemMount.IsProjFsLibraryPresent())
    {
        Console.WriteLine("ProjFS smoke skipped: Client-ProjFS is not available.");
        return;
    }

    var root = Path.Combine(AppContext.BaseDirectory, "projfs-remount-smoke");
    if (Directory.Exists(root))
    {
        Directory.Delete(root, recursive: true);
    }

    Directory.CreateDirectory(root);
    for (var i = 0; i < 2; i++)
    {
        using (var mount = ProjectedFileSystemMount.Start(fs, root))
        {
            var helloPath = Path.Combine(root, "HELLO.TXT");
            Assert(File.Exists(helloPath), $"ProjFS HELLO.TXT exists pass {i + 1}");
            Assert(File.ReadAllText(helloPath, Encoding.ASCII) == TestImageFactory.HelloText, $"ProjFS HELLO.TXT pass {i + 1}");
        }

        Assert(Directory.Exists(root), $"ProjFS root recreated pass {i + 1}");
        Assert((File.GetAttributes(root) & FileAttributes.ReparsePoint) == 0, $"ProjFS root reparse cleared pass {i + 1}");
    }

    Directory.Delete(root, recursive: true);
    Console.WriteLine("ProjFS remount smoke passed.");
}

static void AuditXfsPaths(IReadOnlyFileSystem fs, IBlockReader reader, PartitionInfo partition)
{
    using var directStream = new BlockReaderStream(new PartitionSliceReader(reader, partition));
    using var directXfs = new DiscXfsFileSystem(directStream);

    foreach (var path in new[] { "bin", "sbin", "usr", "usr/bin", "usr/sbin", "etc", "boot", "var/log" })
    {
        if (TryFindPath(fs, fs.Root, path.Split('/'), out var node))
        {
            try
            {
                var children = node.IsDirectory ? fs.ListDirectory(node) : Array.Empty<VfsNode>();
                Console.WriteLine($"  audit /{path}: {(node.IsDirectory ? "dir" : "file")}, entries={children.Count}, size={FormatBytes(node.Size)}, meta={node.Metadata}");
                if (fs is XfsFileSystem xfs)
                {
                    Console.WriteLine($"      xfs: {xfs.DescribeNode(node)}");
                }

                foreach (var child in children.Take(8))
                {
                    Console.WriteLine($"      {(child.IsDirectory ? "<DIR>" : FormatBytes(child.Size)),10} {child.Name}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  audit /{path}: failed {ex.GetType().Name}: {ex.Message}");
            }
        }
        else
        {
            Console.WriteLine($"  audit /{path}: not found");
        }

        var directPath = @"\" + path.Replace('/', '\\');
        try
        {
            var entries = directXfs.GetFileSystemEntries(directPath).ToArray();
            var dirs = directXfs.GetDirectories(directPath).ToArray();
            var files = directXfs.GetFiles(directPath).ToArray();
            Console.WriteLine($"  direct {directPath}: entries={entries.Length}, dirs={dirs.Length}, files={files.Length}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  direct {directPath}: failed {ex.GetType().Name}: {ex.Message}");
        }
    }
}

static bool TryFindPath(IReadOnlyFileSystem fs, VfsNode start, IReadOnlyList<string> parts, out VfsNode node)
{
    node = start;
    foreach (var part in parts)
    {
        var entries = fs.ListDirectory(node);
        var next = entries.FirstOrDefault(e => string.Equals(e.Name, part, StringComparison.OrdinalIgnoreCase));
        if (next is null)
        {
            return false;
        }

        node = next;
    }

    return true;
}

static bool TryFindSmallFile(IReadOnlyFileSystem fs, VfsNode directory, int maxDepth, out VfsNode file)
{
    var entries = fs.ListDirectory(directory);
    foreach (var node in entries.Where(n => !n.IsDirectory && n.Size is > 0 and <= 1024 * 1024))
    {
        file = node;
        return true;
    }

    if (maxDepth > 0)
    {
        foreach (var node in entries.Where(n => n.IsDirectory))
        {
            if (TryFindSmallFile(fs, node, maxDepth - 1, out file))
            {
                return true;
            }
        }
    }

    foreach (var node in entries.Where(n => !n.IsDirectory && n.Size <= 1024 * 1024))
    {
        file = node;
        return true;
    }

    file = default!;
    return false;
}

static void DumpNodes(IReadOnlyFileSystem fs, IReadOnlyList<VfsNode> nodes, string indent, int depth)
{
    foreach (var node in nodes.Take(40))
    {
        var kind = node.IsDirectory ? "<DIR>" : FormatBytes(node.Size);
        Console.WriteLine($"{indent}{kind,10} {node.Name}");
        if (depth > 0 && node.IsDirectory)
        {
            try
            {
                DumpNodes(fs, fs.ListDirectory(node), indent + "  ", depth - 1);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{indent}  list failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}

static string FormatSignature(byte[] data)
{
    var hex = string.Join(" ", data.Take(16).Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
    var ascii = new string(data.Take(16).Select(b => b >= 0x20 && b <= 0x7e ? (char)b : '.').ToArray());
    return $"{hex}  {ascii}";
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException($"Test failed: {message}");
    }
}

static string FormatBytes(long value)
{
    string[] suffixes = { "B", "KB", "MB", "GB", "TB", "PB" };
    double size = value;
    var suffix = 0;
    while (size >= 1024 && suffix < suffixes.Length - 1)
    {
        size /= 1024;
        suffix++;
    }

    return string.Create(CultureInfo.InvariantCulture, $"{size:0.##} {suffixes[suffix]}");
}

internal static class TestImageFactory
{
    public const int VirtualSize = 16 * 1024 * 1024;
    public const string HelloText = "Hello from qcow2 test\r\n";
    public const string ReadmeText = "Nested FAT16 file\r\n";

    private const int ClusterBits = 16;
    private const int ClusterSize = 1 << ClusterBits;
    private const int L1Offset = ClusterSize;
    private const int RefcountTableOffset = ClusterSize * 2;
    private const int RefcountBlockOffset = ClusterSize * 3;
    private const int L2Offset = ClusterSize * 4;
    private const int DataOffset = ClusterSize * 5;
    private const int PartitionStartLba = 2048;
    private const int PartitionSectors = 8192;
    private const int BytesPerSector = 512;
    private const int ReservedSectors = 1;
    private const int FatSectors = 32;
    private const int RootDirectoryEntries = 512;
    private const int RootDirectorySectors = 32;
    private const int FirstDataSector = ReservedSectors + FatSectors + RootDirectorySectors;

    public static void CreateRawFat16Disk(string path)
    {
        File.WriteAllBytes(path, CreateVirtualDisk());
    }

    public static void CreateFat16Vdi(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        using var disk = VdiDisk.InitializeDynamic(stream, Ownership.Dispose, VirtualSize);
        var bytes = CreateVirtualDisk();
        disk.Content.Position = 0;
        disk.Content.Write(bytes, 0, bytes.Length);
    }

    public static void CreateParallelsHdd(string path)
    {
        const string topGuid = "{5fbaabe3-6958-40ff-92a7-860e329aab41}";
        const string zeroGuid = "{00000000-0000-0000-0000-000000000000}";
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }

        Directory.CreateDirectory(path);
        CreateParallelsHds(Path.Combine(path, "disk.hds"), CreateVirtualDisk());
        File.WriteAllText(
            Path.Combine(path, "DiskDescriptor.xml"),
            $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <Parallels_disk_image Version="1.0">
              <Disk_Parameters>
                <Disk_size>{VirtualSize / BytesPerSector}</Disk_size>
              </Disk_Parameters>
              <StorageData>
                <Storage>
                  <Start>0</Start>
                  <End>{VirtualSize / BytesPerSector}</End>
                  <Blocksize>{BytesPerSector}</Blocksize>
                  <Image>
                    <GUID>{topGuid}</GUID>
                    <Type>Compressed</Type>
                    <File>disk.hds</File>
                  </Image>
                </Storage>
              </StorageData>
              <Snapshots>
                <TopGUID>{topGuid}</TopGUID>
                <Shot>
                  <GUID>{topGuid}</GUID>
                  <ParentGUID>{zeroGuid}</ParentGUID>
                </Shot>
              </Snapshots>
            </Parallels_disk_image>
            """,
            Encoding.UTF8);
    }

    public static void CreateFat16Qcow2(string path, bool compressKeyClusters = false)
    {
        var disk = CreateVirtualDisk();
        if (compressKeyClusters)
        {
            CreateCompressedFat16Qcow2(path, disk);
            return;
        }

        var qcow = new byte[DataOffset + disk.Length];

        WriteU32Be(qcow, 0, 0x514649fb);
        WriteU32Be(qcow, 4, 3);
        WriteU64Be(qcow, 8, 0);
        WriteU32Be(qcow, 16, 0);
        WriteU32Be(qcow, 20, ClusterBits);
        WriteU64Be(qcow, 24, VirtualSize);
        WriteU32Be(qcow, 32, 0);
        WriteU32Be(qcow, 36, 1);
        WriteU64Be(qcow, 40, L1Offset);
        WriteU64Be(qcow, 48, RefcountTableOffset);
        WriteU32Be(qcow, 56, 1);
        WriteU32Be(qcow, 60, 0);
        WriteU64Be(qcow, 64, 0);
        WriteU64Be(qcow, 72, 0);
        WriteU64Be(qcow, 80, 0);
        WriteU64Be(qcow, 88, 0);
        WriteU32Be(qcow, 96, 4);
        WriteU32Be(qcow, 100, 104);

        WriteU64Be(qcow, L1Offset, L2Offset);
        WriteU64Be(qcow, RefcountTableOffset, RefcountBlockOffset);

        var clusters = VirtualSize / ClusterSize;
        for (var i = 0; i < clusters; i++)
        {
            WriteU64Be(qcow, L2Offset + i * 8, (ulong)(DataOffset + i * ClusterSize));
        }

        Array.Copy(disk, 0, qcow, DataOffset, disk.Length);
        File.WriteAllBytes(path, qcow);
    }

    private static void CreateCompressedFat16Qcow2(string path, byte[] disk)
    {
        var image = new List<byte>(DataOffset + disk.Length);
        image.AddRange(new byte[DataOffset]);
        WriteHeader(image);

        var l2Entries = new ulong[VirtualSize / ClusterSize];
        for (var i = 0; i < l2Entries.Length; i++)
        {
            var cluster = new byte[ClusterSize];
            Array.Copy(disk, i * ClusterSize, cluster, 0, ClusterSize);

            if (i is 0 or 16)
            {
                Align(image, 512);
                var hostOffset = checked((ulong)image.Count);
                var compressed = CompressCluster(cluster);
                var sectors = (compressed.Length + 511) / 512;
                image.AddRange(compressed);
                image.AddRange(new byte[sectors * 512 - compressed.Length]);
                l2Entries[i] = (1UL << 62) | hostOffset | ((ulong)(sectors - 1) << (62 - (ClusterBits - 8)));
            }
            else
            {
                Align(image, ClusterSize);
                var hostOffset = checked((ulong)image.Count);
                image.AddRange(cluster);
                l2Entries[i] = hostOffset;
            }
        }

        for (var i = 0; i < l2Entries.Length; i++)
        {
            WriteU64Be(image, L2Offset + i * 8, l2Entries[i]);
        }

        File.WriteAllBytes(path, image.ToArray());
    }

    private static void CreateParallelsHds(string path, byte[] disk)
    {
        const int hdsHeaderSize = 64;
        const int hdsClusterSectors = 2048;
        const int hdsClusterSize = hdsClusterSectors * BytesPerSector;
        var batEntries = (disk.Length + hdsClusterSize - 1) / hdsClusterSize;
        var dataOffset = AlignUp(hdsHeaderSize + batEntries * 4, BytesPerSector);

        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var header = new byte[hdsHeaderSize];
        WriteAscii(header, 0, "WithoutFreeSpace", 16);
        WriteU32Le(header, 16, 2);
        WriteU32Le(header, 20, 16);
        WriteU32Le(header, 24, 63);
        WriteU32Le(header, 28, hdsClusterSectors);
        WriteU32Le(header, 32, batEntries);
        WriteU32Le(header, 36, disk.Length / BytesPerSector);
        WriteU32Le(header, 44, 0);
        WriteU32Le(header, 48, dataOffset / BytesPerSector);
        WriteU32Le(header, 52, 0);
        WriteU32Le(header, 56, 0);
        stream.Write(header, 0, header.Length);

        var firstHostSector = dataOffset / BytesPerSector;
        var batBytes = new byte[4];
        for (var i = 0; i < batEntries; i++)
        {
            var entry = firstHostSector + i * hdsClusterSectors;
            WriteU32Le(batBytes, 0, entry);
            stream.Write(batBytes, 0, batBytes.Length);
        }

        stream.Position = dataOffset;
        var zeroPadding = new byte[hdsClusterSize];
        for (var offset = 0; offset < disk.Length; offset += hdsClusterSize)
        {
            var count = Math.Min(hdsClusterSize, disk.Length - offset);
            stream.Write(disk, offset, count);
            if (count < hdsClusterSize)
            {
                stream.Write(zeroPadding, 0, hdsClusterSize - count);
            }
        }
    }

    private static byte[] CompressCluster(byte[] cluster)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            deflate.Write(cluster, 0, cluster.Length);
        }

        return output.ToArray();
    }

    private static void WriteHeader(List<byte> image)
    {
        WriteU32Be(image, 0, 0x514649fb);
        WriteU32Be(image, 4, 3);
        WriteU64Be(image, 8, 0);
        WriteU32Be(image, 16, 0);
        WriteU32Be(image, 20, ClusterBits);
        WriteU64Be(image, 24, VirtualSize);
        WriteU32Be(image, 32, 0);
        WriteU32Be(image, 36, 1);
        WriteU64Be(image, 40, L1Offset);
        WriteU64Be(image, 48, RefcountTableOffset);
        WriteU32Be(image, 56, 1);
        WriteU32Be(image, 60, 0);
        WriteU64Be(image, 64, 0);
        WriteU64Be(image, 72, 0);
        WriteU64Be(image, 80, 0);
        WriteU64Be(image, 88, 0);
        WriteU32Be(image, 96, 4);
        WriteU32Be(image, 100, 104);
        WriteU64Be(image, L1Offset, L2Offset);
        WriteU64Be(image, RefcountTableOffset, RefcountBlockOffset);
    }

    private static void Align(List<byte> image, int alignment)
    {
        var padding = (alignment - image.Count % alignment) % alignment;
        if (padding > 0)
        {
            image.AddRange(new byte[padding]);
        }
    }

    private static int AlignUp(int value, int alignment)
    {
        return (value + alignment - 1) / alignment * alignment;
    }

    private static byte[] CreateVirtualDisk()
    {
        var disk = new byte[VirtualSize];
        CreateMbr(disk);
        CreateFat16Partition(disk, PartitionStartLba * BytesPerSector);
        return disk;
    }

    private static void CreateMbr(byte[] disk)
    {
        var entry = 446;
        disk[entry + 4] = 0x06;
        WriteU32Le(disk, entry + 8, PartitionStartLba);
        WriteU32Le(disk, entry + 12, PartitionSectors);
        disk[510] = 0x55;
        disk[511] = 0xaa;
    }

    private static void CreateFat16Partition(byte[] disk, int start)
    {
        var boot = start;
        disk[boot] = 0xeb;
        disk[boot + 1] = 0x3c;
        disk[boot + 2] = 0x90;
        WriteAscii(disk, boot + 3, "MSDOS5.0", 8);
        WriteU16Le(disk, boot + 11, BytesPerSector);
        disk[boot + 13] = 1;
        WriteU16Le(disk, boot + 14, ReservedSectors);
        disk[boot + 16] = 1;
        WriteU16Le(disk, boot + 17, RootDirectoryEntries);
        WriteU16Le(disk, boot + 19, PartitionSectors);
        disk[boot + 21] = 0xf8;
        WriteU16Le(disk, boot + 22, FatSectors);
        WriteU16Le(disk, boot + 24, 63);
        WriteU16Le(disk, boot + 26, 255);
        WriteU32Le(disk, boot + 28, PartitionStartLba);
        disk[boot + 36] = 0x80;
        disk[boot + 38] = 0x29;
        WriteU32Le(disk, boot + 39, 0x12345678);
        WriteAscii(disk, boot + 43, "QCOW2TEST  ", 11);
        WriteAscii(disk, boot + 54, "FAT16   ", 8);
        disk[boot + 510] = 0x55;
        disk[boot + 511] = 0xaa;

        var fat = start + BytesPerSector;
        WriteU16Le(disk, fat, 0xfff8);
        WriteU16Le(disk, fat + 2, 0xffff);
        WriteU16Le(disk, fat + 4, 0xffff);
        WriteU16Le(disk, fat + 6, 0xffff);
        WriteU16Le(disk, fat + 8, 0xffff);

        var root = start + (ReservedSectors + FatSectors) * BytesPerSector;
        WriteDirectoryEntry(disk, root, "HELLO   TXT", 0x20, 2, HelloText.Length);
        WriteDirectoryEntry(disk, root + 32, "DOCS       ", 0x10, 3, 0);

        WriteAscii(disk, ClusterOffset(start, 2), HelloText, HelloText.Length);
        var docs = ClusterOffset(start, 3);
        WriteDirectoryEntry(disk, docs, ".          ", 0x10, 3, 0);
        WriteDirectoryEntry(disk, docs + 32, "..         ", 0x10, 0, 0);
        WriteDirectoryEntry(disk, docs + 64, "README  TXT", 0x20, 4, ReadmeText.Length);
        WriteAscii(disk, ClusterOffset(start, 4), ReadmeText, ReadmeText.Length);
    }

    private static int ClusterOffset(int partitionStart, int cluster)
    {
        return partitionStart + (FirstDataSector + (cluster - 2)) * BytesPerSector;
    }

    private static void WriteDirectoryEntry(byte[] data, int offset, string name, byte attr, int cluster, int size)
    {
        WriteAscii(data, offset, name, 11);
        data[offset + 11] = attr;
        WriteU16Le(data, offset + 22, 0);
        WriteU16Le(data, offset + 24, 0);
        WriteU16Le(data, offset + 26, cluster);
        WriteU32Le(data, offset + 28, size);
    }

    private static void WriteAscii(byte[] data, int offset, string text, int length)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        Array.Copy(bytes, 0, data, offset, Math.Min(bytes.Length, length));
    }

    private static void WriteU16Le(byte[] data, int offset, int value)
    {
        data[offset] = (byte)value;
        data[offset + 1] = (byte)(value >> 8);
    }

    private static void WriteU32Le(byte[] data, int offset, int value)
    {
        data[offset] = (byte)value;
        data[offset + 1] = (byte)(value >> 8);
        data[offset + 2] = (byte)(value >> 16);
        data[offset + 3] = (byte)(value >> 24);
    }

    private static void WriteU32Le(Span<byte> data, int offset, int value)
    {
        data[offset] = (byte)value;
        data[offset + 1] = (byte)(value >> 8);
        data[offset + 2] = (byte)(value >> 16);
        data[offset + 3] = (byte)(value >> 24);
    }

    private static void WriteU32Be(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)(value >> 24);
        data[offset + 1] = (byte)(value >> 16);
        data[offset + 2] = (byte)(value >> 8);
        data[offset + 3] = (byte)value;
    }

    private static void WriteU32Be(List<byte> data, int offset, uint value)
    {
        data[offset] = (byte)(value >> 24);
        data[offset + 1] = (byte)(value >> 16);
        data[offset + 2] = (byte)(value >> 8);
        data[offset + 3] = (byte)value;
    }

    private static void WriteU64Be(byte[] data, int offset, ulong value)
    {
        data[offset] = (byte)(value >> 56);
        data[offset + 1] = (byte)(value >> 48);
        data[offset + 2] = (byte)(value >> 40);
        data[offset + 3] = (byte)(value >> 32);
        data[offset + 4] = (byte)(value >> 24);
        data[offset + 5] = (byte)(value >> 16);
        data[offset + 6] = (byte)(value >> 8);
        data[offset + 7] = (byte)value;
    }

    private static void WriteU64Be(List<byte> data, int offset, ulong value)
    {
        data[offset] = (byte)(value >> 56);
        data[offset + 1] = (byte)(value >> 48);
        data[offset + 2] = (byte)(value >> 40);
        data[offset + 3] = (byte)(value >> 32);
        data[offset + 4] = (byte)(value >> 24);
        data[offset + 5] = (byte)(value >> 16);
        data[offset + 6] = (byte)(value >> 8);
        data[offset + 7] = (byte)value;
    }
}
