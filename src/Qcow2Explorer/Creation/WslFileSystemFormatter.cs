using System.ComponentModel;
using System.Diagnostics;
using Qcow2Explorer.Creation;

namespace Qcow2Explorer.Platform.Windows;

internal sealed class WslFileSystemFormatter(string distribution) : IVirtualDiskFileSystemFormatter
{
    private readonly string _distribution = string.IsNullOrWhiteSpace(distribution)
        ? throw new ArgumentException("WSL distributionを指定してください。", nameof(distribution))
        : distribution;

    public string Name => $"WSL ({_distribution})";

    public bool Supports(VirtualDiskFileSystemKind fileSystem) =>
        fileSystem is VirtualDiskFileSystemKind.Xfs or VirtualDiskFileSystemKind.Ext4;

    public async ValueTask VerifyAvailableAsync(
        VirtualDiskFileSystemKind fileSystem,
        CancellationToken cancellationToken = default)
    {
        var (tool, package) = GetTool(fileSystem);
        var result = await RunWslAsync([tool, "-V"], cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new NotSupportedException(
                $"WSL上の{Path.GetFileName(tool)}を利用できません。Ubuntuへ{package}をインストールしてください。"
                + Environment.NewLine
                + result.Error.Trim());
        }
    }

    public async Task FormatAsync(
        string imagePath,
        VirtualDiskPartitionLayout layout,
        IReadOnlyList<VirtualDiskInitialFile> initialFiles,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(initialFiles);
        _ = GetTool(layout.FileSystem);
        var wslPath = ConvertToWslPath(imagePath);
        var command = layout.FileSystem switch
        {
            VirtualDiskFileSystemKind.Xfs => new[]
            {
                "/usr/sbin/mkfs.xfs", "-q", "-f", "-m", "crc=1,reflink=1,bigtime=1",
                "-n", "ftype=1", "-L", layout.VolumeLabel, wslPath,
            },
            VirtualDiskFileSystemKind.Ext4 =>
            [
                "/usr/sbin/mkfs.ext4", "-q", "-F", "-b", "4096", "-I", "256",
                "-L", layout.VolumeLabel, wslPath,
            ],
            _ => throw new NotSupportedException("WSLフォーマッターは現在XFSとext4だけに使用します。"),
        };
        var result = await RunWslAsync(command, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new IOException(
                $"{Path.GetFileName(command[0])}による{VirtualDiskPartitionTableWriter.GetDisplayName(layout.FileSystem)}初期化が"
                + $"終了コード{result.ExitCode}で失敗しました。{Environment.NewLine}{result.Error.Trim()}");
        }

        await PopulateAndUnmountAsync(imagePath, layout.FileSystem, initialFiles, cancellationToken);
    }

    internal static VirtualDiskFormatterRegistry CreateRegistry(string distribution) => new(
        [
            new ManagedNtfsFileSystemFormatter(),
            new ManagedExt4FileSystemFormatter(),
            new ManagedXfsFileSystemFormatter(),
            new WslFileSystemFormatter(distribution),
        ]);

    private static (string Tool, string Package) GetTool(VirtualDiskFileSystemKind fileSystem) =>
        fileSystem switch
        {
            VirtualDiskFileSystemKind.Xfs => ("/usr/sbin/mkfs.xfs", "xfsprogs"),
            VirtualDiskFileSystemKind.Ext4 => ("/usr/sbin/mkfs.ext4", "e2fsprogs"),
            _ => throw new NotSupportedException("WSLフォーマッターは現在XFSとext4だけに使用します。"),
        };

    private async Task PopulateAndUnmountAsync(
        string imagePath,
        VirtualDiskFileSystemKind fileSystem,
        IReadOnlyList<VirtualDiskInitialFile> initialFiles,
        CancellationToken cancellationToken)
    {
        var fileSystemName = VirtualDiskPartitionTableWriter.GetDisplayName(fileSystem);
        var mountPath = $"/tmp/vdt-fs-create-{Guid.NewGuid():N}";
        var created = false;
        try
        {
            var mkdir = await RunWslAsync(["/usr/bin/mkdir", "--", mountPath], cancellationToken);
            if (mkdir.ExitCode != 0)
            {
                throw new IOException($"{fileSystemName}初期化用mount directoryを作成できません: {mkdir.Error.Trim()}");
            }

            created = true;
            var mount = await RunWslAsync(
                ["/usr/bin/mount", "-o", "loop", "--", ConvertToWslPath(imagePath), mountPath],
                cancellationToken);
            if (mount.ExitCode != 0)
            {
                throw new IOException($"初期化した{fileSystemName}を検証mountできません: {mount.Error.Trim()}");
            }

            var seed = await RunWslAsync(
                [
                    "/usr/bin/bash", "-c",
                    "printf '%s\\n' 'Created by Virtual Disk Explorer. This file may be deleted.' > \"$1/VDT-README.txt\"",
                    "vdt-fs-create", mountPath,
                ],
                cancellationToken);
            if (seed.ExitCode != 0)
            {
                throw new IOException($"{fileSystemName}初期案内ファイルを作成できません: {seed.Error.Trim()}");
            }

            foreach (var initialFile in initialFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var copy = await RunWslAsync(
                    ["/usr/bin/cp", "--", ConvertToWslPath(initialFile.SourcePath), mountPath + "/" + initialFile.DestinationName],
                    cancellationToken);
                if (copy.ExitCode != 0)
                {
                    throw new IOException(
                        $"初期ファイルを{fileSystemName}へ配置できません: {initialFile.DestinationName}"
                        + Environment.NewLine + copy.Error.Trim());
                }
            }

            var unmount = await RunWslAsync(["/usr/bin/umount", "--", mountPath], cancellationToken);
            if (unmount.ExitCode != 0)
            {
                throw new IOException($"初期化した{fileSystemName}を正常unmountできません: {unmount.Error.Trim()}");
            }
        }
        finally
        {
            if (created)
            {
                await TryRunCleanupAsync(["/usr/bin/umount", "--", mountPath]);
                await TryRunCleanupAsync(["/usr/bin/rmdir", "--", mountPath]);
            }
        }
    }

    private async Task TryRunCleanupAsync(IReadOnlyList<string> command)
    {
        try
        {
            _ = await RunWslAsync(command, CancellationToken.None);
        }
        catch
        {
            // Preserve the original creation failure.
        }
    }

    private async Task<WslResult> RunWslAsync(
        IReadOnlyList<string> command,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("WSLフォーマッターはWindows専用です。");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "wsl.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("--distribution");
        startInfo.ArgumentList.Add(_distribution);
        startInfo.ArgumentList.Add("--user");
        startInfo.ArgumentList.Add("root");
        startInfo.ArgumentList.Add("--exec");
        foreach (var argument in command)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new Win32Exception("wsl.exeを開始できませんでした。");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Cancellation remains the primary result.
            }

            throw;
        }

        return new WslResult(process.ExitCode, await stdoutTask, await stderrTask);
    }

    internal static string ConvertToWslPath(string windowsPath)
    {
        var fullPath = Path.GetFullPath(windowsPath);
        var root = Path.GetPathRoot(fullPath);
        if (root is null || root.Length < 2 || root[1] != ':' || !char.IsAsciiLetter(root[0]))
        {
            throw new NotSupportedException("仮想ディスク作成先はWSLから参照できるローカルドライブ上にしてください。");
        }

        return $"/mnt/{char.ToLowerInvariant(root[0])}/{fullPath[3..].Replace('\\', '/')}";
    }

    private sealed record WslResult(int ExitCode, string Output, string Error);
}
