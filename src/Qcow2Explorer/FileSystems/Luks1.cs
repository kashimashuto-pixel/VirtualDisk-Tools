using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Qcow2Explorer.Core;

namespace Qcow2Explorer.FileSystems;

public sealed class Luks1Metadata
{
    public string CipherName { get; init; } = "";
    public string CipherMode { get; init; } = "";
    public string HashSpec { get; init; } = "";
    public uint PayloadOffsetSectors { get; init; }
    public int KeyBytes { get; init; }
    public byte[] MasterKeyDigest { get; init; } = Array.Empty<byte>();
    public byte[] MasterKeyDigestSalt { get; init; } = Array.Empty<byte>();
    public int MasterKeyDigestIterations { get; init; }
    public string Uuid { get; init; } = "";
    public IReadOnlyList<Luks1KeySlot> KeySlots { get; init; } = Array.Empty<Luks1KeySlot>();
    public IReadOnlyList<Luks1KeySlot> ActiveKeySlots => KeySlots.Where(slot => slot.IsActive).ToList();
    public long PayloadOffsetBytes => checked((long)PayloadOffsetSectors * Luks1MetadataReader.SectorSize);
}

public sealed class Luks1KeySlot
{
    public int Index { get; init; }
    public bool IsActive { get; init; }
    public int PasswordIterations { get; init; }
    public byte[] PasswordSalt { get; init; } = Array.Empty<byte>();
    public uint KeyMaterialOffsetSectors { get; init; }
    public int Stripes { get; init; }
}

public static class Luks1MetadataReader
{
    public const int SectorSize = 512;
    public const int HeaderFieldSize = 592;
    public const int StripeCount = 4000;
    public const int MaximumPbkdf2Iterations = 50_000_000;
    private const uint EnabledKeySlot = 0x00ac71f3;
    private const uint DisabledKeySlot = 0x0000dead;
    private static readonly byte[] Magic = [(byte)'L', (byte)'U', (byte)'K', (byte)'S', 0xba, 0xbe];

    public static bool HasLuksMagic(ReadOnlySpan<byte> data)
    {
        return data.Length >= Magic.Length && data[..Magic.Length].SequenceEqual(Magic);
    }

