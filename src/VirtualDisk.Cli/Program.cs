using System.Globalization;
using Qcow2Explorer.Core;
using Qcow2Explorer.Creation;
using Qcow2Explorer.FileSystems;
using Qcow2Explorer.Partitions;

return await Cli.RunAsync(args);

internal static class Cli
{
    private const int CopyBufferSize = 1024 * 1024;

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.Length == 0 || IsHelp(args[0]))
            {
                PrintHelp();
                return 0;
            }

            if (args[0] is "--version" or "version")
            {
                Console.WriteLine(typeof(Cli).Assembly.GetName().Version?.ToString() ?? "unknown");
                return 0;
            }

            return args[0].ToLowerInvariant() switch
            {
                "info" => RunInfo(args[1..]),
                "list" => RunList(args[1..]),
                "extract" => await RunExtractAsync(args[1..]),
                "create" => await RunCreateAsync(args[1..]),
                _ => throw new CliUsageException($"不明なコマンドです: {args[0]}"),
            };
        }
        catch (CliUsageException ex)
        {
            Console.Error.WriteLine($"入力エラー: {ex.Message}");
            Console.Error.WriteLine("'vdt --help' で使用方法を確認できます。");
            return 2;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("処理をキャンセルしました。");
            return 130;
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or InvalidDataException
                                   or NotSupportedException
                                   or ArgumentException
                                   or OverflowException)
        {
            Console.Error.WriteLine($"エラー: {ex.Message}");
            return 1;
        }
    }

    private static int RunInfo(string[] args)
    {
        if (args.Length != 1)
        {
            throw new CliUsageException("infoにはディスクイメージを1つ指定してください。");
        }

        using var reader = DiskImageReaderFactory.Open(args[0]);
        Console.WriteLine($"Path: {reader.Path}");
        Console.WriteLine($"Format: {reader.FormatName}");
        Console.WriteLine($"Size: {reader.Length} bytes");
        var partitions = PartitionTableReader.ReadPartitions(reader);
        Console.WriteLine($"Partitions: {partitions.Count}");
        foreach (var partition in partitions)
        {
            partition.FileSystem = FileSystemDetector.Detect(reader, partition);
            Console.WriteLine(
                $"  #{partition.Number} {partition.Scheme} offset={partition.StartOffset} "
                + $"size={partition.LengthBytes} fs={partition.FileSystem}");
        }

        return 0;
    }

    private static int RunList(string[] args)
    {
        var options = ParsedOptions.Parse(args, ["partition", "path"]);
        using var context = OpenFileSystem(options);
        var directory = ResolvePath(context.FileSystem, options.Get("path") ?? "/");
        if (!directory.IsDirectory)
        {
            throw new CliUsageException("listの対象はディレクトリである必要があります。");
        }

        foreach (var entry in context.FileSystem.ListDirectory(directory))
        {
            Console.WriteLine(
                $"{(entry.IsDirectory ? "d" : "-")} {entry.Size,14} "
                + $"{entry.ModifiedUtc?.ToString("u", CultureInfo.InvariantCulture) ?? "-",20} {entry.Name}");
        }

        return 0;
    }

    private static async Task<int> RunExtractAsync(string[] args)
    {
        var options = ParsedOptions.Parse(args, ["partition", "path", "output"]);
        var virtualPath = options.Require("path");
        var outputPath = Path.GetFullPath(options.Require("output"));
        if (File.Exists(outputPath) || Directory.Exists(outputPath))
        {
            throw new IOException($"抽出先は既に存在します: {outputPath}");
        }

        using var context = OpenFileSystem(options);
        var file = ResolvePath(context.FileSystem, virtualPath);
        if (file.IsDirectory)
        {
            throw new CliUsageException("extractは現在、通常ファイルを1つ指定してください。");
        }

        var parent = Path.GetDirectoryName(outputPath)
            ?? throw new CliUsageException("抽出先ディレクトリを取得できません。");
        Directory.CreateDirectory(parent);
        await using var destination = new FileStream(
            outputPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        long offset = 0;
        while (offset < file.Size)
        {
            var count = checked((int)Math.Min(CopyBufferSize, file.Size - offset));
            var content = context.FileSystem.ReadFile(file, offset, count);
            if (content.Length != count)
            {
                throw new EndOfStreamException($"ファイル読み取りが途中で終了しました: offset={offset}");
            }

            await destination.WriteAsync(content);
            offset += count;
        }

        Console.WriteLine($"Extracted {file.Size} bytes to {outputPath}");
        return 0;
    }

    private static async Task<int> RunCreateAsync(string[] args)
    {
        var options = ParsedOptions.Parse(args, ["size", "container", "table", "partition"], "partition");
        var outputPath = Path.GetFullPath(options.Positional);
        var size = ParseSize(options.Require("size"));
        var container = (options.Get("container") ?? InferContainer(outputPath)).ToLowerInvariant() switch
        {
            "raw" => VirtualDiskContainerFormat.Raw,
            "qcow2" or "qcow" => VirtualDiskContainerFormat.Qcow2,
            var value => throw new CliUsageException($"未対応のコンテナ形式です: {value}"),
        };
        var table = (options.Get("table") ?? "gpt").ToLowerInvariant() switch
        {
            "mbr" => VirtualDiskPartitionTableKind.Mbr,
            "gpt" => VirtualDiskPartitionTableKind.Gpt,
            var value => throw new CliUsageException($"未対応のパーティション表です: {value}"),
        };
        var specifications = options.GetMany("partition");
        if (specifications.Count == 0)
        {
            throw new CliUsageException("少なくとも1つの--partitionを指定してください。");
        }

        var partitions = specifications.Select((specification, index) =>
            ParsePartition(specification, index + 1)).ToArray();
        var progress = new Progress<DiskImageProgress>(update =>
        {
            var suffix = update.Percentage is int percentage ? $" ({percentage}%)" : string.Empty;
            Console.Error.WriteLine(update.Message + suffix);
        });
        var result = await VirtualDiskCreationService.CreateAsync(
            new VirtualDiskCreationRequest(outputPath, size, container, table, partitions),
            progress);
        Console.WriteLine(
            $"Created {result.ContainerFormat} disk: {result.DestinationPath} "
            + $"({result.CapacityBytes} bytes, {result.Partitions.Count} partition(s))");
        return 0;
    }

    private static FileSystemContext OpenFileSystem(ParsedOptions options)
    {
        var reader = DiskImageReaderFactory.Open(options.Positional);
        try
        {
            var partitions = PartitionTableReader.ReadPartitions(reader);
            var partitionNumber = options.GetInt32("partition");
            var partition = partitionNumber is null
                ? partitions.Count == 1
                    ? partitions[0]
                    : throw new CliUsageException("複数パーティションがあるため--partitionを指定してください。")
                : partitions.SingleOrDefault(candidate => candidate.Number == partitionNumber.Value)
                    ?? throw new CliUsageException($"パーティション#{partitionNumber}が見つかりません。");
            partition.FileSystem = FileSystemDetector.Detect(reader, partition);
            var fileSystem = FileSystemDetector.TryOpen(reader, partition, out var error)
                ?? throw new InvalidDataException(error);
            return new FileSystemContext(reader, fileSystem);
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    private static VfsNode ResolvePath(IReadOnlyFileSystem fileSystem, string virtualPath)
    {
        var current = fileSystem.Root;
        foreach (var component in VirtualPath.Split(virtualPath))
        {
            var comparison = fileSystem.Name is "FAT16" or "FAT32" or "exFAT" or "NTFS"
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            current = fileSystem.ListDirectory(current)
                .SingleOrDefault(entry => string.Equals(entry.Name, component, comparison))
                ?? throw new FileNotFoundException($"仮想パスが見つかりません: {virtualPath}");
        }

        return current;
    }

    private static VirtualDiskPartitionDefinition ParsePartition(string specification, int number)
    {
        var parts = specification.Split(':', 4);
        if (parts.Length < 2)
        {
            throw new CliUsageException("--partitionは xfs|ext4|ntfs:SIZE[:LABEL[:NAME]] の形式で指定してください。");
        }

        var fileSystem = parts[0].ToLowerInvariant() switch
        {
            "ntfs" => VirtualDiskFileSystemKind.Ntfs,
            "ext4" => VirtualDiskFileSystemKind.Ext4,
            "xfs" => VirtualDiskFileSystemKind.Xfs,
            var value => throw new CliUsageException($"未対応のファイルシステムです: {value}"),
        };
        var label = parts.Length >= 3 && !string.IsNullOrWhiteSpace(parts[2])
            ? parts[2]
            : $"VDT_{fileSystem.ToString().ToUpperInvariant()}_{number}";
        var name = parts.Length >= 4 && !string.IsNullOrWhiteSpace(parts[3])
            ? parts[3]
            : $"{fileSystem} partition {number}";
        return new VirtualDiskPartitionDefinition(ParseSize(parts[1]), name, label, fileSystem);
    }

    private static long ParseSize(string text)
    {
        var value = text.Trim();
        var units = new (string Suffix, long Multiplier)[]
        {
            ("TiB", 1024L * 1024 * 1024 * 1024),
            ("GiB", 1024L * 1024 * 1024),
            ("MiB", 1024L * 1024),
            ("KiB", 1024L),
            ("TB", 1_000_000_000_000L),
            ("GB", 1_000_000_000L),
            ("MB", 1_000_000L),
            ("KB", 1_000L),
            ("B", 1L),
        };
        foreach (var (suffix, multiplier) in units)
        {
            if (value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                var number = value[..^suffix.Length].Trim();
                return checked(long.Parse(number, NumberStyles.Integer, CultureInfo.InvariantCulture) * multiplier);
            }
        }

        return long.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
    }

    private static string InferContainer(string path) =>
        Path.GetExtension(path).Equals(".qcow2", StringComparison.OrdinalIgnoreCase)
            ? "qcow2"
            : "raw";

    private static bool IsHelp(string value) => value is "-h" or "--help" or "help";

    private static void PrintHelp()
    {
        Console.WriteLine(
            """
            VirtualDisk Tools CLI

            Usage:
              vdt info IMAGE
              vdt list IMAGE [--partition NUMBER] [--path VIRTUAL_PATH]
              vdt extract IMAGE [--partition NUMBER] --path VIRTUAL_PATH --output FILE
              vdt create OUTPUT --size SIZE [--container raw|qcow2] [--table mbr|gpt]
                         --partition xfs|ext4|ntfs:SIZE[:LABEL[:NAME]] [--partition ...]

            Size suffixes: KiB, MiB, GiB, TiB (or decimal KB, MB, GB, TB).
            NTFS, ext4, and XFS creation are fully managed and do not require WSL or native mkfs tools.
            """);
    }

    private sealed class FileSystemContext(IDiskImageReader reader, IReadOnlyFileSystem fileSystem) : IDisposable
    {
        public IReadOnlyFileSystem FileSystem { get; } = fileSystem;

        public void Dispose()
        {
            (FileSystem as IDisposable)?.Dispose();
            reader.Dispose();
        }
    }

    private sealed class CliUsageException(string message) : Exception(message);

    private sealed class ParsedOptions
    {
        private readonly Dictionary<string, List<string>> _values;

        private ParsedOptions(string positional, Dictionary<string, List<string>> values)
        {
            Positional = positional;
            _values = values;
        }

        public string Positional { get; }

        public static ParsedOptions Parse(
            string[] args,
            IReadOnlyCollection<string> allowedOptions,
            params string[] repeatableOptions)
        {
            if (args.Length == 0 || args[0].StartsWith('-'))
            {
                throw new CliUsageException("ディスクイメージまたは出力先を指定してください。");
            }

            var allowed = allowedOptions.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var repeatable = repeatableOptions.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var values = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            for (var index = 1; index < args.Length; index += 2)
            {
                var option = args[index];
                if (!option.StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Length)
                {
                    throw new CliUsageException($"オプション名と値の組が不正です: {option}");
                }

                var name = option[2..];
                if (!allowed.Contains(name))
                {
                    throw new CliUsageException($"未対応のオプションです: {option}");
                }

                if (!values.TryGetValue(name, out var items))
                {
                    items = [];
                    values.Add(name, items);
                }
                else if (!repeatable.Contains(name))
                {
                    throw new CliUsageException($"オプションが重複しています: {option}");
                }

                items.Add(args[index + 1]);
            }

            return new ParsedOptions(args[0], values);
        }

        public string? Get(string name) =>
            _values.TryGetValue(name, out var values) ? values.Single() : null;

        public string Require(string name) =>
            Get(name) ?? throw new CliUsageException($"--{name}を指定してください。");

        public IReadOnlyList<string> GetMany(string name) =>
            _values.TryGetValue(name, out var values) ? values : [];

        public int? GetInt32(string name)
        {
            var value = Get(name);
            if (value is null)
            {
                return null;
            }

            return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
                ? parsed
                : throw new CliUsageException($"--{name}には正の整数を指定してください。");
        }
    }
}
