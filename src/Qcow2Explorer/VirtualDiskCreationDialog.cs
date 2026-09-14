using System.Globalization;
using Qcow2Explorer.Core;
using Qcow2Explorer.Creation;

namespace Qcow2Explorer;

internal sealed class VirtualDiskCreationDialog : Form
{
    private readonly TextBox _destinationBox = new() { Dock = DockStyle.Fill, ReadOnly = true };
    private readonly ComboBox _formatBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
    private readonly ComboBox _tableBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
    private readonly NumericUpDown _capacityBox = new()
    {
        Minimum = 512,
        Maximum = 8 * 1024 * 1024,
        Value = 2048,
        Increment = 128,
        ThousandsSeparator = true,
        Width = 150,
    };
    private readonly DataGridView _partitionGrid = new()
    {
        Dock = DockStyle.Fill,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = true,
    };
    private readonly DataGridView _initialFileGrid = new()
    {
        Dock = DockStyle.Fill,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = true,
    };
    private readonly Label _layoutSummary = new() { AutoSize = true, Padding = new Padding(8, 6, 0, 0) };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly Label _status = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Button _createButton = new() { Text = "作成", AutoSize = true };
    private readonly Button _cancelButton = new() { Text = "閉じる", AutoSize = true };
    private CancellationTokenSource? _creationCancellation;

