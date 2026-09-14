using Qcow2Explorer.FileSystems;

internal static class ExternalEditorSafetyTests
{
    public static void Run()
    {
        Assert(
            ExternalEditorSafety.CanOpenWithAssociatedApplication("notes.txt"),
            "associated text editor remains available");
        Assert(
            !ExternalEditorSafety.CanOpenWithAssociatedApplication("setup.desktop"),
            "Linux desktop launcher is blocked");
        Assert(
            !ExternalEditorSafety.CanOpenWithAssociatedApplication("script.sh"),
            "Unix shell script is blocked");
        Assert(
            !ExternalEditorSafety.CanOpenWithAssociatedApplication("script.COMMAND"),
            "macOS command script is blocked case-insensitively");
        Assert(
            !ExternalEditorSafety.CanOpenWithAssociatedApplication("installer.run"),
            "Unix self-extracting executable is blocked");
        Assert(
            !ExternalEditorSafety.CanOpenWithAssociatedApplication("payload.py"),
            "interpreter-associated script is blocked");
        Assert(
            !ExternalEditorSafety.CanOpenWithAssociatedApplication("report.xlsm"),
            "macro-enabled document is blocked");
        Assert(
            !ExternalEditorSafety.CanOpenWithAssociatedApplication("page.html"),
            "active web content is blocked");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Assertion failed: {message}");
        }
    }
}
