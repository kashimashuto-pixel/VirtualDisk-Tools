using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Konscious.Security.Cryptography;
using Qcow2Explorer.Core;

namespace Qcow2Explorer.FileSystems;

public sealed class Luks2Metadata
{
    public long HeaderSize { get; init; }
    public ulong SequenceId { get; init; }
    public string Uuid { get; init; } = "";
    public bool UsedSecondaryHeader { get; init; }
    public Luks2Segment Segment { get; init; } = null!;
    public IReadOnlyList<Luks2KeySlot> KeySlots { get; init; } = Array.Empty<Luks2KeySlot>();
    public IReadOnlyList<Luks2KeySlot> SupportedKeySlots => KeySlots.Where(slot => slot.IsSupported).ToList();
    public IReadOnlyList<Luks2KeySlot> UnsupportedKeySlots => KeySlots.Where(slot => !slot.IsSupported).ToList();
}

public sealed class Luks2KeySlot
{
    public int Index { get; init; }
    public int KeyBytes { get; init; }
    public string KdfType { get; init; } = "";
    public string KdfHash { get; init; } = "";
    public int KdfIterations { get; init; }
    public int KdfMemoryKiB { get; init; }
    public int KdfParallelism { get; init; }
    public byte[] KdfSalt { get; init; } = Array.Empty<byte>();
    public long AreaOffset { get; init; }
    public long AreaSize { get; init; }
    public int AreaKeyBytes { get; init; }
    public string AreaEncryption { get; init; } = "";
    public int Stripes { get; init; }
    public string AfHash { get; init; } = "";
    public Luks2Digest? Digest { get; init; }
    public bool IsSupported { get; init; }
    public string UnsupportedReason { get; init; } = "";
}

public sealed class Luks2Digest
{
    public string Hash { get; init; } = "";
    public int Iterations { get; init; }
    public byte[] Salt { get; init; } = Array.Empty<byte>();
    public byte[] Value { get; init; } = Array.Empty<byte>();
}

public sealed class Luks2Segment
{
    public string Id { get; init; } = "";
    public long Offset { get; init; }
    public ulong IvTweak { get; init; }
    public string Encryption { get; init; } = "";
    public int SectorSize { get; init; }
}

public static class Luks2MetadataReader
{
    public const int BinaryHeaderSize = 4096;
    public const int StripeCount = 4000;
    public const int MaximumPbkdf2Iterations = Luks1MetadataReader.MaximumPbkdf2Iterations;
    public const int MaximumArgon2MemoryKiB = 1024 * 1024;
    public const int MaximumArgon2Iterations = 10;
    public const int MaximumArgon2Parallelism = 16;
    private const int MaximumJsonObjects = 32;
    private static readonly long[] AllowedHeaderSizes =
    [
        0x4000, 0x8000, 0x10000, 0x20000, 0x40000,
        0x80000, 0x100000, 0x200000, 0x400000
    ];
    private static readonly byte[] PrimaryMagic = [(byte)'L', (byte)'U', (byte)'K', (byte)'S', 0xba, 0xbe];
    private static readonly byte[] SecondaryMagic = [(byte)'S', (byte)'K', (byte)'U', (byte)'L', 0xba, 0xbe];