    public VirtualDiskCreationDialog()
    {
        Text = "新規仮想ディスク";
        MinimumSize = new Size(820, 560);
        Width = 920;
        Height = 620;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;

        _formatBox.Items.AddRange(["RAW", "QCOW2"]);
        _formatBox.SelectedIndex = 1;
        _tableBox.Items.AddRange(["GPT", "MBR"]);
        _tableBox.SelectedIndex = 0;

        _partitionGrid.Columns.Add("Name", "パーティション名");
        var fileSystemColumn = new DataGridViewComboBoxColumn
        {
            Name = "FileSystem",
            HeaderText = "ファイルシステム",
            FlatStyle = FlatStyle.Flat,
        };
        fileSystemColumn.Items.AddRange("XFS", "ext4", "NTFS");
        _partitionGrid.Columns.Add(fileSystemColumn);
        _partitionGrid.Columns.Add("Size", "容量 (MiB)");
        _partitionGrid.Columns.Add("Label", "ボリュームラベル");
        _partitionGrid.Columns["Name"]!.FillWeight = 150;
        _partitionGrid.Columns["FileSystem"]!.FillWeight = 75;
        _partitionGrid.Columns["Size"]!.FillWeight = 70;
        _partitionGrid.Columns["Label"]!.FillWeight = 100;
        _partitionGrid.CellValueChanged += (_, _) => UpdateLayoutSummary();
        _partitionGrid.RowsRemoved += (_, _) => UpdateLayoutSummary();
        _capacityBox.ValueChanged += (_, _) => UpdateLayoutSummary();
        _initialFileGrid.Columns.Add("Partition", "パーティション #");
        _initialFileGrid.Columns.Add("Source", "ホスト側ファイル");
        _initialFileGrid.Columns.Add("Destination", "作成先ファイル名");
        _initialFileGrid.Columns["Partition"]!.FillWeight = 55;
        _initialFileGrid.Columns["Source"]!.FillWeight = 220;
        _initialFileGrid.Columns["Source"]!.ReadOnly = true;
        _initialFileGrid.Columns["Destination"]!.FillWeight = 110;
        AddPartition();

        var browseButton = new Button { Text = "参照...", AutoSize = true };
        browseButton.Click += (_, _) => BrowseDestination();
        var destinationLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
        };
        destinationLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 115));
        destinationLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        destinationLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        destinationLayout.Controls.Add(new Label
        {
            Text = "出力ファイル",
            AutoSize = true,
            Padding = new Padding(8, 8, 0, 0),
        }, 0, 0);
        destinationLayout.Controls.Add(_destinationBox, 1, 0);
        destinationLayout.Controls.Add(browseButton, 2, 0);

        var options = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            WrapContents = true,
            Padding = new Padding(8, 4, 8, 4),
        };
        options.Controls.AddRange([
            FieldLabel("形式"),
            _formatBox,
            FieldLabel("パーティション表", 16),
            _tableBox,
            FieldLabel("ディスク容量 (MiB)", 16),
            _capacityBox,
        ]);

        var addButton = new Button { Text = "パーティション追加", AutoSize = true };
        addButton.Click += (_, _) => AddPartition();
        var removeButton = new Button { Text = "選択パーティション削除", AutoSize = true };
        removeButton.Click += (_, _) => RemoveSelectedPartitions();
        var partitionButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            WrapContents = false,
            Padding = new Padding(8, 2, 8, 2),
        };
        partitionButtons.Controls.Add(addButton);
        partitionButtons.Controls.Add(removeButton);
        partitionButtons.Controls.Add(_layoutSummary);

        var addInitialFilesButton = new Button { Text = "初期ファイル追加...", AutoSize = true };
        addInitialFilesButton.Click += (_, _) => AddInitialFiles();
        var removeInitialFilesButton = new Button { Text = "選択ファイル削除", AutoSize = true };
        removeInitialFilesButton.Click += (_, _) => RemoveSelectedInitialFiles();
        var initialFileButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            WrapContents = false,
            Padding = new Padding(8, 2, 8, 2),
        };
        initialFileButtons.Controls.Add(addInitialFilesButton);
        initialFileButtons.Controls.Add(removeInitialFilesButton);
        initialFileButtons.Controls.Add(new Label
        {
            Text = "パーティション番号と作成先名は表で変更できます。配置先は各ファイルシステムのrootです。",
            AutoSize = true,
            Padding = new Padding(10, 6, 0, 0),
        });

        var initialFilePanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        initialFilePanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        initialFilePanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        initialFilePanel.Controls.Add(initialFileButtons, 0, 0);
        initialFilePanel.Controls.Add(_initialFileGrid, 0, 1);
        var definitionTabs = new TabControl { Dock = DockStyle.Fill };
        definitionTabs.TabPages.Add(new TabPage("パーティション") { Controls = { _partitionGrid } });
        definitionTabs.TabPages.Add(new TabPage("初期ファイル") { Controls = { initialFilePanel } });

        var description = new Label
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(10, 8, 10, 4),
            Text = "RAWまたは内製writerによる非圧縮sparse QCOW2を新規作成し、MBR/GPT上の各パーティションをXFS、ext4、NTFSで初期化します。"
                + " 容量はXFSが320 MiB以上、ext4／NTFSが64 MiB以上で、すべて1 MiB単位です。"
                + " ファイルシステムもC#で直接生成するためWSLや外部mkfsは不要です。初期ファイルをrootへ配置でき、作成後は自動で開きます。",
        };

        _createButton.Click += async (_, _) => await CreateDiskAsync();
        _cancelButton.Click += (_, _) =>
        {
            if (_creationCancellation is not null)
            {
                _creationCancellation.Cancel();
                _cancelButton.Enabled = false;
            }
            else
            {
                DialogResult = DialogResult.Cancel;
            }
        };
        var bottomButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(8, 5, 8, 5),
        };
        bottomButtons.Controls.Add(_cancelButton);
        bottomButtons.Controls.Add(_createButton);

        var statusLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        statusLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 70));
        statusLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
        statusLayout.Controls.Add(_status, 0, 0);
        statusLayout.Controls.Add(_progress, 1, 0);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.Controls.Add(destinationLayout, 0, 0);
        layout.Controls.Add(options, 0, 1);
        layout.Controls.Add(description, 0, 2);
        layout.Controls.Add(partitionButtons, 0, 3);
        layout.Controls.Add(definitionTabs, 0, 4);
        layout.Controls.Add(statusLayout, 0, 5);
        layout.Controls.Add(bottomButtons, 0, 6);
        Controls.Add(layout);

        AcceptButton = _createButton;
        FormClosing += (_, e) =>
        {
            if (_creationCancellation is not null && DialogResult != DialogResult.OK)
            {
                _creationCancellation.Cancel();
                e.Cancel = true;
            }
        };
        UpdateLayoutSummary();
    }

    public string? CreatedPath { get; private set; }

    private static Label FieldLabel(string text, int left = 0) => new()
    {
        Text = text,
        AutoSize = true,
        Padding = new Padding(left, 7, 0, 0),
    };

    private void BrowseDestination()
    {
        using var dialog = new SaveFileDialog
        {
            AddExtension = true,
            DefaultExt = _formatBox.SelectedIndex == 1 ? "qcow2" : "raw",
            Filter = _formatBox.SelectedIndex == 1
                ? "QCOW2 image (*.qcow2)|*.qcow2|All files (*.*)|*.*"
                : "RAW image (*.raw)|*.raw|Disk image (*.img)|*.img|All files (*.*)|*.*",
            OverwritePrompt = false,
            Title = "新規仮想ディスクの保存先",
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _destinationBox.Text = dialog.FileName;
        }
    }

    private void AddPartition()
    {
        if (_tableBox.SelectedIndex == 1 && _partitionGrid.Rows.Count >= 4)
        {
            MessageBox.Show(this, "MBRでは最大4パーティションです。", "パーティション追加", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var number = _partitionGrid.Rows.Count + 1;
        _partitionGrid.Rows.Add($"XFS partition {number}", "XFS", "512", $"VDT_XFS_{number}");
        UpdateLayoutSummary();
    }

    private void RemoveSelectedPartitions()
    {
        if (_partitionGrid.Rows.Count - _partitionGrid.SelectedRows.Count < 1)
        {
            MessageBox.Show(this, "少なくとも1パーティション必要です。", "パーティション削除", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        foreach (DataGridViewRow row in _partitionGrid.SelectedRows)
        {
            if (!row.IsNewRow)
            {
                _partitionGrid.Rows.Remove(row);
            }
        }

        UpdateLayoutSummary();
    }

    private void AddInitialFiles()
    {
        using var dialog = new OpenFileDialog
        {
            Multiselect = true,
            CheckFileExists = true,
            Title = "新規ファイルシステムへ初期配置するファイル",
            Filter = "All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var partitionNumber = _partitionGrid.SelectedRows.Count == 1
            ? _partitionGrid.SelectedRows[0].Index + 1
            : 1;
        foreach (var path in dialog.FileNames)
        {
            _initialFileGrid.Rows.Add(partitionNumber, Path.GetFullPath(path), Path.GetFileName(path));
        }
    }

    private void RemoveSelectedInitialFiles()
    {
        foreach (DataGridViewRow row in _initialFileGrid.SelectedRows)
        {
            if (!row.IsNewRow)
            {
                _initialFileGrid.Rows.Remove(row);
            }
        }
    }

    private void UpdateLayoutSummary()
    {
        long total = 1;
        foreach (DataGridViewRow row in _partitionGrid.Rows)
        {
            if (long.TryParse(
                    Convert.ToString(row.Cells["Size"].Value, CultureInfo.InvariantCulture),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var size))
            {
                total = checked(total + Math.Max(0, size));
            }
        }

        var capacity = decimal.ToInt64(_capacityBox.Value);
        _layoutSummary.Text = $"配置予定 約{total:N0} / {capacity:N0} MiB";
        _layoutSummary.ForeColor = total <= capacity ? SystemColors.ControlText : Color.Firebrick;
    }

    private async Task CreateDiskAsync()
    {
        VirtualDiskCreationRequest request;
        try
        {
            request = ReadRequest();
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or OverflowException)
        {
            MessageBox.Show(this, ex.Message, "入力エラー", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        SetCreatingState(true);
        using var cancellation = new CancellationTokenSource();
        _creationCancellation = cancellation;
        var progress = new Progress<DiskImageProgress>(update =>
        {
            _status.Text = update.Message;
            if (update.Percentage is int percentage)
            {
                _progress.Style = ProgressBarStyle.Continuous;
                _progress.Value = Math.Clamp(percentage, 0, 100);
            }
            else
            {
                _progress.Style = ProgressBarStyle.Marquee;
            }
        });
        try
        {
            var result = await VirtualDiskCreationService.CreateAsync(request, progress, cancellation.Token);
            CreatedPath = result.DestinationPath;
            _creationCancellation = null;
            DialogResult = DialogResult.OK;
        }
        catch (OperationCanceledException)
        {
            _status.Text = "作成をキャンセルしました。途中ファイルは削除されています。";
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or ArgumentException
                                   or InvalidDataException
                                   or NotSupportedException
                                   or OverflowException)
        {
            MessageBox.Show(
                this,
                $"仮想ディスクを作成できませんでした。{Environment.NewLine}{ex.Message}",
                "作成エラー",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            _status.Text = "作成に失敗しました。途中ファイルは削除されています。";
        }
        finally
        {
            _creationCancellation = null;
            if (DialogResult != DialogResult.OK)
            {
                SetCreatingState(false);
            }
        }
    }

    private VirtualDiskCreationRequest ReadRequest()
    {
        _partitionGrid.EndEdit();
        if (string.IsNullOrWhiteSpace(_destinationBox.Text))
        {
            throw new ArgumentException("出力ファイルを選択してください。");
        }

        var partitions = new List<VirtualDiskPartitionDefinition>();
        foreach (DataGridViewRow row in _partitionGrid.Rows)
        {
            var name = Convert.ToString(row.Cells["Name"].Value, CultureInfo.InvariantCulture) ?? string.Empty;
            var label = Convert.ToString(row.Cells["Label"].Value, CultureInfo.InvariantCulture) ?? string.Empty;
            var fileSystemText = Convert.ToString(row.Cells["FileSystem"].Value, CultureInfo.InvariantCulture);
            var fileSystem = fileSystemText switch
            {
                "XFS" => VirtualDiskFileSystemKind.Xfs,
                "ext4" => VirtualDiskFileSystemKind.Ext4,
                "NTFS" => VirtualDiskFileSystemKind.Ntfs,
                _ => throw new ArgumentException("各パーティションのファイルシステムを選択してください。"),
            };
            var sizeText = Convert.ToString(row.Cells["Size"].Value, CultureInfo.InvariantCulture);
            var minimumMiB = VirtualDiskPartitionTableWriter.GetMinimumPartitionSizeBytes(fileSystem) / (1024 * 1024);
            if (!long.TryParse(sizeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sizeMiB)
                || sizeMiB < minimumMiB)
            {
                throw new ArgumentException(
                    $"{VirtualDiskPartitionTableWriter.GetDisplayName(fileSystem)}パーティション容量は"
                    + $"{minimumMiB:N0} MiB以上の整数で指定してください。");
            }

            partitions.Add(new VirtualDiskPartitionDefinition(
                checked(sizeMiB * 1024 * 1024),
                name,
                label,
                fileSystem));
        }

        var initialFiles = new List<VirtualDiskInitialFile>();
        foreach (DataGridViewRow row in _initialFileGrid.Rows)
        {
            var partitionText = Convert.ToString(row.Cells["Partition"].Value, CultureInfo.InvariantCulture);
            var sourcePath = Convert.ToString(row.Cells["Source"].Value, CultureInfo.InvariantCulture) ?? string.Empty;
            var destinationName = Convert.ToString(row.Cells["Destination"].Value, CultureInfo.InvariantCulture) ?? string.Empty;
            if (!int.TryParse(partitionText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var partitionNumber))
            {
                throw new ArgumentException("初期ファイルのパーティション番号は整数で指定してください。");
            }

            initialFiles.Add(new VirtualDiskInitialFile(partitionNumber, sourcePath, destinationName));
        }

        if (_tableBox.SelectedIndex == 1 && partitions.Count > 4)
        {
            throw new ArgumentException("MBRでは最大4パーティションです。");
        }

        return new VirtualDiskCreationRequest(
            _destinationBox.Text,
            checked(decimal.ToInt64(_capacityBox.Value) * 1024 * 1024),
            _formatBox.SelectedIndex == 1 ? VirtualDiskContainerFormat.Qcow2 : VirtualDiskContainerFormat.Raw,
            _tableBox.SelectedIndex == 0 ? VirtualDiskPartitionTableKind.Gpt : VirtualDiskPartitionTableKind.Mbr,
            partitions,
            initialFiles);
    }

    private void SetCreatingState(bool creating)
    {
        _destinationBox.Enabled = !creating;
        _formatBox.Enabled = !creating;
        _tableBox.Enabled = !creating;
        _capacityBox.Enabled = !creating;
        _partitionGrid.Enabled = !creating;
        _initialFileGrid.Enabled = !creating;
        _createButton.Enabled = !creating;
        _cancelButton.Enabled = true;
        _cancelButton.Text = creating ? "キャンセル" : "閉じる";
        _progress.Visible = creating;
        if (!creating)
        {
            _progress.Value = 0;
            _progress.Style = ProgressBarStyle.Continuous;
        }
    }
}
