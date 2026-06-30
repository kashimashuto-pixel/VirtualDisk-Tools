using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Windows.ProjFS;
using Qcow2Explorer.FileSystems;

namespace Qcow2Explorer.Mounting;

public sealed class ProjectedFileSystemMount : IDisposable
{
    private readonly VirtualizationInstance _instance;
    private readonly ProjectedFileSystemProvider _provider;
    private int _openHandleCount;
    private bool _disposed;

    private ProjectedFileSystemMount(
        IReadOnlyFileSystem fileSystem,
        string rootPath,
        VirtualizationInstance instance,
        ProjectedFileSystemProvider provider)
    {
        FileSystem = fileSystem;
        RootPath = rootPath;
        _instance = instance;
        _provider = provider;
    }

    public IReadOnlyFileSystem FileSystem { get; }
    public string RootPath { get; }
    public int OpenHandleCount => Math.Max(0, Volatile.Read(ref _openHandleCount));
    public int ActiveCallbackCount => _provider.ActiveCallbackCount;
    public bool HasPossibleExternalUse => OpenHandleCount > 0 || ActiveCallbackCount > 0;

    public static bool IsProjFsLibraryPresent()
    {
        var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        return File.Exists(Path.Combine(systemDirectory, "ProjectedFSLib.dll"));
    }

    public static ProjectedFileSystemMount Start(IReadOnlyFileSystem fileSystem, string rootPath)
    {
        return Start(fileSystem, rootPath, allowStaleRootRepair: true);
    }

    private static ProjectedFileSystemMount Start(IReadOnlyFileSystem fileSystem, string rootPath, bool allowStaleRootRepair)
    {
        if (!IsProjFsLibraryPresent())
        {
            throw new ProjFsUnavailableException("ProjFS のライブラリが見つかりません。Windows の Client-ProjFS 機能が無効の可能性があります。");
        }

        Directory.CreateDirectory(rootPath);
        if (MountRootHasEntries(rootPath))
        {
            throw new InvalidOperationException("マウント先フォルダは空にしてください。");
        }

        var instanceId = Guid.NewGuid();
        var markResult = VirtualizationInstance.MarkDirectoryAsVirtualizationRoot(rootPath, instanceId);
        if (markResult is not HResult.Ok and not HResult.AlreadyInitialized)
        {
            if (allowStaleRootRepair && HasStaleReparsePoint(rootPath))
            {
                TryRecreateMountRoot(rootPath);
                return Start(fileSystem, rootPath, allowStaleRootRepair: false);
            }

            throw CreateProjFsException("マウント先フォルダを ProjFS ルートに設定できませんでした。", markResult);
        }

        var mappings = new[]
        {
            new NotificationMapping
            {
                NotificationRoot = string.Empty,
                NotificationMask = NotificationType.FileOpened
                    | NotificationType.FileHandleClosedNoModification
                    | NotificationType.FileHandleClosedFileModified
                    | NotificationType.FileHandleClosedFileDeleted
            }
        };

        var instance = new VirtualizationInstance(rootPath, 0, 0, enableNegativePathCache: false, mappings);
        ProjectedFileSystemMount? mount = null;
        var provider = new ProjectedFileSystemProvider(fileSystem, instance);
        mount = new ProjectedFileSystemMount(fileSystem, rootPath, instance, provider);

        instance.OnNotifyFileOpened = mount.OnFileOpened;
        instance.OnNotifyFileHandleClosedNoModification = mount.OnFileClosed;
        instance.OnNotifyFileHandleClosedFileModifiedOrDeleted = mount.OnFileClosedModifiedOrDeleted;

        var startResult = instance.StartVirtualizing(provider);
        if (startResult != HResult.Ok)
        {
            instance.Dispose();
            if (allowStaleRootRepair && HasStaleReparsePoint(rootPath))
            {
                TryRecreateMountRoot(rootPath);
                return Start(fileSystem, rootPath, allowStaleRootRepair: false);
            }

            throw CreateProjFsException("ProjFS の仮想化を開始できませんでした。", startResult);
        }

        return mount;
    }

