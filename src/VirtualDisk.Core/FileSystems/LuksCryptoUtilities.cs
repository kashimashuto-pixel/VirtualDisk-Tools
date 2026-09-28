using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Qcow2Explorer.FileSystems;

internal static class LuksCryptoUtilities
{
    public const int SectorSize = 512;

    public static bool TryGetHashAlgorithm(string hashSpec, out HashAlgorithmName algorithm, out int digestSize)
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

    public static byte[] DecryptKeyMaterial(byte[] ciphertext, byte[] xtsKey)
    {
        if (ciphertext.Length == 0 || ciphertext.Length % SectorSize != 0)
        {
            throw new InvalidDataException("LUKS key materialは512 byte単位である必要があります。");
        }

        var half = xtsKey.Length / 2;
        var plaintext = new byte[ciphertext.Length];
        try
        {
            for (var offset = 0; offset < ciphertext.Length; offset += SectorSize)
            {
                var sector = AesXtsSectorCipher.DecryptSector(
                    ciphertext.AsSpan(offset, SectorSize),
                    xtsKey.AsSpan(0, half),
                    xtsKey.AsSpan(half),
                    checked((ulong)(offset / SectorSize)));
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

    public static byte[] MergeAntiForensicStripes(
        ReadOnlySpan<byte> splitKey,
        int keyBytes,
        int stripes,
        HashAlgorithmName hashAlgorithm,
        int digestSize)
    {
        if (stripes < 2 || splitKey.Length != checked(keyBytes * stripes))
        {
            throw new InvalidDataException("LUKS anti-forensic key materialのサイズが不正です。");
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
                        throw new CryptographicException("LUKS AF diffuse hashの計算に失敗しました。");
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

    public static void Zero(byte[]? data)
    {
        if (data is not null)
        {
            CryptographicOperations.ZeroMemory(data);
        }
    }
}
