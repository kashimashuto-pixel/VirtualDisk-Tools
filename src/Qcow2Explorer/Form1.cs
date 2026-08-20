using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Qcow2Explorer.Core;
using Qcow2Explorer.FileSystems;
using Qcow2Explorer.Mounting;
using Qcow2Explorer.Partitions;
using Qcow2Explorer.Previewing;
using Qcow2Explorer.Reporting;

namespace Qcow2Explorer;

public partial class Form1 : Form
{
    private readonly ToolStripTextBox _pathBox = new() { AutoSize = false, Width = 480, ReadOnly = true };
    private readonly ToolStripLabel _statusLabel = new("ディスクイメージを開いてください");
    private readonly ToolStripProgressBar _loadProgressBar = new() { AutoSize = false, Width = 140, Visible = false };
    private readonly ToolStripButton _cancelLoadButton = new("読み込みキャンセル") { Enabled = false };
    private readonly ToolStripTextBox _searchBox = new() { AutoSize = false, Width = 240, BorderStyle = BorderStyle.FixedSingle, ToolTipText = "現在のパーティションからファイル名を検索" };
    private readonly ToolStripButton _cancelSearchButton = new("検索キャンセル") { Enabled = false };
    private readonly ToolStripButton _cancelCopyButton = new("コピーキャンセル") { Enabled = false };
    private readonly ToolStripProgressBar _copyProgressBar = new() { AutoSize = false, Width = 120, Visible = false };
    private readonly ToolStripButton _backNavigationButton = new("戻る") { Enabled = false, ToolTipText = "戻る (Alt+←)" };
    private readonly ToolStripButton _forwardNavigationButton = new("進む") { Enabled = false, ToolTipText = "進む (Alt+→)" };
    private readonly ToolStripButton _upNavigationButton = new("上へ") { Enabled = false, ToolTipText = "親フォルダーへ (Alt+↑)" };
    private readonly ListView _headerList = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true };
    private readonly TextBox _warningText = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
    private readonly TextBox _offsetBox = new() { Text = "0x0", Width = 140 };
    private readonly NumericUpDown _lengthBox = new() { Minimum = 1, Maximum = 1024 * 1024, Value = 512, Increment = 512, Width = 110 };
    private readonly TextBox _hexText = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font("Consolas", 10) };
    private readonly DataGridView _partitionGrid = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill };
    private readonly DataGridView _uefiVariableGrid = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill };
    private readonly TextBox _uefiVariableDetails = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font("Consolas", 10) };
    private readonly CheckBox _showInactiveUefiVariables = new() { Text = "削除済み・履歴も表示", AutoSize = true, Padding = new Padding(8, 5, 0, 0) };
    private readonly DataGridView _tpmStateGrid = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill };
    private readonly TextBox _tpmStateDetails = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font("Consolas", 10) };
    private readonly TreeView _tree = new() { Dock = DockStyle.Fill, HideSelection = false };
    private readonly ListView _fileList = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true, MultiSelect = true };
    private readonly TextBox _previewText = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font("Consolas", 10) };
    private readonly ListView _mountList = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true, MultiSelect = true };
    private readonly TextBox _mountText = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };

    private IDiskImageReader? _reader;
    private readonly List<PartitionInfo> _partitions = new();
    private readonly Dictionary<int, IReadOnlyFileSystem> _fileSystems = new();
    private readonly List<IDisposable> _partitionReaders = new();
    private readonly List<ProjectedFileSystemMount> _mounts = new();
    private readonly List<string> _analysisWarnings = new();
    private IReadOnlyFileSystem? _currentFileSystem;
    private VfsNode? _currentDirectory;
    private string _currentDirectoryPath = "/";
    private CancellationTokenSource? _searchCancellation;
    private CancellationTokenSource? _loadCancellation;
    private readonly HashSet<CancellationTokenSource> _copyCancellations = [];
    private readonly SemaphoreSlim _copyExecutionGate = new(1, 1);
    private CancellationTokenSource? _copyProgressOwner;
    private readonly NavigationHistory<TreeNode> _navigationHistory = new();
    private bool _isHistoryNavigation;
    private bool _isLoadingImage;
    private bool _closeAfterLoadCancellation;
    private UefiVariableStore? _currentUefiVariableStore;
    private SwtpmStateStore? _currentTpmStateStore;

    public Form1(string? initialPath = null)
    {
        InitializeComponent();
        BuildUi();
        if (!string.IsNullOrWhiteSpace(initialPath))
        {
            Shown += async (_, _) => await LoadImageAsync(initialPath);
        }
        FormClosing += Form1FormClosing;
        FormClosed += (_, _) =>
        {
            _loadCancellation?.Cancel();
            _searchCancellation?.Cancel();
            CancelCopyOperations();
            DisposeMounts();
            DisposeFileSystems();
            DisposePartitionReaders();
            _reader?.Dispose();
        };
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (_tree.Visible && keyData == (Keys.Alt | Keys.Up))
        {
            NavigateUp();
            return true;
        }

        if (_tree.Visible && keyData == (Keys.Alt | Keys.Left))
        {
            NavigateBack();
            return true;
        }

        if (_tree.Visible && keyData == (Keys.Alt | Keys.Right))
        {
            NavigateForward();
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void BuildUi()
    {
        Text = "Virtual Disk Explorer";
        MinimumSize = new Size(980, 640);
        Width = 1180;
        Height = 760;

        var toolStrip = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden };
        var openButton = new ToolStripButton("開く");
        openButton.Click += async (_, _) => await OpenImageDialogAsync();
        var openFolderButton = new ToolStripButton("フォルダ");
        openFolderButton.Click += async (_, _) => await OpenImageFolderDialogAsync();
        var openPhysicalDiskButton = new ToolStripButton("物理ディスク");
        openPhysicalDiskButton.Click += async (_, _) => await OpenPhysicalDiskDialogAsync();
        var reportButton = new ToolStripButton("解析レポート");
        reportButton.Click += (_, _) => SaveAnalysisReport();
        var snapshotButton = new ToolStripButton("スナップショット");
        snapshotButton.Click += (_, _) => SelectQcow2Snapshot();
        var vmaDiskButton = new ToolStripButton("VMAディスク");
        vmaDiskButton.Click += (_, _) => SelectVmaDisk();
        var ovaDiskButton = new ToolStripButton("OVAディスク");
        ovaDiskButton.Click += (_, _) => SelectOvaDisk();
        toolStrip.Items.Add(openButton);
        toolStrip.Items.Add(openFolderButton);
        toolStrip.Items.Add(openPhysicalDiskButton);
        toolStrip.Items.Add(new ToolStripSeparator());
        toolStrip.Items.Add(new ToolStripLabel("ファイル"));
        toolStrip.Items.Add(_pathBox);
        toolStrip.Items.Add(reportButton);
        toolStrip.Items.Add(snapshotButton);
        toolStrip.Items.Add(vmaDiskButton);
        toolStrip.Items.Add(ovaDiskButton);
        toolStrip.Items.Add(new ToolStripSeparator());
        toolStrip.Items.Add(_statusLabel);
        toolStrip.Items.Add(_loadProgressBar);
        _cancelLoadButton.Click += (_, _) => CancelImageLoad();
        toolStrip.Items.Add(_cancelLoadButton);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(CreateSummaryTab());
        tabs.TabPages.Add(CreateRawTab());
        tabs.TabPages.Add(CreatePartitionTab());
        tabs.TabPages.Add(CreateUefiVariableTab());
        tabs.TabPages.Add(CreateTpmStateTab());
        tabs.TabPages.Add(CreateExplorerTab());
        tabs.TabPages.Add(CreateMountTab());

        Controls.Clear();
        Controls.Add(tabs);
        Controls.Add(toolStrip);
        toolStrip.Dock = DockStyle.Top;
    }

    private TabPage CreateSummaryTab()
    {
        _headerList.Columns.Add("項目", 220);
        _headerList.Columns.Add("値", 760);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 70));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 30));
        layout.Controls.Add(_headerList, 0, 0);
        layout.Controls.Add(_warningText, 0, 1);

        return new TabPage("概要") { Controls = { layout } };
    }

    private TabPage CreateRawTab()
    {
        var top = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Padding = new Padding(8) };
        var readButton = new Button { Text = "読込", Width = 80 };
        var clusterButton = new Button { Text = "クラスタ", Width = 90 };
        readButton.Click += (_, _) => ReadRawData();
        clusterButton.Click += (_, _) => ShowClusterLookup();
        top.Controls.AddRange(new Control[]
        {
            new Label { Text = "Offset", AutoSize = true, Padding = new Padding(0, 6, 0, 0) },
            _offsetBox,
            new Label { Text = "Length", AutoSize = true, Padding = new Padding(12, 6, 0, 0) },
            _lengthBox,
            readButton,
            clusterButton
        });

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(top, 0, 0);
        layout.Controls.Add(_hexText, 0, 1);

        return new TabPage("生データ") { Controls = { layout } };
    }

    private TabPage CreatePartitionTab()
    {
        _partitionGrid.Columns.Add("Number", "#");
        _partitionGrid.Columns.Add("Scheme", "方式");
        _partitionGrid.Columns.Add("FileSystem", "FS");
        _partitionGrid.Columns.Add("Name", "名前");
        _partitionGrid.Columns.Add("Type", "種別");
        _partitionGrid.Columns.Add("Start", "開始 LBA");
        _partitionGrid.Columns.Add("Sectors", "セクタ数");
        _partitionGrid.Columns.Add("Bytes", "サイズ");
        _partitionGrid.CellDoubleClick += (_, _) => ActivateSelectedPartition();
        return new TabPage("パーティション") { Controls = { _partitionGrid } };
    }

    private TabPage CreateUefiVariableTab()
    {
        _uefiVariableGrid.Columns.Add("Name", "名前");
        _uefiVariableGrid.Columns.Add("Guid", "Vendor GUID");
        _uefiVariableGrid.Columns.Add("State", "状態");
        _uefiVariableGrid.Columns.Add("Attributes", "属性");
        _uefiVariableGrid.Columns.Add("Size", "サイズ");
        _uefiVariableGrid.Columns.Add("Summary", "解釈");
        _uefiVariableGrid.Columns["Name"]!.FillWeight = 110;
        _uefiVariableGrid.Columns["Guid"]!.FillWeight = 150;
        _uefiVariableGrid.Columns["State"]!.FillWeight = 65;
        _uefiVariableGrid.Columns["Attributes"]!.FillWeight = 120;
        _uefiVariableGrid.Columns["Size"]!.FillWeight = 60;
        _uefiVariableGrid.Columns["Summary"]!.FillWeight = 200;
        _uefiVariableGrid.SelectionChanged += (_, _) => ShowSelectedUefiVariable();
        _showInactiveUefiVariables.CheckedChanged += (_, _) => PopulateUefiVariableRows();

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 330
        };
        split.Panel1.Controls.Add(_uefiVariableGrid);
        split.Panel2.Controls.Add(_uefiVariableDetails);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(_showInactiveUefiVariables, 0, 0);
        layout.Controls.Add(split, 0, 1);
        return new TabPage("UEFI変数") { Controls = { layout } };
    }

    private TabPage CreateTpmStateTab()
    {
        _tpmStateGrid.Columns.Add("Slot", "スロット");
        _tpmStateGrid.Columns.Add("Name", "状態");
        _tpmStateGrid.Columns.Add("Offset", "オフセット");
        _tpmStateGrid.Columns.Add("DataSize", "データ");
        _tpmStateGrid.Columns.Add("SectionSize", "予約領域");
        _tpmStateGrid.Columns.Add("Blob", "Blob");
        _tpmStateGrid.Columns.Add("Encryption", "暗号化");
        _tpmStateGrid.Columns.Add("Tlvs", "TLV");
        _tpmStateGrid.Columns["Slot"]!.FillWeight = 45;
        _tpmStateGrid.Columns["Name"]!.FillWeight = 150;
        _tpmStateGrid.Columns["Offset"]!.FillWeight = 85;
        _tpmStateGrid.Columns["DataSize"]!.FillWeight = 85;
        _tpmStateGrid.Columns["SectionSize"]!.FillWeight = 85;
        _tpmStateGrid.Columns["Blob"]!.FillWeight = 70;
        _tpmStateGrid.Columns["Encryption"]!.FillWeight = 145;
        _tpmStateGrid.Columns["Tlvs"]!.FillWeight = 55;
        _tpmStateGrid.SelectionChanged += (_, _) => ShowSelectedTpmState();

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 250
        };
        split.Panel1.Controls.Add(_tpmStateGrid);
        split.Panel2.Controls.Add(_tpmStateDetails);
        return new TabPage("TPM状態") { Controls = { split } };
    }

    private TabPage CreateExplorerTab()
    {
        _tree.BeforeExpand += TreeBeforeExpand;
        _tree.AfterSelect += TreeAfterSelect;
        _tree.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && _tree.SelectedNode is TreeNode selectedNode)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                selectedNode.Expand();
            }
        };

        _fileList.Columns.Add("名前", 360);
        _fileList.Columns.Add("サイズ", 120, HorizontalAlignment.Right);
        _fileList.Columns.Add("更新日時 UTC", 170);
        _fileList.Columns.Add("種別 / 属性", 220);
        _fileList.Columns.Add("場所", 420);
        _fileList.DoubleClick += async (_, _) => await OpenSelectedListItemAsync();
        _fileList.SelectedIndexChanged += (_, _) => ShowSelectedItemProperties();
        _fileList.MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Right && _fileList.GetItemAt(e.X, e.Y) is ListViewItem item)
            {
                _fileList.SelectedItems.Clear();
                item.Selected = true;
                item.Focused = true;
            }
        };
        var fileListContextMenu = new ContextMenuStrip();
        var showContainingFolderItem = new ToolStripMenuItem("保存されているフォルダーを表示");
        showContainingFolderItem.Click += (_, _) => ShowSelectedItemContainingDirectory();
        fileListContextMenu.Items.Add(showContainingFolderItem);
        fileListContextMenu.Opening += (_, _) =>
        {
            showContainingFolderItem.Enabled = _currentFileSystem is not null
                && _fileList.SelectedItems.Count == 1
                && _fileList.SelectedItems[0].Tag is VfsNode
                && GetListItemPath(_fileList.SelectedItems[0]).Length > 0;
        };
        _fileList.ContextMenuStrip = fileListContextMenu;
        _fileList.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                await OpenSelectedListItemAsync();
            }
        };

        var previewButton = new ToolStripButton("プレビュー");
        previewButton.Click += (_, _) => PreviewSelectedFile();
        var windowPreviewButton = new ToolStripButton("別窓表示");
        windowPreviewButton.Click += async (_, _) => await OpenSelectedFilePreviewAsync(showUnsupportedMessage: true);
        var copyButton = new ToolStripButton("選択項目をコピー");
        copyButton.Click += async (_, _) => await CopySelectedItemsAsync();
        var copyFolderButton = new ToolStripButton("表示フォルダをコピー");
        copyFolderButton.Click += async (_, _) => await CopyCurrentDirectoryAsync();
        var mountButton = new ToolStripButton("マウント");
        mountButton.Click += (_, _) => MountSelectedPartition();
        var deletedButton = new ToolStripButton("削除済みNTFS");
        deletedButton.Click += (_, _) => ShowDeletedNtfsFiles();
        var searchButton = new ToolStripButton("検索");
        searchButton.Click += async (_, _) => await SearchCurrentFileSystemAsync();
        var clearSearchButton = new ToolStripButton("クリア");
        clearSearchButton.Click += (_, _) =>
        {
            _searchCancellation?.Cancel();
            _searchBox.Clear();
            if (_currentFileSystem is not null && _currentDirectory is not null)
            {
                PopulateFileList(_currentFileSystem, _currentDirectory, _currentDirectoryPath);
            }
        };
        _searchBox.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                await SearchCurrentFileSystemAsync();
            }
        };
        _cancelSearchButton.Click += (_, _) => _searchCancellation?.Cancel();
        _cancelCopyButton.Click += (_, _) => CancelCopyOperations();
        _backNavigationButton.Click += (_, _) => NavigateBack();
        _forwardNavigationButton.Click += (_, _) => NavigateForward();
        _upNavigationButton.Click += (_, _) => NavigateUp();
        var explorerStrip = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden };
        explorerStrip.Items.Add(_backNavigationButton);
        explorerStrip.Items.Add(_forwardNavigationButton);
        explorerStrip.Items.Add(_upNavigationButton);
        explorerStrip.Items.Add(new ToolStripSeparator());
        explorerStrip.Items.Add(new ToolStripLabel("検索"));
        explorerStrip.Items.Add(_searchBox);
        explorerStrip.Items.Add(searchButton);
        explorerStrip.Items.Add(clearSearchButton);
        explorerStrip.Items.Add(_cancelSearchButton);
        explorerStrip.Items.Add(new ToolStripSeparator());
        explorerStrip.Items.Add(windowPreviewButton);
        explorerStrip.Items.Add(previewButton);
        explorerStrip.Items.Add(copyButton);
        explorerStrip.Items.Add(copyFolderButton);
        explorerStrip.Items.Add(_copyProgressBar);
        explorerStrip.Items.Add(_cancelCopyButton);
        explorerStrip.Items.Add(deletedButton);
        explorerStrip.Items.Add(new ToolStripSeparator());
        explorerStrip.Items.Add(mountButton);

        var right = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, FixedPanel = FixedPanel.Panel2 };
        right.Panel1.Controls.Add(_fileList);
        right.Panel2.Controls.Add(_previewText);

        var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1 };
        split.Panel1.Controls.Add(_tree);
        split.Panel2.Controls.Add(right);
        split.HandleCreated += (_, _) => BeginInvoke(() =>
        {
            if (!split.IsDisposed && split.Width > 600)
            {
                split.SplitterDistance = 270;
            }

            if (!right.IsDisposed && right.Height > 300)
            {
                right.SplitterDistance = Math.Max(180, right.Height - 180);
            }
        });

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(explorerStrip, 0, 0);
        layout.Controls.Add(split, 0, 1);

        return new TabPage("エクスプローラー") { Controls = { layout } };
    }

    private TabPage CreateMountTab()
    {
        _mountList.Columns.Add("パーティション", 120);
        _mountList.Columns.Add("FS", 100);
        _mountList.Columns.Add("マウント先", 520);
        _mountList.Columns.Add("状態", 180);

        var mountButton = new Button { Text = "選択中パーティションを指定フォルダへマウント", AutoSize = true };
        mountButton.Click += (_, _) => MountSelectedPartition();
        var openButton = new Button { Text = "開く", Width = 80 };
        openButton.Click += (_, _) => OpenSelectedMountFolder();
        var unmountButton = new Button { Text = "選択解除", Width = 90 };
        unmountButton.Click += (_, _) => UnmountSelectedMounts();
        var unmountAllButton = new Button { Text = "すべて解除", Width = 100 };
        unmountAllButton.Click += (_, _) => UnmountAllMountsWithPrompt();
        var enableButton = new Button { Text = "ProjFS有効化", Width = 110 };
        enableButton.Click += (_, _) => ProjFsFeature.PromptAndEnable(this);
        var refreshButton = new Button { Text = "更新", Width = 80 };
        refreshButton.Click += (_, _) => RefreshMountList();

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Padding = new Padding(8) };
        buttons.Controls.AddRange(new Control[] { mountButton, openButton, unmountButton, unmountAllButton, enableButton, refreshButton });

        _mountText.Text = string.Join(Environment.NewLine, new[]
        {
            "ProjFS による読み取り専用のフォルダ投影型マウントです。",
            "マウント中はこのアプリを終了しないでください。終了時にはマウント中か確認します。",
            "マウント先フォルダは空のフォルダを選択してください。",
            "",
            "ProjFS が無効な場合は「ProjFS有効化」またはマウント時の確認から管理者権限で有効化できます。"
        });

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 260 };
        split.Panel1.Controls.Add(_mountList);
        split.Panel2.Controls.Add(_mountText);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(buttons, 0, 0);
        layout.Controls.Add(split, 0, 1);

        return new TabPage("マウント") { Controls = { layout } };
    }

    private async Task OpenImageDialogAsync()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = DiskImageReaderFactory.DialogFilter,
            Title = "ディスクイメージを開く"
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            await LoadImageAsync(dialog.FileName);
        }
    }

    private async Task OpenImageFolderDialogAsync()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Parallels .hdd フォルダを選択してください",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            await LoadImageAsync(dialog.SelectedPath);
        }
    }

    private async Task OpenPhysicalDiskDialogAsync()
    {
        try
        {
            var disks = PhysicalDiskReader.Enumerate();
            if (disks.Count == 0)
            {
                MessageBox.Show(this, "物理ディスクが見つかりませんでした。", "物理ディスク", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using var dialog = new PhysicalDiskSelectionDialog(disks);
            if (dialog.ShowDialog(this) != DialogResult.OK || dialog.SelectedDisk is not PhysicalDiskInfo disk)
            {
                return;
            }

            var confirmation = MessageBox.Show(
                this,
                $"{disk}{Environment.NewLine}{Environment.NewLine}この物理ディスクを読み取り専用で開きますか？",
                "物理ディスクの確認",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (confirmation == DialogResult.Yes)
            {
                await LoadImageAsync(disk.DevicePath);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "物理ディスク列挙エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private LzopOpenSelection? SelectLzopOpenMode(string path)
    {
        using var dialog = new Form
        {
            Text = "LZO読み込みモード",
            Width = 660,
            Height = 420,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false
        };

        var introduction = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(600, 0),
            Text = $"LZO圧縮ディスクを開きます。用途に合わせて読み込み方法を選択してください。{Environment.NewLine}{path}"
        };
        var fastMode = new RadioButton
        {
            AutoSize = true,
            Checked = true,
            Text = "高速モード（推奨）"
        };
        var fastDescription = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(580, 0),
            Margin = new Padding(24, 0, 0, 8),
            Text = "最初に全体を一時RAWへ展開します。以後の検索・表示・コピーが高速になります。仮想ディスクと同程度の一時空き容量が必要です。"
        };
        var temporaryPathLabel = new Label
        {
            AutoSize = true,
            Margin = new Padding(24, 0, 0, 2),
            Text = "一時ファイルの保存先"
        };
        var temporaryPathBox = new TextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            Text = System.IO.Path.GetTempPath()
        };
        var browseTemporaryPathButton = new Button
        {
            AutoSize = true,
            Text = "参照..."
        };
        browseTemporaryPathButton.Click += (_, _) =>
        {
            using var folderDialog = new FolderBrowserDialog
            {
                Description = "LZO高速モードの一時ファイル保存先を選択してください",
                UseDescriptionForTitle = true,
                SelectedPath = temporaryPathBox.Text,
                ShowNewFolderButton = true
            };
            if (folderDialog.ShowDialog(dialog) == DialogResult.OK)
            {
                temporaryPathBox.Text = folderDialog.SelectedPath;
            }
        };
        var temporaryPathPanel = new TableLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(24, 0, 0, 8)
        };
        temporaryPathPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        temporaryPathPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        temporaryPathPanel.Controls.Add(temporaryPathBox, 0, 0);
        temporaryPathPanel.Controls.Add(browseTemporaryPathButton, 1, 0);
        var onDemandMode = new RadioButton
        {
            AutoSize = true,
            Text = "省容量モード"
        };
        var onDemandDescription = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(580, 0),
            Margin = new Padding(24, 0, 0, 8),
            Text = "必要なLZOブロックだけを随時展開します。一時容量をほとんど使いませんが、コピーやランダムアクセスに時間がかかる場合があります。"
        };
        var cleanupDescription = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(600, 0),
            Text = "高速モードの一時RAWは、別のイメージへ切り替えるかアプリを終了すると削除されます。"
        };
        fastMode.CheckedChanged += (_, _) =>
        {
            temporaryPathBox.Enabled = fastMode.Checked;
            browseTemporaryPathButton.Enabled = fastMode.Checked;
        };

        var okButton = new Button { Text = "開く", DialogResult = DialogResult.OK, Width = 90 };
        var cancelButton = new Button { Text = "キャンセル", DialogResult = DialogResult.Cancel, Width = 90 };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            AutoSize = true
        };
        buttons.Controls.Add(cancelButton);
        buttons.Controls.Add(okButton);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 9,
            Padding = new Padding(16)
        };
        layout.Controls.Add(introduction, 0, 0);
        layout.Controls.Add(fastMode, 0, 1);
        layout.Controls.Add(fastDescription, 0, 2);
        layout.Controls.Add(temporaryPathLabel, 0, 3);
        layout.Controls.Add(temporaryPathPanel, 0, 4);
        layout.Controls.Add(onDemandMode, 0, 5);
        layout.Controls.Add(onDemandDescription, 0, 6);
        layout.Controls.Add(cleanupDescription, 0, 7);
        layout.Controls.Add(buttons, 0, 8);
        dialog.Controls.Add(layout);
        dialog.AcceptButton = okButton;
        dialog.CancelButton = cancelButton;

        return dialog.ShowDialog(this) == DialogResult.OK
            ? fastMode.Checked
                ? new LzopOpenSelection(LzopOpenMode.TemporaryRaw, temporaryPathBox.Text)
                : new LzopOpenSelection(LzopOpenMode.OnDemand, null)
            : null;
    }

    private async Task LoadImageAsync(string path)
    {
        if (_isLoadingImage)
        {
            _statusLabel.Text = "別のディスクイメージを読み込み中です";
            return;
        }

        var lzopSelection = new LzopOpenSelection(LzopOpenMode.OnDemand, null);
        if (DiskImageReaderFactory.IsLzopFile(path))
        {
            var selectedMode = SelectLzopOpenMode(path);
            if (selectedMode is null)
            {
                _statusLabel.Text = "LZOイメージの読み込みをキャンセルしました";
                return;
            }

            lzopSelection = selectedMode;
        }

        if (!ConfirmAndDisposeMounts("新しいディスクイメージを開く前に、現在のマウントを解除します。続行しますか？"))
        {
            return;
        }

        _isLoadingImage = true;
        var loadCancellation = new CancellationTokenSource();
        _loadCancellation = loadCancellation;
        UseWaitCursor = true;
        _loadProgressBar.Visible = true;
        _loadProgressBar.Style = ProgressBarStyle.Marquee;
        _cancelLoadButton.Enabled = true;
        _statusLabel.Text = "ディスクイメージを開いています...";
        ImageLoadResult? loadResult = null;
        var adopted = false;

        try
        {
            var rawOffset = ParseOffset(_offsetBox.Text);
            var rawLength = (int)_lengthBox.Value;
            var progress = new Progress<DiskImageProgress>(UpdateLoadProgress);
            loadResult = await Task.Run(() => LoadAndAnalyzeImage(
                path,
                rawOffset,
                rawLength,
                progress,
                lzopSelection.Mode,
                lzopSelection.TemporaryDirectory,
                loadCancellation.Token),
                loadCancellation.Token);
            loadCancellation.Token.ThrowIfCancellationRequested();
            if (IsDisposed)
            {
                return;
            }

            DisposeFileSystems();
            DisposePartitionReaders();
            _reader?.Dispose();
            _reader = loadResult.Reader;
            _partitionReaders.AddRange(loadResult.Analysis.OwnedReaders);
            adopted = true;
            _partitions.Clear();
            _pathBox.Text = path;

            FillHeader();
            _analysisWarnings.AddRange(loadResult.Analysis.Diagnostics.Select(item => item.Message));
            ApplyPartitionAnalysis(loadResult.Analysis.Partitions);
            RefreshWarnings();
            _hexText.Text = loadResult.RawHex;

            var errors = loadResult.Analysis.Diagnostics.Where(item => item.IsError).ToList();
            if (loadResult.Analysis.LvmVolumeCount == 0 && errors.Count > 0)
            {
                MessageBox.Show(
                    this,
                    string.Join(Environment.NewLine, errors.Select(error => error.Message)),
                    "LVM2を解析できませんでした",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }

            _statusLabel.Text = "読込完了";
        }
        catch (UnauthorizedAccessException ex) when (PhysicalDiskReader.IsPhysicalDiskPath(path))
        {
            _statusLabel.Text = "管理者権限が必要です";
            PromptRestartAsAdministrator(path, ex.Message);
        }
        catch (OperationCanceledException) when (loadCancellation.IsCancellationRequested)
        {
            if (!IsDisposed)
            {
                _statusLabel.Text = "ディスクイメージの読み込みをキャンセルしました";
            }
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "読込失敗";
            MessageBox.Show(this, ex.Message, "ディスクイメージ読込エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            if (!adopted && loadResult is not null)
            {
                loadResult.Dispose();
            }

            if (ReferenceEquals(_loadCancellation, loadCancellation))
            {
                _loadCancellation = null;
            }

            loadCancellation.Dispose();
            _isLoadingImage = false;
            if (!IsDisposed)
            {
                UseWaitCursor = false;
                _loadProgressBar.Visible = false;
                _loadProgressBar.Style = ProgressBarStyle.Blocks;
                _cancelLoadButton.Enabled = false;
                if (_closeAfterLoadCancellation)
                {
                    _closeAfterLoadCancellation = false;
                    BeginInvoke(new Action(Close));
                }
            }
        }
    }

    private void CancelImageLoad()
    {
        var cancellation = _loadCancellation;
        if (cancellation is null || cancellation.IsCancellationRequested)
        {
            return;
        }

        cancellation.Cancel();
        _cancelLoadButton.Enabled = false;
        _statusLabel.Text = "ディスクイメージの読み込みをキャンセル中...";
    }

    private void UpdateLoadProgress(DiskImageProgress progress)
    {
        if (IsDisposed || !_isLoadingImage)
        {
            return;
        }

        _statusLabel.Text = progress.Message;
        if (progress.Percentage is int percentage)
        {
            _loadProgressBar.Style = ProgressBarStyle.Blocks;
            _loadProgressBar.Value = percentage;
        }
        else
        {
            _loadProgressBar.Style = ProgressBarStyle.Marquee;
        }
    }

    private static ImageLoadResult LoadAndAnalyzeImage(
        string path,
        long rawOffset,
        int rawLength,
        IProgress<DiskImageProgress> progress,
        LzopOpenMode lzopOpenMode,
        string? lzopTemporaryDirectory,
        CancellationToken cancellationToken)
    {
        IDiskImageReader? reader = null;
        var ownedReaders = new List<IDisposable>();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            reader = DiskImageReaderFactory.Open(
                path,
                progress,
                lzopOpenMode,
                lzopTemporaryDirectory,
                cancellationToken);
            var analysis = AnalyzeImage(reader, ownedReaders, progress, cancellationToken);
            progress.Report(new DiskImageProgress("先頭データを読み込み中..."));
            cancellationToken.ThrowIfCancellationRequested();
            var rawData = new byte[rawLength];
            reader.ReadAt(rawOffset, rawData, 0, rawLength);
            cancellationToken.ThrowIfCancellationRequested();
            var rawHex = HexFormatter.Format(rawData, rawOffset);
            return new ImageLoadResult(reader, analysis, rawHex);
        }
        catch
        {
            foreach (var disposable in ownedReaders)
            {
                disposable.Dispose();
            }

            reader?.Dispose();
            throw;
        }
    }

    private static ImageAnalysis AnalyzeImage(
        IDiskImageReader reader,
        List<IDisposable> ownedReaders,
        IProgress<DiskImageProgress> progress,
        CancellationToken cancellationToken)
    {
        progress.Report(new DiskImageProgress("パーティションテーブルを解析中..."));
        cancellationToken.ThrowIfCancellationRequested();
        var discovered = PartitionTableReader.ReadPartitions(reader, cancellationToken).ToList();
        if (discovered.Count == 0 && reader.Length >= 512)
        {
            discovered.Add(new PartitionInfo
            {
                Number = 1,
                Scheme = "WholeDisk",
                Name = "Whole disk",
                Type = "Unpartitioned",
                TypeId = "",
                StartLba = 0,
                SectorCount = checked((ulong)(reader.Length / 512))
            });
        }

        for (var index = 0; index < discovered.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(new DiskImageProgress(
                $"ファイルシステムを検出中: {index + 1:N0} / {discovered.Count:N0}",
                index + 1,
                discovered.Count));
            discovered[index].FileSystem = FileSystemDetector.Detect(reader, discovered[index], cancellationToken);
        }

        var allPartitions = new List<PartitionInfo>(discovered);
        var diagnostics = new List<LvmDiagnostic>();
        var lvmPartitions = discovered
            .Where(partition => partition.FileSystem.StartsWith("LVM2", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var lvmVolumeCount = 0;
        if (lvmPartitions.Count > 0)
        {
            progress.Report(new DiskImageProgress("LVM2論理ボリュームを解析中..."));
            var lvmResult = LogicalVolumeDiscoverer.Discover(
                reader,
                lvmPartitions,
                allPartitions.Count + 1,
                ownedReaders,
                cancellationToken);
            foreach (var partition in lvmResult.Volumes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                partition.FileSystem = FileSystemDetector.Detect(reader, partition, cancellationToken);
                allPartitions.Add(partition);
            }

            diagnostics.AddRange(lvmResult.Diagnostics);
            lvmVolumeCount = lvmResult.Volumes.Count;
        }

        return new ImageAnalysis(allPartitions, diagnostics, ownedReaders, lvmVolumeCount);
    }

    private void PromptRestartAsAdministrator(string path, string detail)
    {
        var result = MessageBox.Show(
            this,
            $"{detail}{Environment.NewLine}{Environment.NewLine}管理者としてアプリを再起動し、選択した物理ディスクを開きますか？",
            "物理ディスク",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2);
        if (result != DialogResult.Yes)
        {
            return;
        }

        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            MessageBox.Show(this, "実行ファイルの場所を取得できませんでした。", "管理者として再起動", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                Arguments = $"\"{path}\"",
                UseShellExecute = true,
                Verb = "runas"
            });
            Close();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            _statusLabel.Text = "管理者としての再起動をキャンセルしました";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "管理者として再起動できませんでした", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void FillHeader()
    {
        _headerList.Items.Clear();
        _analysisWarnings.Clear();
        if (_reader is null)
        {
            return;
        }

        foreach (var row in _reader.GetHeaderRows())
        {
            var item = new ListViewItem(row.Key);
            item.SubItems.Add(row.Value);
            _headerList.Items.Add(item);
        }

        RefreshWarnings();
    }

    private void RefreshWarnings()
    {
        var warnings = (_reader?.GetWarnings() ?? Array.Empty<string>())
            .Concat(_analysisWarnings)
            .ToList();
        _warningText.Text = warnings.Count == 0
            ? "警告なし"
            : string.Join(Environment.NewLine, warnings);
    }

    private void SaveAnalysisReport()
    {
        if (_reader is null)
        {
            MessageBox.Show(this, "先にディスクイメージを開いてください。", "解析レポート", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dialog = new SaveFileDialog
        {
            Filter = "JSON (*.json)|*.json",
            FileName = $"{Path.GetFileName(_pathBox.Text.TrimEnd(Path.DirectorySeparatorChar))}-analysis.json",
            Title = "解析レポートを保存"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            AnalysisReportWriter.Write(dialog.FileName, _reader, _partitions, _analysisWarnings);
            _statusLabel.Text = $"解析レポート保存: {dialog.FileName}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "解析レポート保存エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SelectQcow2Snapshot()
    {
        if (_reader is not Qcow2Reader qcow2 || qcow2.Snapshots.Count == 0)
        {
            MessageBox.Show(this, "このイメージには選択可能なqcow2内部スナップショットがありません。", "スナップショット", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dialog = new Form
        {
            Text = "qcow2スナップショット",
            Width = 620,
            Height = 380,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false
        };
        var list = new ListBox { Dock = DockStyle.Fill };
        list.Items.Add("現在のアクティブイメージ");
        foreach (var snapshot in qcow2.Snapshots)
        {
            list.Items.Add($"{snapshot.TimestampUtc:yyyy-MM-dd HH:mm:ss} UTC  {snapshot.Name}  ({snapshot.Id})");
        }
        list.SelectedIndex = qcow2.ActiveSnapshotIndex.HasValue ? qcow2.ActiveSnapshotIndex.Value + 1 : 0;

        var ok = new Button { Text = "選択", DialogResult = DialogResult.OK, Width = 90 };
        var cancel = new Button { Text = "キャンセル", DialogResult = DialogResult.Cancel, Width = 90 };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        dialog.Controls.Add(list);
        dialog.Controls.Add(buttons);
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;

        if (dialog.ShowDialog(this) != DialogResult.OK || list.SelectedIndex < 0)
        {
            return;
        }

        DisposeFileSystems();
        DisposePartitionReaders();
        _partitions.Clear();
        qcow2.SelectSnapshot(list.SelectedIndex == 0 ? null : list.SelectedIndex - 1);
        FillHeader();
        AnalyzePartitions();
        _statusLabel.Text = list.SelectedIndex == 0
            ? "アクティブイメージを選択しました"
            : $"スナップショットを選択しました: {qcow2.Snapshots[list.SelectedIndex - 1].Name}";
    }

    private void SelectVmaDisk()
    {
        if (_reader is not VmaDiskImageReader vma)
        {
            MessageBox.Show(this, "現在のイメージはVMAではありません。", "VMAディスク", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dialog = new Form
        {
            Text = "VMA内の仮想ディスク",
            Width = 680,
            Height = 380,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false
        };
        var list = new ListBox { Dock = DockStyle.Fill };
        foreach (var device in vma.Devices)
        {
            list.Items.Add(device);
        }

        list.SelectedIndex = vma.ActiveDeviceIndex;
        var ok = new Button { Text = "選択", DialogResult = DialogResult.OK, Width = 90 };
        var cancel = new Button { Text = "キャンセル", DialogResult = DialogResult.Cancel, Width = 90 };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 46,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(8)
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        dialog.Controls.Add(list);
        dialog.Controls.Add(buttons);
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;

        if (dialog.ShowDialog(this) != DialogResult.OK || list.SelectedIndex < 0)
        {
            return;
        }

        DisposeFileSystems();
        DisposePartitionReaders();
        _partitions.Clear();
        vma.SelectDevice(list.SelectedIndex);
        FillHeader();
        AnalyzePartitions();
        _statusLabel.Text = $"VMAディスクを選択しました: {vma.ActiveDevice.Name}";
    }

    private void SelectOvaDisk()
    {
        if (_reader is not OvaDiskImageReader ova)
        {
            MessageBox.Show(this, "現在のイメージはOVAではありません。", "OVAディスク", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dialog = new Form
        {
            Text = "OVA内の仮想ディスク",
            Width = 680,
            Height = 380,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false
        };
        var list = new ListBox { Dock = DockStyle.Fill };
        foreach (var disk in ova.Disks)
        {
            list.Items.Add(disk);
        }

        list.SelectedIndex = ova.ActiveDiskIndex;
        var ok = new Button { Text = "選択", DialogResult = DialogResult.OK, Width = 90 };
        var cancel = new Button { Text = "キャンセル", DialogResult = DialogResult.Cancel, Width = 90 };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 46,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(8)
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        dialog.Controls.Add(list);
        dialog.Controls.Add(buttons);
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;

        if (dialog.ShowDialog(this) != DialogResult.OK || list.SelectedIndex < 0 || list.SelectedIndex == ova.ActiveDiskIndex)
        {
            return;
        }

        DisposeFileSystems();
        DisposePartitionReaders();
        _partitions.Clear();
        ova.SelectDisk(list.SelectedIndex);
        FillHeader();
        AnalyzePartitions();
        _statusLabel.Text = $"OVAディスクを選択しました: {ova.ActiveDisk.ArchivePath}";
    }

    private void AnalyzePartitions()
    {
        _partitionGrid.Rows.Clear();
        _tree.Nodes.Clear();
        ResetNavigationHistory();
        _fileList.Items.Clear();
        _previewText.Clear();
        if (_reader is null)
        {
            return;
        }

        var discovered = PartitionTableReader.ReadPartitions(_reader).ToList();
        if (discovered.Count == 0 && _reader.Length >= 512)
        {
            discovered.Add(new PartitionInfo
            {
                Number = 1,
                Scheme = "WholeDisk",
                Name = "Whole disk",
                Type = "Unpartitioned",
                TypeId = "",
                StartLba = 0,
                SectorCount = checked((ulong)(_reader.Length / 512))
            });
        }

        foreach (var partition in discovered)
        {
            partition.FileSystem = FileSystemDetector.Detect(_reader, partition);
            AddPartitionRow(partition);
        }

        var lvmPartitions = discovered
            .Where(partition => partition.FileSystem.StartsWith("LVM2", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (lvmPartitions.Count > 0)
        {
            var lvmResult = LogicalVolumeDiscoverer.Discover(
                _reader,
                lvmPartitions,
                _partitions.Count + 1,
                _partitionReaders);
            foreach (var partition in lvmResult.Volumes)
            {
                partition.FileSystem = FileSystemDetector.Detect(_reader, partition);
                AddPartitionRow(partition);
            }

            _analysisWarnings.AddRange(lvmResult.Diagnostics.Select(diagnostic => diagnostic.Message));
            RefreshWarnings();

            var errors = lvmResult.Diagnostics.Where(diagnostic => diagnostic.IsError).ToList();
            if (lvmResult.Volumes.Count == 0 && errors.Count > 0)
            {
                MessageBox.Show(
                    this,
                    string.Join(Environment.NewLine, errors.Select(error => error.Message)),
                    "LVM2を解析できませんでした",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }

        if (_partitions.Count == 0)
        {
            _statusLabel.Text = "パーティションなし";
        }

        RefreshUefiVariables();
        RefreshTpmState();
    }

    private void ApplyPartitionAnalysis(IReadOnlyList<PartitionInfo> partitions)
    {
        _partitionGrid.Rows.Clear();
        _tree.Nodes.Clear();
        ResetNavigationHistory();
        _fileList.Items.Clear();
        _previewText.Clear();

        foreach (var partition in partitions)
        {
            AddPartitionRow(partition);
        }

        if (_partitions.Count == 0)
        {
            _statusLabel.Text = "パーティションなし";
        }

        RefreshUefiVariables();
        RefreshTpmState();
    }

    private void RefreshUefiVariables()
    {
        _currentUefiVariableStore = null;
        _uefiVariableGrid.Rows.Clear();
        if (_reader is null)
        {
            _uefiVariableDetails.Text = "ディスクイメージを開いてください。";
            return;
        }

        if (!UefiVariableStoreReader.TryRead(_reader, out var store, out var error) || store is null)
        {
            _uefiVariableDetails.Text = _reader is VmaDiskImageReader vma
                ? $"選択中のVMAデバイス「{vma.ActiveDevice.Name}」はUEFI変数ストアではありません。"
                    + $"{Environment.NewLine}ツールバーの「VMAディスク」からefidiskを選択してください。"
                    + $"{Environment.NewLine}{Environment.NewLine}判定結果: {error}"
                : $"UEFI変数ストアを検出できませんでした。{Environment.NewLine}{Environment.NewLine}判定結果: {error}";
            return;
        }

        _currentUefiVariableStore = store;

        var deviceName = _reader is VmaDiskImageReader currentVma
            ? currentVma.ActiveDevice.Name
            : Path.GetFileName(_reader.Path);
        var lines = new List<string>
        {
            $"デバイス: {deviceName}",
            $"Firmware Volume GUID: {store.FirmwareVolumeGuid}",
            $"Firmware Volumeサイズ: {store.FirmwareVolumeLength:N0} bytes",
            $"変数ストア: {(store.Authenticated ? "認証付き" : "通常")}",
            $"現行変数数: {store.Variables.Count(variable => variable.IsActive):N0}",
            $"全レコード数: {store.Variables.Count:N0}"
        };
        if (store.Warnings.Count > 0)
        {
            lines.Add("");
            lines.AddRange(store.Warnings.Select(warning => $"警告: {warning}"));
        }

        _uefiVariableDetails.Text = string.Join(Environment.NewLine, lines);
        PopulateUefiVariableRows();
    }

    private void PopulateUefiVariableRows()
    {
        _uefiVariableGrid.Rows.Clear();
        if (_currentUefiVariableStore is null)
        {
            return;
        }

        var variables = _showInactiveUefiVariables.Checked
            ? _currentUefiVariableStore.Variables
            : _currentUefiVariableStore.Variables.Where(variable => variable.IsActive);
        foreach (var variable in variables)
        {
            var index = _uefiVariableGrid.Rows.Add(
                variable.Name,
                variable.VendorGuid,
                variable.StateText,
                UefiVariableStoreReader.FormatAttributes(variable.Attributes),
                $"{variable.Data.Length:N0} bytes",
                variable.Summary);
            _uefiVariableGrid.Rows[index].Tag = variable;
        }

        if (_uefiVariableGrid.Rows.Count > 0)
        {
            _uefiVariableGrid.Rows[0].Selected = true;
        }
    }

    private void ShowSelectedUefiVariable()
    {
        if (_uefiVariableGrid.CurrentRow?.Tag is UefiVariable variable)
        {
            _uefiVariableDetails.Text = UefiVariableStoreReader.Describe(variable);
        }
    }

    private void RefreshTpmState()
    {
        _currentTpmStateStore = null;
        _tpmStateGrid.Rows.Clear();
        if (_reader is null)
        {
            _tpmStateDetails.Text = "ディスクイメージを開いてください。";
            return;
        }

        if (!SwtpmStateReader.TryRead(_reader, out var store, out var error) || store is null)
        {
            _tpmStateDetails.Text = _reader is VmaDiskImageReader vma
                ? $"選択中のVMAデバイス「{vma.ActiveDevice.Name}」はswtpm状態ストアではありません。"
                    + $"{Environment.NewLine}ツールバーの「VMAディスク」からtpmstateを選択してください。"
                    + $"{Environment.NewLine}{Environment.NewLine}判定結果: {error}"
                : $"swtpm状態ストアを検出できませんでした。{Environment.NewLine}{Environment.NewLine}判定結果: {error}";
            return;
        }

        _currentTpmStateStore = store;
        foreach (var section in store.Sections)
        {
            var blob = section.Blob;
            var rowIndex = _tpmStateGrid.Rows.Add(
                section.Index,
                section.Name,
                $"0x{section.Offset:X}",
                $"{section.DataLength:N0} bytes",
                $"{section.SectionLength:N0} bytes",
                blob is null ? "不明" : $"v{blob.Version}",
                blob is null ? "判定不可" : SwtpmStateReader.FormatEncryption(blob),
                blob?.Tlvs.Count.ToString("N0") ?? "-");
            _tpmStateGrid.Rows[rowIndex].Tag = section;
        }

        var deviceName = _reader is VmaDiskImageReader currentVma
            ? currentVma.ActiveDevice.Name
            : Path.GetFileName(_reader.Path);
        var lines = new List<string>
        {
            $"デバイス: {deviceName}",
            $"swtpm線形ストア version: {store.Version}",
            $"ヘッダーサイズ: {store.HeaderSize:N0} bytes",
            $"デバイスサイズ: {store.DeviceLength:N0} bytes",
            $"割り当て済み状態数: {store.Sections.Count:N0}"
        };
        if (store.Warnings.Count > 0)
        {
            lines.Add("");
            lines.AddRange(store.Warnings.Select(warning => $"警告: {warning}"));
        }

        _tpmStateDetails.Text = string.Join(Environment.NewLine, lines);
        if (_tpmStateGrid.Rows.Count > 0)
        {
            _tpmStateGrid.CurrentCell = _tpmStateGrid.Rows[0].Cells[0];
            _tpmStateGrid.Rows[0].Selected = true;
            ShowSelectedTpmState();
        }
    }

    private void ShowSelectedTpmState()
    {
        if (_currentTpmStateStore is not null
            && _tpmStateGrid.CurrentRow?.Tag is SwtpmStateSection section)
        {
            _tpmStateDetails.Text = SwtpmStateReader.Describe(_currentTpmStateStore, section);
        }
    }

    private void AddPartitionRow(PartitionInfo partition)
    {
        _partitions.Add(partition);
        _partitionGrid.Rows.Add(
            partition.Number,
            partition.Scheme,
            partition.FileSystem,
            partition.Name,
            string.IsNullOrWhiteSpace(partition.TypeId) ? partition.Type : $"{partition.Type} ({partition.TypeId})",
            partition.StartLba.ToString("N0"),
            partition.SectorCount.ToString("N0"),
            FormatBytes(partition.LengthBytes));

        var label = $"{partition.Number}: {partition.Name}";
        if (!string.IsNullOrWhiteSpace(partition.FileSystem))
        {
            label += $" [{partition.FileSystem}]";
        }

        var node = new TreeNode(label) { Tag = new PartitionNodeTag(partition) };
        node.Nodes.Add(CreateDummyNode());
        _tree.Nodes.Add(node);
    }

    private void ReadRawData()
    {
        if (_reader is null)
        {
            return;
        }

        try
        {
            var offset = ParseOffset(_offsetBox.Text);
            var length = (int)_lengthBox.Value;
            var data = new byte[length];
            _reader.ReadAt(offset, data, 0, length);
            _hexText.Text = HexFormatter.Format(data, offset);
            _statusLabel.Text = $"生データ: 0x{offset:X}";
        }
        catch (Exception ex)
        {
            _hexText.Text = ex.Message;
            _statusLabel.Text = "生データ読込失敗";
        }
    }

    private void ShowClusterLookup()
    {
        if (_reader is null)
        {
            return;
        }

        try
        {
            var offset = ParseOffset(_offsetBox.Text);
            var description = _reader.DescribeOffset(offset);
            _statusLabel.Text = $"{_reader.FormatName}: 0x{offset:X}";
            _hexText.Text = $"{description}{Environment.NewLine}{Environment.NewLine}{_hexText.Text}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "クラスタ参照エラー", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ActivateSelectedPartition()
    {
        if (_partitionGrid.CurrentRow?.Index is not int index || index < 0 || index >= _tree.Nodes.Count)
        {
            return;
        }

        _tree.SelectedNode = _tree.Nodes[index];
        _tree.Nodes[index].Expand();
    }

    private void TreeBeforeExpand(object? sender, TreeViewCancelEventArgs e)
    {
        if (e.Node is null)
        {
            return;
        }

        if (!e.Node.Nodes.Cast<TreeNode>().Any(node => node.Tag is DummyNodeTag))
        {
            return;
        }

        try
        {
            if (e.Node.Tag is PartitionNodeTag partitionTag)
            {
                var fs = EnsureFileSystem(partitionTag.Partition);
                if (fs is null)
                {
                    return;
                }

                e.Node.Tag = new DirectoryNodeTag(fs, fs.Root);
                AddDirectoryChildren(e.Node, fs, fs.Root);
            }
            else if (e.Node.Tag is DirectoryNodeTag directoryTag)
            {
                AddDirectoryChildren(e.Node, directoryTag.FileSystem, directoryTag.Node);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "ディレクトリ読込エラー", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void TreeAfterSelect(object? sender, TreeViewEventArgs e)
    {
        if (e.Node is null)
        {
            return;
        }

        if (e.Node.Tag is PartitionNodeTag partitionTag)
        {
            var fs = EnsureFileSystem(partitionTag.Partition);
            if (fs is null)
            {
                _fileList.Items.Clear();
                UpdateNavigationButtons();
                return;
            }

            e.Node.Tag = new DirectoryNodeTag(fs, fs.Root);
            AddDirectoryChildren(e.Node, fs, fs.Root);
        }

        if (e.Node.Tag is DirectoryNodeTag directoryTag)
        {
            PopulateFileList(directoryTag.FileSystem, directoryTag.Node, GetTreeNodePath(e.Node));
            if (!_isHistoryNavigation)
            {
                _navigationHistory.Record(e.Node);
            }
        }

        UpdateNavigationButtons();
    }

    private void NavigateUp()
    {
        var parent = _tree.SelectedNode?.Parent;
        if (parent is null)
        {
            return;
        }

        _tree.SelectedNode = parent;
        parent.EnsureVisible();
        UpdateNavigationButtons();
    }

    private void NavigateBack()
    {
        SelectHistoryNode(_navigationHistory.GoBack());
    }

    private void NavigateForward()
    {
        SelectHistoryNode(_navigationHistory.GoForward());
    }

    private void SelectHistoryNode(TreeNode? node)
    {
        if (node is null || node.TreeView != _tree)
        {
            UpdateNavigationButtons();
            return;
        }

        _isHistoryNavigation = true;
        try
        {
            _tree.SelectedNode = node;
            node.EnsureVisible();
        }
        finally
        {
            _isHistoryNavigation = false;
            UpdateNavigationButtons();
        }
    }

    private void ResetNavigationHistory()
    {
        _navigationHistory.Reset();
        UpdateNavigationButtons();
    }

    private void UpdateNavigationButtons()
    {
        _backNavigationButton.Enabled = _navigationHistory.CanGoBack;
        _forwardNavigationButton.Enabled = _navigationHistory.CanGoForward;
        _upNavigationButton.Enabled = _tree.SelectedNode?.Parent is not null;
    }

    private void AddDirectoryChildren(TreeNode treeNode, IReadOnlyFileSystem fileSystem, VfsNode directory)
    {
        for (var index = treeNode.Nodes.Count - 1; index >= 0; index--)
        {
            if (treeNode.Nodes[index].Tag is DummyNodeTag)
            {
                treeNode.Nodes.RemoveAt(index);
            }
        }

        foreach (var child in fileSystem.ListDirectory(directory).Where(n => n.IsDirectory))
        {
            var existing = treeNode.Nodes
                .Cast<TreeNode>()
                .FirstOrDefault(node => node.Tag is DirectoryNodeTag tag
                    && ReferenceEquals(tag.FileSystem, fileSystem)
                    && IsSameVfsNode(tag.Node, child));
            if (existing is not null)
            {
                continue;
            }

            var childNode = new TreeNode(child.DisplayName) { Tag = new DirectoryNodeTag(fileSystem, child) };
            childNode.Nodes.Add(CreateDummyNode());
            treeNode.Nodes.Add(childNode);
        }
    }

    private void PopulateFileList(IReadOnlyFileSystem fileSystem, VfsNode directory, string directoryPath)
    {
        _currentFileSystem = fileSystem;
        _currentDirectory = directory;
        _currentDirectoryPath = VirtualPath.Normalize(directoryPath);
        _fileList.Items.Clear();
        _previewText.Clear();

        foreach (var node in fileSystem.ListDirectory(directory))
        {
            var item = new ListViewItem(node.DisplayName) { Tag = node };
            item.SubItems.Add(node.IsDirectory ? "" : FormatBytes(node.Size));
            item.SubItems.Add(node.ModifiedUtc?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "");
            item.SubItems.Add(FormatNodeType(node));
            item.SubItems.Add(VirtualPath.Combine(_currentDirectoryPath, node.DisplayName));
            _fileList.Items.Add(item);
        }
    }

    private async Task SearchCurrentFileSystemAsync()
    {
        if (_currentFileSystem is null || string.IsNullOrWhiteSpace(_searchBox.Text))
        {
            return;
        }

        _searchCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource();
        _searchCancellation = cancellation;
        var token = cancellation.Token;
        _cancelSearchButton.Enabled = true;
        _statusLabel.Text = "検索中...";
        var progress = new Progress<int>(count =>
        {
            if (ReferenceEquals(_searchCancellation, cancellation))
            {
                _statusLabel.Text = $"検索中: {count:N0} フォルダー";
            }
        });

        try
        {
            var fileSystem = _currentFileSystem;
            var query = _searchBox.Text.Trim();
            var matches = await Task.Run(() => FileSystemSearch.Search(fileSystem, query, progress, token), token);
            if (!ReferenceEquals(_searchCancellation, cancellation))
            {
                return;
            }

            _fileList.BeginUpdate();
            _fileList.Items.Clear();
            _previewText.Clear();
            foreach (var match in matches)
            {
                var node = match.Node;
                var item = new ListViewItem(node.DisplayName) { Tag = node };
                item.SubItems.Add(node.IsDirectory ? "" : FormatBytes(node.Size));
                item.SubItems.Add(node.ModifiedUtc?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "");
                item.SubItems.Add(FormatNodeType(node));
                item.SubItems.Add(match.Path);
                _fileList.Items.Add(item);
            }
            _fileList.EndUpdate();
            _statusLabel.Text = $"検索結果: {matches.Count:N0} 件";
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_searchCancellation, cancellation))
            {
                _statusLabel.Text = "検索をキャンセルしました";
            }
        }
        finally
        {
            if (ReferenceEquals(_searchCancellation, cancellation))
            {
                _searchCancellation = null;
                _cancelSearchButton.Enabled = false;
            }
        }
    }

    private void ShowSelectedItemProperties()
    {
        if (_fileList.SelectedItems.Count != 1 || _fileList.SelectedItems[0].Tag is not VfsNode node)
        {
            return;
        }

        var location = GetListItemPath(_fileList.SelectedItems[0]);
        _previewText.Text = string.Join(Environment.NewLine, new[]
        {
            $"名前: {node.DisplayName}",
            $"種類: {(node.IsDirectory ? "フォルダー" : "ファイル")}",
            $"サイズ: {(node.IsDirectory ? "-" : $"{node.Size:N0} bytes ({FormatBytes(node.Size)})")}",
            $"更新日時 UTC: {node.ModifiedUtc?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "-"}",
            $"属性: {FormatAttributes(node.Attributes)}",
            string.IsNullOrWhiteSpace(location) ? "" : $"場所: {location}",
            $"ファイルシステム: {_currentFileSystem?.Name ?? "-"}"
        }.Where(line => line.Length > 0));
    }

    private void ShowDeletedNtfsFiles()
    {
        if (_reader is null)
        {
            return;
        }

        var partition = _currentFileSystem?.Partition ?? GetSelectedPartitionForMount();
        if (partition is null || !partition.FileSystem.Contains("NTFS", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "NTFSパーティションを選択してください。", "削除済みファイル", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            Cursor = Cursors.WaitCursor;
            var source = partition.ReaderOverride ?? _reader;
            var scanPartition = partition;
            if (_currentFileSystem is BitLockerFileSystem bitLocker
                && bitLocker.InnerFileSystemName.Contains("NTFS", StringComparison.OrdinalIgnoreCase))
            {
                source = bitLocker.DecryptedReader;
                scanPartition = bitLocker.DecryptedPartition;
            }

            var deleted = new NtfsFileSystem(new PartitionSliceReader(source, scanPartition), scanPartition, deletedOnly: true);
            PopulateFileList(deleted, deleted.Root, "/");
            _statusLabel.Text = $"削除済みNTFSレコード: {_fileList.Items.Count:N0} 件";
            MessageBox.Show(
                this,
                "削除済みMFTレコードを表示しています。削除後にクラスタが再利用されている場合、コピー内容は元ファイルと一致しないことがあります。",
                "削除済みファイル",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "削除済みファイル検出エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            Cursor = Cursors.Default;
        }
    }

    private IReadOnlyFileSystem? EnsureFileSystem(PartitionInfo partition)
    {
        if (_reader is null)
        {
            return null;
        }

        if (_fileSystems.TryGetValue(partition.Number, out var cached))
        {
            return cached;
        }

        Cursor = Cursors.WaitCursor;
        try
        {
            var fs = FileSystemDetector.TryOpen(_reader, partition, out var error);
            if (fs is null && TryReadBitLockerRecoveryMetadata(partition, out var metadata))
            {
                Cursor = Cursors.Default;
                while (TryPromptForBitLockerRecoveryPassword(metadata, out var recoveryPasswordKey))
                {
                    try
                    {
                        Cursor = Cursors.WaitCursor;
                        fs = FileSystemDetector.TryOpen(_reader, partition, recoveryPasswordKey, out error);
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(recoveryPasswordKey);
                    }

                    if (fs is not null)
                    {
                        break;
                    }

                    Cursor = Cursors.Default;
                    var retry = MessageBox.Show(
                        this,
                        $"{error}{Environment.NewLine}{Environment.NewLine}回復パスワードを再入力しますか？",
                        "BitLocker解除失敗",
                        MessageBoxButtons.RetryCancel,
                        MessageBoxIcon.Warning);
                    if (retry != DialogResult.Retry)
                    {
                        _statusLabel.Text = "BitLocker解除をキャンセルしました";
                        return null;
                    }
                }

                if (fs is null)
                {
                    _statusLabel.Text = "BitLocker解除をキャンセルしました";
                    return null;
                }
            }

            if (fs is null)
            {
                MessageBox.Show(this, error, "ファイルシステム未対応", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return null;
            }

            _fileSystems[partition.Number] = fs;
            _statusLabel.Text = $"{partition.Number}: {fs.Name}";
            return fs;
        }
        finally
        {
            Cursor = Cursors.Default;
        }
    }

    private bool TryReadBitLockerRecoveryMetadata(PartitionInfo partition, out BitLockerMetadata metadata)
    {
        metadata = null!;
        if (_reader is null
            || !partition.FileSystem.StartsWith("BitLocker/FVE", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var slice = new PartitionSliceReader(_reader, partition);
        if (!BitLockerMetadataReader.TryRead(slice, out var parsed, out _)
            || parsed is null
            || !parsed.HasRecoveryPasswordProtector)
        {
            return false;
        }

        metadata = parsed;
        return true;
    }

    private bool TryPromptForBitLockerRecoveryPassword(
        BitLockerMetadata metadata,
        out byte[] recoveryPasswordKey)
    {
        recoveryPasswordKey = Array.Empty<byte>();
        byte[]? decodedKey = null;

        using var dialog = new Form
        {
            Text = "BitLocker回復パスワード",
            ClientSize = new Size(620, 230),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.CenterParent
        };
        var protectorIds = metadata.KeyProtectors
            .Where(protector => protector.ProtectionType == BitLockerProtectionType.RecoveryPassword)
            .Select(protector => protector.Identifier.ToString("B"))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var explanation = new Label
        {
            AutoSize = false,
            Location = new Point(16, 14),
            Size = new Size(588, 62),
            Text = $"このボリュームはBitLockerで保護されています。48桁の回復パスワードを入力してください。{Environment.NewLine}" +
                $"形式: 000000-000000-000000-000000-000000-000000-000000-000000{Environment.NewLine}" +
                $"回復キーID: {string.Join(", ", protectorIds)}"
        };
        var passwordBox = new TextBox
        {
            Location = new Point(16, 88),
            Size = new Size(588, 27),
            UseSystemPasswordChar = true,
            MaxLength = 96
        };
        var showPassword = new CheckBox
        {
            AutoSize = true,
            Location = new Point(16, 126),
            Text = "入力内容を表示"
        };
        var okButton = new Button
        {
            Location = new Point(420, 178),
            Size = new Size(88, 32),
            Text = "解除"
        };
        var cancelButton = new Button
        {
            DialogResult = DialogResult.Cancel,
            Location = new Point(516, 178),
            Size = new Size(88, 32),
            Text = "キャンセル"
        };

        showPassword.CheckedChanged += (_, _) => passwordBox.UseSystemPasswordChar = !showPassword.Checked;
        okButton.Click += (_, _) =>
        {
            if (!BitLockerRecoveryPassword.TryDecode(passwordBox.Text, out var candidate, out var validationError))
            {
                MessageBox.Show(dialog, validationError, "回復パスワードの確認", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                passwordBox.Focus();
                passwordBox.SelectAll();
                return;
            }

            decodedKey = candidate;
            dialog.DialogResult = DialogResult.OK;
            dialog.Close();
        };
        dialog.FormClosed += (_, _) => passwordBox.Clear();
        dialog.Controls.AddRange([explanation, passwordBox, showPassword, okButton, cancelButton]);
        dialog.AcceptButton = okButton;
        dialog.CancelButton = cancelButton;
        dialog.Shown += (_, _) => passwordBox.Focus();

        if (dialog.ShowDialog(this) != DialogResult.OK || decodedKey is null)
        {
            if (decodedKey is not null)
            {
                CryptographicOperations.ZeroMemory(decodedKey);
            }

            return false;
        }

        recoveryPasswordKey = decodedKey;
        return true;
    }

    private void DisposeFileSystems()
    {
        foreach (var disposable in _fileSystems.Values.OfType<IDisposable>())
        {
            disposable.Dispose();
        }

        _fileSystems.Clear();
    }

    private void DisposePartitionReaders()
    {
        foreach (var reader in _partitionReaders)
        {
            reader.Dispose();
        }

        _partitionReaders.Clear();
    }

    private async Task OpenSelectedListItemAsync()
    {
        if (_fileList.SelectedItems.Count == 0 || _fileList.SelectedItems[0].Tag is not VfsNode node)
        {
            return;
        }

        if (node.IsDirectory)
        {
            OpenDirectoryFromList(node);
        }
        else
        {
            if (!await OpenSelectedFilePreviewAsync(showUnsupportedMessage: false))
            {
                PreviewSelectedFile();
            }
        }
    }

    private void OpenDirectoryFromList(VfsNode node)
    {
        if (_tree.SelectedNode is null || _currentFileSystem is null)
        {
            return;
        }

        if (_currentDirectory is not null)
        {
            AddDirectoryChildren(_tree.SelectedNode, _currentFileSystem, _currentDirectory);
        }

        foreach (TreeNode child in _tree.SelectedNode.Nodes)
        {
            if (child.Tag is DirectoryNodeTag tag
                && ReferenceEquals(tag.FileSystem, _currentFileSystem)
                && IsSameVfsNode(tag.Node, node))
            {
                _tree.SelectedNode = child;
                child.Expand();
                return;
            }
        }

        var newNode = new TreeNode(node.DisplayName) { Tag = new DirectoryNodeTag(_currentFileSystem, node) };
        newNode.Nodes.Add(CreateDummyNode());
        _tree.SelectedNode.Nodes.Add(newNode);
        _tree.SelectedNode = newNode;
        newNode.Expand();
    }

    private void ShowSelectedItemContainingDirectory()
    {
        if (_currentFileSystem is null
            || _fileList.SelectedItems.Count != 1
            || _fileList.SelectedItems[0].Tag is not VfsNode selectedNode)
        {
            return;
        }

        var selectedPath = GetListItemPath(_fileList.SelectedItems[0]);
        if (selectedPath.Length == 0)
        {
            return;
        }

        try
        {
            var fileSystem = _currentFileSystem;
            var directoryPath = VirtualPath.GetParent(selectedPath);
            var rootTreeNode = _tree.Nodes
                .Cast<TreeNode>()
                .FirstOrDefault(node => node.Tag is DirectoryNodeTag tag
                    && ReferenceEquals(tag.FileSystem, fileSystem)
                    && IsSameVfsNode(tag.Node, fileSystem.Root));
            if (rootTreeNode is null)
            {
                throw new InvalidOperationException("対象パーティションのルートフォルダーが見つかりません。");
            }

            var targetTreeNode = rootTreeNode;
            var targetDirectory = fileSystem.Root;
            foreach (var segment in VirtualPath.Split(directoryPath))
            {
                AddDirectoryChildren(targetTreeNode, fileSystem, targetDirectory);
                var childTreeNode = targetTreeNode.Nodes
                    .Cast<TreeNode>()
                    .FirstOrDefault(node => node.Tag is DirectoryNodeTag tag
                        && ReferenceEquals(tag.FileSystem, fileSystem)
                        && string.Equals(tag.Node.DisplayName, segment, StringComparison.Ordinal));
                if (childTreeNode?.Tag is not DirectoryNodeTag childTag)
                {
                    throw new DirectoryNotFoundException($"フォルダーが見つかりません: {directoryPath}");
                }

                targetTreeNode = childTreeNode;
                targetDirectory = childTag.Node;
            }

            if (ReferenceEquals(_tree.SelectedNode, targetTreeNode))
            {
                PopulateFileList(fileSystem, targetDirectory, directoryPath);
            }
            else
            {
                _tree.SelectedNode = targetTreeNode;
            }

            targetTreeNode.Expand();
            targetTreeNode.EnsureVisible();

            var targetItem = _fileList.Items
                .Cast<ListViewItem>()
                .FirstOrDefault(item => item.Tag is VfsNode node
                    && string.Equals(GetListItemPath(item), selectedPath, StringComparison.Ordinal)
                    && IsSameVfsNode(node, selectedNode));
            if (targetItem is not null)
            {
                targetItem.Selected = true;
                targetItem.Focused = true;
                targetItem.EnsureVisible();
            }

            _statusLabel.Text = $"保存フォルダーを表示: {directoryPath}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "保存フォルダーを表示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static string GetListItemPath(ListViewItem item)
    {
        return item.SubItems.Count > 4 ? item.SubItems[4].Text : "";
    }

    private static string GetTreeNodePath(TreeNode node)
    {
        var segments = new Stack<string>();
        for (var current = node; current.Parent is not null; current = current.Parent)
        {
            segments.Push(current.Text);
        }

        return segments.Count == 0 ? "/" : "/" + string.Join('/', segments);
    }

    private void PreviewSelectedFile()
    {
        if (_currentFileSystem is null || _fileList.SelectedItems.Count == 0 || _fileList.SelectedItems[0].Tag is not VfsNode node || node.IsDirectory)
        {
            return;
        }

        try
        {
            var data = _currentFileSystem.ReadFile(node, 0, (int)Math.Min(node.Size, 64 * 1024));
            _previewText.Text = HexFormatter.Format(data, 0);
            _statusLabel.Text = $"{node.Name}: {FormatBytes(data.Length)} preview";
        }
        catch (Exception ex)
        {
            _previewText.Text = ex.Message;
        }
    }

    private async Task CopySelectedItemsAsync()
    {
        if (_currentFileSystem is null)
        {
            return;
        }

        var nodes = _fileList.SelectedItems
            .Cast<ListViewItem>()
            .Select(item => item.Tag)
            .OfType<VfsNode>()
            .ToList();
        if (nodes.Count == 0)
        {
            MessageBox.Show(this, "コピーするファイルまたはフォルダを選択してください。", "コピー", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        await CopyNodesToHostAsync(_currentFileSystem, nodes);
    }

    private async Task CopyCurrentDirectoryAsync()
    {
        if (_currentFileSystem is null || _currentDirectory is null)
        {
            return;
        }

        await CopyNodesToHostAsync(_currentFileSystem, new[] { _currentDirectory });
    }

    private async Task CopyNodesToHostAsync(IReadOnlyFileSystem fileSystem, IReadOnlyList<VfsNode> nodes)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "コピー先フォルダを選択してください",
            UseDescriptionForTitle = true
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var destinationPath = dialog.SelectedPath;
        var cancellation = new CancellationTokenSource();
        _copyCancellations.Add(cancellation);
        _cancelCopyButton.Enabled = true;
        var progress = new Progress<CopyProgress>(p =>
        {
            if (ReferenceEquals(_copyProgressOwner, cancellation))
            {
                if (p.TotalBytes > 0)
                {
                    _copyProgressBar.Style = ProgressBarStyle.Blocks;
                    _copyProgressBar.Value = (int)Math.Clamp(
                        (double)p.BytesCopied / p.TotalBytes * 100,
                        _copyProgressBar.Minimum,
                        _copyProgressBar.Maximum);
                }

                var name = Path.GetFileName(p.CurrentPath);
                var transferred = p.TotalBytes > 0
                    ? $"{FormatBytes(p.BytesCopied)} / {FormatBytes(p.TotalBytes)}"
                    : FormatBytes(p.BytesCopied);
                var performance = "速度計測中";
                if (p.BytesCopied > 0 && p.Elapsed.TotalSeconds >= 0.1)
                {
                    var bytesPerSecond = p.BytesCopied / p.Elapsed.TotalSeconds;
                    var formattedSpeed = FormatBytes((long)Math.Min(bytesPerSecond, long.MaxValue));
                    var remainingBytes = Math.Max(0, p.TotalBytes - p.BytesCopied);
                    performance = remainingBytes > 0 && bytesPerSecond > 0
                        ? $"{formattedSpeed}/秒、残り約{FormatDuration(remainingBytes / bytesPerSecond)}"
                        : $"{formattedSpeed}/秒";
                }

                _statusLabel.Text =
                    $"コピー中 ({_copyCancellations.Count:N0}件): {name} {transferred}、{performance}";
            }
        });

        try
        {
            if (_copyCancellations.Count > 1)
            {
                _statusLabel.Text = $"コピー待機中: {_copyCancellations.Count:N0}件";
            }

            await _copyExecutionGate.WaitAsync(cancellation.Token);
            CopyResult result;
            try
            {
                _copyProgressOwner = cancellation;
                _copyProgressBar.Value = 0;
                _copyProgressBar.Style = ProgressBarStyle.Marquee;
                _copyProgressBar.MarqueeAnimationSpeed = 30;
                _copyProgressBar.Visible = true;
                _statusLabel.Text = $"コピー準備中 ({_copyCancellations.Count:N0}件): 合計サイズを計算しています...";
                result = await Task.Run(() => FileSystemExporter.CopyNodes(
                    fileSystem,
                    nodes,
                    destinationPath,
                    progress,
                    cancellation.Token,
                    new CopyOptions(ContinueOnError: true)), cancellation.Token);
            }
            finally
            {
                _copyExecutionGate.Release();
            }

            _statusLabel.Text = result.Errors.Count == 0
                ? $"コピー完了: {result.FilesCopied:N0} files, {FormatBytes(result.BytesCopied)}"
                : $"コピー完了: {result.FilesCopied:N0} files, エラー {result.Errors.Count:N0}件";
        }
        catch (OperationCanceledException)
        {
            _statusLabel.Text = "コピーをキャンセルしました";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "コピーエラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _statusLabel.Text = "コピー失敗";
        }
        finally
        {
            _copyCancellations.Remove(cancellation);
            if (ReferenceEquals(_copyProgressOwner, cancellation))
            {
                _copyProgressOwner = null;
                _copyProgressBar.Value = 0;
                _copyProgressBar.Style = ProgressBarStyle.Marquee;
                _copyProgressBar.Visible = _copyCancellations.Count > 0;
            }

            cancellation.Dispose();
            _cancelCopyButton.Enabled = _copyCancellations.Count > 0;
            if (_copyCancellations.Count > 0)
            {
                _statusLabel.Text = $"コピー処理中: {_copyCancellations.Count:N0}件";
            }
        }
    }

    private void CancelCopyOperations()
    {
        foreach (var cancellation in _copyCancellations.ToArray())
        {
            cancellation.Cancel();
        }

        if (_copyCancellations.Count > 0)
        {
            _statusLabel.Text = $"コピーをキャンセル中: {_copyCancellations.Count:N0}件";
        }
    }

    private void MountSelectedPartition()
    {
        var partition = GetSelectedPartitionForMount();
        if (partition is null)
        {
            MessageBox.Show(this, "マウントするパーティションを選択してください。", "マウント", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (!ProjFsFeature.IsLibraryPresent && !ProjFsFeature.PromptAndEnable(this))
        {
            return;
        }

        var fileSystem = EnsureFileSystem(partition);
        if (fileSystem is null)
        {
            return;
        }

        using var dialog = new FolderBrowserDialog
        {
            Description = "マウント先の空フォルダを選択してください。",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            Cursor = Cursors.WaitCursor;
            var mount = ProjectedFileSystemMount.Start(fileSystem, dialog.SelectedPath);
            _mounts.Add(mount);
            RefreshMountList();
            _statusLabel.Text = $"マウント開始: {dialog.SelectedPath}";
            mount.OpenInExplorer();
        }
        catch (ProjFsUnavailableException ex)
        {
            if (MessageBox.Show(this, $"{ex.Message}{Environment.NewLine}{Environment.NewLine}ProjFS を有効化しますか？", "ProjFS", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                ProjFsFeature.PromptAndEnable(this);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "マウントエラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            Cursor = Cursors.Default;
        }
    }

    private PartitionInfo? GetSelectedPartitionForMount()
    {
        if (_currentFileSystem is not null)
        {
            return _currentFileSystem.Partition;
        }

        if (_tree.SelectedNode?.Tag is DirectoryNodeTag directoryTag)
        {
            return directoryTag.FileSystem.Partition;
        }

        if (_tree.SelectedNode?.Tag is PartitionNodeTag treePartitionTag)
        {
            return treePartitionTag.Partition;
        }

        if (_partitionGrid.CurrentRow?.Index is int index && index >= 0 && index < _partitions.Count)
        {
            return _partitions[index];
        }

        return null;
    }

    private void OpenSelectedMountFolder()
    {
        foreach (ListViewItem item in _mountList.SelectedItems)
        {
            if (item.Tag is ProjectedFileSystemMount mount)
            {
                mount.OpenInExplorer();
            }
        }
    }

    private void UnmountSelectedMounts()
    {
        var selected = _mountList.SelectedItems
            .Cast<ListViewItem>()
            .Select(item => item.Tag)
            .OfType<ProjectedFileSystemMount>()
            .ToList();
        if (selected.Count == 0)
        {
            return;
        }

        foreach (var mount in selected)
        {
            mount.Dispose();
            _mounts.Remove(mount);
        }

        RefreshMountList();
    }

    private void UnmountAllMountsWithPrompt()
    {
        ConfirmAndDisposeMounts("すべてのマウントを解除します。続行しますか？");
    }

    private bool ConfirmAndDisposeMounts(string message)
    {
        if (_mounts.Count == 0)
        {
            return true;
        }

        var active = _mounts.Count(m => m.HasPossibleExternalUse);
        var detail = active > 0
            ? $"{active} 件のマウントは Explorer などから使用中の可能性があります。"
            : "現在アクティブな読み取り通知はありません。";
        var result = MessageBox.Show(
            this,
            $"{message}{Environment.NewLine}{Environment.NewLine}{detail}",
            "マウント解除",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);
        if (result != DialogResult.Yes)
        {
            return false;
        }

        DisposeMounts();
        return true;
    }

    private void DisposeMounts()
    {
        foreach (var mount in _mounts.ToList())
        {
            mount.Dispose();
        }

        _mounts.Clear();
        RefreshMountList();
    }

    private void RefreshMountList()
    {
        _mountList.Items.Clear();
        foreach (var mount in _mounts)
        {
            var item = new ListViewItem($"#{mount.FileSystem.Partition.Number}") { Tag = mount };
            item.SubItems.Add(mount.FileSystem.Name);
            item.SubItems.Add(mount.RootPath);
            item.SubItems.Add(mount.HasPossibleExternalUse
                ? $"使用中の可能性あり open={mount.OpenHandleCount}, callbacks={mount.ActiveCallbackCount}"
                : "待機中");
            _mountList.Items.Add(item);
        }
    }

    private void Form1FormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!ConfirmAndDisposeMounts("アプリ終了前にマウントを解除します。続行しますか？"))
        {
            e.Cancel = true;
            return;
        }

        if (_isLoadingImage)
        {
            _closeAfterLoadCancellation = true;
            CancelImageLoad();
            e.Cancel = true;
            return;
        }

        _loadCancellation?.Cancel();
    }

    private static TreeNode CreateDummyNode()
    {
        return new TreeNode("...") { Tag = new DummyNodeTag() };
    }

    private static long ParseOffset(string text)
    {
        text = text.Trim().Replace("_", "", StringComparison.Ordinal);
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return long.Parse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        return long.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
    }

    private static string FormatBytes(long value)
    {
        string[] suffixes = { "B", "KB", "MB", "GB", "TB", "PB" };
        double size = value;
        var suffix = 0;
        while (size >= 1024 && suffix < suffixes.Length - 1)
        {
            size /= 1024;
            suffix++;
        }

        return $"{size:0.##} {suffixes[suffix]}";
    }

    private static string FormatDuration(double seconds)
    {
        seconds = Math.Max(1, Math.Ceiling(seconds));
        if (seconds < 60)
        {
            return $"{seconds:0}秒";
        }

        if (seconds < 60 * 60)
        {
            var minutes = (int)(seconds / 60);
            var remainingSeconds = (int)(seconds % 60);
            return $"{minutes:N0}分{remainingSeconds:N0}秒";
        }

        if (seconds < 24 * 60 * 60)
        {
            var hours = (int)(seconds / (60 * 60));
            var minutes = (int)(seconds % (60 * 60) / 60);
            return $"{hours:N0}時間{minutes:N0}分";
        }

        var days = Math.Min(seconds / (24 * 60 * 60), 9999);
        return $"{days:0.#}日";
    }

    private static bool IsSameVfsNode(VfsNode left, VfsNode right)
    {
        if (left.Metadata is string leftPath && right.Metadata is string rightPath)
        {
            return string.Equals(
                leftPath.Replace('/', '\\').TrimEnd('\\'),
                rightPath.Replace('/', '\\').TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase);
        }

        if (left.Metadata is not null && right.Metadata is not null && left.Metadata.Equals(right.Metadata))
        {
            return true;
        }

        return left.IsDirectory == right.IsDirectory
            && string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatNodeType(VfsNode node)
    {
        var type = node.IsDirectory ? "Folder" : "File";
        var attributes = FormatAttributes(node.Attributes, includeNormal: false);
        return attributes.Length == 0 ? type : $"{type} [{attributes}]";
    }

    private static string FormatAttributes(FileAttributes attributes, bool includeNormal = true)
    {
        var values = new List<string>();
        if ((attributes & FileAttributes.Hidden) != 0) values.Add("Hidden");
        if ((attributes & FileAttributes.System) != 0) values.Add("System");
        if ((attributes & FileAttributes.ReadOnly) != 0) values.Add("Read-only");
        if ((attributes & FileAttributes.Archive) != 0) values.Add("Archive");
        if ((attributes & FileAttributes.ReparsePoint) != 0) values.Add("Reparse point");
        if ((attributes & FileAttributes.Compressed) != 0) values.Add("Compressed");
        if ((attributes & FileAttributes.Encrypted) != 0) values.Add("Encrypted");
        return values.Count == 0 && includeNormal ? "Normal" : string.Join(", ", values);
    }

    private sealed record PartitionNodeTag(PartitionInfo Partition);
    private sealed record DirectoryNodeTag(IReadOnlyFileSystem FileSystem, VfsNode Node);
    private sealed record DummyNodeTag;
    private sealed record LzopOpenSelection(LzopOpenMode Mode, string? TemporaryDirectory);
    private sealed record ImageAnalysis(
        IReadOnlyList<PartitionInfo> Partitions,
        IReadOnlyList<LvmDiagnostic> Diagnostics,
        List<IDisposable> OwnedReaders,
        int LvmVolumeCount);

    private sealed record ImageLoadResult(
        IDiskImageReader Reader,
        ImageAnalysis Analysis,
        string RawHex) : IDisposable
    {
        public void Dispose()
        {
            foreach (var disposable in Analysis.OwnedReaders)
            {
                disposable.Dispose();
            }

            Reader.Dispose();
        }
    }

    private async Task<bool> OpenSelectedFilePreviewAsync(bool showUnsupportedMessage)
    {
        if (_currentFileSystem is null
            || _fileList.SelectedItems.Count == 0
            || _fileList.SelectedItems[0].Tag is not VfsNode node
            || node.IsDirectory)
        {
            return false;
        }

        if (node.Size < 0 || node.Size > FilePreviewReader.MaximumFileSize)
        {
            if (showUnsupportedMessage)
            {
                MessageBox.Show(
                    this,
                    $"別窓表示できるファイルサイズは{FilePreviewReader.MaximumFileSize / 1024 / 1024:N0} MBまでです。",
                    "別窓表示",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }

            return false;
        }

        try
        {
            _statusLabel.Text = $"{node.Name} を読み込み中...";
            var fileSystem = _currentFileSystem;
            var preview = await Task.Run(() =>
            {
                var data = fileSystem.ReadFile(node, 0, checked((int)node.Size));
                return FilePreviewReader.TryRead(node.Name, data, out var content)
                    ? content
                    : null;
            });
            if (IsDisposed)
            {
                return false;
            }

            if (preview is null)
            {
                _statusLabel.Text = $"{node.Name}: テキストとして判定できませんでした";
                if (showUnsupportedMessage)
                {
                    MessageBox.Show(
                        this,
                        "対応する文書形式ではなく、内容もテキストとして安全に判定できませんでした。",
                        "別窓表示",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }

                return false;
            }

            var window = new FilePreviewForm(node.Name, preview);
            window.Show(this);
            _statusLabel.Text = $"{node.Name}: 別窓表示";
            return true;
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "別窓表示に失敗しました";
            MessageBox.Show(this, ex.Message, "別窓表示エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return true;
        }
    }
}
