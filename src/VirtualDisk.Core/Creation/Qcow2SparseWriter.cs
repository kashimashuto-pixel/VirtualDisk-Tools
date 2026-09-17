using System.Buffers.Binary;
using Qcow2Explorer.Core;

namespace Qcow2Explorer.Creation;

public static class Qcow2SparseWriter
{
    private const int ClusterBits = 16;
    private const int ClusterSize = 1 << ClusterBits;
    private const int RefcountOrder = 4;
    private const ulong CopiedFlag = 1UL << 63;
    private const int MaximumL1TableBytes = 64 * 1024 * 1024;

    public static async Task WriteFromRawAsync(
        string rawPath,
        string destinationPath,
        IProgress<DiskImageProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        rawPath = Path.GetFullPath(rawPath);
        destinationPath = Path.GetFullPath(destinationPath);
        if (!File.Exists(rawPath))
        {
            throw new FileNotFoundException("QCOW2へ変換するRAWが見つかりません。", rawPath);
        }

        if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
        {
            throw new IOException($"出力先は既に存在します: {destinationPath}");
        }

        if (string.Equals(rawPath, destinationPath, PathSemantics.Comparison))
        {
            throw new IOException("RAW原本と同じパスにはQCOW2を作成できません。");
        }

        await using var raw = new FileStream(
            rawPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            ClusterSize,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        var rawLength = raw.Length;
        if (rawLength <= 0 || rawLength % 512 != 0)
        {
            throw new InvalidDataException("QCOW2へ変換するRAW容量は正の512-byte倍数である必要があります。");
        }

        var rawLastWriteUtc = File.GetLastWriteTimeUtc(rawPath);

        var destinationDirectory = Path.GetDirectoryName(destinationPath)
            ?? throw new ArgumentException("QCOW2出力フォルダーを取得できません。", nameof(destinationPath));
        Directory.CreateDirectory(destinationDirectory);
        var temporaryPath = Path.Combine(
            destinationDirectory,
            $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.partial");
        using var rawReader = new StreamBlockReader(raw, leaveOpen: true);

        try
        {
            await WriteCoreAsync(rawReader, temporaryPath, progress, cancellationToken);
            if (raw.Length != rawLength || File.GetLastWriteTimeUtc(rawPath) != rawLastWriteUtc)
            {
                throw new IOException("変換中にRAW原本が変更されました。QCOW2出力を破棄します。");
            }

            File.Move(temporaryPath, destinationPath);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    public static async Task WriteFromBlockReaderAsync(
        IBlockReader source,
        string destinationPath,
        IProgress<DiskImageProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        destinationPath = Path.GetFullPath(destinationPath);
        if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
        {
            throw new IOException($"出力先は既に存在します: {destinationPath}");
        }

        if (source.Length <= 0 || source.Length % 512 != 0)
        {
            throw new InvalidDataException("QCOW2へ変換する仮想容量は正の512-byte倍数である必要があります。");
        }

        var destinationDirectory = Path.GetDirectoryName(destinationPath)
            ?? throw new ArgumentException("QCOW2出力フォルダーを取得できません。", nameof(destinationPath));
        Directory.CreateDirectory(destinationDirectory);
        var temporaryPath = Path.Combine(
            destinationDirectory,
            $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.partial");
        try
        {
            await WriteCoreAsync(source, temporaryPath, progress, cancellationToken);
            File.Move(temporaryPath, destinationPath);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    private static async Task WriteCoreAsync(
        IBlockReader source,
        string destinationPath,
        IProgress<DiskImageProgress>? progress,
        CancellationToken cancellationToken)
    {
        var rawLength = source.Length;
        var guestClusterCount = DivideRoundUp(rawLength, ClusterSize);
        var l2Entries = ClusterSize / 8;
        var l1EntriesLong = DivideRoundUp(guestClusterCount, l2Entries);
        var l1Bytes = checked(l1EntriesLong * 8);
        if (l1EntriesLong > uint.MaxValue || l1Bytes > MaximumL1TableBytes)
        {
            throw new NotSupportedException(
                $"QCOW2 L1 tableが対応上限 ({MaximumL1TableBytes:N0} bytes) を超えています。");
        }

        var l1Entries = checked((int)l1EntriesLong);
        var l1Clusters = checked((int)DivideRoundUp(checked((long)l1Entries * 8), ClusterSize));
        var allocatedGuestClusters = FindAllocatedGuestClusters(
            source,
            progress,
            cancellationToken);
        var allocatedL2Tables = new List<int>();
        long previousTable = -1;
        foreach (var guestCluster in allocatedGuestClusters)
        {
            var table = guestCluster / l2Entries;
            if (table == previousTable)
            {
                continue;
            }

            allocatedL2Tables.Add(checked((int)table));
            previousTable = table;
        }

        var l2Clusters = allocatedL2Tables.Count;
        var refcountBlockEntries = ClusterSize / 2;
        var refcountBlocks = 1;
        var refcountTableClusters = 1;
        while (true)
        {
            var hostClusters = checked(
                1L + l1Clusters + refcountTableClusters + refcountBlocks
                + l2Clusters + allocatedGuestClusters.Count);
            var requiredBlocks = checked((int)DivideRoundUp(hostClusters, refcountBlockEntries));
            var requiredTableClusters = checked((int)DivideRoundUp(checked((long)requiredBlocks * 8), ClusterSize));
            if (requiredBlocks == refcountBlocks && requiredTableClusters == refcountTableClusters)
            {
                break;
            }

            refcountBlocks = requiredBlocks;
            refcountTableClusters = requiredTableClusters;
        }

        var l1ClusterIndex = 1L;
        var refcountTableClusterIndex = checked(l1ClusterIndex + l1Clusters);
        var refcountBlockClusterIndex = checked(refcountTableClusterIndex + refcountTableClusters);
        var l2ClusterIndex = checked(refcountBlockClusterIndex + refcountBlocks);
        var dataClusterIndex = checked(l2ClusterIndex + l2Clusters);
        var totalHostClusters = checked(dataClusterIndex + allocatedGuestClusters.Count);
        var totalHostLength = checked(totalHostClusters * ClusterSize);

        await using var output = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            ClusterSize,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        output.SetLength(totalHostLength);

        var header = new byte[ClusterSize];
        WriteUInt32Big(header, 0, Qcow2Header.MagicValue);
        WriteUInt32Big(header, 4, 3);
        WriteUInt32Big(header, 20, ClusterBits);
        WriteUInt64Big(header, 24, checked((ulong)rawLength));
        WriteUInt32Big(header, 36, checked((uint)l1Entries));
        WriteUInt64Big(header, 40, checked((ulong)(l1ClusterIndex * ClusterSize)));
        WriteUInt64Big(header, 48, checked((ulong)(refcountTableClusterIndex * ClusterSize)));
        WriteUInt32Big(header, 56, checked((uint)refcountTableClusters));
        WriteUInt32Big(header, 96, RefcountOrder);
        WriteUInt32Big(header, 100, 104);
        await WriteAtAsync(output, 0, header, cancellationToken);

        var l1 = new byte[checked(l1Clusters * ClusterSize)];
        for (var index = 0; index < allocatedL2Tables.Count; index++)
        {
            var offset = checked((ulong)((l2ClusterIndex + index) * ClusterSize));
            WriteUInt64Big(l1, checked(allocatedL2Tables[index] * 8), offset | CopiedFlag);
        }

        await WriteAtAsync(output, l1ClusterIndex * ClusterSize, l1, cancellationToken);

        var refcountTable = new byte[checked(refcountTableClusters * ClusterSize)];
        for (var index = 0; index < refcountBlocks; index++)
        {
            WriteUInt64Big(
                refcountTable,
                index * 8,
                checked((ulong)((refcountBlockClusterIndex + index) * ClusterSize)));
        }

        await WriteAtAsync(output, refcountTableClusterIndex * ClusterSize, refcountTable, cancellationToken);
        for (var blockIndex = 0; blockIndex < refcountBlocks; blockIndex++)
        {
            var block = new byte[ClusterSize];
            var firstHostCluster = checked((long)blockIndex * refcountBlockEntries);
            var count = checked((int)Math.Min(refcountBlockEntries, totalHostClusters - firstHostCluster));
            for (var index = 0; index < count; index++)
            {
                BinaryPrimitives.WriteUInt16BigEndian(block.AsSpan(index * 2, 2), 1);
            }

            await WriteAtAsync(
                output,
                checked((refcountBlockClusterIndex + blockIndex) * ClusterSize),
                block,
                cancellationToken);
        }

        var allocatedGuestIndex = 0;
        for (var tablePosition = 0; tablePosition < allocatedL2Tables.Count; tablePosition++)
        {
            var l2 = new byte[ClusterSize];
            var tableIndex = allocatedL2Tables[tablePosition];
            var firstGuestCluster = checked((long)tableIndex * l2Entries);
            var finalGuestCluster = Math.Min(guestClusterCount, firstGuestCluster + l2Entries);
            while (allocatedGuestIndex < allocatedGuestClusters.Count)
            {
                var guestCluster = allocatedGuestClusters[allocatedGuestIndex];
                if (guestCluster >= finalGuestCluster)
                {
                    break;
                }

                if (guestCluster < firstGuestCluster)
                {
                    throw new InvalidDataException("QCOW2 sparse cluster索引の順序が不正です。");
                }

                var index = checked((int)(guestCluster - firstGuestCluster));
                var hostCluster = checked(dataClusterIndex + allocatedGuestIndex);
                WriteUInt64Big(
                    l2,
                    index * 8,
                    checked((ulong)(hostCluster * ClusterSize)) | CopiedFlag);
                allocatedGuestIndex++;
            }

            await WriteAtAsync(
                output,
                checked((l2ClusterIndex + tablePosition) * ClusterSize),
                l2,
                cancellationToken);
        }

        if (allocatedGuestIndex != allocatedGuestClusters.Count)
        {
            throw new InvalidDataException("QCOW2 sparse cluster索引をL2 tableに格納できませんでした。");
        }

        var data = new byte[ClusterSize];
        for (var index = 0; index < allocatedGuestClusters.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Array.Clear(data);
            var guestCluster = allocatedGuestClusters[index];
            var rawOffset = checked(guestCluster * ClusterSize);
            var count = checked((int)Math.Min(ClusterSize, rawLength - rawOffset));
            source.ReadAt(rawOffset, data, 0, count);
            await WriteAtAsync(
                output,
                checked((dataClusterIndex + index) * ClusterSize),
                data,
                cancellationToken);
            progress?.Report(new DiskImageProgress(
                "QCOW2 data clusterを保存",
                index + 1L,
                allocatedGuestClusters.Count));
        }

        await output.FlushAsync(cancellationToken);
    }

    private static List<long> FindAllocatedGuestClusters(
        IBlockReader source,
        IProgress<DiskImageProgress>? progress,
        CancellationToken cancellationToken)
    {
        var rawLength = source.Length;
        var allocated = new List<long>();
        var buffer = new byte[ClusterSize];
        long offset = 0;
        const long progressInterval = 16L * 1024 * 1024;
        while (offset < rawLength)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Array.Clear(buffer);
            var count = checked((int)Math.Min(buffer.Length, rawLength - offset));
            source.ReadAt(offset, buffer, 0, count);
            if (buffer.AsSpan(0, count).IndexOfAnyExcept((byte)0) >= 0)
            {
                allocated.Add(offset / ClusterSize);
            }

            offset += count;
            if (offset == rawLength || offset % progressInterval == 0)
            {
                progress?.Report(new DiskImageProgress("QCOW2 sparse clusterを解析", offset, rawLength));
            }
        }

        return allocated;
    }

    private static Task WriteAtAsync(
        FileStream stream,
        long offset,
        byte[] data,
        CancellationToken cancellationToken) =>
        RandomAccess.WriteAsync(stream.SafeFileHandle, data, offset, cancellationToken).AsTask();

    private static long DivideRoundUp(long value, long divisor) =>
        checked((value + divisor - 1) / divisor);

    private static void WriteUInt32Big(byte[] data, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset, 4), value);

    private static void WriteUInt64Big(byte[] data, int offset, ulong value) =>
        BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(offset, 8), value);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Preserve the original create/convert failure.
        }
    }
}
