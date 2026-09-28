using Qcow2Explorer.Core;

internal static class PrivateStorageTests
{
    public static void Run(string directory)
    {
        var path = Path.Combine(directory, "private-storage");
        PrivateStorage.CreateDirectory(path);
        Assert(Directory.Exists(path), "private storage directory created");
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        const UnixFileMode nonOwnerPermissions =
            UnixFileMode.GroupRead
            | UnixFileMode.GroupWrite
            | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead
            | UnixFileMode.OtherWrite
            | UnixFileMode.OtherExecute;
        var mode = File.GetUnixFileMode(path);
        Assert((mode & nonOwnerPermissions) == 0, "private storage is inaccessible to other Unix users");
        Assert((mode & UnixFileMode.UserRead) != 0, "private storage remains readable by its owner");
        Assert((mode & UnixFileMode.UserWrite) != 0, "private storage remains writable by its owner");
        Assert((mode & UnixFileMode.UserExecute) != 0, "private storage remains traversable by its owner");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Assertion failed: {message}");
        }
    }
}
