using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace Qcow2Explorer.Core;

public sealed class ParallelsHddReader : IDiskImageReader
{
    private const int SectorSize = 512;
    private const string DescriptorFileName = "DiskDescriptor.xml";
    private const string ZeroGuid = "{00000000-0000-0000-0000-000000000000}";
    private const string DefaultTopGuid = "{5fbaabe3-6958-40ff-92a7-860e329aab41}";

    private readonly IReadOnlyList<ParallelsLayer> _layers;
    private readonly IReadOnlyList<string> _warnings;

    private ParallelsHddReader(string path, string formatName, long length, IReadOnlyList<ParallelsLayer> layers, IReadOnlyList<string> warnings)
    {
        Path = path;
        FormatName = formatName;
        Length = length;
        _layers = layers;
        _warnings = warnings;
    }

    public string Path { get; }
    public string FormatName { get; }
    public long Length { get; }

    public static bool CanOpenDirectory(string path)
    {
        return File.Exists(System.IO.Path.Combine(path, DescriptorFileName));
    }

    public static ParallelsHddReader Open(string path)
    {
        if (Directory.Exists(path))
        {
            return OpenBundle(path);
        }

        var extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
        if (extension is ".hds" or ".hdd")
        {
            return OpenSingleExpandableImage(path);
        }

        throw new NotSupportedException("Parallels HDD は .hdd フォルダまたは .hds ファイルを指定してください。");
    }

    public IReadOnlyList<KeyValuePair<string, string>> GetHeaderRows()
    {
        return new List<KeyValuePair<string, string>>
        {
            Row("ファイル", Path),
            Row("形式", FormatName),
            Row("仮想ディスクサイズ", $"{Length:N0} bytes"),
            Row("layer count", _layers.Count.ToString(CultureInfo.InvariantCulture)),
            Row("layers", string.Join(" -> ", _layers.Select(layer => $"{layer.Type}:{System.IO.Path.GetFileName(layer.Path)}")))
        };

        static KeyValuePair<string, string> Row(string key, string value) => new(key, value);
    }

    public IReadOnlyList<string> GetWarnings() => _warnings;

    public string DescribeOffset(long offset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        foreach (var layer in _layers)
        {
            if (layer.TryDescribeOffset(offset, out var description))
            {
                return description;
            }
        }

        return $"Parallels HDD virtual offset 0x{offset:X}: unallocated, zero-filled";
    }

    public void ReadAt(long offset, byte[] buffer, int bufferOffset, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentNullException.ThrowIfNull(buffer);
        if (bufferOffset < 0 || count < 0 || bufferOffset + count > buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(bufferOffset));
        }

        Array.Clear(buffer, bufferOffset, count);
        if (count == 0 || offset >= Length)
        {
            return;
        }

