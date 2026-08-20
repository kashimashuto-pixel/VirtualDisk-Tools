namespace Qcow2Explorer.Core;

/// <summary>
/// Expands an lzop-compressed disk to an isolated temporary raw file so later
/// random reads do not repeatedly decompress LZO blocks.
/// </summary>
public sealed class TemporaryLzopDiskImageReader : IDiskImageReader
{
    private const int CopyBufferSize = 4 * 1024 * 1024;

    private readonly string _temporaryDirectory;
    private readonly RawDiskImageReader _rawReader;
    private readonly long _compressedLength;

    private TemporaryLzopDiskImageReader(
        string sourcePath,
        string temporaryDirectory,
        string temporaryPath,
        long compressedLength,
        RawDiskImageReader rawReader)
    {
        Path = sourcePath;
        _temporaryDirectory = temporaryDirectory;
        TemporaryPath = temporaryPath;
        _compressedLength = compressedLength;
        _rawReader = rawReader;
    }

    public string Path { get; }
    public string TemporaryPath { get; }
    public string FormatName => "raw/dd (lzop高速モード一時展開)";
    public long Length => _rawReader.Length;

    public static TemporaryLzopDiskImageReader Open(
        string path,
        IProgress<DiskImageProgress>? progress = null,
        string? temporaryRoot = null)
    {
        var sourcePath = System.IO.Path.GetFullPath(path);
        temporaryRoot = string.IsNullOrWhiteSpace(temporaryRoot)
            ? System.IO.Path.GetTempPath()
            : System.IO.Path.GetFullPath(temporaryRoot);
        Directory.CreateDirectory(temporaryRoot);
        var temporaryDirectory = System.IO.Path.Combine(
            temporaryRoot,
            $"VirtualDiskExplorer-lzo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        var temporaryPath = System.IO.Path.Combine(temporaryDirectory, "disk.raw");

        RawDiskImageReader? rawReader = null;
        try
        {
            using var lzop = new LzopDiskImageReader(sourcePath, progress);
            EnsureTemporarySpace(temporaryDirectory, lzop.Length);
            progress?.Report(new DiskImageProgress(
                "LZO高速モード: 一時RAWの領域を事前確保しています...",
                0,
                lzop.Length));

            using (var output = new FileStream(temporaryPath, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.Read,
                BufferSize = CopyBufferSize,
                Options = FileOptions.SequentialScan,
                PreallocationSize = lzop.Length
            }))
            {
                progress?.Report(new DiskImageProgress(
                    "LZO高速モード: 一時RAWの領域を確保しました。展開を開始します...",
                    0,
                    lzop.Length));
                var buffer = new byte[CopyBufferSize];
                long offset = 0;
                while (offset < lzop.Length)
                {
                    var count = (int)Math.Min(buffer.Length, lzop.Length - offset);
                    lzop.ReadAt(offset, buffer, 0, count);
                    output.Write(buffer, 0, count);
                    offset += count;
                    progress?.Report(new DiskImageProgress(
                        $"LZO高速モード: 一時RAWへ展開中 ({offset:N0} / {lzop.Length:N0} bytes)",
                        offset,
                        lzop.Length));
                }
            }

            rawReader = new RawDiskImageReader(temporaryPath, "raw/dd (lzop高速モード一時展開)");
            return new TemporaryLzopDiskImageReader(
                sourcePath,
                temporaryDirectory,
                temporaryPath,
                new FileInfo(sourcePath).Length,
                rawReader);
        }
        catch
        {
            rawReader?.Dispose();
            TryDeleteDirectory(temporaryDirectory);
            throw;
        }
    }

    public IReadOnlyList<KeyValuePair<string, string>> GetHeaderRows() =>
    [
        new("ファイル", Path),
        new("形式", FormatName),
        new("LZO読み込みモード", "高速（一時RAWへ全体展開）"),
        new("圧縮ファイルサイズ", $"{_compressedLength:N0} bytes"),
        new("仮想ディスクサイズ", $"{Length:N0} bytes"),
        new("一時RAW", TemporaryPath)
    ];

    public IReadOnlyList<string> GetWarnings() =>
    [
        "高速モードではLZO全体を一時RAWへ展開しています。一時ファイルはイメージを閉じると削除します。"
    ];

    public string DescribeOffset(long offset) => $"temporary raw offset 0x{offset:X}";

    public void ReadAt(long offset, byte[] buffer, int bufferOffset, int count) =>
        _rawReader.ReadAt(offset, buffer, bufferOffset, count);

    public void Dispose()
    {
        _rawReader.Dispose();
        TryDeleteDirectory(_temporaryDirectory);
    }

    private static void EnsureTemporarySpace(string temporaryDirectory, long requiredBytes)
    {
        var root = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(temporaryDirectory));
        if (string.IsNullOrWhiteSpace(root))
        {
            return;
        }

        var drive = new DriveInfo(root);
        if (drive.AvailableFreeSpace < requiredBytes)
        {
            throw new IOException(
                $"LZO高速モード用の一時領域が不足しています。必要: {requiredBytes:N0} bytes、空き: {drive.AvailableFreeSpace:N0} bytes。省容量モードで開き直してください。");
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
