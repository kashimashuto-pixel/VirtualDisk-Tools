using System.Formats.Tar;
using System.Xml;
using System.Xml.Linq;

namespace Qcow2Explorer.Core;

public sealed record OvaDiskInfo(string ArchivePath, string ExtractedPath, long? Capacity)
{
    public override string ToString()
    {
        var size = Capacity is long capacity ? $"{capacity:N0} bytes" : "size unknown";
        return $"{ArchivePath} ({size})";
    }
}

/// <summary>
/// Opens an OVF appliance tar archive and delegates reads to one of its virtual disks.
/// Archive contents are extracted to an isolated temporary directory because VMDK
/// descriptors may refer to companion extent files by name.
/// </summary>
public sealed class OvaDiskImageReader : IDiskImageReader
{
    private const int MaximumArchiveEntries = 4096;
    private const long MaximumOvfBytes = 16L * 1024 * 1024;
    private const long SparseExpansionAllowance = 64L * 1024 * 1024;
    private const int MaximumSparseExpansionRatio = 16;
    private const long MinimumFreeSpaceReserve = 256L * 1024 * 1024;

    private readonly string _temporaryDirectory;
    private readonly List<string> _warnings = [];
    private IDiskImageReader _activeReader;

    private OvaDiskImageReader(
        string path,
        string temporaryDirectory,
        IReadOnlyList<OvaDiskInfo> disks,
        int activeDiskIndex,
        IDiskImageReader activeReader,
        IEnumerable<string> warnings)
    {
        Path = path;
        _temporaryDirectory = temporaryDirectory;
        Disks = disks;
        ActiveDiskIndex = activeDiskIndex;
        _activeReader = activeReader;
        _warnings.AddRange(warnings);
    }

    public string Path { get; }
    public string FormatName => $"OVA / {_activeReader.FormatName}";
    public long Length => _activeReader.Length;
    public IReadOnlyList<OvaDiskInfo> Disks { get; }
    public int ActiveDiskIndex { get; private set; }
    public OvaDiskInfo ActiveDisk => Disks[ActiveDiskIndex];

