using System.ComponentModel;

namespace Qcow2Explorer.Shell;

internal sealed class FileAssociationDialog : Form
{
    private readonly RadioButton _currentUserRadio = new()
    {
        Text = "現在のユーザーのみ",
        AutoSize = true,
        Checked = true,
    };
    private readonly RadioButton _allUsersRadio = new()
    {
        Text = "このPCのすべてのユーザー（管理者権限が必要）",
        AutoSize = true,
    };
    private readonly CheckedListBox _extensionList = new()
    {
        Dock = DockStyle.Fill,
        CheckOnClick = true,
        IntegralHeight = false,
    };
    private readonly Label _statusLabel = new()
    {
        Dock = DockStyle.Fill,
        AutoEllipsis = true,
        TextAlign = ContentAlignment.MiddleLeft,
    };
    private readonly Button _applyButton = new()
    {
        Text = "選択内容を登録",
        AutoSize = true,
    };

    internal FileAssociationDialog()
    {
        Text = "ファイルの関連付け";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(620, 560);
        Size = new Size(680, 640);
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;

        BuildUi();
        PopulateExtensions();
        Shown += (_, _) => LoadRegisteredExtensions();
        _currentUserRadio.CheckedChanged += (_, _) =>
        {
            if (_currentUserRadio.Checked)
            {
                LoadRegisteredExtensions();
            }
        };
        _allUsersRadio.CheckedChanged += (_, _) =>
        {
            if (_allUsersRadio.Checked)
            {
                LoadRegisteredExtensions();
            }
        };
    }

    private FileAssociationScope SelectedScope => _allUsersRadio.Checked
        ? FileAssociationScope.AllUsers
        : FileAssociationScope.CurrentUser;

    private void BuildUi()
    {
        var introduction = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            Text = "このアプリで開ける拡張子を選択してください。登録後、既定アプリの最終選択はWindows設定で行います。",
            Padding = new Padding(4, 2, 4, 6),
        };

        var scopePanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(8, 4, 8, 4),
        };
        scopePanel.Controls.Add(_currentUserRadio);
        scopePanel.Controls.Add(_allUsersRadio);
        var scopeGroup = new GroupBox
        {
            Text = "登録範囲",
            Dock = DockStyle.Fill,
            Controls = { scopePanel },
        };

        var selectAllButton = new Button { Text = "すべて選択", AutoSize = true };
        selectAllButton.Click += (_, _) => SetAllChecked(true);
        var clearAllButton = new Button { Text = "すべて解除", AutoSize = true };
        clearAllButton.Click += (_, _) => SetAllChecked(false);
        var selectionButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = false,
        };
        selectionButtons.Controls.Add(selectAllButton);
        selectionButtons.Controls.Add(clearAllButton);

        _applyButton.Click += async (_, _) => await ApplyAsync();
        var settingsButton = new Button { Text = "Windowsの既定アプリ設定", AutoSize = true };
        settingsButton.Click += (_, _) => OpenDefaultAppsSettings();
        var closeButton = new Button { Text = "閉じる", AutoSize = true, DialogResult = DialogResult.Cancel };
        var actionButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
        };
        actionButtons.Controls.Add(closeButton);
        actionButtons.Controls.Add(settingsButton);
        actionButtons.Controls.Add(_applyButton);

        var note = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Padding = new Padding(4, 4, 4, 0),
            Text = "※ 全ユーザー登録は、このアプリを選択肢としてPC全体へ登録します。既定アプリは各ユーザーが個別に選択します。\r\n"
                + "※ アプリを別の場所へ移動した場合は、移動後にもう一度登録してください。",
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 7,
            Padding = new Padding(12),
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 88));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(introduction, 0, 0);
        layout.Controls.Add(scopeGroup, 0, 1);
        layout.Controls.Add(selectionButtons, 0, 2);
        layout.Controls.Add(_extensionList, 0, 3);
        layout.Controls.Add(_statusLabel, 0, 4);
        layout.Controls.Add(note, 0, 5);
        layout.Controls.Add(actionButtons, 0, 6);
        Controls.Add(layout);

        AcceptButton = _applyButton;
        CancelButton = closeButton;
    }

    private void PopulateExtensions()
    {
        foreach (var association in FileAssociationManager.SupportedExtensions)
        {
            _extensionList.Items.Add(
                new ExtensionListItem(association.Extension, association.Description),
                isChecked: false);
        }
    }

    private void LoadRegisteredExtensions()
    {
        try
        {
            var registered = FileAssociationManager.GetRegisteredExtensions(SelectedScope);
            for (var index = 0; index < _extensionList.Items.Count; index++)
            {
                var item = (ExtensionListItem)_extensionList.Items[index];
                _extensionList.SetItemChecked(index, registered.Contains(item.Extension));
            }

            _statusLabel.Text = registered.Count == 0
                ? "この範囲にはまだ登録されていません。"
                : $"この範囲で {registered.Count} 個の拡張子が登録されています。";
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "登録状態を取得できませんでした。";
            MessageBox.Show(this, ex.Message, "ファイルの関連付け", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SetAllChecked(bool value)
    {
        for (var index = 0; index < _extensionList.Items.Count; index++)
        {
            _extensionList.SetItemChecked(index, value);
        }
    }

    private IReadOnlyList<string> GetSelectedExtensions()
    {
        return _extensionList.CheckedItems
            .Cast<ExtensionListItem>()
            .Select(item => item.Extension)
            .ToArray();
    }

    private async Task ApplyAsync()
    {
        var selected = GetSelectedExtensions();
        _applyButton.Enabled = false;
        UseWaitCursor = true;
        try
        {
            if (SelectedScope == FileAssociationScope.CurrentUser || FileAssociationManager.IsProcessElevated())
            {
                FileAssociationManager.Apply(SelectedScope, selected);
            }
            else
            {
                int exitCode;
                try
                {
                    exitCode = await Task.Run(() => FileAssociationManager.ApplyForAllUsersElevated(selected));
                }
                catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
                {
                    _statusLabel.Text = "管理者権限の要求がキャンセルされました。";
                    return;
                }

                if (exitCode != 0)
                {
                    throw new InvalidOperationException($"全ユーザー向け登録に失敗しました（終了コード {exitCode}）。");
                }
            }

            LoadRegisteredExtensions();
            if (selected.Count == 0)
            {
                MessageBox.Show(
                    this,
                    "この範囲からVirtual Disk Explorerの登録を解除しました。",
                    "ファイルの関連付け",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            var result = MessageBox.Show(
                this,
                $"{selected.Count} 個の拡張子を登録しました。\r\n\r\n"
                    + "このアプリを既定にする拡張子は、Windows設定で選択してください。今すぐ開きますか？",
                "ファイルの関連付け",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Information);
            if (result == DialogResult.Yes)
            {
                OpenDefaultAppsSettings();
            }
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "登録に失敗しました。";
            MessageBox.Show(this, ex.Message, "ファイルの関連付け", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            UseWaitCursor = false;
            _applyButton.Enabled = true;
        }
    }

    private void OpenDefaultAppsSettings()
    {
        try
        {
            FileAssociationManager.OpenWindowsDefaultApps(SelectedScope);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Windowsの既定アプリ設定", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private sealed record ExtensionListItem(string Extension, string Description)
    {
        public override string ToString() => $"{Extension,-8}  {Description}";
    }
}
