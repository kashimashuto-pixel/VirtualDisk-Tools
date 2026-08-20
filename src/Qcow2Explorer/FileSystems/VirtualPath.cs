namespace Qcow2Explorer.FileSystems;

internal static class VirtualPath
{
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == "/")
        {
            return "/";
        }

        return "/" + string.Join('/', Split(path));
    }

    public static string Combine(string directoryPath, string name)
    {
        var normalizedDirectory = Normalize(directoryPath);
        return normalizedDirectory == "/" ? $"/{name}" : $"{normalizedDirectory}/{name}";
    }

    public static string GetParent(string path)
    {
        var segments = Split(path);
        return segments.Length <= 1 ? "/" : "/" + string.Join('/', segments[..^1]);
    }

    public static string[] Split(string path)
    {
        return path.Split('/', StringSplitOptions.RemoveEmptyEntries);
    }
}
