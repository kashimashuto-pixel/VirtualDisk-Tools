using Qcow2Explorer.Core;
using Qcow2Explorer.FileSystems;

namespace Qcow2Explorer;

internal sealed class PhysicalDiskWriteConfirmationDialog : Form
{
    private readonly PhysicalDiskTargetInfo _target;
    private readonly bool _isRecovery;
    private readonly TextBox _confirmationInput = new();
    private readonly CheckBox _targetAcknowledgement = new();
    private readonly CheckBox _backupAcknowledgement = new();
    private readonly Button _proceedButton = new();
    private readonly TextBox? _journalPathInput;

    public PhysicalDiskWriteConfirmationDialog(
        PhysicalDiskTargetInfo target,
        string recoveryJournalPath,
        IReadOnlyList<PendingFileEdit>? edits = null,
        bool isRecovery = false)
    {
        _target = target;
        _isRecovery = isRecovery;
        Text = isRecovery ? "物理ディスクを復旧" : "物理ディスクへ変更を適用";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(720, isRecovery ? 510 : 590);

        var warning = new Label
        {
            Dock = DockStyle.Top,
            Height = 64,
            ForeColor = Color.DarkRed,
            Font = new Font(Font, FontStyle.Bold),
            Text = isRecovery
                ? "復旧ジャーナルの変更前セクターを物理ディスクへ直接書き戻します。対象を誤るとデータを破壊します。"
                : "物理ディスクへ直接書き込みます。途中失敗時は自動復旧しますが、停電や機器故障では完全復旧できない場合があります。",
        };

        var targetText = new TextBox
        {
            Dock = DockStyle.Top,
            Height = 142,
            Multiline = true,
            ReadOnly = true,
            BackColor = SystemColors.Window,
            Text = FormatTarget(target),
        };

        var editText = new TextBox
        {
            Dock = DockStyle.Top,
            Height = isRecovery ? 48 : 104,
            Multiline = true,
            ReadOnly = true,
            BackColor = SystemColors.Window,
            ScrollBars = ScrollBars.Vertical,
            Text = isRecovery
                ? $"復旧ジャーナル:\r\n{recoveryJournalPath}"
                : FormatEdits(edits ?? []),
        };

        Control journalControl;
        if (isRecovery)
        {
            journalControl = new Label
            {
                Dock = DockStyle.Top,
                Height = 34,
                Text = "復旧前に対象ディスクの識別情報と全対象セクターを再検証します。",
            };
        }
        else
        {
            _journalPathInput = new TextBox
            {
                Dock = DockStyle.Fill,
                Text = recoveryJournalPath,
            };
            var browse = new Button { Text = "参照...", AutoSize = true };
            browse.Click += (_, _) => BrowseJournalPath();
            var journalLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 62,
                ColumnCount = 2,
                RowCount = 2,
            };
            journalLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            journalLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            journalLayout.Controls.Add(new Label
            {
                Text = "対象ディスクとは別のローカルドライブに保存する復旧ジャーナル",
                AutoSize = true,
            }, 0, 0);
            journalLayout.SetColumnSpan(journalLayout.GetControlFromPosition(0, 0)!, 2);
            journalLayout.Controls.Add(_journalPathInput, 0, 1);
            journalLayout.Controls.Add(browse, 1, 1);
            journalControl = journalLayout;
        }

        _targetAcknowledgement.Text = "表示されたディスク番号・型番・容量・シリアル番号が対象と一致しています。";
        _targetAcknowledgement.AutoSize = true;
        _targetAcknowledgement.CheckedChanged += (_, _) => UpdateProceedState();
        _backupAcknowledgement.Text = "必要なバックアップを取得済みで、対象ボリュームが強制的にアンマウントされることを了承します。";
        _backupAcknowledgement.AutoSize = true;
        _backupAcknowledgement.CheckedChanged += (_, _) => UpdateProceedState();

        var phraseLabel = new Label
        {
            AutoSize = true,
            Text = $"続行するには次を正確に入力してください:  {_target.ConfirmationText}",
        };
        _confirmationInput.Width = 660;
        _confirmationInput.TextChanged += (_, _) => UpdateProceedState();