    public static bool TryRead(IBlockReader reader, out Luks2Metadata? metadata, out string error)
    {
        metadata = null;
        error = "";
        try
        {
            var valid = new List<Luks2Metadata>(2);
            var diagnostics = new List<string>();
            TryAddCandidate(reader, 0, false, valid, diagnostics);
            foreach (var offset in AllowedHeaderSizes)
            {
                if (offset + BinaryHeaderSize <= reader.Length)
                {
                    TryAddCandidate(reader, offset, true, valid, diagnostics);
                }
            }

            if (valid.Count == 0)
            {
                error = diagnostics.Count == 0
                    ? "有効なLUKS2 primary/secondary headerがありません。"
                    : $"有効なLUKS2 headerがありません: {string.Join(" / ", diagnostics.Take(3))}";
                return false;
            }

            metadata = valid
                .OrderByDescending(item => item.SequenceId)
                .ThenBy(item => item.UsedSecondaryHeader)
                .First();
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OverflowException
            or ArgumentOutOfRangeException or JsonException or DecoderFallbackException)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void TryAddCandidate(
        IBlockReader reader,
        long offset,
        bool secondary,
        List<Luks2Metadata> valid,
        List<string> diagnostics)
    {
        var binary = EndianUtilities.ReadBytes(reader, offset, BinaryHeaderSize);
        var expectedMagic = secondary ? SecondaryMagic : PrimaryMagic;
        if (!binary.AsSpan(0, expectedMagic.Length).SequenceEqual(expectedMagic))
        {
            if (!secondary)
            {
                diagnostics.Add("primary magic不一致");
            }
            return;
        }

        if (TryReadCandidate(reader, binary, offset, secondary, out var candidate, out var error)
            && candidate is not null)
        {
            valid.Add(candidate);
        }
        else
        {
            diagnostics.Add($"{(secondary ? $"secondary@0x{offset:X}" : "primary")}: {error}");
        }
    }

    private static bool TryReadCandidate(
        IBlockReader reader,
        byte[] binary,
        long offset,
        bool secondary,
        out Luks2Metadata? metadata,
        out string error)
    {
        metadata = null;
        error = "";
        try
        {
            if (EndianUtilities.ReadUInt16Big(binary, 6) != 2)
            {
                throw new InvalidDataException("versionが2ではありません。");
            }

            var headerSizeValue = EndianUtilities.ReadUInt64Big(binary, 8);
            if (headerSizeValue > long.MaxValue || !AllowedHeaderSizes.Contains(checked((long)headerSizeValue)))
            {
                throw new InvalidDataException($"header sizeが未対応です: 0x{headerSizeValue:X}");
            }

            var headerSize = checked((long)headerSizeValue);
            if ((secondary && offset != headerSize) || EndianUtilities.ReadUInt64Big(binary, 256) != checked((ulong)offset))
            {
                throw new InvalidDataException("header offsetが物理位置と一致しません。");
            }

            if (offset > reader.Length - headerSize)
            {
                throw new InvalidDataException("header領域が入力範囲を超えています。");
            }

            RequireZero(binary.AsSpan(264, 184), "binary header reserved領域");
            RequireZero(binary.AsSpan(512), "binary header padding");
            var checksumAlgorithm = ReadAsciiZ(binary, 72, 32).ToLowerInvariant();
            if (!LuksCryptoUtilities.TryGetHashAlgorithm(checksumAlgorithm, out var hashAlgorithm, out var checksumSize)
                || checksumAlgorithm == "sha1")
            {
                throw new InvalidDataException($"header checksum方式が未対応です: {checksumAlgorithm}");
            }

            var fullHeader = EndianUtilities.ReadBytes(reader, offset, checked((int)headerSize));
            var storedChecksum = fullHeader.AsSpan(448, checksumSize).ToArray();
            RequireZero(fullHeader.AsSpan(448 + checksumSize, 64 - checksumSize), "checksum padding");
            fullHeader.AsSpan(448, 64).Clear();
            var calculatedChecksum = HashData(hashAlgorithm, fullHeader);
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(storedChecksum, calculatedChecksum))
                {
                    throw new InvalidDataException("header checksumが一致しません。");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(storedChecksum);
                CryptographicOperations.ZeroMemory(calculatedChecksum);
            }

            var jsonArea = fullHeader.AsSpan(BinaryHeaderSize);
            var terminator = jsonArea.IndexOf((byte)0);
            if (terminator <= 0 || jsonArea[0] != (byte)'{')
            {
                throw new InvalidDataException("JSON metadataの開始または終端が不正です。");
            }
            RequireZero(jsonArea[(terminator + 1)..], "JSON metadata padding");
            var jsonText = new UTF8Encoding(false, true).GetString(jsonArea[..terminator]);
            using var document = JsonDocument.Parse(jsonText, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32
            });
            metadata = ParseMetadata(
                document.RootElement,
                reader.Length,
                headerSize,
                EndianUtilities.ReadUInt64Big(binary, 16),
                ReadAsciiZ(binary, 168, 40),
                secondary);
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OverflowException
            or ArgumentOutOfRangeException or JsonException or DecoderFallbackException or FormatException)
        {
            error = ex.Message;
            return false;
        }
    }

    private static Luks2Metadata ParseMetadata(
        JsonElement root,
        long readerLength,
        long headerSize,
        ulong sequenceId,
        string uuid,
        bool secondary)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("LUKS2 JSON metadataのrootはobjectである必要があります。");
        }

        var config = GetObject(root, "config");
        var expectedJsonSize = checked(headerSize - BinaryHeaderSize);
        if (ReadDecimalString(config, "json_size") != expectedJsonSize)
        {
            throw new InvalidDataException("config.json_sizeがbinary headerと一致しません。");
        }
        var keyslotsSize = ReadDecimalString(config, "keyslots_size");
        var keyslotsStart = checked(headerSize * 2);
        if (keyslotsSize <= 0 || keyslotsSize % BinaryHeaderSize != 0
            || keyslotsStart > readerLength || keyslotsSize > readerLength - keyslotsStart)
        {
            throw new InvalidDataException("config.keyslots_sizeが入力範囲外です。");
        }

        var segmentsObject = GetObject(root, "segments");
        if (segmentsObject.GetRawText().Length > 1024 * 1024)
        {
            throw new InvalidDataException("segments metadataが大きすぎます。");
        }
        var segmentProperties = segmentsObject.EnumerateObject().ToList();
        if (segmentProperties.Count != 1 || segmentProperties.Count > MaximumJsonObjects)
        {
            throw new InvalidDataException("通常の単一LUKS2 segment構成のみ対応しています。");
        }
        var segment = ParseSegment(segmentProperties[0], readerLength);

        var digestAssignments = ParseDigests(GetObject(root, "digests"), segment.Id);
        var keyslotProperties = GetObject(root, "keyslots").EnumerateObject().ToList();
        if (keyslotProperties.Count == 0 || keyslotProperties.Count > MaximumJsonObjects)
        {
            throw new InvalidDataException("LUKS2 keyslot数が不正です。");
        }

        var keySlots = new List<Luks2KeySlot>(keyslotProperties.Count);
        var areaRanges = new List<(long Start, long End)>();
        foreach (var property in keyslotProperties)
        {
            if (!int.TryParse(property.Name, out var index) || index < 0)
            {
                throw new InvalidDataException($"LUKS2 keyslot IDが不正です: {property.Name}");
            }
            var keySlot = ParseKeySlot(property.Value, index, keyslotsStart, keyslotsSize, digestAssignments);
            var areaEnd = checked(keySlot.AreaOffset + keySlot.AreaSize);
            if (areaRanges.Any(range => keySlot.AreaOffset < range.End && range.Start < areaEnd))
            {
                throw new InvalidDataException($"LUKS2 keyslot {index}のareaが他のslotと重複しています。");
            }
            areaRanges.Add((keySlot.AreaOffset, areaEnd));
            keySlots.Add(keySlot);
        }

        return new Luks2Metadata
        {
            HeaderSize = headerSize,
            SequenceId = sequenceId,
            Uuid = uuid,
            UsedSecondaryHeader = secondary,
            Segment = segment,
            KeySlots = keySlots
        };
    }

    private static Luks2Segment ParseSegment(JsonProperty property, long readerLength)
    {
        var value = property.Value;
        if (GetString(value, "type") != "crypt")
        {
            throw new InvalidDataException("LUKS2 segment typeはcryptのみ対応しています。");
        }
        var offset = ReadDecimalString(value, "offset");
        var size = GetString(value, "size");
        var encryption = GetString(value, "encryption").ToLowerInvariant();
        var sectorSize = GetInt32(value, "sector_size");
        var ivTweak = ReadUnsignedDecimalString(value, "iv_tweak");
        if (size != "dynamic")
        {
            throw new InvalidDataException("固定長または複数のLUKS2 segmentは未対応です。");
        }
        if (encryption != "aes-xts-plain64")
        {
            throw new InvalidDataException($"未対応のLUKS2 segment暗号方式です: {encryption}");
        }
        if (sectorSize is not (512 or 1024 or 2048 or 4096) || offset % sectorSize != 0
            || offset < 0 || offset >= readerLength)
        {
            throw new InvalidDataException("LUKS2 segment offsetまたはsector_sizeが不正です。");
        }
        return new Luks2Segment
        {
            Id = property.Name,
            Offset = offset,
            IvTweak = ivTweak,
            Encryption = encryption,
            SectorSize = sectorSize
        };
    }

    private static Dictionary<int, Luks2Digest> ParseDigests(JsonElement digests, string segmentId)
    {
        var result = new Dictionary<int, Luks2Digest>();
        var properties = digests.EnumerateObject().ToList();
        if (properties.Count == 0 || properties.Count > MaximumJsonObjects)
        {
            throw new InvalidDataException("LUKS2 digest数が不正です。");
        }
        foreach (var property in properties)
        {
            var value = property.Value;
            if (GetString(value, "type") != "pbkdf2")
            {
                continue;
            }
            if (!ReadStringArray(value, "segments").Contains(segmentId, StringComparer.Ordinal))
            {
                continue;
            }
            var hash = GetString(value, "hash").ToLowerInvariant();
            if (!LuksCryptoUtilities.TryGetHashAlgorithm(hash, out _, out _))
            {
                continue;
            }
            var iterations = ValidateIterations(GetInt32(value, "iterations"), "digest");
            var digest = new Luks2Digest
            {
                Hash = hash,
                Iterations = iterations,
                Salt = ReadBase64(value, "salt", 16, 128),
                Value = ReadBase64(value, "digest", 16, 128)
            };
            foreach (var slotId in ReadStringArray(value, "keyslots"))
            {
                if (!int.TryParse(slotId, out var keyslotIndex) || keyslotIndex < 0
                    || !result.TryAdd(keyslotIndex, digest))
                {
                    throw new InvalidDataException("LUKS2 digestのkeyslot割り当てが不正または重複しています。");
                }
            }
        }
        return result;
    }

    private static Luks2KeySlot ParseKeySlot(
        JsonElement value,
        int index,
        long keyslotsStart,
        long keyslotsSize,
        IReadOnlyDictionary<int, Luks2Digest> digestAssignments)
    {
        if (GetString(value, "type") != "luks2")
        {
            throw new InvalidDataException($"LUKS2 keyslot {index}のtypeが未対応です。");
        }
        var keyBytes = GetInt32(value, "key_size");
        if (keyBytes is not (32 or 64))
        {
            throw new InvalidDataException($"LUKS2 keyslot {index}のvolume key長が未対応です: {keyBytes}");
        }
        var area = GetObject(value, "area");
        var areaOffset = ReadDecimalString(area, "offset");
        var areaSize = ReadDecimalString(area, "size");
        var areaEncryption = GetString(area, "encryption").ToLowerInvariant();
        var areaKeyBytes = GetInt32(area, "key_size");
        var keyslotsEnd = checked(keyslotsStart + keyslotsSize);
        if (GetString(area, "type") != "raw" || areaOffset < keyslotsStart || areaSize <= 0
            || areaOffset % LuksCryptoUtilities.SectorSize != 0 || areaSize % LuksCryptoUtilities.SectorSize != 0
            || areaOffset > keyslotsEnd || areaSize > keyslotsEnd - areaOffset)
        {
            throw new InvalidDataException($"LUKS2 keyslot {index}のarea範囲が不正です。");
        }

        var af = GetObject(value, "af");
        var afType = GetString(af, "type");
        var stripes = GetInt32(af, "stripes");
        var afHash = GetString(af, "hash").ToLowerInvariant();
        var requiredMaterialSize = checked((keyBytes * stripes + LuksCryptoUtilities.SectorSize - 1)
            / LuksCryptoUtilities.SectorSize * LuksCryptoUtilities.SectorSize);
        if (afType != "luks1" || stripes != StripeCount || areaSize < requiredMaterialSize)
        {
            throw new InvalidDataException($"LUKS2 keyslot {index}のAF設定またはarea sizeが未対応です。");
        }

        var kdf = GetObject(value, "kdf");
        var kdfType = GetString(kdf, "type").ToLowerInvariant();
        var supported = true;
        var unsupportedReason = "";
        var kdfHash = "";
        var kdfIterations = 0;
        var kdfMemoryKiB = 0;
        var kdfParallelism = 0;
        var kdfSalt = Array.Empty<byte>();
        if (kdfType == "pbkdf2")
        {
            kdfHash = GetString(kdf, "hash").ToLowerInvariant();
            kdfIterations = ValidateIterations(GetInt32(kdf, "iterations"), $"keyslot {index}");
            kdfSalt = ReadBase64(kdf, "salt", 16, 128);
            if (!LuksCryptoUtilities.TryGetHashAlgorithm(kdfHash, out _, out _))
            {
                supported = false;
                unsupportedReason = $"PBKDF2 hash {kdfHash}は未対応です";
            }
        }
        else if (kdfType == "argon2id")
        {
            kdfIterations = GetInt32(kdf, "time");
            kdfMemoryKiB = GetInt32(kdf, "memory");
            kdfParallelism = GetInt32(kdf, "cpus");
            kdfSalt = ReadBase64(kdf, "salt", 16, 128);
            if (kdfIterations <= 0 || kdfIterations > MaximumArgon2Iterations)
            {
                throw new InvalidDataException(
                    $"LUKS2 keyslot {index}のArgon2 time costが不正です: {kdfIterations}");
            }
            if (kdfParallelism <= 0 || kdfParallelism > MaximumArgon2Parallelism)
            {
                throw new InvalidDataException(
                    $"LUKS2 keyslot {index}のArgon2 parallelismが不正です: {kdfParallelism}");
            }
            if (kdfMemoryKiB < checked(8 * kdfParallelism) || kdfMemoryKiB > MaximumArgon2MemoryKiB)
            {
                throw new InvalidDataException(
                    $"LUKS2 keyslot {index}のArgon2 memory costが不正です: {kdfMemoryKiB} KiB");
            }
        }
        else
        {
            supported = false;
            unsupportedReason = $"KDF {kdfType}は未対応です";
        }
        if (areaEncryption != "aes-xts-plain64" || areaKeyBytes is not (32 or 64))
        {
            supported = false;
            unsupportedReason = $"keyslot暗号方式 {areaEncryption}/{areaKeyBytes * 8}-bitは未対応です";
        }
        if (!LuksCryptoUtilities.TryGetHashAlgorithm(afHash, out _, out _))
        {
            supported = false;
            unsupportedReason = $"AF hash {afHash}は未対応です";
        }
        digestAssignments.TryGetValue(index, out var digest);
        if (digest is null)
        {
            supported = false;
            unsupportedReason = "segmentに割り当てられたPBKDF2 digestがありません";
        }

        return new Luks2KeySlot
        {
            Index = index,
            KeyBytes = keyBytes,
            KdfType = kdfType,
            KdfHash = kdfHash,
            KdfIterations = kdfIterations,
            KdfMemoryKiB = kdfMemoryKiB,
            KdfParallelism = kdfParallelism,
            KdfSalt = kdfSalt,
            AreaOffset = areaOffset,
            AreaSize = areaSize,
            AreaKeyBytes = areaKeyBytes,
            AreaEncryption = areaEncryption,
            Stripes = stripes,
            AfHash = afHash,
            Digest = digest,
            IsSupported = supported,
            UnsupportedReason = unsupportedReason
        };
    }

    private static int ValidateIterations(int iterations, string owner)
    {
        if (iterations <= 0 || iterations > MaximumPbkdf2Iterations)
        {
            throw new InvalidDataException($"LUKS2 {owner}のPBKDF2反復回数が不正です: {iterations}");
        }
        return iterations;
    }

    private static JsonElement GetObject(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"LUKS2 JSON field {name}がobjectではありません。");
        }
        return value;
    }

    private static string GetString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"LUKS2 JSON field {name}がstringではありません。");
        }
        return value.GetString() ?? "";
    }

    private static int GetInt32(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out var result))
        {
            throw new InvalidDataException($"LUKS2 JSON field {name}がint32ではありません。");
        }
        return result;
    }

    private static long ReadDecimalString(JsonElement parent, string name)
    {
        var text = GetString(parent, name);
        if (!long.TryParse(text, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            throw new InvalidDataException($"LUKS2 JSON field {name}がdecimal stringではありません。");
        }
        return value;
    }

    private static ulong ReadUnsignedDecimalString(JsonElement parent, string name)
    {
        var text = GetString(parent, name);
        if (!ulong.TryParse(text, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            throw new InvalidDataException($"LUKS2 JSON field {name}がunsigned decimal stringではありません。");
        }
        return value;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"LUKS2 JSON field {name}がarrayではありません。");
        }
        var result = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || result.Count >= MaximumJsonObjects)
            {
                throw new InvalidDataException($"LUKS2 JSON field {name}の要素が不正です。");
            }
            result.Add(item.GetString() ?? "");
        }
        return result;
    }

    private static byte[] ReadBase64(JsonElement parent, string name, int minimumLength, int maximumLength)
    {
        var result = Convert.FromBase64String(GetString(parent, name));
        if (result.Length < minimumLength || result.Length > maximumLength)
        {
            CryptographicOperations.ZeroMemory(result);
            throw new InvalidDataException($"LUKS2 JSON field {name}のdecoded sizeが不正です。");
        }
        return result;
    }

    private static string ReadAsciiZ(byte[] data, int offset, int length)
    {
        var value = data.AsSpan(offset, length);
        var terminator = value.IndexOf((byte)0);
        if (terminator < 1)
        {
            throw new InvalidDataException($"LUKS2 ASCII field @0x{offset:X}が不正です。");
        }
        var text = value[..terminator];
        foreach (var item in text)
        {
            if (item is < 0x20 or > 0x7e)
            {
                throw new InvalidDataException($"LUKS2 ASCII field @0x{offset:X}に非ASCII文字があります。");
            }
        }
        return Encoding.ASCII.GetString(text);
    }

    private static byte[] HashData(HashAlgorithmName algorithm, byte[] data)
    {
        if (algorithm == HashAlgorithmName.SHA256)
        {
            return SHA256.HashData(data);
        }
        if (algorithm == HashAlgorithmName.SHA512)
        {
            return SHA512.HashData(data);
        }
        throw new InvalidDataException($"未対応のhashです: {algorithm.Name}");
    }

    private static void RequireZero(ReadOnlySpan<byte> data, string field)
    {
        foreach (var value in data)
        {
            if (value != 0)
            {
                throw new InvalidDataException($"LUKS2 {field}がzero paddingではありません。");
            }
        }
    }
}

