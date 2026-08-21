using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Qcow2Explorer.Core;
using Qcow2Explorer.FileSystems;
using Qcow2Explorer.Partitions;

internal static class RealImageRegressionRunner
{
    private const int HashBufferSize = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static RealImageRegressionSummary Run(string manifestPath)
    {
        var fullManifestPath = Path.GetFullPath(manifestPath);
        var manifest = JsonSerializer.Deserialize<RealImageRegressionManifest>(
                File.ReadAllText(fullManifestPath),
                JsonOptions)
            ?? throw new InvalidDataException("実イメージ回帰テストmanifestを読み取れませんでした。");
        ValidateManifest(manifest);

        var manifestDirectory = Path.GetDirectoryName(fullManifestPath)
            ?? throw new InvalidDataException("manifestの保存先フォルダーを判定できません。");
        var stopwatch = Stopwatch.StartNew();
        foreach (var regressionCase in manifest.Cases)
        {
            RunCase(regressionCase, manifestDirectory);
        }

        stopwatch.Stop();
        return new RealImageRegressionSummary(manifest.Cases.Count, stopwatch.Elapsed);
    }

    private static void ValidateManifest(RealImageRegressionManifest manifest)
    {
        if (manifest.Version != 1)
        {
            throw new InvalidDataException($"未対応の実イメージ回帰テストmanifest versionです: {manifest.Version}");
        }

        if (manifest.Cases.Count == 0)
        {
            throw new InvalidDataException("実イメージ回帰テストcaseがありません。");
        }

        var duplicateName = manifest.Cases
            .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => string.IsNullOrWhiteSpace(group.Key) || group.Count() > 1);
        if (duplicateName is not null)
        {
            throw new InvalidDataException("各回帰テストcaseには一意のnameが必要です。");
        }
    }

    private static void RunCase(RealImageRegressionCase regressionCase, string manifestDirectory)
    {
        var expandedPath = Environment.ExpandEnvironmentVariables(regressionCase.Path);
        var imagePath = Path.GetFullPath(expandedPath, manifestDirectory);
        if (!File.Exists(imagePath))
        {
            throw new FileNotFoundException($"[{regressionCase.Name}] 実イメージが見つかりません。", imagePath);
        }

        var expectedImageHash = NormalizeSha256(regressionCase.Sha256, regressionCase.Name, "image");
        Console.WriteLine($"[{regressionCase.Name}] SHA-256を検証中 ({new FileInfo(imagePath).Length:N0} bytes)...");
        var actualImageHash = ComputeFileSha256(imagePath);
        Require(
            string.Equals(actualImageHash, expectedImageHash, StringComparison.OrdinalIgnoreCase),
            regressionCase.Name,
            $"image SHA-256 mismatch: expected={expectedImageHash}, actual={actualImageHash}");

        if (regressionCase.VerifyLzopCacheReuse)
        {
            RunLzopCacheCase(regressionCase, imagePath);
        }
        else
        {
            using var reader = DiskImageReaderFactory.Open(imagePath);
            ValidateReader(regressionCase, reader);
        }

        Console.WriteLine($"[{regressionCase.Name}] passed");
    }

    private static void RunLzopCacheCase(RealImageRegressionCase regressionCase, string imagePath)
    {
        Require(
            DiskImageReaderFactory.IsLzopFile(imagePath),
            regressionCase.Name,
            "verifyLzopCacheReuse requires an lzop image");

        var cacheRoot = Path.Combine(Path.GetTempPath(), $"VirtualDiskTools-real-regression-{Guid.NewGuid():N}");
        try
        {
            var firstStopwatch = Stopwatch.StartNew();
            using (var firstReader = DiskImageReaderFactory.Open(
                imagePath,
                lzopOpenMode: LzopOpenMode.CachedRaw,
                lzopTemporaryDirectory: cacheRoot))
            {
                firstStopwatch.Stop();
                Require(
                    firstReader is TemporaryLzopDiskImageReader { CacheReused: false },
                    regressionCase.Name,
                    "first LZO cache open must expand a new RAW cache");
                ValidateReader(regressionCase, firstReader);
            }

            var reuseStopwatch = Stopwatch.StartNew();
            using (var reusedReader = DiskImageReaderFactory.Open(
                imagePath,
                lzopOpenMode: LzopOpenMode.CachedRaw,
                lzopTemporaryDirectory: cacheRoot))
            {
                reuseStopwatch.Stop();
                Require(
                    reusedReader is TemporaryLzopDiskImageReader { CacheReused: true },
                    regressionCase.Name,
                    "second LZO cache open must reuse the verified RAW cache");
                ValidateReader(regressionCase, reusedReader);
            }

            Console.WriteLine(
                $"[{regressionCase.Name}] LZO cache: first={firstStopwatch.Elapsed.TotalSeconds:0.00}s, "
                + $"reuse={reuseStopwatch.Elapsed.TotalSeconds:0.00}s");

            if (regressionCase.VerifyLzopCacheCancellation)
            {
                RunLzopCacheCancellation(regressionCase, imagePath);
            }
        }
        finally
        {
            if (Directory.Exists(cacheRoot))
            {
                Directory.Delete(cacheRoot, recursive: true);
            }
        }
    }

    private static void RunLzopCacheCancellation(RealImageRegressionCase regressionCase, string imagePath)
    {
        var cancellationRoot = Path.Combine(Path.GetTempPath(), $"VirtualDiskTools-real-cancel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(cancellationRoot);
        using var cancellation = new CancellationTokenSource();
        var canceled = false;
        try
        {
            try
            {
                using var _ = DiskImageReaderFactory.Open(
                    imagePath,
                    new CallbackProgress<DiskImageProgress>(item =>
                    {
                        if (item.Message.Contains("RAWキャッシュへ展開中", StringComparison.Ordinal))
                        {
                            cancellation.Cancel();
                        }
                    }),
                    LzopOpenMode.CachedRaw,
                    cancellationRoot,
                    cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                canceled = true;
            }

            Require(canceled, regressionCase.Name, "LZO cache cancellation did not interrupt expansion");
            Require(
                !Directory.EnumerateFileSystemEntries(cancellationRoot).Any(),
                regressionCase.Name,
                "LZO cache cancellation left partial files");
        }
        finally
        {
            if (Directory.Exists(cancellationRoot))
            {
                Directory.Delete(cancellationRoot, recursive: true);
            }
        }
    }

    private static void ValidateReader(RealImageRegressionCase regressionCase, IDiskImageReader reader)
    {
        if (!string.IsNullOrWhiteSpace(regressionCase.ExpectedFormatContains))
        {
            Require(
                reader.FormatName.Contains(regressionCase.ExpectedFormatContains, StringComparison.OrdinalIgnoreCase),
                regressionCase.Name,
                $"format mismatch: expected contains '{regressionCase.ExpectedFormatContains}', actual='{reader.FormatName}'");
        }

        if (regressionCase.ExpectedDiskLength is long expectedLength)
        {
            Require(
                reader.Length == expectedLength,
                regressionCase.Name,
                $"disk length mismatch: expected={expectedLength}, actual={reader.Length}");
        }

        var partitions = PartitionTableReader.ReadPartitions(reader).ToList();
        if (regressionCase.ExpectedPartitionCount is int expectedPartitionCount)
        {
            Require(
                partitions.Count == expectedPartitionCount,
                regressionCase.Name,
                $"partition count mismatch: expected={expectedPartitionCount}, actual={partitions.Count}");
        }

        foreach (var partitionExpectation in regressionCase.Partitions)
        {
            var partition = partitions.SingleOrDefault(item => item.Number == partitionExpectation.Number);
            Require(partition is not null, regressionCase.Name, $"partition #{partitionExpectation.Number} was not found");
            ValidatePartition(regressionCase.Name, reader, partition!, partitionExpectation);
        }
    }

    private static void ValidatePartition(
        string caseName,
        IBlockReader reader,
        PartitionInfo partition,
        RealImagePartitionExpectation expectation)
    {
        partition.FileSystem = FileSystemDetector.Detect(reader, partition);
        byte[] recoveryKey = [];
        IReadOnlyFileSystem? fileSystem = null;
        try
        {
            var shouldOpen = expectation.Files.Count > 0
                || !string.IsNullOrWhiteSpace(expectation.RecoveryPasswordEnvironmentVariable)
                || expectation.ExpectedFileSystem.Contains("->", StringComparison.Ordinal);
            if (shouldOpen)
            {
                if (!string.IsNullOrWhiteSpace(expectation.RecoveryPasswordEnvironmentVariable))
                {
                    var password = Environment.GetEnvironmentVariable(expectation.RecoveryPasswordEnvironmentVariable);
                    Require(
                        !string.IsNullOrWhiteSpace(password),
                        caseName,
                        $"environment variable '{expectation.RecoveryPasswordEnvironmentVariable}' is not set");
                    Require(
                        BitLockerRecoveryPassword.TryDecode(password, out recoveryKey, out var decodeError),
                        caseName,
                        $"BitLocker recovery password validation failed: {decodeError}");
                }

                fileSystem = recoveryKey.Length == 0
                    ? FileSystemDetector.TryOpen(reader, partition, out var openError)
                    : FileSystemDetector.TryOpen(reader, partition, recoveryKey, out openError);
                Require(fileSystem is not null, caseName, $"partition #{partition.Number} open failed: {openError}");
            }

            Require(
                string.Equals(partition.FileSystem, expectation.ExpectedFileSystem, StringComparison.OrdinalIgnoreCase),
                caseName,
                $"partition #{partition.Number} filesystem mismatch: expected='{expectation.ExpectedFileSystem}', actual='{partition.FileSystem}'");

            foreach (var fileExpectation in expectation.Files)
            {
                ValidateFile(caseName, fileSystem!, fileExpectation);
            }
        }
        finally
        {
            if (fileSystem is IDisposable disposable)
            {
                disposable.Dispose();
            }

            if (recoveryKey.Length > 0)
            {
                CryptographicOperations.ZeroMemory(recoveryKey);
            }
        }
    }

    private static void ValidateFile(
        string caseName,
        IReadOnlyFileSystem fileSystem,
        RealImageFileExpectation expectation)
    {
        var node = ResolveNode(fileSystem, expectation.Path);
        Require(node is not null, caseName, $"path was not found: {expectation.Path}");
        if (expectation.ExpectedDirectory is bool expectedDirectory)
        {
            Require(
                node!.IsDirectory == expectedDirectory,
                caseName,
                $"path type mismatch: {expectation.Path}");
        }

        if (expectation.ExpectedLength is long expectedLength)
        {
            Require(
                node!.Size == expectedLength,
                caseName,
                $"file length mismatch for {expectation.Path}: expected={expectedLength}, actual={node.Size}");
        }

        if (!string.IsNullOrWhiteSpace(expectation.Sha256))
        {
            Require(!node!.IsDirectory, caseName, $"cannot hash a directory: {expectation.Path}");
            var expectedHash = NormalizeSha256(expectation.Sha256, caseName, expectation.Path);
            var actualHash = ComputeFileSystemFileSha256(fileSystem, node);
            Require(
                string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase),
                caseName,
                $"file SHA-256 mismatch for {expectation.Path}: expected={expectedHash}, actual={actualHash}");
        }

        if (!string.IsNullOrWhiteSpace(expectation.ExpectedModifiedUtc))
        {
            Require(node!.ModifiedUtc is not null, caseName, $"timestamp is unavailable: {expectation.Path}");
            Require(
                DateTimeOffset.TryParse(
                    expectation.ExpectedModifiedUtc,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var expectedModified),
                caseName,
                $"invalid expectedModifiedUtc: {expectation.ExpectedModifiedUtc}");
            var actualModified = new DateTimeOffset(DateTime.SpecifyKind(node.ModifiedUtc!.Value, DateTimeKind.Utc));
            var tolerance = TimeSpan.FromSeconds(expectation.TimestampToleranceSeconds);
            Require(
                (actualModified - expectedModified).Duration() <= tolerance,
                caseName,
                $"timestamp mismatch for {expectation.Path}: expected={expectedModified:O}, actual={actualModified:O}, tolerance={tolerance}");
        }
    }

    private static VfsNode? ResolveNode(IReadOnlyFileSystem fileSystem, string path)
    {
        var current = fileSystem.Root;
        foreach (var segment in path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment is "." or "..")
            {
                throw new InvalidDataException($"回帰テストpathに相対segmentは使用できません: {path}");
            }

            current = fileSystem.ListDirectory(current)
                .SingleOrDefault(item => string.Equals(item.Name, segment, StringComparison.Ordinal));
            if (current is null)
            {
                return null;
            }
        }

        return current;
    }

    private static string ComputeFileSha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string ComputeFileSystemFileSha256(IReadOnlyFileSystem fileSystem, VfsNode file)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long offset = 0;
        while (offset < file.Size)
        {
            var count = checked((int)Math.Min(HashBufferSize, file.Size - offset));
            var data = fileSystem.ReadFile(file, offset, count);
            if (data.Length == 0)
            {
                throw new EndOfStreamException($"ファイルが途中で終了しました: {file.Name}");
            }

            hash.AppendData(data);
            offset += data.Length;
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string NormalizeSha256(string value, string caseName, string label)
    {
        var normalized = value.Trim().Replace("-", "", StringComparison.Ordinal);
        if (normalized.Length != 64 || !normalized.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException($"[{caseName}] {label} SHA-256 must be 64 hexadecimal characters");
        }

        return normalized.ToUpperInvariant();
    }

    private static void Require(bool condition, string caseName, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Real-image regression failed [{caseName}]: {message}");
        }
    }
}

