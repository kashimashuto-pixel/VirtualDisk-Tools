using System.Security.Cryptography;
using System.Text;

namespace Qcow2Explorer.FileSystems;

public static class BitLockerPassword
{
    public static bool TryDeriveInitialHash(
        ReadOnlySpan<char> password,
        out byte[] initialHash,
        out string error)
    {
        initialHash = Array.Empty<byte>();
        error = "";
        if (password.IsEmpty)
        {
            error = "BitLockerパスワードを入力してください。";
            return false;
        }

        var passwordBytes = new byte[Encoding.Unicode.GetByteCount(password)];
        byte[]? firstHash = null;
        try
        {
            Encoding.Unicode.GetBytes(password, passwordBytes);
            firstHash = SHA256.HashData(passwordBytes);
            initialHash = SHA256.HashData(firstHash);
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
            if (firstHash is not null)
            {
                CryptographicOperations.ZeroMemory(firstHash);
            }
        }
    }
}
