using DiscUtils.Streams;
using DiscUtils.Vdi;
using Qcow2Explorer.Core;

namespace Qcow2Explorer.Creation;

public static class VdiSparseWriter
{
    private const int BufferSize = 1024 * 1024;

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
            throw new FileNotFoundException("VDIへ変換するRAWが見つかりません。", rawPath);
        }

        if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
        {
            throw new IOException($"出力先は既に存在します: {destinationPath}");
        }

        if (string.Equals(rawPath, destinationPath, PathSemantics.Comparison))
        {
            throw new IOException("RAW原本と同じパスにはVDIを作成できません。");
        }

        await using var raw = new FileStream(
            rawPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var rawLength = raw.Length;
        if (rawLength <= 0 || rawLength % 512 != 0)
        {
            throw new InvalidDataException("VDIへ変換するRAW容量は正の512-byte倍数である必要があります。");
        }

        var rawLastWriteUtc = File.GetLastWriteTimeUtc(rawPath);
        var destinationDirectory = Path.GetDirectoryName(destinationPath)
            ?? throw new ArgumentException("VDI出力フォルダーを取得できません。", nameof(destinationPath));
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
                throw new IOException("変換中にRAW原本が変更されました。VDI出力を破棄します。");
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
            throw new InvalidDataException("VDIへ変換する仮想容量は正の512-byte倍数である必要があります。");
        }

        var destinationDirectory = Path.GetDirectoryName(destinationPath)
            ?? throw new ArgumentException("VDI出力フォルダーを取得できません。", nameof(destinationPath));
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
        await using var output = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        using (var disk = Disk.InitializeDynamic(output, Ownership.None, rawLength))
        {
            var content = disk.Content;
            var buffer = new byte[BufferSize];
            long offset = 0;
            while (offset < rawLength)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = checked((int)Math.Min(buffer.Length, rawLength - offset));
                source.ReadAt(offset, buffer, 0, count);
                if (buffer.AsSpan(0, count).IndexOfAnyExcept((byte)0) >= 0)
                {
                    content.Position = offset;
                    content.Write(buffer, 0, count);
                }

                offset += count;
                progress?.Report(new DiskImageProgress("動的VDIを保存", offset, rawLength));
            }

            content.Flush();
        }

        await output.FlushAsync(cancellationToken);
        output.Flush(flushToDisk: true);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Preserve the original conversion failure.
        }
    }
}
