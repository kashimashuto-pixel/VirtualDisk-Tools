using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Qcow2Explorer.FileSystems;

public static class BitLockerRecoveryPassword
{
    public const int BlockCount = 8;
    public const int DigitsPerBlock = 6;
    public const int IntermediateKeySize = BlockCount * sizeof(ushort);
    public const int SaltSize = 16;
    public const int StretchedKeySize = 32;

    private const int MaximumBlockValue = 720885;
    private const int StretchIterationCount = 0x100000;

    public static bool TryDecode(string? password, out byte[] intermediateKey, out string error)
    {
        intermediateKey = Array.Empty<byte>();
        error = "";

        if (string.IsNullOrWhiteSpace(password))
        {
            error = "48桁のBitLocker回復パスワードを入力してください。";
            return false;
        }

        Span<char> digits = stackalloc char[BlockCount * DigitsPerBlock];
        var digitCount = 0;
        foreach (var character in password)
        {
            if (character is >= '0' and <= '9')
            {
                if (digitCount >= digits.Length)
                {
                    error = "BitLocker回復パスワードは48桁で入力してください。";
                    return false;
                }

                digits[digitCount++] = character;
            }
            else if (character != '-' && !char.IsWhiteSpace(character))
            {
                error = $"回復パスワードに使用できない文字があります: '{character}'";
                return false;
            }
        }

        if (digitCount != digits.Length)
        {
            error = $"BitLocker回復パスワードは48桁必要です。現在は{digitCount}桁です。";
            return false;
        }

        var decoded = new byte[IntermediateKeySize];
        for (var blockIndex = 0; blockIndex < BlockCount; blockIndex++)
        {
            var block = digits.Slice(blockIndex * DigitsPerBlock, DigitsPerBlock);
            var value = 0;
            foreach (var digit in block)
            {
                value = checked(value * 10 + digit - '0');
            }

            if (value > MaximumBlockValue)
            {
                error = $"第{blockIndex + 1}ブロックは720885以下である必要があります。";
                CryptographicOperations.ZeroMemory(decoded);
                return false;
            }

            if (value % 11 != 0)
            {
                error = $"第{blockIndex + 1}ブロックのチェックサムが一致しません。6桁の入力を確認してください。";
                CryptographicOperations.ZeroMemory(decoded);
                return false;
            }

            var expectedCheckDigit = Modulo11(
                block[0] - '0'
                - (block[1] - '0')
                + block[2] - '0'
                - (block[3] - '0')
                + block[4] - '0');
            if (expectedCheckDigit > 9 || block[5] - '0' != expectedCheckDigit)
            {
                error = $"第{blockIndex + 1}ブロックの末尾チェック数字が一致しません。";
                CryptographicOperations.ZeroMemory(decoded);
                return false;
            }

            BinaryPrimitives.WriteUInt16LittleEndian(
                decoded.AsSpan(blockIndex * sizeof(ushort), sizeof(ushort)),
                checked((ushort)(value / 11)));
        }

        intermediateKey = decoded;
        return true;
    }

    public static byte[] DeriveStretchedKey(
        ReadOnlySpan<byte> intermediateKey,
        ReadOnlySpan<byte> salt,
        CancellationToken cancellationToken = default)
    {
        if (intermediateKey.Length != IntermediateKeySize)
        {
            throw new ArgumentException(
                $"BitLocker回復パスワードの中間キーは{IntermediateKeySize} bytesである必要があります。",
                nameof(intermediateKey));
        }

        if (salt.Length != SaltSize)
        {
            throw new ArgumentException(
                $"BitLocker stretch keyのsaltは{SaltSize} bytesである必要があります。",
                nameof(salt));
        }

        var state = new byte[32 + 32 + SaltSize + sizeof(ulong)];
        var nextHash = new byte[StretchedKeySize];
        try
        {
            SHA256.HashData(intermediateKey, state.AsSpan(32, 32));
            salt.CopyTo(state.AsSpan(64, SaltSize));

            for (var iteration = 0; iteration < StretchIterationCount; iteration++)
            {
                if ((iteration & 0x3fff) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                BinaryPrimitives.WriteUInt64LittleEndian(state.AsSpan(80, sizeof(ulong)), checked((ulong)iteration));
                SHA256.HashData(state, nextHash);
                nextHash.CopyTo(state, 0);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return state.AsSpan(0, StretchedKeySize).ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(state);
            CryptographicOperations.ZeroMemory(nextHash);
        }
    }

    private static int Modulo11(int value)
    {
        var result = value % 11;
        return result < 0 ? result + 11 : result;
    }
}
