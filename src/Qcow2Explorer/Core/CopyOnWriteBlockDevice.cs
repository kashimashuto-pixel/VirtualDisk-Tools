namespace Qcow2Explorer.Core;

public sealed record CopyOnWritePage(
    long Offset,
    byte[] OriginalData,
    byte[] ModifiedData);

public sealed class CopyOnWriteBlockDevice : IBlockDevice
{
    private const int DefaultPageSize = 64 * 1024;
    private const int ExportBufferSize = 4 * 1024 * 1024;

    private readonly IBlockReader _source;
    private readonly int _pageSize;
    private readonly Dictionary<long, byte[]> _pages = [];
    private readonly Dictionary<long, byte[]> _originalPages = [];
    private readonly object _sync = new();

    public CopyOnWriteBlockDevice(IBlockReader source, int pageSize = DefaultPageSize)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(source), "ブロックデバイスのサイズが不正です。");
        }

        if (pageSize < 512 || (pageSize & (pageSize - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize), "ページサイズは512以上の2の累乗にしてください。");
        }

        _source = source;
        _pageSize = pageSize;
    }

    public long Length => _source.Length;
    public int ModifiedPageCount
    {
        get
        {
            lock (_sync)
            {
                return _pages.Count;
            }
        }
    }

    public long ModifiedStorageBytes => checked((long)ModifiedPageCount * _pageSize);

    public IReadOnlyList<CopyOnWritePage> GetModifiedPages()
    {
        lock (_sync)
        {
            return _pages
                .OrderBy(entry => entry.Key)
                .Where(entry => !_originalPages[entry.Key].AsSpan().SequenceEqual(entry.Value))
                .Select(entry => new CopyOnWritePage(
                    checked(entry.Key * _pageSize),
                    (byte[])_originalPages[entry.Key].Clone(),
                    (byte[])entry.Value.Clone()))
                .ToArray();
        }
    }

    public void ReadAt(long offset, byte[] buffer, int bufferOffset, int count)
    {
        ValidateRange(offset, buffer, bufferOffset, count);
        if (count == 0)
        {
            return;
        }

        lock (_sync)
        {
            var remaining = count;
            var currentOffset = offset;
            var destinationOffset = bufferOffset;
            while (remaining > 0)
            {
                var pageIndex = currentOffset / _pageSize;
                var pageOffset = checked((int)(currentOffset % _pageSize));
                var pageLength = GetPageLength(pageIndex);
                var copyLength = Math.Min(remaining, pageLength - pageOffset);
                if (_pages.TryGetValue(pageIndex, out var page))
                {
                    Array.Copy(page, pageOffset, buffer, destinationOffset, copyLength);
                }
                else
                {
                    _source.ReadAt(currentOffset, buffer, destinationOffset, copyLength);
                }

                remaining -= copyLength;
                currentOffset += copyLength;
                destinationOffset += copyLength;
            }
        }
    }

    public void WriteAt(long offset, byte[] buffer, int bufferOffset, int count)
    {
        ValidateRange(offset, buffer, bufferOffset, count);
        if (count == 0)
        {
            return;
        }

        lock (_sync)
        {
            var remaining = count;
            var currentOffset = offset;
            var sourceOffset = bufferOffset;
            while (remaining > 0)
            {
                var pageIndex = currentOffset / _pageSize;
                var pageOffset = checked((int)(currentOffset % _pageSize));
                var pageLength = GetPageLength(pageIndex);
                var copyLength = Math.Min(remaining, pageLength - pageOffset);
                var page = GetOrCreatePage(pageIndex, pageLength);
                Array.Copy(buffer, sourceOffset, page, pageOffset, copyLength);

                remaining -= copyLength;
                currentOffset += copyLength;
                sourceOffset += copyLength;
            }
        }
    }

    public void Flush()
    {
        // Changes stay in the overlay until ExportRawAsync is called.
    }

    public async Task ExportRawAsync(
        string destinationPath,
        IProgress<DiskImageProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        destinationPath = Path.GetFullPath(destinationPath);
        if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
        {
            throw new IOException($"出力先は既に存在します: {destinationPath}");
        }

        var destinationDirectory = Path.GetDirectoryName(destinationPath)
            ?? throw new ArgumentException("出力先フォルダーを取得できません。", nameof(destinationPath));
        Directory.CreateDirectory(destinationDirectory);
        var temporaryPath = Path.Combine(
            destinationDirectory,
            $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.partial");

        try
        {
            await using (var output = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                ExportBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[ExportBufferSize];
                long offset = 0;
                while (offset < Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var count = checked((int)Math.Min(buffer.Length, Length - offset));
                    ReadAt(offset, buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    offset += count;
                    progress?.Report(new DiskImageProgress("変更済みRAWを保存", offset, Length));
                }

                await output.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, destinationPath);
        }
        catch
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch
            {
                // Preserve the original failure. A uniquely named partial file can be removed manually.
            }

            throw;
        }
    }

    private byte[] GetOrCreatePage(long pageIndex, int pageLength)
    {
        if (_pages.TryGetValue(pageIndex, out var page))
        {
            return page;
        }

        var original = new byte[pageLength];
        var pageOffset = checked(pageIndex * _pageSize);
        _source.ReadAt(pageOffset, original, 0, original.Length);
        page = (byte[])original.Clone();
        _originalPages.Add(pageIndex, original);
        _pages.Add(pageIndex, page);
        return page;
    }

    private int GetPageLength(long pageIndex)
    {
        var pageOffset = checked(pageIndex * _pageSize);
        return checked((int)Math.Min(_pageSize, Length - pageOffset));
    }

    private void ValidateRange(long offset, byte[] buffer, int bufferOffset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(bufferOffset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (bufferOffset > buffer.Length - count)
        {
            throw new ArgumentException("バッファー範囲が不正です。", nameof(bufferOffset));
        }

        if (offset > Length - count)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "ブロックデバイスの末尾を超えています。");
        }
    }
}