        var remaining = checked((int)Math.Min(count, Length - offset));
        var position = offset;
        var outputOffset = bufferOffset;
        while (remaining > 0)
        {
            var request = Math.Min(remaining, 1024 * 1024);
            var read = 0;
            foreach (var layer in _layers)
            {
                read = layer.TryReadAt(position, buffer, outputOffset, request);
                if (read > 0)
                {
                    break;
                }
            }

            if (read == 0)
            {
                read = _layers.Count == 0
                    ? request
                    : Math.Max(1, _layers.Min(layer => layer.GetRunLength(position, request)));
            }

            position += read;
            outputOffset += read;
            remaining -= read;
        }
    }

    public void Dispose()
    {
        foreach (var layer in _layers)
        {
            layer.Dispose();
        }
    }

    private static ParallelsHddReader OpenSingleExpandableImage(string path)
    {
        var warnings = new List<string>();
        var layer = ExpandableLayer.Open(path, warnings);
        return new ParallelsHddReader(path, "Parallels HDD (.hds)", layer.Length, new[] { layer }, warnings);
    }

    private static ParallelsHddReader OpenBundle(string path)
    {
        var descriptorPath = System.IO.Path.Combine(path, DescriptorFileName);
        if (!File.Exists(descriptorPath))
        {
            throw new FileNotFoundException("Parallels HDD の DiskDescriptor.xml が見つかりません。", descriptorPath);
        }

        var document = XDocument.Load(descriptorPath);
        var warnings = new List<string>();
        var diskSectors = ParseRequiredInt64(DescendantValue(document, "Disk_size"), "Disk_size");
        var storages = Descendants(document, "Storage").ToList();
        if (storages.Count != 1)
        {
            throw new NotSupportedException($"Parallels HDD の split image は未対応です。Storage={storages.Count}");
        }

        var storage = storages[0];
        var start = ParseOptionalInt64(ChildValue(storage, "Start")) ?? 0;
        var end = ParseOptionalInt64(ChildValue(storage, "End")) ?? diskSectors;
        if (start != 0 || end != diskSectors)
        {
            throw new NotSupportedException("Parallels HDD の split storage は未対応です。");
        }

        var images = storage.Elements()
            .Where(e => IsName(e, "Image"))
            .Select(ImageInfo.FromElement)
            .Where(image => !string.IsNullOrWhiteSpace(image.FileName))
            .ToList();
        if (images.Count == 0)
        {
            throw new InvalidDataException("Parallels HDD の Image 要素が見つかりません。");
        }

        var orderedImages = OrderSnapshotImages(document, images, warnings);
        var layers = new List<ParallelsLayer>();
        try
        {
            foreach (var image in orderedImages)
            {
                var imagePath = ResolveImagePath(path, image.FileName);
                layers.Add(OpenLayer(imagePath, image.Type, warnings));
            }
        }
        catch
        {
            foreach (var layer in layers)
            {
                layer.Dispose();
            }

            throw;
        }

        return new ParallelsHddReader(path, "Parallels HDD (.hdd)", checked(diskSectors * SectorSize), layers, warnings);
    }

    private static ParallelsLayer OpenLayer(string path, string type, List<string> warnings)
    {
        if (string.Equals(type, "Plain", StringComparison.OrdinalIgnoreCase))
        {
            return PlainLayer.Open(path);
        }

        if (string.Equals(type, "Compressed", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(type))
        {
            return ExpandableLayer.Open(path, warnings);
        }

        throw new NotSupportedException($"Parallels HDD の Image Type={type} は未対応です。");
    }

    private static string ResolveImagePath(string baseDirectory, string imagePath)
    {
        imagePath = imagePath.Replace('/', System.IO.Path.DirectorySeparatorChar);
        return System.IO.Path.IsPathRooted(imagePath)
            ? imagePath
            : System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDirectory, imagePath));
    }

    private static IReadOnlyList<ImageInfo> OrderSnapshotImages(XDocument document, IReadOnlyList<ImageInfo> images, List<string> warnings)
    {
        if (images.Count == 1)
        {
            return images;
        }

        var byGuid = images
            .Where(image => !string.IsNullOrWhiteSpace(image.Guid))
            .GroupBy(image => image.Guid, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var parentByGuid = Descendants(document, "Shot")
            .Select(shot => new
            {
                Guid = ChildValue(shot, "GUID"),
                ParentGuid = ChildValue(shot, "ParentGUID")
            })
            .Where(shot => !string.IsNullOrWhiteSpace(shot.Guid))
            .ToDictionary(shot => shot.Guid!, shot => shot.ParentGuid ?? ZeroGuid, StringComparer.OrdinalIgnoreCase);

        var topGuid = DescendantValue(document, "TopGUID");
        if (string.IsNullOrWhiteSpace(topGuid) && byGuid.ContainsKey(DefaultTopGuid))
        {
            topGuid = DefaultTopGuid;
        }

        if (string.IsNullOrWhiteSpace(topGuid))
        {
            var parentGuids = new HashSet<string>(parentByGuid.Values, StringComparer.OrdinalIgnoreCase);
            topGuid = images.LastOrDefault(image => !string.IsNullOrWhiteSpace(image.Guid) && !parentGuids.Contains(image.Guid))?.Guid
                ?? images.Last().Guid;
        }

        var result = new List<ImageInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = topGuid;
        while (!string.IsNullOrWhiteSpace(current) && !IsZeroGuid(current) && seen.Add(current))
        {
            if (!byGuid.TryGetValue(current, out var image))
            {
                warnings.Add($"Parallels HDD snapshot {current} に対応する Image が見つからないため、残りの親チェーンを省略しました。");
                break;
            }

            result.Add(image);
            if (!parentByGuid.TryGetValue(current, out current))
            {
                break;
            }
        }

        if (result.Count == 0)
        {
            warnings.Add("Parallels HDD の snapshot 順序を判定できなかったため、Image 要素の逆順で読み取ります。");
            result.AddRange(images.Reverse());
        }

        return result;
    }

    private static IEnumerable<XElement> Descendants(XContainer container, string name)
    {
        return container.Descendants().Where(element => IsName(element, name));
    }

    private static string? DescendantValue(XContainer container, string name)
    {
        return Descendants(container, name).FirstOrDefault()?.Value.Trim();
    }

    private static string? ChildValue(XElement element, string name)
    {
        return element.Elements().FirstOrDefault(child => IsName(child, name))?.Value.Trim();
    }

    private static bool IsName(XElement element, string name)
    {
        return string.Equals(element.Name.LocalName, name, StringComparison.Ordinal);
    }

    private static long ParseRequiredInt64(string? value, string name)
    {
        return ParseOptionalInt64(value) ?? throw new InvalidDataException($"Parallels HDD の {name} が見つかりません。");
    }

    private static long? ParseOptionalInt64(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return long.Parse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture);
    }

    private static bool IsZeroGuid(string value)
    {
        return string.Equals(value.Trim(), ZeroGuid, StringComparison.OrdinalIgnoreCase);
    }

    private abstract class ParallelsLayer : IDisposable
    {
        protected ParallelsLayer(string path, string type, long length)
        {
            Path = path;
            Type = type;
            Length = length;
        }

        public string Path { get; }
        public string Type { get; }
        public long Length { get; }

        public abstract int TryReadAt(long offset, byte[] buffer, int bufferOffset, int count);
        public virtual int GetRunLength(long offset, int count) => count;
        public abstract bool TryDescribeOffset(long offset, out string description);
        public abstract void Dispose();
    }

    private sealed class PlainLayer : ParallelsLayer
    {
        private readonly FileStream _stream;
        private readonly object _sync = new();

        private PlainLayer(string path, FileStream stream)
            : base(path, "Plain", stream.Length)
        {
            _stream = stream;
        }

        public static PlainLayer Open(string path)
        {
            return new PlainLayer(path, OpenRead(path));
        }

        public override int TryReadAt(long offset, byte[] buffer, int bufferOffset, int count)
        {
            if (count == 0)
            {
                return 0;
            }

            if (offset >= _stream.Length)
            {
                return count;
            }

            var toRead = checked((int)Math.Min(count, _stream.Length - offset));
            lock (_sync)
            {
                _stream.Position = offset;
                ReadFullyOrZero(_stream, buffer, bufferOffset, toRead);
            }

            return count;
        }

        public override bool TryDescribeOffset(long offset, out string description)
        {
            description = $"Parallels HDD virtual offset 0x{offset:X}: Plain layer {System.IO.Path.GetFileName(Path)} host offset 0x{offset:X}";
            return true;
        }

        public override void Dispose()
        {
            _stream.Dispose();
        }
    }

    private sealed class ExpandableLayer : ParallelsLayer
    {
        private const string Magic = "WithoutFreeSpace";
        private const string ExtMagic = "WithouFreSpacExt";

        private readonly FileStream _stream;
        private readonly object _sync = new();
        private readonly uint[] _bat;
        private readonly int _clusterSize;
        private readonly bool _extendedOffsets;
        private readonly bool _emptyImage;

        private ExpandableLayer(
            string path,
            FileStream stream,
            long length,
            uint[] bat,
            int clusterSize,
            bool extendedOffsets,
            bool emptyImage)
            : base(path, "Compressed", length)
        {
            _stream = stream;
            _bat = bat;
            _clusterSize = clusterSize;
            _extendedOffsets = extendedOffsets;
            _emptyImage = emptyImage;
        }

        public static ExpandableLayer Open(string path, List<string> warnings)
        {
            var stream = OpenRead(path);
            try
            {
                var header = new byte[64];
                ReadExact(stream, header, 0, header.Length);
                var magic = Encoding.ASCII.GetString(header, 0, 16);
                if (magic is not Magic and not ExtMagic)
                {
                    throw new InvalidDataException("Parallels expandable image の magic が不正です。");
                }

                var version = EndianUtilities.ReadUInt32Little(header, 16);
                if (version != 2)
                {
                    throw new NotSupportedException($"Parallels expandable image version {version} は未対応です。");
                }

                var clusterSectors = EndianUtilities.ReadUInt32Little(header, 28);
                if (clusterSectors == 0 || clusterSectors > int.MaxValue / SectorSize)
                {
                    throw new InvalidDataException("Parallels expandable image の cluster size が不正です。");
                }

                var batEntryCount = EndianUtilities.ReadUInt32Little(header, 32);
                if (batEntryCount > int.MaxValue)
                {
                    throw new NotSupportedException("Parallels expandable image の BAT が大きすぎます。");
                }

                var diskSectors = magic == ExtMagic
                    ? EndianUtilities.ReadUInt64Little(header, 36)
                    : EndianUtilities.ReadUInt32Little(header, 36);
                var inUse = EndianUtilities.ReadUInt32Little(header, 44);
                var flags = EndianUtilities.ReadUInt32Little(header, 52);
                if (inUse is not 0 and not 0x746F6E59 and not 0x312e3276)
                {
                    warnings.Add($"{System.IO.Path.GetFileName(path)}: in_use=0x{inUse:X8} は仕様外の値です。読み取りは継続します。");
                }

                var bat = new uint[batEntryCount];
                var batBytes = checked((int)batEntryCount * 4);
                var batBuffer = new byte[batBytes];
                ReadExact(stream, batBuffer, 0, batBuffer.Length);
                for (var i = 0; i < bat.Length; i++)
                {
                    bat[i] = EndianUtilities.ReadUInt32Little(batBuffer, i * 4);
                }

                var clusterSize = checked((int)clusterSectors * SectorSize);
                return new ExpandableLayer(
                    path,
                    stream,
                    checked((long)diskSectors * SectorSize),
                    bat,
                    clusterSize,
                    magic == ExtMagic,
                    (flags & 1) != 0);
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        public override int TryReadAt(long offset, byte[] buffer, int bufferOffset, int count)
        {
            if (_emptyImage || count == 0 || offset >= Length)
            {
                return 0;
            }

            var cluster = offset / _clusterSize;
            if (cluster < 0 || cluster >= _bat.Length)
            {
                return 0;
            }

            var batEntry = _bat[cluster];
            if (batEntry == 0)
            {
                return 0;
            }

            var inCluster = (int)(offset % _clusterSize);
            var toRead = checked((int)Math.Min(Math.Min(count, Length - offset), _clusterSize - inCluster));
            var hostOffset = checked((_extendedOffsets ? (long)batEntry * _clusterSize : (long)batEntry * SectorSize) + inCluster);
            if (hostOffset < 0 || hostOffset >= _stream.Length)
            {
                return toRead;
            }

            lock (_sync)
            {
                _stream.Position = hostOffset;
                ReadFullyOrZero(_stream, buffer, bufferOffset, checked((int)Math.Min(toRead, _stream.Length - hostOffset)));
            }

            return toRead;
        }

        public override int GetRunLength(long offset, int count)
        {
            if (offset >= Length)
            {
                return count;
            }

            var inCluster = (int)(offset % _clusterSize);
            return Math.Max(1, Math.Min(count, _clusterSize - inCluster));
        }

        public override bool TryDescribeOffset(long offset, out string description)
        {
            if (_emptyImage || offset >= Length)
            {
                description = "";
                return false;
            }

            var cluster = offset / _clusterSize;
            if (cluster < 0 || cluster >= _bat.Length)
            {
                description = "";
                return false;
            }

            var batEntry = _bat[cluster];
            if (batEntry == 0)
            {
                description = "";
                return false;
            }

            var inCluster = (int)(offset % _clusterSize);
            var hostOffset = checked((_extendedOffsets ? (long)batEntry * _clusterSize : (long)batEntry * SectorSize) + inCluster);
            description = $"Parallels HDD virtual offset 0x{offset:X}: {System.IO.Path.GetFileName(Path)} cluster {cluster:N0}, host offset 0x{hostOffset:X}";
            return true;
        }

        public override void Dispose()
        {
            _stream.Dispose();
        }
    }

    private sealed record ImageInfo(string Guid, string Type, string FileName)
    {
        public static ImageInfo FromElement(XElement element)
        {
            return new ImageInfo(
                ChildValue(element, "GUID") ?? "",
                ChildValue(element, "Type") ?? "Compressed",
                ChildValue(element, "File") ?? "");
        }
    }

    private static FileStream OpenRead(string path)
    {
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    }

    private static void ReadExact(Stream stream, byte[] buffer, int offset, int count)
    {
        var total = 0;
        while (total < count)
        {
            var read = stream.Read(buffer, offset + total, count - total);
            if (read == 0)
            {
                throw new EndOfStreamException();
            }

            total += read;
        }
    }

    private static void ReadFullyOrZero(Stream stream, byte[] buffer, int offset, int count)
    {
        var total = 0;
        while (total < count)
        {
            var read = stream.Read(buffer, offset + total, count - total);
            if (read == 0)
            {
                break;
            }

            total += read;
        }
    }
}
