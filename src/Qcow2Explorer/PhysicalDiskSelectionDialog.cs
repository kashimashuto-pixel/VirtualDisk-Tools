using Qcow2Explorer.Core;

namespace Qcow2Explorer;

internal sealed class PhysicalDiskSelectionDialog : Form
{
    private readonly ListBox _diskList = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly Button _openButton = new() { Text = "読み取り専用で開く", AutoSize = true, Enabled = false };

    public PhysicalDiskSelectionDialog(IReadOnlyList<PhysicalDiskInfo> disks)
    {
        Text = "物理ディスクを開く";
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(680, 310);
        MinimumSize = new Size(560, 280);

        foreach (var disk in disks)
        {
            _diskList.Items.Add(disk);
        }

        _diskList.SelectedIndexChanged += (_, _) => _openButton.Enabled = _diskList.SelectedItem is PhysicalDiskInfo;
        _diskList.DoubleClick += (_, _) => AcceptSelection();
        _openButton.Click += (_, _) => AcceptSelection();

        var warning = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(640, 0),
            Text = "選択した物理ディスクを直接読み取ります。書き込みは行いません。使用中のディスクは解析中に内容が変化する場合があります。"
        };
        var cancelButton = new Button { Text = "キャンセル", AutoSize = true, DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false
        };
        buttons.Controls.Add(cancelButton);
        buttons.Controls.Add(_openButton);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            RowCount = 3,
            ColumnCount = 1
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(warning, 0, 0);
        layout.Controls.Add(_diskList, 0, 1);
        layout.Controls.Add(buttons, 0, 2);
        Controls.Add(layout);

        AcceptButton = _openButton;
        CancelButton = cancelButton;
    }

    public PhysicalDiskInfo? SelectedDisk => _diskList.SelectedItem as PhysicalDiskInfo;

    private void AcceptSelection()
    {
        if (SelectedDisk is null)
        {
            return;
        }

        DialogResult = DialogResult.OK;
        Close();
    }
}