    public static bool TryRead(IBlockReader reader, out Luks1Metadata? metadata, out string error)
    {
        metadata = null;
        error = "";
        try
        {
            if (reader.Length < HeaderFieldSize)
            {
                error = "LUKS1ヘッダーが切り詰められています。";
                return false;
            }

            var header = EndianUtilities.ReadBytes(reader, 0, HeaderFieldSize);
            if (!HasLuksMagic(header))
            {
                error = "LUKSマジックが一致しません。";
                return false;
            }

            var version = EndianUtilities.ReadUInt16Big(header, 6);
            if (version != 1)
            {
                error = $"未対応のLUKS versionです: {version}";
                return false;
            }

            var cipherName = ReadAsciiField(header, 8, 32);
            var cipherMode = ReadAsciiField(header, 40, 32);
            var hashSpec = ReadAsciiField(header, 72, 32).ToLowerInvariant();
            if (!string.Equals(cipherName, "aes", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(cipherMode, "xts-plain64", StringComparison.OrdinalIgnoreCase))
            {
                error = $"未対応のLUKS1暗号方式です: {cipherName}-{cipherMode}";
                return false;
            }

            if (!TryGetHashAlgorithm(hashSpec, out _, out _))
            {
                error = $"未対応のLUKS1ハッシュ方式です: {hashSpec}";
                return false;
            }

            var payloadOffset = EndianUtilities.ReadUInt32Big(header, 104);
            var keyBytesValue = EndianUtilities.ReadUInt32Big(header, 108);
            var masterIterationsValue = EndianUtilities.ReadUInt32Big(header, 164);
            if (payloadOffset == 0 || (ulong)payloadOffset * SectorSize >= (ulong)reader.Length)
            {
                error = $"LUKS1 payload offsetが入力範囲外です: {payloadOffset} sectors";
                return false;
            }

            if (keyBytesValue is not (32 or 64))
            {
                error = $"LUKS1 AES-XTSキー長が未対応です: {keyBytesValue} bytes";
                return false;
            }

            if (masterIterationsValue == 0 || masterIterationsValue > MaximumPbkdf2Iterations)
            {
                error = $"LUKS1 master key digestの反復回数が不正です: {masterIterationsValue}";
                return false;
            }

            var keyBytes = checked((int)keyBytesValue);
            var keyMaterialSectors = checked((keyBytes * StripeCount + SectorSize - 1) / SectorSize);
            var slots = new List<Luks1KeySlot>(8);
            var ranges = new List<(uint Start, uint End)>();
            for (var index = 0; index < 8; index++)
            {
                var slotOffset = 208 + index * 48;
                var activeValue = EndianUtilities.ReadUInt32Big(header, slotOffset);
                if (activeValue is not (EnabledKeySlot or DisabledKeySlot))
                {
                    error = $"LUKS1 key slot {index}の状態値が不正です: 0x{activeValue:X8}";
                    return false;
                }

                var passwordIterationsValue = EndianUtilities.ReadUInt32Big(header, slotOffset + 4);
                var keyMaterialOffset = EndianUtilities.ReadUInt32Big(header, slotOffset + 40);
                var stripesValue = EndianUtilities.ReadUInt32Big(header, slotOffset + 44);
                if (stripesValue != StripeCount)
                {
                    error = $"LUKS1 key slot {index}のAF stripe数が未対応です: {stripesValue}";
                    return false;
                }

                if (keyMaterialOffset < 2
                    || keyMaterialOffset >= payloadOffset
                    || keyMaterialSectors > payloadOffset - keyMaterialOffset)
                {
                    error = $"LUKS1 key slot {index}のkey material範囲が不正です。";
                    return false;
                }

                var rangeEnd = checked(keyMaterialOffset + (uint)keyMaterialSectors);
                if (ranges.Any(range => keyMaterialOffset < range.End && range.Start < rangeEnd))
                {
                    error = $"LUKS1 key slot {index}のkey material範囲が他のslotと重複しています。";
                    return false;
                }

                ranges.Add((keyMaterialOffset, rangeEnd));
                var isActive = activeValue == EnabledKeySlot;
                if (isActive && (passwordIterationsValue == 0 || passwordIterationsValue > MaximumPbkdf2Iterations))
                {
                    error = $"LUKS1 key slot {index}のPBKDF2反復回数が不正です: {passwordIterationsValue}";
                    return false;
                }

                slots.Add(new Luks1KeySlot
                {
                    Index = index,
                    IsActive = isActive,
                    PasswordIterations = passwordIterationsValue <= int.MaxValue
                        ? checked((int)passwordIterationsValue)
                        : 0,
                    PasswordSalt = header.AsSpan(slotOffset + 8, 32).ToArray(),
                    KeyMaterialOffsetSectors = keyMaterialOffset,
                    Stripes = checked((int)stripesValue)
                });
            }

            if (!slots.Any(slot => slot.IsActive))
            {
                error = "LUKS1に有効なkey slotがありません。";
                return false;
            }

            metadata = new Luks1Metadata
            {
                CipherName = cipherName,
                CipherMode = cipherMode,
                HashSpec = hashSpec,
                PayloadOffsetSectors = payloadOffset,
                KeyBytes = keyBytes,
                MasterKeyDigest = header.AsSpan(112, 20).ToArray(),
                MasterKeyDigestSalt = header.AsSpan(132, 32).ToArray(),
                MasterKeyDigestIterations = checked((int)masterIterationsValue),
                Uuid = ReadAsciiField(header, 168, 40),
                KeySlots = slots
            };
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OverflowException or ArgumentOutOfRangeException)
        {
            error = ex.Message;
            return false;
        }
    }

    internal static bool TryGetHashAlgorithm(string hashSpec, out HashAlgorithmName algorithm, out int digestSize)
    {
        switch (hashSpec.ToLowerInvariant())
        {
            case "sha1":
                algorithm = HashAlgorithmName.SHA1;
                digestSize = 20;
                return true;
            case "sha256":
                algorithm = HashAlgorithmName.SHA256;
                digestSize = 32;
                return true;
            case "sha512":
                algorithm = HashAlgorithmName.SHA512;
                digestSize = 64;
                return true;
            default:
                algorithm = default;
                digestSize = 0;
                return false;
        }
    }

    private static string ReadAsciiField(byte[] data, int offset, int length)
    {
        var field = data.AsSpan(offset, length);
        var zero = field.IndexOf((byte)0);
        var value = zero >= 0 ? field[..zero] : field;
        if (value.IsEmpty || !IsPrintableAscii(value))
        {
            throw new InvalidDataException($"LUKS1ヘッダーのASCIIフィールドが不正です: offset=0x{offset:X}");
        }

        return Encoding.ASCII.GetString(value);
    }

    private static bool IsPrintableAscii(ReadOnlySpan<byte> value)
    {
        foreach (var item in value)
        {
            if (item is < 0x20 or > 0x7e)
            {
                return false;
            }
        }

        return true;
    }
}

public sealed class Luks1DecryptingReader : IBlockReader, IDisposable
{
    private const int SectorSize = Luks1MetadataReader.SectorSize;
    private readonly IBlockReader _reader;
    private readonly long _payloadOffset;
    private readonly byte[] _dataKey;
    private readonly byte[] _tweakKey;
    private bool _disposed;

