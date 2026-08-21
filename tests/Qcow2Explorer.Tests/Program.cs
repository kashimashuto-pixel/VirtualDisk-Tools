using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Formats.Tar;
using System.Security.Cryptography;
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
using VmdkDisk = DiscUtils.Vmdk.Disk;
using VmdkDiskCreateType = DiscUtils.Vmdk.DiskCreateType;

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

if (args.Length > 0 && string.Equals(args[0], "--real-image-regression", StringComparison.OrdinalIgnoreCase))
{
    if (args.Length != 2)
    {
        throw new ArgumentException("Usage: --real-image-regression <manifest.json>");
    }

    var summary = RealImageRegressionRunner.Run(args[1]);
    Console.WriteLine($"Real-image regression passed: {summary.CaseCount} case(s), {summary.Elapsed.TotalSeconds:0.00} sec");
    return;
}

if (args.Length > 0)
{
    int? vmaDeviceId = null;
    var vmaDeviceArgument = args.FirstOrDefault(argument =>
        argument.StartsWith("--vma-device=", StringComparison.OrdinalIgnoreCase));
    if (vmaDeviceArgument is not null
        && int.TryParse(vmaDeviceArgument.AsSpan(vmaDeviceArgument.IndexOf('=') + 1), out var parsedDeviceId))
    {
        vmaDeviceId = parsedDeviceId;
    }

    InspectImage(
        args[0],
        args.Any(a => string.Equals(a, "--copy-smoke", StringComparison.OrdinalIgnoreCase)),
        vmaDeviceId);
    return;
}

RunGeneratedImageTests();

static void RunGeneratedImageTests()
{
    Assert(PhysicalDiskReader.IsPhysicalDiskPath(@"\\.\PhysicalDrive0"), "physical disk path detection");
    Assert(!PhysicalDiskReader.IsPhysicalDiskPath("PhysicalDrive0"), "physical disk path rejection");
    TestBitLockerRecoveryPasswordUnlock();
    TestXfsTimestampDecoding();
    Test4KnGptParsing();
    TestLvmMetadataDiagnostics();
    TestGeneratedLvm2Image();
    TestGeneratedLzopExt4Image();
    TestRealImageRegressionRunner();
    TestGeneratedVmaLzopImage();
    TestGeneratedUefiVariableStore();
    TestGeneratedSwtpmStateStore();
    TestFilePreviews();
    TestNavigationHistory();
    TestVirtualPaths();
    TestNtfsMftMirrorFallback();

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

    var copyProgress = new List<CopyProgress>();
    var copyResult = FileSystemExporter.CopyNodes(
        fs,
        new[] { hello, docs },
        copyDirectory,
        new CallbackProgress<CopyProgress>(copyProgress.Add));
    Assert(copyResult.FilesCopied == 2, "copied file count");
    Assert(copyResult.Errors.Count == 0, "copy errors");
    Assert(copyProgress.Count > 0, "copy progress events");
    Assert(copyProgress.All(item => item.TotalBytes == hello.Size + readme.Size), "copy progress total bytes");
    Assert(copyProgress[^1].BytesCopied == hello.Size + readme.Size, "copy progress completed bytes");
    Assert(
        copyProgress.Zip(copyProgress.Skip(1), (left, right) => left.BytesCopied <= right.BytesCopied).All(value => value),
        "copy progress bytes are cumulative");
    Assert(copyProgress.All(item => item.Elapsed >= TimeSpan.Zero), "copy progress elapsed time");
    Assert(!File.Exists(Path.Combine(copyDirectory, "VirtualDiskExplorer.sha256")), "SHA-256 manifest is not created");
    Assert(File.ReadAllText(Path.Combine(copyDirectory, "HELLO.TXT"), Encoding.ASCII) == TestImageFactory.HelloText, "copied HELLO.TXT");
    Assert(File.ReadAllText(Path.Combine(copyDirectory, "DOCS", "README.TXT"), Encoding.ASCII) == TestImageFactory.ReadmeText, "copied README.TXT");
    var canceledCopyDirectory = Path.Combine(copyDirectory, "canceled-copy");
    try
    {
        FileSystemExporter.CopyNodes(
            fs,
            new[] { hello },
            canceledCopyDirectory,
            cancellationToken: new CancellationToken(canceled: true));
        Assert(false, "canceled copy throws");
    }
    catch (OperationCanceledException)
    {
        Assert(!File.Exists(Path.Combine(canceledCopyDirectory, "HELLO.TXT")), "canceled copy does not create a file");
    }

    var interruptibleFileSystem = new InterruptibleCopyFileSystem();
    var interruptedCopyDirectory = Path.Combine(copyDirectory, "interrupted-copy");
    using var copyCancellation = new CancellationTokenSource();
    try
    {
        FileSystemExporter.CopyNode(
            interruptibleFileSystem,
            interruptibleFileSystem.LargeFile,
            interruptedCopyDirectory,
            new CallbackProgress<CopyProgress>(_ => copyCancellation.Cancel()),
            copyCancellation.Token);
        Assert(false, "interrupted copy throws");
    }
    catch (OperationCanceledException)
    {
        Assert(
            !File.Exists(Path.Combine(interruptedCopyDirectory, interruptibleFileSystem.LargeFile.Name)),
            "interrupted copy removes partial file");
    }

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
    using (var cancellation = new CancellationTokenSource())
    {
        cancellation.Cancel();
        try
        {
            _ = PartitionTableReader.ReadPartitions(rawReader, cancellation.Token);
            Assert(false, "partition analysis cancellation throws");
        }
        catch (OperationCanceledException)
        {
        }

        var rawPartition = PartitionTableReader.ReadPartitions(rawReader).Single();
        try
        {
            _ = FileSystemDetector.Detect(rawReader, rawPartition, cancellation.Token);
            Assert(false, "file system detection cancellation throws");
        }
        catch (OperationCanceledException)
        {
        }
    }

    Console.WriteLine(rawImagePath);

    var vdiImagePath = Path.Combine(AppContext.BaseDirectory, "sample-fat16.vdi");
    TestImageFactory.CreateFat16Vdi(vdiImagePath);
    using (var vdiReader = DiskImageReaderFactory.Open(vdiImagePath))
    {
        Assert(vdiReader.FormatName == "VDI", "VDI image factory");
        AssertFat16Readable(vdiReader, "VDI");
    }

    Console.WriteLine(vdiImagePath);

    var vmdkImagePath = Path.Combine(AppContext.BaseDirectory, "sample-fat16.vmdk");
    CreateVmdk(vmdkImagePath, rawImagePath);
    var ovaImagePath = Path.Combine(AppContext.BaseDirectory, "sample-fat16.ova");
    CreateOva(ovaImagePath, vmdkImagePath);
    using (var ovaReader = DiskImageReaderFactory.Open(ovaImagePath))
    {
        Assert(ovaReader is OvaDiskImageReader, "OVA image factory");
        Assert(ovaReader.FormatName.StartsWith("OVA", StringComparison.OrdinalIgnoreCase), "OVA format name");
        AssertFat16Readable(ovaReader, "OVA");
    }

    var ovaCancellationRoot = Path.Combine(AppContext.BaseDirectory, "ova-cancellation-temporary-root");
    if (Directory.Exists(ovaCancellationRoot))
    {
        Directory.Delete(ovaCancellationRoot, recursive: true);
    }

    using (var cancellation = new CancellationTokenSource())
    {
        try
        {
            using var _ = OvaDiskImageReader.Open(
                ovaImagePath,
                new CallbackProgress<DiskImageProgress>(item =>
                {
                    if (item.Message.Contains("disk.vmdk", StringComparison.Ordinal))
                    {
                        cancellation.Cancel();
                    }
                }),
                cancellation.Token,
                ovaCancellationRoot);
            Assert(false, "OVA extraction cancellation throws");
        }
        catch (OperationCanceledException)
        {
        }
    }

    Assert(Directory.Exists(ovaCancellationRoot), "OVA cancellation temporary root is preserved");
    Assert(!Directory.EnumerateFileSystemEntries(ovaCancellationRoot).Any(), "OVA cancellation temporary files cleanup");
    Directory.Delete(ovaCancellationRoot);

    Console.WriteLine(ovaImagePath);

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

static void TestRealImageRegressionRunner()
{
    var imagePath = Path.Combine(AppContext.BaseDirectory, "sample-regression-fat16.qcow2");
    TestImageFactory.CreateFat16Qcow2(imagePath);
    var helloSha256 = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(TestImageFactory.HelloText)));
    var manifestPath = Path.Combine(AppContext.BaseDirectory, "real-image-regression.generated.json");
    var manifest = $$"""
        {
          "version": 1,
          "cases": [
            {
              "name": "generated FAT16 runner smoke",
              "path": "{{Path.GetFileName(imagePath)}}",
              "sha256": "{{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(imagePath)))}}",
              "expectedFormatContains": "qcow2",
              "expectedDiskLength": {{TestImageFactory.VirtualSize}},
              "expectedPartitionCount": 1,
              "partitions": [
                {
                  "number": 1,
                  "expectedFileSystem": "FAT16",
                  "files": [
                    {
                      "path": "/HELLO.TXT",
                      "expectedDirectory": false,
                      "expectedLength": {{Encoding.ASCII.GetByteCount(TestImageFactory.HelloText)}},
                      "sha256": "{{helloSha256}}"
                    }
                  ]
                }
              ]
            }
          ]
        }
        """;
    File.WriteAllText(manifestPath, manifest, new UTF8Encoding(false));
    var summary = RealImageRegressionRunner.Run(manifestPath);
    Assert(summary.CaseCount == 1, "real-image regression runner case count");
}

