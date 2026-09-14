using System.Globalization;
using Qcow2Explorer.Core;
using Qcow2Explorer.Creation;
using Qcow2Explorer.FileSystems;
using Qcow2Explorer.Partitions;

return await Cli.RunAsync(args);

internal static class Cli
{
    private const int DefaultMaximumListedEntries = 100_000;

    public static async Task<int> RunAsync(string[] args)
    {
        using var cancellationSource = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellationSource.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
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
                "info" => RunInfo(args[1..], cancellationSource.Token),
                "list" => RunList(args[1..], cancellationSource.Token),
                "verify" => RunVerify(args[1..], cancellationSource.Token),
                "extract" => await RunExtractAsync(args[1..], cancellationSource.Token),
                "create" => await RunCreateAsync(args[1..], cancellationSource.Token),
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
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static int RunInfo(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length != 1)
        {
            throw new CliUsageException("infoにはディスクイメージを1つ指定してください。");
        }

        using var reader = DiskImageReaderFactory.Open(args[0], cancellationToken: cancellationToken);
        Console.WriteLine($"Path: {reader.Path}");
        Console.WriteLine($"Format: {reader.FormatName}");
        Console.WriteLine($"Size: {reader.Length} bytes");
        var partitions = PartitionTableReader.ReadPartitionsWithWholeDiskFallback(reader, cancellationToken);
        Console.WriteLine($"Partitions: {partitions.Count}");
        foreach (var partition in partitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            partition.FileSystem = FileSystemDetector.Detect(reader, partition, cancellationToken);
            Console.WriteLine(
                $"  #{partition.Number} {partition.Scheme} offset={partition.StartOffset} "
                + $"size={partition.LengthBytes} fs={partition.FileSystem}");
        }

        return 0;
    }

    private static int RunList(string[] args, CancellationToken cancellationToken)
    {
        var options = ParsedOptions.Parse(args, ["partition", "path", "max-entries"]);
        using var context = OpenFileSystem(options, cancellationToken);
        var directory = ResolvePath(context.FileSystem, options.Get("path") ?? "/", cancellationToken);
        if (!directory.IsDirectory)
        {
            throw new CliUsageException("listの対象はディレクトリである必要があります。");
        }

        var entries = context.FileSystem.ListDirectory(directory)
            ?? throw new InvalidDataException("ファイルシステムがnullの一覧を返しました。");
        var maximumEntries = options.GetInt32("max-entries") ?? DefaultMaximumListedEntries;
        if (entries.Count > maximumEntries)
        {
            throw new NotSupportedException(
                $"ディレクトリ項目数 ({entries.Count:N0}) が表示上限 ({maximumEntries:N0}) を超えています。"
                + " 必要な場合は--max-entriesを明示的に指定してください。");
        }

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Console.WriteLine(
                $"{(entry.IsDirectory ? "d" : "-")} {entry.Size,14} "
                + $"{entry.ModifiedUtc?.ToString("u", CultureInfo.InvariantCulture) ?? "-",20} {entry.Name}");
        }