    internal Luks1DecryptingReader(IBlockReader reader, Luks1Metadata metadata, ReadOnlySpan<byte> masterKey)
    {
        if (masterKey.Length != metadata.KeyBytes || masterKey.Length is not (32 or 64))
        {
            throw new InvalidDataException("LUKS1 master keyの長さがヘッダーと一致しません。");
        }

        _reader = reader;
        _payloadOffset = metadata.PayloadOffsetBytes;
        var half = masterKey.Length / 2;
        _dataKey = masterKey[..half].ToArray();
        _tweakKey = masterKey[half..].ToArray();
        Length = reader.Length - _payloadOffset;
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
            var sectorNumber = currentOffset / SectorSize;
            var inSectorOffset = checked((int)(currentOffset % SectorSize));
            var chunk = checked((int)Math.Min(remaining, SectorSize - inSectorOffset));
            var encryptedSector = EndianUtilities.ReadBytes(
                _reader,
                checked(_payloadOffset + sectorNumber * SectorSize),
                SectorSize);
            byte[]? plaintextSector = null;
            try
            {
                plaintextSector = AesXtsSectorCipher.DecryptSector(
                    encryptedSector,
                    _dataKey,
                    _tweakKey,
                    checked((ulong)sectorNumber));
                Array.Copy(plaintextSector, inSectorOffset, buffer, outputOffset, chunk);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(encryptedSector);
                if (plaintextSector is not null)
                {
                    CryptographicOperations.ZeroMemory(plaintextSector);
                }
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

public static class Luks1Unlock
{
    private const int MaximumPassphraseBytes = 1024 * 1024;

    public static bool TryCreateReader(
        IBlockReader encryptedReader,
        Luks1Metadata metadata,
        ReadOnlySpan<char> passphrase,
        out IBlockReader? decryptedReader,
        out string error,
        CancellationToken cancellationToken = default)
    {
        decryptedReader = null;
        error = "";
        if (passphrase.IsEmpty)
        {
            error = "LUKS1パスフレーズを入力してください。";
            return false;
        }

        if (!Luks1MetadataReader.TryGetHashAlgorithm(metadata.HashSpec, out var hashAlgorithm, out var digestSize))
        {
            error = $"未対応のLUKS1ハッシュ方式です: {metadata.HashSpec}";
            return false;
        }

        var passphraseByteCount = Encoding.UTF8.GetByteCount(passphrase);
        if (passphraseByteCount <= 0 || passphraseByteCount > MaximumPassphraseBytes)
        {
            error = "LUKS1パスフレーズが長すぎます。";
            return false;
        }

        var passphraseBytes = new byte[passphraseByteCount];
        Encoding.UTF8.GetBytes(passphrase, passphraseBytes);
        try
        {
            foreach (var slot in metadata.ActiveKeySlots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte[]? derivedKey = null;
                byte[]? encryptedMaterial = null;
                byte[]? decryptedMaterial = null;
                byte[]? masterKey = null;
                byte[]? candidateDigest = null;
                try
                {
                    derivedKey = new byte[metadata.KeyBytes];
                    Rfc2898DeriveBytes.Pbkdf2(
                        passphraseBytes,
                        slot.PasswordSalt,
                        derivedKey,
                        slot.PasswordIterations,
                        hashAlgorithm);

                    var splitBytes = checked(metadata.KeyBytes * slot.Stripes);
                    var materialBytes = checked((splitBytes + Luks1MetadataReader.SectorSize - 1)
                        / Luks1MetadataReader.SectorSize
                        * Luks1MetadataReader.SectorSize);
                    encryptedMaterial = EndianUtilities.ReadBytes(
                        encryptedReader,
                        checked((long)slot.KeyMaterialOffsetSectors * Luks1MetadataReader.SectorSize),
                        materialBytes);
                    decryptedMaterial = DecryptKeyMaterial(encryptedMaterial, derivedKey);
                    masterKey = MergeAntiForensicStripes(
                        decryptedMaterial.AsSpan(0, splitBytes),
                        metadata.KeyBytes,
                        slot.Stripes,
                        hashAlgorithm,
                        digestSize);
                    candidateDigest = new byte[metadata.MasterKeyDigest.Length];
                    Rfc2898DeriveBytes.Pbkdf2(
                        masterKey,
                        metadata.MasterKeyDigestSalt,
                        candidateDigest,
                        metadata.MasterKeyDigestIterations,
                        hashAlgorithm);
                    if (!CryptographicOperations.FixedTimeEquals(candidateDigest, metadata.MasterKeyDigest))
                    {
                        continue;
                    }

                    decryptedReader = new Luks1DecryptingReader(encryptedReader, metadata, masterKey);
                    return true;
                }
                catch (CryptographicException)
                {
                    // A correctly encoded passphrase can belong to a different active key slot.
                }
                finally
                {
                    Zero(derivedKey);
                    Zero(encryptedMaterial);
                    Zero(decryptedMaterial);
                    Zero(masterKey);
                    Zero(candidateDigest);
                }
            }

            error = "LUKS1パスフレーズが有効なkey slotと一致しません。";
            return false;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OverflowException or ArgumentOutOfRangeException)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passphraseBytes);
        }
    }

    private static byte[] DecryptKeyMaterial(byte[] ciphertext, byte[] xtsKey)
    {
        var half = xtsKey.Length / 2;
        var plaintext = new byte[ciphertext.Length];
        try
        {
            for (var offset = 0; offset < ciphertext.Length; offset += Luks1MetadataReader.SectorSize)
            {
                var sector = AesXtsSectorCipher.DecryptSector(
                    ciphertext.AsSpan(offset, Luks1MetadataReader.SectorSize),
                    xtsKey.AsSpan(0, half),
                    xtsKey.AsSpan(half),
                    checked((ulong)(offset / Luks1MetadataReader.SectorSize)));
                try
                {
                    sector.CopyTo(plaintext, offset);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(sector);
                }
            }

            return plaintext;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw;
        }
    }

    private static byte[] MergeAntiForensicStripes(
        ReadOnlySpan<byte> splitKey,
        int keyBytes,
        int stripes,
        HashAlgorithmName hashAlgorithm,
        int digestSize)
    {
        if (stripes < 2 || splitKey.Length != checked(keyBytes * stripes))
        {
            throw new InvalidDataException("LUKS1 anti-forensic key materialのサイズが不正です。");
        }

        var accumulator = new byte[keyBytes];
        var digest = new byte[digestSize];
        try
        {
            using var hash = IncrementalHash.CreateHash(hashAlgorithm);
            Span<byte> blockIndex = stackalloc byte[4];
            for (var stripe = 0; stripe < stripes - 1; stripe++)
            {
                var stripeData = splitKey.Slice(stripe * keyBytes, keyBytes);
                for (var index = 0; index < keyBytes; index++)
                {
                    accumulator[index] ^= stripeData[index];
                }

                var chunkIndex = 0;
                for (var offset = 0; offset < keyBytes; offset += digestSize)
                {
                    var chunkLength = Math.Min(digestSize, keyBytes - offset);
                    BinaryPrimitives.WriteUInt32BigEndian(blockIndex, checked((uint)chunkIndex));
                    hash.AppendData(blockIndex);
                    hash.AppendData(accumulator.AsSpan(offset, chunkLength));
                    if (!hash.TryGetHashAndReset(digest, out var bytesWritten) || bytesWritten != digestSize)
                    {
                        throw new CryptographicException("LUKS1 AF diffuse hashの計算に失敗しました。");
                    }

                    digest.AsSpan(0, chunkLength).CopyTo(accumulator.AsSpan(offset, chunkLength));
                    chunkIndex++;
                }
            }

            var finalStripe = splitKey.Slice((stripes - 1) * keyBytes, keyBytes);
            for (var index = 0; index < keyBytes; index++)
            {
                accumulator[index] ^= finalStripe[index];
            }

            return accumulator;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(accumulator);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    private static void Zero(byte[]? data)
    {
        if (data is not null)
        {
            CryptographicOperations.ZeroMemory(data);
        }
    }
}