static void TestBitLockerRecoveryPasswordUnlock()
{
    const string recoveryPassword = "471207-278498-422125-177177-561902-537405-468006-693451";
    const string expectedIntermediateKey = "55A7E662E795EB3E8AC7D7BE32A641F6";
    const string expectedStretchedKey = "5E39D3E56A4E477FB569BB49B3E8C3B2C9D864EE5D75BD658B8E2720E49000D4";

    Assert(
        BitLockerRecoveryPassword.TryDecode(recoveryPassword, out var intermediateKey, out var decodeError),
        decodeError);
    Assert(Convert.ToHexString(intermediateKey) == expectedIntermediateKey, "BitLocker recovery password decoding");
    Assert(
        BitLockerRecoveryPassword.TryDecode(recoveryPassword.Replace('-', ' '), out var spacedKey, out _)
        && spacedKey.SequenceEqual(intermediateKey),
        "BitLocker recovery password accepts spaces");
    CryptographicOperations.ZeroMemory(spacedKey);

    Assert(
        !BitLockerRecoveryPassword.TryDecode(
            "471208-278498-422125-177177-561902-537405-468006-693451",
            out _,
            out var checksumError)
        && checksumError.Contains("第1ブロック", StringComparison.Ordinal),
        "BitLocker recovery password checksum rejection");
    Assert(
        !BitLockerRecoveryPassword.TryDecode(
            "999999-278498-422125-177177-561902-537405-468006-693451",
            out _,
            out var rangeError)
        && rangeError.Contains("720885", StringComparison.Ordinal),
        "BitLocker recovery password range rejection");
    Assert(
        !BitLockerRecoveryPassword.TryDecode("471207-invalid", out _, out _),
        "BitLocker recovery password character rejection");

    var salt = Enumerable.Range(0, BitLockerRecoveryPassword.SaltSize).Select(value => (byte)value).ToArray();
    var stretchedKey = BitLockerRecoveryPassword.DeriveStretchedKey(intermediateKey, salt);
    Assert(Convert.ToHexString(stretchedKey) == expectedStretchedKey, "BitLocker stretch-key derivation");
    try
    {
        _ = BitLockerRecoveryPassword.DeriveStretchedKey(
            intermediateKey,
            salt,
            new CancellationToken(canceled: true));
        Assert(false, "BitLocker stretch-key cancellation throws");
    }
    catch (OperationCanceledException)
    {
    }

    var clearKey = Enumerable.Range(0x20, 32).Select(value => (byte)value).ToArray();
    var vmk = Enumerable.Range(0x60, 32).Select(value => (byte)value).ToArray();
    var fvek = Enumerable.Range(0xa0, 32).Select(value => (byte)value).ToArray();
    var plaintext = Enumerable.Range(0, 1024).Select(value => (byte)(value * 37 + 11)).ToArray();
    var encryptedVolume = EncryptBitLockerXts(plaintext, fvek);
    var encryptedFvek = new BitLockerMetadataEntry
    {
        EntryType = 0x0003,
        ValueType = 0x0005,
        Data = EncryptBitLockerAesCcm(vmk, CreateBitLockerKeyData(0x8004, fvek), nonceSeed: 0x31)
    };
    var encryptedRecoveryVmk = new BitLockerMetadataEntry
    {
        EntryType = 0x0003,
        ValueType = 0x0005,
        Data = EncryptBitLockerAesCcm(stretchedKey, CreateBitLockerKeyData(0x2000, vmk), nonceSeed: 0x11)
    };
    var decoyEncryptedVmkData = encryptedRecoveryVmk.Data.ToArray();
    decoyEncryptedVmkData[^1] ^= 0xff;
    var decoyEncryptedVmk = new BitLockerMetadataEntry
    {
        EntryType = 0x0012,
        ValueType = 0x0005,
        Data = decoyEncryptedVmkData
    };
    var stretchData = new byte[4 + BitLockerRecoveryPassword.SaltSize];
    BinaryPrimitives.WriteUInt32LittleEndian(stretchData, 0x1000);
    salt.CopyTo(stretchData, 4);
    var recoveryProtector = new BitLockerKeyProtector
    {
        Identifier = Guid.Parse("8c7486c7-4d57-4f27-9548-025be1c2088f"),
        ProtectionType = BitLockerProtectionType.RecoveryPassword,
        RawProtectionType = 0x0800,
        Properties =
        [
            new BitLockerMetadataEntry
            {
                EntryType = 0x0003,
                ValueType = 0x0003,
                Data = stretchData,
                Children = [decoyEncryptedVmk]
            },
            encryptedRecoveryVmk
        ]
    };
    var recoveryMetadata = new BitLockerMetadata
    {
        EncryptedVolumeSize = encryptedVolume.Length,
        EncryptionMethod = 0x8004,
        KeyProtectors = [recoveryProtector],
        Entries = [encryptedFvek]
    };
    var metadataImage = CreateBitLockerMetadataTestImage(
        recoveryProtector.Identifier,
        stretchData,
        encryptedRecoveryVmk.Data,
        encryptedFvek.Data);
    Assert(
        BitLockerMetadataReader.TryRead(
            new MemorySectorReader(metadataImage, 512),
            out var parsedMetadata,
            out var metadataError),
        metadataError);
    Assert(
        parsedMetadata is { HasRecoveryPasswordProtector: true }
        && parsedMetadata.EncryptionMethod == 0x8004
        && parsedMetadata.KeyProtectors.Single().Properties.Any(entry => entry.ValueType == 0x0005)
        && parsedMetadata.KeyProtectors.Single().Properties.Single(entry => entry.ValueType == 0x0003)
            .Children.Single().ValueType == 0x0005,
        "BitLocker recovery protector metadata parsing and encryption method normalization");

    var encryptedReader = new MemorySectorReader(encryptedVolume, 512);
    Assert(
        BitLockerUnlock.TryCreateReaderWithRecoveryKey(
            encryptedReader,
            recoveryMetadata,
            intermediateKey,
            out var recoveryReader,
            out var recoveryError),
        recoveryError);
    Assert(recoveryReader is not null, "BitLocker recovery reader created");
    var recovered = new byte[plaintext.Length];
    recoveryReader!.ReadAt(0, recovered, 0, recovered.Length);
    Assert(recovered.SequenceEqual(plaintext), "BitLocker recovery password AES-CCM and XTS unlock");

    ((IDisposable)recoveryReader).Dispose();

    var nestedRecoveryMetadata = new BitLockerMetadata
    {
        EncryptedVolumeSize = encryptedVolume.Length,
        EncryptionMethod = 0x8004,
        KeyProtectors =
        [
            new BitLockerKeyProtector
            {
                Identifier = recoveryProtector.Identifier,
                ProtectionType = BitLockerProtectionType.RecoveryPassword,
                RawProtectionType = 0x0800,
                Properties =
                [
                    new BitLockerMetadataEntry
                    {
                        EntryType = 0x0003,
                        ValueType = 0x0003,
                        Data = stretchData,
                        Children = [encryptedRecoveryVmk]
                    }
                ]
            }
        ],
        Entries = [encryptedFvek]
    };
    Assert(
        BitLockerUnlock.TryCreateReaderWithRecoveryKey(
            encryptedReader,
            nestedRecoveryMetadata,
            intermediateKey,
            out var nestedRecoveryReader,
            out var nestedRecoveryError),
        nestedRecoveryError);
    Assert(nestedRecoveryReader is not null, "nested BitLocker recovery VMK fallback");
    ((IDisposable)nestedRecoveryReader!).Dispose();

    try
    {
        recoveryReader.ReadAt(0, new byte[1], 0, 1);
        Assert(false, "disposed BitLocker reader rejects reads");
    }
    catch (ObjectDisposedException)
    {
    }

    const string wrongRecoveryPassword = "000000-000000-000000-000000-000000-000000-000000-000000";
    Assert(BitLockerRecoveryPassword.TryDecode(wrongRecoveryPassword, out var wrongKey, out _), "valid alternate recovery password");
    Assert(
        !BitLockerUnlock.TryCreateReaderWithRecoveryKey(
            encryptedReader,
            recoveryMetadata,
            wrongKey,
            out _,
            out var wrongPasswordError),
        "wrong BitLocker recovery password rejection");
    Assert(
        !wrongPasswordError.Contains(wrongRecoveryPassword, StringComparison.Ordinal)
        && !wrongPasswordError.Contains(Convert.ToHexString(intermediateKey), StringComparison.OrdinalIgnoreCase)
        && !wrongPasswordError.Contains(Convert.ToHexString(fvek), StringComparison.OrdinalIgnoreCase),
        "BitLocker errors do not expose passwords or keys");

    var clearProtector = new BitLockerKeyProtector
    {
        Identifier = Guid.Parse("bc79bc9f-ff72-492b-ad3f-2f3e748a8e31"),
        ProtectionType = BitLockerProtectionType.ClearKey,
        RawProtectionType = 0x0000,
        Properties =
        [
            new BitLockerMetadataEntry
            {
                EntryType = 0x0003,
                ValueType = 0x0001,
                Data = CreateBitLockerKeyData(0x2000, clearKey)
            },
            new BitLockerMetadataEntry
            {
                EntryType = 0x0003,
                ValueType = 0x0005,
                Data = EncryptBitLockerAesCcm(clearKey, CreateBitLockerKeyData(0x2000, vmk), nonceSeed: 0x21)
            }
        ]
    };
    var clearMetadata = new BitLockerMetadata
    {
        EncryptedVolumeSize = encryptedVolume.Length,
        EncryptionMethod = 0x8004,
        KeyProtectors = [clearProtector],
        Entries = [encryptedFvek]
    };
    Assert(
        BitLockerUnlock.TryCreateReaderWithClearKey(
            encryptedReader,
            clearMetadata,
            out var clearReader,
            out var clearError),
        clearError);
    var clearRecovered = new byte[plaintext.Length];
    clearReader!.ReadAt(0, clearRecovered, 0, clearRecovered.Length);
    Assert(clearRecovered.SequenceEqual(plaintext), "BitLocker clear-key regression");
    ((IDisposable)clearReader).Dispose();

    CryptographicOperations.ZeroMemory(intermediateKey);
    CryptographicOperations.ZeroMemory(wrongKey);
    CryptographicOperations.ZeroMemory(stretchedKey);
    CryptographicOperations.ZeroMemory(decoyEncryptedVmkData);
    CryptographicOperations.ZeroMemory(clearKey);
    CryptographicOperations.ZeroMemory(vmk);
    CryptographicOperations.ZeroMemory(fvek);
    CryptographicOperations.ZeroMemory(recovered);
    CryptographicOperations.ZeroMemory(clearRecovered);
}