    public void OpenInExplorer()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = RootPath,
            UseShellExecute = true
        });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _instance.StopVirtualizing();
        }
        catch
        {
            // The app is shutting down; best effort is enough here.
        }

        _instance.Dispose();

        try
        {
            TryCleanMountRootContents();
            TryRecreateMountRoot(RootPath);
        }
        catch
        {
            // If another process still owns a handle, the close confirmation already warned.
        }
    }

    private static bool MountRootHasEntries(string rootPath)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(rootPath).Any();
        }
        catch (IOException) when (HasStaleReparsePoint(rootPath))
        {
            TryRecreateMountRoot(rootPath);
            return Directory.EnumerateFileSystemEntries(rootPath).Any();
        }
        catch (UnauthorizedAccessException) when (HasStaleReparsePoint(rootPath))
        {
            TryRecreateMountRoot(rootPath);
            return Directory.EnumerateFileSystemEntries(rootPath).Any();
        }
    }

    private static bool HasStaleReparsePoint(string rootPath)
    {
        try
        {
            return Directory.Exists(rootPath)
                && (File.GetAttributes(rootPath) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return false;
        }
    }

    private void TryCleanMountRootContents()
    {
        if (!Directory.Exists(RootPath))
        {
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(RootPath))
        {
            TryDeleteEntry(entry);
        }
    }

    private static void TryRecreateMountRoot(string rootPath)
    {
        if (!Directory.Exists(rootPath))
        {
            Directory.CreateDirectory(rootPath);
            return;
        }

        Directory.Delete(rootPath, recursive: false);
        Directory.CreateDirectory(rootPath);
    }

    private static void TryDeleteEntry(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                foreach (var child in Directory.EnumerateFileSystemEntries(path))
                {
                    TryDeleteEntry(child);
                }

                File.SetAttributes(path, FileAttributes.Normal);
                Directory.Delete(path, recursive: false);
            }
            else if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
        }
        catch
        {
            // If another process still owns a handle, the close confirmation already warned.
        }
    }

    private bool OnFileOpened(
        string relativePath,
        bool isDirectory,
        uint triggeringProcessId,
        string triggeringProcessImageFileName,
        out NotificationType notificationMask)
    {
        Interlocked.Increment(ref _openHandleCount);
        notificationMask = NotificationType.FileHandleClosedNoModification
            | NotificationType.FileHandleClosedFileModified
            | NotificationType.FileHandleClosedFileDeleted;
        return true;
    }

    private void OnFileClosed(
        string relativePath,
        bool isDirectory,
        uint triggeringProcessId,
        string triggeringProcessImageFileName)
    {
        DecrementOpenHandleCount();
    }

    private void OnFileClosedModifiedOrDeleted(
        string relativePath,
        bool isDirectory,
        bool isFileModified,
        bool isFileDeleted,
        uint triggeringProcessId,
        string triggeringProcessImageFileName)
    {
        DecrementOpenHandleCount();
    }

    private void DecrementOpenHandleCount()
    {
        while (true)
        {
            var current = Volatile.Read(ref _openHandleCount);
            if (current <= 0)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _openHandleCount, current - 1, current) == current)
            {
                return;
            }
        }
    }

    private static Exception CreateProjFsException(string message, HResult result)
    {
        return result == HResult.VirtualizationUnavaliable
            ? new ProjFsUnavailableException($"{message}{Environment.NewLine}ProjFS が無効、または利用できない状態です。")
            : new IOException($"{message}{Environment.NewLine}ProjFS result: {result}");
    }

    private sealed class ProjectedFileSystemProvider : IRequiredCallbacks
    {
        private readonly IReadOnlyFileSystem _fileSystem;
        private readonly VirtualizationInstance _instance;
        private readonly ConcurrentDictionary<string, ProjectedNode> _nodeCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, IReadOnlyList<ProjectedNode>> _childrenCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<Guid, EnumerationState> _enumerations = new();
        private int _activeCallbackCount;

        public ProjectedFileSystemProvider(IReadOnlyFileSystem fileSystem, VirtualizationInstance instance)
        {
            _fileSystem = fileSystem;
            _instance = instance;
            _nodeCache[string.Empty] = new ProjectedNode(string.Empty, string.Empty, _fileSystem.Root);
        }

        public int ActiveCallbackCount => Math.Max(0, Volatile.Read(ref _activeCallbackCount));

        public HResult StartDirectoryEnumerationCallback(
            int commandId,
            Guid enumerationId,
            string relativePath,
            uint triggeringProcessId,
            string triggeringProcessImageFileName)
        {
            return WithCallback(() =>
            {
                _enumerations[enumerationId] = new EnumerationState(NormalizeRelativePath(relativePath));
                return HResult.Ok;
            });
        }

        public HResult EndDirectoryEnumerationCallback(Guid enumerationId)
        {
            return WithCallback(() =>
            {
                _enumerations.TryRemove(enumerationId, out _);
                return HResult.Ok;
            });
        }

        public HResult GetDirectoryEnumerationCallback(
            int commandId,
            Guid enumerationId,
            string filterFileName,
            bool restartScan,
            IDirectoryEnumerationResults result)
        {
            return WithCallback(() =>
            {
                if (!_enumerations.TryGetValue(enumerationId, out var state))
                {
                    state = new EnumerationState(string.Empty);
                    _enumerations[enumerationId] = state;
                }

                if (restartScan || state.Items is null)
                {
                    if (!TryGetNode(state.RelativePath, out var directory))
                    {
                        return HResult.PathNotFound;
                    }

                    if (!directory.Node.IsDirectory)
                    {
                        return HResult.Directory;
                    }

                    state.Items = GetChildren(directory)
                        .Where(n => MatchesFilter(n.ProjectedName, filterFileName))
                        .OrderBy(n => n.ProjectedName, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    state.Index = 0;
                }

                while (state.Index < state.Items.Count)
                {
                    var node = state.Items[state.Index];
                    if (!result.Add(
                            node.ProjectedName,
                            node.Node.IsDirectory ? 0 : node.Node.Size,
                            node.Node.IsDirectory,
                            GetFileAttributes(node.Node),
                            GetTimestamp(node.Node),
                            GetTimestamp(node.Node),
                            GetTimestamp(node.Node),
                            GetTimestamp(node.Node)))
                    {
                        break;
                    }

                    state.Index++;
                }

                return HResult.Ok;
            });
        }

        public HResult GetPlaceholderInfoCallback(
            int commandId,
            string relativePath,
            uint triggeringProcessId,
            string triggeringProcessImageFileName)
        {
            return WithCallback(() =>
            {
                if (!TryGetNode(relativePath, out var node))
                {
                    return HResult.FileNotFound;
                }

                return _instance.WritePlaceholderInfo(
                    NormalizeRelativePath(relativePath),
                    GetTimestamp(node.Node),
                    GetTimestamp(node.Node),
                    GetTimestamp(node.Node),
                    GetTimestamp(node.Node),
                    GetFileAttributes(node.Node),
                    node.Node.IsDirectory ? 0 : node.Node.Size,
                    node.Node.IsDirectory,
                    CreateContentId(node.Path, node.Node),
                    CreateProviderId());
            });
        }

        public HResult GetFileDataCallback(
            int commandId,
            string relativePath,
            ulong byteOffset,
            uint length,
            Guid dataStreamId,
            byte[] contentId,
            byte[] providerId,
            uint triggeringProcessId,
            string triggeringProcessImageFileName)
        {
            return WithCallback(() =>
            {
                if (!TryGetNode(relativePath, out var node))
                {
                    return HResult.FileNotFound;
                }

                if (node.Node.IsDirectory)
                {
                    return HResult.Directory;
                }

                if (byteOffset >= (ulong)Math.Max(0, node.Node.Size) || length == 0)
                {
                    return HResult.Ok;
                }

                using var buffer = _instance.CreateWriteBuffer(byteOffset, length, out var alignedOffset, out var alignedLength);
                if (alignedOffset >= (ulong)Math.Max(0, node.Node.Size))
                {
                    return HResult.Ok;
                }

                var bytesToRead = checked((int)Math.Min(alignedLength, (ulong)node.Node.Size - alignedOffset));
                var data = _fileSystem.ReadFile(node.Node, checked((long)alignedOffset), bytesToRead);
                if (data.Length == 0 && bytesToRead > 0)
                {
                    return HResult.InternalError;
                }

                buffer.Stream.Write(data, 0, data.Length);
                return _instance.WriteFileData(dataStreamId, buffer, alignedOffset, checked((uint)data.Length));
            });
        }

        private HResult WithCallback(Func<HResult> callback)
        {
            Interlocked.Increment(ref _activeCallbackCount);
            try
            {
                return callback();
            }
            catch (FileNotFoundException)
            {
                return HResult.FileNotFound;
            }
            catch (DirectoryNotFoundException)
            {
                return HResult.PathNotFound;
            }
            catch
            {
                return HResult.InternalError;
            }
            finally
            {
                Interlocked.Decrement(ref _activeCallbackCount);
            }
        }

        private bool TryGetNode(string relativePath, out ProjectedNode node)
        {
            var normalized = NormalizeRelativePath(relativePath);
            if (_nodeCache.TryGetValue(normalized, out node!))
            {
                return true;
            }

            var parts = normalized.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            var current = _nodeCache[string.Empty];
            var currentPath = string.Empty;
            foreach (var part in parts)
            {
                var children = GetChildren(current);
                var child = children.FirstOrDefault(n => string.Equals(n.ProjectedName, part, StringComparison.OrdinalIgnoreCase));
                if (child is null)
                {
                    node = default!;
                    return false;
                }

                currentPath = CombineRelativePath(currentPath, child.ProjectedName);
                current = child with { Path = currentPath };
                _nodeCache[currentPath] = current;
            }

            node = current;
            return true;
        }

        private IReadOnlyList<ProjectedNode> GetChildren(ProjectedNode directory)
        {
            return _childrenCache.GetOrAdd(directory.Path, _ =>
            {
                var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var result = new List<ProjectedNode>();
                foreach (var child in _fileSystem.ListDirectory(directory.Node))
                {
                    var projectedName = GetUniqueProjectedName(child.DisplayName, usedNames);
                    var childPath = CombineRelativePath(directory.Path, projectedName);
                    var projected = new ProjectedNode(childPath, projectedName, child);
                    result.Add(projected);
                    _nodeCache[childPath] = projected;
                }

                return result;
            });
        }

        private static bool MatchesFilter(string name, string? filterFileName)
        {
            if (string.IsNullOrWhiteSpace(filterFileName) || filterFileName == "*")
            {
                return true;
            }

            return Utils.DoesNameContainWildCards(filterFileName)
                ? Utils.IsFileNameMatch(name, filterFileName)
                : string.Equals(name, filterFileName, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeRelativePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            return path.Replace('/', '\\').Trim('\\');
        }

        private static string CombineRelativePath(string parent, string name)
        {
            return string.IsNullOrEmpty(parent) ? name : parent + "\\" + name;
        }

        private static FileAttributes GetFileAttributes(VfsNode node)
        {
            return node.IsDirectory ? FileAttributes.Directory : FileAttributes.Archive | FileAttributes.ReadOnly;
        }

        private static DateTime GetTimestamp(VfsNode node)
        {
            return node.ModifiedUtc ?? DateTime.UtcNow;
        }

        private static byte[] CreateProviderId()
        {
            return Encoding.ASCII.GetBytes("Qcow2Explorer");
        }

        private static byte[] CreateContentId(string path, VfsNode node)
        {
            var source = $"{path}|{node.Size}|{node.ModifiedUtc:O}";
            return SHA256.HashData(Encoding.UTF8.GetBytes(source))[..16];
        }

        private static string GetUniqueProjectedName(string sourceName, HashSet<string> usedNames)
        {
            var sanitized = SanitizeFileName(sourceName);
            if (usedNames.Add(sanitized))
            {
                return sanitized;
            }

            var stem = Path.GetFileNameWithoutExtension(sanitized);
            var extension = Path.GetExtension(sanitized);
            for (var i = 2; ; i++)
            {
                var candidate = $"{stem} ({i}){extension}";
                if (usedNames.Add(candidate))
                {
                    return candidate;
                }
            }
        }

        private static string SanitizeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars().ToHashSet();
            var builder = new StringBuilder(name.Length);
            foreach (var ch in name)
            {
                builder.Append(invalid.Contains(ch) || ch < 32 ? '_' : ch);
            }

            var sanitized = builder.ToString().Trim().TrimEnd('.');
            if (string.IsNullOrWhiteSpace(sanitized))
            {
                sanitized = "unnamed";
            }

            return IsReservedWindowsName(sanitized) ? $"_{sanitized}" : sanitized;
        }

        private static bool IsReservedWindowsName(string name)
        {
            var baseName = Path.GetFileNameWithoutExtension(name).ToUpperInvariant();
            return baseName is "CON" or "PRN" or "AUX" or "NUL"
                or "COM1" or "COM2" or "COM3" or "COM4" or "COM5" or "COM6" or "COM7" or "COM8" or "COM9"
                or "LPT1" or "LPT2" or "LPT3" or "LPT4" or "LPT5" or "LPT6" or "LPT7" or "LPT8" or "LPT9";
        }
    }

    private sealed class EnumerationState(string relativePath)
    {
        public string RelativePath { get; } = relativePath;
        public IReadOnlyList<ProjectedNode>? Items { get; set; }
        public int Index { get; set; }
    }

    private sealed record ProjectedNode(string Path, string ProjectedName, VfsNode Node);
}

public sealed class ProjFsUnavailableException : InvalidOperationException
{
    public ProjFsUnavailableException(string message)
        : base(message)
    {
    }
}
