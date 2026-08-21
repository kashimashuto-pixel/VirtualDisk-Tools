using System.Security.Cryptography;
using Qcow2Explorer.Core;

namespace Qcow2Explorer.FileSystems;

public sealed class BitLockerDecryptingReader : IBlockReader, IDisposable
{
    private const int SectorSize = 512;
    private readonly IBlockReader _reader;
    private readonly BitLockerMetadata _metadata;
    private readonly byte[] _dataKey;
    private readonly byte[] _tweakKey;
    private bool _disposed;

    public BitLockerDecryptingReader(IBlockReader reader, BitLockerMetadata metadata, byte[] fvek)
    {
        _reader = reader;
        _metadata = metadata;
        Length = metadata.EncryptedVolumeSize > 0 && metadata.EncryptedVolumeSize <= reader.Length
            ? metadata.EncryptedVolumeSize
            : reader.Length;

        (_dataKey, _tweakKey) = SplitXtsKey(metadata.EncryptionMethod, fvek);
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
        var outputOffset = bufferOffset;
        var currentOffset = offset;
        while (remaining > 0)
        {
            var sectorNumber = currentOffset / SectorSize;
            var inSector = (int)(currentOffset % SectorSize);
            var chunk = (int)Math.Min(remaining, SectorSize - inSector);
            var sector = ReadAndDecryptSector(sectorNumber);
            Array.Copy(sector, inSector, buffer, outputOffset, chunk);
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

    private byte[] ReadAndDecryptSector(long logicalSector)
    {
        var encryptedSector = new byte[SectorSize];
        try
        {
            var encryptedOffset = GetEncryptedOffsetForLogicalSector(logicalSector);
            _reader.ReadAt(encryptedOffset, encryptedSector, 0, encryptedSector.Length);
            return DecryptXtsSector(encryptedSector, checked((ulong)(encryptedOffset / SectorSize)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encryptedSector);
        }
    }

    private long GetEncryptedOffsetForLogicalSector(long logicalSector)
    {
        var logicalOffset = checked(logicalSector * SectorSize);
        var headerBytes = checked((long)_metadata.VolumeHeaderSectors * SectorSize);
        if (_metadata.VolumeHeaderOffset > 0 && headerBytes > 0 && logicalOffset < headerBytes)
        {
            return checked(_metadata.VolumeHeaderOffset + logicalOffset);
        }

        return logicalOffset;
    }

    private byte[] DecryptXtsSector(byte[] ciphertext, ulong sectorNumber)
    {
        if (ciphertext.Length % 16 != 0)
        {
            throw new InvalidDataException("XTS-AES 復号は 16 byte 単位のセクタだけに対応しています。");
        }

        using var dataAes = CreateEcbAes(_dataKey);
        using var tweakAes = CreateEcbAes(_tweakKey);
        using var dataDecryptor = dataAes.CreateDecryptor();
        using var tweakEncryptor = tweakAes.CreateEncryptor();

        Span<byte> tweakInput = stackalloc byte[16];
        BitConverter.TryWriteBytes(tweakInput, sectorNumber);
        Span<byte> tweak = stackalloc byte[16];
        TransformBlock(tweakEncryptor, tweakInput, tweak);

        var plaintext = new byte[ciphertext.Length];
        Span<byte> block = stackalloc byte[16];
        Span<byte> decrypted = stackalloc byte[16];
        for (var offset = 0; offset < ciphertext.Length; offset += 16)
        {
            for (var i = 0; i < 16; i++)
            {
                block[i] = (byte)(ciphertext[offset + i] ^ tweak[i]);
            }

            TransformBlock(dataDecryptor, block, decrypted);
            for (var i = 0; i < 16; i++)
            {
                plaintext[offset + i] = (byte)(decrypted[i] ^ tweak[i]);
            }

            MultiplyByX(tweak);
        }

        return plaintext;
    }

    private static (byte[] DataKey, byte[] TweakKey) SplitXtsKey(uint encryptionMethod, byte[] fvek)
    {
        return encryptionMethod switch
        {
            0x8004 when fvek.Length >= 32 => (fvek[..16], fvek[16..32]),
            0x8005 when fvek.Length >= 64 => (fvek[..32], fvek[32..64]),
            0x8004 => throw new InvalidDataException("XTS-AES 128-bit には 32 byte の FVEK が必要です。"),
            0x8005 => throw new InvalidDataException("XTS-AES 256-bit には 64 byte の FVEK が必要です。"),
            0x8000 or 0x8001 => throw new NotSupportedException("Elephant Diffuser 付き AES-CBC の BitLocker 復号は未対応です。"),
            0x8002 or 0x8003 => throw new NotSupportedException("AES-CBC の BitLocker 復号は未対応です。"),
            _ => throw new NotSupportedException($"未対応の BitLocker 暗号方式です: {BitLockerMetadataReader.GetEncryptionMethodName(encryptionMethod)}")
        };
    }

    private static Aes CreateEcbAes(byte[] key)
    {
        var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = key;
        return aes;
    }

    private static void TransformBlock(ICryptoTransform transform, ReadOnlySpan<byte> input, Span<byte> output)
    {
        var inputArray = input.ToArray();
        var outputArray = new byte[inputArray.Length];
        try
        {
            var bytes = transform.TransformBlock(inputArray, 0, inputArray.Length, outputArray, 0);
            if (bytes != inputArray.Length)
            {
                throw new CryptographicException("AES block transform failed.");
            }

            outputArray.CopyTo(output);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(inputArray);
            CryptographicOperations.ZeroMemory(outputArray);
        }
    }

    private static void MultiplyByX(Span<byte> tweak)
    {
        var carry = 0;
        for (var i = 0; i < tweak.Length; i++)
        {
            var value = tweak[i];
            var nextCarry = value >> 7;
            tweak[i] = (byte)((value << 1) | carry);
            carry = nextCarry;
        }

        if (carry != 0)
        {
            tweak[0] ^= 0x87;
        }
    }
}

public static class BitLockerUnlock
{
    public static bool TryCreateReaderWithClearKey(
        IBlockReader encryptedReader,
        BitLockerMetadata metadata,
        out IBlockReader? decryptedReader,
        out string error)
    {
        decryptedReader = null;
        error = "";

        byte[]? clearKey = null;
        byte[]? vmkEntryBytes = null;
        byte[]? vmk = null;
        byte[]? fvekEntryBytes = null;
        byte[]? fvek = null;

        try
        {
            var clearProtector = metadata.KeyProtectors.FirstOrDefault(p => p.ProtectionType == BitLockerProtectionType.ClearKey);
            if (clearProtector is null)
            {
                error = "クリアキー保護子がありません。";
                return false;
            }

            var clearKeyEntry = clearProtector.Properties.FirstOrDefault(e => e.ValueType == 0x0001);
            var encryptedVmkEntry = clearProtector.Properties.FirstOrDefault(e => e.ValueType == 0x0005);
            var encryptedFvekEntry = metadata.Entries.FirstOrDefault(e => e.EntryType == 0x0003 && e.ValueType == 0x0005);
            if (clearKeyEntry is null || encryptedVmkEntry is null || encryptedFvekEntry is null)
            {
                error = "クリアキー、暗号化 VMK、または暗号化 FVEK のエントリが不足しています。";
                return false;
            }

            clearKey = ExtractKeyData(clearKeyEntry.Data, "clear key");
            vmkEntryBytes = DecryptAesCcmEntry(clearKey, encryptedVmkEntry.Data);
            vmk = ExtractKeyFromDecryptedEntry(vmkEntryBytes, "VMK");
            fvekEntryBytes = DecryptAesCcmEntry(vmk, encryptedFvekEntry.Data);
            fvek = ExtractKeyFromDecryptedEntry(fvekEntryBytes, "FVEK");

            decryptedReader = new BitLockerDecryptingReader(encryptedReader, metadata, fvek);
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or CryptographicException)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            ZeroKey(clearKey);
            ZeroKey(vmkEntryBytes);
            ZeroKey(vmk);
            ZeroKey(fvekEntryBytes);
            ZeroKey(fvek);
        }
    }

    public static bool TryCreateReaderWithRecoveryKey(
        IBlockReader encryptedReader,
        BitLockerMetadata metadata,
        ReadOnlySpan<byte> recoveryPasswordKey,
        out IBlockReader? decryptedReader,
        out string error,
        CancellationToken cancellationToken = default)
    {
        decryptedReader = null;
        error = "";

        if (recoveryPasswordKey.Length != BitLockerRecoveryPassword.IntermediateKeySize)
        {
            error = $"BitLocker回復パスワードの中間キーは{BitLockerRecoveryPassword.IntermediateKeySize} bytesである必要があります。";
            return false;
        }

        return TryCreateReaderWithStretchProtector(
            encryptedReader,
            metadata,
            BitLockerProtectionType.RecoveryPassword,
            recoveryPasswordKey,
            inputIsInitialHash: false,
            "回復パスワード",
            out decryptedReader,
            out error,
            cancellationToken);
    }

    public static bool TryCreateReaderWithPassword(
        IBlockReader encryptedReader,
        BitLockerMetadata metadata,
        ReadOnlySpan<char> password,
        out IBlockReader? decryptedReader,
        out string error,
        CancellationToken cancellationToken = default)
    {
        decryptedReader = null;
        if (!BitLockerPassword.TryDeriveInitialHash(password, out var initialHash, out error))
        {
            return false;
        }

        try
        {
            return TryCreateReaderWithStretchProtector(
                encryptedReader,
                metadata,
                BitLockerProtectionType.Password,
                initialHash,
                inputIsInitialHash: true,
                "パスワード",
                out decryptedReader,
                out error,
                cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(initialHash);
        }
    }

    private static bool TryCreateReaderWithStretchProtector(
        IBlockReader encryptedReader,
        BitLockerMetadata metadata,
        BitLockerProtectionType protectionType,
        ReadOnlySpan<byte> keyMaterial,
        bool inputIsInitialHash,
        string credentialName,
        out IBlockReader? decryptedReader,
        out string error,
        CancellationToken cancellationToken)
    {
        decryptedReader = null;
        error = "";

        var recoveryProtectors = metadata.KeyProtectors
            .Where(protector => protector.ProtectionType == protectionType)
            .ToList();
        if (recoveryProtectors.Count == 0)
        {
            error = $"{credentialName}保護子がありません。";
            return false;
        }

        var encryptedFvekEntry = metadata.Entries.FirstOrDefault(
            entry => entry.EntryType == 0x0003 && entry.ValueType == 0x0005);
        if (encryptedFvekEntry is null)
        {
            error = "暗号化FVEKエントリがありません。";
            return false;
        }

        var usableProtectorCount = 0;
        foreach (var protector in recoveryProtectors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stretchEntry = protector.Properties.FirstOrDefault(entry => entry.ValueType == 0x0003);
            if (stretchEntry is null || stretchEntry.Data.Length < 20)
            {
                continue;
            }

            var stretchMethod = EndianUtilities.ReadUInt32Little(stretchEntry.Data, 0);
            if (stretchMethod is not (0x1000 or 0x1001))
            {
                continue;
            }

            var encryptedVmkEntry = protector.Properties.FirstOrDefault(entry => entry.ValueType == 0x0005)
                ?? stretchEntry.Children.FirstOrDefault(entry => entry.ValueType == 0x0005);
            if (encryptedVmkEntry is null)
            {
                continue;
            }

            usableProtectorCount++;
            byte[]? stretchedKey = null;
            byte[]? vmkEntryBytes = null;
            byte[]? vmk = null;
            byte[]? fvekEntryBytes = null;
            byte[]? fvek = null;
            try
            {
                stretchedKey = inputIsInitialHash
                    ? BitLockerRecoveryPassword.DeriveStretchedKeyFromInitialHash(
                        keyMaterial,
                        stretchEntry.Data.AsSpan(4, BitLockerRecoveryPassword.SaltSize),
                        cancellationToken)
                    : BitLockerRecoveryPassword.DeriveStretchedKey(
                        keyMaterial,
                        stretchEntry.Data.AsSpan(4, BitLockerRecoveryPassword.SaltSize),
                        cancellationToken);
                vmkEntryBytes = DecryptAesCcmEntry(stretchedKey, encryptedVmkEntry.Data);
                vmk = ExtractKeyFromDecryptedEntry(vmkEntryBytes, "VMK");
                fvekEntryBytes = DecryptAesCcmEntry(vmk, encryptedFvekEntry.Data);
                fvek = ExtractKeyFromDecryptedEntry(fvekEntryBytes, "FVEK");
                decryptedReader = new BitLockerDecryptingReader(encryptedReader, metadata, fvek);
                return true;
            }
            catch (CryptographicException)
            {
                // A validly formatted password can belong to a different recovery protector.
            }
            catch (InvalidDataException ex)
            {
                error = ex.Message;
            }
            catch (NotSupportedException ex)
            {
                error = ex.Message;
            }
            finally
            {
                ZeroKey(stretchedKey);
                ZeroKey(vmkEntryBytes);
                ZeroKey(vmk);
                ZeroKey(fvekEntryBytes);
                ZeroKey(fvek);
            }
        }

        error = usableProtectorCount == 0
            ? $"{credentialName}保護子にstretch keyまたは暗号化VMKがありません。"
            : string.IsNullOrWhiteSpace(error)
                ? $"{credentialName}がこのBitLocker保護子と一致しません。"
                : error;
        return false;
    }

    public static bool TryCreateReaderWithRawFvek(
        IBlockReader encryptedReader,
        BitLockerMetadata metadata,
        ReadOnlySpan<byte> fvek,
        out IBlockReader? decryptedReader,
        out string error)
    {
        decryptedReader = null;
        error = "";

        try
        {
            var keyCopy = fvek.ToArray();
            try
            {
                decryptedReader = new BitLockerDecryptingReader(encryptedReader, metadata, keyCopy);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(keyCopy);
            }

            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or CryptographicException)
        {
            error = ex.Message;
            return false;
        }
    }

    public static string GetUnlockStatus(BitLockerMetadata metadata)
    {
        if (metadata.KeyProtectors.Count == 0)
        {
            return "BitLocker キー保護子が見つかりません。";
        }

        if (metadata.HasClearKeyProtector)
        {
            return "クリアキー保護子があります。対応形式であれば自動解除します。";
        }

        if (metadata.HasRecoveryPasswordProtector)
        {
            return "回復パスワード保護子があります。48桁の回復パスワードを入力して解除できます。";
        }

        if (metadata.HasPasswordProtector)
        {
            return "パスワード保護子があります。BitLockerパスワードを入力して解除できます。";
        }

        return "対応する解除キーが必要です。TPM のみの保護子はオフライン復号できません。";
    }

    private static byte[] DecryptAesCcmEntry(byte[] key, byte[] data)
    {
        if (key.Length is not (16 or 24 or 32))
        {
            throw new InvalidDataException($"AES-CCM キー長が不正です: {key.Length} bytes");
        }

        if (data.Length <= 28)
        {
            throw new InvalidDataException("AES-CCM エントリが短すぎます。");
        }

        var nonce = data[..12];
        var plaintextLength = data.Length - 12 - 16;
        var plaintext = new byte[plaintextLength];

        if (TryDecryptAesCcm(key, nonce, data[28..], data[12..28], plaintext))
        {
            return plaintext;
        }

        if (TryDecryptAesCcm(key, nonce, data[12..^16], data[^16..], plaintext))
        {
            return plaintext;
        }

        throw new CryptographicException("AES-CCM エントリの復号に失敗しました。");
    }

    private static bool TryDecryptAesCcm(byte[] key, byte[] nonce, byte[] ciphertext, byte[] tag, byte[] plaintext)
    {
        try
        {
#pragma warning disable SYSLIB0053
            using var aesCcm = new AesCcm(key);
#pragma warning restore SYSLIB0053
            aesCcm.Decrypt(nonce, ciphertext, tag, plaintext);
            return true;
        }
        catch (CryptographicException)
        {
            Array.Clear(plaintext);
            return false;
        }
    }

    private static byte[] ExtractKeyFromDecryptedEntry(byte[] entryBytes, string name)
    {
        if (entryBytes.Length >= 8)
        {
            var size = EndianUtilities.ReadUInt16Little(entryBytes, 0);
            var valueType = EndianUtilities.ReadUInt16Little(entryBytes, 4);
            if (size == entryBytes.Length && valueType == 0x0001)
            {
                var keyData = new byte[entryBytes.Length - 8];
                try
                {
                    Array.Copy(entryBytes, 8, keyData, 0, keyData.Length);
                    return ExtractKeyData(keyData, name);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(keyData);
                }
            }
        }

        return ExtractKeyData(entryBytes, name);
    }

    private static byte[] ExtractKeyData(byte[] data, string name)
    {
        if (data.Length < 20)
        {
            throw new InvalidDataException($"{name} エントリが短すぎます。");
        }

        var method = EndianUtilities.ReadUInt32Little(data, 0);
        var key = data[4..];
        if (key.Length is not (16 or 24 or 32 or 64))
        {
            throw new InvalidDataException($"{name} のキー長が不正です: {key.Length} bytes");
        }

        if (method is not (0x0000 or 0x2000 or 0x2001 or 0x2002 or 0x2003 or 0x2004 or 0x2005 or 0x8004 or 0x8005))
        {
            throw new InvalidDataException($"{name} のキー方式が未対応です: {BitLockerMetadataReader.GetEncryptionMethodName(method)}");
        }

        return key;
    }

    private static void ZeroKey(byte[]? key)
    {
        if (key is not null)
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }
}