    public static OvaDiskImageReader Open(
        string path,
        IProgress<DiskImageProgress>? progress = null,
        CancellationToken cancellationToken = default,
        string? temporaryRoot = null)
    {
        temporaryRoot = string.IsNullOrWhiteSpace(temporaryRoot)
            ? System.IO.Path.GetTempPath()
            : System.IO.Path.GetFullPath(temporaryRoot);
        Directory.CreateDirectory(temporaryRoot);
        var temporaryDirectory = System.IO.Path.Combine(
            temporaryRoot,
            $"VirtualDiskExplorer-ova-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);

        IDiskImageReader? activeReader = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new DiskImageProgress("OVAアーカイブを展開中..."));
            var extractedFiles = ExtractArchive(path, temporaryDirectory, progress, cancellationToken);
            var warnings = new List<string>();
            var disks = DiscoverDisks(extractedFiles, warnings, cancellationToken);
            if (disks.Count == 0)
            {
                throw new InvalidDataException("OVA内にOVFから参照された対応仮想ディスク、またはVMDKが見つかりませんでした。");
            }

            var activeDiskIndex = FindDefaultDisk(disks);
            activeReader = DiskImageReaderFactory.Open(
                disks[activeDiskIndex].ExtractedPath,
                progress,
                cancellationToken: cancellationToken);
            if (disks.Count > 1)
            {
                warnings.Add($"OVAには仮想ディスクが{disks.Count:N0}個あります。現在は「{disks[activeDiskIndex].ArchivePath}」を表示しています。");
            }

            return new OvaDiskImageReader(
                System.IO.Path.GetFullPath(path),
                temporaryDirectory,
                disks,
                activeDiskIndex,
                activeReader,
                warnings);
        }
        catch
        {
            activeReader?.Dispose();
            TryDeleteDirectory(temporaryDirectory);
            throw;
        }
    }

    public void SelectDisk(int index)
    {
        if ((uint)index >= (uint)Disks.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        if (index == ActiveDiskIndex)
        {
            return;
        }

        var replacement = DiskImageReaderFactory.Open(Disks[index].ExtractedPath);
        var previous = _activeReader;
        _activeReader = replacement;
        ActiveDiskIndex = index;
        previous.Dispose();
    }

    public IReadOnlyList<KeyValuePair<string, string>> GetHeaderRows()
    {
        var rows = new List<KeyValuePair<string, string>>
        {
            new("ファイル", Path),
            new("形式", FormatName),
            new("OVA内ディスク", ActiveDisk.ArchivePath),
            new("OVA内ディスク数", Disks.Count.ToString("N0"))
        };
        rows.AddRange(_activeReader.GetHeaderRows().Where(row => row.Key != "ファイル" && row.Key != "形式"));
        return rows;
    }

    public IReadOnlyList<string> GetWarnings() => _warnings.Concat(_activeReader.GetWarnings()).ToList();

    public string DescribeOffset(long offset) => $"OVA {ActiveDisk.ArchivePath}: {_activeReader.DescribeOffset(offset)}";

    public void ReadAt(long offset, byte[] buffer, int bufferOffset, int count) =>
        _activeReader.ReadAt(offset, buffer, bufferOffset, count);

    public void Dispose()
    {
        _activeReader.Dispose();
        TryDeleteDirectory(_temporaryDirectory);
    }

    private static Dictionary<string, string> ExtractArchive(
        string path,
        string destination,
        IProgress<DiskImageProgress>? progress,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var destinationRoot = System.IO.Path.GetFullPath(destination) + System.IO.Path.DirectorySeparatorChar;
        using var archive = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new TarReader(archive, leaveOpen: false);
        TarEntry? entry;
        var entryCount = 0;
        long totalDeclaredBytes = 0;
        var maximumExpandedBytes = archive.Length > (long.MaxValue - SparseExpansionAllowance) / MaximumSparseExpansionRatio
            ? long.MaxValue
            : archive.Length * MaximumSparseExpansionRatio + SparseExpansionAllowance;
        while ((entry = reader.GetNextEntry()) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            entryCount++;
            if (entryCount > MaximumArchiveEntries)
            {
                throw new InvalidDataException(
                    $"OVA内のentry数が対応上限 ({MaximumArchiveEntries:N0}) を超えています。");
            }

            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile))
            {
                continue;
            }

            var archivePath = ValidateArchivePath(entry.Name);
            if (string.IsNullOrWhiteSpace(archivePath))
            {
                continue;
            }

            totalDeclaredBytes = checked(totalDeclaredBytes + entry.Length);
            if (totalDeclaredBytes > maximumExpandedBytes)
            {
                throw new InvalidDataException(
                    $"OVAの展開予定量 ({totalDeclaredBytes:N0} bytes) がarchive容量に対して大きすぎます。");
            }

            EnsureExtractionSpace(destination, entry.Length);

            var extractedPath = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(destination, archivePath.Replace('/', System.IO.Path.DirectorySeparatorChar)));
            if (!extractedPath.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"OVA内の危険なパスを拒否しました: {entry.Name}");
            }

            if (!result.TryAdd(archivePath, extractedPath))
            {
                throw new InvalidDataException($"OVA内に重複するパスがあります: {entry.Name}");
            }

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(extractedPath)!);
            using var output = new FileStream(extractedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            if (entry.DataStream is not null)
            {
                var buffer = new byte[1024 * 1024];
                int count;
                long written = 0;
                while ((count = entry.DataStream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    written = checked(written + count);
                    if (written > entry.Length)
                    {
                        throw new InvalidDataException($"OVA entryの展開量が宣言サイズを超えています: {archivePath}");
                    }

                    output.Write(buffer, 0, count);
                    progress?.Report(new DiskImageProgress(
                        $"OVAを展開中: {archivePath}",
                        Math.Min(archive.Position, archive.Length),
                        archive.Length));
                }

                if (written != entry.Length)
                {
                    throw new EndOfStreamException(
                        $"OVA entryが途中で終了しています: {archivePath} ({written:N0}/{entry.Length:N0} bytes)");
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new DiskImageProgress(
                $"OVAを展開中: {archivePath}",
                Math.Min(archive.Position, archive.Length),
                archive.Length));
        }

        return result;
    }

    private static IReadOnlyList<OvaDiskInfo> DiscoverDisks(
        IReadOnlyDictionary<string, string> extractedFiles,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ovf = extractedFiles.FirstOrDefault(file =>
            file.Key.EndsWith(".ovf", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(ovf.Key))
        {
            try
            {
                var ovfLength = new FileInfo(ovf.Value).Length;
                if (ovfLength <= 0 || ovfLength > MaximumOvfBytes)
                {
                    throw new InvalidDataException(
                        $"OVF記述が対応上限 ({MaximumOvfBytes:N0} bytes) を超えているか空です。");
                }

                using var xmlReader = XmlReader.Create(
                    ovf.Value,
                    new XmlReaderSettings
                    {
                        DtdProcessing = DtdProcessing.Prohibit,
                        MaxCharactersInDocument = MaximumOvfBytes,
                        XmlResolver = null,
                    });
                var document = XDocument.Load(xmlReader, LoadOptions.None);
                cancellationToken.ThrowIfCancellationRequested();
                var fileReferences = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var element in document.Descendants().Where(element => element.Name.LocalName == "File"))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var id = AttributeValue(element, "id");
                    var href = AttributeValue(element, "href");
                    if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(href))
                    {
                        fileReferences.Add(id, NormalizeArchivePath(Uri.UnescapeDataString(href)));
                    }
                }

                var disks = new List<OvaDiskInfo>();
                foreach (var diskElement in document.Descendants().Where(element => element.Name.LocalName == "Disk"))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var fileRef = AttributeValue(diskElement, "fileRef");
                    if (fileRef is null || !fileReferences.TryGetValue(fileRef, out var href))
                    {
                        continue;
                    }

                    var ovfDirectory = System.IO.Path.GetDirectoryName(ovf.Key)?.Replace('\\', '/') ?? string.Empty;
                    var archivePath = NormalizeArchivePath(string.IsNullOrEmpty(ovfDirectory) ? href : $"{ovfDirectory}/{href}");
                    if (!extractedFiles.TryGetValue(archivePath, out var extractedPath) || !IsSupportedDisk(extractedPath))
                    {
                        continue;
                    }

                    long? capacity = long.TryParse(AttributeValue(diskElement, "capacity"), out var parsedCapacity)
                        ? parsedCapacity
                        : null;
                    disks.Add(new OvaDiskInfo(archivePath, extractedPath, capacity));
                }

                if (disks.Count > 0)
                {
                    return disks;
                }
            }
            catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
            {
                warnings.Add($"OVF記述の解析に失敗したため、アーカイブ内のディスクを拡張子で検出しました: {ex.Message}");
            }
        }

        var fallbackDisks = new List<OvaDiskInfo>();
        foreach (var file in extractedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsSupportedDisk(file.Value))
            {
                fallbackDisks.Add(new OvaDiskInfo(file.Key, file.Value, new FileInfo(file.Value).Length));
            }
        }

        return fallbackDisks;
    }

    private static string? AttributeValue(XElement element, string localName) =>
        element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == localName)?.Value;

    private static int FindDefaultDisk(IReadOnlyList<OvaDiskInfo> disks) => disks
        .Select((disk, index) => new { Disk = disk, Index = index })
        .OrderByDescending(item => item.Disk.Capacity ?? new FileInfo(item.Disk.ExtractedPath).Length)
        .First().Index;

    private static bool IsSupportedDisk(string path)
    {
        var extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return extension is ".vmdk" or ".vhd" or ".vhdx" or ".avhdx" or ".vdi" or ".qcow" or ".qcow2" or ".img" or ".raw" or ".dd";
    }

    private static string NormalizeArchivePath(string path)
    {
        var normalized = path.Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return normalized;
    }

    private static string ValidateArchivePath(string path)
    {
        var normalized = NormalizeArchivePath(path);
        if (string.IsNullOrWhiteSpace(normalized)
            || normalized.StartsWith("/", StringComparison.Ordinal)
            || normalized.Contains(":", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"OVA内の危険なパスを拒否しました: {path}");
        }

        var segments = normalized.Split('/');
        if (segments.Any(IsUnsafeArchivePathSegment))
        {
            throw new InvalidDataException($"OVA内の危険なパスを拒否しました: {path}");
        }

        return normalized;
    }

    private static bool IsUnsafeArchivePathSegment(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment)
            || segment is "." or ".."
            || segment.EndsWith(' ')
            || segment.EndsWith('.')
            || segment.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
        {
            return true;
        }

        var baseName = segment.Split('.')[0];
        return baseName.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || baseName.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || baseName.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || baseName.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || (baseName.Length == 4
                && (baseName.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                    || baseName.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                && baseName[3] is >= '1' and <= '9');
    }

    private static void EnsureExtractionSpace(string destination, long requiredBytes)
    {
        if (requiredBytes < 0)
        {
            throw new InvalidDataException("OVA entryのサイズが負です。");
        }

        try
        {
            var root = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(destination));
            if (string.IsNullOrWhiteSpace(root))
            {
                return;
            }

            var available = new DriveInfo(root).AvailableFreeSpace;
            if (requiredBytes > Math.Max(0, available - MinimumFreeSpaceReserve))
            {
                throw new IOException(
                    $"OVAを安全に展開する空き容量が不足しています。"
                    + $" 必要={requiredBytes:N0} bytes, 空き={available:N0} bytes");
            }
        }
        catch (Exception exception) when (exception is ArgumentException
                                           or DriveNotFoundException
                                           or NotSupportedException
                                           or UnauthorizedAccessException)
        {
            // Some virtual filesystems do not expose drive capacity; extraction still has bounded archive expansion.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // The OS will eventually reclaim its temporary directory; failure to clean up must not hide the read result.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
