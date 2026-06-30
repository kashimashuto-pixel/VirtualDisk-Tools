namespace Qcow2Explorer.Core;

public static class DiskImageReaderFactory
{
    public const string DialogFilter =
        "対応/検出ディスク (*.qcow2;*.qcow;*.vhd;*.vhdx;*.vmdk;*.vdi;*.hdd;*.hds;*.dd;*.img;*.raw;*.dd.lzo)|*.qcow2;*.qcow;*.vhd;*.vhdx;*.vmdk;*.vdi;*.hdd;*.hds;*.dd;*.img;*.raw;*.dd.lzo|All files (*.*)|*.*";

    public static IDiskImageReader Open(string path)
    {
        if (Directory.Exists(path))
        {
            if (ParallelsHddReader.CanOpenDirectory(path))
            {
                return ParallelsHddReader.Open(path);
            }

            throw new NotSupportedException("フォルダ形式の仮想ディスクは Parallels .hdd の DiskDescriptor.xml がある場合のみ対応しています。");
        }

        var lower = path.ToLowerInvariant();
        if (lower.EndsWith(".dd.lzo", StringComparison.Ordinal))
        {
            throw new NotSupportedException(
                ".dd.lzo は LZO/lzop 形式の展開処理が必要ですが、現在は内部展開まで未対応です。先に .dd/.img/.raw へ展開したファイルを開いてください。");
        }

        if (IsQcow2(path))
        {
            return new Qcow2Reader(path);
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
}
