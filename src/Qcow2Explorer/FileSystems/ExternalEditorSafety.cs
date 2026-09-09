namespace Qcow2Explorer.FileSystems;

internal static class ExternalEditorSafety
{
    private static readonly HashSet<string> UnsafeAssociatedOpenExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".appref-ms", ".application", ".bat", ".chm", ".cmd", ".com", ".cpl",
        ".exe", ".gadget", ".hta", ".inf", ".ins", ".isp", ".jar", ".js",
        ".jse", ".lnk", ".msc", ".msi", ".msp", ".msu", ".pif", ".ps1",
        ".ps1xml", ".ps2", ".ps2xml", ".psc1", ".psc2", ".reg", ".scf",
        ".scr", ".sct", ".shb", ".sys", ".url", ".vb", ".vbe", ".vbs",
        ".ws", ".wsc", ".wsf", ".wsh",
    };

    public static bool CanOpenWithAssociatedApplication(string path) =>
        !UnsafeAssociatedOpenExtensions.Contains(Path.GetExtension(path));
}
