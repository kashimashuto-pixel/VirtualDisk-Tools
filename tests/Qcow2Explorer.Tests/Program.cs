using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using Qcow2Explorer;
using Qcow2Explorer.Core;
using Qcow2Explorer.FileSystems;
using Qcow2Explorer.Mounting;
using Qcow2Explorer.Partitions;
using Qcow2Explorer.Previewing;
using DiscUtils.Streams;
using DiscXfsFileSystem = DiscUtils.Xfs.XfsFileSystem;
using VdiDisk = DiscUtils.Vdi.Disk;

if (args.Length > 0 && string.Equals(args[0], "--list-physical", StringComparison.OrdinalIgnoreCase))
{
    foreach (var disk in PhysicalDiskReader.Enumerate())
    {
        Console.WriteLine(disk);
    }

    return;
}

if (args.Length > 1 && string.Equals(args[0], "--probe-physical", StringComparison.OrdinalIgnoreCase))
{
    try
    {
        using var physical = new PhysicalDiskReader(args[1]);
        var sector = new byte[512];
        physical.ReadAt(0, sector, 0, sector.Length);
        Console.WriteLine($"{physical.Path}: {physical.Length:N0} bytes, sector {physical.LogicalSectorSize:N0} bytes");
        Console.WriteLine($"MBR signature: {sector[510]:X2} {sector[511]:X2}");
    }
    catch (UnauthorizedAccessException ex)
    {
        Console.WriteLine($"Access denied as expected without elevation: {ex.Message}");
    }

    return;
}

if (args.Length > 0)
{
    InspectImage(args[0], args.Any(a => string.Equals(a, "--copy-smoke", StringComparison.OrdinalIgnoreCase)));
    return;
}

RunGeneratedImageTests();

static void RunGeneratedImageTests()
{
    Assert(PhysicalDiskReader.IsPhysicalDiskPath(@"\\.\PhysicalDrive0"), "physical disk path detection");
    Assert(!PhysicalDiskReader.IsPhysicalDiskPath("PhysicalDrive0"), "physical disk path rejection");
    Test4KnGptParsing();
    TestLvmMetadataDiagnostics();
    TestGeneratedLvm2Image();
    TestGeneratedLzopExt4Image();
    TestFilePreviews();
    TestNavigationHistory();

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
    Assert((hello.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == (FileAttributes.Hidden | FileAttributes.System), "hidden/system attributes");

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

static void Test4KnGptParsing()
{
    const int sectorSize = 4096;
    var data = new byte[sectorSize * 32];
    data[510] = 0x55;
    data[511] = 0xaa;
    data[446 + 4] = 0xee;
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(446 + 8, 4), 1);
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(446 + 12, 4), 31);

    var header = data.AsSpan(sectorSize, 512);
    Encoding.ASCII.GetBytes("EFI PART").CopyTo(header);
    BinaryPrimitives.WriteUInt64LittleEndian(header[72..80], 2);
    BinaryPrimitives.WriteUInt32LittleEndian(header[80..84], 1);
    BinaryPrimitives.WriteUInt32LittleEndian(header[84..88], 128);

    var entry = data.AsSpan(sectorSize * 2, 128);
    Guid.Parse("0fc63daf-8483-4772-8e79-3d69d8477de4").TryWriteBytes(entry[..16]);
    BinaryPrimitives.WriteUInt64LittleEndian(entry[32..40], 10);
    BinaryPrimitives.WriteUInt64LittleEndian(entry[40..48], 20);
    Encoding.Unicode.GetBytes("Linux").CopyTo(entry[56..]);

    var partitions = PartitionTableReader.ReadPartitions(new MemorySectorReader(data, sectorSize));
    Assert(partitions.Count == 1, "4Kn GPT partition count");
    Assert(partitions[0].SectorSize == sectorSize, "4Kn GPT sector size");
    Assert(partitions[0].StartOffset == sectorSize * 10L, "4Kn GPT partition offset");
}

