using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace Qcow2Explorer.Shell;

internal enum FileAssociationScope
{
    CurrentUser,
    AllUsers
}

internal sealed record FileAssociationDefinition(string Extension, string Description);

internal static class FileAssociationManager
{
    internal const string RegisteredApplicationName = "Virtual Disk Explorer";
    internal const string ApplyMachineArgument = "--apply-machine-file-associations";

    private const string ProgId = "KashimashutoPixel.VirtualDiskTools.DiskImage.1";
    private const string CapabilitiesPath = @"Software\KashimashutoPixel\VirtualDiskTools\Capabilities";
    private const string RegisteredApplicationsPath = @"Software\RegisteredApplications";
    private const string ClassesPath = @"Software\Classes";
    private const uint ShcneAssocChanged = 0x08000000;
    private const uint ShcnfIdList = 0x0000;

    internal static IReadOnlyList<FileAssociationDefinition> SupportedExtensions { get; } =
    [
        new(".qcow2", "QEMU Copy-On-Write 2"),
        new(".qcow", "QEMU Copy-On-Write"),
        new(".vhd", "Virtual Hard Disk"),
        new(".vhdx", "Virtual Hard Disk v2"),
        new(".vmdk", "VMware Virtual Disk"),
        new(".vdi", "VirtualBox Disk Image"),
        new(".ova", "Open Virtual Appliance"),
        new(".hdd", "Parallels HDD（ファイルのみ）"),
        new(".hds", "Parallels Disk Image"),
        new(".vma", "Proxmox VMA"),
        new(".dd", "RAW / DD Disk Image"),
        new(".img", "Disk Image"),
        new(".raw", "RAW Disk Image"),
        new(".lzo", "LZO-compressed Disk Image"),
        new(".e01", "Expert Witness Format"),
    ];

