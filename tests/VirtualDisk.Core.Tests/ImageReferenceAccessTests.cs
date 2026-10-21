using System.Buffers.Binary;
using System.Formats.Tar;
using System.Text;
using DiscUtils.Streams;
using Qcow2Explorer.Core;

internal static class ImageReferenceAccessTests
{
    public static void Run(string temporaryDirectory)
    {
        var root = Path.Combine(temporaryDirectory, "reference-access");
        var images = Path.Combine(root, "images");
        var host = Path.Combine(root, "host");
        Directory.CreateDirectory(images);
        Directory.CreateDirectory(host);
        var marker = Enumerable.Repeat((byte)0x5a, 512).ToArray();
        var outside = Path.Combine(host, "marker.raw");
        File.WriteAllBytes(outside, marker);
        var top = Path.Combine(images, "disk.qcow2");

        foreach (var reference in new[] { outside, Path.Combine("..", "host", "marker.raw") })
        {
            File.WriteAllBytes(top, BackedQcow(reference));
            var blocked = Denied(() => { using var reader = DiskImageReaderFactory.Open(top); });
            Assert(blocked.CanSelectFile, "local outside reference can be selected explicitly");
            var policy = ImageFileAccessPolicy.ForImage(top);
            policy.SelectFile(blocked.ReferencePath, outside);
            using (var reader = DiskImageReaderFactory.Open(top, fileAccessPolicy: policy))
            {
                var bytes = new byte[512];
                reader.ReadAt(0, bytes, 0, bytes.Length);
                Assert(bytes.SequenceEqual(marker), "explicitly selected backing file content");
            }
            Denied(() => { using var reader = new Qcow2Reader(top); });
            Assert(policy.ResolveFile(outside) == outside, "selected target can be reopened recursively");
            var sibling = Path.Combine(host, "sibling.raw");
            File.WriteAllBytes(sibling, marker);
            Denied(() => policy.ResolveFile(sibling));
        }

        // Selecting a replacement does not grant access to the original file or its containing directory.
        var requested = Path.Combine(host, "absent.raw");
        File.WriteAllBytes(top, BackedQcow(requested));
        var replacementPolicy = ImageFileAccessPolicy.ForImage(top);
        replacementPolicy.SelectFile(requested, outside);
        using (var reader = new Qcow2Reader(top, fileAccessPolicy: replacementPolicy))
        {
            var bytes = new byte[512];
            reader.ReadAt(0, bytes, 0, 512);
            Assert(bytes.SequenceEqual(marker), "replacement backing path supported");
        }

        // Regular companion files in the chosen directory continue to work without an approval.
        var local = Path.Combine(images, "local.raw");
        File.WriteAllBytes(local, marker);
        File.WriteAllBytes(top, BackedQcow("local.raw"));
        using (var reader = new Qcow2Reader(top))
        {
            var bytes = new byte[512];
            reader.ReadAt(0, bytes, 0, 512);
            Assert(bytes.SequenceEqual(marker), "same-directory backing retained");
        }

        foreach (var reference in new[] { @"\\127.0.0.1\review-never-connect\disk.raw", @"\\?\C:\review.raw", @"\\.\PhysicalDrive0" })
        {
            File.WriteAllBytes(top, BackedQcow(reference));
            Assert(!Denied(() => { using var reader = new Qcow2Reader(top); }).CanSelectFile,
                "network/device backing rejected without filesystem probes");
        }
        foreach (var path in new[] { @"\\127.0.0.1\review-never-connect\disk.raw", "https://example.invalid/disk.raw" })
        {
            Assert(!Denied(() => ImageFileAccessPolicy.ForImage(path)).CanSelectFile, "non-local input rejected");
        }
        if (OperatingSystem.IsWindows())
        {
            foreach (var reference in new[] { "NUL", "CON.txt", "COM1", "local.raw:stream", "C:relative.raw" })
            {
                Assert(!Denied(() => ImageFileAccessPolicy.ForImage(Path.Combine(images, reference))).CanSelectFile,
                    "Windows device/ADS path rejected");
            }
        }

        File.WriteAllBytes(top, ExternalDataQcow(outside));
        var externalBlocked = Denied(() => { using var reader = new Qcow2Reader(top); });
        var externalPolicy = ImageFileAccessPolicy.ForImage(top);
        externalPolicy.SelectFile(externalBlocked.ReferencePath, outside);
        using (var reader = new Qcow2Reader(top, fileAccessPolicy: externalPolicy))
        {
            Assert(reader.Header.UsesExternalDataFile, "selected external data file can be opened");
        }

        var secondOutside = Path.Combine(host, "second.raw");
        File.WriteAllBytes(secondOutside, marker);
        var externalParent = Path.Combine(host, "parent.qcow2");
        File.WriteAllBytes(externalParent, BackedQcow("second.raw"));
        File.WriteAllBytes(top, BackedQcow(externalParent));
        var chainPolicy = ImageFileAccessPolicy.ForImage(top);
        chainPolicy.SelectFile(externalParent, externalParent);
        var chainBlocked = Denied(() => { using var reader = new Qcow2Reader(top, fileAccessPolicy: chainPolicy); });
        Assert(chainBlocked.ReferencePath == secondOutside, "selected parent does not expand the allowed directory");

        var bundle = Path.Combine(images, "test.hdd");
        Directory.CreateDirectory(bundle);
        File.WriteAllText(Path.Combine(bundle, "DiskDescriptor.xml"),
            "<Parallels_disk_image><Disk_Parameters><Disk_size>1</Disk_size></Disk_Parameters>"
            + "<StorageData><Storage><Start>0</Start><End>1</End>"
            + $"<Image><Type>Plain</Type><File>{System.Security.SecurityElement.Escape(outside)}</File></Image>"
            + "</Storage></StorageData></Parallels_disk_image>");
        Denied(() => { using var reader = ParallelsHddReader.Open(bundle); });

        // An OVA must not escape its extracted directory, even if the caller approved that host file.
        var archive = Path.Combine(images, "outside.ova");
        WriteOva(archive, ("disk.qcow2", BackedQcow(outside)));
        var ovaPolicy = ImageFileAccessPolicy.ForImage(archive);
        ovaPolicy.SelectFile(outside, outside);
        Assert(!Denied(() => { using var reader = DiskImageReaderFactory.Open(archive, fileAccessPolicy: ovaPolicy); }).CanSelectFile,
            "OVA restriction cannot be overridden by host selections");
        var validArchive = Path.Combine(images, "inside.ova");
        WriteOva(validArchive, ("disk.qcow2", BackedQcow("base.raw")), ("base.raw", marker));
        using (var reader = OvaDiskImageReader.Open(validArchive))
        {
            var bytes = new byte[512];
            reader.ReadAt(0, bytes, 0, 512);
            Assert(bytes.SequenceEqual(marker), "OVA-contained backing retained");
        }

        TestVmdk(images, outside, marker);
        TestVhdx(images, host);

        var link = Path.Combine(images, "linked-host");
        try
        {
            Directory.CreateSymbolicLink(link, host);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Console.WriteLine("ImageReferenceAccessTests: symlink creation not permitted; junction smoke is documented separately.");
            return;
        }
        try
        {
            Assert(!Denied(() => ImageFileAccessPolicy.ForImage(top).ResolveFile(Path.Combine(link, "marker.raw"))).CanSelectFile,
                "symlink path rejected");
        }
        finally { Directory.Delete(link); }
        Console.WriteLine("ImageReferenceAccessTests: policy, QCOW2, external data, OVA, VMDK, VHDX and symlink checks passed.");
    }

