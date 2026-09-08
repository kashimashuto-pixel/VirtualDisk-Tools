using System.Security.Cryptography;
using System.Text;

namespace Qcow2Explorer.Core;

public enum PhysicalDiskRecoveryState
{
    Prepared = 1,
    Committed = 2,
    RolledBack = 3,
}

internal sealed record PhysicalDiskRecoveryPage(
    long Offset,
    byte[] OriginalData,
    byte[] ModifiedData);

internal sealed record PhysicalDiskRecoveryData(
    PhysicalDiskRecoveryState State,
    DateTime CreatedUtc,
    PhysicalDiskTargetInfo Target,
    IReadOnlyList<PhysicalDiskRecoveryPage> Pages);

internal static class PhysicalDiskRecoveryJournal
{
    private static readonly byte[] Magic = "VDTPDW01"u8.ToArray();
    private const int Version = 1;
    private const long StateOffset = 12;
    private const int HashLength = 32;
    private const int MaxPageLength = 64 * 1024 * 1024;
    private const int MaxPageCount = 1_000_000;

    public static void Create(
        string path,
        PhysicalDiskTargetInfo target,
        IReadOnlyList<CopyOnWritePage> pages)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(pages);
        ValidatePages(target, pages);

        path = Path.GetFullPath(path);
        if (File.Exists(path) || Directory.Exists(path))
        {
            throw new IOException($"物理ディスク復旧ジャーナルの保存先は既に存在します: {path}");
        }

