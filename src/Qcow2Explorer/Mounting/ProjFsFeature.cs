using System.ComponentModel;
using System.Diagnostics;

namespace Qcow2Explorer.Mounting;

public static class ProjFsFeature
{
    public static bool IsLibraryPresent => ProjectedFileSystemMount.IsProjFsLibraryPresent();

    public static bool PromptAndEnable(IWin32Window owner)
    {
        var answer = MessageBox.Show(
            owner,
            "ProjFS が有効ではない可能性があります。Windows の Client-ProjFS 機能を管理者権限で有効化しますか？",
            "ProjFS の有効化",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);
        if (answer != DialogResult.Yes)
        {
            return false;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -Command \"Enable-WindowsOptionalFeature -Online -FeatureName Client-ProjFS -All -NoRestart\"",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Normal
            });

            if (process is null)
            {
                MessageBox.Show(owner, "有効化コマンドを起動できませんでした。", "ProjFS", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            process.WaitForExit();
            if (IsLibraryPresent)
            {
                MessageBox.Show(owner, "ProjFS を確認できました。もう一度マウントを実行してください。", "ProjFS", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return true;
            }

            MessageBox.Show(
                owner,
                "有効化コマンドは終了しましたが、ProjFS ライブラリをまだ確認できません。再起動が必要な場合があります。",
                "ProjFS",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return false;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            MessageBox.Show(owner, "UAC 昇格がキャンセルされました。", "ProjFS", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, ex.Message, "ProjFS 有効化エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }
}