static void TestLvmMetadataDiagnostics()
{
    const string metadata = """
        contents = "Text Format Volume Group"
        version = 1
        vg_test {
            physical_volumes {
                pv0 {
                    id = "pv-id-0"
                }
                pv1 {
                    id = "pv-id-1"
                }
            }
            logical_volumes {
                root {
                    id = "lv-id"
                    segment_count = 2
                    segment1 {
                        type = "striped"
                        stripe_count = 2
                    }
                    segment2 {
                        type = "thin-pool"
                    }
                }
            }
        }
        """;

    var summary = LvmMetadataInspector.Summarize(7, metadata);
    Assert(summary.PartitionNumber == 7, "LVM diagnostic partition number");
    Assert(summary.PhysicalVolumeCount == 2, "LVM diagnostic PV count");
    Assert(summary.LogicalVolumeCount == 1, "LVM diagnostic LV count");
    Assert(summary.MaximumStripeCount == 2, "LVM diagnostic stripe count");
    Assert(summary.SegmentTypes.SequenceEqual(new[] { "striped", "thin-pool" }), "LVM diagnostic segment types");
}

static void TestGeneratedLvm2Image()
{
    var imagePath = Path.Combine(AppContext.BaseDirectory, "sample-lvm2.img");
    TestImageFactory.CreateLvm2Fat16Disk(imagePath);

    using var reader = DiskImageReaderFactory.Open(imagePath);
    var partitions = PartitionTableReader.ReadPartitions(reader).ToList();
    Assert(partitions.Count == 1 && partitions[0].TypeId == "0x8E", "generated LVM2 partition");

    var ownedReaders = new List<IDisposable>();
    try
    {
        var result = LogicalVolumeDiscoverer.Discover(reader, partitions, 2, ownedReaders);
        Assert(result.Volumes.Count == 1, string.Join(Environment.NewLine, result.Diagnostics.Select(item => item.Message)));
        Assert(!result.Diagnostics.Any(item => item.IsError), string.Join(Environment.NewLine, result.Diagnostics.Select(item => item.Message)));

        var volume = result.Volumes[0];
        volume.FileSystem = FileSystemDetector.Detect(reader, volume);
        Assert(volume.FileSystem == "FAT16", "FAT16 inside generated LVM2 LV");
        var fs = FileSystemDetector.TryOpen(reader, volume, out var error);
        Assert(fs is not null, error);
        var hello = fs!.ListDirectory(fs.Root).Single(node => node.Name == "HELLO.TXT");
        Assert(Encoding.ASCII.GetString(fs.ReadFile(hello, 0, (int)hello.Size)) == TestImageFactory.HelloText, "generated LVM2 HELLO.TXT content");
    }
    finally
    {
        foreach (var disposable in ownedReaders)
        {
            disposable.Dispose();
        }
    }
}

