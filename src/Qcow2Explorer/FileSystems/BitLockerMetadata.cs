using Qcow2Explorer.Core;

namespace Qcow2Explorer.FileSystems;

public sealed class BitLockerMetadata
{
    public Guid VolumeIdentifier { get; init; }
    public int BlockHeaderVersion { get; init; }
    public int MetadataVersion { get; init; }
    public int MetadataSize { get; init; }
    public int MetadataHeaderSize { get; init; }
    public long MetadataBlockOffset { get; init; }
    public long[] MetadataBlockOffsets { get; init; } = Array.Empty<long>();
    public long VolumeHeaderOffset { get; init; }
    public int VolumeHeaderSectors { get; init; }
    public long EncryptedVolumeSize { get; init; }
    public uint EncryptionMethod { get; init; }
    public string EncryptionMethodName => BitLockerMetadataReader.GetEncryptionMethodName(EncryptionMethod);
    public DateTime? CreatedUtc { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<BitLockerKeyProtector> KeyProtectors { get; init; } = Array.Empty<BitLockerKeyProtector>();
    public IReadOnlyList<BitLockerMetadataEntry> Entries { get; init; } = Array.Empty<BitLockerMetadataEntry>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public bool HasClearKeyProtector => KeyProtectors.Any(p => p.ProtectionType == BitLockerProtectionType.ClearKey);
    public bool HasRecoveryPasswordProtector => KeyProtectors.Any(p => p.ProtectionType == BitLockerProtectionType.RecoveryPassword);
    public bool HasPasswordProtector => KeyProtectors.Any(p => p.ProtectionType == BitLockerProtectionType.Password);
}

public sealed class BitLockerKeyProtector
{
    public Guid Identifier { get; init; }
    public DateTime? ModifiedUtc { get; init; }
    public BitLockerProtectionType ProtectionType { get; init; } = BitLockerProtectionType.Unknown;
    public ushort RawProtectionType { get; init; }
    public string? Description { get; init; }
    public bool HasClearKey { get; init; }
    public bool HasStretchKey { get; init; }
    public bool HasEncryptedKey { get; init; }
    public bool HasExternalKey { get; init; }
    public IReadOnlyList<BitLockerMetadataEntry> Properties { get; init; } = Array.Empty<BitLockerMetadataEntry>();

    public string ProtectionName => BitLockerMetadataReader.GetProtectionTypeName(RawProtectionType);
}

public sealed class BitLockerMetadataEntry
{
    public int Offset { get; init; }
    public ushort Size { get; init; }
    public ushort EntryType { get; init; }
    public ushort ValueType { get; init; }
    public ushort Version { get; init; }
    public byte[] Data { get; init; } = Array.Empty<byte>();
    public IReadOnlyList<BitLockerMetadataEntry> Children { get; init; } = Array.Empty<BitLockerMetadataEntry>();

    public string EntryTypeName => BitLockerMetadataReader.GetEntryTypeName(EntryType);
    public string ValueTypeName => BitLockerMetadataReader.GetValueTypeName(ValueType);
}

public enum BitLockerProtectionType
{
    Unknown,
    ClearKey,
    Tpm,
    StartupKey,
    TpmAndPin,
    RecoveryPassword,
    Password
}

public static class BitLockerMetadataReader
{
    private const string FveSignature = "-FVE-FS-";

    public static bool TryRead(IBlockReader reader, out BitLockerMetadata? metadata, out string error)
    {
        metadata = null;
        error = "";

        try
        {
            var boot = EndianUtilities.ReadBytes(reader, 0, 512);
            if (EndianUtilities.ReadAscii(boot, 3, 8) != FveSignature)
            {
                error = "BitLocker/FVE ボリュームヘッダーではありません。";
                return false;
            }

            var candidates = GetMetadataOffsetCandidates(boot, reader.Length);
            var candidateErrors = new List<string>();
            foreach (var offset in candidates)
            {
                try
                {
                    if (TryReadMetadataBlock(reader, offset, out metadata, out var candidateError))
                    {
                        return true;
                    }

                    candidateErrors.Add($"0x{offset:X}: {candidateError}");
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentOutOfRangeException or OverflowException)
                {
                    metadata = null;
                    candidateErrors.Add($"0x{offset:X}: {ex.Message}");
                }
            }

            error = candidateErrors.Count == 0
                ? "有効な BitLocker/FVE メタデータブロックを検出できませんでした。"
                : $"有効な BitLocker/FVE メタデータブロックを検出できませんでした: {string.Join("; ", candidateErrors)}";
            return false;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentOutOfRangeException or OverflowException)
        {
            error = ex.Message;
            return false;
        }
    }

