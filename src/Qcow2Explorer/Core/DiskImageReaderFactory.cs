namespace Qcow2Explorer.Core;

public enum LzopOpenMode
{
    OnDemand,
    TemporaryRaw,
    CachedRaw,
    SavedRaw
}

public static class DiskImageReaderFactory
{
    public const string DialogFilter =
        "対応/検出ディスク (*.qcow2;*.qcow;*.vhd;*.vhdx;*.vmdk;*.vdi;*.ova;*.hdd;*.hds;*.vma;*.dd;*.img;*.raw;*.lzo;*.E01)|*.qcow2;*.qcow;*.vhd;*.vhdx;*.vmdk;*.vdi;*.ova;*.hdd;*.hds;*.vma;*.dd;*.img;*.raw;*.lzo;*.E01|All files (*.*)|*.*";

    public static IDiskImageReader Open(
        string path,
        IProgress<DiskImageProgress>? progress = null,
        LzopOpenMode lzopOpenMode = LzopOpenMode.OnDemand,
        string? lzopTemporaryDirectory = null,
        CancellationToken cancellationToken = default,
        bool overwriteSavedRaw = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (PhysicalDiskReader.IsPhysicalDiskPath(path))
        {
            return new PhysicalDiskReader(path);
        }

        if (Directory.Exists(path))
        {
            if (ParallelsHddReader.CanOpenDirectory(path))
            {
                return ParallelsHddReader.Open(path);
            }

            throw new NotSupportedException("フォルダ形式の仮想ディスクは Parallels .hdd の DiskDescriptor.xml がある場合のみ対応しています。");
        }

        if (path.EndsWith(".lzo", StringComparison.OrdinalIgnoreCase) || IsLzop(path))
        {
            IDiskImageReader lzop = lzopOpenMode switch
            {
                LzopOpenMode.TemporaryRaw => TemporaryLzopDiskImageReader.Open(
                    path,
                    progress,
                    lzopTemporaryDirectory,
                    cancellationToken),
                LzopOpenMode.CachedRaw => TemporaryLzopDiskImageReader.OpenCached(
                    path,
                    progress,
                    lzopTemporaryDirectory,
                    cancellationToken),
                LzopOpenMode.SavedRaw when !string.IsNullOrWhiteSpace(lzopTemporaryDirectory) =>
                    TemporaryLzopDiskImageReader.OpenSavedRaw(
                        path,
                        lzopTemporaryDirectory,
                        overwriteSavedRaw,
                        progress,
                        cancellationToken),
                LzopOpenMode.SavedRaw => throw new ArgumentException("RAW保存先を指定してください。", nameof(lzopTemporaryDirectory)),
                _ => new LzopDiskImageReader(path, progress, cancellationToken)
            };
            bool isVma;
            try
            {
                isVma = VmaDiskImageReader.HasMagic(lzop, cancellationToken);
            }
            catch
            {
                lzop.Dispose();
                throw;
            }

            return isVma ? new VmaDiskImageReader(lzop, progress, cancellationToken) : lzop;
        }

        if (IsVma(path))
        {
            return new VmaDiskImageReader(
                new RawDiskImageReader(path, "Proxmox VMA container"),
                progress,
                cancellationToken);
        }

        if (path.EndsWith(".ova", StringComparison.OrdinalIgnoreCase))
        {
            return OvaDiskImageReader.Open(path, progress, cancellationToken);
        }

        if (IsQcow2(path))
        {
            return new Qcow2Reader(path);
        }

        if (path.EndsWith(".E01", StringComparison.OrdinalIgnoreCase) || EwfDiskImageReader.HasMagic(path))
        {
            return EwfDiskImageReader.Open(path, cancellationToken);
        }

        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".vhd" or ".vhdx" or ".vmdk" or ".vdi" => DiscUtilsDiskImageReader.Open(path),
            ".hdd" or ".hds" => ParallelsHddReader.Open(path),
            ".dd" or ".img" or ".raw" => new RawDiskImageReader(path),
            _ => new RawDiskImageReader(path, "raw/dd (拡張子未判定)")
        };
    }

    public static bool IsLzopFile(string path)
    {
        if (PhysicalDiskReader.IsPhysicalDiskPath(path) || Directory.Exists(path))
        {
            return false;
        }

        return path.EndsWith(".lzo", StringComparison.OrdinalIgnoreCase)
            || (File.Exists(path) && IsLzop(path));
    }

    private static bool IsQcow2(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is ".qcow2" or ".qcow")
        {
            return true;
        }

        Span<byte> magic = stackalloc byte[4];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return stream.Read(magic) == 4 && magic[0] == (byte)'Q' && magic[1] == (byte)'F' && magic[2] == (byte)'I' && magic[3] == 0xfb;
    }

    private static bool IsLzop(string path)
    {
        Span<byte> magic = stackalloc byte[9];
        ReadOnlySpan<byte> expected = [0x89, 0x4c, 0x5a, 0x4f, 0x00, 0x0d, 0x0a, 0x1a, 0x0a];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return stream.Read(magic) == magic.Length
            && magic.SequenceEqual(expected);
    }

    private static bool IsVma(string path)
    {
        Span<byte> magic = stackalloc byte[4];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return stream.Read(magic) == magic.Length
            && magic.SequenceEqual("VMA\0"u8);
    }
}
