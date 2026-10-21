namespace Qcow2Explorer.Core;

/// <summary>Per-open read access to an image's local companion files. Never persisted globally.</summary>
public sealed class ImageFileAccessPolicy
{
    private readonly string _root;
    private readonly bool _archive;
    private readonly Dictionary<string, string> _selectedFiles = new(PathSemantics.Comparer);
    private readonly HashSet<string> _selectedPaths = new(PathSemantics.Comparer);
    public const int MaximumSelectedFiles = 64;

    private ImageFileAccessPolicy(string root, bool archive)
    {
        _root = NormalizeLocalPath(root);
        RejectLinks(_root);
        _archive = archive;
    }

    public static ImageFileAccessPolicy ForImage(string path)
    {
        var fullPath = NormalizeLocalPath(path);
        RejectLinks(fullPath);
        return new ImageFileAccessPolicy(
            Directory.Exists(fullPath) ? fullPath : Path.GetDirectoryName(fullPath)!, archive: false);
    }

    internal static ImageFileAccessPolicy ForArchive(string directory) => new(directory, archive: true);

    internal string ResolveReference(string directory, string reference)
    {
        // Validate the original reference before combining paths, including Windows-style names on Unix.
        _ = NormalizeLocalPath(reference);
        reference = reference.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        return ResolveFile(Path.IsPathRooted(reference) ? reference : Path.Combine(directory, reference));
    }

    public string ResolveFile(string path)
    {
        var fullPath = NormalizeLocalPath(path);
        if (_selectedFiles.TryGetValue(fullPath, out var selected))
        {
            RejectLinks(selected);
            return selected;
        }
        if (_selectedPaths.Contains(fullPath))
        {
            RejectLinks(fullPath);
            return fullPath;
        }

        var relative = Path.GetRelativePath(_root, fullPath);
        if (Path.IsPathRooted(relative) || relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ImageReferenceAccessException(fullPath, canSelectFile: !_archive,
                _archive
                    ? "OVAの外にあるファイルへの参照を拒否しました。必要な親ディスクや分割ファイルをOVA内に含めてください。"
                    : "イメージのフォルダー外にある関連ファイルは、自動では開きません。必要なファイルを自分で選択してください。");
        }

        RejectLinks(fullPath);
        return fullPath;
    }

    /// <summary>Grants only the file explicitly selected by the user, not its containing directory.</summary>
    public void SelectFile(string requestedPath, string selectedPath)
    {
        if (_archive)
        {
            throw new InvalidOperationException("OVAの展開先外のファイルは許可できません。");
        }

        var request = NormalizeLocalPath(requestedPath);
        var selected = NormalizeLocalPath(selectedPath);
        RejectLinks(selected);
        if (!File.Exists(selected))
        {
            throw new FileNotFoundException("選択した関連ファイルが見つかりません。", selected);
        }

        if (_selectedFiles.Count >= MaximumSelectedFiles || _selectedFiles.ContainsKey(request))
        {
            throw new InvalidDataException("関連ファイルの選択回数が上限に達したか、同じ参照が繰り返されました。");
        }

        _selectedFiles.Add(request, selected);
        _selectedPaths.Add(selected);
    }

    internal static string NormalizeLocalPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        // Check before any existence probe or metadata access: UNC can initiate SMB authentication.
        var portable = path.Replace('\\', '/');
        if (portable.StartsWith("//", StringComparison.Ordinal)
            || portable.StartsWith("/dev/", StringComparison.Ordinal)
            || portable.StartsWith("/proc/", StringComparison.Ordinal)
            || portable.StartsWith("/sys/", StringComparison.Ordinal)
            || portable.IndexOf(':', OperatingSystem.IsWindows() && portable.Length >= 2 && portable[1] == ':' ? 2 : 0) >= 0)
        {
            throw new ImageReferenceAccessException(path, false,
                "ネットワーク、デバイス、または代替データストリームへの参照は許可されていません。");
        }

        if (OperatingSystem.IsWindows() && path.Length >= 2 && path[1] == ':' && !Path.IsPathFullyQualified(path))
        {
            throw new ImageReferenceAccessException(path, false, "ドライブ相対パスへの参照は許可されていません。");
        }

        var fullPath = Path.GetFullPath(path);
        if (fullPath.Replace('\\', '/').StartsWith("//", StringComparison.Ordinal))
        {
            throw new ImageReferenceAccessException(path, false, "ネットワークまたはWindowsデバイスへの参照は許可されていません。");
        }
        if (OperatingSystem.IsWindows())
        {
            if (new DriveInfo(Path.GetPathRoot(fullPath)!).DriveType == DriveType.Network)
            {
                throw new ImageReferenceAccessException(path, false, "ネットワークドライブへの参照は許可されていません。");
            }
            foreach (var component in fullPath[Path.GetPathRoot(fullPath)!.Length..].Split(Path.DirectorySeparatorChar))
            {
                var baseName = component.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
                if (baseName is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
                    || (baseName.Length == 4 && (baseName.StartsWith("COM", StringComparison.Ordinal) || baseName.StartsWith("LPT", StringComparison.Ordinal))
                        && "123456789¹²³".Contains(baseName[3])))
                {
                    throw new ImageReferenceAccessException(path, false, "Windowsデバイス名への参照は許可されていません。");
                }
            }
        }
        return fullPath;
    }

    private static void RejectLinks(string path)
    {
        // Inspect each component, including the selected root: lexical containment alone is insufficient.
        var root = Path.GetPathRoot(path)!;
        var current = root;
        foreach (var component in path[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new ImageReferenceAccessException(path, false,
                        "シンボリックリンクやジャンクションを経由するイメージ参照は許可されていません。実体のファイルを選択してください。");
                }
            }
            catch (FileNotFoundException) { break; }
            catch (DirectoryNotFoundException) { break; }
        }
    }
}

public sealed class ImageReferenceAccessException : IOException
{
    public string ReferencePath { get; }
    public bool CanSelectFile { get; }

    internal ImageReferenceAccessException(string path, bool canSelectFile, string reason)
        : base($"{reason}{Environment.NewLine}{Environment.NewLine}参照先: {path}")
    {
        ReferencePath = path;
        CanSelectFile = canSelectFile;
    }
}