    public static string GetEncryptionMethodName(uint value)
    {
        return value switch
        {
            0x0000 => "未暗号化/外部キー",
            0x1000 => "Stretch key",
            0x1001 => "Stretch key",
            0x2000 => "AES-CCM 256-bit",
            0x2001 => "AES-CCM 256-bit",
            0x2002 => "AES-CCM 256-bit",
            0x2003 => "AES-CCM 256-bit",
            0x2004 => "AES-CCM 256-bit",
            0x2005 => "AES-CCM 256-bit",
            0x8000 => "AES-CBC 128-bit + Elephant Diffuser",
            0x8001 => "AES-CBC 256-bit + Elephant Diffuser",
            0x8002 => "AES-CBC 128-bit",
            0x8003 => "AES-CBC 256-bit",
            0x8004 => "XTS-AES 128-bit",
            0x8005 => "XTS-AES 256-bit",
            _ => $"Unknown 0x{value:X4}"
        };
    }

    public static string GetEntryTypeName(ushort value)
    {
        return value switch
        {
            0x0000 => "Property",
            0x0002 => "VMK",
            0x0003 => "FVEK",
            0x0004 => "Validation",
            0x0006 => "Startup key",
            0x0007 => "Description",
            0x000b => "Backup FVEK",
            0x000f => "Volume header block",
            _ => $"Unknown 0x{value:X4}"
        };
    }

    public static string GetValueTypeName(ushort value)
    {
        return value switch
        {
            0x0000 => "Erased",
            0x0001 => "Key",
            0x0002 => "Unicode string",
            0x0003 => "Stretch key",
            0x0004 => "Use key",
            0x0005 => "AES-CCM encrypted key",
            0x0006 => "TPM encoded key",
            0x0007 => "Validation",
            0x0008 => "VMK",
            0x0009 => "External key",
            0x000a => "Update",
            0x000b => "Error",
            0x000f => "Offset and size",
            _ => $"Unknown 0x{value:X4}"
        };
    }

    public static string GetProtectionTypeName(ushort value)
    {
        return value switch
        {
            0x0000 => "Clear key",
            0x0100 => "TPM",
            0x0200 => "Startup key",
            0x0500 => "TPM + PIN",
            0x0800 => "Recovery password",
            0x2000 => "Password",
            _ => $"Unknown 0x{value:X4}"
        };
    }

    private static IReadOnlyList<long> GetMetadataOffsetCandidates(byte[] boot, long readerLength)
    {
        var result = new List<long>();

        AddOffset(EndianUtilities.ReadUInt64Little(boot, 176));
        AddOffset(EndianUtilities.ReadUInt64Little(boot, 184));
        AddOffset(EndianUtilities.ReadUInt64Little(boot, 192));

        AddOffset(EndianUtilities.ReadUInt64Little(boot, 440));
        AddOffset(EndianUtilities.ReadUInt64Little(boot, 448));
        AddOffset(EndianUtilities.ReadUInt64Little(boot, 456));

        var vistaCluster = EndianUtilities.ReadUInt64Little(boot, 56);
        if (vistaCluster > 0 && vistaCluster < (ulong)(readerLength / 512))
        {
            AddOffset(vistaCluster * 512);
        }

        return result;

        void AddOffset(ulong value)
        {
            if (value == 0 || value > long.MaxValue || value >= (ulong)readerLength)
            {
                return;
            }

            var offset = (long)value;
            if (!result.Contains(offset))
            {
                result.Add(offset);
            }
        }
    }

