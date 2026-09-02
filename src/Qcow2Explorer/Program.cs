namespace Qcow2Explorer;

using Qcow2Explorer.Shell;

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

        if (args.Length > 0
            && string.Equals(args[0], FileAssociationManager.ApplyMachineArgument, StringComparison.Ordinal))
        {
            try
            {
                FileAssociationManager.Apply(FileAssociationScope.AllUsers, args.Skip(1));
                Environment.ExitCode = 0;
            }
            catch (Exception ex)
            {
                Environment.ExitCode = 1;
                MessageBox.Show(
                    ex.Message,
                    "全ユーザー向けファイル関連付け",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }

            return;
        }

        Application.Run(new Form1(args.FirstOrDefault()));
    }
}