        var directory = Path.GetDirectoryName(path)
            ?? throw new ArgumentException("復旧ジャーナルの保存先フォルダーを取得できません。", nameof(path));
        Directory.CreateDirectory(directory);
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.Read,
            1024 * 1024,
            FileOptions.WriteThrough | FileOptions.SequentialScan);
        using var writer = new BinaryWriter(stream, new UTF8Encoding(false), leaveOpen: true);
        writer.Write(Magic);
        writer.Write(Version);
        writer.Write((int)PhysicalDiskRecoveryState.Prepared);
        writer.Write(DateTime.UtcNow.Ticks);
        writer.Write(target.DiskNumber);
        writer.Write(target.DevicePath);
        writer.Write(target.Length);
        writer.Write(target.LogicalSectorSize);
        writer.Write(target.IsRemovable);
        writer.Write(target.Model ?? string.Empty);
        writer.Write(target.SerialNumber ?? string.Empty);
        writer.Write(target.BusType ?? string.Empty);
        writer.Write(target.IdentityToken ?? string.Empty);
        writer.Write(pages.Count);
        foreach (var page in pages.OrderBy(page => page.Offset))
        {
            writer.Write(page.Offset);
            writer.Write(page.OriginalData.Length);
            writer.Write(SHA256.HashData(page.OriginalData));
            writer.Write(SHA256.HashData(page.ModifiedData));
            writer.Write(page.OriginalData);
            writer.Write(page.ModifiedData);
        }

        writer.Flush();
        stream.Flush();
        var payloadEnd = stream.Position;
        var journalHash = HashRange(stream, 16, payloadEnd - 16);
        stream.Position = payloadEnd;
        writer.Write(journalHash);
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    public static PhysicalDiskRecoveryData Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        path = Path.GetFullPath(path);
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.SequentialScan);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (stream.Length < 16 + HashLength)
        {
            throw new InvalidDataException("物理ディスク復旧ジャーナルが短すぎます。");
        }

        var payloadEnd = stream.Length - HashLength;
        if (!reader.ReadBytes(Magic.Length).SequenceEqual(Magic))
        {
            throw new InvalidDataException("物理ディスク復旧ジャーナルの識別子が一致しません。");
        }

        if (reader.ReadInt32() != Version)
        {
            throw new InvalidDataException("未対応バージョンの物理ディスク復旧ジャーナルです。");
        }

        var state = (PhysicalDiskRecoveryState)reader.ReadInt32();
        if (!Enum.IsDefined(state))
        {
            throw new InvalidDataException("物理ディスク復旧ジャーナルの状態が不正です。");
        }

        var createdTicks = reader.ReadInt64();
        var createdUtc = new DateTime(createdTicks, DateTimeKind.Utc);
        var diskNumber = reader.ReadInt32();
        var devicePath = ReadLimitedString(reader, payloadEnd);
        var length = reader.ReadInt64();
        var sectorSize = reader.ReadUInt32();
        var isRemovable = reader.ReadBoolean();
        var model = ReadLimitedString(reader, payloadEnd);
        var serial = ReadLimitedString(reader, payloadEnd);
        var busType = ReadLimitedString(reader, payloadEnd);
        var identityToken = ReadLimitedString(reader, payloadEnd);
        var target = new PhysicalDiskTargetInfo(
            diskNumber,
            devicePath,
            length,
            sectorSize,
            isRemovable,
            IsSystemDisk: false,
            model,
            serial,
            busType,
            identityToken);
        var pageCount = reader.ReadInt32();
        if (pageCount is < 0 or > MaxPageCount)
        {
            throw new InvalidDataException("物理ディスク復旧ジャーナルのページ数が不正です。");
        }

        var pages = new List<PhysicalDiskRecoveryPage>(pageCount);
        for (var index = 0; index < pageCount; index++)
        {
            var offset = reader.ReadInt64();
            var pageLength = reader.ReadInt32();
            if (pageLength <= 0 || pageLength > MaxPageLength)
            {
                throw new InvalidDataException("物理ディスク復旧ジャーナルのページサイズが不正です。");
            }

            var requiredBytes = checked(2L * HashLength + 2L * pageLength);
            if (reader.BaseStream.Position > payloadEnd - requiredBytes)
            {
                throw new EndOfStreamException("物理ディスク復旧ジャーナルのページが途中で終了しています。");
            }

            var originalHash = ReadExact(reader, HashLength);
            var modifiedHash = ReadExact(reader, HashLength);
            var original = ReadExact(reader, pageLength);
            var modified = ReadExact(reader, pageLength);
            if (!CryptographicOperations.FixedTimeEquals(originalHash, SHA256.HashData(original))
                || !CryptographicOperations.FixedTimeEquals(modifiedHash, SHA256.HashData(modified)))
            {
                throw new InvalidDataException("物理ディスク復旧ジャーナルのページ検証値が一致しません。");
            }

            pages.Add(new PhysicalDiskRecoveryPage(offset, original, modified));
        }

        if (stream.Position != payloadEnd)
        {
            throw new InvalidDataException("物理ディスク復旧ジャーナルの長さが一致しません。");
        }

        var expectedJournalHash = ReadExact(reader, HashLength);
        var actualJournalHash = HashRange(stream, 16, payloadEnd - 16);
        if (!CryptographicOperations.FixedTimeEquals(expectedJournalHash, actualJournalHash))
        {
            throw new InvalidDataException("物理ディスク復旧ジャーナル全体の検証値が一致しません。");
        }

        ValidatePages(
            target,
            pages.Select(page => new CopyOnWritePage(page.Offset, page.OriginalData, page.ModifiedData)).ToArray());
        return new PhysicalDiskRecoveryData(state, createdUtc, target, pages);
    }

    public static void SetState(string path, PhysicalDiskRecoveryState state)
    {
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        using var stream = new FileStream(
            Path.GetFullPath(path),
            FileMode.Open,
            FileAccess.Write,
            FileShare.Read,
            sizeof(int),
            FileOptions.WriteThrough);
        stream.Position = StateOffset;
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write((int)state);
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    private static void ValidatePages(
        PhysicalDiskTargetInfo target,
        IReadOnlyList<CopyOnWritePage> pages)
    {
        if (target.Length <= 0
            || target.LogicalSectorSize is < 512 or > 65536
            || (target.LogicalSectorSize & (target.LogicalSectorSize - 1)) != 0)
        {
            throw new InvalidDataException("物理ディスクのサイズまたは論理セクターサイズが不正です。");
        }

        if (pages.Count is <= 0 or > MaxPageCount)
        {
            throw new InvalidDataException("物理ディスクへ適用する差分ページ数が不正です。");
        }

        long previousEnd = 0;
        foreach (var page in pages.OrderBy(page => page.Offset))
        {
            if (page.OriginalData.Length == 0
                || page.OriginalData.Length != page.ModifiedData.Length
                || page.OriginalData.Length > MaxPageLength
                || page.Offset < previousEnd
                || page.Offset % target.LogicalSectorSize != 0
                || page.OriginalData.Length % target.LogicalSectorSize != 0
                || page.Offset > target.Length - page.OriginalData.Length)
            {
                throw new InvalidDataException("物理ディスクへ適用する差分ページ範囲が不正です。");
            }

            previousEnd = checked(page.Offset + page.OriginalData.Length);
        }
    }

    private static string ReadLimitedString(BinaryReader reader, long payloadEnd)
    {
        const int maxUtf8Bytes = 16 * 1024;
        var byteLength = Read7BitEncodedInt(reader, payloadEnd);
        if (byteLength < 0 || byteLength > maxUtf8Bytes)
        {
            throw new InvalidDataException("物理ディスク復旧ジャーナルの文字列が長すぎます。");
        }

        if (reader.BaseStream.Position > payloadEnd - byteLength)
        {
            throw new EndOfStreamException("物理ディスク復旧ジャーナルの文字列が途中で終了しています。");
        }

        try
        {
            return new UTF8Encoding(false, true).GetString(ReadExact(reader, byteLength));
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException("物理ディスク復旧ジャーナルの文字列が不正なUTF-8です。", ex);
        }
    }

    private static int Read7BitEncodedInt(BinaryReader reader, long payloadEnd)
    {
        uint value = 0;
        for (var shift = 0; shift < 35; shift += 7)
        {
            if (reader.BaseStream.Position >= payloadEnd)
            {
                throw new EndOfStreamException("物理ディスク復旧ジャーナルの文字列長が途中で終了しています。");
            }

            var current = reader.ReadByte();
            value |= (uint)(current & 0x7F) << shift;
            if ((current & 0x80) == 0)
            {
                if (shift == 28 && current > 0x0F)
                {
                    throw new InvalidDataException("物理ディスク復旧ジャーナルの文字列長が不正です。");
                }

                return checked((int)value);
            }
        }

        throw new InvalidDataException("物理ディスク復旧ジャーナルの文字列長が不正です。");
    }

    private static byte[] ReadExact(BinaryReader reader, int length)
    {
        var value = reader.ReadBytes(length);
        if (value.Length != length)
        {
            throw new EndOfStreamException("物理ディスク復旧ジャーナルが途中で終了しています。");
        }

        return value;
    }

    private static byte[] HashRange(Stream stream, long offset, long length)
    {
        if (offset < 0 || length < 0 || offset > stream.Length - length)
        {
            throw new InvalidDataException("物理ディスク復旧ジャーナルの検証範囲が不正です。");
        }

        var originalPosition = stream.Position;
        try
        {
            stream.Position = offset;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1024 * 1024];
            var remaining = length;
            while (remaining > 0)
            {
                var requested = checked((int)Math.Min(buffer.Length, remaining));
                var read = stream.Read(buffer, 0, requested);
                if (read == 0)
                {
                    throw new EndOfStreamException("物理ディスク復旧ジャーナルの検証範囲が途中で終了しています。");
                }

                hash.AppendData(buffer, 0, read);
                remaining -= read;
            }

            return hash.GetHashAndReset();
        }
        finally
        {
            stream.Position = originalPosition;
        }
    }
}
