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
    private readonly ToolStripProgressBar _writeProgressBar = new() { AutoSize = false, Width = 120, Visible = false };
    private readonly ToolStripButton _cancelWriteButton = new("保存キャンセル") { Enabled = false };
    private readonly ToolStripButton _backNavigationButton = new("戻る") { Enabled = false, ToolTipText = "戻る (Alt+←)" };
    private readonly ToolStripButton _forwardNavigationButton = new("進む") { Enabled = false, ToolTipText = "進む (Alt+→)" };
    private readonly ToolStripButton _upNavigationButton = new("上へ") { Enabled = false, ToolTipText = "親フォルダーへ (Alt+↑)" };
    private readonly ListView _headerList = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true };
    private readonly TextBox _warningText = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
    private readonly TextBox _offsetBox = new() { Text = "0x0", Width = 140 };
    private readonly NumericUpDown _lengthBox = new() { Minimum = 1, Maximum = 64 * 1024 * 1024, Value = 512, Increment = 512, Width = 110 };
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
    private readonly ListView _pendingEditList = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true, MultiSelect = true };
    private readonly Button _pendingReplaceContentButton = new() { Text = "内容元を差し替え...", AutoSize = true, Enabled = false };
    private readonly Button _pendingExternalEditButton = new() { Text = "外部エディターで編集...", AutoSize = true, Enabled = false };
    private readonly List<PendingFileEdit> _pendingFileEdits = [];
    private readonly TabControl _explorerDetailTabs = new() { Dock = DockStyle.Fill };
    private readonly ListView _mountList = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true, MultiSelect = true };
    private readonly TextBox _mountText = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };

    private IDiskImageReader? _reader;
    private readonly List<IDiskImageReader> _companionReaders = new();
    private IReadOnlyList<BtrfsDevicePartition> _btrfsDevices = [];
    private readonly List<PartitionInfo> _partitions = new();
    private readonly Dictionary<int, IReadOnlyFileSystem> _fileSystems = new();
    private readonly List<IDisposable> _partitionReaders = new();
    private readonly List<ProjectedFileSystemMount> _mounts = new();
    private readonly List<string> _analysisWarnings = new();
    private IReadOnlyFileSystem? _currentFileSystem;
    private IReadOnlyFileSystem? _pendingEditFileSystem;
    private VfsNode? _currentDirectory;
    private string _currentDirectoryPath = "/";
    private CancellationTokenSource? _searchCancellation;
    private CancellationTokenSource? _loadCancellation;
    private readonly HashSet<CancellationTokenSource> _copyCancellations = [];
    private readonly SemaphoreSlim _copyExecutionGate = new(1, 1);
    private CancellationTokenSource? _copyProgressOwner;
    private CancellationTokenSource? _writeCancellation;
    private readonly NavigationHistory<TreeNode> _navigationHistory = new();
    private bool _isHistoryNavigation;
    private bool _isLoadingImage;
    private bool _isWritingImage;
    private bool _closeAfterLoadCancellation;
    private bool _closeAfterWriteCancellation;
    private UefiVariableStore? _currentUefiVariableStore;
    private SwtpmStateStore? _currentTpmStateStore;
    private PendingEditContentStore? _pendingEditContentStore;

    public Form1(string? initialPath = null)
    {
        InitializeComponent();
        BuildUi();
        Shown += (_, _) => ShowPreparedPhysicalDiskRecoveryJournals();
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
            _writeCancellation?.Cancel();
            DisposeMounts();
            DisposeFileSystems();
            DisposePartitionReaders();
            DisposeCompanionReaders();
            _reader?.Dispose();
            _pendingEditContentStore?.Dispose();
        };
    }

    private void ShowPreparedPhysicalDiskRecoveryJournals()
    {
        PhysicalDiskRecoveryJournalScanResult scan;
        try
        {
            scan = PhysicalDiskRecoveryJournalLocator.Scan();
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or ArgumentException)
        {
            MessageBox.Show(
                this,
                $"物理ディスク復旧ジャーナルを確認できませんでした。{Environment.NewLine}{ex.Message}",
                "復旧ジャーナル確認エラー",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        if (scan.Prepared.Count == 0 && scan.Unreadable.Count == 0)
        {
            return;
        }

        var sections = new List<string>();
        if (scan.Prepared.Count > 0)
        {
            var examples = string.Join(
                Environment.NewLine,
                scan.Prepared.Take(3).Select(journal =>
                    $"- PhysicalDrive{journal.Target.DiskNumber}: {journal.Path}"));
            var remaining = scan.Prepared.Count > 3
                ? $"{Environment.NewLine}- ほか {scan.Prepared.Count - 3:N0} 件"
                : string.Empty;
            sections.Add(
                $"完了状態が記録されていない復旧ジャーナル: {scan.Prepared.Count:N0} 件"
                + Environment.NewLine
                + examples
                + remaining);
        }

        if (scan.Unreadable.Count > 0)
        {
            var examples = string.Join(
                Environment.NewLine,
                scan.Unreadable.Take(3).Select(journal =>
                    $"- {journal.Path}{Environment.NewLine}  {journal.Error}"));
            var remaining = scan.Unreadable.Count > 3
                ? $"{Environment.NewLine}- ほか {scan.Unreadable.Count - 3:N0} 件"
                : string.Empty;
            sections.Add(
                $"破損または読み取れない復旧ジャーナル: {scan.Unreadable.Count:N0} 件"
                + Environment.NewLine
                + examples
                + remaining);
        }

        MessageBox.Show(
            this,
            "物理ディスクの復旧が必要な可能性があるジャーナルを検出しました。"
            + Environment.NewLine
            + Environment.NewLine
            + string.Join(Environment.NewLine + Environment.NewLine, sections)
            + Environment.NewLine
            + Environment.NewLine
            + "対象ディスクへ自動では書き込みません。内容を確認し、正常なジャーナルの場合だけ［物理ディスク復旧］を実行してください。",
            "物理ディスク復旧ジャーナルの確認",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
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
        var openDeviceSetButton = new ToolStripButton("複数ディスク");
        openDeviceSetButton.Click += async (_, _) => await OpenDeviceSetDialogAsync();
        var openFolderButton = new ToolStripButton("フォルダ");
        openFolderButton.Click += async (_, _) => await OpenImageFolderDialogAsync();
        var openPhysicalDiskButton = new ToolStripButton("物理ディスク");
        openPhysicalDiskButton.Click += async (_, _) => await OpenPhysicalDiskDialogAsync();
        var recoverPhysicalDiskButton = new ToolStripButton("物理ディスク復旧");
        recoverPhysicalDiskButton.Click += async (_, _) => await RestorePhysicalDiskAsync();
        var reportButton = new ToolStripButton("解析レポート");
        reportButton.Click += (_, _) => SaveAnalysisReport();
        var snapshotButton = new ToolStripButton("スナップショット");
        snapshotButton.Click += (_, _) => SelectQcow2Snapshot();
        var vmaDiskButton = new ToolStripButton("VMAディスク");
        vmaDiskButton.Click += (_, _) => SelectVmaDisk();
        var ovaDiskButton = new ToolStripButton("OVAディスク");
        ovaDiskButton.Click += (_, _) => SelectOvaDisk();
        toolStrip.Items.Add(openButton);
        toolStrip.Items.Add(openDeviceSetButton);
        toolStrip.Items.Add(openFolderButton);
        toolStrip.Items.Add(openPhysicalDiskButton);
        toolStrip.Items.Add(recoverPhysicalDiskButton);
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
        var probeButton = new Button { Text = "Probe保存", Width = 90 };
        var lzoVerifyButton = new Button { Text = "LZO検証保存", Width = 100 };
        readButton.Click += (_, _) => ReadRawData();
        clusterButton.Click += (_, _) => ShowClusterLookup();
        probeButton.Click += async (_, _) => await SaveRawProbeAsync();
        lzoVerifyButton.Click += (_, _) => SaveLzoVerificationBlock();
        top.Controls.AddRange(new Control[]
        {
            new Label { Text = "Offset", AutoSize = true, Padding = new Padding(0, 6, 0, 0) },
            _offsetBox,
            new Label { Text = "Length", AutoSize = true, Padding = new Padding(12, 6, 0, 0) },
            _lengthBox,
            readButton,
            clusterButton,
            probeButton,
            lzoVerifyButton
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
        var editButton = new ToolStripDropDownButton("編集（実験）");
        var queueWriteItem = new ToolStripMenuItem("選択ファイルの内容を変更...");
        queueWriteItem.Click += (_, _) => QueueSelectedFileWrite();
        var queueCreateItem = new ToolStripMenuItem("表示フォルダーへファイルを追加...");
        queueCreateItem.Click += (_, _) => QueueFileCreation();
        var queueExternalCreateItem = new ToolStripMenuItem("表示フォルダーへ空ファイルを作成して外部編集...");
        queueExternalCreateItem.Click += async (_, _) => await QueueExternalFileCreationAsync();
        var queueExternalWriteItem = new ToolStripMenuItem("選択ファイルを外部エディターで編集...");
        queueExternalWriteItem.Click += async (_, _) => await QueueSelectedFileExternalEditAsync();
        var queueDeleteItem = new ToolStripMenuItem("選択ファイルを削除予定に追加");
        queueDeleteItem.Click += (_, _) => QueueSelectedFileDeletion();
        var queueCreateDirectoryItem = new ToolStripMenuItem("表示フォルダーへディレクトリを作成...");
        queueCreateDirectoryItem.Click += (_, _) => QueueDirectoryCreation();
        var queueDeleteDirectoryItem = new ToolStripMenuItem("選択ディレクトリを削除予定に追加");
        queueDeleteDirectoryItem.Click += (_, _) => QueueSelectedDirectoryDeletion();
        var queueMoveItem = new ToolStripMenuItem("選択項目を移動・名前変更...");
        queueMoveItem.Click += (_, _) => QueueSelectedEntryMove();
        var queueAttributesItem = new ToolStripMenuItem("選択項目の属性を変更...");
        queueAttributesItem.Click += (_, _) => QueueSelectedEntryAttributes();
        var queueTimestampItem = new ToolStripMenuItem("選択項目の更新日時を変更...");
        queueTimestampItem.Click += (_, _) => QueueSelectedEntryTimestamp();
        var saveEditsItem = new ToolStripMenuItem("変更一覧を新しいRAWへ保存...");
        saveEditsItem.Click += async (_, _) => await SavePendingEditsAsync();
        var undoEditItem = new ToolStripMenuItem("最後の変更を取り消す");
        undoEditItem.Click += (_, _) => UndoLastPendingEdit();
        editButton.DropDownItems.Add(queueWriteItem);
        editButton.DropDownItems.Add(queueExternalWriteItem);
        editButton.DropDownItems.Add(queueCreateItem);
        editButton.DropDownItems.Add(queueExternalCreateItem);
        editButton.DropDownItems.Add(queueDeleteItem);
        editButton.DropDownItems.Add(queueCreateDirectoryItem);
        editButton.DropDownItems.Add(queueDeleteDirectoryItem);
        editButton.DropDownItems.Add(queueMoveItem);
        editButton.DropDownItems.Add(queueAttributesItem);
        editButton.DropDownItems.Add(queueTimestampItem);
        editButton.DropDownItems.Add(new ToolStripSeparator());
        editButton.DropDownItems.Add(undoEditItem);
        editButton.DropDownItems.Add(saveEditsItem);
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
        _cancelWriteButton.Click += (_, _) => _writeCancellation?.Cancel();
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
        explorerStrip.Items.Add(new ToolStripSeparator());
        explorerStrip.Items.Add(editButton);
        explorerStrip.Items.Add(_writeProgressBar);
        explorerStrip.Items.Add(_cancelWriteButton);
        explorerStrip.Items.Add(deletedButton);
        explorerStrip.Items.Add(new ToolStripSeparator());
        explorerStrip.Items.Add(mountButton);

        var right = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, FixedPanel = FixedPanel.Panel2 };
        right.Panel1.Controls.Add(_fileList);
        _explorerDetailTabs.TabPages.Add(new TabPage("プレビュー") { Controls = { _previewText } });
        _explorerDetailTabs.TabPages.Add(new TabPage("変更予定") { Controls = { CreatePendingEditPanel() } });
        right.Panel2.Controls.Add(_explorerDetailTabs);

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

    private Control CreatePendingEditPanel()
    {
        _pendingEditList.Columns.Add("操作", 90);
        _pendingEditList.Columns.Add("仮想パス", 360);
        _pendingEditList.Columns.Add("入力・設定内容", 420);
        _pendingEditList.Columns.Add("入力サイズ", 110, HorizontalAlignment.Right);

        var undoButton = new Button { Text = "選択を取り消す", AutoSize = true };
        undoButton.Click += (_, _) => UndoSelectedPendingEdits();
        _pendingReplaceContentButton.Click += (_, _) => ReplaceSelectedPendingEditContent();
        _pendingExternalEditButton.Click += async (_, _) => await EditSelectedPendingContentExternallyAsync();
        _pendingEditList.SelectedIndexChanged += (_, _) => UpdatePendingContentButtons();
        _pendingEditList.DoubleClick += async (_, _) => await EditSelectedPendingContentExternallyAsync();
        var undoLastButton = new Button { Text = "最後を取り消す", AutoSize = true };
        undoLastButton.Click += (_, _) => UndoLastPendingEdit();
        var clearButton = new Button { Text = "すべて取り消す", AutoSize = true };
        clearButton.Click += (_, _) => ClearPendingEditsWithPrompt();
        var saveButton = new Button { Text = "新しいRAWへ保存...", AutoSize = true };
        saveButton.Click += async (_, _) => await SavePendingEditsAsync();
        var applyPhysicalButton = new Button { Text = "物理ディスクへ適用...", AutoSize = true };
        applyPhysicalButton.Click += async (_, _) => await ApplyPendingEditsToPhysicalDiskAsync();
        var description = new Label
        {
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            Padding = new Padding(8, 4, 8, 0),
            Text = "追加・変更予定の内容元は差し替えや外部編集ができます。通常は新規RAWへ保存します。",
        };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(4, 2, 4, 2),
        };
        buttons.Controls.Add(saveButton);
        buttons.Controls.Add(applyPhysicalButton);
        buttons.Controls.Add(clearButton);
        buttons.Controls.Add(undoLastButton);
        buttons.Controls.Add(undoButton);
        buttons.Controls.Add(_pendingExternalEditButton);
        buttons.Controls.Add(_pendingReplaceContentButton);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(description, 0, 0);
        layout.Controls.Add(buttons, 0, 1);
        layout.Controls.Add(_pendingEditList, 0, 2);
        return layout;
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

    private async Task OpenDeviceSetDialogAsync()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = DiskImageReaderFactory.DialogFilter,
            Title = "同じBtrfs／Linux md構成に属するディスクイメージをすべて選択",
            Multiselect = true
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var paths = dialog.FileNames
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (paths.Length < 2)
        {
            MessageBox.Show(
                this,
                "複数ディスクでは2個以上の異なるイメージを選択してください。",
                "複数ディスク",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        await LoadImageAsync(paths[0], paths[1..]);
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
            Width = 720,
            Height = 520,
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
        var retentionLabel = new Label
        {
            AutoSize = true,
            Margin = new Padding(24, 0, 0, 2),
            Text = "展開したRAWの扱い"
        };
        var retentionBox = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Margin = new Padding(24, 0, 0, 8),
            Width = 400
        };
        retentionBox.Items.AddRange(
        [
            "イメージを閉じると削除",
            "検証済みキャッシュとして保持・再利用（推奨）",
            "指定場所へ通常RAWとして保存"
        ]);
        retentionBox.SelectedIndex = 1;
        var temporaryPathLabel = new Label
        {
            AutoSize = true,
            Margin = new Padding(24, 0, 0, 2),
            Text = "キャッシュ保存先"
        };
        var temporaryPathBox = new TextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            Text = LzopRawCacheManager.DefaultCacheRoot
        };
        var browseTemporaryPathButton = new Button
        {
            AutoSize = true,
            Text = "参照..."
        };
        var manageCacheButton = new Button
        {
            AutoSize = true,
            Text = "キャッシュ管理..."
        };
        var storagePaths = new[]
        {
            System.IO.Path.GetTempPath(),
            LzopRawCacheManager.DefaultCacheRoot,
            System.IO.Path.ChangeExtension(System.IO.Path.GetFullPath(path), ".raw")
        };
        var selectedRetentionIndex = retentionBox.SelectedIndex;
        var overwriteSavedRaw = false;
        browseTemporaryPathButton.Click += (_, _) =>
        {
            if (retentionBox.SelectedIndex == 2)
            {
                using var saveDialog = new SaveFileDialog
                {
                    Title = "展開したRAWの保存先を選択してください",
                    Filter = "RAW disk image (*.raw;*.img;*.dd)|*.raw;*.img;*.dd|All files (*.*)|*.*",
                    FileName = System.IO.Path.GetFileName(temporaryPathBox.Text),
                    InitialDirectory = System.IO.Path.GetDirectoryName(temporaryPathBox.Text),
                    AddExtension = true,
                    DefaultExt = "raw",
                    OverwritePrompt = true
                };
                if (saveDialog.ShowDialog(dialog) == DialogResult.OK)
                {
                    temporaryPathBox.Text = saveDialog.FileName;
                    storagePaths[2] = saveDialog.FileName;
                    overwriteSavedRaw = File.Exists(saveDialog.FileName);
                }

                return;
            }

            using var folderDialog = new FolderBrowserDialog
            {
                Description = retentionBox.SelectedIndex == 1
                    ? "LZO高速モードのキャッシュ保存先を選択してください"
                    : "LZO高速モードの一時ファイル保存先を選択してください",
                UseDescriptionForTitle = true,
                SelectedPath = temporaryPathBox.Text,
                ShowNewFolderButton = true
            };
            if (folderDialog.ShowDialog(dialog) == DialogResult.OK)
            {
                temporaryPathBox.Text = folderDialog.SelectedPath;
                storagePaths[retentionBox.SelectedIndex] = folderDialog.SelectedPath;
            }
        };
        manageCacheButton.Click += (_, _) => ShowLzopCacheManager(temporaryPathBox.Text);
        var temporaryPathPanel = new TableLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Margin = new Padding(24, 0, 0, 8)
        };
        temporaryPathPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        temporaryPathPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        temporaryPathPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        temporaryPathPanel.Controls.Add(temporaryPathBox, 0, 0);
        temporaryPathPanel.Controls.Add(browseTemporaryPathButton, 1, 0);
        temporaryPathPanel.Controls.Add(manageCacheButton, 2, 0);
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
            Text = "キャッシュは元LZOのパス、サイズ、更新日時、SHA-256、RAWサイズ、完了状態を検証してから再利用します。"
        };
        retentionBox.SelectedIndexChanged += (_, _) =>
        {
            if (selectedRetentionIndex >= 0)
            {
                storagePaths[selectedRetentionIndex] = temporaryPathBox.Text;
            }

            selectedRetentionIndex = retentionBox.SelectedIndex;
            temporaryPathBox.Text = storagePaths[selectedRetentionIndex];
            temporaryPathLabel.Text = selectedRetentionIndex switch
            {
                0 => "一時ファイルの保存先",
                1 => "キャッシュ保存先",
                _ => "通常RAWの保存先"
            };
            cleanupDescription.Text = selectedRetentionIndex switch
            {
                0 => "一時RAWは、別のイメージへ切り替えるかアプリを終了すると削除されます。",
                1 => "キャッシュは元LZOのパス、サイズ、更新日時、SHA-256、RAWサイズ、完了状態を検証してから再利用します。",
                _ => "展開したRAWは指定場所に残ります。不要になった場合は手動で削除してください。"
            };
            manageCacheButton.Enabled = fastMode.Checked && selectedRetentionIndex == 1;
        };
        fastMode.CheckedChanged += (_, _) =>
        {
            temporaryPathBox.Enabled = fastMode.Checked;
            browseTemporaryPathButton.Enabled = fastMode.Checked;
            retentionBox.Enabled = fastMode.Checked;
            manageCacheButton.Enabled = fastMode.Checked && retentionBox.SelectedIndex == 1;
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
            RowCount = 11,
            Padding = new Padding(16)
        };
        layout.Controls.Add(introduction, 0, 0);
        layout.Controls.Add(fastMode, 0, 1);
        layout.Controls.Add(fastDescription, 0, 2);
        layout.Controls.Add(retentionLabel, 0, 3);
        layout.Controls.Add(retentionBox, 0, 4);
        layout.Controls.Add(temporaryPathLabel, 0, 5);
        layout.Controls.Add(temporaryPathPanel, 0, 6);
        layout.Controls.Add(onDemandMode, 0, 7);
        layout.Controls.Add(onDemandDescription, 0, 8);
        layout.Controls.Add(cleanupDescription, 0, 9);
        layout.Controls.Add(buttons, 0, 10);
        dialog.Controls.Add(layout);
        dialog.AcceptButton = okButton;
        dialog.CancelButton = cancelButton;
        dialog.FormClosing += (_, e) =>
        {
            if (dialog.DialogResult != DialogResult.OK || !fastMode.Checked || retentionBox.SelectedIndex != 2)
            {
                return;
            }

            var sourcePath = System.IO.Path.GetFullPath(path);
            var rawPath = System.IO.Path.GetFullPath(temporaryPathBox.Text);
            if (string.Equals(sourcePath, rawPath, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(dialog, "元LZOファイルと同じ場所へRAWを保存できません。", "RAW保存先", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                e.Cancel = true;
                return;
            }

            if (File.Exists(rawPath) && !overwriteSavedRaw)
            {
                overwriteSavedRaw = MessageBox.Show(
                    dialog,
                    $"{rawPath}{Environment.NewLine}{Environment.NewLine}既存ファイルを、展開完了後に置き換えますか？",
                    "RAWファイルの上書き",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2) == DialogResult.Yes;
                e.Cancel = !overwriteSavedRaw;
            }
        };

        return dialog.ShowDialog(this) == DialogResult.OK
            ? fastMode.Checked
                ? new LzopOpenSelection(
                    retentionBox.SelectedIndex switch
                    {
                        1 => LzopOpenMode.CachedRaw,
                        2 => LzopOpenMode.SavedRaw,
                        _ => LzopOpenMode.TemporaryRaw
                    },
                    temporaryPathBox.Text,
                    overwriteSavedRaw)
                : new LzopOpenSelection(LzopOpenMode.OnDemand, null, OverwriteSavedRaw: false)
            : null;
    }

    private void ShowLzopCacheManager(string cacheRoot)
    {
        using var dialog = new Form
        {
            Text = "LZOキャッシュ管理",
            Width = 980,
            Height = 480,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.Sizable,
            MinimizeBox = false,
            ShowInTaskbar = false
        };
        var rootLabel = new Label
        {
            AutoEllipsis = true,
            Dock = DockStyle.Top,
            Height = 62,
            Padding = new Padding(10, 10, 10, 4),
            Text = $"展開RAW: {Path.GetFullPath(cacheRoot)}{Environment.NewLine}省容量索引: {LzopIndexCacheManager.DefaultIndexRoot}"
        };
        var list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            MultiSelect = true
        };
        list.Columns.Add("種類", 100);
        list.Columns.Add("元LZO", 390);
        list.Columns.Add("状態", 80);
        list.Columns.Add("保存サイズ", 110);
        list.Columns.Add("最終利用", 150);
        var summary = new Label { AutoSize = true, Padding = new Padding(8, 9, 8, 0) };
        var deleteButton = new Button { AutoSize = true, Text = "選択したキャッシュを削除" };
        var deleteUnusableButton = new Button { AutoSize = true, Text = "未完成・破損を削除" };
        var closeButton = new Button { AutoSize = true, Text = "閉じる", DialogResult = DialogResult.OK };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 50,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(8)
        };
        buttons.Controls.Add(closeButton);
        buttons.Controls.Add(deleteButton);
        buttons.Controls.Add(deleteUnusableButton);
        buttons.Controls.Add(summary);
        dialog.Controls.Add(list);
        dialog.Controls.Add(rootLabel);
        dialog.Controls.Add(buttons);
        dialog.AcceptButton = closeButton;

        void RefreshEntries()
        {
            list.BeginUpdate();
            list.Items.Clear();
            var entries = new List<object>();
            entries.AddRange(LzopRawCacheManager.GetEntries(cacheRoot));
            entries.AddRange(LzopIndexCacheManager.GetEntries());
            foreach (var entry in entries.OrderByDescending(GetLastUsedUtc))
            {
                var item = new ListViewItem(GetCacheKind(entry)) { Tag = entry };
                item.SubItems.Add(GetSourcePath(entry));
                item.SubItems.Add(GetCacheStatus(entry));
                item.SubItems.Add(FormatBytes(GetStoredBytes(entry)));
                item.SubItems.Add(GetLastUsedUtc(entry).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                list.Items.Add(item);
            }

            list.EndUpdate();
            summary.Text = $"{entries.Count:N0}件 / {FormatBytes(entries.Sum(GetStoredBytes))}";
            deleteButton.Enabled = list.SelectedItems.Count > 0;
            deleteUnusableButton.Enabled = entries.Any(entry => !IsCacheUsable(entry));
        }

        bool DeleteEntries(IEnumerable<object> entries)
        {
            foreach (var entry in entries)
            {
                var deleted = entry switch
                {
                    LzopRawCacheEntry raw => LzopRawCacheManager.TryDelete(raw.CacheId, cacheRoot, out var error)
                        ? (true, string.Empty)
                        : (false, error),
                    LzopIndexCacheEntry index => LzopIndexCacheManager.TryDelete(index.CacheId, out var error)
                        ? (true, string.Empty)
                        : (false, error),
                    _ => (false, "不明なキャッシュ形式です。")
                };
                if (!deleted.Item1)
                {
                    MessageBox.Show(dialog, deleted.Item2, "キャッシュ削除エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return false;
                }
            }

            return true;
        }

        list.SelectedIndexChanged += (_, _) => deleteButton.Enabled = list.SelectedItems.Count > 0;
        deleteButton.Click += (_, _) =>
        {
            var selected = list.SelectedItems.Cast<ListViewItem>()
                .Select(item => item.Tag!)
                .ToList();
            if (selected.Count == 0
                || MessageBox.Show(
                    dialog,
                    $"選択した{selected.Count:N0}件のキャッシュを削除しますか？",
                    "キャッシュ削除",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            {
                return;
            }

            if (DeleteEntries(selected))
            {
                RefreshEntries();
            }
        };
        deleteUnusableButton.Click += (_, _) =>
        {
            var unusable = new List<object>();
            unusable.AddRange(LzopRawCacheManager.GetEntries(cacheRoot).Where(entry => !entry.IsUsable));
            unusable.AddRange(LzopIndexCacheManager.GetEntries().Where(entry => !entry.IsUsable));
            if (unusable.Count > 0 && DeleteEntries(unusable))
            {
                RefreshEntries();
            }
        };

        RefreshEntries();
        dialog.ShowDialog(this);

        static string GetCacheKind(object entry) => entry switch
        {
            LzopRawCacheEntry => "展開RAW",
            LzopIndexCacheEntry => "省容量索引",
            _ => "不明"
        };
        static string GetSourcePath(object entry) => entry switch
        {
            LzopRawCacheEntry raw => raw.SourcePath,
            LzopIndexCacheEntry index => index.SourcePath,
            _ => string.Empty
        };
        static string GetCacheStatus(object entry) => entry switch
        {
            LzopRawCacheEntry raw => raw.Status,
            LzopIndexCacheEntry index => index.Status,
            _ => "不明"
        };
        static long GetStoredBytes(object entry) => entry switch
        {
            LzopRawCacheEntry raw => raw.StoredBytes,
            LzopIndexCacheEntry index => index.StoredBytes,
            _ => 0
        };
        static DateTime GetLastUsedUtc(object entry) => entry switch
        {
            LzopRawCacheEntry raw => raw.LastUsedUtc,
            LzopIndexCacheEntry index => index.LastUsedUtc,
            _ => DateTime.MinValue
        };
        static bool IsCacheUsable(object entry) => entry switch
        {
            LzopRawCacheEntry raw => raw.IsUsable,
            LzopIndexCacheEntry index => index.IsUsable,
            _ => false
        };
    }

    private async Task LoadImageAsync(string path, IReadOnlyList<string>? companionPaths = null)
    {
        if (_isWritingImage)
        {
            _statusLabel.Text = "変更済みRAWの保存中は別のイメージを開けません";
            return;
        }

        if (_isLoadingImage)
        {
            _statusLabel.Text = "別のディスクイメージを読み込み中です";
            return;
        }

        if (!ConfirmDiscardPendingEdits("別のディスクイメージを開くと変更予定を破棄します。続行しますか？"))
        {
            return;
        }

        var lzopSelection = new LzopOpenSelection(LzopOpenMode.OnDemand, null, OverwriteSavedRaw: false);
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
                companionPaths ?? [],
                rawOffset,
                rawLength,
                progress,
                lzopSelection.Mode,
                lzopSelection.TemporaryDirectory,
                loadCancellation.Token,
                lzopSelection.OverwriteSavedRaw),
                loadCancellation.Token);
            loadCancellation.Token.ThrowIfCancellationRequested();
            if (IsDisposed)
            {
                return;
            }

            ClearPendingEdits();
            DisposeFileSystems();
            DisposePartitionReaders();
            DisposeCompanionReaders();
            _reader?.Dispose();
            _reader = loadResult.Reader;
            _companionReaders.AddRange(loadResult.CompanionReaders);
            _btrfsDevices = loadResult.BtrfsDevices;
            _partitionReaders.AddRange(loadResult.Analysis.OwnedReaders);
            adopted = true;
            _partitions.Clear();
            _pathBox.Text = _companionReaders.Count == 0
                ? path
                : $"{path} (+{_companionReaders.Count:N0} companion disk)";

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
        IReadOnlyList<string> companionPaths,
        long rawOffset,
        int rawLength,
        IProgress<DiskImageProgress> progress,
        LzopOpenMode lzopOpenMode,
        string? lzopTemporaryDirectory,
        CancellationToken cancellationToken,
        bool overwriteSavedRaw)
    {
        IDiskImageReader? reader = null;
        var companionReaders = new List<IDiskImageReader>();
        var ownedReaders = new List<IDisposable>();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            reader = DiskImageReaderFactory.Open(
                path,
                progress,
                lzopOpenMode,
                lzopTemporaryDirectory,
                cancellationToken,
                overwriteSavedRaw);
            foreach (var companionPath in companionPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress.Report(new DiskImageProgress(
                    $"companion diskを開いています: {companionReaders.Count + 1:N0} / {companionPaths.Count:N0}",
                    companionReaders.Count + 1,
                    companionPaths.Count));
                companionReaders.Add(DiskImageReaderFactory.Open(
                    companionPath,
                    progress,
                    cancellationToken: cancellationToken));
            }

            var disks = new List<IBlockReader> { reader };
            disks.AddRange(companionReaders);
            progress.Report(new DiskImageProgress("Linux md arrayを照合中..."));
            var mdDiscovery = MdRaidDeviceSet.Discover(disks, cancellationToken);
            IReadOnlyList<BtrfsDevicePartition> btrfsDevices = [];
            if (companionReaders.Count > 0)
            {
                progress.Report(new DiskImageProgress("Btrfs device setを照合中..."));
                btrfsDevices = BtrfsDeviceSet.Discover(disks, cancellationToken);
                var primaryFileSystemIds = btrfsDevices
                    .Where(item => ReferenceEquals(item.Disk, reader))
                    .Select(item => item.Identity.FileSystemId)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var hasCommonBtrfsSet = primaryFileSystemIds.Any(fileSystemId => disks.All(disk => btrfsDevices.Any(item =>
                    ReferenceEquals(item.Disk, disk)
                    && string.Equals(
                        item.Identity.FileSystemId,
                        fileSystemId,
                        StringComparison.OrdinalIgnoreCase))));
                if (!hasCommonBtrfsSet && mdDiscovery.Arrays.Count == 0)
                {
                    throw new InvalidDataException(
                        "選択したディスクから共通のBtrfs FSIDまたは組み立て可能なLinux md arrayが見つかりません。");
                }
            }

            var analysis = AnalyzeImage(
                reader,
                disks,
                mdDiscovery,
                ownedReaders,
                progress,
                cancellationToken);

            progress.Report(new DiskImageProgress("先頭データを読み込み中..."));
            cancellationToken.ThrowIfCancellationRequested();
            var rawData = new byte[rawLength];
            reader.ReadAt(rawOffset, rawData, 0, rawLength);
            cancellationToken.ThrowIfCancellationRequested();
            var rawHex = HexFormatter.Format(rawData, rawOffset);
            return new ImageLoadResult(reader, companionReaders, btrfsDevices, analysis, rawHex);
        }
        catch
        {
            foreach (var disposable in ownedReaders)
            {
                disposable.Dispose();
            }

            foreach (var companionReader in companionReaders)
            {
                companionReader.Dispose();
            }

            reader?.Dispose();
            throw;
        }
    }

    private static ImageAnalysis AnalyzeImage(
        IDiskImageReader reader,
        IReadOnlyList<IBlockReader> inputDisks,
        MdRaidDiscoveryResult mdDiscovery,
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

        var nextNumber = discovered.Count + 1;
        foreach (var companionDisk in inputDisks.Skip(1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var companionPartitions = PartitionTableReader.ReadPartitions(companionDisk, cancellationToken).ToList();
            if (companionPartitions.Count == 0 && companionDisk.Length >= 512)
            {
                companionPartitions.Add(new PartitionInfo
                {
                    Number = 1,
                    Scheme = "WholeDisk",
                    Name = "Whole companion disk",
                    Type = "Unpartitioned",
                    StartLba = 0,
                    SectorCount = checked((ulong)(companionDisk.Length / 512))
                });
            }

            foreach (var companionPartition in companionPartitions)
            {
                var slice = new PartitionSliceReader(companionDisk, companionPartition);
                discovered.Add(new PartitionInfo
                {
                    Number = nextNumber++,
                    Scheme = $"Companion {companionPartition.Scheme}",
                    Name = companionPartition.Name,
                    Type = companionPartition.Type,
                    TypeId = companionPartition.TypeId,
                    Bootable = companionPartition.Bootable,
                    StartLba = 0,
                    SectorCount = checked((ulong)(slice.Length / 512)),
                    ReaderOverride = slice,
                    LengthOverrideBytes = slice.Length
                });
            }
        }

        foreach (var array in mdDiscovery.Arrays)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var arrayPartitions = PartitionTableReader.ReadPartitions(array.Reader, cancellationToken);
            if (arrayPartitions.Count == 0)
            {
                discovered.Add(new PartitionInfo
                {
                    Number = nextNumber++,
                    Scheme = $"Linux md {array.LevelName}",
                    Name = string.IsNullOrWhiteSpace(array.SetName)
                        ? $"md {array.LevelName} {array.SetUuid[..8]}"
                        : array.SetName,
                    Type = array.Reader.IsDegraded
                        ? $"Linux md {array.LevelName} (degraded)"
                        : $"Linux md {array.LevelName}",
                    TypeId = array.SetUuid,
                    StartLba = 0,
                    SectorCount = checked((ulong)(array.Reader.Length / 512)),
                    ReaderOverride = array.Reader,
                    LengthOverrideBytes = array.Reader.Length
                });
                continue;
            }

            foreach (var nested in arrayPartitions)
            {
                var slice = new PartitionSliceReader(array.Reader, nested);
                discovered.Add(new PartitionInfo
                {
                    Number = nextNumber++,
                    Scheme = $"Linux md {array.LevelName}",
                    Name = $"{array.SetName}: {nested.Name}",
                    Type = nested.Type,
                    TypeId = $"{array.SetUuid}:{nested.TypeId}",
                    Bootable = nested.Bootable,
                    StartLba = 0,
                    SectorCount = checked((ulong)(slice.Length / 512)),
                    ReaderOverride = slice,
                    LengthOverrideBytes = slice.Length
                });
            }
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
        diagnostics.AddRange(mdDiscovery.Diagnostics.Select(message => new LvmDiagnostic(message, false)));
        diagnostics.AddRange(mdDiscovery.Arrays.Select(array => new LvmDiagnostic(
            $"Linux md {array.LevelName}: {array.SetName} ({array.SetUuid})、"
            + $"member={array.Components.Count:N0}/{array.ExpectedDeviceCount:N0}、"
            + $"event={array.Events:N0}{(array.Reader.IsDegraded ? "、degraded" : "")}",
            false)));
        var lvmPartitions = discovered
            .Where(partition => partition.FileSystem.StartsWith("LVM2", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var lvmVolumeCount = 0;
        if (lvmPartitions.Count > 0)
        {
            progress.Report(new DiskImageProgress("LVM2論理ボリュームを解析中..."));
            var lvmInputDevices = inputDisks
                .Concat(mdDiscovery.Arrays.Select(array => (IBlockReader)array.Reader))
                .ToList();
            var lvmResult = LogicalVolumeDiscoverer.Discover(
                lvmInputDevices,
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

        for (var index = 0; index < _companionReaders.Count; index++)
        {
            var path = _companionReaders[index].GetHeaderRows()
                .FirstOrDefault(row => string.Equals(row.Key, "ファイル", StringComparison.Ordinal))
                .Value;
            var item = new ListViewItem($"Companion disk {index + 1}");
            item.SubItems.Add(path ?? "(path unavailable)");
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
        if (_isWritingImage || _isLoadingImage)
        {
            _statusLabel.Text = "イメージ処理中はqcow2スナップショットを切り替えられません";
            return;
        }

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

        if (!ConfirmDiscardPendingEdits("別のqcow2スナップショットへ切り替えると変更予定を破棄します。続行しますか？"))
        {
            return;
        }

        ClearPendingEdits();
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
        if (_isWritingImage || _isLoadingImage)
        {
            _statusLabel.Text = "イメージ処理中はVMAディスクを切り替えられません";
            return;
        }

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

        if (!ConfirmDiscardPendingEdits("別のVMAディスクへ切り替えると変更予定を破棄します。続行しますか？"))
        {
            return;
        }

        ClearPendingEdits();
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
        if (_isWritingImage || _isLoadingImage)
        {
            _statusLabel.Text = "イメージ処理中はOVAディスクを切り替えられません";
            return;
        }

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

        if (!ConfirmDiscardPendingEdits("別のOVAディスクへ切り替えると変更予定を破棄します。続行しますか？"))
        {
            return;
        }

        ClearPendingEdits();
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

    private async Task SaveRawProbeAsync()
    {
        if (_reader is null)
        {
            return;
        }

        long offset;
        int length;
        try
        {
            offset = ParseOffset(_offsetBox.Text);
            length = (int)_lengthBox.Value;
            if (offset < 0 || offset > _reader.Length - length)
            {
                throw new ArgumentOutOfRangeException(nameof(offset), "Probe 範囲がディスクイメージの範囲外です。");
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Probe保存エラー", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        using var dialog = new SaveFileDialog
        {
            Title = "LZO raw probe を保存",
            FileName = $"raw-probe-{offset:X}-{length}.bin",
            Filter = "Binary files (*.bin)|*.bin|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var reader = _reader;
        var description = reader.DescribeOffset(offset);
        _statusLabel.Text = "Probe保存中...";
        try
        {
            var hash = await Task.Run(() => SaveRawProbe(reader, offset, length, dialog.FileName));
            var message = $"Probe保存完了: {dialog.FileName}{Environment.NewLine}offset=0x{offset:X}, length={length}, SHA-256={hash}{Environment.NewLine}{description}";
            DiagnosticLog.Write(message);
            _hexText.Text = message;
            _statusLabel.Text = "Probe保存完了";
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"Probe save failed: offset={offset}, length={length}, error={ex}");
            MessageBox.Show(this, ex.Message, "Probe保存エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _statusLabel.Text = "Probe保存失敗";
        }
    }

    private static string SaveRawProbe(IDiskImageReader reader, long offset, int length, string path)
    {
        const int bufferSize = 1024 * 1024;
        var buffer = new byte[Math.Min(bufferSize, length)];
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        var remaining = length;
        var currentOffset = offset;
        while (remaining > 0)
        {
            var count = Math.Min(buffer.Length, remaining);
            reader.ReadAt(currentOffset, buffer, 0, count);
            output.Write(buffer, 0, count);
            hash.AppendData(buffer, 0, count);
            currentOffset = checked(currentOffset + count);
            remaining -= count;
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private void SaveLzoVerificationBlock()
    {
        if (_reader is not LzopDiskImageReader lzopReader)
        {
            MessageBox.Show(this, "LZO省容量モードで開いた .lzo イメージでのみ利用できます。", "LZO検証保存", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            var offset = ParseOffset(_offsetBox.Text);
            using var dialog = new SaveFileDialog
            {
                Title = "LZO検証用 block を保存",
                FileName = $"lzop-block-{offset:X}.lzo",
                Filter = "LZOP files (*.lzo)|*.lzo|All files (*.*)|*.*"
            };
            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }

            var block = lzopReader.ExportVerificationBlock(offset, dialog.FileName);
            var message = $"LZO検証block保存完了: {block.OutputPath}{Environment.NewLine}block={block.BlockIndex}, blockRawOffset=0x{block.BlockRawOffset:X}, inBlockOffset={block.InBlockOffset}, blockRawSize={block.BlockRawSize}";
            DiagnosticLog.Write(message);
            _hexText.Text = message;
            _statusLabel.Text = "LZO検証block保存完了";
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"LZO verification block export failed: error={ex}");
            MessageBox.Show(this, ex.Message, "LZO検証保存エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
            var fs = string.Equals(partition.FileSystem, "Btrfs", StringComparison.OrdinalIgnoreCase)
                && _btrfsDevices.Count > 0
                    ? BtrfsDeviceSet.TryOpen(_reader, partition, _btrfsDevices, out var error)
                    : FileSystemDetector.TryOpen(_reader, partition, out error);
            if (fs is null && TryReadBitLockerUnlockMetadata(partition, out var metadata))
            {
                Cursor = Cursors.Default;
                while (TryPromptForBitLockerCredential(
                    metadata,
                    out var recoveryPasswordKey,
                    out var password,
                    out var startupKey))
                {
                    try
                    {
                        Cursor = Cursors.WaitCursor;
                        fs = recoveryPasswordKey.Length > 0
                            ? FileSystemDetector.TryOpen(_reader, partition, recoveryPasswordKey, out error)
                            : password.Length > 0
                                ? FileSystemDetector.TryOpenWithBitLockerPassword(_reader, partition, password, out error)
                                : FileSystemDetector.TryOpenWithBitLockerStartupKey(_reader, partition, startupKey!, out error);
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(recoveryPasswordKey);
                        Array.Clear(password);
                        startupKey?.Dispose();
                    }

                    if (fs is not null)
                    {
                        break;
                    }

                    Cursor = Cursors.Default;
                    var retry = MessageBox.Show(
                        this,
                        $"{error}{Environment.NewLine}{Environment.NewLine}解除情報を再入力しますか？",
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

            if (fs is null && TryReadLuks1UnlockMetadata(partition, out var luksMetadata))
            {
                Cursor = Cursors.Default;
                while (TryPromptForLuksPassphrase(
                    "LUKS1",
                    $"暗号方式: {luksMetadata.CipherName}-{luksMetadata.CipherMode}, hash: {luksMetadata.HashSpec}",
                    luksMetadata.ActiveKeySlots.Select(slot => slot.Index),
                    out var passphrase))
                {
                    try
                    {
                        Cursor = Cursors.WaitCursor;
                        fs = FileSystemDetector.TryOpenWithLuksPassphrase(_reader, partition, passphrase, out error);
                    }
                    finally
                    {
                        Array.Clear(passphrase);
                    }

                    if (fs is not null)
                    {
                        break;
                    }

                    Cursor = Cursors.Default;
                    var retry = MessageBox.Show(
                        this,
                        $"{error}{Environment.NewLine}{Environment.NewLine}パスフレーズを再入力しますか？",
                        "LUKS1解除失敗",
                        MessageBoxButtons.RetryCancel,
                        MessageBoxIcon.Warning);
                    if (retry != DialogResult.Retry)
                    {
                        _statusLabel.Text = "LUKS1解除をキャンセルしました";
                        return null;
                    }
                }

                if (fs is null)
                {
                    _statusLabel.Text = "LUKS1解除をキャンセルしました";
                    return null;
                }
            }

            if (fs is null && TryReadLuks2UnlockMetadata(partition, out var luks2Metadata))
            {
                if (luks2Metadata.SupportedKeySlots.Count == 0)
                {
                    MessageBox.Show(
                        this,
                        $"対応するLUKS2 keyslotがありません。{Environment.NewLine}" +
                        string.Join(Environment.NewLine, luks2Metadata.UnsupportedKeySlots.Select(
                            slot => $"keyslot {slot.Index}: {slot.UnsupportedReason}")),
                        "LUKS2解除未対応",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    return null;
                }

                Cursor = Cursors.Default;
                while (TryPromptForLuksPassphrase(
                    "LUKS2",
                    $"暗号方式: {luks2Metadata.Segment.Encryption}, sector: {luks2Metadata.Segment.SectorSize} bytes, " +
                    $"KDF: {string.Join(", ", luks2Metadata.SupportedKeySlots.Select(slot => slot.KdfType).Distinct())}",
                    luks2Metadata.SupportedKeySlots.Select(slot => slot.Index),
                    out var passphrase))
                {
                    try
                    {
                        Cursor = Cursors.WaitCursor;
                        fs = FileSystemDetector.TryOpenWithLuksPassphrase(_reader, partition, passphrase, out error);
                    }
                    finally
                    {
                        Array.Clear(passphrase);
                    }

                    if (fs is not null)
                    {
                        break;
                    }

                    Cursor = Cursors.Default;
                    var retry = MessageBox.Show(
                        this,
                        $"{error}{Environment.NewLine}{Environment.NewLine}パスフレーズを再入力しますか？",
                        "LUKS2解除失敗",
                        MessageBoxButtons.RetryCancel,
                        MessageBoxIcon.Warning);
                    if (retry != DialogResult.Retry)
                    {
                        _statusLabel.Text = "LUKS2解除をキャンセルしました";
                        return null;
                    }
                }

                if (fs is null)
                {
                    _statusLabel.Text = "LUKS2解除をキャンセルしました";
                    return null;
                }
            }

            if (fs is null)
            {
                MessageBox.Show(this, error, "ファイルシステム未対応", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return null;
            }

            if (fs is BtrfsFileSystem { IsDegraded: true } degradedBtrfs)
            {
                _analysisWarnings.Add(
                    "Btrfsをdegraded読み取りで開きました。欠損device: devid="
                    + string.Join(",", degradedBtrfs.MissingDeviceIds));
                RefreshWarnings();
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

    private bool TryReadBitLockerUnlockMetadata(PartitionInfo partition, out BitLockerMetadata metadata)
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
            || (!parsed.HasRecoveryPasswordProtector
                && !parsed.HasPasswordProtector
                && !parsed.HasStartupKeyProtector))
        {
            return false;
        }

        metadata = parsed;
        return true;
    }

    private bool TryReadLuks1UnlockMetadata(PartitionInfo partition, out Luks1Metadata metadata)
    {
        metadata = null!;
        if (_reader is null
            || !partition.FileSystem.Equals("LUKS1", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var slice = new PartitionSliceReader(_reader, partition);
        if (!Luks1MetadataReader.TryRead(slice, out var parsed, out _) || parsed is null)
        {
            return false;
        }

        metadata = parsed;
        return true;
    }

    private bool TryReadLuks2UnlockMetadata(PartitionInfo partition, out Luks2Metadata metadata)
    {
        metadata = null!;
        if (_reader is null
            || !partition.FileSystem.Equals("LUKS2", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var slice = new PartitionSliceReader(_reader, partition);
        if (!Luks2MetadataReader.TryRead(slice, out var parsed, out _) || parsed is null)
        {
            return false;
        }

        metadata = parsed;
        return true;
    }

    private bool TryPromptForBitLockerCredential(
        BitLockerMetadata metadata,
        out byte[] recoveryPasswordKey,
        out char[] password,
        out BitLockerStartupKey? startupKey)
    {
        recoveryPasswordKey = Array.Empty<byte>();
        password = Array.Empty<char>();
        startupKey = null;
        byte[]? decodedKey = null;
        char[]? passwordCharacters = null;
        BitLockerStartupKey? selectedStartupKey = null;

        using var dialog = new Form
        {
            Text = "BitLocker解除",
            ClientSize = new Size(620, 270),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.CenterParent
        };
        var recoveryProtectorIds = metadata.KeyProtectors
            .Where(protector => protector.ProtectionType == BitLockerProtectionType.RecoveryPassword)
            .Select(protector => protector.Identifier.ToString("B"))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var explanation = new Label
        {
            AutoSize = false,
            Location = new Point(16, 14),
            Size = new Size(588, 62)
        };
        var credentialType = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Location = new Point(16, 82),
            Size = new Size(240, 28)
        };
        if (metadata.HasRecoveryPasswordProtector)
        {
            credentialType.Items.Add("48桁の回復パスワード");
        }
        if (metadata.HasPasswordProtector)
        {
            credentialType.Items.Add("BitLockerパスワード");
        }
        if (metadata.HasStartupKeyProtector)
        {
            credentialType.Items.Add("スタートアップキー (.BEK)");
        }
        credentialType.SelectedIndex = 0;
        var passwordBox = new TextBox
        {
            Location = new Point(16, 120),
            Size = new Size(588, 27),
            UseSystemPasswordChar = true,
            MaxLength = 96
        };
        var startupKeyPath = new TextBox
        {
            Location = new Point(16, 120),
            Size = new Size(484, 27),
            ReadOnly = true,
            Visible = false
        };
        var browseStartupKey = new Button
        {
            Location = new Point(508, 119),
            Size = new Size(96, 29),
            Text = "参照...",
            Visible = false
        };
        var showPassword = new CheckBox
        {
            AutoSize = true,
            Location = new Point(16, 158),
            Text = "入力内容を表示"
        };
        var okButton = new Button
        {
            Location = new Point(420, 218),
            Size = new Size(88, 32),
            Text = "解除"
        };
        var cancelButton = new Button
        {
            DialogResult = DialogResult.Cancel,
            Location = new Point(516, 218),
            Size = new Size(88, 32),
            Text = "キャンセル"
        };

        bool UsesRecoveryPassword() => credentialType.SelectedItem?.ToString()?.StartsWith("48", StringComparison.Ordinal) == true;
        bool UsesStartupKey() => credentialType.SelectedItem?.ToString()?.StartsWith("スタートアップ", StringComparison.Ordinal) == true;
        void UpdateCredentialExplanation()
        {
            var recovery = UsesRecoveryPassword();
            var external = UsesStartupKey();
            explanation.Text = recovery
                ? $"このボリュームはBitLockerで保護されています。48桁の回復パスワードを入力してください。{Environment.NewLine}" +
                    $"形式: 000000-000000-000000-000000-000000-000000-000000-000000{Environment.NewLine}" +
                    $"回復キーID: {string.Join(", ", recoveryProtectorIds)}"
                : external
                    ? $"このボリュームに対応するBitLockerスタートアップキー（.BEK）を選択してください。{Environment.NewLine}" +
                        "キーデータは解除中のみ保持し、保存・ログ出力しません。"
                    : $"このボリュームはBitLockerで保護されています。設定したBitLockerパスワードを入力してください。{Environment.NewLine}" +
                        "パスワードは保存・ログ出力されません。";
            passwordBox.MaxLength = recovery ? 96 : short.MaxValue;
            passwordBox.Clear();
            passwordBox.Visible = !external;
            showPassword.Visible = !external;
            startupKeyPath.Visible = external;
            browseStartupKey.Visible = external;
            if (external)
            {
                startupKeyPath.Focus();
            }
        }

        credentialType.SelectedIndexChanged += (_, _) => UpdateCredentialExplanation();
        showPassword.CheckedChanged += (_, _) => passwordBox.UseSystemPasswordChar = !showPassword.Checked;
        browseStartupKey.Click += (_, _) =>
        {
            using var picker = new OpenFileDialog
            {
                Title = "BitLockerスタートアップキーを選択",
                Filter = "BitLocker startup key (*.bek)|*.bek|すべてのファイル (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false
            };
            if (picker.ShowDialog(dialog) == DialogResult.OK)
            {
                startupKeyPath.Text = picker.FileName;
            }
        };
        okButton.Click += (_, _) =>
        {
            if (UsesStartupKey())
            {
                if (!BitLockerStartupKey.TryRead(startupKeyPath.Text, out var candidate, out var validationError)
                    || candidate is null)
                {
                    MessageBox.Show(dialog, validationError, "スタートアップキーの確認", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    browseStartupKey.Focus();
                    return;
                }

                if (!metadata.KeyProtectors.Any(protector =>
                    protector.ProtectionType == BitLockerProtectionType.StartupKey
                    && protector.Identifier == candidate.Identifier))
                {
                    candidate.Dispose();
                    MessageBox.Show(
                        dialog,
                        "選択したスタートアップキーの識別子は、このボリュームの保護子と一致しません。",
                        "スタートアップキーの確認",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    browseStartupKey.Focus();
                    return;
                }

                selectedStartupKey?.Dispose();
                selectedStartupKey = candidate;
            }
            else if (UsesRecoveryPassword())
            {
                if (!BitLockerRecoveryPassword.TryDecode(passwordBox.Text, out var candidate, out var validationError))
                {
                    MessageBox.Show(dialog, validationError, "回復パスワードの確認", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    passwordBox.Focus();
                    passwordBox.SelectAll();
                    return;
                }

                decodedKey = candidate;
            }
            else
            {
                if (!BitLockerPassword.TryDeriveInitialHash(passwordBox.Text, out var validationHash, out var validationError))
                {
                    MessageBox.Show(dialog, validationError, "パスワードの確認", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    passwordBox.Focus();
                    passwordBox.SelectAll();
                    return;
                }

                CryptographicOperations.ZeroMemory(validationHash);
                passwordCharacters = passwordBox.Text.ToCharArray();
            }
            dialog.DialogResult = DialogResult.OK;
            dialog.Close();
        };
        dialog.FormClosed += (_, _) => passwordBox.Clear();
        dialog.Controls.AddRange([
            explanation,
            credentialType,
            passwordBox,
            startupKeyPath,
            browseStartupKey,
            showPassword,
            okButton,
            cancelButton
        ]);
        dialog.AcceptButton = okButton;
        dialog.CancelButton = cancelButton;
        dialog.Shown += (_, _) => passwordBox.Focus();
        UpdateCredentialExplanation();

        if (dialog.ShowDialog(this) != DialogResult.OK
            || decodedKey is null && passwordCharacters is null && selectedStartupKey is null)
        {
            if (decodedKey is not null)
            {
                CryptographicOperations.ZeroMemory(decodedKey);
            }
            if (passwordCharacters is not null)
            {
                Array.Clear(passwordCharacters);
            }
            selectedStartupKey?.Dispose();

            return false;
        }

        recoveryPasswordKey = decodedKey ?? Array.Empty<byte>();
        password = passwordCharacters ?? Array.Empty<char>();
        startupKey = selectedStartupKey;
        return true;
    }

    private bool TryPromptForLuksPassphrase(
        string version,
        string details,
        IEnumerable<int> keySlots,
        out char[] passphrase)
    {
        passphrase = Array.Empty<char>();
        char[]? passphraseCharacters = null;
        using var dialog = new Form
        {
            Text = $"{version}解除",
            ClientSize = new Size(620, 220),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.CenterParent
        };
        var explanation = new Label
        {
            AutoSize = false,
            Location = new Point(16, 14),
            Size = new Size(588, 62),
            Text = $"{version}パスフレーズを入力してください。{details}{Environment.NewLine}" +
                $"対応keyslot: {string.Join(", ", keySlots)}。" +
                "パスフレーズは保存・ログ出力されません。"
        };
        var passphraseBox = new TextBox
        {
            Location = new Point(16, 84),
            Size = new Size(588, 27),
            UseSystemPasswordChar = true,
            MaxLength = 4096
        };
        var showPassphrase = new CheckBox
        {
            AutoSize = true,
            Location = new Point(16, 122),
            Text = "入力内容を表示"
        };
        var okButton = new Button
        {
            Location = new Point(420, 170),
            Size = new Size(88, 32),
            Text = "解除"
        };
        var cancelButton = new Button
        {
            DialogResult = DialogResult.Cancel,
            Location = new Point(516, 170),
            Size = new Size(88, 32),
            Text = "キャンセル"
        };

        showPassphrase.CheckedChanged += (_, _) =>
            passphraseBox.UseSystemPasswordChar = !showPassphrase.Checked;
        okButton.Click += (_, _) =>
        {
            if (passphraseBox.TextLength == 0)
            {
                MessageBox.Show(
                    dialog,
                    $"{version}パスフレーズを入力してください。",
                    "パスフレーズの確認",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                passphraseBox.Focus();
                return;
            }

            passphraseCharacters = passphraseBox.Text.ToCharArray();
            dialog.DialogResult = DialogResult.OK;
            dialog.Close();
        };
        dialog.FormClosed += (_, _) => passphraseBox.Clear();
        dialog.Controls.AddRange([explanation, passphraseBox, showPassphrase, okButton, cancelButton]);
        dialog.AcceptButton = okButton;
        dialog.CancelButton = cancelButton;
        dialog.Shown += (_, _) => passphraseBox.Focus();

        if (dialog.ShowDialog(this) != DialogResult.OK || passphraseCharacters is null)
        {
            if (passphraseCharacters is not null)
            {
                Array.Clear(passphraseCharacters);
            }

            return false;
        }

        passphrase = passphraseCharacters;
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

    private void DisposeCompanionReaders()
    {
        foreach (var reader in _companionReaders)
        {
            reader.Dispose();
        }

        _companionReaders.Clear();
        _btrfsDevices = [];
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

    private void QueueSelectedFileWrite()
    {
        if (_isWritingImage || _isLoadingImage)
        {
            _statusLabel.Text = "イメージの読み込み・RAW保存中は変更予定を編集できません";
            return;
        }

        if (!TryGetSelectedEditableFile("内容を変更する通常ファイルを1個選択してください。", out var file, out var path)
            || !TryPreparePendingEditContext())
        {
            return;
        }

        using var dialog = new OpenFileDialog
        {
            Title = $"{file.Name} の新しい内容を選択（サイズ変更可）",
            Filter = "すべてのファイル (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            AddPendingEdit(new PendingFileEdit(FileEditOperationKind.WriteContent, path, dialog.FileName));
        }
    }

    private void QueueFileCreation()
    {
        if (_isWritingImage || _isLoadingImage)
        {
            _statusLabel.Text = "イメージの読み込み・RAW保存中は変更予定を編集できません";
            return;
        }

        if (_currentDirectory is null || _currentFileSystem is null || !TryPreparePendingEditContext())
        {
            return;
        }

        using var contentDialog = new OpenFileDialog
        {
            Title = "追加するファイルの内容を選択",
            Filter = "すべてのファイル (*.*)|*.*",
        };
        if (contentDialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var name = PromptForNewFileName(Path.GetFileName(contentDialog.FileName));
        if (name is null)
        {
            return;
        }

        AddPendingEdit(new PendingFileEdit(
            FileEditOperationKind.CreateFile,
            VirtualPath.Combine(_currentDirectoryPath, name),
            contentDialog.FileName));
    }

    private async Task QueueSelectedFileExternalEditAsync()
    {
        if (_isWritingImage || _isLoadingImage)
        {
            _statusLabel.Text = "イメージの読み込み・RAW保存中は外部編集を開始できません";
            return;
        }

        if (!TryGetSelectedEditableFile("外部エディターで編集する通常ファイルを1個選択してください。", out var file, out var path)
            || !TryPreparePendingEditContext()
            || _currentFileSystem is null)
        {
            return;
        }

        var fileSystem = _currentFileSystem;
        string? workingPath = null;
        try
        {
            workingPath = await CreateExternalWorkingCopyAsync(
                token => PendingEditContentStore.CreateWorkingCopy(fileSystem, file, token),
                file.Name);
            if (workingPath is null)
            {
                return;
            }

            var capturedPath = await EditAndCaptureExternalWorkingCopyAsync(workingPath, path);
            workingPath = null;
            if (capturedPath is not null)
            {
                AddPendingEdit(new PendingFileEdit(FileEditOperationKind.WriteContent, path, capturedPath));
            }
        }
        catch (OperationCanceledException)
        {
            _statusLabel.Text = "外部編集用ファイルの取り出しをキャンセルしました";
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or InvalidDataException
                                   or ArgumentException
                                   or NotSupportedException)
        {
            MessageBox.Show(this, ex.Message, "外部編集の準備エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _statusLabel.Text = "外部編集を開始できませんでした";
        }
        finally
        {
            if (workingPath is not null)
            {
                PendingEditContentStore.TryDeleteOwnedFile(workingPath);
            }
        }
    }

    private async Task QueueExternalFileCreationAsync()
    {
        if (_isWritingImage || _isLoadingImage)
        {
            _statusLabel.Text = "イメージの読み込み・RAW保存中は外部編集を開始できません";
            return;
        }

        if (_currentDirectory is null || _currentFileSystem is null || !TryPreparePendingEditContext())
        {
            return;
        }

        var name = PromptForNewFileName("新規ファイル.txt");
        if (name is null)
        {
            return;
        }

        string? workingPath = null;
        try
        {
            workingPath = PendingEditContentStore.CreateEmptyWorkingCopy(name);
            var virtualPath = VirtualPath.Combine(_currentDirectoryPath, name);
            var capturedPath = await EditAndCaptureExternalWorkingCopyAsync(workingPath, virtualPath);
            workingPath = null;
            if (capturedPath is not null)
            {
                AddPendingEdit(new PendingFileEdit(FileEditOperationKind.CreateFile, virtualPath, capturedPath));
            }
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or ArgumentException
                                   or NotSupportedException)
        {
            MessageBox.Show(this, ex.Message, "外部編集の準備エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _statusLabel.Text = "外部編集を開始できませんでした";
        }
        finally
        {
            if (workingPath is not null)
            {
                PendingEditContentStore.TryDeleteOwnedFile(workingPath);
            }
        }

    }

    private void ReplaceSelectedPendingEditContent()
    {
        if (_isWritingImage || _isLoadingImage)
        {
            _statusLabel.Text = "イメージの読み込み・RAW保存中は変更予定を編集できません";
            return;
        }

        if (!TryGetSelectedPendingContentEdit(out var index, out var edit, showMessage: true))
        {
            return;
        }

        using var dialog = new OpenFileDialog
        {
            Title = $"{edit.VirtualPath} の新しい内容元を選択",
            Filter = "すべてのファイル (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        ReplacePendingEditContent(index, dialog.FileName);
    }

    private async Task EditSelectedPendingContentExternallyAsync()
    {
        if (_isWritingImage || _isLoadingImage)
        {
            _statusLabel.Text = "イメージの読み込み・RAW保存中は変更予定を編集できません";
            return;
        }

        if (!TryGetSelectedPendingContentEdit(out var index, out var edit, showMessage: true))
        {
            return;
        }

        var contentPath = edit.ContentPath!;
        string? workingPath = null;
        try
        {
            workingPath = await CreateExternalWorkingCopyAsync(
                token => PendingEditContentStore.CreateWorkingCopy(contentPath, Path.GetFileName(edit.VirtualPath), token),
                Path.GetFileName(edit.VirtualPath));
            if (workingPath is null)
            {
                return;
            }

            var capturedPath = await EditAndCaptureExternalWorkingCopyAsync(workingPath, edit.VirtualPath);
            workingPath = null;
            if (capturedPath is not null)
            {
                ReplacePendingEditContent(index, capturedPath);
            }
        }
        catch (OperationCanceledException)
        {
            _statusLabel.Text = "外部編集用ファイルのコピーをキャンセルしました";
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or ArgumentException
                                   or NotSupportedException)
        {
            MessageBox.Show(this, ex.Message, "外部編集の準備エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _statusLabel.Text = "外部編集を開始できませんでした";
        }
        finally
        {
            if (workingPath is not null)
            {
                PendingEditContentStore.TryDeleteOwnedFile(workingPath);
            }
        }
    }

    private async Task<string?> CreateExternalWorkingCopyAsync(
        Func<CancellationToken, string> create,
        string displayName)
    {
        var cancellation = new CancellationTokenSource();
        _copyCancellations.Add(cancellation);
        _cancelCopyButton.Enabled = true;
        try
        {
            if (_copyCancellations.Count > 1)
            {
                _statusLabel.Text = $"外部編集用コピーの待機中: {displayName}";
            }

            await _copyExecutionGate.WaitAsync(cancellation.Token);
            try
            {
                _statusLabel.Text = $"外部編集用に取り出しています: {displayName}";
                return await Task.Run(() => create(cancellation.Token), cancellation.Token);
            }
            finally
            {
                _copyExecutionGate.Release();
            }
        }
        finally
        {
            _copyCancellations.Remove(cancellation);
            cancellation.Dispose();
            _cancelCopyButton.Enabled = _copyCancellations.Count > 0;
        }
    }

    private async Task<string?> EditAndCaptureExternalWorkingCopyAsync(string workingPath, string virtualPath)
    {
        if (!TryStartExternalEditor(workingPath, editorPath: null, this)
            && !TryChooseAndStartExternalEditor(workingPath, this))
        {
            PendingEditContentStore.TryDeleteOwnedFile(workingPath);
            return null;
        }

        while (ShowExternalEditImportDialog(workingPath, virtualPath) == DialogResult.OK)
        {
            try
            {
                var capturedPath = await CreateExternalWorkingCopyAsync(
                    token => PendingEditContentStore.CaptureWorkingCopy(
                        workingPath,
                        Path.GetFileName(virtualPath),
                        token),
                    Path.GetFileName(virtualPath));
                PendingEditContentStore.TryDeleteOwnedFile(workingPath);
                _statusLabel.Text = $"外部エディターの内容を取り込みました: {virtualPath}";
                return capturedPath;
            }
            catch (OperationCanceledException)
            {
                _statusLabel.Text = $"外部編集内容の取り込みをキャンセルしました: {virtualPath}";
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (MessageBox.Show(
                        this,
                        $"編集内容を取り込めませんでした。外部エディターで保存し、必要ならファイルを閉じてから再試行してください。"
                        + Environment.NewLine
                        + Environment.NewLine
                        + ex.Message,
                        "外部編集内容の取り込みエラー",
                        MessageBoxButtons.RetryCancel,
                        MessageBoxIcon.Warning) != DialogResult.Retry)
                {
                    break;
                }
            }
        }

        PendingEditContentStore.TryDeleteOwnedFile(workingPath);
        _statusLabel.Text = $"外部編集の取り込みをキャンセルしました: {virtualPath}";
        return null;
    }

    private DialogResult ShowExternalEditImportDialog(string workingPath, string virtualPath)
    {
        using var dialog = new Form
        {
            Text = "外部エディターから変更を取り込む",
            Width = 720,
            Height = 235,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
        };
        var explanation = new Label
        {
            Left = 14,
            Top = 14,
            Width = 675,
            Height = 56,
            Text = $"{virtualPath}{Environment.NewLine}外部エディターで保存してから［保存内容を取り込む］を押してください。取り込み後の編集は自動反映されません。",
        };
        var pathBox = new TextBox
        {
            Left = 14,
            Top = 74,
            Width = 675,
            ReadOnly = true,
            Text = workingPath,
        };
        var reopenButton = new Button { Left = 14, Top = 116, Width = 145, Text = "既定アプリで開く" };
        reopenButton.Click += (_, _) => TryStartExternalEditor(workingPath, editorPath: null, dialog);
        var chooseButton = new Button { Left = 169, Top = 116, Width = 150, Text = "エディターを指定..." };
        chooseButton.Click += (_, _) => TryChooseAndStartExternalEditor(workingPath, dialog);
        var importButton = new Button
        {
            Left = 431,
            Top = 116,
            Width = 160,
            Text = "保存内容を取り込む",
            DialogResult = DialogResult.OK,
        };
        var cancelButton = new Button
        {
            Left = 601,
            Top = 116,
            Width = 88,
            Text = "キャンセル",
            DialogResult = DialogResult.Cancel,
        };
        dialog.Controls.AddRange([explanation, pathBox, reopenButton, chooseButton, importButton, cancelButton]);
        dialog.AcceptButton = importButton;
        dialog.CancelButton = cancelButton;
        return dialog.ShowDialog(this);
    }

    private static bool TryStartExternalEditor(string workingPath, string? editorPath, IWin32Window owner)
    {
        if (editorPath is null && !ExternalEditorSafety.CanOpenWithAssociatedApplication(workingPath))
        {
            MessageBox.Show(
                owner,
                "実行可能ファイルやスクリプトを既定アプリで開くと、その内容を実行する危険があります。"
                + Environment.NewLine
                + "［エディターを指定］から、信頼できるテキスト／バイナリエディターを選択してください。",
                "関連付け起動を停止しました",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return false;
        }

        if (editorPath is not null
            && string.Equals(Path.GetFullPath(editorPath), Path.GetFullPath(workingPath), StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(
                owner,
                "編集対象そのものをエディターとして実行することはできません。",
                "外部エディターの指定エラー",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return false;
        }

        try
        {
            var startInfo = editorPath is null
                ? new ProcessStartInfo
                {
                    FileName = workingPath,
                    UseShellExecute = true,
                    Verb = "open",
                }
                : new ProcessStartInfo
                {
                    FileName = editorPath,
                    UseShellExecute = true,
                };
            if (editorPath is not null)
            {
                startInfo.ArgumentList.Add(workingPath);
            }

            Process.Start(startInfo);
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            MessageBox.Show(
                owner,
                $"外部エディターを起動できませんでした。{Environment.NewLine}{ex.Message}",
                "外部エディター起動エラー",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return false;
        }
    }

    private static bool TryChooseAndStartExternalEditor(string workingPath, IWin32Window owner)
    {
        using var dialog = new OpenFileDialog
        {
            Title = "使用する外部エディターを選択",
            Filter = "実行ファイル (*.exe;*.cmd;*.bat;*.com)|*.exe;*.cmd;*.bat;*.com|すべてのファイル (*.*)|*.*",
            CheckFileExists = true,
        };
        return dialog.ShowDialog(owner) == DialogResult.OK
            && TryStartExternalEditor(workingPath, dialog.FileName, owner);
    }

    private PendingEditContentStore PendingEditContentStore =>
        _pendingEditContentStore ??= new PendingEditContentStore();

    private void QueueSelectedFileDeletion()
    {
        if (_isWritingImage || _isLoadingImage)
        {
            _statusLabel.Text = "イメージの読み込み・RAW保存中は変更予定を編集できません";
            return;
        }

        if (!TryGetSelectedEditableFile("削除予定に追加する通常ファイルを1個選択してください。", out _, out var path)
            || !TryPreparePendingEditContext())
        {
            return;
        }

        AddPendingEdit(new PendingFileEdit(FileEditOperationKind.DeleteFile, path));
    }

    private void QueueDirectoryCreation()
    {
        if (!CanQueuePendingEdit() || _currentDirectory is null || !TryPreparePendingEditContext())
        {
            return;
        }

        var name = PromptForNewFileName("New folder", isDirectory: true);
        if (name is not null)
        {
            AddPendingEdit(new PendingFileEdit(
                FileEditOperationKind.CreateDirectory,
                VirtualPath.Combine(_currentDirectoryPath, name)));
        }
    }

    private void QueueSelectedDirectoryDeletion()
    {
        if (!CanQueuePendingEdit()
            || !TryGetSelectedEditableEntry(
                "削除予定に追加するディレクトリを1個選択してください。",
                out var directory,
                out var path))
        {
            return;
        }

        if (!directory.IsDirectory)
        {
            MessageBox.Show(this, "削除予定に追加するディレクトリを1個選択してください。", "実験的なファイル編集", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (!TryPreparePendingEditContext())
        {
            return;
        }

        AddPendingEdit(new PendingFileEdit(FileEditOperationKind.DeleteDirectory, path));
    }

    private void QueueSelectedEntryMove()
    {
        if (!CanQueuePendingEdit()
            || !TryGetSelectedEditableEntry(
                "移動または名前変更する項目を1個選択してください。",
                out _,
                out var sourcePath)
            || !TryPreparePendingEditContext())
        {
            return;
        }

        var destinationPath = PromptForVirtualPath(
            "移動・名前変更先",
            "移動先の絶対仮想パス（例: /Folder/New name.bin）",
            sourcePath);
        if (destinationPath is not null)
        {
            AddPendingEdit(new PendingFileEdit(
                FileEditOperationKind.MoveEntry,
                sourcePath,
                DestinationVirtualPath: destinationPath));
        }
    }

    private void QueueSelectedEntryAttributes()
    {
        if (!CanQueuePendingEdit()
            || !TryGetSelectedEditableEntry(
                "属性を変更する項目を1個選択してください。",
                out var entry,
                out var path)
            || !TryPreparePendingEditContext())
        {
            return;
        }

        var attributes = PromptForAttributes(entry);
        if (attributes.HasValue)
        {
            AddPendingEdit(new PendingFileEdit(
                FileEditOperationKind.SetAttributes,
                path,
                Attributes: attributes.Value));
        }
    }

    private void QueueSelectedEntryTimestamp()
    {
        if (!CanQueuePendingEdit()
            || !TryGetSelectedEditableEntry(
                "更新日時を変更する項目を1個選択してください。",
                out var entry,
                out var path)
            || !TryPreparePendingEditContext())
        {
            return;
        }

        var modifiedUtc = PromptForUtcTimestamp(entry.ModifiedUtc ?? DateTime.UtcNow);
        if (modifiedUtc.HasValue)
        {
            AddPendingEdit(new PendingFileEdit(
                FileEditOperationKind.SetLastWriteTimeUtc,
                path,
                ModifiedUtc: modifiedUtc.Value));
        }
    }

    private bool CanQueuePendingEdit()
    {
        if (!_isWritingImage && !_isLoadingImage)
        {
            return true;
        }

        _statusLabel.Text = "イメージの読み込み・RAW保存中は変更予定を編集できません";
        return false;
    }

    private bool TryGetSelectedEditableEntry(string message, out VfsNode entry, out string path)
    {
        entry = null!;
        path = string.Empty;
        if (_fileList.SelectedItems.Count != 1
            || _fileList.SelectedItems[0].Tag is not VfsNode selected)
        {
            MessageBox.Show(this, message, "実験的なファイル編集", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }

        entry = selected;
        path = VirtualPath.Normalize(
            string.IsNullOrWhiteSpace(selected.VirtualPath)
                ? GetListItemPath(_fileList.SelectedItems[0])
                : selected.VirtualPath);
        return path != "/";
    }

    private bool TryGetSelectedEditableFile(string message, out VfsNode file, out string path)
    {
        file = null!;
        path = string.Empty;
        if (_fileList.SelectedItems.Count != 1
            || _fileList.SelectedItems[0].Tag is not VfsNode selected
            || selected.IsDirectory)
        {
            MessageBox.Show(this, message, "実験的なファイル編集", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }

        file = selected;
        path = VirtualPath.Normalize(
            string.IsNullOrWhiteSpace(selected.VirtualPath)
                ? GetListItemPath(_fileList.SelectedItems[0])
                : selected.VirtualPath);
        return true;
    }

    private bool TryPreparePendingEditContext()
    {
        if (_reader is null || _currentFileSystem is null)
        {
            MessageBox.Show(this, "編集するファイルシステムを開いてください。", "実験的なファイル編集", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }

        ResetPendingEditContextIfEmpty();
        if (_pendingEditFileSystem is not null
            && !ReferenceEquals(_pendingEditFileSystem.Partition, _currentFileSystem.Partition))
        {
            MessageBox.Show(
                this,
                "変更一覧には別のパーティションの操作が含まれています。先に保存するか、変更一覧を取り消してください。",
                "パーティションが異なります",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return false;
        }

        if (!FileEditBatchService.CanEdit(_reader, _currentFileSystem.Partition, _currentFileSystem, out var reason))
        {
            MessageBox.Show(this, reason, "このファイルシステムは編集できません", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }

        return true;
    }

    private void AddPendingEdit(PendingFileEdit edit)
    {
        _pendingEditFileSystem ??= _currentFileSystem
            ?? throw new InvalidOperationException("編集対象のファイルシステムが選択されていません。");
        _pendingFileEdits.Add(edit with
        {
            VirtualPath = VirtualPath.Normalize(edit.VirtualPath),
            DestinationVirtualPath = edit.DestinationVirtualPath is null
                ? null
                : VirtualPath.Normalize(edit.DestinationVirtualPath),
        });
        RefreshPendingEditList();
        _explorerDetailTabs.SelectedIndex = 1;
        _statusLabel.Text = $"変更予定に追加しました: {edit.VirtualPath}";
    }

    private void RefreshPendingEditList()
    {
        _pendingEditList.BeginUpdate();
        try
        {
            _pendingEditList.Items.Clear();
            foreach (var edit in _pendingFileEdits)
            {
                var operation = edit.Operation switch
                {
                    FileEditOperationKind.WriteContent => "内容変更",
                    FileEditOperationKind.CreateFile => "ファイル追加",
                    FileEditOperationKind.DeleteFile => "ファイル削除",
                    FileEditOperationKind.CreateDirectory => "ディレクトリ作成",
                    FileEditOperationKind.DeleteDirectory => "ディレクトリ削除",
                    FileEditOperationKind.MoveEntry => "移動・名前変更",
                    FileEditOperationKind.SetAttributes => "属性変更",
                    FileEditOperationKind.SetLastWriteTimeUtc => "更新日時変更",
                    _ => edit.Operation.ToString(),
                };
                var item = new ListViewItem(operation) { Tag = edit };
                item.SubItems.Add(edit.VirtualPath);
                item.SubItems.Add(edit.Operation switch
                {
                    FileEditOperationKind.MoveEntry => edit.DestinationVirtualPath ?? "-",
                    FileEditOperationKind.SetAttributes => edit.Attributes?.ToString() ?? "-",
                    FileEditOperationKind.SetLastWriteTimeUtc => edit.ModifiedUtc?.ToString("O") ?? "-",
                    _ => edit.ContentPath ?? "-",
                });
                if (edit.ContentPath is not null)
                {
                    try
                    {
                        var info = new FileInfo(edit.ContentPath);
                        item.SubItems.Add(info.Exists ? FormatBytes(info.Length) : "見つかりません");
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                    {
                        item.SubItems.Add("確認できません");
                    }
                }
                else
                {
                    item.SubItems.Add("-");
                }

                _pendingEditList.Items.Add(item);
            }
        }
        finally
        {
            _pendingEditList.EndUpdate();
        }

        UpdatePendingContentButtons();
    }

    private void UpdatePendingContentButtons()
    {
        var enabled = !_isWritingImage
            && !_isLoadingImage
            && TryGetSelectedPendingContentEdit(out _, out _, showMessage: false);
        _pendingReplaceContentButton.Enabled = enabled;
        _pendingExternalEditButton.Enabled = enabled;
    }

    private bool TryGetSelectedPendingContentEdit(
        out int index,
        out PendingFileEdit edit,
        bool showMessage)
    {
        index = -1;
        edit = null!;
        if (_pendingEditList.SelectedIndices.Count == 1)
        {
            var selectedIndex = _pendingEditList.SelectedIndices[0];
            if (selectedIndex >= 0 && selectedIndex < _pendingFileEdits.Count)
            {
                var selected = _pendingFileEdits[selectedIndex];
                if (selected.Operation is FileEditOperationKind.CreateFile or FileEditOperationKind.WriteContent
                    && !string.IsNullOrWhiteSpace(selected.ContentPath))
                {
                    index = selectedIndex;
                    edit = selected;
                    return true;
                }
            }
        }

        if (showMessage)
        {
            MessageBox.Show(
                this,
                "内容を編集するファイル追加または内容変更の予定を1件選択してください。",
                "変更予定の内容編集",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        return false;
    }

    private void ReplacePendingEditContent(int index, string contentPath)
    {
        contentPath = Path.GetFullPath(contentPath);
        var previous = _pendingFileEdits[index];
        _pendingFileEdits[index] = previous with { ContentPath = contentPath };
        if (previous.ContentPath is not null
            && !string.Equals(previous.ContentPath, contentPath, StringComparison.OrdinalIgnoreCase))
        {
            _pendingEditContentStore?.TryDeleteOwnedFile(previous.ContentPath);
        }

        RefreshPendingEditList();
        if (index < _pendingEditList.Items.Count)
        {
            _pendingEditList.Items[index].Selected = true;
            _pendingEditList.Items[index].Focused = true;
            _pendingEditList.Items[index].EnsureVisible();
        }

        _statusLabel.Text = $"変更予定の内容を差し替えました: {previous.VirtualPath}";
    }

    private void UndoSelectedPendingEdits()
    {
        if (_isWritingImage)
        {
            _statusLabel.Text = "RAW保存中は変更予定を編集できません";
            return;
        }

        var indices = _pendingEditList.SelectedIndices.Cast<int>().OrderByDescending(index => index).ToArray();
        if (indices.Length == 0)
        {
            _statusLabel.Text = "取り消す変更を選択してください";
            return;
        }

        foreach (var index in indices)
        {
            ReleasePendingEditContent(_pendingFileEdits[index]);
            _pendingFileEdits.RemoveAt(index);
        }

        ResetPendingEditContextIfEmpty();
        RefreshPendingEditList();
        _statusLabel.Text = $"変更予定を{indices.Length:N0}件取り消しました";
    }

    private void UndoLastPendingEdit()
    {
        if (_isWritingImage)
        {
            _statusLabel.Text = "RAW保存中は変更予定を編集できません";
            return;
        }

        if (_pendingFileEdits.Count == 0)
        {
            _statusLabel.Text = "取り消す変更はありません";
            return;
        }

        var removed = _pendingFileEdits[^1];
        _pendingFileEdits.RemoveAt(_pendingFileEdits.Count - 1);
        ReleasePendingEditContent(removed);
        ResetPendingEditContextIfEmpty();
        RefreshPendingEditList();
        _statusLabel.Text = $"最後の変更予定を取り消しました: {removed.VirtualPath}";
    }

    private void ClearPendingEditsWithPrompt()
    {
        if (_isWritingImage)
        {
            _statusLabel.Text = "RAW保存中は変更予定を編集できません";
            return;
        }

        if (_pendingFileEdits.Count == 0)
        {
            return;
        }

        if (MessageBox.Show(
                this,
                $"保存していない変更予定{_pendingFileEdits.Count:N0}件をすべて取り消しますか？",
                "変更予定を取り消す",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2) == DialogResult.Yes)
        {
            ClearPendingEdits();
            _statusLabel.Text = "変更予定をすべて取り消しました";
        }
    }

    private bool ConfirmDiscardPendingEdits(string message)
    {
        return _pendingFileEdits.Count == 0
            || MessageBox.Show(
                this,
                $"{message}\r\n\r\n保存していない変更予定: {_pendingFileEdits.Count:N0}件",
                "変更予定の破棄",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) == DialogResult.Yes;
    }

    private void ClearPendingEdits()
    {
        _pendingFileEdits.Clear();
        _pendingEditFileSystem = null;
        _pendingEditContentStore?.Dispose();
        _pendingEditContentStore = null;
        RefreshPendingEditList();
    }

    private void ReleasePendingEditContent(PendingFileEdit edit)
    {
        if (edit.ContentPath is not null)
        {
            _pendingEditContentStore?.TryDeleteOwnedFile(edit.ContentPath);
        }
    }

    private void ResetPendingEditContextIfEmpty()
    {
        if (_pendingFileEdits.Count == 0)
        {
            _pendingEditFileSystem = null;
        }
    }

    private async Task SavePendingEditsAsync()
    {
        if (_isWritingImage || _isLoadingImage)
        {
            _statusLabel.Text = "イメージの読み込み・RAW保存中は保存を開始できません";
            return;
        }

        if (_searchCancellation is not null || _copyCancellations.Count > 0)
        {
            MessageBox.Show(
                this,
                "検索またはホストへのコピーが完了してから変更済みRAWを保存してください。",
                "読み取り処理を実行中です",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        if (_reader is null || _pendingEditFileSystem is null || _pendingFileEdits.Count == 0)
        {
            MessageBox.Show(this, "保存する変更予定がありません。", "実験的なファイル編集", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var isLogicalOutput = _pendingEditFileSystem.Partition.ReaderOverride is not null;
        using var outputDialog = new SaveFileDialog
        {
            Title = isLogicalOutput
                ? "変更済み論理ボリュームを新しいRAWイメージとして保存"
                : "変更済みディスクを新しいRAWイメージとして保存",
            Filter = "RAW disk image (*.raw)|*.raw|Disk image (*.img)|*.img|All files (*.*)|*.*",
            DefaultExt = "raw",
            AddExtension = true,
            OverwritePrompt = false,
            FileName = $"{Path.GetFileNameWithoutExtension(_reader.Path)}-modified{(isLogicalOutput ? "-logical" : "")}.raw",
        };
        if (outputDialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        if (File.Exists(outputDialog.FileName) || Directory.Exists(outputDialog.FileName))
        {
            MessageBox.Show(
                this,
                "安全のため既存ファイルは上書きしません。存在しない新しい名前を指定してください。",
                "出力先が既に存在します",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        var summary = string.Join("\r\n", _pendingFileEdits.Take(8).Select(edit =>
            $"・{FormatEditOperation(edit.Operation)}: {edit.VirtualPath}"));
        if (_pendingFileEdits.Count > 8)
        {
            summary += $"\r\n・ほか {_pendingFileEdits.Count - 8:N0}件";
        }

        if (MessageBox.Show(
                this,
                $"実験的な書き込み機能です。\r\n\r\n{summary}\r\n\r\n"
                    + $"出力: {outputDialog.FileName}\r\n\r\n"
                    + (isLogicalOutput
                        ? "RAID/LVM/復号レイヤーの構成元は変更せず、選択した論理ボリュームを平坦なRAWとして出力します。\r\n"
                        : string.Empty)
                    + "原本は変更せず、変更を上から順に仮適用して検証後、新しいRAWへ保存します。続行しますか？",
                "変更済みRAW保存の確認",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes)
        {
            return;
        }

        if (!ConfirmAndDisposeMounts("変更済みRAWを保存する前に、現在の読み取り専用マウントを解除します。続行しますか？"))
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _writeCancellation = cancellation;
        _isWritingImage = true;
        _cancelWriteButton.Enabled = true;
        _writeProgressBar.Visible = true;
        _writeProgressBar.Style = ProgressBarStyle.Marquee;
        var progress = new Progress<DiskImageProgress>(update =>
        {
            _statusLabel.Text = update.Message;
            if (update.Percentage is int percentage)
            {
                _writeProgressBar.Style = ProgressBarStyle.Blocks;
                _writeProgressBar.Value = Math.Clamp(percentage, 0, 100);
                _statusLabel.Text = $"{update.Message}: {percentage}%";
            }
            else
            {
                _writeProgressBar.Style = ProgressBarStyle.Marquee;
            }
        });

        try
        {
            var source = _reader;
            var fileSystem = _pendingEditFileSystem;
            var edits = _pendingFileEdits.ToArray();
            var result = await Task.Run(() => FileEditBatchService.ApplyToRawAsync(
                source,
                fileSystem.Partition,
                fileSystem,
                edits,
                outputDialog.FileName,
                progress,
                cancellation.Token), cancellation.Token);
            ClearPendingEdits();
            _statusLabel.Text = $"変更済みRAWを保存しました: {result.DestinationPath}";
            MessageBox.Show(
                this,
                $"変更済みRAWを保存しました。\r\n\r\n{result.DestinationPath}\r\n"
                    + (result.IsLogicalVolumeOutput ? "形式: 構成元へ書き戻さない平坦化済み論理RAW\r\n" : string.Empty)
                    + $"変更: {result.EditCount:N0}件\r\n変更ページ: {result.ModifiedPageCount:N0}",
                "ファイル編集完了",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (OperationCanceledException)
        {
            _statusLabel.Text = "変更済みRAWの保存をキャンセルしました";
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "変更済みRAWの保存に失敗しました";
            MessageBox.Show(this, ex.Message, "ファイル編集エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _writeCancellation = null;
            _isWritingImage = false;
            _cancelWriteButton.Enabled = false;
            _writeProgressBar.Visible = false;
            _writeProgressBar.Value = 0;
            if (_closeAfterWriteCancellation && !IsDisposed)
            {
                _closeAfterWriteCancellation = false;
                BeginInvoke(new Action(Close));
            }
        }
    }

    private async Task ApplyPendingEditsToPhysicalDiskAsync()
    {
        if (_isWritingImage || _isLoadingImage)
        {
            _statusLabel.Text = "別の読み込み・書き込み処理が完了してから開始してください";
            return;
        }

        if (_searchCancellation is not null || _copyCancellations.Count > 0)
        {
            MessageBox.Show(
                this,
                "検索またはホストへのコピーが完了してから物理ディスクへ適用してください。",
                "読み取り処理を実行中です",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        if (_reader is null || _pendingEditFileSystem is null || _pendingFileEdits.Count == 0)
        {
            MessageBox.Show(this, "物理ディスクへ適用する変更予定がありません。", "物理ディスク書き込み", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (!PhysicalDiskEditService.CanApply(
                _reader,
                _pendingEditFileSystem.Partition,
                _pendingEditFileSystem,
                out var target,
                out var reason)
            || target is null)
        {
            MessageBox.Show(this, reason, "物理ディスクへ適用できません", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var defaultJournalPath = PhysicalDiskEditService.GetDefaultRecoveryJournalPath(target);
        using var confirmation = new PhysicalDiskWriteConfirmationDialog(
            target,
            defaultJournalPath,
            _pendingFileEdits);
        if (confirmation.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        if (!ConfirmAndDisposeMounts("物理ディスクへ書き込む前に、このアプリの読み取り専用マウントを解除します。続行しますか？"))
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _writeCancellation = cancellation;
        _isWritingImage = true;
        _cancelWriteButton.Enabled = true;
        _writeProgressBar.Visible = true;
        _writeProgressBar.Style = ProgressBarStyle.Marquee;
        var progress = CreateWriteProgress();
        string? reloadPath = null;
        try
        {
            var source = _reader;
            var fileSystem = _pendingEditFileSystem;
            var edits = _pendingFileEdits.ToArray();
            var result = await Task.Run(() => PhysicalDiskEditService.ApplyAsync(
                source,
                fileSystem.Partition,
                fileSystem,
                edits,
                target,
                confirmation.ConfirmationText,
                confirmation.RecoveryJournalPath,
                progress,
                cancellation.Token), cancellation.Token);
            ClearPendingEdits();
            reloadPath = source.Path;
            _statusLabel.Text = $"PhysicalDrive{result.Target.DiskNumber}へ変更を適用しました";
            MessageBox.Show(
                this,
                $"物理ディスクへの書き込みと読み戻し検証が完了しました。\r\n\r\n"
                    + $"対象: {result.Target.DevicePath}\r\n"
                    + $"種類: {(result.Target.IsRemovable ? "リムーバブル／ホットプラグ" : "固定ディスク")}\r\n"
                    + $"変更: {result.EditCount:N0}件\r\n"
                    + $"差分: {result.ModifiedPageCount:N0}ページ ({FormatBytes(result.ModifiedBytes)})\r\n\r\n"
                    + $"復旧ジャーナル:\r\n{result.RecoveryJournalPath}\r\n\r\n"
                    + "復旧が不要と確認できるまではジャーナルを保管してください。",
                "物理ディスク書き込み完了",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (PhysicalDiskCommitException ex)
        {
            _statusLabel.Text = ex.RollbackSucceeded
                ? "物理ディスクへの書き込みに失敗しました（自動復旧済み）"
                : "物理ディスクへの書き込みと自動復旧に失敗しました";
            MessageBox.Show(
                this,
                ex.Message,
                ex.RollbackSucceeded ? "書き込み失敗・自動復旧済み" : "重大な物理ディスク書き込みエラー",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        catch (OperationCanceledException)
        {
            _statusLabel.Text = "物理ディスクへの変更準備をキャンセルしました";
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "物理ディスクへの書き込みを開始できませんでした";
            MessageBox.Show(this, ex.Message, "物理ディスク書き込みエラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            FinishWriteUi();
        }

        if (reloadPath is not null && !IsDisposed)
        {
            await LoadImageAsync(reloadPath);
        }
    }

    private async Task RestorePhysicalDiskAsync()
    {
        if (_isWritingImage || _isLoadingImage)
        {
            _statusLabel.Text = "別の読み込み・書き込み処理が完了してから復旧してください";
            return;
        }

        using var fileDialog = new OpenFileDialog
        {
            Title = "物理ディスク復旧ジャーナルを選択",
            Filter = "Virtual Disk Tools recovery journal (*.vdt-recovery)|*.vdt-recovery|All files (*.*)|*.*",
        };
        if (fileDialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            var journal = PhysicalDiskRecoveryJournal.Read(fileDialog.FileName);
            var target = PhysicalDiskWriteSession.Inspect(journal.Target.DevicePath);
            using var confirmation = new PhysicalDiskWriteConfirmationDialog(
                target,
                fileDialog.FileName,
                isRecovery: true);
            if (confirmation.ShowDialog(this) != DialogResult.OK
                || !ConfirmAndDisposeMounts("物理ディスクを復旧する前に、このアプリの読み取り専用マウントを解除します。続行しますか？"))
            {
                return;
            }

            using var cancellation = new CancellationTokenSource();
            _writeCancellation = cancellation;
            _isWritingImage = true;
            _cancelWriteButton.Enabled = true;
            _writeProgressBar.Visible = true;
            _writeProgressBar.Style = ProgressBarStyle.Marquee;
            var progress = CreateWriteProgress();
            try
            {
                await Task.Run(() => PhysicalDiskEditService.RestoreAsync(
                    fileDialog.FileName,
                    confirmation.ConfirmationText,
                    progress,
                    cancellation.Token), cancellation.Token);
                _statusLabel.Text = $"PhysicalDrive{target.DiskNumber}を変更前へ復旧しました";
                MessageBox.Show(
                    this,
                    $"復旧ジャーナルに保存された変更前データを書き戻し、読み戻し検証を完了しました。\r\n\r\n対象: {target.DevicePath}",
                    "物理ディスク復旧完了",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            catch (OperationCanceledException)
            {
                _statusLabel.Text = "物理ディスク復旧の開始前検証をキャンセルしました";
            }
            catch (Exception ex)
            {
                _statusLabel.Text = "物理ディスクの復旧に失敗しました";
                MessageBox.Show(this, ex.Message, "物理ディスク復旧エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                FinishWriteUi();
            }

            if (_reader is not null
                && string.Equals(_reader.Path, target.DevicePath, StringComparison.OrdinalIgnoreCase)
                && !IsDisposed)
            {
                await LoadImageAsync(target.DevicePath);
            }
        }
        catch (Exception ex) when (ex is IOException
                                   or InvalidDataException
                                   or UnauthorizedAccessException
                                   or NotSupportedException
                                   or ArgumentException)
        {
            MessageBox.Show(this, ex.Message, "復旧ジャーナルを使用できません", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private Progress<DiskImageProgress> CreateWriteProgress() => new(update =>
    {
        _statusLabel.Text = update.Message;
        if (update.Percentage is int percentage)
        {
            _writeProgressBar.Style = ProgressBarStyle.Blocks;
            _writeProgressBar.Value = Math.Clamp(percentage, 0, 100);
            _statusLabel.Text = $"{update.Message}: {percentage}%";
        }
        else
        {
            _writeProgressBar.Style = ProgressBarStyle.Marquee;
        }
    });

    private void FinishWriteUi()
    {
        _writeCancellation = null;
        _isWritingImage = false;
        _cancelWriteButton.Enabled = false;
        _writeProgressBar.Visible = false;
        _writeProgressBar.Value = 0;
        if (_closeAfterWriteCancellation && !IsDisposed)
        {
            _closeAfterWriteCancellation = false;
            BeginInvoke(new Action(Close));
        }
    }

    private string? PromptForNewFileName(string defaultName, bool isDirectory = false)
    {
        using var dialog = new Form
        {
            Text = isDirectory ? "仮想ディスク内のディレクトリ名" : "仮想ディスク内のファイル名",
            Width = 540,
            Height = 165,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
        };
        var label = new Label
        {
            AutoSize = true,
            Left = 12,
            Top = 14,
            Text = $"{_currentDirectoryPath} に作成する{(isDirectory ? "ディレクトリ" : "ファイル")}名",
        };
        var input = new TextBox { Left = 12, Top = 40, Width = 500, Text = defaultName };
        var ok = new Button { Text = "追加", Left = 326, Top = 76, Width = 88 };
        var cancel = new Button { Text = "キャンセル", Left = 424, Top = 76, Width = 88, DialogResult = DialogResult.Cancel };
        ok.Click += (_, _) =>
        {
            var name = input.Text.Trim();
            if (name.Length == 0
                || name.Length > 255
                || name is "." or ".."
                || name.IndexOfAny(['/', '\\', '\0']) >= 0
                || name.Any(char.IsControl))
            {
                MessageBox.Show(dialog, "255文字以内で、パス区切りや制御文字を含まない名前を指定してください。", "名前が不正です", MessageBoxButtons.OK, MessageBoxIcon.Information);
                input.Focus();
                return;
            }

            input.Text = name;
            dialog.DialogResult = DialogResult.OK;
            dialog.Close();
        };
        dialog.Controls.AddRange([label, input, ok, cancel]);
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;
        dialog.Shown += (_, _) =>
        {
            input.SelectAll();
            input.Focus();
        };
        return dialog.ShowDialog(this) == DialogResult.OK ? input.Text : null;
    }

    private string? PromptForVirtualPath(string title, string labelText, string defaultValue)
    {
        using var dialog = new Form
        {
            Text = title,
            Width = 620,
            Height = 165,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
        };
        var label = new Label { AutoSize = true, Left = 12, Top = 14, Text = labelText };
        var input = new TextBox { Left = 12, Top = 40, Width = 580, Text = defaultValue };
        var ok = new Button { Text = "追加", Left = 406, Top = 76, Width = 88 };
        var cancel = new Button { Text = "キャンセル", Left = 504, Top = 76, Width = 88, DialogResult = DialogResult.Cancel };
        ok.Click += (_, _) =>
        {
            var value = input.Text.Trim();
            try
            {
                if (!value.StartsWith('/') && !value.StartsWith('\\'))
                {
                    throw new ArgumentException();
                }

                var normalized = VirtualPath.Normalize(value);
                if (normalized == "/" || VirtualPath.Split(normalized).Any(part => part is "." or ".." || part.Any(char.IsControl)))
                {
                    throw new ArgumentException();
                }

                input.Text = normalized;
                dialog.DialogResult = DialogResult.OK;
                dialog.Close();
            }
            catch (ArgumentException)
            {
                MessageBox.Show(dialog, "ルート以外の有効な絶対仮想パスを指定してください。", "仮想パスが不正です", MessageBoxButtons.OK, MessageBoxIcon.Information);
                input.Focus();
            }
        };
        dialog.Controls.AddRange([label, input, ok, cancel]);
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;
        dialog.Shown += (_, _) => input.Focus();
        return dialog.ShowDialog(this) == DialogResult.OK ? input.Text : null;
    }

    private FileAttributes? PromptForAttributes(VfsNode entry)
    {
        using var dialog = new Form
        {
            Text = $"属性を変更: {entry.Name}",
            Width = 430,
            Height = 235,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
        };
        var readOnly = new CheckBox { Left = 16, Top = 18, AutoSize = true, Text = "ReadOnly", Checked = entry.Attributes.HasFlag(FileAttributes.ReadOnly) };
        var hidden = new CheckBox { Left = 16, Top = 48, AutoSize = true, Text = "Hidden", Checked = entry.Attributes.HasFlag(FileAttributes.Hidden) };
        var system = new CheckBox { Left = 16, Top = 78, AutoSize = true, Text = "System", Checked = entry.Attributes.HasFlag(FileAttributes.System) };
        var archive = new CheckBox { Left = 16, Top = 108, AutoSize = true, Text = "Archive", Checked = entry.Attributes.HasFlag(FileAttributes.Archive) };
        var note = new Label { Left = 140, Top = 18, Width = 255, Height = 75, Text = "FAT/exFAT/NTFSは4属性、ext4/XFSはReadOnlyだけを利用できます。Directory属性は自動的に保持します。" };
        var ok = new Button { Text = "追加", Left = 214, Top = 145, Width = 88, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "キャンセル", Left = 312, Top = 145, Width = 88, DialogResult = DialogResult.Cancel };
        dialog.Controls.AddRange([readOnly, hidden, system, archive, note, ok, cancel]);
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return null;
        }

        var attributes = entry.IsDirectory ? FileAttributes.Directory : (FileAttributes)0;
        if (readOnly.Checked) attributes |= FileAttributes.ReadOnly;
        if (hidden.Checked) attributes |= FileAttributes.Hidden;
        if (system.Checked) attributes |= FileAttributes.System;
        if (archive.Checked) attributes |= FileAttributes.Archive;
        return attributes;
    }

    private DateTime? PromptForUtcTimestamp(DateTime initialUtc)
    {
        using var dialog = new Form
        {
            Text = "更新日時（UTC）",
            Width = 430,
            Height = 150,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
        };
        var picker = new DateTimePicker
        {
            Left = 16,
            Top = 20,
            Width = 384,
            Format = DateTimePickerFormat.Custom,
            CustomFormat = "yyyy-MM-dd HH:mm:ss 'UTC'",
            Value = initialUtc.ToUniversalTime(),
        };
        var ok = new Button { Text = "追加", Left = 214, Top = 58, Width = 88, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "キャンセル", Left = 312, Top = 58, Width = 88, DialogResult = DialogResult.Cancel };
        dialog.Controls.AddRange([picker, ok, cancel]);
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;
        return dialog.ShowDialog(this) == DialogResult.OK
            ? DateTime.SpecifyKind(picker.Value, DateTimeKind.Utc)
            : null;
    }

    private static string FormatEditOperation(FileEditOperationKind operation) => operation switch
    {
        FileEditOperationKind.WriteContent => "内容変更",
        FileEditOperationKind.CreateFile => "ファイル追加",
        FileEditOperationKind.DeleteFile => "ファイル削除",
        FileEditOperationKind.CreateDirectory => "ディレクトリ作成",
        FileEditOperationKind.DeleteDirectory => "ディレクトリ削除",
        FileEditOperationKind.MoveEntry => "移動・名前変更",
        FileEditOperationKind.SetAttributes => "属性変更",
        FileEditOperationKind.SetLastWriteTimeUtc => "更新日時変更",
        _ => operation.ToString(),
    };

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
        if (_isLoadingImage)
        {
            _closeAfterLoadCancellation = true;
            CancelImageLoad();
            e.Cancel = true;
            return;
        }

        if (_isWritingImage)
        {
            _closeAfterWriteCancellation = true;
            _writeCancellation?.Cancel();
            e.Cancel = true;
            return;
        }

        if (!ConfirmDiscardPendingEdits("アプリを終了すると変更予定を破棄します。続行しますか？"))
        {
            e.Cancel = true;
            return;
        }

        if (!ConfirmAndDisposeMounts("アプリ終了前にマウントを解除します。続行しますか？"))
        {
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
    private sealed record LzopOpenSelection(
        LzopOpenMode Mode,
        string? TemporaryDirectory,
        bool OverwriteSavedRaw);
    private sealed record ImageAnalysis(
        IReadOnlyList<PartitionInfo> Partitions,
        IReadOnlyList<LvmDiagnostic> Diagnostics,
        List<IDisposable> OwnedReaders,
        int LvmVolumeCount);

    private sealed record ImageLoadResult(
        IDiskImageReader Reader,
        IReadOnlyList<IDiskImageReader> CompanionReaders,
        IReadOnlyList<BtrfsDevicePartition> BtrfsDevices,
        ImageAnalysis Analysis,
        string RawHex) : IDisposable
    {
        public void Dispose()
        {
            foreach (var disposable in Analysis.OwnedReaders)
            {
                disposable.Dispose();
            }

            foreach (var companionReader in CompanionReaders)
            {
                companionReader.Dispose();
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
