using System.Buffers.Binary;
using System.Formats.Tar;
using System.Text;
using System.Xml;
using Qcow2Explorer.Core;

internal static class ContainerRobustnessTests
{
    public static void Run(string directory)
    {
        TestRawTruncationDetected(directory);
        TestLzopIndexPathBounds();
        TestLzopRawMetadataBounds(directory);
        TestMinimalParallelsImage(directory);
        TestParallelsBatLimits(directory);
        TestParallelsDescriptorLimits(directory);
        TestVmaHeaderLimit(directory);
        TestOvaExtractionGuards(directory);
    }

    private static void TestRawTruncationDetected(string directory)
    {
        var path = Path.Combine(directory, "raw-truncated-while-open.raw");
        File.WriteAllBytes(path, new byte[8192]);
        using var reader = new RawDiskImageReader(path);
        using (var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            writer.SetLength(4096);
        }

        AssertThrows<EndOfStreamException>(
            () => reader.ReadAt(4096, new byte[512], 0, 512),
            "RAW truncation after opening is not returned as zero data");
    }

    private static void TestLzopIndexPathBounds()
    {
        using var validStream = new MemoryStream();
        using (var writer = new BinaryWriter(validStream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("/tmp/日本語/disk.lzo");
        }

        validStream.Position = 0;
        using (var reader = new BinaryReader(validStream, Encoding.UTF8, leaveOpen: true))
        {
            Assert(
                LzopIndexCacheManager.ReadSourcePath(reader) == "/tmp/日本語/disk.lzo",
                "bounded LZO index source path accepted");
        }

        using var oversizedStream = new MemoryStream();
        using (var writer = new BinaryWriter(oversizedStream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write7BitEncodedInt(LzopIndexCacheManager.MaximumSourcePathBytes + 1);
        }

        oversizedStream.Position = 0;
        using var oversizedReader = new BinaryReader(oversizedStream, Encoding.UTF8, leaveOpen: true);
        AssertThrows<InvalidDataException>(
            () => LzopIndexCacheManager.ReadSourcePath(oversizedReader),
            "oversized LZO index source path rejected before allocation");
    }

    private static void TestLzopRawMetadataBounds(string directory)
    {
        var cacheRoot = Path.Combine(directory, "oversized-lzo-raw-metadata");
        var entryDirectory = Path.Combine(cacheRoot, "entry");
        Directory.CreateDirectory(entryDirectory);
        var metadataPath = Path.Combine(entryDirectory, LzopRawCacheManager.MetadataFileName);
        using (var stream = new FileStream(metadataPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.SetLength(LzopRawCacheManager.MaximumMetadataBytes + 1L);
        }

        var entry = LzopRawCacheManager.GetEntries(cacheRoot).Single();
        Assert(!entry.IsUsable, "oversized LZO raw metadata treated as unusable");
        Assert(entry.SourcePath == "(メタデータなし)", "oversized LZO raw metadata not deserialized");
    }

    private static void TestMinimalParallelsImage(string directory)
    {
        var path = Path.Combine(directory, "minimal-empty.hds");
        var image = CreateParallelsHeader(batEntryCount: 1, diskSectors: 128, emptyImage: true);
        Array.Resize(ref image, 68);
        File.WriteAllBytes(path, image);

        using var reader = ParallelsHddReader.Open(path);
        Assert(reader.Length == 64L * 1024, "minimal Parallels virtual size");
        var buffer = new byte[4096];
        reader.ReadAt(0, buffer, 0, buffer.Length);
        Assert(buffer.All(value => value == 0), "minimal Parallels empty image reads as zero");
    }

    private static void TestParallelsBatLimits(string directory)
    {
        var oversizedPath = Path.Combine(directory, "oversized-bat.hds");
        var oversized = CreateParallelsHeader(16_777_217, 128, emptyImage: false);
        AssertRejectedAndDeletable<NotSupportedException>(
            oversizedPath,
            oversized,
            () => ParallelsHddReader.Open(oversizedPath),
            "oversized Parallels BAT");

        var truncatedPath = Path.Combine(directory, "truncated-bat.hds");
        var truncated = CreateParallelsHeader(1, 128, emptyImage: false);
        AssertRejectedAndDeletable<EndOfStreamException>(
            truncatedPath,
            truncated,
            () => ParallelsHddReader.Open(truncatedPath),
            "truncated Parallels BAT");

        var zeroSizePath = Path.Combine(directory, "zero-size.hds");
        var zeroSize = CreateParallelsHeader(0, 0, emptyImage: true);
        AssertRejectedAndDeletable<InvalidDataException>(
            zeroSizePath,
            zeroSize,
            () => ParallelsHddReader.Open(zeroSizePath),
            "zero-size Parallels image");
    }

    private static void TestParallelsDescriptorLimits(string directory)
    {
        var negativeBundle = Path.Combine(directory, "negative-size.hdd");
        Directory.CreateDirectory(negativeBundle);
        var negativeDescriptor = Path.Combine(negativeBundle, "DiskDescriptor.xml");
        File.WriteAllText(negativeDescriptor, "<Parallels_disk_image><Disk_size>-1</Disk_size></Parallels_disk_image>");
        AssertThrows<InvalidDataException>(
            () => ParallelsHddReader.Open(negativeBundle),
            "negative Parallels bundle size rejected");

        var dtdBundle = Path.Combine(directory, "dtd.hdd");
        Directory.CreateDirectory(dtdBundle);
        var dtdDescriptor = Path.Combine(dtdBundle, "DiskDescriptor.xml");
        File.WriteAllText(
            dtdDescriptor,
            "<!DOCTYPE x [<!ENTITY local SYSTEM 'file:///does-not-exist'>]><x><Disk_size>1</Disk_size>&local;</x>");
        AssertThrows<XmlException>(
            () => ParallelsHddReader.Open(dtdBundle),
            "Parallels descriptor DTD rejected");

        var oversizedBundle = Path.Combine(directory, "oversized-descriptor.hdd");
        Directory.CreateDirectory(oversizedBundle);
        var oversizedDescriptor = Path.Combine(oversizedBundle, "DiskDescriptor.xml");
        using (var stream = new FileStream(oversizedDescriptor, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.SetLength(16L * 1024 * 1024 + 1);
        }

        AssertThrows<InvalidDataException>(
            () => ParallelsHddReader.Open(oversizedBundle),
            "oversized Parallels descriptor rejected");

        var excessiveLayersBundle = Path.Combine(directory, "excessive-layers.hdd");
        Directory.CreateDirectory(excessiveLayersBundle);
        var excessiveLayers = new StringBuilder(
            "<Parallels_disk_image><Disk_size>128</Disk_size><Storage><Start>0</Start><End>128</End>");
        for (var index = 0; index <= 4096; index++)
        {
            excessiveLayers.Append("<Image><File>layer-")
                .Append(index)
                .Append(".hds</File></Image>");
        }

        excessiveLayers.Append("</Storage></Parallels_disk_image>");
        File.WriteAllText(
            Path.Combine(excessiveLayersBundle, "DiskDescriptor.xml"),
            excessiveLayers.ToString());
        AssertThrows<NotSupportedException>(
            () => ParallelsHddReader.Open(excessiveLayersBundle),
            "excessive Parallels layer count rejected");

        var canceledBundle = Path.Combine(directory, "canceled-open.hdd");
        Directory.CreateDirectory(canceledBundle);
        File.WriteAllText(
            Path.Combine(canceledBundle, "DiskDescriptor.xml"),
            "<Parallels_disk_image><Disk_size>128</Disk_size></Parallels_disk_image>");
        AssertThrows<OperationCanceledException>(
            () => ParallelsHddReader.Open(canceledBundle, new CancellationToken(canceled: true)),
            "canceled Parallels open");

        var windowsSeparatorBundle = Path.Combine(directory, "windows-separator.hdd");
        var nestedDirectory = Path.Combine(windowsSeparatorBundle, "layers");
        Directory.CreateDirectory(nestedDirectory);
        var nestedImage = CreateParallelsHeader(batEntryCount: 1, diskSectors: 128, emptyImage: true);
        Array.Resize(ref nestedImage, 68);
        File.WriteAllBytes(Path.Combine(nestedDirectory, "disk.hds"), nestedImage);
        File.WriteAllText(
            Path.Combine(windowsSeparatorBundle, "DiskDescriptor.xml"),
            """
            <Parallels_disk_image>
              <Disk_size>128</Disk_size>
              <Storage>
                <Start>0</Start>
                <End>128</End>
                <Image>
                  <Type>Compressed</Type>
                  <File>layers\disk.hds</File>
                </Image>
              </Storage>
            </Parallels_disk_image>
            """);
        using var reader = ParallelsHddReader.Open(windowsSeparatorBundle);
        Assert(reader.Length == 64L * 1024, "Windows-style Parallels image path opens cross-platform");
    }

    private static void TestVmaHeaderLimit(string directory)
    {
        var path = Path.Combine(directory, "oversized-header.vma");
        const int headerSize = 64 * 1024 * 1024 + 512;
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.SetLength(headerSize);
            var prefix = new byte[60];
            "VMA\0"u8.CopyTo(prefix);
            BinaryPrimitives.WriteUInt32BigEndian(prefix.AsSpan(4), 1);
            BinaryPrimitives.WriteUInt32BigEndian(prefix.AsSpan(48), 12 * 1024);
            BinaryPrimitives.WriteUInt32BigEndian(prefix.AsSpan(56), headerSize);
            stream.Position = 0;
            stream.Write(prefix);
        }

        var rejected = false;
        try
        {
            var source = new RawDiskImageReader(path);
            using var reader = new VmaDiskImageReader(source);
        }
        catch (InvalidDataException)
        {
            rejected = true;
        }

        Assert(rejected, "oversized VMA header rejected");
        File.Delete(path);
        Assert(!File.Exists(path), "invalid VMA source handle released");
    }

    private static void TestOvaExtractionGuards(string directory)
    {
        var temporaryRoot = Path.Combine(directory, "ova-guard-temporary");
        Directory.CreateDirectory(temporaryRoot);
        var escapedPath = Path.Combine(directory, "escaped.raw");
        var traversalArchive = Path.Combine(directory, "traversal.ova");
        WriteTar(traversalArchive, [("../escaped.raw", new byte[] { 1, 2, 3 })]);
        AssertThrows<InvalidDataException>(
            () => OvaDiskImageReader.Open(traversalArchive, temporaryRoot: temporaryRoot),
            "OVA traversal path rejected");
        Assert(!File.Exists(escapedPath), "OVA traversal did not escape extraction root");
        Assert(!Directory.EnumerateFileSystemEntries(temporaryRoot).Any(), "OVA traversal temporary cleanup");

        var alternateStreamArchive = Path.Combine(directory, "alternate-stream.ova");
        WriteTar(alternateStreamArchive, [("disk.raw:hidden", new byte[] { 4, 5, 6 })]);
        AssertThrows<InvalidDataException>(
            () => OvaDiskImageReader.Open(alternateStreamArchive, temporaryRoot: temporaryRoot),
            "OVA alternate-stream path rejected");
        Assert(!Directory.EnumerateFileSystemEntries(temporaryRoot).Any(), "OVA alternate-stream temporary cleanup");

        var excessiveEntriesArchive = Path.Combine(directory, "too-many-entries.ova");
        using (var output = new FileStream(excessiveEntriesArchive, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new TarWriter(output, leaveOpen: false))
        {
            for (var index = 0; index <= 4096; index++)
            {
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, $"files/{index:D4}.txt")
                {
                    DataStream = Stream.Null,
                });
            }
        }

        AssertThrows<InvalidDataException>(
            () => OvaDiskImageReader.Open(excessiveEntriesArchive, temporaryRoot: temporaryRoot),
            "OVA excessive entry count rejected");
        Assert(!Directory.EnumerateFileSystemEntries(temporaryRoot).Any(), "OVA entry-limit temporary cleanup");
    }

    private static void WriteTar(string path, IReadOnlyList<(string Name, byte[] Data)> entries)
    {
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new TarWriter(output, leaveOpen: false);
        foreach (var (name, data) in entries)
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name)
            {
                DataStream = new MemoryStream(data, writable: false),
            });
        }
    }

    private static byte[] CreateParallelsHeader(uint batEntryCount, uint diskSectors, bool emptyImage)
    {
        var header = new byte[64];
        Encoding.ASCII.GetBytes("WithoutFreeSpace").CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28), 128);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(32), batEntryCount);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(36), diskSectors);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(52), emptyImage ? 1U : 0U);
        return header;
    }

    private static void AssertRejectedAndDeletable<TException>(
        string path,
        byte[] image,
        Func<IDisposable> open,
        string message)
        where TException : Exception
    {
        File.WriteAllBytes(path, image);
        AssertThrows<TException>(() =>
        {
            using var value = open();
        }, $"{message} rejected");
        File.Delete(path);
        Assert(!File.Exists(path), $"{message} handle released");
    }

    private static void AssertThrows<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Assertion failed: {message}");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Assertion failed: {message}");
        }
    }
}