        return 0;
    }

    private static async Task<int> RunExtractAsync(string[] args, CancellationToken cancellationToken)
    {
        var options = ParsedOptions.Parse(args, ["partition", "path", "output"]);
        var virtualPath = options.Require("path");
        var outputPath = Path.GetFullPath(options.Require("output"));
        if (File.Exists(outputPath) || Directory.Exists(outputPath))
        {
            throw new IOException($"抽出先は既に存在します: {outputPath}");
        }

        using var context = OpenFileSystem(options, cancellationToken);
        var file = ResolvePath(context.FileSystem, virtualPath, cancellationToken);
        if (!file.IsDirectory && file.Size < 0)
        {
            throw new CliUsageException("サイズが不正なファイルは抽出できません。");
        }

        var lastPercentage = -1;
        var progress = new CallbackProgress<CopyProgress>(update =>
        {
            var percentage = update.TotalBytes == 0
                ? 100
                : (int)Math.Min(100, update.BytesCopied * 100d / update.TotalBytes);
            if (percentage == lastPercentage)
            {
                return;
            }

            lastPercentage = percentage;
            Console.Error.WriteLine($"Extracting: {percentage}% — {update.CurrentPath}");
        });
        if (file.IsDirectory)
        {
            Directory.CreateDirectory(outputPath);
            var result = FileSystemExporter.CopyNodes(
                context.FileSystem,
                context.FileSystem.ListDirectory(file),
                outputPath,
                progress,
                cancellationToken);
            Console.WriteLine(
                $"Extracted directory to {outputPath}: files={result.FilesCopied:N0}, "
                + $"directories={result.DirectoriesCreated:N0}, bytes={result.BytesCopied:N0}, "
                + $"errors={result.Errors.Count:N0}");
            if (result.Errors.Count > 0)
            {
                Console.Error.WriteLine("Some entries failed. See VirtualDiskExplorer-copy-errors*.json in the output directory.");
                return 3;
            }

            return 0;
        }

        await FileSystemExporter.ExtractFileAsync(
            context.FileSystem,
            file,
            outputPath,
            progress: progress,
            cancellationToken: cancellationToken);

        Console.WriteLine($"Extracted {file.Size} bytes to {outputPath}");
        return 0;
    }

    private static int RunVerify(string[] args, CancellationToken cancellationToken)
    {
        var options = ParsedOptions.Parse(args, ["partition", "path"]);
        using var context = OpenFileSystem(options, cancellationToken);
        var start = ResolvePath(context.FileSystem, options.Get("path") ?? "/", cancellationToken);
        long lastReportedBytes = 0;
        var lastReportedEntries = 0;
        var progress = new CallbackProgress<FileSystemVerificationProgress>(update =>
        {
            if (update.BytesRead - lastReportedBytes < 256L * 1024 * 1024
                && update.EntriesChecked - lastReportedEntries < 1_000)
            {
                return;
            }

            lastReportedBytes = update.BytesRead;
            lastReportedEntries = update.EntriesChecked;
            Console.Error.WriteLine(
                $"Verifying: entries={update.EntriesChecked:N0}, files={update.FilesChecked:N0}, "
                + $"bytes={update.BytesRead:N0}, path={update.CurrentPath}");
        });
        var result = FileSystemVerifier.Verify(
            context.FileSystem,
            start,
            progress,
            cancellationToken);
        Console.WriteLine(
            $"Verification {(result.IsValid ? "passed" : "failed")}: "
            + $"entries={result.EntriesChecked:N0}, files={result.FilesChecked:N0}, "
            + $"directories={result.DirectoriesChecked:N0}, bytes={result.BytesRead:N0}, "
            + $"issues={result.Issues.Count:N0}, completed={result.Completed}");
        const int maximumDisplayedIssues = 100;
        foreach (var issue in result.Issues.Take(maximumDisplayedIssues))
        {
            Console.Error.WriteLine($"  {issue.Path}: {issue.ErrorType}: {issue.Message}");
        }

        if (result.Issues.Count > maximumDisplayedIssues)
        {
            Console.Error.WriteLine(
                $"  ... {result.Issues.Count - maximumDisplayedIssues:N0} additional issue(s) omitted");
        }

        return result.IsValid ? 0 : 3;
    }

    private static async Task<int> RunCreateAsync(string[] args, CancellationToken cancellationToken)
    {
        var options = ParsedOptions.Parse(
            args,
            ["size", "container", "table", "partition", "initial-file"],
            "partition",
            "initial-file");
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
        var initialFiles = options.GetMany("initial-file")
            .Select(ParseInitialFile)
            .ToArray();
        string? lastProgressMessage = null;
        int? lastProgressPercentage = null;
        var progress = new CallbackProgress<DiskImageProgress>(update =>
        {
            var percentage = update.Percentage;
            if (string.Equals(update.Message, lastProgressMessage, StringComparison.Ordinal)
                && percentage == lastProgressPercentage)
            {
                return;
            }

            lastProgressMessage = update.Message;
            lastProgressPercentage = percentage;
            var suffix = percentage is int value ? $" ({value}%)" : string.Empty;
            Console.Error.WriteLine(update.Message + suffix);
        });
        var result = await VirtualDiskCreationService.CreateAsync(
            new VirtualDiskCreationRequest(
                outputPath,
                size,
                container,
                table,
                partitions,
                initialFiles),
            progress,
            cancellationToken);
        Console.WriteLine(
            $"Created {result.ContainerFormat} disk: {result.DestinationPath} "
            + $"({result.CapacityBytes} bytes, {result.Partitions.Count} partition(s))");
        return 0;
    }

    private static FileSystemContext OpenFileSystem(
        ParsedOptions options,
        CancellationToken cancellationToken)
    {
        var reader = DiskImageReaderFactory.Open(options.Positional, cancellationToken: cancellationToken);
        try
        {
            var partitions = PartitionTableReader.ReadPartitionsWithWholeDiskFallback(reader, cancellationToken);
            var partitionNumber = options.GetInt32("partition");
            var partition = partitionNumber is null
                ? partitions.Count == 1
                    ? partitions[0]
                    : throw new CliUsageException("複数パーティションがあるため--partitionを指定してください。")
                : partitions.SingleOrDefault(candidate => candidate.Number == partitionNumber.Value)
                    ?? throw new CliUsageException($"パーティション#{partitionNumber}が見つかりません。");
            partition.FileSystem = FileSystemDetector.Detect(reader, partition, cancellationToken);
            var fileSystem = FileSystemDetector.TryOpen(reader, partition, out var error, cancellationToken)
                ?? throw new InvalidDataException(error);
            return new FileSystemContext(reader, fileSystem);
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    private static VfsNode ResolvePath(
        IReadOnlyFileSystem fileSystem,
        string virtualPath,
        CancellationToken cancellationToken)
    {
        if (!FileEditService.TryResolvePath(
                fileSystem,
                virtualPath,
                out var node,
                cancellationToken))
        {
            throw new FileNotFoundException($"仮想パスが見つかりません: {virtualPath}");
        }

        return node;
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

    private static VirtualDiskInitialFile ParseInitialFile(string specification)
    {
        var separator = specification.IndexOf('=');
        if (separator <= 0 || separator == specification.Length - 1
            || !int.TryParse(
                specification.AsSpan(0, separator),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var partitionNumber)
            || partitionNumber <= 0)
        {
            throw new CliUsageException(
                "--initial-fileは PARTITION_NUMBER=HOST_FILE の形式で指定してください。");
        }

        var sourcePath = Path.GetFullPath(specification[(separator + 1)..]);
        var destinationName = Path.GetFileName(sourcePath);
        if (string.IsNullOrWhiteSpace(destinationName))
        {
            throw new CliUsageException("--initial-fileのホスト側ファイル名を取得できません。");
        }

        return new VirtualDiskInitialFile(partitionNumber, sourcePath, destinationName);
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
                return ParsePositiveSize(number, multiplier);
            }
        }

        return ParsePositiveSize(value, 1);
    }

    private static long ParsePositiveSize(string value, long multiplier)
    {
        if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            || parsed <= 0)
        {
            throw new CliUsageException("サイズには正の整数を指定してください。");
        }

        try
        {
            return checked(parsed * multiplier);
        }
        catch (OverflowException)
        {
            throw new CliUsageException("指定されたサイズが大きすぎます。");
        }
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
              vdt list IMAGE [--partition NUMBER] [--path VIRTUAL_PATH] [--max-entries NUMBER]
              vdt verify IMAGE [--partition NUMBER] [--path VIRTUAL_PATH]
              vdt extract IMAGE [--partition NUMBER] --path VIRTUAL_PATH --output FILE_OR_DIRECTORY
              vdt create OUTPUT --size SIZE [--container raw|qcow2] [--table mbr|gpt]
                         --partition xfs|ext4|ntfs:SIZE[:LABEL[:NAME]] [--partition ...]
                         [--initial-file PARTITION_NUMBER=HOST_FILE] [...]

            Size suffixes: KiB, MiB, GiB, TiB (or decimal KB, MB, GB, TB).
            NTFS, ext4, and XFS creation are fully managed and do not require WSL or native mkfs tools.
            list displays at most 100,000 entries unless --max-entries is explicitly specified.
            Press Ctrl+C to cancel long-running operations safely.
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

    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }

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
