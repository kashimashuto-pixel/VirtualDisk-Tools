using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Qcow2Explorer.Core;

public sealed record LzopRawCacheEntry(
    string CacheId,
    string SourcePath,
    string RawPath,
    long SourceLength,
    long RawLength,
    long StoredBytes,
    bool Completed,
    bool SourceIsCurrent,
    bool IsUsable,
    DateTime CreatedUtc,
    DateTime LastUsedUtc)
{
    public string Status => IsUsable
        ? "利用可能"
        : !Completed
            ? "未完成"
            : !SourceIsCurrent
                ? "元LZO変更"
            : "破損";
}

public sealed record LzopIndexCacheEntry(
    string CacheId,
    string FileName,
    string? SourcePath,
    long StoredBytes,
    bool Completed,
    bool? SourceIsCurrent,
    DateTime LastUsedUtc)
{
    public string DisplayName => string.IsNullOrWhiteSpace(SourcePath)
        ? $"(元ファイル不明) {CacheId}"
        : SourcePath;

    public string Status => !Completed
        ? "未完成"
        : SourceIsCurrent == false
            ? "元LZO変更"
            : SourceIsCurrent == true
                ? "利用可能"
                : "保存済み";

    public bool IsUsable => Completed && SourceIsCurrent != false;
}

public static class LzopRawCacheManager
{
    internal const string MetadataFileName = "cache.json";
    internal const string RawFileName = "disk.raw";
    internal const string PartialRawFileName = "disk.raw.partial";
    internal const string IndexDirectoryName = "Index";
    internal const string IndexFileSuffix = ".lzop-index.br";
    internal const string IndexMetadataSuffix = ".lzop-index.json";

