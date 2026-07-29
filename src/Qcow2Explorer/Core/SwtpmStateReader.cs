using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Qcow2Explorer.Core;

public static class SwtpmStateReader
{
    private const ulong LinearMagic = 0x737774706d6c696e;
    private const int LinearHeaderSize = 192;
    private const int StateSlotCount = 15;
    private const int BlobHeaderSize = 10;
    private const int TlvHeaderSize = 6;
    private const int MaximumStateSize = 64 * 1024 * 1024;

    public static bool TryRead(IBlockReader reader, out SwtpmStateStore? store, out string error)
    {
        store = null;
        error = "";

        try
        {
            if (reader.Length < LinearHeaderSize)
            {
                error = "swtpm線形ストアのヘッダーより小さいデバイスです。";
                return false;
            }

            var header = new byte[LinearHeaderSize];
            reader.ReadAt(0, header, 0, header.Length);
            if (BinaryPrimitives.ReadUInt64LittleEndian(header) != LinearMagic)
            {
                error = "swtpm線形ストアのシグネチャー (swtpmlin) がありません。";
                return false;
            }

            var version = header[8];
            var declaredHeaderSize = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(10, 2));
            if (version != 1)
            {
                error = $"未対応のswtpm線形ストアversionです: {version}";
                return false;
            }

            if (declaredHeaderSize < LinearHeaderSize || declaredHeaderSize > reader.Length)
            {
                error = $"swtpmヘッダーサイズが不正です: {declaredHeaderSize:N0} bytes";
                return false;
            }

            var sections = new List<SwtpmStateSection>();
            var warnings = new List<string>();
            var allocatedRanges = new List<(long Start, long End, int Index)>();
            for (var index = 0; index < StateSlotCount; index++)
            {
                var entryOffset = 12 + index * 12;
                var offset = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(entryOffset, 4));
                var dataLength = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(entryOffset + 4, 4));
                var sectionLength = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(entryOffset + 8, 4));
                if (offset == 0 && dataLength == 0 && sectionLength == 0)
                {
                    continue;
                }

                if (offset < declaredHeaderSize
                    || dataLength == 0
                    || sectionLength < dataLength
                    || dataLength > MaximumStateSize
                    || (long)offset + sectionLength > reader.Length)
                {
                    error = $"swtpm状態スロット{index}の範囲が不正です。"
                        + $" offset={offset:N0}, data={dataLength:N0}, section={sectionLength:N0}";
                    return false;
                }

                var rangeEnd = (long)offset + sectionLength;
                var overlap = allocatedRanges.FirstOrDefault(range =>
                    offset < range.End && range.Start < rangeEnd);
                if (overlap != default)
                {
                    error = $"swtpm状態スロット{index}がスロット{overlap.Index}と重複しています。";
                    return false;
                }

                allocatedRanges.Add((offset, rangeEnd, index));
                var data = new byte[checked((int)dataLength)];
                reader.ReadAt(offset, data, 0, data.Length);
                var blob = ParseBlob(data, offset, warnings, index);
                sections.Add(new SwtpmStateSection(
                    index,
                    GetStateName(index),
                    offset,
                    dataLength,
                    sectionLength,
                    Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant(),
                    data.AsSpan(0, Math.Min(data.Length, 512)).ToArray(),
                    blob));
            }

            if (sections.Count == 0)
            {
                warnings.Add("割り当て済みのTPM状態セクションがありません。");
            }

            store = new SwtpmStateStore(version, declaredHeaderSize, reader.Length, sections, warnings);
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or OverflowException)
        {
            error = ex.Message;
            return false;
        }
    }

    public static string Describe(SwtpmStateStore store, SwtpmStateSection section)
    {
        var lines = new List<string>
        {
            $"状態: {section.Name} (slot {section.Index})",
            $"オフセット: 0x{section.Offset:X} ({section.Offset:N0})",
            $"データ長: {section.DataLength:N0} bytes",
            $"予約領域: {section.SectionLength:N0} bytes",
            $"SHA-256: {section.Sha256}"
        };

        if (section.Blob is null)
        {
            lines.Add("内部Blob: 認識できません");
        }
        else
        {
            var blob = section.Blob;
            lines.Add("");
            lines.Add($"Blob version: {blob.Version} (minimum {blob.MinimumVersion})");
            lines.Add($"Blobヘッダー: {blob.HeaderSize:N0} bytes");
            lines.Add($"Blob全長: {blob.TotalLength:N0} bytes");
            lines.Add($"フラグ: 0x{blob.Flags:X4} ({FormatFlags(blob.Flags)})");
            lines.Add($"暗号化: {FormatEncryption(blob)}");
            lines.Add($"解析可否: {GetReadability(blob)}");

            if (blob.Tlvs.Count > 0)
            {
                lines.Add("");
                lines.Add("TLV:");
                foreach (var tlv in blob.Tlvs)
                {
                    lines.Add(
                        $"  {tlv.Name} (tag {tlv.Tag}), offset 0x{tlv.Offset:X}, "
                        + $"{tlv.Length:N0} bytes, SHA-256 {tlv.Sha256}");
                }
            }
        }

        lines.Add("");
        lines.Add("状態データ先頭:");
        lines.Add(FormatHex(section.DataPrefix));
        return string.Join(Environment.NewLine, lines);
    }

    public static string FormatEncryption(SwtpmBlob blob)
    {
        if (!blob.IsEncrypted && !blob.IsMigrationEncrypted)
        {
            return "なし";
        }

        var keyBits = blob.Uses256BitKey ? "256-bit" : "128-bit";
        var kinds = new List<string>();
        if (blob.IsEncrypted)
        {
            kinds.Add($"ローカル鍵 ({keyBits})");
        }

        if (blob.IsMigrationEncrypted)
        {
            kinds.Add($"移行鍵 ({(blob.Uses256BitMigrationKey ? "256-bit" : "128-bit")})");
        }

        return string.Join(", ", kinds);
    }

    public static string GetReadability(SwtpmBlob blob)
    {
        if (blob.IsEncrypted || blob.IsMigrationEncrypted)
        {
            return "コンテナー構造は読取可能、TPM内部状態の復号にはswtpmの対応鍵が必要";
        }

        return blob.Tlvs.Any(tlv => tlv.Tag is 1 or 4)
            ? "平文状態データあり（libtpms内部形式）"
            : "コンテナー構造は読取可能";
    }

    private static SwtpmBlob? ParseBlob(
        byte[] data,
        long sectionOffset,
        List<string> warnings,
        int sectionIndex)
    {
        if (data.Length < BlobHeaderSize)
        {
            warnings.Add($"状態スロット{sectionIndex}: 内部Blobヘッダーが途中で終わっています。");
            return null;
        }

        var version = data[0];
        var minimumVersion = data[1];
        var headerSize = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2, 2));
        var flags = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(4, 2));
        var totalLength = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(6, 4));
        if (headerSize < BlobHeaderSize || headerSize > data.Length || totalLength != data.Length)
        {
            warnings.Add(
                $"状態スロット{sectionIndex}: 内部Blobの長さが不正です。"
                + $" header={headerSize:N0}, total={totalLength:N0}, actual={data.Length:N0}");
            return new SwtpmBlob(version, minimumVersion, headerSize, flags, totalLength, [], false);
        }

        if (version != 2)
        {
            warnings.Add($"状態スロット{sectionIndex}: Blob version {version} はTLV解析対象外です。");
            return new SwtpmBlob(version, minimumVersion, headerSize, flags, totalLength, [], true);
        }

        var tlvs = new List<SwtpmTlv>();
        var offset = (int)headerSize;
        var structurallyValid = true;
        while (offset < data.Length)
        {
            if (data.Length - offset < TlvHeaderSize)
            {
                warnings.Add($"状態スロット{sectionIndex}: TLVヘッダーが途中で終わっています (0x{offset:X})。");
                structurallyValid = false;
                break;
            }

            var tag = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));
            var length = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset + 2, 4));
            if (length > int.MaxValue || length > data.Length - offset - TlvHeaderSize)
            {
                warnings.Add($"状態スロット{sectionIndex}: TLV tag {tag} の長さが不正です: {length:N0}");
                structurallyValid = false;
                break;
            }

            var valueOffset = offset + TlvHeaderSize;
            var value = data.AsSpan(valueOffset, checked((int)length));
            tlvs.Add(new SwtpmTlv(
                tag,
                GetTlvName(tag),
                sectionOffset + valueOffset,
                length,
                Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant()));
            offset = valueOffset + checked((int)length);
        }

        return new SwtpmBlob(version, minimumVersion, headerSize, flags, totalLength, tlvs, structurallyValid);
    }

    private static string GetStateName(int index) => index switch
    {
        0 => "Permanent state (permall)",
        1 => "Volatile state",
        2 => "Save state",
        _ => $"State slot {index}"
    };

    private static string GetTlvName(ushort tag) => tag switch
    {
        1 => "Plain data",
        2 => "Encrypted data",
        3 => "HMAC-SHA256",
        4 => "Migration data",
        5 => "Encrypted migration data",
        6 => "IV for encrypted data",
        7 => "IV for encrypted migration data",
        _ => "Unknown"
    };

    private static string FormatFlags(ushort flags)
    {
        var names = new List<string>();
        if ((flags & 0x01) != 0) names.Add("ENCRYPTED");
        if ((flags & 0x02) != 0) names.Add("MIGRATION_ENCRYPTED");
        if ((flags & 0x04) != 0) names.Add("MIGRATION_DATA");
        if ((flags & 0x08) != 0) names.Add("ENCRYPTED_256BIT_KEY");
        if ((flags & 0x10) != 0) names.Add("MIGRATION_256BIT_KEY");
        var unknown = flags & ~0x1f;
        if (unknown != 0) names.Add($"UNKNOWN_0x{unknown:X4}");
        return names.Count == 0 ? "なし" : string.Join(", ", names);
    }

    private static string FormatHex(ReadOnlySpan<byte> data)
    {
        var builder = new StringBuilder();
        for (var offset = 0; offset < data.Length; offset += 16)
        {
            var count = Math.Min(16, data.Length - offset);
            builder.Append($"{offset:X8}  ");
            for (var index = 0; index < 16; index++)
            {
                builder.Append(index < count ? $"{data[offset + index]:X2} " : "   ");
            }

            builder.Append(" ");
            for (var index = 0; index < count; index++)
            {
                var value = data[offset + index];
                builder.Append(value is >= 0x20 and <= 0x7e ? (char)value : '.');
            }

            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }
}

public sealed record SwtpmStateStore(
    byte Version,
    ushort HeaderSize,
    long DeviceLength,
    IReadOnlyList<SwtpmStateSection> Sections,
    IReadOnlyList<string> Warnings);

public sealed record SwtpmStateSection(
    int Index,
    string Name,
    long Offset,
    uint DataLength,
    uint SectionLength,
    string Sha256,
    byte[] DataPrefix,
    SwtpmBlob? Blob);

public sealed record SwtpmBlob(
    byte Version,
    byte MinimumVersion,
    ushort HeaderSize,
    ushort Flags,
    uint TotalLength,
    IReadOnlyList<SwtpmTlv> Tlvs,
    bool StructurallyValid)
{
    public bool IsEncrypted => (Flags & 0x01) != 0;
    public bool IsMigrationEncrypted => (Flags & 0x02) != 0;
    public bool HasMigrationData => (Flags & 0x04) != 0;
    public bool Uses256BitKey => (Flags & 0x08) != 0;
    public bool Uses256BitMigrationKey => (Flags & 0x10) != 0;
}

public sealed record SwtpmTlv(
    ushort Tag,
    string Name,
    long Offset,
    uint Length,
    string Sha256);
