using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Qcow2Explorer.FileSystems;

public sealed class BitLockerStartupKey : IDisposable
{
    private const int HeaderSize = 48;
    private const int EntryHeaderSize = 8;
    private const int ExternalKeyHeaderSize = 24;
    private const int MaximumFileSize = 1024 * 1024;
    private byte[] _key;

    private BitLockerStartupKey(Guid identifier, byte[] key)
    {
        Identifier = identifier;
        _key = key;
    }

    public Guid Identifier { get; }
    public bool IsDisposed => _key.Length == 0;
    internal ReadOnlySpan<byte> Key => _key;

    public static bool TryRead(string path, out BitLockerStartupKey? startupKey, out string error)
    {
        startupKey = null;
        error = "";
        byte[]? fileBytes = null;
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                error = "BitLockerスタートアップキーファイルを選択してください。";
                return false;
            }

            var fileInfo = new FileInfo(path);
            if (!fileInfo.Exists)
            {
                error = "BitLockerスタートアップキーファイルが見つかりません。";
                return false;
            }

            if (fileInfo.Length < HeaderSize + EntryHeaderSize || fileInfo.Length > MaximumFileSize)
            {
                error = $"BitLockerスタートアップキーファイルのサイズが不正です: {fileInfo.Length} bytes";
                return false;
            }