    private static bool TryReadMetadataBlock(IBlockReader reader, long blockOffset, out BitLockerMetadata? metadata, out string error)
    {
        metadata = null;
        error = "";
        if (blockOffset < 0 || blockOffset + 128 > reader.Length)
        {
            error = $"メタデータオフセットが範囲外です: 0x{blockOffset:X}";
            return false;
        }

        var blockHeader = EndianUtilities.ReadBytes(reader, blockOffset, 64);
        if (EndianUtilities.ReadAscii(blockHeader, 0, 8) != FveSignature)
        {
            error = $"メタデータブロック署名が一致しません: 0x{blockOffset:X}";
            return false;
        }

        var blockHeaderVersion = EndianUtilities.ReadUInt16Little(blockHeader, 10);
        if (blockHeaderVersion is not (1 or 2))
        {
            error = $"未対応の BitLocker メタデータブロックバージョンです: {blockHeaderVersion}";
            return false;
        }

        var metadataHeader = EndianUtilities.ReadBytes(reader, blockOffset + 64, 48);
        var metadataSize = checked((int)EndianUtilities.ReadUInt32Little(metadataHeader, 0));
        var metadataVersion = checked((int)EndianUtilities.ReadUInt32Little(metadataHeader, 4));
        var metadataHeaderSize = checked((int)EndianUtilities.ReadUInt32Little(metadataHeader, 8));
        if (metadataSize < 48 || metadataSize > 1024 * 1024 || metadataHeaderSize < 48 || metadataHeaderSize > metadataSize)
        {
            error = $"BitLocker メタデータサイズが不正です: size={metadataSize}, header={metadataHeaderSize}";
            return false;
        }

        var metadataOffset = checked(blockOffset + 64);
        if (metadataSize > reader.Length - metadataOffset)
        {
            error = $"BitLocker メタデータが入力範囲を超えています: offset=0x{metadataOffset:X}, size={metadataSize}";
            return false;
        }

        var metadataBytes = EndianUtilities.ReadBytes(reader, metadataOffset, metadataSize);
        if (!TryParseEntries(metadataBytes, metadataHeaderSize, metadataSize, 0, out var entries, out var entryError))
        {
            error = $"BitLocker メタデータエントリが不正です: {entryError}";
            return false;
        }

        // Windows can repeat the 16-bit algorithm identifier in the upper word.
        var encryptionMethod = EndianUtilities.ReadUInt32Little(metadataBytes, 36) & ushort.MaxValue;
        metadata = new BitLockerMetadata
        {
            VolumeIdentifier = new Guid(metadataBytes.AsSpan(16, 16)),
            BlockHeaderVersion = blockHeaderVersion,
            MetadataVersion = metadataVersion,
            MetadataSize = metadataSize,
            MetadataHeaderSize = metadataHeaderSize,
            MetadataBlockOffset = blockOffset,
            MetadataBlockOffsets = GetBlockOffsets(blockHeader, blockHeaderVersion),
            VolumeHeaderOffset = blockHeaderVersion == 2 ? checked((long)EndianUtilities.ReadUInt64Little(blockHeader, 56)) : 0,
            VolumeHeaderSectors = blockHeaderVersion == 2 ? checked((int)EndianUtilities.ReadUInt32Little(blockHeader, 28)) : 0,
            EncryptedVolumeSize = blockHeaderVersion == 2 ? checked((long)EndianUtilities.ReadUInt64Little(blockHeader, 16)) : 0,
            EncryptionMethod = encryptionMethod,
            CreatedUtc = ReadFileTime(metadataBytes, 40),
            Description = ReadDescription(entries),
            KeyProtectors = ReadKeyProtectors(entries),
            Entries = entries,
            Warnings = BuildWarnings(entries)
        };
        return true;
    }

    private static long[] GetBlockOffsets(byte[] blockHeader, int blockHeaderVersion)
    {
        var offset = blockHeaderVersion == 2 ? 32 : 32;
        return new[]
        {
            checked((long)EndianUtilities.ReadUInt64Little(blockHeader, offset)),
            checked((long)EndianUtilities.ReadUInt64Little(blockHeader, offset + 8)),
            checked((long)EndianUtilities.ReadUInt64Little(blockHeader, offset + 16))
        };
    }

    private static bool TryParseEntries(
        byte[] buffer,
        int startOffset,
        int endOffset,
        int depth,
        out IReadOnlyList<BitLockerMetadataEntry> entries,
        out string error)
    {
        var result = new List<BitLockerMetadataEntry>();
        entries = result;
        error = "";
        if (depth > 32)
        {
            error = "メタデータエントリの入れ子が深すぎます。";
            return false;
        }

        var offset = startOffset;
        while (offset + 8 <= endOffset)
        {
            var size = EndianUtilities.ReadUInt16Little(buffer, offset);
            var entryType = EndianUtilities.ReadUInt16Little(buffer, offset + 2);
            var valueType = EndianUtilities.ReadUInt16Little(buffer, offset + 4);
            var version = EndianUtilities.ReadUInt16Little(buffer, offset + 6);
            if (size == 0 || entryType == 0 && valueType == 0 && version == 0)
            {
                if (!IsZeroFilled(buffer, offset, endOffset))
                {
                    error = $"オフセット0x{offset:X}以降に不正な終端データがあります。";
                    return false;
                }

                return true;
            }

            if (size < 8 || offset + size > endOffset)
            {
                error = $"エントリ範囲が不正です: offset=0x{offset:X}, size={size}, end=0x{endOffset:X}";
                return false;
            }

            var data = new byte[size - 8];
            Array.Copy(buffer, offset + 8, data, 0, data.Length);
            if (!TryParseChildEntries(entryType, valueType, data, depth, out var children, out var childError))
            {
                error = $"オフセット0x{offset:X}の子エントリが不正です: {childError}";
                return false;
            }

            result.Add(new BitLockerMetadataEntry
            {
                Offset = offset,
                Size = size,
                EntryType = entryType,
                ValueType = valueType,
                Version = version,
                Data = data,
                Children = children
            });

            offset += size;
        }

        if (!IsZeroFilled(buffer, offset, endOffset))
        {
            error = $"オフセット0x{offset:X}に切り詰められたエントリがあります。";
            return false;
        }

        return true;
    }