        var confirmationLayout = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 106,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
        };
        confirmationLayout.Controls.Add(_targetAcknowledgement);
        confirmationLayout.Controls.Add(_backupAcknowledgement);
        confirmationLayout.Controls.Add(phraseLabel);
        confirmationLayout.Controls.Add(_confirmationInput);

        _proceedButton.Text = isRecovery ? "復旧を開始" : "書き込みを開始";
        _proceedButton.AutoSize = true;
        _proceedButton.Enabled = false;
        _proceedButton.DialogResult = DialogResult.OK;
        var cancelButton = new Button
        {
            Text = "キャンセル",
            AutoSize = true,
            DialogResult = DialogResult.Cancel,
        };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 42,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
        };
        buttons.Controls.Add(cancelButton);
        buttons.Controls.Add(_proceedButton);

        var content = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12) };
        content.Controls.Add(confirmationLayout);
        content.Controls.Add(journalControl);
        content.Controls.Add(editText);
        content.Controls.Add(targetText);
        content.Controls.Add(warning);
        Controls.Add(content);
        Controls.Add(buttons);
        AcceptButton = _proceedButton;
        CancelButton = cancelButton;
        Shown += (_, _) => _confirmationInput.Focus();
    }

    public string ConfirmationText => _confirmationInput.Text;
    public string RecoveryJournalPath => _journalPathInput?.Text.Trim() ?? string.Empty;

    private void UpdateProceedState()
    {
        _proceedButton.Enabled = _targetAcknowledgement.Checked
            && _backupAcknowledgement.Checked
            && string.Equals(_confirmationInput.Text, _target.ConfirmationText, StringComparison.Ordinal)
            && (_isRecovery || !string.IsNullOrWhiteSpace(_journalPathInput?.Text));
    }

    private void BrowseJournalPath()
    {
        using var dialog = new SaveFileDialog
        {
            Title = "物理ディスク復旧ジャーナルの保存先",
            Filter = "Virtual Disk Tools recovery journal (*.vdt-recovery)|*.vdt-recovery|All files (*.*)|*.*",
            DefaultExt = "vdt-recovery",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = Path.GetFileName(_journalPathInput!.Text),
            InitialDirectory = Path.GetDirectoryName(_journalPathInput.Text),
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _journalPathInput.Text = dialog.FileName;
            UpdateProceedState();
        }
    }

    private static string FormatTarget(PhysicalDiskTargetInfo target) =>
        $"デバイス: {target.DevicePath}\r\n"
        + $"種類: {(target.IsRemovable ? "リムーバブル／ホットプラグ" : "固定ディスク")}\r\n"
        + $"型番: {(string.IsNullOrWhiteSpace(target.Model) ? "取得できません" : target.Model)}\r\n"
        + $"シリアル番号: {(string.IsNullOrWhiteSpace(target.SerialNumber) ? "取得できません" : target.SerialNumber)}\r\n"
        + $"デバイス識別値: {(string.IsNullOrWhiteSpace(target.IdentityToken) ? "取得できません" : target.IdentityToken[..Math.Min(16, target.IdentityToken.Length)])}\r\n"
        + $"接続: {target.BusType}\r\n"
        + $"容量: {FormatBytes(target.Length)} ({target.Length:N0} bytes)\r\n"
        + $"論理セクター: {target.LogicalSectorSize:N0} bytes";

    private static string FormatEdits(IReadOnlyList<PendingFileEdit> edits)
    {
        var text = string.Join("\r\n", edits.Take(8).Select(edit => $"・{edit.Operation}: {edit.VirtualPath}"));
        return edits.Count <= 8 ? text : $"{text}\r\n・ほか {edits.Count - 8:N0}件";
    }

    private static string FormatBytes(long value)
    {
        string[] suffixes = ["B", "KB", "MB", "GB", "TB", "PB"];
        double size = value;
        var suffix = 0;
        while (size >= 1024 && suffix < suffixes.Length - 1)
        {
            size /= 1024;
            suffix++;
        }

        return $"{size:0.##} {suffixes[suffix]}";
    }
}