            fileBytes = File.ReadAllBytes(path);
            return TryParse(fileBytes, out startupKey, out error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            if (fileBytes is not null)
            {
                CryptographicOperations.ZeroMemory(fileBytes);
            }
        }
    }

    public static bool TryParse(ReadOnlySpan<byte> data, out BitLockerStartupKey? startupKey, out string error)
    {
        startupKey = null;
        error = "";
        if (data.Length < HeaderSize + EntryHeaderSize || data.Length > MaximumFileSize)
        {
            error = $"BitLockerスタートアップキーデータのサイズが不正です: {data.Length} bytes";
            return false;
        }

        var metadataSize = BinaryPrimitives.ReadUInt32LittleEndian(data);
        var version = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
        var metadataSizeCopy = BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);
        if (version != 1 || headerSize != HeaderSize)
        {
            error = $"未対応のBitLockerスタートアップキーヘッダーです: version={version}, header={headerSize}";
            return false;
        }

        if (metadataSize != data.Length || metadataSizeCopy != metadataSize)
        {
            error = $"BitLockerスタートアップキーのメタデータサイズが一致しません: size={metadataSize}, copy={metadataSizeCopy}, file={data.Length}";
            return false;
        }

        var headerIdentifier = new Guid(data.Slice(16, 16));
        Guid? externalIdentifier = null;
        byte[]? externalKey = null;
        var offset = HeaderSize;
        while (offset < data.Length)
        {
            if (!TryReadEntry(data, offset, data.Length, out var size, out var entryType, out var valueType, out var entryVersion, out var entryError))
            {
                error = entryError;
                Zero(externalKey);
                return false;
            }

            if (size == 0)
            {
                if (!data[offset..].IsEmpty && !IsZeroFilled(data[offset..]))
                {
                    error = $"BitLockerスタートアップキーの終端データが不正です: offset=0x{offset:X}";
                    Zero(externalKey);
                    return false;
                }

                break;
            }

            if (entryType != 0x0006 || valueType != 0x0009 || entryVersion != 1)
            {
                error = $"BitLockerスタートアップキーに未対応のトップレベルエントリがあります: type=0x{entryType:X4}, value=0x{valueType:X4}, version={entryVersion}";
                Zero(externalKey);
                return false;
            }

            if (externalIdentifier is not null)
            {
                error = "BitLockerスタートアップキーに外部キーエントリが複数あります。";
                Zero(externalKey);
                return false;
            }

            if (size < EntryHeaderSize + ExternalKeyHeaderSize)
            {
                error = "BitLocker外部キーエントリが短すぎます。";
                return false;
            }

            externalIdentifier = new Guid(data.Slice(offset + EntryHeaderSize, 16));
            var childOffset = offset + EntryHeaderSize + ExternalKeyHeaderSize;
            var entryEnd = offset + size;
            while (childOffset < entryEnd)
            {
                if (!TryReadEntry(data, childOffset, entryEnd, out var childSize, out var childType, out var childValueType, out var childVersion, out entryError))
                {
                    error = entryError;
                    Zero(externalKey);
                    return false;
                }

                if (childSize == 0)
                {
                    if (!IsZeroFilled(data[childOffset..entryEnd]))
                    {
                        error = $"BitLocker外部キーの終端データが不正です: offset=0x{childOffset:X}";
                        Zero(externalKey);
                        return false;
                    }

                    break;
                }

                if (childVersion != 1)
                {
                    error = $"BitLocker外部キーのプロパティversionが不正です: type=0x{childType:X4}, version={childVersion}";
                    Zero(externalKey);
                    return false;
                }

                if (childValueType == 0x0001)
                {
                    if (childType != 0)
                    {
                        error = $"BitLocker外部キーのキープロパティtypeが不正です: 0x{childType:X4}";
                        Zero(externalKey);
                        return false;
                    }

                    if (externalKey is not null)
                    {
                        error = "BitLocker外部キーにキープロパティが複数あります。";
                        Zero(externalKey);
                        return false;
                    }

                    var keyData = data.Slice(childOffset + EntryHeaderSize, childSize - EntryHeaderSize);
                    var keyMethod = keyData.Length >= 4
                        ? BinaryPrimitives.ReadUInt32LittleEndian(keyData)
                        : uint.MaxValue;
                    if (keyData.Length != 36 || keyMethod is not (0x0000 or >= 0x2000 and <= 0x2005))
                    {
                        error = "BitLocker外部キーは未対応の方式またはキー長です。";
                        return false;
                    }

                    externalKey = keyData[4..].ToArray();
                }

                childOffset += childSize;
            }

            offset += size;
        }

        if (externalIdentifier is null || externalKey is null)
        {
            error = "BitLockerスタートアップキーに外部キー識別子または256-bitキーがありません。";
            Zero(externalKey);
            return false;
        }

        if (headerIdentifier != externalIdentifier.Value)
        {
            error = "BitLockerスタートアップキーのヘッダーと外部キーの識別子が一致しません。";
            Zero(externalKey);
            return false;
        }

        startupKey = new BitLockerStartupKey(externalIdentifier.Value, externalKey);
        return true;
    }

    public void Dispose()
    {
        Zero(_key);
        _key = Array.Empty<byte>();
    }

    private static bool TryReadEntry(
        ReadOnlySpan<byte> data,
        int offset,
        int endOffset,
        out int size,
        out ushort entryType,
        out ushort valueType,
        out ushort version,
        out string error)
    {
        size = 0;
        entryType = 0;
        valueType = 0;
        version = 0;
        error = "";
        if (offset < 0 || endOffset > data.Length || offset + EntryHeaderSize > endOffset)
        {
            error = $"BitLockerスタートアップキーのエントリヘッダーが切り詰められています: offset=0x{offset:X}";
            return false;
        }

        size = BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
        entryType = BinaryPrimitives.ReadUInt16LittleEndian(data[(offset + 2)..]);
        valueType = BinaryPrimitives.ReadUInt16LittleEndian(data[(offset + 4)..]);
        version = BinaryPrimitives.ReadUInt16LittleEndian(data[(offset + 6)..]);
        if (size == 0 && entryType == 0 && valueType == 0 && version == 0)
        {
            return true;
        }

        if (size < EntryHeaderSize || size > endOffset - offset)
        {
            error = $"BitLockerスタートアップキーのエントリ範囲が不正です: offset=0x{offset:X}, size={size}";
            return false;
        }

        return true;
    }

    private static bool IsZeroFilled(ReadOnlySpan<byte> data)
    {
        foreach (var value in data)
        {
            if (value != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static void Zero(byte[]? data)
    {
        if (data is not null)
        {
            CryptographicOperations.ZeroMemory(data);
        }
    }
}