    private const int MetadataVersion = 1;
    private const int HashBufferSize = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string DefaultCacheRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VirtualDiskExplorer",
        "LzoCache");

    public static IReadOnlyList<LzopRawCacheEntry> GetEntries(string? cacheRoot = null)
    {
        var root = NormalizeRoot(cacheRoot);
        if (!Directory.Exists(root))
        {
            return Array.Empty<LzopRawCacheEntry>();
        }

        var entries = new List<LzopRawCacheEntry>();
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            if (string.Equals(Path.GetFileName(directory), IndexDirectoryName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var metadata = TryReadMetadata(directory);
            if (metadata is null)
            {
                var orphanRawPath = Path.Combine(directory, RawFileName);
                var orphanPartialPath = Path.Combine(directory, PartialRawFileName);
                var directoryInfo = new DirectoryInfo(directory);
                entries.Add(new LzopRawCacheEntry(
                    Path.GetFileName(directory),
                    "(メタデータなし)",
                    orphanRawPath,
                    0,
                    0,
                    GetFileLength(orphanRawPath) + GetFileLength(orphanPartialPath),
                    Completed: false,
                    SourceIsCurrent: false,
                    IsUsable: false,
                    directoryInfo.CreationTimeUtc,
                    directoryInfo.LastWriteTimeUtc));
                continue;
            }

            var rawPath = Path.Combine(directory, RawFileName);
            var partialPath = Path.Combine(directory, PartialRawFileName);
            var rawLength = GetFileLength(rawPath);
            var storedBytes = rawLength + GetFileLength(partialPath);
            var sourceIsCurrent = IsSourceCurrent(metadata);
            entries.Add(new LzopRawCacheEntry(
                Path.GetFileName(directory),
                metadata.SourcePath,
                rawPath,
                metadata.SourceLength,
                metadata.RawLength,
                storedBytes,
                metadata.Completed,
                sourceIsCurrent,
                metadata.Completed && sourceIsCurrent && rawLength == metadata.RawLength,
                metadata.CreatedUtc,
                metadata.LastUsedUtc));
        }

        return entries
            .OrderByDescending(entry => entry.LastUsedUtc)
            .ThenBy(entry => entry.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static IReadOnlyList<LzopIndexCacheEntry> GetIndexEntries(string? cacheRoot = null)
    {
        var indexRoot = Path.Combine(NormalizeRoot(cacheRoot), IndexDirectoryName);
        if (!Directory.Exists(indexRoot))
        {
            return Array.Empty<LzopIndexCacheEntry>();
        }

        var partialSuffix = IndexFileSuffix + ".partial";
        return Directory.EnumerateFiles(indexRoot)
            .Where(path =>
            {
                var fileName = Path.GetFileName(path);
                return fileName.EndsWith(IndexFileSuffix, StringComparison.OrdinalIgnoreCase)
                    || fileName.EndsWith(partialSuffix, StringComparison.OrdinalIgnoreCase);
            })
            .Select(path =>
            {
                var fileName = Path.GetFileName(path);
                var completed = fileName.EndsWith(IndexFileSuffix, StringComparison.OrdinalIgnoreCase);
                var cacheId = completed
                    ? fileName[..^IndexFileSuffix.Length]
                    : fileName.EndsWith(partialSuffix, StringComparison.OrdinalIgnoreCase)
                        ? fileName[..^partialSuffix.Length]
                        : fileName;
                var info = new FileInfo(path);
                var metadata = TryReadIndexMetadata(indexRoot, cacheId);
                return new LzopIndexCacheEntry(
                    cacheId,
                    fileName,
                    metadata?.SourcePath,
                    info.Length,
                    completed,
                    metadata is null ? null : IsIndexSourceCurrent(metadata),
                    metadata?.LastUsedUtc ?? info.LastWriteTimeUtc);
            })
            .OrderByDescending(entry => entry.LastUsedUtc)
            .ThenBy(entry => entry.CacheId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static void RecordIndexSource(string cachePath, string sourcePath)
    {
        try
        {
            var cacheFileName = Path.GetFileName(cachePath);
            if (!cacheFileName.EndsWith(IndexFileSuffix, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var cacheId = cacheFileName[..^IndexFileSuffix.Length];
            var source = new FileInfo(Path.GetFullPath(sourcePath));
            var metadata = new LzopIndexCacheMetadata
            {
                Version = 1,
                SourcePath = source.FullName,
                SourceLength = source.Length,
                SourceLastWriteUtcTicks = source.LastWriteTimeUtc.Ticks,
                LastUsedUtc = DateTime.UtcNow
            };
            var metadataPath = Path.Combine(Path.GetDirectoryName(cachePath)!, cacheId + IndexMetadataSuffix);
            var temporaryPath = metadataPath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(metadata, JsonOptions), new UTF8Encoding(false));
            File.Move(temporaryPath, metadataPath, overwrite: true);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // The index remains usable when its optional display metadata cannot be updated.
        }
    }

    public static bool TryDelete(string cacheId, string? cacheRoot, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(cacheId)
            || cacheId.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
        {
            error = "キャッシュIDが不正です。";
            return false;
        }

        string root;
        string directory;
        try
        {
            root = NormalizeRoot(cacheRoot);
            directory = Path.GetFullPath(Path.Combine(root, cacheId));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            error = $"キャッシュ保存先が不正です: {ex.Message}";
            return false;
        }

        if (!IsChildPath(root, directory))
        {
            error = "キャッシュ保存先の外は削除できません。";
            return false;
        }

        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"キャッシュを削除できませんでした: {ex.Message}";
            return false;
        }
    }

    public static bool TryDeleteIndex(string fileName, string? cacheRoot, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(fileName)
            || fileName.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
        {
            error = "索引キャッシュ名が不正です。";
            return false;
        }

        try
        {
            var indexRoot = Path.GetFullPath(Path.Combine(NormalizeRoot(cacheRoot), IndexDirectoryName));
            var path = Path.GetFullPath(Path.Combine(indexRoot, fileName));
            if (!IsChildPath(indexRoot, path))
            {
                error = "索引キャッシュ保存先の外は削除できません。";
                return false;
            }

            if (File.Exists(path))
            {
                File.Delete(path);
            }

            var cacheId = fileName.EndsWith(IndexFileSuffix, StringComparison.OrdinalIgnoreCase)
                ? fileName[..^IndexFileSuffix.Length]
                : fileName.EndsWith(IndexFileSuffix + ".partial", StringComparison.OrdinalIgnoreCase)
                    ? fileName[..^(IndexFileSuffix.Length + ".partial".Length)]
                    : null;
            if (cacheId is not null
                && !File.Exists(Path.Combine(indexRoot, cacheId + IndexFileSuffix))
                && !File.Exists(Path.Combine(indexRoot, cacheId + IndexFileSuffix + ".partial")))
            {
                var metadataPath = Path.Combine(indexRoot, cacheId + IndexMetadataSuffix);
                if (File.Exists(metadataPath))
                {
                    File.Delete(metadataPath);
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            error = $"索引キャッシュを削除できませんでした: {ex.Message}";
            return false;
        }
    }

    internal static string NormalizeRoot(string? cacheRoot)
    {
        var selected = string.IsNullOrWhiteSpace(cacheRoot) ? DefaultCacheRoot : cacheRoot;
        return Path.GetFullPath(selected);
    }

    internal static LzopSourceIdentity ReadSourceIdentity(
        string sourcePath,
        IProgress<DiskImageProgress>? progress,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(sourcePath);
        var before = new FileInfo(fullPath);
        var expectedLength = before.Length;
        var expectedWriteTime = before.LastWriteTimeUtc;
        progress?.Report(new DiskImageProgress(
            "LZO高速モード: キャッシュ識別用のSHA-256を計算中...",
            0,
            expectedLength));

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using (var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var buffer = new byte[HashBufferSize];
            long total = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    break;
                }

                hash.AppendData(buffer, 0, read);
                total += read;
                progress?.Report(new DiskImageProgress(
                    $"LZO高速モード: キャッシュ識別中 ({total:N0} / {expectedLength:N0} bytes)",
                    total,
                    expectedLength));
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        before.Refresh();
        if (before.Length != expectedLength || before.LastWriteTimeUtc != expectedWriteTime)
        {
            throw new IOException("キャッシュ識別中に元LZOファイルが変更されました。もう一度開いてください。");
        }

        return new LzopSourceIdentity(
            fullPath,
            expectedLength,
            expectedWriteTime.Ticks,
            Convert.ToHexString(hash.GetHashAndReset()));
    }

    internal static string GetCacheDirectory(string root, string sourcePath)
    {
        var normalizedPath = Path.GetFullPath(sourcePath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
        var cacheId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath))).ToLowerInvariant();
        return Path.Combine(root, cacheId);
    }

    internal static LzopCacheMetadata? TryGetUsableMetadata(string directory, LzopSourceIdentity source)
    {
        var metadata = TryReadMetadata(directory);
        if (metadata is null
            || metadata.Version != MetadataVersion
            || !metadata.Completed
            || !string.Equals(metadata.SourcePath, source.Path, StringComparison.OrdinalIgnoreCase)
            || metadata.SourceLength != source.Length
            || metadata.SourceLastWriteUtcTicks != source.LastWriteUtcTicks
            || !string.Equals(metadata.SourceSha256, source.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var rawPath = Path.Combine(directory, RawFileName);
        return File.Exists(rawPath) && new FileInfo(rawPath).Length == metadata.RawLength
            ? metadata
            : null;
    }

    internal static LzopCacheMetadata CreateIncompleteMetadata(LzopSourceIdentity source, long rawLength)
    {
        var now = DateTime.UtcNow;
        return new LzopCacheMetadata
        {
            Version = MetadataVersion,
            SourcePath = source.Path,
            SourceLength = source.Length,
            SourceLastWriteUtcTicks = source.LastWriteUtcTicks,
            SourceSha256 = source.Sha256,
            RawLength = rawLength,
            Completed = false,
            CreatedUtc = now,
            LastUsedUtc = now
        };
    }

    internal static void WriteMetadata(string directory, LzopCacheMetadata metadata)
    {
        Directory.CreateDirectory(directory);
        var metadataPath = Path.Combine(directory, MetadataFileName);
        var temporaryPath = metadataPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(metadata, JsonOptions), new UTF8Encoding(false));
        File.Move(temporaryPath, metadataPath, overwrite: true);
    }

    internal static void TouchMetadata(string directory, LzopCacheMetadata metadata)
    {
        metadata.LastUsedUtc = DateTime.UtcNow;
        try
        {
            WriteMetadata(directory, metadata);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cache reuse itself remains safe if only the last-used timestamp cannot be updated.
        }
    }

    internal static void DeleteDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static LzopCacheMetadata? TryReadMetadata(string directory)
    {
        try
        {
            var path = Path.Combine(directory, MetadataFileName);
            return File.Exists(path)
                ? JsonSerializer.Deserialize<LzopCacheMetadata>(File.ReadAllText(path))
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static LzopIndexCacheMetadata? TryReadIndexMetadata(string indexRoot, string cacheId)
    {
        try
        {
            var path = Path.Combine(indexRoot, cacheId + IndexMetadataSuffix);
            if (!File.Exists(path))
            {
                return null;
            }

            var metadata = JsonSerializer.Deserialize<LzopIndexCacheMetadata>(File.ReadAllText(path));
            return metadata is { Version: 1 } ? metadata : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static long GetFileLength(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;

    private static bool IsSourceCurrent(LzopCacheMetadata metadata)
    {
        try
        {
            var source = new FileInfo(metadata.SourcePath);
            return source.Exists
                && source.Length == metadata.SourceLength
                && source.LastWriteTimeUtc.Ticks == metadata.SourceLastWriteUtcTicks;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsIndexSourceCurrent(LzopIndexCacheMetadata metadata)
    {
        try
        {
            var source = new FileInfo(metadata.SourcePath);
            return source.Exists
                && source.Length == metadata.SourceLength
                && source.LastWriteTimeUtc.Ticks == metadata.SourceLastWriteUtcTicks;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsChildPath(string root, string candidate)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return candidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class LzopCacheMetadata
{
    public int Version { get; set; }
    public string SourcePath { get; set; } = "";
    public long SourceLength { get; set; }
    public long SourceLastWriteUtcTicks { get; set; }
    public string SourceSha256 { get; set; } = "";
    public long RawLength { get; set; }
    public bool Completed { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime LastUsedUtc { get; set; }
}

internal sealed class LzopIndexCacheMetadata
{
    public int Version { get; set; }
    public string SourcePath { get; set; } = "";
    public long SourceLength { get; set; }
    public long SourceLastWriteUtcTicks { get; set; }
    public DateTime LastUsedUtc { get; set; }
}

internal sealed record LzopSourceIdentity(
    string Path,
    long Length,
    long LastWriteUtcTicks,
    string Sha256);