    internal static IReadOnlyList<string> NormalizeExtensions(IEnumerable<string> extensions)
    {
        var requested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var extension in extensions)
        {
            if (!SupportedExtensions.Any(item =>
                    string.Equals(item.Extension, extension, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException($"未対応の拡張子です: {extension}", nameof(extensions));
            }

            requested.Add(extension);
        }

        return SupportedExtensions
            .Where(item => requested.Contains(item.Extension))
            .Select(item => item.Extension)
            .ToArray();
    }

    internal static IReadOnlySet<string> GetRegisteredExtensions(FileAssociationScope scope)
    {
        EnsureWindows();
        using var root = OpenRoot(scope);
        using var associations = root.OpenSubKey($@"{CapabilitiesPath}\FileAssociations", writable: false);
        var registered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (associations is null)
        {
            return registered;
        }

        foreach (var item in SupportedExtensions)
        {
            if (string.Equals(associations.GetValue(item.Extension) as string, ProgId, StringComparison.Ordinal))
            {
                registered.Add(item.Extension);
            }
        }

        return registered;
    }

    internal static void Apply(
        FileAssociationScope scope,
        IEnumerable<string> selectedExtensions,
        string? executablePath = null)
    {
        EnsureWindows();
        var selected = NormalizeExtensions(selectedExtensions);
        var selectedSet = selected.ToHashSet(StringComparer.OrdinalIgnoreCase);
        executablePath = Path.GetFullPath(executablePath ?? GetExecutablePath());

        using var root = OpenRoot(scope);
        RemoveExtensionClaims(root, selectedSet);

        if (selected.Count == 0)
        {
            RemoveApplicationRegistration(root, executablePath);
            NotifyShellAssociationChanged();
            return;
        }

        WriteProgId(root, executablePath);
        WriteApplicationRegistration(root, executablePath, selected);
        WriteCapabilities(root, executablePath, selected);
        NotifyShellAssociationChanged();
    }

    internal static bool IsProcessElevated()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    internal static int ApplyForAllUsersElevated(IEnumerable<string> selectedExtensions)
    {
        EnsureWindows();
        var selected = NormalizeExtensions(selectedExtensions);
        var executablePath = GetExecutablePath();
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = AppContext.BaseDirectory,
        };
        startInfo.ArgumentList.Add(ApplyMachineArgument);
        foreach (var extension in selected)
        {
            startInfo.ArgumentList.Add(extension);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("管理者権限のプロセスを開始できませんでした。");
        process.WaitForExit();
        return process.ExitCode;
    }

    internal static void OpenWindowsDefaultApps(FileAssociationScope scope)
    {
        EnsureWindows();
        var parameter = scope == FileAssociationScope.CurrentUser
            ? "registeredAppUser"
            : "registeredAppMachine";
        var uri = $"ms-settings:defaultapps?{parameter}={Uri.EscapeDataString(RegisteredApplicationName)}";
        Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
    }

    internal static string GetExecutablePath()
    {
        var entryAssemblyName = Assembly.GetEntryAssembly()?.GetName().Name;
        if (!string.IsNullOrWhiteSpace(entryAssemblyName))
        {
            var appHostPath = Path.Combine(AppContext.BaseDirectory, $"{entryAssemblyName}.exe");
            if (File.Exists(appHostPath))
            {
                return Path.GetFullPath(appHostPath);
            }
        }

        return Environment.ProcessPath
            ?? throw new InvalidOperationException("実行ファイルのパスを取得できませんでした。");
    }

    private static RegistryKey OpenRoot(FileAssociationScope scope)
    {
        var hive = scope == FileAssociationScope.CurrentUser
            ? RegistryHive.CurrentUser
            : RegistryHive.LocalMachine;
        var view = Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Registry32;
        return RegistryKey.OpenBaseKey(hive, view);
    }

    private static void RemoveExtensionClaims(RegistryKey root, IReadOnlySet<string> selected)
    {
        foreach (var item in SupportedExtensions)
        {
            var extensionPath = $@"{ClassesPath}\{item.Extension}";
            if (selected.Contains(item.Extension))
            {
                using var openWith = root.CreateSubKey($@"{extensionPath}\OpenWithProgids", writable: true);
                openWith.SetValue(ProgId, string.Empty, RegistryValueKind.String);
                continue;
            }

            using var existingOpenWith = root.OpenSubKey($@"{extensionPath}\OpenWithProgids", writable: true);
            existingOpenWith?.DeleteValue(ProgId, throwOnMissingValue: false);
        }
    }

    private static void WriteProgId(RegistryKey root, string executablePath)
    {
        using var progId = root.CreateSubKey($@"{ClassesPath}\{ProgId}", writable: true);
        progId.SetValue(null, "Virtual Disk Image", RegistryValueKind.String);

        using var icon = progId.CreateSubKey("DefaultIcon", writable: true);
        icon.SetValue(null, $"\"{executablePath}\",0", RegistryValueKind.String);

        using var command = progId.CreateSubKey(@"shell\open\command", writable: true);
        command.SetValue(null, $"\"{executablePath}\" \"%1\"", RegistryValueKind.String);
    }

    private static void WriteApplicationRegistration(
        RegistryKey root,
        string executablePath,
        IReadOnlyList<string> selected)
    {
        var executableName = Path.GetFileName(executablePath);
        var applicationPath = $@"{ClassesPath}\Applications\{executableName}";
        using var application = root.CreateSubKey(applicationPath, writable: true);
        application.SetValue("FriendlyAppName", RegisteredApplicationName, RegistryValueKind.String);

        using var command = application.CreateSubKey(@"shell\open\command", writable: true);
        command.SetValue(null, $"\"{executablePath}\" \"%1\"", RegistryValueKind.String);

        using var supportedTypes = application.CreateSubKey("SupportedTypes", writable: true);
        foreach (var item in SupportedExtensions)
        {
            supportedTypes.DeleteValue(item.Extension, throwOnMissingValue: false);
        }

        foreach (var extension in selected)
        {
            supportedTypes.SetValue(extension, string.Empty, RegistryValueKind.String);
        }
    }

    private static void WriteCapabilities(
        RegistryKey root,
        string executablePath,
        IReadOnlyList<string> selected)
    {
        using var capabilities = root.CreateSubKey(CapabilitiesPath, writable: true);
        capabilities.SetValue("ApplicationName", RegisteredApplicationName, RegistryValueKind.String);
        capabilities.SetValue(
            "ApplicationDescription",
            "仮想ディスクイメージを読み取り専用で解析・参照します。",
            RegistryValueKind.String);
        capabilities.SetValue("ApplicationIcon", $"\"{executablePath}\",0", RegistryValueKind.String);

        using var associations = capabilities.CreateSubKey("FileAssociations", writable: true);
        foreach (var item in SupportedExtensions)
        {
            associations.DeleteValue(item.Extension, throwOnMissingValue: false);
        }

        foreach (var extension in selected)
        {
            associations.SetValue(extension, ProgId, RegistryValueKind.String);
        }

        using var registeredApplications = root.CreateSubKey(RegisteredApplicationsPath, writable: true);
        registeredApplications.SetValue(RegisteredApplicationName, CapabilitiesPath, RegistryValueKind.String);
    }

    private static void RemoveApplicationRegistration(RegistryKey root, string executablePath)
    {
        using (var registeredApplications = root.OpenSubKey(RegisteredApplicationsPath, writable: true))
        {
            registeredApplications?.DeleteValue(RegisteredApplicationName, throwOnMissingValue: false);
        }

        root.DeleteSubKeyTree(CapabilitiesPath, throwOnMissingSubKey: false);
        root.DeleteSubKeyTree($@"{ClassesPath}\{ProgId}", throwOnMissingSubKey: false);
        root.DeleteSubKeyTree(
            $@"{ClassesPath}\Applications\{Path.GetFileName(executablePath)}",
            throwOnMissingSubKey: false);
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("ファイルの関連付けは Windows でのみ利用できます。");
        }
    }

    private static void NotifyShellAssociationChanged()
    {
        SHChangeNotify(ShcneAssocChanged, ShcnfIdList, IntPtr.Zero, IntPtr.Zero);
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
}
