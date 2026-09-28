namespace Qcow2Explorer.FileSystems;

internal sealed record PendingEditSequenceIssue(int EditIndex, string Message);

internal static class PendingEditSequenceValidator
{
    public static IReadOnlyList<PendingEditSequenceIssue> Validate(
        IReadOnlyFileSystem fileSystem,
        IReadOnlyList<PendingFileEdit> edits)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(edits);
        var state = new PlannedNamespace(fileSystem);
        var issues = new List<PendingEditSequenceIssue>();
        for (var index = 0; index < edits.Count; index++)
        {
            try
            {
                var message = ValidateAndApply(state, edits[index]);
                if (message is not null)
                {
                    issues.Add(new PendingEditSequenceIssue(index, message));
                }
            }
            catch (Exception ex) when (ex is IOException
                                       or InvalidDataException
                                       or UnauthorizedAccessException
                                       or ArgumentException
                                       or NotSupportedException
                                       or OverflowException)
            {
                issues.Add(new PendingEditSequenceIssue(index, $"事前確認できません: {ex.Message}"));
            }
        }

        return issues;
    }

    private static string? ValidateAndApply(PlannedNamespace state, PendingFileEdit edit)
    {
        var path = VirtualPath.Normalize(edit.VirtualPath);
        if (path == "/")
        {
            return "ルートディレクトリ自体は変更できません。";
        }

        if (edit.Operation is FileEditOperationKind.WriteContent or FileEditOperationKind.CreateFile)
        {
            if (string.IsNullOrWhiteSpace(edit.ContentPath) || !File.Exists(edit.ContentPath))
            {
                return "入力内容のファイルが見つかりません。";
            }
        }

        switch (edit.Operation)
        {
            case FileEditOperationKind.WriteContent:
                return RequireKind(state, path, expectDirectory: false, "内容変更対象", out var writeError)
                    ? writeError
                    : null;

            case FileEditOperationKind.CreateFile:
                if (RequireKind(state, VirtualPath.GetParent(path), expectDirectory: true, "追加先", out var error))
                {
                    return error;
                }

                if (state.TryResolve(path, out _))
                {
                    return "同じパスに既存項目または先行する作成予定があります。";
                }

                state.Set(path, new PlannedNode(IsDirectory: false, OriginalPath: null));
                return null;

            case FileEditOperationKind.DeleteFile:
                if (RequireKind(state, path, expectDirectory: false, "削除対象", out error))
                {
                    return error;
                }

                state.Remove(path, includeDescendants: false);
                return null;

            case FileEditOperationKind.CreateDirectory:
                if (RequireKind(state, VirtualPath.GetParent(path), expectDirectory: true, "作成先", out error))
                {
                    return error;
                }

                if (state.TryResolve(path, out _))
                {
                    return "同じパスに既存項目または先行する作成予定があります。";
                }

                state.Set(path, new PlannedNode(IsDirectory: true, OriginalPath: null));
                return null;

            case FileEditOperationKind.DeleteDirectory:
                if (RequireKind(state, path, expectDirectory: true, "削除対象", out error))
                {
                    return error;
                }

                if (state.HasChildren(path))
                {
                    return "空ではないディレクトリは削除できません。先に内容を削除または移動してください。";
                }

                state.Remove(path, includeDescendants: true);
                return null;

            case FileEditOperationKind.MoveEntry:
                var destination = edit.DestinationVirtualPath is null
                    ? null
                    : VirtualPath.Normalize(edit.DestinationVirtualPath);
                if (destination is null || destination == "/")
                {
                    return "移動先パスが不正です。";
                }

                if (!state.TryResolve(path, out var source))
                {
                    return "移動元が存在しません。先行する削除・移動との順序を確認してください。";
                }

                if (state.PathsEqual(path, destination))
                {
                    return "移動元と移動先が同じです。";
                }

                if (source.IsDirectory && state.IsDescendant(destination, path))
                {
                    return "ディレクトリ自身の配下へは移動できません。";
                }

                if (RequireKind(state, VirtualPath.GetParent(destination), expectDirectory: true, "移動先", out error))
                {
                    return error;
                }

                if (state.TryResolve(destination, out _))
                {
                    return "移動先には既存項目または先行する作成予定があります。";
                }

                state.Move(path, destination, source);
                return null;

            case FileEditOperationKind.SetAttributes:
                if (!edit.Attributes.HasValue)
                {
                    return "設定する属性がありません。";
                }

                return state.TryResolve(path, out _)
                    ? null
                    : "属性変更対象が存在しません。先行する削除・移動との順序を確認してください。";

            case FileEditOperationKind.SetLastWriteTimeUtc:
                if (!edit.ModifiedUtc.HasValue)
                {
                    return "設定する更新日時がありません。";
                }

                return state.TryResolve(path, out _)
                    ? null
                    : "更新日時変更対象が存在しません。先行する削除・移動との順序を確認してください。";

            default:
                return "未対応の変更操作です。";
        }
    }

    private static bool RequireKind(
        PlannedNamespace state,
        string path,
        bool expectDirectory,
        string label,
        out string error)
    {
        if (!state.TryResolve(path, out var node))
        {
            error = $"{label}が存在しません。先行する作成・削除・移動との順序を確認してください。";
            return true;
        }

        if (node.IsDirectory != expectDirectory)
        {
            error = expectDirectory ? $"{label}はディレクトリではありません。" : $"{label}は通常ファイルではありません。";
            return true;
        }

        error = string.Empty;
        return false;
    }

    private sealed record PlannedNode(bool IsDirectory, string? OriginalPath);

    private sealed class PlannedNamespace
    {
        private readonly IReadOnlyFileSystem _fileSystem;
        private readonly Dictionary<string, PlannedNode?> _overrides;
        private readonly StringComparison _comparison;

        public PlannedNamespace(IReadOnlyFileSystem fileSystem)
        {
            _fileSystem = fileSystem;
            var caseInsensitive = fileSystem.Name is "FAT16" or "FAT32" or "exFAT" or "NTFS";
            _comparison = caseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            _overrides = new Dictionary<string, PlannedNode?>(
                caseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        }

        public bool PathsEqual(string left, string right) =>
            string.Equals(VirtualPath.Normalize(left), VirtualPath.Normalize(right), _comparison);

        public bool IsDescendant(string candidate, string parent)
        {
            candidate = VirtualPath.Normalize(candidate);
            parent = VirtualPath.Normalize(parent);
            var prefix = parent == "/" ? "/" : parent + "/";
            return candidate.StartsWith(prefix, _comparison) && !PathsEqual(candidate, parent);
        }

        public bool TryResolve(string path, out PlannedNode node)
        {
            path = VirtualPath.Normalize(path);
            if (path == "/")
            {
                node = new PlannedNode(IsDirectory: true, OriginalPath: "/");
                return true;
            }

            var candidate = path;
            while (candidate != "/")
            {
                if (_overrides.TryGetValue(candidate, out var planned))
                {
                    if (planned is null)
                    {
                        node = null!;
                        return false;
                    }

                    if (PathsEqual(candidate, path))
                    {
                        node = planned;
                        return true;
                    }

                    if (!planned.IsDirectory || planned.OriginalPath is null)
                    {
                        node = null!;
                        return false;
                    }

                    var suffix = path[(candidate.Length + 1)..];
                    return TryResolveOriginal(VirtualPath.Combine(planned.OriginalPath, suffix), out node);
                }

                candidate = VirtualPath.GetParent(candidate);
            }

            return TryResolveOriginal(path, out node);
        }

        public void Set(string path, PlannedNode node)
        {
            _overrides[VirtualPath.Normalize(path)] = node;
        }

        public void Remove(string path, bool includeDescendants)
        {
            path = VirtualPath.Normalize(path);
            if (includeDescendants)
            {
                foreach (var descendant in _overrides.Keys.Where(candidate => IsDescendant(candidate, path)).ToArray())
                {
                    _overrides.Remove(descendant);
                }
            }

            _overrides[path] = null;
        }

        public void Move(string sourcePath, string destinationPath, PlannedNode source)
        {
            sourcePath = VirtualPath.Normalize(sourcePath);
            destinationPath = VirtualPath.Normalize(destinationPath);
            var descendants = _overrides
                .Where(item => IsDescendant(item.Key, sourcePath))
                .Select(item => new KeyValuePair<string, PlannedNode?>(item.Key, item.Value))
                .ToArray();
            foreach (var descendant in descendants)
            {
                _overrides.Remove(descendant.Key);
                var suffix = descendant.Key[(sourcePath.Length + 1)..];
                _overrides[VirtualPath.Combine(destinationPath, suffix)] = descendant.Value;
            }

            _overrides[sourcePath] = null;
            _overrides[destinationPath] = source;
        }

        public bool HasChildren(string directoryPath)
        {
            directoryPath = VirtualPath.Normalize(directoryPath);
            if (!TryResolve(directoryPath, out var directory) || !directory.IsDirectory)
            {
                return false;
            }

            if (_overrides.Any(item => item.Value is not null
                && PathsEqual(VirtualPath.GetParent(item.Key), directoryPath)))
            {
                return true;
            }

            if (directory.OriginalPath is null
                || !FileEditService.TryResolvePath(_fileSystem, directory.OriginalPath, out var originalDirectory))
            {
                return false;
            }

            foreach (var child in _fileSystem.ListDirectory(originalDirectory))
            {
                if (TryResolve(VirtualPath.Combine(directoryPath, child.Name), out _))
                {
                    return true;
                }
            }

            return false;
        }

        private bool TryResolveOriginal(string path, out PlannedNode node)
        {
            path = VirtualPath.Normalize(path);
            if (FileEditService.TryResolvePath(_fileSystem, path, out var original))
            {
                node = new PlannedNode(original.IsDirectory, path);
                return true;
            }

            node = null!;
            return false;
        }
    }
}
