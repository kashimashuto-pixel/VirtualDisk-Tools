using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;

namespace Qcow2Explorer.Core;

public static partial class UefiVariableStoreReader
{
    private const uint FirmwareVolumeSignature = 0x4856465f;
    private const ushort VariableDataStartId = 0x55aa;
    private const int VariableStoreHeaderSize = 28;
    private const int VariableHeaderSize = 32;
    private const int AuthenticatedVariableHeaderSize = 60;
    private const int MaximumStoreSize = 64 * 1024 * 1024;

    private static readonly Guid SystemNvDataFirmwareVolumeGuid = new("fff12b8d-7696-4c8b-a985-2747075b4f50");
    private static readonly Guid VariableStoreGuid = new("ddcf3616-3275-4164-98b6-fe85707ffe7d");
    private static readonly Guid AuthenticatedVariableStoreGuid = new("aaf32c78-947b-439a-a180-2e144ec37792");
    private static readonly Guid X509SignatureGuid = new("a5c059a1-94e4-4aa7-87b5-ab155c2bf072");
    private static readonly Guid Sha256SignatureGuid = new("c1c41626-504c-4092-aca9-41f936934328");

    public static bool TryRead(IBlockReader reader, out UefiVariableStore? store, out string error)
    {
        store = null;
        error = "";
        if (reader.Length < 100)
        {
            error = "UEFI変数ストアとしては短すぎます。";
            return false;
        }

        try
        {
            var prefix = ReadExact(reader, 0, 56);
            if (BinaryPrimitives.ReadUInt32LittleEndian(prefix.AsSpan(40, 4)) != FirmwareVolumeSignature)
            {
                error = "Firmware Volumeシグネチャ (_FVH) がありません。";
                return false;
            }

            var fileSystemGuid = new Guid(prefix.AsSpan(16, 16));
            var firmwareVolumeLengthValue = BinaryPrimitives.ReadUInt64LittleEndian(prefix.AsSpan(32, 8));
            var headerLength = BinaryPrimitives.ReadUInt16LittleEndian(prefix.AsSpan(48, 2));
            if (firmwareVolumeLengthValue > long.MaxValue)
            {
                throw new InvalidDataException("Firmware Volumeサイズが.NETの範囲を超えています。");
            }

            var firmwareVolumeLength = (long)firmwareVolumeLengthValue;
            if (headerLength < 56 || firmwareVolumeLength < headerLength + VariableStoreHeaderSize)
            {
                throw new InvalidDataException(
                    $"Firmware Volumeヘッダーの範囲が不正です: header={headerLength:N0}, volume={firmwareVolumeLength:N0}");
            }

            if (firmwareVolumeLength > reader.Length || firmwareVolumeLength > MaximumStoreSize)
            {
                throw new InvalidDataException(
                    $"Firmware Volumeサイズが読み取り可能範囲を超えています: {firmwareVolumeLength:N0} bytes");
            }

            var image = ReadExact(reader, 0, checked((int)firmwareVolumeLength));
            var warnings = new List<string>();
            if (!ValidateFirmwareVolumeChecksum(image.AsSpan(0, headerLength)))
            {
                warnings.Add("Firmware Volumeヘッダーの16-bitチェックサムが一致しません。");
            }

            if (fileSystemGuid != SystemNvDataFirmwareVolumeGuid)
            {
                warnings.Add($"Firmware Volume GUIDが標準System NV Data GUIDと異なります: {fileSystemGuid}");
            }

            var storeOffset = headerLength;
            var storeSignature = new Guid(image.AsSpan(storeOffset, 16));
            var authenticated = storeSignature == AuthenticatedVariableStoreGuid;
            if (!authenticated && storeSignature != VariableStoreGuid)
            {
                error = $"EDK II変数ストアGUIDではありません: {storeSignature}";
                return false;
            }

            var storeSize = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(storeOffset + 16, 4));
            var format = image[storeOffset + 20];
            var health = image[storeOffset + 21];
            if (storeSize < VariableStoreHeaderSize || storeOffset + (long)storeSize > firmwareVolumeLength)
            {
                throw new InvalidDataException($"UEFI変数ストアサイズが不正です: {storeSize:N0} bytes");
            }

            if (format != 0x5a)
            {
                warnings.Add($"UEFI変数ストアがformatted状態ではありません: 0x{format:X2}");
            }

            if (health != 0xfe)
            {
                warnings.Add($"UEFI変数ストアがhealthy状態ではありません: 0x{health:X2}");
            }

            var variables = ParseVariables(
                image,
                storeOffset + VariableStoreHeaderSize,
                checked(storeOffset + (int)storeSize),
                authenticated,
                warnings);
            store = new UefiVariableStore(
                fileSystemGuid,
                firmwareVolumeLength,
                headerLength,
                storeSignature,
                storeSize,
                authenticated,
                format,
                health,
                variables,
                warnings);
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or OverflowException)
        {
            error = ex.Message;
            return false;
        }
    }

    public static string Describe(UefiVariable variable)
    {
        var lines = new List<string>
        {
            $"名前: {variable.Name}",
            $"Vendor GUID: {variable.VendorGuid}",
            $"状態: {variable.StateText} (0x{variable.State:X2})",
            $"属性: {FormatAttributes(variable.Attributes)} (0x{variable.Attributes:X8})",
            $"データサイズ: {variable.Data.Length:N0} bytes",
            $"オフセット: 0x{variable.Offset:X}",
            $"解釈: {variable.Summary}"
        };

        if (variable.TimestampUtc is DateTime timestamp)
        {
            lines.Add($"認証タイムスタンプ: {timestamp:yyyy-MM-dd HH:mm:ss} UTC");
        }

        lines.Add("");
        lines.Add("データ (先頭最大4 KiB):");
        lines.Add(FormatHex(variable.Data, 4096));
        return string.Join(Environment.NewLine, lines);
    }

    public static string FormatAttributes(uint attributes)
    {
        var values = new List<string>();
        Add(0x00000001, "NV");
        Add(0x00000002, "BootService");
        Add(0x00000004, "Runtime");
        Add(0x00000008, "HardwareError");
        Add(0x00000010, "AuthenticatedWrite");
        Add(0x00000020, "TimeBasedAuth");
        Add(0x00000040, "AppendWrite");
        Add(0x00000080, "EnhancedAuth");
        return values.Count == 0 ? "なし" : string.Join(", ", values);

        void Add(uint flag, string name)
        {
            if ((attributes & flag) != 0)
            {
                values.Add(name);
            }
        }
    }

    private static IReadOnlyList<UefiVariable> ParseVariables(
        byte[] image,
        int firstOffset,
        int storeEnd,
        bool authenticated,
        List<string> warnings)
    {
        var variables = new List<UefiVariable>();
        var offset = Align4(firstOffset);
        var headerSize = authenticated ? AuthenticatedVariableHeaderSize : VariableHeaderSize;

        while (offset <= storeEnd - headerSize)
        {
            if (IsErased(image.AsSpan(offset, Math.Min(16, storeEnd - offset))))
            {
                break;
            }

            var startId = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(offset, 2));
            if (startId != VariableDataStartId)
            {
                warnings.Add($"変数領域の0x{offset:X}でStartId 0x55AAを確認できないため、以降の走査を停止しました。");
                break;
            }

            var state = image[offset + 2];
            var attributes = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(offset + 4, 4));
            int nameSizeOffset;
            int dataSizeOffset;
            int vendorGuidOffset;
            DateTime? timestamp = null;
            if (authenticated)
            {
                timestamp = TryReadEfiTime(image.AsSpan(offset + 16, 16));
                nameSizeOffset = offset + 36;
                dataSizeOffset = offset + 40;
                vendorGuidOffset = offset + 44;
            }
            else
            {
                nameSizeOffset = offset + 8;
                dataSizeOffset = offset + 12;
                vendorGuidOffset = offset + 16;
            }

            var nameSize = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(nameSizeOffset, 4));
            var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(dataSizeOffset, 4));
            var entryEnd = (long)offset + headerSize + nameSize + dataSize;
            if (nameSize == 0
                || nameSize > int.MaxValue
                || dataSize > int.MaxValue
                || entryEnd > storeEnd)
            {
                warnings.Add(
                    $"変数領域の0x{offset:X}に不正なサイズがあります: name={nameSize:N0}, data={dataSize:N0}");
                break;
            }

            var nameOffset = offset + headerSize;
            var dataOffset = checked(nameOffset + (int)nameSize);
            var nameBytes = image.AsSpan(nameOffset, checked((int)nameSize));
            var name = DecodeVariableName(nameBytes);
            var data = image.AsSpan(dataOffset, checked((int)dataSize)).ToArray();
            var vendorGuid = new Guid(image.AsSpan(vendorGuidOffset, 16));
            variables.Add(new UefiVariable(
                offset,
                name,
                vendorGuid,
                state,
                GetStateText(state),
                attributes,
                timestamp,
                data,
                Summarize(name, data)));

            offset = Align4(checked((int)entryEnd));
        }

        return variables;
    }

    private static string Summarize(string name, byte[] data)
    {
        if (string.Equals(name, "BootOrder", StringComparison.Ordinal) && data.Length % 2 == 0)
        {
            var entries = Enumerable.Range(0, data.Length / 2)
                .Select(index => $"Boot{BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(index * 2, 2)):X4}");
            return string.Join(" → ", entries);
        }

        if (name is "BootNext" or "BootCurrent" or "Timeout" && data.Length >= 2)
        {
            var value = BinaryPrimitives.ReadUInt16LittleEndian(data);
            return name == "Timeout" ? $"{value}秒" : $"Boot{value:X4}";
        }

        if (name is "SecureBoot" or "SecureBootEnable" or "SetupMode" or "AuditMode" or "DeployedMode"
            or "CustomMode" or "VendorKeys" or "VendorKeysNv"
            && data.Length >= 1)
        {
            return data[0] == 0 ? "無効 (0)" : "有効 (1)";
        }

        if (BootVariableName().IsMatch(name) && TryDescribeLoadOption(data, out var loadOption))
        {
            return loadOption;
        }

        if (name is "PK" or "KEK" or "db" or "dbx" or "dbt"
            && TryDescribeSignatureLists(data, out var signatureLists))
        {
            return signatureLists;
        }

        if (name is "PlatformLang" or "Lang")
        {
            return Encoding.ASCII.GetString(data).TrimEnd('\0');
        }

        if (data.Length == 0)
        {
            return "空データ";
        }

        if (data.Length <= 8)
        {
            return Convert.ToHexString(data);
        }

        return $"{data.Length:N0} bytes";
    }

    private static bool TryDescribeLoadOption(byte[] data, out string description)
    {
        description = "";
        if (data.Length < 8)
        {
            return false;
        }

        var attributes = BinaryPrimitives.ReadUInt32LittleEndian(data);
        var filePathLength = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4, 2));
        var textEnd = -1;
        for (var offset = 6; offset <= data.Length - 2; offset += 2)
        {
            if (data[offset] == 0 && data[offset + 1] == 0)
            {
                textEnd = offset;
                break;
            }
        }

        if (textEnd < 0)
        {
            return false;
        }

        var text = Encoding.Unicode.GetString(data, 6, textEnd - 6);
        var active = (attributes & 1) != 0 ? "有効" : "無効";
        var hidden = (attributes & 8) != 0 ? ", hidden" : "";
        description = $"{active}{hidden}, \"{text}\", device path {filePathLength:N0} bytes";
        return true;
    }

    private static bool TryDescribeSignatureLists(byte[] data, out string description)
    {
        description = "";
        var offset = 0;
        var listCount = 0;
        var signatureCount = 0;
        var types = new HashSet<string>(StringComparer.Ordinal);
        string? certificateSummary = null;

        while (offset <= data.Length - 28)
        {
            var type = new Guid(data.AsSpan(offset, 16));
            var listSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 16, 4));
            var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 20, 4));
            var signatureSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 24, 4));
            if (listSize < 28 || signatureSize < 16 || offset + (long)listSize > data.Length)
            {
                return false;
            }

            var payloadSize = (long)listSize - 28 - headerSize;
            if (payloadSize < 0 || payloadSize % signatureSize != 0)
            {
                return false;
            }

            var count = checked((int)(payloadSize / signatureSize));
            signatureCount += count;
            listCount++;
            types.Add(type == X509SignatureGuid ? "X.509" : type == Sha256SignatureGuid ? "SHA-256" : type.ToString());

            if (certificateSummary is null && type == X509SignatureGuid && count > 0 && signatureSize > 16)
            {
                var certificateOffset = checked(offset + 28 + (int)headerSize + 16);
                var certificateLength = checked((int)signatureSize - 16);
                try
                {
                    using var certificate = X509CertificateLoader.LoadCertificate(
                        data.AsSpan(certificateOffset, certificateLength));
                    certificateSummary = $"証明書: {certificate.GetNameInfo(X509NameType.SimpleName, false)}, 期限 {certificate.NotAfter:yyyy-MM-dd}";
                }
                catch (CryptographicException)
                {
                    certificateSummary = "X.509証明書の詳細をデコードできませんでした";
                }
            }

            offset += checked((int)listSize);
        }

        if (offset != data.Length || listCount == 0)
        {
            return false;
        }

        description = $"{listCount:N0}リスト / {signatureCount:N0}署名 ({string.Join(", ", types)})";
        if (certificateSummary is not null)
        {
            description += $", {certificateSummary}";
        }

        return true;
    }

    private static DateTime? TryReadEfiTime(ReadOnlySpan<byte> data)
    {
        var year = BinaryPrimitives.ReadUInt16LittleEndian(data);
        var month = data[2];
        var day = data[3];
        var hour = data[4];
        var minute = data[5];
        var second = data[6];
        if (year is < 1900 or > 9999 || month is < 1 or > 12 || day is < 1 or > 31)
        {
            return null;
        }

        try
        {
            return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static bool ValidateFirmwareVolumeChecksum(ReadOnlySpan<byte> header)
    {
        if (header.Length % 2 != 0)
        {
            return false;
        }

        uint sum = 0;
        for (var offset = 0; offset < header.Length; offset += 2)
        {
            sum += BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(offset, 2));
        }

        return (sum & 0xffff) == 0;
    }

    private static byte[] ReadExact(IBlockReader reader, long offset, int count)
    {
        if (offset < 0 || count < 0 || offset > reader.Length - count)
        {
            throw new EndOfStreamException($"UEFI変数ストアが途中で終了しています: offset=0x{offset:X}");
        }

        var data = new byte[count];
        reader.ReadAt(offset, data, 0, count);
        return data;
    }

    private static string DecodeVariableName(ReadOnlySpan<byte> data)
    {
        if (data.Length % 2 != 0)
        {
            throw new InvalidDataException("UEFI変数名のUTF-16バイト数が奇数です。");
        }

        return Encoding.Unicode.GetString(data).TrimEnd('\0');
    }

    private static bool IsErased(ReadOnlySpan<byte> data)
    {
        return data.Length > 0 && data.IndexOfAnyExcept((byte)0xff) < 0;
    }

    private static int Align4(int value)
    {
        return checked((value + 3) & ~3);
    }

    private static string GetStateText(byte state)
    {
        return state switch
        {
            0x3f => "有効",
            0x3e => "削除移行中",
            0x3d or 0x3c => "削除済み",
            0x7f => "ヘッダーのみ有効",
            0xfd => "削除済み",
            0xfe => "削除移行中",
            _ => $"不明 0x{state:X2}"
        };
    }

    private static string FormatHex(byte[] data, int maximumBytes)
    {
        if (data.Length == 0)
        {
            return "(空)";
        }

        var length = Math.Min(data.Length, maximumBytes);
        var builder = new StringBuilder();
        for (var offset = 0; offset < length; offset += 16)
        {
            var rowLength = Math.Min(16, length - offset);
            builder.Append(offset.ToString("X8")).Append("  ");
            for (var index = 0; index < 16; index++)
            {
                builder.Append(index < rowLength ? $"{data[offset + index]:X2} " : "   ");
                if (index == 7)
                {
                    builder.Append(' ');
                }
            }

            builder.Append(' ');
            for (var index = 0; index < rowLength; index++)
            {
                var value = data[offset + index];
                builder.Append(value is >= 0x20 and <= 0x7e ? (char)value : '.');
            }

            builder.AppendLine();
        }

        if (length < data.Length)
        {
            builder.AppendLine($"... 残り {data.Length - length:N0} bytes");
        }

        return builder.ToString();
    }

    [GeneratedRegex("^Boot[0-9A-Fa-f]{4}$", RegexOptions.CultureInvariant)]
    private static partial Regex BootVariableName();
}

public sealed record UefiVariableStore(
    Guid FirmwareVolumeGuid,
    long FirmwareVolumeLength,
    ushort FirmwareVolumeHeaderLength,
    Guid StoreSignature,
    uint StoreSize,
    bool Authenticated,
    byte Format,
    byte Health,
    IReadOnlyList<UefiVariable> Variables,
    IReadOnlyList<string> Warnings);

public sealed record UefiVariable(
    int Offset,
    string Name,
    Guid VendorGuid,
    byte State,
    string StateText,
    uint Attributes,
    DateTime? TimestampUtc,
    byte[] Data,
    string Summary)
{
    public bool IsActive => State == 0x3f;
}
