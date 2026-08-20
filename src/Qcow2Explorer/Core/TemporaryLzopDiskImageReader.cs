namespace Qcow2Explorer.Core;

/// <summary>
/// Expands an lzop-compressed disk to raw storage so later random reads do not
/// repeatedly decompress LZO blocks. The raw file can be temporary, cached, or
/// retained at a user-selected path.
/// </summary>
public sealed class TemporaryLzopDiskImageReader : IDiskImageReader
{
    private const int CopyBufferSize = 4 * 1024 * 1024;

    private readonly string? _deleteDirectoryOnDispose;
    private readonly RawDiskImageReader _rawReader;
    private readonly long _compressedLength;
    private readonly string _modeDescription;
    private readonly string _storageLabel;
    private readonly string _warning;
    private bool _disposed;

    private TemporaryLzopDiskImageReader(
        string sourcePath,
        string rawPath,
        long compressedLength,
        RawDiskImageReader rawReader,
        string modeDescription,
        string storageLabel,
        string warning,
        string? deleteDirectoryOnDispose,
        bool cacheReused)
    {
        Path = sourcePath;
        TemporaryPath = rawPath;
        _compressedLength = compressedLength;
        _rawReader = rawReader;
        _modeDescription = modeDescription;
        _storageLabel = storageLabel;
        _warning = warning;
        _deleteDirectoryOnDispose = deleteDirectoryOnDispose;
        CacheReused = cacheReused;
    }

    public string Path { get; }
    public string TemporaryPath { get; }
    public bool CacheReused { get; }
    public bool IsPersistent => _deleteDirectoryOnDispose is null;
    public string FormatName => $"raw/dd (lzop{_modeDescription})";
    public long Length => _rawReader.Length;