static byte[] CreateBitLockerKeyData(uint method, byte[] key)
{
    var data = new byte[4 + key.Length];
    BinaryPrimitives.WriteUInt32LittleEndian(data, method);
    key.CopyTo(data, 4);
    return data;
}

static byte[] CreateBitLockerMetadataTestImage(
    Guid protectorIdentifier,
    byte[] stretchData,
    byte[] encryptedVmkData,
    byte[] encryptedFvekData)
{
    const int metadataBlockOffset = 4096;
    var encryptedVmkEntry = CreateBitLockerMetadataEntry(0x0003, 0x0005, encryptedVmkData);
    var decoyEncryptedVmkData = encryptedVmkData.ToArray();
    decoyEncryptedVmkData[^1] ^= 0xff;
    var decoyEncryptedVmkEntry = CreateBitLockerMetadataEntry(0x0012, 0x0005, decoyEncryptedVmkData);
    var stretchPayload = new byte[stretchData.Length + decoyEncryptedVmkEntry.Length];
    stretchData.CopyTo(stretchPayload, 0);
    decoyEncryptedVmkEntry.CopyTo(stretchPayload, stretchData.Length);
    var stretchEntry = CreateBitLockerMetadataEntry(0x0003, 0x0003, stretchPayload);
    var protectorPayload = new byte[28 + stretchEntry.Length + encryptedVmkEntry.Length];
    protectorIdentifier.ToByteArray().CopyTo(protectorPayload, 0);
    BinaryPrimitives.WriteUInt16LittleEndian(protectorPayload.AsSpan(26), 0x0800);
    stretchEntry.CopyTo(protectorPayload, 28);
    encryptedVmkEntry.CopyTo(protectorPayload, 28 + stretchEntry.Length);
    var protectorEntry = CreateBitLockerMetadataEntry(0x0002, 0x0008, protectorPayload);
    var fvekEntry = CreateBitLockerMetadataEntry(0x0003, 0x0005, encryptedFvekData);

    const int metadataHeaderSize = 48;
    var metadataSize = metadataHeaderSize + protectorEntry.Length + fvekEntry.Length;
    var image = new byte[metadataBlockOffset + 64 + metadataSize + 512];
    Encoding.ASCII.GetBytes("-FVE-FS-").CopyTo(image, 3);
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(176), metadataBlockOffset);

    Encoding.ASCII.GetBytes("-FVE-FS-").CopyTo(image, metadataBlockOffset);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(metadataBlockOffset + 10), 2);
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(metadataBlockOffset + 16), checked((ulong)image.Length));
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(metadataBlockOffset + 28), 1);
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(metadataBlockOffset + 32), metadataBlockOffset);

    var metadataOffset = metadataBlockOffset + 64;
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(metadataOffset), checked((uint)metadataSize));
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(metadataOffset + 4), 2);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(metadataOffset + 8), metadataHeaderSize);
    Guid.Parse("511c4978-8ba7-4da5-a4f8-63d8f37460e2").ToByteArray().CopyTo(image, metadataOffset + 16);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(metadataOffset + 36), 0x80048004);
    protectorEntry.CopyTo(image, metadataOffset + metadataHeaderSize);
    fvekEntry.CopyTo(image, metadataOffset + metadataHeaderSize + protectorEntry.Length);
    return image;
}

static byte[] CreateBitLockerMetadataEntry(ushort entryType, ushort valueType, byte[] data)
{
    var result = new byte[8 + data.Length];
    BinaryPrimitives.WriteUInt16LittleEndian(result, checked((ushort)result.Length));
    BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(2), entryType);
    BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(4), valueType);
    BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(6), 1);
    data.CopyTo(result, 8);
    return result;
}

static byte[] EncryptBitLockerAesCcm(byte[] key, byte[] plaintext, byte nonceSeed)
{
    var result = new byte[12 + 16 + plaintext.Length];
    for (var index = 0; index < 12; index++)
    {
        result[index] = checked((byte)(nonceSeed + index));
    }

#pragma warning disable SYSLIB0053
    using var aesCcm = new AesCcm(key);
#pragma warning restore SYSLIB0053
    aesCcm.Encrypt(result.AsSpan(0, 12), plaintext, result.AsSpan(28), result.AsSpan(12, 16));
    return result;
}

static byte[] EncryptBitLockerXts(byte[] plaintext, byte[] fvek)
{
    Assert(plaintext.Length % 512 == 0, "BitLocker XTS test data sector alignment");
    Assert(fvek.Length == 32, "BitLocker XTS-128 test FVEK length");
    using var dataAes = Aes.Create();
    using var tweakAes = Aes.Create();
    dataAes.Mode = tweakAes.Mode = CipherMode.ECB;
    dataAes.Padding = tweakAes.Padding = PaddingMode.None;
    dataAes.Key = fvek[..16];
    tweakAes.Key = fvek[16..32];
    using var dataEncryptor = dataAes.CreateEncryptor();
    using var tweakEncryptor = tweakAes.CreateEncryptor();

    var ciphertext = new byte[plaintext.Length];
    Span<byte> tweakInput = stackalloc byte[16];
    Span<byte> tweak = stackalloc byte[16];
    Span<byte> input = stackalloc byte[16];
    Span<byte> encrypted = stackalloc byte[16];
    for (var sectorOffset = 0; sectorOffset < plaintext.Length; sectorOffset += 512)
    {
        tweakInput.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(tweakInput, checked((ulong)(sectorOffset / 512)));
        TransformBitLockerTestBlock(tweakEncryptor, tweakInput, tweak);
        for (var blockOffset = 0; blockOffset < 512; blockOffset += 16)
        {
            for (var index = 0; index < 16; index++)
            {
                input[index] = (byte)(plaintext[sectorOffset + blockOffset + index] ^ tweak[index]);
            }

            TransformBitLockerTestBlock(dataEncryptor, input, encrypted);
            for (var index = 0; index < 16; index++)
            {
                ciphertext[sectorOffset + blockOffset + index] = (byte)(encrypted[index] ^ tweak[index]);
            }

            MultiplyBitLockerTestTweak(tweak);
        }
    }

    return ciphertext;
}

static void TransformBitLockerTestBlock(ICryptoTransform transform, ReadOnlySpan<byte> input, Span<byte> output)
{
    var inputArray = input.ToArray();
    var outputArray = new byte[inputArray.Length];
    try
    {
        Assert(
            transform.TransformBlock(inputArray, 0, inputArray.Length, outputArray, 0) == inputArray.Length,
            "BitLocker test AES block transform");
        outputArray.CopyTo(output);
    }
    finally
    {
        CryptographicOperations.ZeroMemory(inputArray);
        CryptographicOperations.ZeroMemory(outputArray);
    }
}

static void MultiplyBitLockerTestTweak(Span<byte> tweak)
{
    var carry = 0;
    for (var index = 0; index < tweak.Length; index++)
    {
        var value = tweak[index];
        var nextCarry = value >> 7;
        tweak[index] = (byte)((value << 1) | carry);
        carry = nextCarry;
    }

    if (carry != 0)
    {
        tweak[0] ^= 0x87;
    }
}

