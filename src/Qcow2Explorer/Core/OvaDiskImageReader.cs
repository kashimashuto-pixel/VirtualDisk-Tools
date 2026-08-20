using System.Formats.Tar;
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
        while ((entry = reader.GetNextEntry()) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile))
            {
                continue;
            }

            var archivePath = NormalizeArchivePath(entry.Name);
            if (string.IsNullOrWhiteSpace(archivePath))
            {
                continue;
            }

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
                while ((count = entry.DataStream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    output.Write(buffer, 0, count);
                    progress?.Report(new DiskImageProgress(
                        $"OVAを展開中: {archivePath}",
                        Math.Min(archive.Position, archive.Length),
                        archive.Length));
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
                var document = XDocument.Load(ovf.Value, LoadOptions.None);
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
        return extension is ".vmdk" or ".vhd" or ".vhdx" or ".vdi" or ".qcow" or ".qcow2" or ".img" or ".raw" or ".dd";
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