    public static TemporaryLzopDiskImageReader Open(
        string path,
        IProgress<DiskImageProgress>? progress = null,
        string? temporaryRoot = null,
        CancellationToken cancellationToken = default)
    {
        var sourcePath = System.IO.Path.GetFullPath(path);
        var root = string.IsNullOrWhiteSpace(temporaryRoot)
            ? System.IO.Path.GetTempPath()
            : System.IO.Path.GetFullPath(temporaryRoot);
        Directory.CreateDirectory(root);
        var temporaryDirectory = System.IO.Path.Combine(
            root,
            $"VirtualDiskExplorer-lzo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        var temporaryPath = System.IO.Path.Combine(temporaryDirectory, "disk.raw");

        RawDiskImageReader? rawReader = null;
        try
        {
            using var lzop = new LzopDiskImageReader(sourcePath, progress, cancellationToken);
            EnsureStorageSpace(temporaryDirectory, lzop.Length);
            ExpandToRaw(lzop, temporaryPath, progress, cancellationToken, "一時RAW");
            cancellationToken.ThrowIfCancellationRequested();
            rawReader = new RawDiskImageReader(temporaryPath, "raw/dd (lzop高速モード一時展開)");
            return new TemporaryLzopDiskImageReader(
                sourcePath,
                temporaryPath,
                new FileInfo(sourcePath).Length,
                rawReader,
                "高速モード一時展開",
                "一時RAW",
                "高速モードの一時RAWは、イメージを閉じると削除します。",
                temporaryDirectory,
                cacheReused: false);
        }
        catch
        {
            rawReader?.Dispose();
            TryDeleteDirectory(temporaryDirectory);
            throw;
        }
    }

    public static TemporaryLzopDiskImageReader OpenCached(
        string path,
        IProgress<DiskImageProgress>? progress = null,
        string? cacheRoot = null,
        CancellationToken cancellationToken = default)
    {
        var sourcePath = System.IO.Path.GetFullPath(path);
        var root = LzopRawCacheManager.NormalizeRoot(cacheRoot);
        Directory.CreateDirectory(root);
        var source = LzopRawCacheManager.ReadSourceIdentity(sourcePath, progress, cancellationToken);
        var cacheDirectory = LzopRawCacheManager.GetCacheDirectory(root, sourcePath);
        var rawPath = System.IO.Path.Combine(cacheDirectory, LzopRawCacheManager.RawFileName);
        var cachedMetadata = LzopRawCacheManager.TryGetUsableMetadata(cacheDirectory, source);
        if (cachedMetadata is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new DiskImageProgress(
                "LZO高速モード: 検証済みRAWキャッシュを再利用します。",
                cachedMetadata.RawLength,
                cachedMetadata.RawLength));
            var cachedReader = new RawDiskImageReader(rawPath, "raw/dd (lzop高速モードキャッシュ再利用)");
            LzopRawCacheManager.TouchMetadata(cacheDirectory, cachedMetadata);
            return new TemporaryLzopDiskImageReader(
                sourcePath,
                rawPath,
                source.Length,
                cachedReader,
                "高速モードキャッシュ再利用",
                "RAWキャッシュ",
                "検証済みのLZO高速モードRAWキャッシュを再利用しています。",
                deleteDirectoryOnDispose: null,
                cacheReused: true);
        }

        if (Directory.Exists(cacheDirectory))
        {
            progress?.Report(new DiskImageProgress("LZO高速モード: 古い、未完成、または破損したキャッシュを削除します..."));
            LzopRawCacheManager.DeleteDirectory(cacheDirectory);
        }

        Directory.CreateDirectory(cacheDirectory);
        RawDiskImageReader? rawReader = null;
        try
        {
            using var lzop = new LzopDiskImageReader(sourcePath, progress, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            EnsureStorageSpace(cacheDirectory, lzop.Length);
            var metadata = LzopRawCacheManager.CreateIncompleteMetadata(source, lzop.Length);
            LzopRawCacheManager.WriteMetadata(cacheDirectory, metadata);
            var partialPath = System.IO.Path.Combine(cacheDirectory, LzopRawCacheManager.PartialRawFileName);
            ExpandToRaw(lzop, partialPath, progress, cancellationToken, "RAWキャッシュ");
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(partialPath, rawPath);
            metadata.Completed = true;
            metadata.LastUsedUtc = DateTime.UtcNow;
            LzopRawCacheManager.WriteMetadata(cacheDirectory, metadata);
            rawReader = new RawDiskImageReader(rawPath, "raw/dd (lzop高速モードキャッシュ)");
            return new TemporaryLzopDiskImageReader(
                sourcePath,
                rawPath,
                source.Length,
                rawReader,
                "高速モードキャッシュ",
                "RAWキャッシュ",
                "LZO高速モードのRAWキャッシュを保持し、次回の読み込みで検証後に再利用します。",
                deleteDirectoryOnDispose: null,
                cacheReused: false);
        }
        catch
        {
            rawReader?.Dispose();
            TryDeleteDirectory(cacheDirectory);
            throw;
        }
    }

    public static TemporaryLzopDiskImageReader OpenSavedRaw(
        string path,
        string outputPath,
        bool overwrite,
        IProgress<DiskImageProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var sourcePath = System.IO.Path.GetFullPath(path);
        var rawPath = System.IO.Path.GetFullPath(outputPath);
        if (string.Equals(sourcePath, rawPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("元LZOファイルと同じ場所へRAWを保存できません。");
        }

        var outputDirectory = System.IO.Path.GetDirectoryName(rawPath)
            ?? throw new IOException("RAW保存先フォルダーを判定できません。");
        Directory.CreateDirectory(outputDirectory);
        if (File.Exists(rawPath) && !overwrite)
        {
            throw new IOException("RAW保存先に同名ファイルがあります。上書きを確認してから再実行してください。");
        }

        var partialPath = rawPath + $".VirtualDiskExplorer-partial-{Guid.NewGuid():N}";
        RawDiskImageReader? rawReader = null;
        try
        {
            using var lzop = new LzopDiskImageReader(sourcePath, progress, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            EnsureStorageSpace(outputDirectory, lzop.Length);
            ExpandToRaw(lzop, partialPath, progress, cancellationToken, "指定RAW");
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(partialPath, rawPath, overwrite);
            rawReader = new RawDiskImageReader(rawPath, "raw/dd (lzop高速モード指定RAW)");
            return new TemporaryLzopDiskImageReader(
                sourcePath,
                rawPath,
                new FileInfo(sourcePath).Length,
                rawReader,
                "高速モード指定RAW",
                "保存RAW",
                "展開したRAWは指定場所に保持します。不要になった場合は手動で削除してください。",
                deleteDirectoryOnDispose: null,
                cacheReused: false);
        }
        catch
        {
            rawReader?.Dispose();
            TryDeleteFile(partialPath);
            throw;
        }
    }

    public IReadOnlyList<KeyValuePair<string, string>> GetHeaderRows() =>
    [
        new("ファイル", Path),
        new("形式", FormatName),
        new("LZO読み込みモード", _modeDescription),
        new("圧縮ファイルサイズ", $"{_compressedLength:N0} bytes"),
        new("仮想ディスクサイズ", $"{Length:N0} bytes"),
        new(_storageLabel, TemporaryPath),
        new("キャッシュ再利用", CacheReused ? "はい" : "いいえ")
    ];

    public IReadOnlyList<string> GetWarnings() => [_warning];

    public string DescribeOffset(long offset) => $"expanded raw offset 0x{offset:X}";

    public void ReadAt(long offset, byte[] buffer, int bufferOffset, int count) =>
        _rawReader.ReadAt(offset, buffer, bufferOffset, count);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _rawReader.Dispose();
        if (_deleteDirectoryOnDispose is not null)
        {
            TryDeleteDirectory(_deleteDirectoryOnDispose);
        }
    }

    private static void ExpandToRaw(
        LzopDiskImageReader lzop,
        string outputPath,
        IProgress<DiskImageProgress>? progress,
        CancellationToken cancellationToken,
        string targetName)
    {
        progress?.Report(new DiskImageProgress(
            $"LZO高速モード: {targetName}の領域を事前確保しています...",
            0,
            lzop.Length));
        using var output = new FileStream(outputPath, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.Read,
            BufferSize = CopyBufferSize,
            Options = FileOptions.SequentialScan,
            PreallocationSize = lzop.Length
        });
        progress?.Report(new DiskImageProgress(
            $"LZO高速モード: {targetName}の領域を確保しました。展開を開始します...",
            0,
            lzop.Length));
        var buffer = new byte[CopyBufferSize];
        long offset = 0;
        while (offset < lzop.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = (int)Math.Min(buffer.Length, lzop.Length - offset);
            lzop.ReadAt(offset, buffer, 0, count);
            cancellationToken.ThrowIfCancellationRequested();
            output.Write(buffer, 0, count);
            offset += count;
            progress?.Report(new DiskImageProgress(
                $"LZO高速モード: {targetName}へ展開中 ({offset:N0} / {lzop.Length:N0} bytes)",
                offset,
                lzop.Length));
        }

        output.Flush(flushToDisk: true);
    }

    private static void EnsureStorageSpace(string directory, long requiredBytes)
    {
        var root = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(directory));
        if (string.IsNullOrWhiteSpace(root))
        {
            return;
        }

        var drive = new DriveInfo(root);
        if (drive.AvailableFreeSpace < requiredBytes)
        {
            throw new IOException(
                $"LZO高速モード用の領域が不足しています。必要: {requiredBytes:N0} bytes、空き: {drive.AvailableFreeSpace:N0} bytes。省容量モードで開き直してください。");
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

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