    private static void TestVmdk(string images, string outside, byte[] marker)
    {
        var descriptor = Path.Combine(images, "external.vmdk");
        File.WriteAllText(descriptor, "# Disk DescriptorFile\nversion=1\nCID=fffffffe\nparentCID=ffffffff\ncreateType=\"monolithicFlat\"\n"
            + $"RW 1 FLAT \"{outside.Replace('\\', '/')}\" 0\n");
        var policy = ImageFileAccessPolicy.ForImage(descriptor);
        var blocked = Denied(() => { using var reader = DiskImageReaderFactory.Open(descriptor); });
        policy.SelectFile(blocked.ReferencePath, outside);
        using var accepted = DiskImageReaderFactory.Open(descriptor, fileAccessPolicy: policy);
        var bytes = new byte[512];
        accepted.ReadAt(0, bytes, 0, 512);
        Assert(bytes.SequenceEqual(marker), "explicit VMDK extent retained");
    }

    private static void TestVhdx(string images, string host)
    {
        var parentPath = Path.Combine(host, "parent.vhdx");
        var childPath = Path.Combine(images, "child.avhdx");
        using (var stream = File.Create(parentPath))
        using (var disk = DiscUtils.Vhdx.Disk.InitializeDynamic(stream, Ownership.None, 16L * 1024 * 1024))
        {
            disk.Content.Position = 0;
            disk.Content.WriteByte(0x73);
        }
        using (var parent = new DiscUtils.Vhdx.Disk(parentPath, FileAccess.Read))
        using (var child = parent.CreateDifferencingDisk(childPath)) { }
        var policy = ImageFileAccessPolicy.ForImage(childPath);
        var blocked = Denied(() => { using var reader = DiskImageReaderFactory.Open(childPath); });
        policy.SelectFile(blocked.ReferencePath, parentPath);
        using (var accepted = DiskImageReaderFactory.Open(childPath, fileAccessPolicy: policy))
        {
            var bytes = new byte[1];
            accepted.ReadAt(0, bytes, 0, 1);
            Assert(bytes[0] == 0x73, "explicit VHDX parent retained");
        }
        // Rejected constructors must release the child handle, so callers can retry or delete it.
        File.Move(childPath, childPath + ".checked");
        File.Move(childPath + ".checked", childPath);
    }

    internal static byte[] BackedQcow(string? backing = null)
    {
        var bytes = new byte[1024];
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0), 0x514649fb);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4), 3);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), 9);
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(24), 512);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(36), 1);
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(40), 512);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(96), 4);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(100), 104);
        if (backing is not null)
        {
            var name = Encoding.UTF8.GetBytes(backing);
            BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(8), 104);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), (uint)name.Length);
            name.CopyTo(bytes, 104);
        }
        return bytes;
    }

    private static byte[] ExternalDataQcow(string path)
    {
        var bytes = BackedQcow();
        var name = Encoding.UTF8.GetBytes(path);
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(72), 4);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(104), 0x44415441);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(108), (uint)name.Length);
        name.CopyTo(bytes, 112);
        return bytes;
    }

    private static void WriteOva(string path, params (string Name, byte[] Data)[] files)
    {
        using var stream = File.Create(path);
        using var writer = new TarWriter(stream);
        foreach (var file in files)
        {
            using var body = new MemoryStream(file.Data);
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, file.Name) { DataStream = body });
        }
    }

    private static ImageReferenceAccessException Denied(Action action)
    {
        try { action(); }
        catch (ImageReferenceAccessException denied) { return denied; }
        throw new InvalidOperationException("Expected the image reference to be blocked.");
    }
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("ImageReferenceAccessTests: " + message);
    }
}