public sealed class Luks2DecryptingReader : IBlockReader, IDisposable
{
    private readonly IBlockReader _reader;
    private readonly Luks2Segment _segment;
    private readonly byte[] _dataKey;
    private readonly byte[] _tweakKey;
    private bool _disposed;

    internal Luks2DecryptingReader(IBlockReader reader, Luks2Metadata metadata, ReadOnlySpan<byte> volumeKey)
    {
        if (volumeKey.Length is not (32 or 64))
        {
            throw new InvalidDataException("LUKS2 volume key長がAES-XTSに対応していません。");
        }
        _reader = reader;
        _segment = metadata.Segment;
        var half = volumeKey.Length / 2;
        _dataKey = volumeKey[..half].ToArray();
        _tweakKey = volumeKey[half..].ToArray();
        Length = reader.Length - _segment.Offset;
    }

    public long Length { get; }

    public void ReadAt(long offset, byte[] buffer, int bufferOffset, int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentNullException.ThrowIfNull(buffer);
        if (bufferOffset < 0 || count < 0 || bufferOffset + count > buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(bufferOffset));
        }
        Array.Clear(buffer, bufferOffset, count);
        if (count == 0 || offset >= Length)
        {
            return;
        }

        var remaining = Math.Min(count, Length - offset);
        var currentOffset = offset;
        var outputOffset = bufferOffset;
        while (remaining > 0)
        {
            var sectorNumber = currentOffset / _segment.SectorSize;
            var inSectorOffset = checked((int)(currentOffset % _segment.SectorSize));
            var chunk = checked((int)Math.Min(remaining, _segment.SectorSize - inSectorOffset));
            var encryptedSector = EndianUtilities.ReadBytes(
                _reader,
                checked(_segment.Offset + sectorNumber * _segment.SectorSize),
                _segment.SectorSize);
            byte[]? plaintextSector = null;
            try
            {
                plaintextSector = AesXtsSectorCipher.DecryptSector(
                    encryptedSector,
                    _dataKey,
                    _tweakKey,
                    checked(_segment.IvTweak + (ulong)sectorNumber));
                Array.Copy(plaintextSector, inSectorOffset, buffer, outputOffset, chunk);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(encryptedSector);
                LuksCryptoUtilities.Zero(plaintextSector);
            }
            currentOffset += chunk;
            outputOffset += chunk;
            remaining -= chunk;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        CryptographicOperations.ZeroMemory(_dataKey);
        CryptographicOperations.ZeroMemory(_tweakKey);
    }
}