    private static bool TryParseChildEntries(
        ushort entryType,
        ushort valueType,
        byte[] data,
        int depth,
        out IReadOnlyList<BitLockerMetadataEntry> children,
        out string error)
    {
        if (entryType == 0x0002 && valueType == 0x0008 && data.Length >= 28)
        {
            return TryParseEntries(data, 28, data.Length, depth + 1, out children, out error);
        }

        if (valueType == 0x0003 && data.Length >= 20)
        {
            return TryParseEntries(data, 20, data.Length, depth + 1, out children, out error);
        }

        if (valueType == 0x0009 && data.Length >= 24)
        {
            return TryParseEntries(data, 24, data.Length, depth + 1, out children, out error);
        }

        children = Array.Empty<BitLockerMetadataEntry>();
        error = "";
        return true;
    }

    private static bool IsZeroFilled(byte[] buffer, int startOffset, int endOffset)
    {
        for (var offset = startOffset; offset < endOffset; offset++)
        {
            if (buffer[offset] != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static IReadOnlyList<BitLockerKeyProtector> ReadKeyProtectors(IReadOnlyList<BitLockerMetadataEntry> entries)
    {
        var protectors = new List<BitLockerKeyProtector>();
        foreach (var entry in entries.Where(e => e.EntryType == 0x0002 && e.ValueType == 0x0008 && e.Data.Length >= 28))
        {
            var rawProtection = EndianUtilities.ReadUInt16Little(entry.Data, 26);
            protectors.Add(new BitLockerKeyProtector
            {
                Identifier = new Guid(entry.Data.AsSpan(0, 16)),
                ModifiedUtc = ReadFileTime(entry.Data, 16),
                RawProtectionType = rawProtection,
                ProtectionType = MapProtectionType(rawProtection),
                Description = ReadDescription(entry.Children),
                HasClearKey = entry.Children.Any(e => e.ValueType == 0x0001),
                HasStretchKey = entry.Children.Any(e => e.ValueType == 0x0003),
                HasEncryptedKey = entry.Children.Any(e => e.ValueType == 0x0005 || e.Children.Any(c => c.ValueType == 0x0005)),
                HasExternalKey = entry.Children.Any(e => e.ValueType == 0x0009),
                Properties = entry.Children
            });
        }

        return protectors;
    }

    private static string? ReadDescription(IReadOnlyList<BitLockerMetadataEntry> entries)
    {
        var description = entries.FirstOrDefault(e => e.ValueType == 0x0002);
        if (description is null || description.Data.Length < 2)
        {
            return null;
        }

        var bytes = description.Data;
        var length = bytes.Length;
        while (length >= 2 && bytes[length - 1] == 0 && bytes[length - 2] == 0)
        {
            length -= 2;
        }

        return length <= 0 ? null : System.Text.Encoding.Unicode.GetString(bytes, 0, length);
    }

    private static BitLockerProtectionType MapProtectionType(ushort value)
    {
        return value switch
        {
            0x0000 => BitLockerProtectionType.ClearKey,
            0x0100 => BitLockerProtectionType.Tpm,
            0x0200 => BitLockerProtectionType.StartupKey,
            0x0500 => BitLockerProtectionType.TpmAndPin,
            0x0800 => BitLockerProtectionType.RecoveryPassword,
            0x2000 => BitLockerProtectionType.Password,
            _ => BitLockerProtectionType.Unknown
        };
    }

    private static DateTime? ReadFileTime(byte[] data, int offset)
    {
        if (offset + 8 > data.Length)
        {
            return null;
        }

        var value = EndianUtilities.ReadUInt64Little(data, offset);
        if (value == 0 || value > long.MaxValue)
        {
            return null;
        }

        try
        {
            return DateTime.FromFileTimeUtc((long)value);
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<string> BuildWarnings(IReadOnlyList<BitLockerMetadataEntry> entries)
    {
        var warnings = new List<string>();
        if (!entries.Any(e => e.EntryType == 0x0003))
        {
            warnings.Add("FVEK エントリを検出できませんでした。");
        }

        if (!entries.Any(e => e.EntryType == 0x0002))
        {
            warnings.Add("VMK エントリを検出できませんでした。");
        }

        return warnings;
    }
}
