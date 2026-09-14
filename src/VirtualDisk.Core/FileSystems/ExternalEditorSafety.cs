namespace Qcow2Explorer.FileSystems;

internal static class ExternalEditorSafety
{
    private static readonly HashSet<string> UnsafeAssociatedOpenExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".appimage", ".appref-ms", ".application", ".bash", ".bat", ".chm", ".cmd", ".com", ".command", ".cpl",
        ".csh", ".desktop", ".docm", ".dotm",
        ".exe", ".fish", ".gadget", ".hta", ".htm", ".html", ".inf", ".ins", ".isp", ".jar", ".js",
        ".jse", ".ksh", ".lnk", ".msc", ".msi", ".msp", ".msu", ".pif", ".pl", ".potm", ".ppam",
        ".ppsm", ".pptm", ".ps1", ".ps1xml", ".ps2", ".ps2xml", ".psc1", ".psc2", ".py", ".pyw",
        ".rb", ".reg", ".run", ".scf", ".scr", ".sct", ".sh", ".shb", ".sldm", ".svg", ".svgz",
        ".sys", ".url", ".vb", ".vbe", ".vbs", ".workflow", ".ws", ".wsc", ".wsf", ".wsh",
        ".xlsm", ".xltm", ".zsh",
    };

    public static bool CanOpenWithAssociatedApplication(string path) =>
        !UnsafeAssociatedOpenExtensions.Contains(Path.GetExtension(path));
}