public static class Luks2Unlock
{
    private const int MaximumPassphraseBytes = 1024 * 1024;

    public static bool TryCreateReader(
        IBlockReader encryptedReader,
        Luks2Metadata metadata,
        ReadOnlySpan<char> passphrase,
        out IBlockReader? decryptedReader,
        out string error,
        CancellationToken cancellationToken = default)
    {
        decryptedReader = null;
        error = "";
        if (passphrase.IsEmpty)
        {
            error = "LUKS2パスフレーズを入力してください。";
            return false;
        }
        if (metadata.SupportedKeySlots.Count == 0)
        {
            error = metadata.UnsupportedKeySlots.Count == 0
                ? "LUKS2に利用可能なkeyslotがありません。"
                : $"対応するLUKS2 keyslotがありません: {string.Join(", ", metadata.UnsupportedKeySlots.Select(slot => $"{slot.Index} ({slot.UnsupportedReason})"))}";
            return false;
        }

        var passphraseByteCount = Encoding.UTF8.GetByteCount(passphrase);
        if (passphraseByteCount <= 0 || passphraseByteCount > MaximumPassphraseBytes)
        {
            error = "LUKS2パスフレーズが長すぎます。";
            return false;
        }
        var passphraseBytes = new byte[passphraseByteCount];
        Encoding.UTF8.GetBytes(passphrase, passphraseBytes);
        try
        {
            foreach (var slot in metadata.SupportedKeySlots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte[]? derivedKey = null;
                byte[]? encryptedMaterial = null;
                byte[]? decryptedMaterial = null;
                byte[]? volumeKey = null;
                byte[]? candidateDigest = null;
                try
                {
                    if (!LuksCryptoUtilities.TryGetHashAlgorithm(slot.AfHash, out var afHash, out var afDigestSize)
                        || slot.Digest is null
                        || !LuksCryptoUtilities.TryGetHashAlgorithm(slot.Digest.Hash, out var digestHash, out _))
                    {
                        continue;
                    }
                    if (slot.KdfType == "pbkdf2")
                    {
                        if (!LuksCryptoUtilities.TryGetHashAlgorithm(slot.KdfHash, out var kdfHash, out _))
                        {
                            continue;
                        }
                        derivedKey = new byte[slot.AreaKeyBytes];
                        Rfc2898DeriveBytes.Pbkdf2(
                            passphraseBytes, slot.KdfSalt, derivedKey, slot.KdfIterations, kdfHash);
                    }
                    else if (slot.KdfType == "argon2id")
                    {
                        if (!TryCheckArgon2MemoryAvailability(slot.KdfMemoryKiB, out error))
                        {
                            return false;
                        }
                        using var argon2 = new Argon2id(passphraseBytes)
                        {
                            Salt = slot.KdfSalt,
                            Iterations = slot.KdfIterations,
                            MemorySize = slot.KdfMemoryKiB,
                            DegreeOfParallelism = slot.KdfParallelism
                        };
                        derivedKey = argon2.GetBytesAsync(slot.AreaKeyBytes).GetAwaiter().GetResult();
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                    else
                    {
                        continue;
                    }
                    var splitBytes = checked(slot.KeyBytes * slot.Stripes);
                    var materialBytes = checked((splitBytes + LuksCryptoUtilities.SectorSize - 1)
                        / LuksCryptoUtilities.SectorSize * LuksCryptoUtilities.SectorSize);
                    encryptedMaterial = EndianUtilities.ReadBytes(encryptedReader, slot.AreaOffset, materialBytes);
                    decryptedMaterial = LuksCryptoUtilities.DecryptKeyMaterial(encryptedMaterial, derivedKey);
                    volumeKey = LuksCryptoUtilities.MergeAntiForensicStripes(
                        decryptedMaterial.AsSpan(0, splitBytes),
                        slot.KeyBytes,
                        slot.Stripes,
                        afHash,
                        afDigestSize);
                    candidateDigest = new byte[slot.Digest.Value.Length];
                    Rfc2898DeriveBytes.Pbkdf2(
                        volumeKey,
                        slot.Digest.Salt,
                        candidateDigest,
                        slot.Digest.Iterations,
                        digestHash);
                    if (!CryptographicOperations.FixedTimeEquals(candidateDigest, slot.Digest.Value))
                    {
                        continue;
                    }
                    decryptedReader = new Luks2DecryptingReader(encryptedReader, metadata, volumeKey);
                    return true;
                }
                catch (CryptographicException)
                {
                    // The passphrase can belong to another supported keyslot.
                }
                finally
                {
                    LuksCryptoUtilities.Zero(derivedKey);
                    LuksCryptoUtilities.Zero(encryptedMaterial);
                    LuksCryptoUtilities.Zero(decryptedMaterial);
                    LuksCryptoUtilities.Zero(volumeKey);
                    LuksCryptoUtilities.Zero(candidateDigest);
                }
            }
            error = "LUKS2パスフレーズが対応するkeyslotと一致しません。";
            return false;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OverflowException
            or ArgumentOutOfRangeException or InvalidOperationException or CryptographicException or OutOfMemoryException)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passphraseBytes);
        }
    }

    private static bool TryCheckArgon2MemoryAvailability(int memoryKiB, out string error)
    {
        error = "";
        var requiredBytes = checked((long)memoryKiB * 1024);
        var memoryInfo = GC.GetGCMemoryInfo();
        var remainingBudget = Math.Max(0, memoryInfo.TotalAvailableMemoryBytes - memoryInfo.MemoryLoadBytes);
        const long reserveBytes = 256L * 1024 * 1024;
        if (requiredBytes > Math.Max(0, remainingBudget - reserveBytes))
        {
            error = $"LUKS2 Argon2id解除には約{requiredBytes / (1024 * 1024):N0} MiBの空きメモリが必要です。";
            return false;
        }
        return true;
    }
}