internal sealed record RealImageRegressionSummary(int CaseCount, TimeSpan Elapsed);

internal sealed class RealImageRegressionManifest
{
    public int Version { get; set; }
    public List<RealImageRegressionCase> Cases { get; set; } = [];
}

internal sealed class RealImageRegressionCase
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string ExpectedFormatContains { get; set; } = "";
    public long? ExpectedDiskLength { get; set; }
    public int? ExpectedPartitionCount { get; set; }
    public bool VerifyLzopCacheReuse { get; set; }
    public bool VerifyLzopCacheCancellation { get; set; }
    public List<RealImagePartitionExpectation> Partitions { get; set; } = [];
}

internal sealed class RealImagePartitionExpectation
{
    public int Number { get; set; }
    public string ExpectedFileSystem { get; set; } = "";
    public string RecoveryPasswordEnvironmentVariable { get; set; } = "";
    public List<RealImageFileExpectation> Files { get; set; } = [];
}

internal sealed class RealImageFileExpectation
{
    public string Path { get; set; } = "";
    public bool? ExpectedDirectory { get; set; }
    public long? ExpectedLength { get; set; }
    public string Sha256 { get; set; } = "";
    public string ExpectedModifiedUtc { get; set; } = "";
    public double TimestampToleranceSeconds { get; set; }
}
