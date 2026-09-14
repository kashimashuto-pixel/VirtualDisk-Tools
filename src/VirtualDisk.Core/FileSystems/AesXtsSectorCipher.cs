using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Qcow2Explorer.FileSystems;

internal static class AesXtsSectorCipher
{
    public static byte[] DecryptSector(
        ReadOnlySpan<byte> ciphertext,
        ReadOnlySpan<byte> dataKey,
        ReadOnlySpan<byte> tweakKey,
        ulong sectorNumber)
    {
        if (ciphertext.Length == 0 || ciphertext.Length % 16 != 0)
        {
            throw new InvalidDataException("XTS-AES復号データは16 byte単位である必要があります。");
        }

        if (dataKey.Length != tweakKey.Length || dataKey.Length is not (16 or 32))
        {
            throw new InvalidDataException("XTS-AESキーは同じ長さの128-bitまたは256-bitキー2個である必要があります。");
        }

        var dataKeyBytes = dataKey.ToArray();
        var tweakKeyBytes = tweakKey.ToArray();
        try
        {
            using var dataAes = CreateEcbAes(dataKeyBytes);
            using var tweakAes = CreateEcbAes(tweakKeyBytes);
            using var dataDecryptor = dataAes.CreateDecryptor();
            using var tweakEncryptor = tweakAes.CreateEncryptor();

            Span<byte> tweakInput = stackalloc byte[16];
            BinaryPrimitives.WriteUInt64LittleEndian(tweakInput, sectorNumber);
            Span<byte> tweak = stackalloc byte[16];
            TransformBlock(tweakEncryptor, tweakInput, tweak);

            var plaintext = new byte[ciphertext.Length];
            Span<byte> block = stackalloc byte[16];
            Span<byte> decrypted = stackalloc byte[16];
            try
            {
                for (var offset = 0; offset < ciphertext.Length; offset += 16)
                {
                    for (var index = 0; index < 16; index++)
                    {
                        block[index] = (byte)(ciphertext[offset + index] ^ tweak[index]);
                    }

                    TransformBlock(dataDecryptor, block, decrypted);
                    for (var index = 0; index < 16; index++)
                    {
                        plaintext[offset + index] = (byte)(decrypted[index] ^ tweak[index]);
                    }

                    MultiplyByX(tweak);
                }

                return plaintext;
            }
            catch
            {
                CryptographicOperations.ZeroMemory(plaintext);
                throw;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(block);
                CryptographicOperations.ZeroMemory(decrypted);
                CryptographicOperations.ZeroMemory(tweak);
                CryptographicOperations.ZeroMemory(tweakInput);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKeyBytes);
            CryptographicOperations.ZeroMemory(tweakKeyBytes);
        }
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
        for (var index = 0; index < tweak.Length; index++)
        {
            var value = tweak[index];
            var nextCarry = value >> 7;
            tweak[index] = (byte)((value << 1) | carry);
            carry = nextCarry;
        }

        if (carry != 0)
        {
            tweak[0] ^= 0x87;
        }
    }
}