static void CreateOva(string ovaPath, string diskPath)
{
    const string diskName = "disk.vmdk";
    var diskLength = new FileInfo(diskPath).Length;
    var ovf = $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <Envelope xmlns="http://schemas.dmtf.org/ovf/envelope/1" xmlns:ovf="http://schemas.dmtf.org/ovf/envelope/1">
          <References><File ovf:id="disk-file" ovf:href="{diskName}" /></References>
          <DiskSection><Disk ovf:diskId="disk-1" ovf:fileRef="disk-file" ovf:capacity="{diskLength}" /></DiskSection>
        </Envelope>
        """;

    using var output = new FileStream(ovaPath, FileMode.Create, FileAccess.Write, FileShare.None);
    using var writer = new TarWriter(output, TarEntryFormat.Pax, leaveOpen: false);
    using var ovfStream = new MemoryStream(Encoding.UTF8.GetBytes(ovf));
    var ovfEntry = new PaxTarEntry(TarEntryType.RegularFile, "appliance.ovf") { DataStream = ovfStream };
    writer.WriteEntry(ovfEntry);
    using var diskStream = new FileStream(diskPath, FileMode.Open, FileAccess.Read, FileShare.Read);
    var diskEntry = new PaxTarEntry(TarEntryType.RegularFile, diskName) { DataStream = diskStream };
    writer.WriteEntry(diskEntry);
}

static void CreateVmdk(string vmdkPath, string rawPath)
{
    using var disk = VmdkDisk.Initialize(vmdkPath, new FileInfo(rawPath).Length, VmdkDiskCreateType.MonolithicSparse);
    using var raw = new FileStream(rawPath, FileMode.Open, FileAccess.Read, FileShare.Read);
    raw.CopyTo(disk.Content);
}

static void TestXfsTimestampDecoding()
{
    const long bigTimeEpochOffset = 2_147_483_648;
    const uint bigTimeNanoseconds = 123_456_700;
    var bigTimeSeconds = new DateTimeOffset(2026, 8, 20, 9, 23, 9, TimeSpan.Zero).ToUnixTimeSeconds();
    var bigTimeEncoded = checked((ulong)(bigTimeSeconds + bigTimeEpochOffset) * 1_000_000_000UL + bigTimeNanoseconds);
    Span<byte> bigTimeData = stackalloc byte[8];
    BinaryPrimitives.WriteUInt64BigEndian(bigTimeData, bigTimeEncoded);
    var decodedBigTime = XfsTimestampDecoder.Decode(bigTimeData, bigTime: true);
    var expectedBigTime = DateTimeOffset.FromUnixTimeSeconds(bigTimeSeconds)
        .AddTicks(bigTimeNanoseconds / 100)
        .UtcDateTime;
    Assert(decodedBigTime == expectedBigTime, "XFS bigtime timestamp decoding");

    var incorrectlyDecodedYear = DateTimeOffset
        .FromUnixTimeSeconds(BinaryPrimitives.ReadUInt32BigEndian(bigTimeData))
        .Year;
    Assert(incorrectlyDecodedYear == 1999, "XFS bigtime regression fixture");

    const int legacySeconds = -315_619_200;
    const uint legacyNanoseconds = 987_654_300;
    Span<byte> legacyData = stackalloc byte[8];
    BinaryPrimitives.WriteInt32BigEndian(legacyData, legacySeconds);
    BinaryPrimitives.WriteUInt32BigEndian(legacyData[4..], legacyNanoseconds);
    var decodedLegacy = XfsTimestampDecoder.Decode(legacyData, bigTime: false);
    var expectedLegacy = DateTimeOffset.FromUnixTimeSeconds(legacySeconds)
        .AddTicks(legacyNanoseconds / 100)
        .UtcDateTime;
    Assert(decodedLegacy == expectedLegacy, "XFS signed legacy timestamp decoding");
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

    var fastProgressEvents = new List<DiskImageProgress>();
    var fastTemporaryRoot = Path.Combine(AppContext.BaseDirectory, "lzo-fast-temporary-root");
    if (Directory.Exists(fastTemporaryRoot))
    {
        Directory.Delete(fastTemporaryRoot, recursive: true);
    }

    string temporaryRawPath;
    using (var fastReader = DiskImageReaderFactory.Open(
        imagePath,
        new CallbackProgress<DiskImageProgress>(fastProgressEvents.Add),
        LzopOpenMode.TemporaryRaw,
        fastTemporaryRoot))
    {
        Assert(fastReader is TemporaryLzopDiskImageReader, "dd.lzo temporary raw reader factory");
        var temporaryReader = (TemporaryLzopDiskImageReader)fastReader;
        temporaryRawPath = temporaryReader.TemporaryPath;
        Assert(File.Exists(temporaryRawPath), "dd.lzo temporary raw exists while open");
        Assert(
            temporaryRawPath.StartsWith(Path.GetFullPath(fastTemporaryRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            "dd.lzo selected temporary root");
        Assert(new FileInfo(temporaryRawPath).Length == reader.Length, "dd.lzo temporary raw length");
        Assert(fastReader.Length == reader.Length, "dd.lzo fast mode virtual length");

        var expected = new byte[8192];
        var actual = new byte[8192];
        reader.ReadAt(60 * 1024, expected, 0, expected.Length);
        fastReader.ReadAt(60 * 1024, actual, 0, actual.Length);
        Assert(actual.SequenceEqual(expected), "dd.lzo fast mode raw data");
        Assert(
            fastProgressEvents.Any(item => item.Message.Contains("一時RAWへ展開", StringComparison.Ordinal)),
            "dd.lzo temporary raw progress");
        Assert(
            fastProgressEvents.Any(item => item.Message.Contains("事前確保", StringComparison.Ordinal)),
            "dd.lzo temporary raw preallocation progress");
    }

    Assert(!Directory.Exists(Path.GetDirectoryName(temporaryRawPath)), "dd.lzo temporary raw cleanup");
    Assert(Directory.Exists(fastTemporaryRoot), "dd.lzo selected temporary root is preserved");
    Directory.Delete(fastTemporaryRoot);

    var cacheSourcePath = Path.Combine(AppContext.BaseDirectory, "sample-ext4-cache-source.dd.lzo");
    File.Copy(imagePath, cacheSourcePath, overwrite: true);
    var cacheRoot = Path.Combine(AppContext.BaseDirectory, "lzo-fast-cache-root");
    if (Directory.Exists(cacheRoot))
    {
        Directory.Delete(cacheRoot, recursive: true);
    }

    string cachedRawPath;
    var firstCacheProgress = new List<DiskImageProgress>();
    using (var firstCachedReader = DiskImageReaderFactory.Open(
        cacheSourcePath,
        new CallbackProgress<DiskImageProgress>(firstCacheProgress.Add),
        LzopOpenMode.CachedRaw,
        cacheRoot))
    {
        Assert(firstCachedReader is TemporaryLzopDiskImageReader, "dd.lzo cached raw reader factory");
        var cachedReader = (TemporaryLzopDiskImageReader)firstCachedReader;
        cachedRawPath = cachedReader.TemporaryPath;
        Assert(!cachedReader.CacheReused && cachedReader.IsPersistent, "dd.lzo first cache expansion");
        Assert(File.Exists(cachedRawPath), "dd.lzo cache raw created");
        Assert(
            firstCacheProgress.Any(item => item.Message.Contains("RAWキャッシュへ展開中", StringComparison.Ordinal)),
            "dd.lzo cache expansion progress");
    }

    Assert(File.Exists(cachedRawPath), "dd.lzo cache persists after reader disposal");
    var cacheEntries = LzopRawCacheManager.GetEntries(cacheRoot);
    Assert(cacheEntries is [{ IsUsable: true, Completed: true }], "dd.lzo usable cache metadata");
    var orphanCacheDirectory = Path.Combine(cacheRoot, "orphan-cache-fixture");
    Directory.CreateDirectory(orphanCacheDirectory);
    File.WriteAllBytes(Path.Combine(orphanCacheDirectory, "disk.raw.partial"), new byte[32]);
    var orphanEntry = LzopRawCacheManager.GetEntries(cacheRoot).Single(entry => !entry.IsUsable);
    Assert(orphanEntry.SourcePath == "(メタデータなし)", "dd.lzo orphan cache detection");
    Assert(
        LzopRawCacheManager.TryDelete(orphanEntry.CacheId, cacheRoot, out var orphanDeleteError),
        orphanDeleteError);

    var reuseProgress = new List<DiskImageProgress>();
    using (var reusedReader = DiskImageReaderFactory.Open(
        cacheSourcePath,
        new CallbackProgress<DiskImageProgress>(reuseProgress.Add),
        LzopOpenMode.CachedRaw,
        cacheRoot))
    {
        var cachedReader = (TemporaryLzopDiskImageReader)reusedReader;
        Assert(cachedReader.CacheReused, "dd.lzo unchanged cache reuse");
        Assert(cachedReader.TemporaryPath == cachedRawPath, "dd.lzo stable cache raw path");
        Assert(
            reuseProgress.Any(item => item.Message.Contains("キャッシュを再利用", StringComparison.Ordinal)),
            "dd.lzo cache reuse progress");
    }

    var metadataPath = Path.Combine(Path.GetDirectoryName(cachedRawPath)!, "cache.json");
    File.WriteAllText(metadataPath, "{ invalid json", new UTF8Encoding(false));
    using (var invalidMetadataReader = DiskImageReaderFactory.Open(
        cacheSourcePath,
        lzopOpenMode: LzopOpenMode.CachedRaw,
        lzopTemporaryDirectory: cacheRoot))
    {
        Assert(
            invalidMetadataReader is TemporaryLzopDiskImageReader { CacheReused: false },
            "dd.lzo invalid cache metadata rejection");
    }

    var metadataJson = File.ReadAllText(metadataPath);
    var unsupportedVersionJson = metadataJson.Replace("\"Version\": 1", "\"Version\": 99", StringComparison.Ordinal);
    Assert(unsupportedVersionJson != metadataJson, "dd.lzo cache metadata version fixture");
    File.WriteAllText(metadataPath, unsupportedVersionJson, new UTF8Encoding(false));
    using (var unsupportedMetadataReader = DiskImageReaderFactory.Open(
        cacheSourcePath,
        lzopOpenMode: LzopOpenMode.CachedRaw,
        lzopTemporaryDirectory: cacheRoot))
    {
        Assert(
            unsupportedMetadataReader is TemporaryLzopDiskImageReader { CacheReused: false },
            "dd.lzo unsupported cache metadata version rejection");
    }

    metadataJson = File.ReadAllText(metadataPath);
    var incompleteJson = metadataJson.Replace("\"Completed\": true", "\"Completed\": false", StringComparison.Ordinal);
    Assert(incompleteJson != metadataJson, "dd.lzo cache completion fixture");
    File.WriteAllText(metadataPath, incompleteJson, new UTF8Encoding(false));
    using (var rebuiltReader = DiskImageReaderFactory.Open(
        cacheSourcePath,
        lzopOpenMode: LzopOpenMode.CachedRaw,
        lzopTemporaryDirectory: cacheRoot))
    {
        Assert(
            rebuiltReader is TemporaryLzopDiskImageReader { CacheReused: false },
            "dd.lzo incomplete cache rejection");
    }

    using (var stream = new FileStream(cachedRawPath, FileMode.Open, FileAccess.Write, FileShare.None))
    {
        stream.SetLength(stream.Length - 512);
    }

    using (var repairedReader = DiskImageReaderFactory.Open(
        cacheSourcePath,
        lzopOpenMode: LzopOpenMode.CachedRaw,
        lzopTemporaryDirectory: cacheRoot))
    {
        Assert(
            repairedReader is TemporaryLzopDiskImageReader { CacheReused: false },
            "dd.lzo truncated cache rejection");
        Assert(repairedReader.Length == reader.Length, "dd.lzo truncated cache rebuild");
    }

    var sourceWriteTime = File.GetLastWriteTimeUtc(cacheSourcePath);
    var sameLengthReplacementPath = Path.Combine(AppContext.BaseDirectory, "sample-ext4-cache-replacement.dd.lzo");
    TestImageFactory.CreateExt4LzopDisk(sameLengthReplacementPath, originalName: "sample-alt0.dd");
    Assert(
        new FileInfo(sameLengthReplacementPath).Length == new FileInfo(cacheSourcePath).Length,
        "dd.lzo same-length source replacement fixture");
    File.Copy(sameLengthReplacementPath, cacheSourcePath, overwrite: true);
    File.SetLastWriteTimeUtc(cacheSourcePath, sourceWriteTime);
    using (var contentChangedReader = DiskImageReaderFactory.Open(
        cacheSourcePath,
        lzopOpenMode: LzopOpenMode.CachedRaw,
        lzopTemporaryDirectory: cacheRoot))
    {
        Assert(
            contentChangedReader is TemporaryLzopDiskImageReader { CacheReused: false },
            "dd.lzo SHA-256 detects same-length same-time source replacement");
    }

    File.Delete(sameLengthReplacementPath);
    File.SetLastWriteTimeUtc(cacheSourcePath, File.GetLastWriteTimeUtc(cacheSourcePath).AddSeconds(2));
    using (var changedSourceReader = DiskImageReaderFactory.Open(
        cacheSourcePath,
        lzopOpenMode: LzopOpenMode.CachedRaw,
        lzopTemporaryDirectory: cacheRoot))
    {
        Assert(
            changedSourceReader is TemporaryLzopDiskImageReader { CacheReused: false },
            "dd.lzo changed source invalidates cache");
    }

    var savedRawPath = Path.Combine(AppContext.BaseDirectory, "sample-ext4-saved.raw");
    File.Delete(savedRawPath);
    using (var savedRawReader = DiskImageReaderFactory.Open(
        cacheSourcePath,
        lzopOpenMode: LzopOpenMode.SavedRaw,
        lzopTemporaryDirectory: savedRawPath))
    {
        Assert(savedRawReader.Length == reader.Length, "dd.lzo saved raw virtual length");
        Assert(File.Exists(savedRawPath), "dd.lzo saved raw created");
    }

    Assert(File.Exists(savedRawPath), "dd.lzo saved raw persists after disposal");
    try
    {
        using var _ = DiskImageReaderFactory.Open(
            cacheSourcePath,
            lzopOpenMode: LzopOpenMode.SavedRaw,
            lzopTemporaryDirectory: savedRawPath);
        Assert(false, "dd.lzo saved raw overwrite requires confirmation");
    }
    catch (IOException)
    {
    }

    using (var overwrittenRawReader = DiskImageReaderFactory.Open(
        cacheSourcePath,
        lzopOpenMode: LzopOpenMode.SavedRaw,
        lzopTemporaryDirectory: savedRawPath,
        overwriteSavedRaw: true))
    {
        Assert(overwrittenRawReader.Length == reader.Length, "dd.lzo confirmed saved raw overwrite");
    }

    File.Delete(savedRawPath);
    cacheEntries = LzopRawCacheManager.GetEntries(cacheRoot);
    Assert(cacheEntries.Count == 1, "dd.lzo cache manager entry count");
    Assert(
        !LzopRawCacheManager.TryDelete("..", cacheRoot, out _),
        "dd.lzo cache manager rejects parent deletion");
    Assert(
        LzopRawCacheManager.TryDelete(cacheEntries[0].CacheId, cacheRoot, out var cacheDeleteError),
        cacheDeleteError);
    Assert(!Directory.EnumerateFileSystemEntries(cacheRoot).Any(), "dd.lzo cache manager deletion");
    Directory.Delete(cacheRoot);
    File.Delete(cacheSourcePath);

    using (var cancellation = new CancellationTokenSource())
    {
        try
        {
            using var _ = DiskImageReaderFactory.Open(
                imagePath,
                new CallbackProgress<DiskImageProgress>(item =>
                {
                    if (item.Message.Contains("LZO索引作成中", StringComparison.Ordinal))
                    {
                        cancellation.Cancel();
                    }
                }),
                cancellationToken: cancellation.Token);
            Assert(false, "dd.lzo index cancellation throws");
        }
        catch (OperationCanceledException)
        {
        }
    }

    var cancellationTemporaryRoot = Path.Combine(AppContext.BaseDirectory, "lzo-cancellation-temporary-root");
    if (Directory.Exists(cancellationTemporaryRoot))
    {
        Directory.Delete(cancellationTemporaryRoot, recursive: true);
    }

    using (var cancellation = new CancellationTokenSource())
    {
        try
        {
            using var _ = DiskImageReaderFactory.Open(
                imagePath,
                new CallbackProgress<DiskImageProgress>(item =>
                {
                    if (item.Message.Contains("一時RAWへ展開中", StringComparison.Ordinal))
                    {
                        cancellation.Cancel();
                    }
                }),
                LzopOpenMode.TemporaryRaw,
                cancellationTemporaryRoot,
                cancellation.Token);
            Assert(false, "dd.lzo load cancellation throws");
        }
        catch (OperationCanceledException)
        {
        }
    }

    Assert(Directory.Exists(cancellationTemporaryRoot), "dd.lzo cancellation temporary root is preserved");
    Assert(!Directory.EnumerateFileSystemEntries(cancellationTemporaryRoot).Any(), "dd.lzo cancellation temporary files cleanup");
    Directory.Delete(cancellationTemporaryRoot);

    var cancellationCacheRoot = Path.Combine(AppContext.BaseDirectory, "lzo-cancellation-cache-root");
    if (Directory.Exists(cancellationCacheRoot))
    {
        Directory.Delete(cancellationCacheRoot, recursive: true);
    }

    using (var cancellation = new CancellationTokenSource())
    {
        try
        {
            using var _ = DiskImageReaderFactory.Open(
                imagePath,
                new CallbackProgress<DiskImageProgress>(item =>
                {
                    if (item.Message.Contains("RAWキャッシュへ展開中", StringComparison.Ordinal))
                    {
                        cancellation.Cancel();
                    }
                }),
                LzopOpenMode.CachedRaw,
                cancellationCacheRoot,
                cancellation.Token);
            Assert(false, "dd.lzo cache load cancellation throws");
        }
        catch (OperationCanceledException)
        {
        }
    }

    Assert(Directory.Exists(cancellationCacheRoot), "dd.lzo cancellation cache root is preserved");
    Assert(!Directory.EnumerateFileSystemEntries(cancellationCacheRoot).Any(), "dd.lzo cancellation cache cleanup");
    Directory.Delete(cancellationCacheRoot);

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

    var sourceBytes = File.ReadAllBytes(imagePath);
    foreach (var truncatedLength in new[] { 0, 8, 32, sourceBytes.Length / 2, sourceBytes.Length - 1 })
    {
        var truncatedPath = Path.Combine(AppContext.BaseDirectory, $"sample-ext4-truncated-{truncatedLength}.dd.lzo");
        File.WriteAllBytes(truncatedPath, sourceBytes.AsSpan(0, truncatedLength).ToArray());
        try
        {
            using var _ = DiskImageReaderFactory.Open(truncatedPath);
            Assert(false, $"truncated dd.lzo rejection at {truncatedLength} bytes");
        }
        catch (Exception ex) when (ex is EndOfStreamException or InvalidDataException)
        {
        }
        finally
        {
            File.Delete(truncatedPath);
        }
    }

    var corruptedBlockPath = Path.Combine(AppContext.BaseDirectory, "sample-ext4-corrupted-block.dd.lzo");
    var corruptedBlockBytes = sourceBytes.ToArray();
    var firstBlockOffset = 9 + 24;
    var originalNameLength = corruptedBlockBytes[firstBlockOffset];
    firstBlockOffset += 1 + originalNameLength + sizeof(uint);
    var uncompressedSize = BinaryPrimitives.ReadUInt32BigEndian(corruptedBlockBytes.AsSpan(firstBlockOffset, sizeof(uint)));
    var compressedSize = BinaryPrimitives.ReadUInt32BigEndian(corruptedBlockBytes.AsSpan(firstBlockOffset + sizeof(uint), sizeof(uint)));
    firstBlockOffset += 2 * sizeof(uint);
    firstBlockOffset += 2 * sizeof(uint);
    if (compressedSize < uncompressedSize)
    {
        firstBlockOffset += 2 * sizeof(uint);
    }

    corruptedBlockBytes[firstBlockOffset] ^= 0x01;
    File.WriteAllBytes(corruptedBlockPath, corruptedBlockBytes);
    try
    {
        using var corruptedReader = DiskImageReaderFactory.Open(corruptedBlockPath);
        var firstByte = new byte[1];
        corruptedReader.ReadAt(0, firstByte, 0, firstByte.Length);
        Assert(false, "dd.lzo data checksum corruption rejection");
    }
    catch (InvalidDataException ex)
    {
        Assert(
            ex.Message.Contains("lzopブロック", StringComparison.Ordinal)
            && ex.Message.Contains("一致しません", StringComparison.Ordinal),
            "dd.lzo data checksum corruption diagnostic");
    }
    finally
    {
        File.Delete(corruptedBlockPath);
    }
}

static void TestGeneratedVmaLzopImage()
{
    var imagePath = Path.Combine(AppContext.BaseDirectory, "sample-fat16.vma.lzo");
    TestImageFactory.CreateFat16VmaLzop(imagePath);

    var progressEvents = new List<DiskImageProgress>();
    using var reader = DiskImageReaderFactory.Open(
        imagePath,
        new CallbackProgress<DiskImageProgress>(progressEvents.Add));
    Assert(reader is VmaDiskImageReader, "VMA.lzo reader factory");
    var vma = (VmaDiskImageReader)reader;
    Assert(vma.Devices.Count == 2 && vma.ActiveDevice.Name == "scsi0", "VMA largest device selection");
    Assert(vma.Length == TestImageFactory.VirtualSize, "VMA virtual disk size");
    Assert(
        progressEvents.Any(item => item.Message.Contains("VMA索引作成", StringComparison.Ordinal)),
        "VMA index progress");
    AssertFat16Readable(vma, "VMA.lzo");

    var zeroBlock = new byte[4096];
    vma.ReadAt(12 * 1024 * 1024, zeroBlock, 0, zeroBlock.Length);
    Assert(zeroBlock.All(value => value == 0), "VMA sparse zero block");

    vma.SelectDevice(0);
    Assert(vma.ActiveDevice.Name == "efidisk0" && vma.Length == 528 * 1024, "VMA device switching");
    vma.SelectDevice(1);

    using var fastReader = DiskImageReaderFactory.Open(
        imagePath,
        lzopOpenMode: LzopOpenMode.TemporaryRaw);
    Assert(fastReader is VmaDiskImageReader, "VMA.lzo temporary raw reader factory");
    Assert(fastReader.FormatName.Contains("高速モード", StringComparison.Ordinal), "VMA.lzo fast mode format name");
    AssertFat16Readable(fastReader, "VMA.lzo fast mode");

    using var cancellation = new CancellationTokenSource();
    try
    {
        using var _ = DiskImageReaderFactory.Open(
            imagePath,
            new CallbackProgress<DiskImageProgress>(item =>
            {
                if (item.Message.Contains("VMA索引作成中", StringComparison.Ordinal))
                {
                    cancellation.Cancel();
                }
            }),
            cancellationToken: cancellation.Token);
        Assert(false, "VMA index cancellation throws");
    }
    catch (OperationCanceledException)
    {
    }
}

static void TestGeneratedUefiVariableStore()
{
    var image = TestImageFactory.CreateUefiVariableStore();
    var reader = new MemorySectorReader(image, 512);
    Assert(
        UefiVariableStoreReader.TryRead(reader, out var store, out var error) && store is not null,
        error);
    Assert(store!.Authenticated, "authenticated UEFI variable store");
    Assert(store.Variables.Count == 2 && store.Variables.All(variable => variable.IsActive), "UEFI variable count");
    Assert(store.Variables.Single(variable => variable.Name == "BootOrder").Summary == "Boot0001", "UEFI BootOrder decoding");
    Assert(
        store.Variables.Single(variable => variable.Name == "Boot0001").Summary.Contains("Windows Boot Manager", StringComparison.Ordinal),
        "UEFI load option decoding");

    var standardImage = TestImageFactory.CreateUefiVariableStore(authenticated: false);
    var standardReader = new MemorySectorReader(standardImage, 512);
    Assert(
        UefiVariableStoreReader.TryRead(standardReader, out var standardStore, out var standardError)
        && standardStore is not null
        && !standardStore.Authenticated,
        standardError);
}

static void TestGeneratedSwtpmStateStore()
{
    var plainImage = TestImageFactory.CreateSwtpmStateStore(encrypted: false);
    var plainReader = new MemorySectorReader(plainImage, 512);
    Assert(
        SwtpmStateReader.TryRead(plainReader, out var plainStore, out var plainError)
        && plainStore is not null,
        plainError);
    var plainSection = plainStore!.Sections.Single();
    Assert(plainSection.Index == 0 && plainSection.Name.Contains("permall", StringComparison.Ordinal), "swtpm permanent state");
    Assert(plainSection.Blob is { Version: 2, StructurallyValid: true }, "swtpm plain blob structure");
    Assert(plainSection.Blob!.Tlvs is [{ Tag: 1 }], "swtpm plain data TLV");
    Assert(!plainSection.Blob.IsEncrypted, "swtpm plain encryption flag");

    var encryptedImage = TestImageFactory.CreateSwtpmStateStore(encrypted: true);
    var encryptedReader = new MemorySectorReader(encryptedImage, 512);
    Assert(
        SwtpmStateReader.TryRead(encryptedReader, out var encryptedStore, out var encryptedError)
        && encryptedStore is not null,
        encryptedError);
    var encryptedBlob = encryptedStore!.Sections.Single().Blob!;
    Assert(encryptedBlob.IsEncrypted && encryptedBlob.Uses256BitKey, "swtpm encrypted 256-bit state");
    Assert(
        encryptedBlob.Tlvs.Select(tlv => tlv.Tag).SequenceEqual(new ushort[] { 2, 3, 6 }),
        "swtpm encrypted TLV sequence");

    var malformedImage = TestImageFactory.CreateSwtpmStateStore(encrypted: false);
    BinaryPrimitives.WriteUInt32LittleEndian(malformedImage.AsSpan(20, 4), uint.MaxValue);
    var malformedReader = new MemorySectorReader(malformedImage, 512);
    Assert(
        !SwtpmStateReader.TryRead(malformedReader, out _, out var malformedError)
        && malformedError.Contains("範囲", StringComparison.Ordinal),
        "swtpm invalid section bounds");
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

static void TestVirtualPaths()
{
    Assert(VirtualPath.Normalize("") == "/", "virtual path empty normalization");
    Assert(VirtualPath.Normalize("//backup//images/") == "/backup/images", "virtual path normalization");
    Assert(VirtualPath.Combine("/", "disk.qcow2") == "/disk.qcow2", "virtual path root combination");
    Assert(VirtualPath.Combine("/backup", "disk.qcow2") == "/backup/disk.qcow2", "virtual path nested combination");
    Assert(VirtualPath.GetParent("/disk.qcow2") == "/", "virtual path root parent");
    Assert(VirtualPath.GetParent("/backup/images/disk.qcow2") == "/backup/images", "virtual path nested parent");
    Assert(VirtualPath.Split("/backup/images").SequenceEqual(["backup", "images"]), "virtual path split");
}

static void TestNtfsMftMirrorFallback()
{
    const int bytesPerSector = 512;
    const int clusterSize = 4096;
    const int recordSize = 1024;
    const int mftLcn = 32;
    const int mftMirrorLcn = 2;
    var volume = new byte[256 * 1024];
    Encoding.ASCII.GetBytes("NTFS    ").CopyTo(volume, 3);
    BinaryPrimitives.WriteUInt16LittleEndian(volume.AsSpan(11, 2), bytesPerSector);
    volume[13] = clusterSize / bytesPerSector;
    BinaryPrimitives.WriteInt64LittleEndian(volume.AsSpan(48, 8), mftLcn);
    BinaryPrimitives.WriteInt64LittleEndian(volume.AsSpan(56, 8), mftMirrorLcn);
    volume[64] = unchecked((byte)-10);
    volume[510] = 0x55;
    volume[511] = 0xaa;

    var mftRecord = CreateFileRecord(0, 5, "$MFT", isDirectory: false, data: null, includeMftRuns: true);
    mftRecord.CopyTo(volume, mftMirrorLcn * clusterSize);
    CreateFileRecord(5, 5, ".", isDirectory: true, data: null, includeMftRuns: false)
        .CopyTo(volume, mftLcn * clusterSize + 5 * recordSize);
    CreateFileRecord(6, 5, "hello.txt", isDirectory: false, data: Encoding.ASCII.GetBytes("mirror recovery"), includeMftRuns: false)
        .CopyTo(volume, mftLcn * clusterSize + 6 * recordSize);
    var deletedRecord = CreateFileRecord(7, 5, "deleted.txt", isDirectory: false, data: Encoding.ASCII.GetBytes("deleted content"), includeMftRuns: false);
    BinaryPrimitives.WriteUInt16LittleEndian(deletedRecord.AsSpan(22, 2), 0);
    deletedRecord.CopyTo(volume, mftLcn * clusterSize + 7 * recordSize);

    var reader = new MemorySectorReader(volume, bytesPerSector);
    var partition = new PartitionInfo
    {
        Number = 1,
        Scheme = "test",
        StartLba = 0,
        SectorCount = (ulong)(volume.Length / bytesPerSector),
        LengthOverrideBytes = volume.Length
    };
    var fileSystem = new NtfsFileSystem(reader, partition);
    var file = fileSystem.ListDirectory(fileSystem.Root).Single(node => node.Name == "hello.txt");
    Assert(
        Encoding.ASCII.GetString(fileSystem.ReadFile(file, 0, (int)file.Size)) == "mirror recovery",
        "NTFS $MFTMirr fallback");

    var deletedFileSystem = new NtfsFileSystem(reader, partition, deletedOnly: true);
    var deletedFile = deletedFileSystem.ListDirectory(deletedFileSystem.Root).Single(node => node.Name == "deleted.txt");
    Assert(
        Encoding.ASCII.GetString(deletedFileSystem.ReadFile(deletedFile, 0, (int)deletedFile.Size)) == "deleted content",
        "NTFS deleted-only scan uses active $MFT record 0");

    static byte[] CreateFileRecord(long recordNumber, long parentRecord, string name, bool isDirectory, byte[]? data, bool includeMftRuns)
    {
        var record = new byte[recordSize];
        Encoding.ASCII.GetBytes("FILE").CopyTo(record, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4, 2), 0x30);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(6, 2), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(16, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(18, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(20, 2), 0x38);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(22, 2), (ushort)(isDirectory ? 3 : 1));
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(44, 4), checked((uint)recordNumber));

        var attributeOffset = 0x38;
        var nameBytes = Encoding.Unicode.GetBytes(name);
        var nameValueLength = 66 + nameBytes.Length;
        var nameAttributeLength = (24 + nameValueLength + 7) & ~7;
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(attributeOffset, 4), 0x30);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(attributeOffset + 4, 4), checked((uint)nameAttributeLength));
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(attributeOffset + 16, 4), checked((uint)nameValueLength));
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(attributeOffset + 20, 2), 24);
        var nameValue = attributeOffset + 24;
        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(nameValue, 8), parentRecord);
        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(nameValue + 48, 8), data?.Length ?? 0);
        record[nameValue + 64] = checked((byte)name.Length);
        record[nameValue + 65] = 1;
        nameBytes.CopyTo(record, nameValue + 66);
        attributeOffset += nameAttributeLength;

        if (includeMftRuns)
        {
            const int dataAttributeLength = 72;
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(attributeOffset, 4), 0x80);
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(attributeOffset + 4, 4), dataAttributeLength);
            record[attributeOffset + 8] = 1;
            BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(attributeOffset + 24, 8), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(attributeOffset + 32, 2), 64);
            BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(attributeOffset + 40, 8), 2 * clusterSize);
            BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(attributeOffset + 48, 8), 2 * clusterSize);
            BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(attributeOffset + 56, 8), 2 * clusterSize);
            record[attributeOffset + 64] = 0x11;
            record[attributeOffset + 65] = 2;
            record[attributeOffset + 66] = mftLcn;
            attributeOffset += dataAttributeLength;
        }
        else if (data is not null)
        {
            var dataAttributeLength = (24 + data.Length + 7) & ~7;
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(attributeOffset, 4), 0x80);
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(attributeOffset + 4, 4), checked((uint)dataAttributeLength));
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(attributeOffset + 16, 4), checked((uint)data.Length));
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(attributeOffset + 20, 2), 24);
            data.CopyTo(record, attributeOffset + 24);
            attributeOffset += dataAttributeLength;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(attributeOffset, 4), uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(24, 4), checked((uint)(attributeOffset + 8)));
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(28, 4), recordSize);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(0x30, 2), 0xa55a);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(0x32, 2), BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(510, 2)));
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(0x34, 2), BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(1022, 2)));
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(510, 2), 0xa55a);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(1022, 2), 0xa55a);
        return record;
    }
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

static void InspectImage(string imagePath, bool copySmoke, int? vmaDeviceId)
{
    using var reader = DiskImageReaderFactory.Open(imagePath);
    if (reader is VmaDiskImageReader selectableVma && vmaDeviceId is int requestedDeviceId)
    {
        var index = selectableVma.Devices
            .Select((device, deviceIndex) => (device, deviceIndex))
            .Where(item => item.device.Id == requestedDeviceId)
            .Select(item => item.deviceIndex)
            .DefaultIfEmpty(-1)
            .Single();
        if (index < 0)
        {
            throw new ArgumentException($"VMA device {requestedDeviceId} が見つかりません。");
        }

        selectableVma.SelectDevice(index);
    }

    Console.WriteLine(imagePath);
    Console.WriteLine($"format: {reader.FormatName}");
    Console.WriteLine($"virtual size: {FormatBytes(reader.Length)}");
    if (reader is VmaDiskImageReader vma)
    {
        Console.WriteLine($"VMA devices: {vma.Devices.Count}");
        foreach (var device in vma.Devices)
        {
            var marker = device == vma.ActiveDevice ? "*" : " ";
            Console.WriteLine($"{marker} device {device.Id}: {device.Name}, {FormatBytes(device.Size)}");
        }
    }

    foreach (var warning in reader.GetWarnings())
    {
        Console.WriteLine($"warning: {warning}");
    }

    if (UefiVariableStoreReader.TryRead(reader, out var uefiStore, out var uefiError)
        && uefiStore is not null)
    {
        Console.WriteLine($"UEFI variable store: {(uefiStore.Authenticated ? "authenticated" : "standard")}");
        Console.WriteLine($"UEFI variables: {uefiStore.Variables.Count}");
        foreach (var variable in uefiStore.Variables)
        {
            Console.WriteLine(
                $"  {variable.StateText,-10} {variable.Name,-24} {variable.Data.Length,8:N0} bytes  {variable.Summary}");
        }
    }
    else if (reader is VmaDiskImageReader selectedVma
        && selectedVma.ActiveDevice.Name.Contains("efi", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine($"UEFI variable store: {uefiError}");
    }

    if (SwtpmStateReader.TryRead(reader, out var tpmStore, out var tpmError)
        && tpmStore is not null)
    {
        Console.WriteLine($"swtpm linear store: version {tpmStore.Version}, header {tpmStore.HeaderSize:N0} bytes");
        foreach (var section in tpmStore.Sections)
        {
            var blob = section.Blob;
            Console.WriteLine(
                $"  slot {section.Index}: {section.Name}, data {section.DataLength:N0} bytes, "
                + $"section {section.SectionLength:N0} bytes");
            Console.WriteLine(
                blob is null
                    ? "    blob: unrecognized"
                    : $"    blob: v{blob.Version}, flags 0x{blob.Flags:X4}, "
                        + $"encryption {SwtpmStateReader.FormatEncryption(blob)}, "
                        + $"TLVs {blob.Tlvs.Count}, valid {blob.StructurallyValid}");
            if (blob is not null)
            {
                Console.WriteLine($"    readability: {SwtpmStateReader.GetReadability(blob)}");
                foreach (var tlv in blob.Tlvs)
                {
                    Console.WriteLine($"      tag {tlv.Tag}: {tlv.Name}, {tlv.Length:N0} bytes");
                }
            }
        }

        foreach (var warning in tpmStore.Warnings)
        {
            Console.WriteLine($"swtpm warning: {warning}");
        }
    }
    else if (reader is VmaDiskImageReader tpmVma
        && tpmVma.ActiveDevice.Name.Contains("tpm", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine($"swtpm linear store: {tpmError}");
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

    public static void CreateExt4LzopDisk(
        string path,
        bool corruptHeaderChecksum = false,
        string originalName = "sample-ext4.dd")
    {
        WriteLzop(path, CreateMinimalExt4Disk(), originalName, corruptHeaderChecksum);
    }

    public static void CreateFat16VmaLzop(string path)
    {
        WriteLzop(path, CreateVma(CreateVirtualDisk()), "sample-fat16.vma", corruptHeaderChecksum: false);
    }

    public static byte[] CreateUefiVariableStore(bool authenticated = true)
    {
        const int imageSize = 128 * 1024;
        const int firmwareVolumeHeaderSize = 72;
        const int variableStoreHeaderSize = 28;
        var image = Enumerable.Repeat((byte)0xff, imageSize).ToArray();
        Array.Clear(image, 0, firmwareVolumeHeaderSize);

        Guid.Parse("fff12b8d-7696-4c8b-a985-2747075b4f50").TryWriteBytes(image.AsSpan(16, 16));
        WriteU64Le(image, 32, imageSize);
        WriteU32Le(image, 40, 0x4856465f);
        WriteU32Le(image, 44, 0x0004feff);
        WriteU16Le(image, 48, firmwareVolumeHeaderSize);
        image[55] = 2;
        WriteU32Le(image, 56, 2);
        WriteU32Le(image, 60, 64 * 1024);

        var storeOffset = firmwareVolumeHeaderSize;
        Array.Clear(image, storeOffset, variableStoreHeaderSize);
        var storeGuid = authenticated
            ? Guid.Parse("aaf32c78-947b-439a-a180-2e144ec37792")
            : Guid.Parse("ddcf3616-3275-4164-98b6-fe85707ffe7d");
        storeGuid.TryWriteBytes(image.AsSpan(storeOffset, 16));
        WriteU32Le(image, storeOffset + 16, imageSize - storeOffset);
        image[storeOffset + 20] = 0x5a;
        image[storeOffset + 21] = 0xfe;

        var variableOffset = storeOffset + variableStoreHeaderSize;
        WriteUefiVariable(
            image,
            ref variableOffset,
            "BootOrder",
            Guid.Parse("8be4df61-93ca-11d2-aa0d-00e098032b8c"),
            [0x01, 0x00],
            authenticated);

        var description = Encoding.Unicode.GetBytes("Windows Boot Manager\0");
        var loadOption = new byte[6 + description.Length];
        WriteU32Le(loadOption, 0, 1);
        description.CopyTo(loadOption, 6);
        WriteUefiVariable(
            image,
            ref variableOffset,
            "Boot0001",
            Guid.Parse("8be4df61-93ca-11d2-aa0d-00e098032b8c"),
            loadOption,
            authenticated);

        uint checksum = 0;
        for (var offset = 0; offset < firmwareVolumeHeaderSize; offset += 2)
        {
            checksum += BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(offset, 2));
        }

        WriteU16Le(image, 50, unchecked((ushort)(0u - checksum)));
        return image;
    }

    public static byte[] CreateSwtpmStateStore(bool encrypted)
    {
        const int imageSize = 1024 * 1024;
        const int linearHeaderSize = 192;
        var tlvs = new List<(ushort Tag, byte[] Data)>();
        ushort flags;
        if (encrypted)
        {
            flags = 0x0009;
            tlvs.Add((2, Enumerable.Range(0, 48).Select(index => (byte)(index + 1)).ToArray()));
            tlvs.Add((3, Enumerable.Repeat((byte)0xa5, 32).ToArray()));
            tlvs.Add((6, Enumerable.Repeat((byte)0x5a, 16).ToArray()));
        }
        else
        {
            flags = 0;
            tlvs.Add((1, Encoding.ASCII.GetBytes("synthetic libtpms permanent state")));
        }

        var blobLength = 10 + tlvs.Sum(tlv => 6 + tlv.Data.Length);
        var sectionLength = 1;
        while (sectionLength < blobLength)
        {
            sectionLength <<= 1;
        }

        var image = new byte[imageSize];
        WriteU64Le(image, 0, 0x737774706d6c696e);
        image[8] = 1;
        WriteU16Le(image, 10, linearHeaderSize);
        WriteU32Le(image, 12, linearHeaderSize);
        WriteU32Le(image, 16, blobLength);
        WriteU32Le(image, 20, sectionLength);

        var offset = linearHeaderSize;
        image[offset] = 2;
        image[offset + 1] = 1;
        WriteU16Be(image, offset + 2, 10);
        WriteU16Be(image, offset + 4, flags);
        WriteU32Be(image, offset + 6, checked((uint)blobLength));
        offset += 10;
        foreach (var tlv in tlvs)
        {
            WriteU16Be(image, offset, tlv.Tag);
            WriteU32Be(image, offset + 2, checked((uint)tlv.Data.Length));
            tlv.Data.CopyTo(image, offset + 6);
            offset += 6 + tlv.Data.Length;
        }

        return image;
    }

    private static void WriteLzop(string path, byte[] disk, string originalName, bool corruptHeaderChecksum)
    {
        const int blockSize = 64 * 1024;
        const uint flags = 0x00001303;
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
        var name = Encoding.UTF8.GetBytes(originalName);
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

    private static byte[] CreateVma(byte[] disk)
    {
        const int headerSize = 13 * 1024;
        const int blobOffset = 12 * 1024;
        const int extentHeaderSize = 512;
        const int blockSize = 4 * 1024;
        const int clusterSize = 64 * 1024;
        const int blockInfoCount = 59;

        var uuid = Guid.Parse("93818867-21f5-40aa-9177-0d706569af39").ToByteArray(bigEndian: true);
        var header = new byte[headerSize];
        Encoding.ASCII.GetBytes("VMA\0").CopyTo(header, 0);
        WriteU32Be(header, 4, 1);
        uuid.CopyTo(header, 8);
        WriteU64Be(header, 24, 1_700_000_000);
        WriteU32Be(header, 48, blobOffset);

        var efiName = Encoding.UTF8.GetBytes("efidisk0");
        var diskName = Encoding.UTF8.GetBytes("scsi0");
        const int efiNamePointer = 1;
        var diskNamePointer = efiNamePointer + 2 + efiName.Length;
        var blobSize = diskNamePointer + 2 + diskName.Length;
        WriteU32Be(header, 52, checked((uint)blobSize));
        WriteU32Be(header, 56, headerSize);
        WriteU16Le(header, blobOffset + efiNamePointer, checked((ushort)efiName.Length));
        efiName.CopyTo(header, blobOffset + efiNamePointer + 2);
        WriteU16Le(header, blobOffset + diskNamePointer, checked((ushort)diskName.Length));
        diskName.CopyTo(header, blobOffset + diskNamePointer + 2);

        var efiDeviceInfoOffset = 4096 + 32;
        WriteU32Be(header, efiDeviceInfoOffset, efiNamePointer);
        WriteU64Be(header, efiDeviceInfoOffset + 8, 528 * 1024);
        var diskDeviceInfoOffset = 4096 + 64;
        WriteU32Be(header, diskDeviceInfoOffset, checked((uint)diskNamePointer));
        WriteU64Be(header, diskDeviceInfoOffset + 8, checked((ulong)disk.Length));
        WriteMd5(header, 32);

        var output = new List<byte>(header);
        var clusters = new List<(uint Number, ushort Mask, List<byte[]> Blocks)>();
        var clusterCount = (disk.Length + clusterSize - 1) / clusterSize;
        for (var clusterNumber = 0; clusterNumber < clusterCount; clusterNumber++)
        {
            ushort mask = 0;
            var blocks = new List<byte[]>();
            for (var blockIndex = 0; blockIndex < 16; blockIndex++)
            {
                var diskOffset = clusterNumber * clusterSize + blockIndex * blockSize;
                var block = new byte[blockSize];
                var available = Math.Min(blockSize, disk.Length - diskOffset);
                if (available > 0)
                {
                    Array.Copy(disk, diskOffset, block, 0, available);
                }

                if (block.Any(value => value != 0))
                {
                    mask |= checked((ushort)(1 << blockIndex));
                    blocks.Add(block);
                }
            }

            if (mask != 0)
            {
                clusters.Add((checked((uint)clusterNumber), mask, blocks));
            }
        }

        for (var clusterStart = 0; clusterStart < clusters.Count; clusterStart += blockInfoCount)
        {
            var extentClusters = clusters
                .Skip(clusterStart)
                .Take(blockInfoCount)
                .ToList();
            var extentHeader = new byte[extentHeaderSize];
            Encoding.ASCII.GetBytes("VMAE").CopyTo(extentHeader, 0);
            var blockCount = extentClusters.Sum(item => item.Blocks.Count);
            WriteU16Be(extentHeader, 6, checked((ushort)blockCount));
            uuid.CopyTo(extentHeader, 8);

            for (var index = 0; index < extentClusters.Count; index++)
            {
                var item = extentClusters[index];
                var infoOffset = 40 + index * 8;
                WriteU16Be(extentHeader, infoOffset, item.Mask);
                extentHeader[infoOffset + 3] = 2;
                WriteU32Be(extentHeader, infoOffset + 4, item.Number);
            }

            WriteMd5(extentHeader, 24);
            output.AddRange(extentHeader);
            foreach (var item in extentClusters)
            {
                foreach (var block in item.Blocks)
                {
                    output.AddRange(block);
                }
            }
        }

        return output.ToArray();
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

    private static void WriteUefiVariable(
        byte[] image,
        ref int offset,
        string name,
        Guid vendorGuid,
        byte[] data,
        bool authenticated)
    {
        var headerSize = authenticated ? 60 : 32;
        offset = (offset + 3) & ~3;
        Array.Clear(image, offset, headerSize);
        WriteU16Le(image, offset, 0x55aa);
        image[offset + 2] = 0x3f;
        WriteU32Le(image, offset + 4, 7);
        var nameBytes = Encoding.Unicode.GetBytes(name + '\0');
        var nameSizeOffset = authenticated ? offset + 36 : offset + 8;
        var dataSizeOffset = authenticated ? offset + 40 : offset + 12;
        var guidOffset = authenticated ? offset + 44 : offset + 16;
        WriteU32Le(image, nameSizeOffset, nameBytes.Length);
        WriteU32Le(image, dataSizeOffset, data.Length);
        vendorGuid.TryWriteBytes(image.AsSpan(guidOffset, 16));
        nameBytes.CopyTo(image, offset + headerSize);
        data.CopyTo(image, offset + headerSize + nameBytes.Length);
        offset += headerSize + nameBytes.Length + data.Length;
    }

    private static void WriteU16Be(byte[] data, int offset, ushort value)
    {
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset, sizeof(ushort)), value);
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

    private static void WriteMd5(byte[] data, int checksumOffset)
    {
        Array.Clear(data, checksumOffset, 16);
        MD5.HashData(data).CopyTo(data, checksumOffset);
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

internal sealed class InterruptibleCopyFileSystem : IReadOnlyFileSystem
{
    public string Name => "Interruptible copy test";
    public PartitionInfo Partition { get; } = new();
    public VfsNode Root { get; } = new() { IsDirectory = true };
    public VfsNode LargeFile { get; } = new()
    {
        Name = "large-copy.bin",
        Size = 3L * 1024 * 1024
    };

    public IReadOnlyList<VfsNode> ListDirectory(VfsNode directory) => Array.Empty<VfsNode>();

    public byte[] ReadFile(VfsNode file, long offset, int count) => new byte[count];
}
