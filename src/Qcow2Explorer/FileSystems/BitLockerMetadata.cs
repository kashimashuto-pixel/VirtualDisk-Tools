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
            foreach (var offset in candidates)
            {
                if (TryReadMetadataBlock(reader, offset, out metadata, out error))
                {
                    return true;
                }
            }

            error = string.IsNullOrWhiteSpace(error)
                ? "有効な BitLocker/FVE メタデータブロックを検出できませんでした。"
                : error;
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

        var metadataBytes = EndianUtilities.ReadBytes(reader, blockOffset + 64, metadataSize);
        var entries = ParseEntries(metadataBytes, metadataHeaderSize, metadataSize).ToList();
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
            EncryptionMethod = EndianUtilities.ReadUInt32Little(metadataBytes, 36),
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

    private static IReadOnlyList<BitLockerMetadataEntry> ParseEntries(byte[] buffer, int startOffset, int endOffset)
    {
        var result = new List<BitLockerMetadataEntry>();
        var offset = startOffset;
        while (offset + 8 <= endOffset)
        {
            var size = EndianUtilities.ReadUInt16Little(buffer, offset);
            var entryType = EndianUtilities.ReadUInt16Little(buffer, offset + 2);
            var valueType = EndianUtilities.ReadUInt16Little(buffer, offset + 4);
            var version = EndianUtilities.ReadUInt16Little(buffer, offset + 6);
            if (size == 0 || entryType == 0 && valueType == 0 && version == 0)
            {
                break;
            }

            if (size < 8 || offset + size > endOffset)
            {
                break;
            }

            var data = new byte[size - 8];
            Array.Copy(buffer, offset + 8, data, 0, data.Length);
            var children = ParseChildEntries(entryType, valueType, data);
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

        return result;
    }

    private static IReadOnlyList<BitLockerMetadataEntry> ParseChildEntries(ushort entryType, ushort valueType, byte[] data)
    {
        if (entryType == 0x0002 && valueType == 0x0008 && data.Length >= 28)
        {
            return ParseEntries(data, 28, data.Length);
        }

        if (valueType == 0x0003 && data.Length >= 20)
        {
            return ParseEntries(data, 20, data.Length);
        }

        if (valueType == 0x0009 && data.Length >= 24)
        {
            return ParseEntries(data, 24, data.Length);
        }

        return Array.Empty<BitLockerMetadataEntry>();
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
