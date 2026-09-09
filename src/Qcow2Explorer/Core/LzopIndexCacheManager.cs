using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Qcow2Explorer.Core;

public sealed record LzopIndexCacheEntry(
    string CacheId,
    string SourcePath,
    string CachePath,
    long SourceLength,
    long RawLength,
    long StoredBytes,
    bool SourceIsCurrent,
    bool IsUsable,
    DateTime CreatedUtc,
    DateTime LastUsedUtc,
    string Error)
{
    public string Status => IsUsable
        ? "利用可能"
        : !string.IsNullOrWhiteSpace(Error)
            ? "破損・旧形式"
            : "元LZO変更";
}

public static class LzopIndexCacheManager
{
    internal const int Version = 3;
    internal const int FingerprintLength = 4096;
    internal const int HashLength = 32;
    private const int MaximumBlockCount = 4_194_304;
    private const int MaximumBlockSize = 64 * 1024 * 1024;
    internal const string FileSuffix = ".lzop-index.br";
    internal static readonly byte[] Magic = "VDLZOIDX"u8.ToArray();

    public static string DefaultIndexRoot => Path.Combine(LzopRawCacheManager.DefaultCacheRoot, "Index");

    public static IReadOnlyList<LzopIndexCacheEntry> GetEntries()
    {
        var root = Path.GetFullPath(DefaultIndexRoot);
        if (!Directory.Exists(root))
        {
            return Array.Empty<LzopIndexCacheEntry>();
        }

        var entries = new List<LzopIndexCacheEntry>();
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly))
        {
            var file = new FileInfo(path);
            var cacheId = file.Name;
            try
            {
                var header = ReadHeader(path);
                var sourceIsCurrent = IsSourceCurrent(header);
                entries.Add(new LzopIndexCacheEntry(
                    cacheId,
                    header.SourcePath,
                    file.FullName,
                    header.SourceLength,
                    header.RawLength,
                    file.Length,
                    sourceIsCurrent,
                    sourceIsCurrent,
                    file.CreationTimeUtc,
                    file.LastAccessTimeUtc,
                    string.Empty));
            }
            catch (Exception ex) when (ex is IOException
                                       or InvalidDataException
                                       or UnauthorizedAccessException
                                       or ArgumentException
                                       or NotSupportedException
                                       or OverflowException)
            {
                entries.Add(new LzopIndexCacheEntry(
                    cacheId,
                    "(索引メタデータを読み取れません)",
                    file.FullName,
                    0,
                    0,
                    file.Length,
                    SourceIsCurrent: false,
                    IsUsable: false,
                    file.CreationTimeUtc,
                    file.LastAccessTimeUtc,
                    ex.Message));
            }
        }

        return entries
            .OrderByDescending(entry => entry.LastUsedUtc)
            .ThenBy(entry => entry.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static bool TryDelete(string cacheId, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(cacheId)
            || !string.Equals(cacheId, Path.GetFileName(cacheId), StringComparison.Ordinal)
            || (!cacheId.EndsWith(FileSuffix, StringComparison.OrdinalIgnoreCase)
                && !cacheId.EndsWith(FileSuffix + ".partial", StringComparison.OrdinalIgnoreCase)))
        {
            error = "索引キャッシュIDが不正です。";
            return false;
        }

        var root = Path.GetFullPath(DefaultIndexRoot);
        var path = Path.GetFullPath(Path.Combine(root, cacheId));
        var rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            error = "索引キャッシュ保存先の外は削除できません。";
            return false;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            error = $"索引キャッシュを削除できませんでした: {ex.Message}";
            return false;
        }
    }

    internal static string GetCachePath(string sourcePath)
    {
        var normalizedPath = Path.GetFullPath(sourcePath).ToUpperInvariant();
        var cacheId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath))).ToLowerInvariant();
        return Path.Combine(DefaultIndexRoot, cacheId + FileSuffix);
    }

    internal static LzopIndexCacheHeader ReadHeader(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var compressed = new BrotliStream(file, CompressionMode.Decompress);
        using var reader = new BinaryReader(compressed, Encoding.UTF8, leaveOpen: false);
        if (!reader.ReadBytes(Magic.Length).SequenceEqual(Magic))
        {
            throw new InvalidDataException("LZO索引キャッシュの識別子が一致しません。");
        }

        if (reader.ReadInt32() != Version)
        {
            throw new InvalidDataException("LZO索引キャッシュは旧形式または未対応バージョンです。");
        }

        var sourcePath = reader.ReadString();
        if (string.IsNullOrWhiteSpace(sourcePath) || sourcePath.Length > 32_768)
        {
            throw new InvalidDataException("LZO索引キャッシュの元ファイルパスが不正です。");
        }

        var sourceLength = reader.ReadInt64();
        var sourceWriteTicks = reader.ReadInt64();
        var fingerprint = reader.ReadBytes(HashLength);
        var rawLength = reader.ReadInt64();
        var blockCount = reader.ReadInt32();
        if (sourceLength <= 0
            || sourceWriteTicks <= 0
            || fingerprint.Length != HashLength
            || rawLength <= 0
            || blockCount <= 0
            || blockCount > MaximumBlockCount
            || blockCount > Math.Max(1, sourceLength / (sizeof(uint) * 2)))
        {
            throw new InvalidDataException("LZO索引キャッシュの元ファイル情報が不正です。");
        }

        long expectedRawOffset = 0;
        long previousCompressedEnd = 0;
        for (var index = 0; index < blockCount; index++)
        {
            var uncompressedOffset = reader.ReadInt64();
            var uncompressedSize = reader.ReadInt32();
            var compressedSize = reader.ReadInt32();
            var dataOffset = reader.ReadInt64();
            ReadNullableUInt32(reader);
            ReadNullableUInt32(reader);
            ReadNullableUInt32(reader);
            ReadNullableUInt32(reader);
            if (uncompressedOffset != expectedRawOffset
                || uncompressedSize <= 0
                || uncompressedSize > MaximumBlockSize
                || compressedSize <= 0
                || compressedSize > uncompressedSize
                || dataOffset < previousCompressedEnd
                || dataOffset > sourceLength - compressedSize)
            {
                throw new InvalidDataException($"LZO索引キャッシュのブロック#{index}が不正です。");
            }

            expectedRawOffset = checked(expectedRawOffset + uncompressedSize);
            previousCompressedEnd = checked(dataOffset + compressedSize);
        }

        if (expectedRawOffset != rawLength || reader.BaseStream.ReadByte() != -1)
        {
            throw new InvalidDataException("LZO索引キャッシュが元RAW全体を正しく表していません。");
        }

        return new LzopIndexCacheHeader(
            sourcePath,
            sourceLength,
            sourceWriteTicks,
            fingerprint,
            rawLength,
            blockCount);
    }

    private static uint? ReadNullableUInt32(BinaryReader reader) => reader.ReadBoolean() ? reader.ReadUInt32() : null;

    private static bool IsSourceCurrent(LzopIndexCacheHeader header)
    {
        try
        {
            var source = new FileInfo(header.SourcePath);
            if (!source.Exists
                || source.Length != header.SourceLength
                || source.LastWriteTimeUtc.Ticks != header.SourceLastWriteUtcTicks)
            {
                return false;
            }

            return CryptographicOperations.FixedTimeEquals(
                header.Fingerprint,
                ComputeFingerprint(source.FullName, source.Length));
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or ArgumentException
                                   or NotSupportedException)
        {
            return false;
        }
    }

    internal static byte[] ComputeFingerprint(string path, long length)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        AppendRange(hash, stream, 0, Math.Min(FingerprintLength, length));
        if (length > FingerprintLength)
        {
            AppendRange(hash, stream, length - FingerprintLength, FingerprintLength);
        }

        return hash.GetHashAndReset();
    }

    private static void AppendRange(IncrementalHash hash, Stream stream, long offset, long count)
    {
        stream.Position = offset;
        var buffer = new byte[checked((int)count)];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer, total, buffer.Length - total);
            if (read == 0)
            {
                throw new EndOfStreamException("LZO索引の元ファイルが途中で終了しました。");
            }

            total += read;
        }

        hash.AppendData(buffer);
    }
}

internal sealed record LzopIndexCacheHeader(
    string SourcePath,
    long SourceLength,
    long SourceLastWriteUtcTicks,
    byte[] Fingerprint,
    long RawLength,
    int BlockCount);
