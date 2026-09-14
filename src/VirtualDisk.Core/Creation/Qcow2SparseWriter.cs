using System.Buffers.Binary;
using Qcow2Explorer.Core;

namespace Qcow2Explorer.Creation;

public static class Qcow2SparseWriter
{
    private const int ClusterBits = 16;
    private const int ClusterSize = 1 << ClusterBits;
    private const int RefcountOrder = 4;
    private const ulong CopiedFlag = 1UL << 63;

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

        if (string.Equals(rawPath, destinationPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("RAW原本と同じパスにはQCOW2を作成できません。");
        }

        var rawLength = new FileInfo(rawPath).Length;
        if (rawLength <= 0 || rawLength % 512 != 0)
        {
            throw new InvalidDataException("QCOW2へ変換するRAW容量は正の512-byte倍数である必要があります。");
        }

        var destinationDirectory = Path.GetDirectoryName(destinationPath)
            ?? throw new ArgumentException("QCOW2出力フォルダーを取得できません。", nameof(destinationPath));
        Directory.CreateDirectory(destinationDirectory);
        var temporaryPath = Path.Combine(
            destinationDirectory,
            $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.partial");

        try
        {
            await WriteCoreAsync(rawPath, rawLength, temporaryPath, progress, cancellationToken);
            File.Move(temporaryPath, destinationPath);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    private static async Task WriteCoreAsync(
        string rawPath,
        long rawLength,
        string destinationPath,
        IProgress<DiskImageProgress>? progress,
        CancellationToken cancellationToken)
    {
        var allocatedGuestClusters = await FindAllocatedGuestClustersAsync(
            rawPath,
            rawLength,
            progress,
            cancellationToken);
        var guestClusterCount = DivideRoundUp(rawLength, ClusterSize);
        var l2Entries = ClusterSize / 8;
        var l1Entries = checked((int)DivideRoundUp(guestClusterCount, l2Entries));
        var l1Clusters = checked((int)DivideRoundUp(checked((long)l1Entries * 8), ClusterSize));
        var l2Clusters = l1Entries;
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
        for (var index = 0; index < l1Entries; index++)
        {
            var offset = checked((ulong)((l2ClusterIndex + index) * ClusterSize));
            WriteUInt64Big(l1, index * 8, offset | CopiedFlag);
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

        var guestToHost = new Dictionary<long, long>(allocatedGuestClusters.Count);
        for (var index = 0; index < allocatedGuestClusters.Count; index++)
        {
            guestToHost.Add(allocatedGuestClusters[index], checked(dataClusterIndex + index));
        }

        for (var tableIndex = 0; tableIndex < l1Entries; tableIndex++)
        {
            var l2 = new byte[ClusterSize];
            var firstGuestCluster = checked((long)tableIndex * l2Entries);
            var finalGuestCluster = Math.Min(guestClusterCount, firstGuestCluster + l2Entries);
            for (var guestCluster = firstGuestCluster; guestCluster < finalGuestCluster; guestCluster++)
            {
                if (guestToHost.TryGetValue(guestCluster, out var hostCluster))
                {
                    var index = checked((int)(guestCluster - firstGuestCluster));
                    WriteUInt64Big(
                        l2,
                        index * 8,
                        checked((ulong)(hostCluster * ClusterSize)) | CopiedFlag);
                }
            }

            await WriteAtAsync(
                output,
                checked((l2ClusterIndex + tableIndex) * ClusterSize),
                l2,
                cancellationToken);
        }

        await using var raw = new FileStream(
            rawPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            ClusterSize,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        var data = new byte[ClusterSize];
        for (var index = 0; index < allocatedGuestClusters.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Array.Clear(data);
            var guestCluster = allocatedGuestClusters[index];
            var rawOffset = checked(guestCluster * ClusterSize);
            var count = checked((int)Math.Min(ClusterSize, rawLength - rawOffset));
            raw.Position = rawOffset;
            await raw.ReadExactlyAsync(data.AsMemory(0, count), cancellationToken);
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

    private static async Task<List<long>> FindAllocatedGuestClustersAsync(
        string rawPath,
        long rawLength,
        IProgress<DiskImageProgress>? progress,
        CancellationToken cancellationToken)
    {
        var allocated = new List<long>();
        await using var raw = new FileStream(
            rawPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            ClusterSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[ClusterSize];
        long offset = 0;
        const long progressInterval = 16L * 1024 * 1024;
        while (offset < rawLength)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Array.Clear(buffer);
            var count = checked((int)Math.Min(buffer.Length, rawLength - offset));
            await raw.ReadExactlyAsync(buffer.AsMemory(0, count), cancellationToken);
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