static void TestGeneratedLzopExt4Image()
{
    var imagePath = Path.Combine(AppContext.BaseDirectory, "sample-ext4.dd.lzo");
    TestImageFactory.CreateExt4LzopDisk(imagePath);

    var progressEvents = new List<DiskImageProgress>();
    using var reader = DiskImageReaderFactory.Open(
        imagePath,
        new CallbackProgress<DiskImageProgress>(progressEvents.Add));
    Assert(reader is LzopDiskImageReader, "dd.lzo reader factory");
    Assert(reader.FormatName.Contains("lzop", StringComparison.OrdinalIgnoreCase), "dd.lzo format name");
    Assert(
        progressEvents.Any(item => item.Message.Contains("索引作成", StringComparison.Ordinal)),
        "dd.lzo index progress");

    var partition = new PartitionInfo
    {
        Number = 1,
        Scheme = "WholeDisk",
        Name = "Whole disk",
        Type = "Unpartitioned",
        SectorCount = checked((ulong)(reader.Length / 512))
    };
    partition.FileSystem = FileSystemDetector.Detect(reader, partition);
    Assert(partition.FileSystem == "ext4", "ext4 detection inside dd.lzo");

    var fs = FileSystemDetector.TryOpen(reader, partition, out var error);
    Assert(fs is not null, error);
    var root = fs!.ListDirectory(fs.Root);
    var hello = root.Single(node => node.Name == "HELLO.TXT");
    var zeros = root.Single(node => node.Name == "ZEROS.BIN");
    Assert(
        Encoding.ASCII.GetString(fs.ReadFile(hello, 0, (int)hello.Size)) == TestImageFactory.Ext4HelloText,
        "ext4 text file inside dd.lzo");
    var zeroBytes = fs.ReadFile(zeros, 0, (int)zeros.Size);
    Assert(zeroBytes.Length == 4096 && zeroBytes.All(value => value == 0), "LZO-compressed ext4 file content");

    var copyDirectory = Path.Combine(AppContext.BaseDirectory, "lzo-ext4-copy-output");
    if (Directory.Exists(copyDirectory))
    {
        Directory.Delete(copyDirectory, recursive: true);
    }

    var copyResult = FileSystemExporter.CopyNodes(fs, [hello, zeros], copyDirectory);
    Assert(copyResult.FilesCopied == 2 && copyResult.Errors.Count == 0, "copy files from LZO-compressed ext4");
    Assert(
        File.ReadAllText(Path.Combine(copyDirectory, "HELLO.TXT"), Encoding.ASCII) == TestImageFactory.Ext4HelloText,
        "copied ext4 text file from dd.lzo");
    Assert(
        File.ReadAllBytes(Path.Combine(copyDirectory, "ZEROS.BIN")).All(value => value == 0),
        "copied LZO-compressed ext4 zero file");

    var crossBlock = new byte[8192];
    reader.ReadAt(60 * 1024, crossBlock, 0, crossBlock.Length);
    Assert(crossBlock.Length == 8192, "dd.lzo cross-block random read");
    Assert(
        progressEvents.Any(item => item.Message.Contains("ブロック展開", StringComparison.Ordinal)),
        "dd.lzo decompression progress");

    var damagedPath = Path.Combine(AppContext.BaseDirectory, "sample-ext4-damaged.dd.lzo");
    TestImageFactory.CreateExt4LzopDisk(damagedPath, corruptHeaderChecksum: true);
    try
    {
        using var _ = DiskImageReaderFactory.Open(damagedPath);
        Assert(false, "damaged dd.lzo header rejection");
    }
    catch (InvalidDataException ex)
    {
        Assert(ex.Message.Contains("チェックサム", StringComparison.Ordinal), "damaged dd.lzo diagnostic");
    }
}

static void TestFilePreviews()
{
    var text = FilePreviewReader.Read("notes.txt", Encoding.UTF8.GetBytes("日本語テキスト"));
    Assert(text.Text == "日本語テキスト", "UTF-8 text preview");
    var shiftJisText = FilePreviewReader.Read("unknown.dat", [0x93, 0xfa, 0x96, 0x7b, 0x8c, 0xea]);
    Assert(shiftJisText.Text == "日本語", "Shift-JIS text preview");
    var utf16Text = FilePreviewReader.Read("extensionless", Encoding.Unicode.GetBytes("plain text"));
    Assert(utf16Text.Text == "plain text", "UTF-16 content detection without extension");
    Assert(
        !FilePreviewReader.TryRead("program.bin", [0x4d, 0x5a, 0x00, 0x02, 0x10, 0xff, 0x00, 0x01], out _),
        "binary content preview rejection");
    Assert(FilePreviewReader.CanPreview("report.docx"), "docx preview detection");
    Assert(!FilePreviewReader.CanPreview("legacy.xls"), "legacy xls preview rejection");

    var docx = CreateZip(
        ("word/document.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
              <w:body>
                <w:p><w:r><w:t>Word本文</w:t></w:r></w:p>
                <w:tbl><w:tr><w:tc><w:p><w:r><w:t>表セル</w:t></w:r></w:p></w:tc></w:tr></w:tbl>
              </w:body>
            </w:document>
            """));
    var wordPreview = FilePreviewReader.Read("report.docx", docx);
    Assert(wordPreview.Text?.Contains("Word本文", StringComparison.Ordinal) == true, "docx paragraph preview");
    Assert(wordPreview.Text?.Contains("表セル", StringComparison.Ordinal) == true, "docx table preview");

    var xlsx = CreateZip(
        ("xl/workbook.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"
                      xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <sheets><sheet name="一覧" sheetId="1" r:id="rId1"/></sheets>
            </workbook>
            """),
        ("xl/_rels/workbook.xml.rels", """
            <?xml version="1.0" encoding="UTF-8"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Target="worksheets/sheet1.xml"
                            Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"/>
            </Relationships>
            """),
        ("xl/sharedStrings.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
              <si><t>見出し</t></si>
            </sst>
            """),
        ("xl/worksheets/sheet1.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
              <sheetData>
                <row r="1"><c r="A1" t="s"><v>0</v></c><c r="B1"><v>123</v></c></row>
                <row r="2"><c r="A2"><f>SUM(B1:B1)</f><v>123</v></c></row>
              </sheetData>
            </worksheet>
            """));
    var excelPreview = FilePreviewReader.Read("book.xlsx", xlsx);
    Assert(excelPreview.Sheets.Count == 1 && excelPreview.Sheets[0].Name == "一覧", "xlsx sheet preview");
    Assert(excelPreview.Sheets[0].Rows[0][0] == "見出し", "xlsx shared string preview");
    Assert(excelPreview.Sheets[0].Rows[1][0] == "=SUM(B1:B1)", "xlsx formula preview");
}

