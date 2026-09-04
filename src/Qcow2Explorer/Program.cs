using Qcow2Explorer.Core;

namespace Qcow2Explorer;

static class Program
{
    /// <summary>
    ///  The main entry point for the application.
    /// </summary>
    [STAThread]
    static void Main(string[] args)
    {
        // To customize application configuration such as set high DPI settings or default font,
        // see https://aka.ms/applicationconfiguration.
        ApplicationConfiguration.Initialize();
        DiagnosticLog.Initialize();
        DiagnosticLog.Write($"Application started: build=xfs-extent-read-context-1, baseDirectory={AppContext.BaseDirectory}");
        new DiagnosticLogForm().Show();
        Application.Run(new Form1(args.FirstOrDefault()));
        DiagnosticLog.Dispose();
    }
}