static void TestNavigationHistory()
{
    var history = new NavigationHistory<object>();
    var root = new object();
    var child = new object();
    var sibling = new object();
    history.Record(root);
    history.Record(child);
    Assert(history.CanGoBack && !history.CanGoForward, "navigation history initial state");
    Assert(ReferenceEquals(history.GoBack(), root), "navigation history back");
    Assert(history.CanGoForward, "navigation history forward enabled");
    Assert(ReferenceEquals(history.GoForward(), child), "navigation history forward");
    history.GoBack();
    history.Record(sibling);
    Assert(!history.CanGoForward && ReferenceEquals(history.Current, sibling), "navigation history forward truncation");
    history.Reset();
    Assert(!history.CanGoBack && !history.CanGoForward && history.Current is null, "navigation history reset");
}

static byte[] CreateZip(params (string Path, string Content)[] entries)
{
    using var output = new MemoryStream();
    using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
    {
        foreach (var (path, content) in entries)
        {
            var entry = archive.CreateEntry(path, CompressionLevel.Fastest);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(content);
        }
    }

    return output.ToArray();
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
    public const string Ext4HelloText = "Hello from LZO ext4 test\n";

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

    public static void CreateExt4LzopDisk(string path, bool corruptHeaderChecksum = false)
    {
        const int blockSize = 64 * 1024;
        const uint flags = 0x00001303;
        var disk = CreateMinimalExt4Disk();
        var output = new List<byte>();
        output.AddRange([0x89, 0x4c, 0x5a, 0x4f, 0x00, 0x0d, 0x0a, 0x1a, 0x0a]);

        var header = new List<byte>();
        AppendU16Be(header, 0x1030);
        AppendU16Be(header, 0x20a0);
        AppendU16Be(header, 0x0940);
        header.Add(1);
        header.Add(3);
        AppendU32Be(header, flags);
        AppendU32Be(header, 0x000081a4);
        AppendU32Be(header, 0);
        AppendU32Be(header, 0);
        var name = Encoding.UTF8.GetBytes("sample-ext4.dd");
        header.Add(checked((byte)name.Length));
        header.AddRange(name);
        output.AddRange(header);
        var headerChecksum = TestCrc32(header.ToArray());
        if (corruptHeaderChecksum)
        {
            headerChecksum ^= 1;
        }

        AppendU32Be(output, headerChecksum);

        for (var offset = 0; offset < disk.Length; offset += blockSize)
        {
            var length = Math.Min(blockSize, disk.Length - offset);
            var block = disk.AsSpan(offset, length).ToArray();
            var compressed = block.All(value => value == 0)
                ? EncodeLzoZeroBlock(length)
                : block;
            AppendU32Be(output, checked((uint)length));
            AppendU32Be(output, checked((uint)compressed.Length));
            AppendU32Be(output, TestAdler32(block));
            AppendU32Be(output, TestCrc32(block));
            if (compressed.Length < block.Length)
            {
                AppendU32Be(output, TestAdler32(compressed));
                AppendU32Be(output, TestCrc32(compressed));
            }

            output.AddRange(compressed);
        }

        AppendU32Be(output, 0);
        File.WriteAllBytes(path, output.ToArray());
    }

    public static void CreateLvm2Fat16Disk(string path)
    {
        const int lvmPartitionSectors = VirtualSize / BytesPerSector - PartitionStartLba;
        const int metadataAreaOffset = 4096;
        const int metadataAreaLength = 4096;
        const int metadataTextOffset = 512;
        const int dataAreaOffset = 1024 * 1024;
        const int extentSizeSectors = 8;
        const int logicalVolumeExtents = PartitionSectors * BytesPerSector / (extentSizeSectors * BytesPerSector);
        const string pvId = "abcdef-1234-5678-90ab-cdef-1234-567890";
        const string pvIdRaw = "abcdef1234567890abcdef1234567890";
        const string lvId = "123456-7890-abcd-efgh-ijkl-mnop-qrstuv";

        var disk = new byte[VirtualSize];
        var partitionStart = PartitionStartLba * BytesPerSector;
        var partitionLength = lvmPartitionSectors * BytesPerSector;

        disk[446 + 4] = 0x8e;
        WriteU32Le(disk, 446 + 8, PartitionStartLba);
        WriteU32Le(disk, 446 + 12, lvmPartitionSectors);
        disk[510] = 0x55;
        disk[511] = 0xaa;

        var metadata = $$"""
            contents = "Text Format Volume Group"
            version = 1
            description = "Qcow2Explorer generated LVM2 test"
            creation_host = "Qcow2Explorer"
            creation_time = 1
            vg_test {
                id = "fedcba-9876-5432-10fe-dcba-9876-543210"
                seqno = 1
                format = "lvm2"
                status = ["RESIZEABLE", "READ", "WRITE"]
                flags = []
                extent_size = {{extentSizeSectors}}
                max_lv = 0
                max_pv = 0
                metadata_copies = 0
                physical_volumes {
                    pv0 {
                        id = "{{pvId}}"
                        device = "/dev/test"
                        status = ["ALLOCATABLE"]
                        flags = []
                        dev_size = {{lvmPartitionSectors}}
                        pe_start = {{dataAreaOffset / BytesPerSector}}
                        pe_count = {{(partitionLength - dataAreaOffset) / (extentSizeSectors * BytesPerSector)}}
                    }
                }
                logical_volumes {
                    root {
                        id = "{{lvId}}"
                        status = ["READ", "WRITE", "VISIBLE"]
                        flags = []
                        creation_host = "Qcow2Explorer"
                        creation_time = 1
                        segment_count = 1
                        segment1 {
                            start_extent = 0
                            extent_count = {{logicalVolumeExtents}}
                            type = "striped"
                            stripe_count = 1
                            stripes = [
                                "pv0", 0
                            ]
                        }
                    }
                }
            }
            """;
        var metadataBytes = Encoding.ASCII.GetBytes(metadata);
        if (metadataBytes.Length >= metadataAreaLength - metadataTextOffset)
        {
            throw new InvalidOperationException("Generated LVM2 metadata exceeds its test area.");
        }

        var metadataArea = partitionStart + metadataAreaOffset;
        WriteAscii(disk, metadataArea + 4, " LVM2 x[5A%r0N*>", 16);
        WriteU32Le(disk, metadataArea + 20, 1);
        WriteU64Le(disk, metadataArea + 24, metadataAreaOffset);
        WriteU64Le(disk, metadataArea + 32, metadataAreaLength);
        WriteU64Le(disk, metadataArea + 40, metadataTextOffset);
        WriteU64Le(disk, metadataArea + 48, metadataBytes.Length);
        WriteU32Le(disk, metadataArea + 56, CalculateLvmCrc(metadataBytes, 0, metadataBytes.Length));
        Array.Copy(metadataBytes, 0, disk, metadataArea + metadataTextOffset, metadataBytes.Length);
        WriteU32Le(disk, metadataArea, CalculateLvmCrc(disk, metadataArea + 4, 508));

        var label = partitionStart + BytesPerSector;
        WriteAscii(disk, label, "LABELONE", 8);
        WriteU64Le(disk, label + 8, 1);
        WriteU32Le(disk, label + 20, 32);
        WriteAscii(disk, label + 24, "LVM2 001", 8);

        var pvHeader = label + 32;
        WriteAscii(disk, pvHeader, pvIdRaw, 32);
        WriteU64Le(disk, pvHeader + 32, partitionLength);
        WriteU64Le(disk, pvHeader + 40, dataAreaOffset);
        WriteU64Le(disk, pvHeader + 48, partitionLength - dataAreaOffset);
        WriteU64Le(disk, pvHeader + 72, metadataAreaOffset);
        WriteU64Le(disk, pvHeader + 80, metadataAreaLength);
        WriteU32Le(disk, label + 16, CalculateLvmCrc(disk, label + 20, BytesPerSector - 20));

        var fatDisk = CreateVirtualDisk();
        Array.Copy(
            fatDisk,
            PartitionStartLba * BytesPerSector,
            disk,
            partitionStart + dataAreaOffset,
            PartitionSectors * BytesPerSector);
        File.WriteAllBytes(path, disk);
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

    private static byte[] CreateMinimalExt4Disk()
    {
        const int size = 2 * 1024 * 1024;
        const int blockSize = 1024;
        const int inodeSize = 256;
        const int inodeTableBlock = 5;
        const int rootDirectoryBlock = 20;
        const int textFileBlock = 21;
        const int zeroFileBlock = 128;
        var disk = new byte[size];

        var super = blockSize;
        WriteU32Le(disk, super + 0x00, 32);
        WriteU32Le(disk, super + 0x04, size / blockSize);
        WriteU32Le(disk, super + 0x14, 1);
        WriteU32Le(disk, super + 0x18, 0);
        WriteU32Le(disk, super + 0x20, 8192);
        WriteU32Le(disk, super + 0x28, 32);
        WriteU16Le(disk, super + 0x38, 0xef53);
        WriteU16Le(disk, super + 0x58, inodeSize);
        WriteU32Le(disk, super + 0x60, 0x40);
        WriteU16Le(disk, super + 0xfe, 32);

        var groupDescriptor = blockSize * 2;
        WriteU32Le(disk, groupDescriptor + 8, inodeTableBlock);

        var rootInode = inodeTableBlock * blockSize + inodeSize;
        WriteExt4Inode(disk, rootInode, 0x41ed, blockSize, rootDirectoryBlock, 1);
        var textInode = inodeTableBlock * blockSize + inodeSize * 11;
        WriteExt4Inode(disk, textInode, 0x81a4, Ext4HelloText.Length, textFileBlock, 1);
        var zeroInode = inodeTableBlock * blockSize + inodeSize * 12;
        WriteExt4Inode(disk, zeroInode, 0x81a4, 4096, zeroFileBlock, 4);

        var root = rootDirectoryBlock * blockSize;
        WriteExt4DirectoryEntry(disk, root, 2, 12, ".", 2);
        WriteExt4DirectoryEntry(disk, root + 12, 2, 12, "..", 2);
        WriteExt4DirectoryEntry(disk, root + 24, 12, 20, "HELLO.TXT", 1);
        WriteExt4DirectoryEntry(disk, root + 44, 13, blockSize - 44, "ZEROS.BIN", 1);
        WriteAscii(disk, textFileBlock * blockSize, Ext4HelloText, Ext4HelloText.Length);
        return disk;
    }

    private static void WriteExt4Inode(
        byte[] disk,
        int offset,
        int mode,
        int size,
        int physicalBlock,
        int blockCount)
    {
        WriteU16Le(disk, offset, mode);
        WriteU32Le(disk, offset + 4, size);
        WriteU32Le(disk, offset + 32, 0x00080000);
        WriteU16Le(disk, offset + 40, 0xf30a);
        WriteU16Le(disk, offset + 42, 1);
        WriteU16Le(disk, offset + 44, 4);
        WriteU16Le(disk, offset + 46, 0);
        WriteU32Le(disk, offset + 52, 0);
        WriteU16Le(disk, offset + 56, blockCount);
        WriteU16Le(disk, offset + 58, 0);
        WriteU32Le(disk, offset + 60, physicalBlock);
    }

    private static void WriteExt4DirectoryEntry(
        byte[] disk,
        int offset,
        int inode,
        int recordLength,
        string name,
        byte fileType)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        WriteU32Le(disk, offset, inode);
        WriteU16Le(disk, offset + 4, recordLength);
        disk[offset + 6] = checked((byte)nameBytes.Length);
        disk[offset + 7] = fileType;
        Array.Copy(nameBytes, 0, disk, offset + 8, nameBytes.Length);
    }

    private static byte[] EncodeLzoZeroBlock(int length)
    {
        if (length < 37)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        var compressed = new List<byte> { 21, 0, 0, 0, 0, 32 };
        var extended = length - 4 - 33;
        while (extended > 255)
        {
            compressed.Add(0);
            extended -= 255;
        }

        if (extended == 0)
        {
            compressed.Add(255);
        }
        else
        {
            compressed.Add(checked((byte)extended));
        }

        compressed.Add(0);
        compressed.Add(0);
        compressed.Add(17);
        compressed.Add(0);
        compressed.Add(0);
        return compressed.ToArray();
    }

    private static uint TestAdler32(ReadOnlySpan<byte> data)
    {
        const uint prime = 65521;
        uint first = 1;
        uint second = 0;
        foreach (var value in data)
        {
            first = (first + value) % prime;
            second = (second + first) % prime;
        }

        return (second << 16) | first;
    }

    private static uint TestCrc32(ReadOnlySpan<byte> data)
    {
        var crc = uint.MaxValue;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? 0xedb88320u ^ (crc >> 1) : crc >> 1;
            }
        }

        return ~crc;
    }

    private static void AppendU16Be(List<byte> data, ushort value)
    {
        data.Add((byte)(value >> 8));
        data.Add((byte)value);
    }

    private static void AppendU32Be(List<byte> data, uint value)
    {
        data.Add((byte)(value >> 24));
        data.Add((byte)(value >> 16));
        data.Add((byte)(value >> 8));
        data.Add((byte)value);
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
        WriteDirectoryEntry(disk, root, "HELLO   TXT", 0x26, 2, HelloText.Length);
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

    private static void WriteU32Le(byte[] data, int offset, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset, sizeof(uint)), value);
    }

    private static void WriteU64Le(byte[] data, int offset, long value)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(offset, sizeof(ulong)), checked((ulong)value));
    }

    private static uint CalculateLvmCrc(byte[] data, int offset, int length)
    {
        ReadOnlySpan<uint> table =
        [
            0x00000000, 0x1db71064, 0x3b6e20c8, 0x26d930ac,
            0x76dc4190, 0x6b6b51f4, 0x4db26158, 0x5005713c,
            0xedb88320, 0xf00f9344, 0xd6d6a3e8, 0xcb61b38c,
            0x9b64c2b0, 0x86d3d2d4, 0xa00ae278, 0xbdbdf21c
        ];
        var crc = 0xf597a6cfu;
        for (var i = 0; i < length; i++)
        {
            crc ^= data[offset + i];
            crc = table[(int)(crc & 0xf)] ^ (crc >> 4);
            crc = table[(int)(crc & 0xf)] ^ (crc >> 4);
        }

        return crc;
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

internal sealed class MemorySectorReader : IBlockReader, ILogicalSectorReader
{
    private readonly byte[] _data;

    public MemorySectorReader(byte[] data, int logicalSectorSize)
    {
        _data = data;
        LogicalSectorSize = checked((uint)logicalSectorSize);
    }

    public long Length => _data.LongLength;
    public uint LogicalSectorSize { get; }

    public void ReadAt(long offset, byte[] buffer, int bufferOffset, int count)
    {
        Array.Clear(buffer, bufferOffset, count);
        if (offset < 0 || offset >= Length || count <= 0)
        {
            return;
        }

        var available = checked((int)Math.Min(count, Length - offset));
        Array.Copy(_data, offset, buffer, bufferOffset, available);
    }
}

internal sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
{
    public void Report(T value)
    {
        callback(value);
    }
}
