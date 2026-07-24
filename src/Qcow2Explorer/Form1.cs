using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
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
    private readonly ToolStripTextBox _searchBox = new() { AutoSize = false, Width = 240, BorderStyle = BorderStyle.FixedSingle, ToolTipText = "現在のパーティションからファイル名を検索" };
    private readonly ToolStripButton _cancelOperationButton = new("キャンセル") { Enabled = false };
    private readonly ListView _headerList = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true };
    private readonly TextBox _warningText = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
    private readonly TextBox _offsetBox = new() { Text = "0x0", Width = 140 };
    private readonly NumericUpDown _lengthBox = new() { Minimum = 1, Maximum = 1024 * 1024, Value = 512, Increment = 512, Width = 110 };
    private readonly TextBox _hexText = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font("Consolas", 10) };
    private readonly DataGridView _partitionGrid = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill };
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
    private CancellationTokenSource? _operationCancellation;
    private bool _isLoadingImage;

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
            DisposeMounts();
            DisposeFileSystems();
            DisposePartitionReaders();
            _reader?.Dispose();
        };
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
        toolStrip.Items.Add(openButton);
        toolStrip.Items.Add(openFolderButton);
        toolStrip.Items.Add(openPhysicalDiskButton);
        toolStrip.Items.Add(new ToolStripSeparator());
        toolStrip.Items.Add(new ToolStripLabel("ファイル"));
        toolStrip.Items.Add(_pathBox);
        toolStrip.Items.Add(reportButton);
        toolStrip.Items.Add(snapshotButton);
        toolStrip.Items.Add(new ToolStripSeparator());
        toolStrip.Items.Add(_statusLabel);
        toolStrip.Items.Add(_loadProgressBar);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(CreateSummaryTab());
        tabs.TabPages.Add(CreateRawTab());
        tabs.TabPages.Add(CreatePartitionTab());
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

    private TabPage CreateExplorerTab()
    {
        _tree.BeforeExpand += TreeBeforeExpand;
        _tree.AfterSelect += TreeAfterSelect;

        _fileList.Columns.Add("名前", 360);
        _fileList.Columns.Add("サイズ", 120, HorizontalAlignment.Right);
        _fileList.Columns.Add("更新日時 UTC", 170);
        _fileList.Columns.Add("種別 / 属性", 220);
        _fileList.Columns.Add("場所", 420);
        _fileList.DoubleClick += async (_, _) => await OpenSelectedListItemAsync();
        _fileList.SelectedIndexChanged += (_, _) => ShowSelectedItemProperties();

        var previewButton = new ToolStripButton("プレビュー");
        previewButton.Click += (_, _) => PreviewSelectedFile();
        var windowPreviewButton = new ToolStripButton("別窓表示");
        windowPreviewButton.Click += async (_, _) => await OpenSelectedFilePreviewAsync();
        var extractButton = new ToolStripButton("抽出");
        extractButton.Click += (_, _) => ExtractSelectedFile();
        var copyButton = new ToolStripButton("選択コピー");
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
            _searchBox.Clear();
            if (_currentFileSystem is not null && _currentDirectory is not null)
            {
                PopulateFileList(_currentFileSystem, _currentDirectory);
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
        _cancelOperationButton.Click += (_, _) => _operationCancellation?.Cancel();
        var explorerStrip = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden };
        explorerStrip.Items.Add(new ToolStripLabel("検索"));
        explorerStrip.Items.Add(_searchBox);
        explorerStrip.Items.Add(searchButton);
        explorerStrip.Items.Add(clearSearchButton);
        explorerStrip.Items.Add(_cancelOperationButton);
        explorerStrip.Items.Add(new ToolStripSeparator());
        explorerStrip.Items.Add(windowPreviewButton);
        explorerStrip.Items.Add(previewButton);
        explorerStrip.Items.Add(extractButton);
        explorerStrip.Items.Add(copyButton);
        explorerStrip.Items.Add(copyFolderButton);
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

    private async Task LoadImageAsync(string path)
    {
        if (_isLoadingImage)
        {
            _statusLabel.Text = "別のディスクイメージを読み込み中です";
            return;
        }

        if (!ConfirmAndDisposeMounts("新しいディスクイメージを開く前に、現在のマウントを解除します。続行しますか？"))
        {
            return;
        }

        _isLoadingImage = true;
        UseWaitCursor = true;
        _loadProgressBar.Visible = true;
        _loadProgressBar.Style = ProgressBarStyle.Marquee;
        _statusLabel.Text = "ディスクイメージを開いています...";
        ImageLoadResult? loadResult = null;
        var adopted = false;

        try
        {
            var rawOffset = ParseOffset(_offsetBox.Text);
            var rawLength = (int)_lengthBox.Value;
            var progress = new Progress<DiskImageProgress>(UpdateLoadProgress);
            loadResult = await Task.Run(() => LoadAndAnalyzeImage(path, rawOffset, rawLength, progress));
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

            _isLoadingImage = false;
            UseWaitCursor = false;
            _loadProgressBar.Visible = false;
            _loadProgressBar.Style = ProgressBarStyle.Blocks;
        }
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
        IProgress<DiskImageProgress> progress)
    {
        IDiskImageReader? reader = null;
        var ownedReaders = new List<IDisposable>();
        try
        {
            reader = DiskImageReaderFactory.Open(path, progress);
            var analysis = AnalyzeImage(reader, ownedReaders, progress);
            progress.Report(new DiskImageProgress("先頭データを読み込み中..."));
            var rawData = new byte[rawLength];
            reader.ReadAt(rawOffset, rawData, 0, rawLength);
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
        IProgress<DiskImageProgress> progress)
    {
        progress.Report(new DiskImageProgress("パーティションテーブルを解析中..."));
        var discovered = PartitionTableReader.ReadPartitions(reader).ToList();
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
            progress.Report(new DiskImageProgress(
                $"ファイルシステムを検出中: {index + 1:N0} / {discovered.Count:N0}",
                index + 1,
                discovered.Count));
            discovered[index].FileSystem = FileSystemDetector.Detect(reader, discovered[index]);
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
                ownedReaders);
            foreach (var partition in lvmResult.Volumes)
            {
                partition.FileSystem = FileSystemDetector.Detect(reader, partition);
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

    private void AnalyzePartitions()
    {
        _partitionGrid.Rows.Clear();
        _tree.Nodes.Clear();
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
    }

    private void ApplyPartitionAnalysis(IReadOnlyList<PartitionInfo> partitions)
    {
        _partitionGrid.Rows.Clear();
        _tree.Nodes.Clear();
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
                return;
            }

            e.Node.Tag = new DirectoryNodeTag(fs, fs.Root);
            AddDirectoryChildren(e.Node, fs, fs.Root);
        }

        if (e.Node.Tag is DirectoryNodeTag directoryTag)
        {
            PopulateFileList(directoryTag.FileSystem, directoryTag.Node);
        }
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

    private void PopulateFileList(IReadOnlyFileSystem fileSystem, VfsNode directory)
    {
        _currentFileSystem = fileSystem;
        _currentDirectory = directory;
        _fileList.Items.Clear();
        _previewText.Clear();

        foreach (var node in fileSystem.ListDirectory(directory))
        {
            var item = new ListViewItem(node.DisplayName) { Tag = node };
            item.SubItems.Add(node.IsDirectory ? "" : FormatBytes(node.Size));
            item.SubItems.Add(node.ModifiedUtc?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "");
            item.SubItems.Add(FormatNodeType(node));
            item.SubItems.Add("");
            _fileList.Items.Add(item);
        }
    }

    private async Task SearchCurrentFileSystemAsync()
    {
        if (_currentFileSystem is null || string.IsNullOrWhiteSpace(_searchBox.Text))
        {
            return;
        }

        _operationCancellation?.Cancel();
        _operationCancellation?.Dispose();
        _operationCancellation = new CancellationTokenSource();
        var token = _operationCancellation.Token;
        _cancelOperationButton.Enabled = true;
        _statusLabel.Text = "検索中...";
        var progress = new Progress<int>(count => _statusLabel.Text = $"検索中: {count:N0} フォルダー");

        try
        {
            var fileSystem = _currentFileSystem;
            var query = _searchBox.Text.Trim();
            var matches = await Task.Run(() => FileSystemSearch.Search(fileSystem, query, progress, token), token);
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
            _statusLabel.Text = "検索をキャンセルしました";
        }
        finally
        {
            _cancelOperationButton.Enabled = false;
        }
    }

    private void ShowSelectedItemProperties()
    {
        if (_fileList.SelectedItems.Count != 1 || _fileList.SelectedItems[0].Tag is not VfsNode node)
        {
            return;
        }

        var location = _fileList.SelectedItems[0].SubItems.Count > 4 ? _fileList.SelectedItems[0].SubItems[4].Text : "";
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
            PopulateFileList(deleted, deleted.Root);
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
            if (FilePreviewReader.CanPreview(node.Name))
            {
                await OpenSelectedFilePreviewAsync();
            }
            else
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

    private void ExtractSelectedFile()
    {
        if (_currentFileSystem is null || _fileList.SelectedItems.Count == 0 || _fileList.SelectedItems[0].Tag is not VfsNode node || node.IsDirectory)
        {
            return;
        }

        using var dialog = new SaveFileDialog { FileName = node.Name, Title = "ファイルを抽出" };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            Cursor = Cursors.WaitCursor;
            using var output = new FileStream(dialog.FileName, FileMode.Create, FileAccess.Write, FileShare.None);
            long offset = 0;
            const int chunkSize = 1024 * 1024;
            while (offset < node.Size)
            {
                var chunk = _currentFileSystem.ReadFile(node, offset, (int)Math.Min(chunkSize, node.Size - offset));
                if (chunk.Length == 0)
                {
                    break;
                }

                output.Write(chunk, 0, chunk.Length);
                offset += chunk.Length;
            }

            _statusLabel.Text = $"抽出完了: {node.Name}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "抽出エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            Cursor = Cursors.Default;
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

        var progress = new Progress<CopyProgress>(p =>
        {
            var name = Path.GetFileName(p.CurrentPath);
            _statusLabel.Text = $"コピー中: {name} {FormatBytes(p.BytesCopied)}";
        });

        try
        {
            _operationCancellation?.Cancel();
            _operationCancellation?.Dispose();
            _operationCancellation = new CancellationTokenSource();
            _cancelOperationButton.Enabled = true;
            var result = await Task.Run(() => FileSystemExporter.CopyNodes(
                fileSystem,
                nodes,
                dialog.SelectedPath,
                progress,
                _operationCancellation.Token,
                new CopyOptions(ContinueOnError: true, CreateSha256Manifest: true)));
            _statusLabel.Text = $"コピー完了: {result.FilesCopied:N0} files, {FormatBytes(result.BytesCopied)}";
            MessageBox.Show(
                this,
                $"コピーが完了しました。{Environment.NewLine}ファイル: {result.FilesCopied:N0}{Environment.NewLine}フォルダ: {result.DirectoriesCreated:N0}{Environment.NewLine}サイズ: {FormatBytes(result.BytesCopied)}{Environment.NewLine}エラー: {result.Errors.Count:N0}{Environment.NewLine}{Environment.NewLine}SHA-256一覧もコピー先へ保存しました。",
                "コピー完了",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
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
            _cancelOperationButton.Enabled = false;
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
        }
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

    private async Task OpenSelectedFilePreviewAsync()
    {
        if (_currentFileSystem is null
            || _fileList.SelectedItems.Count == 0
            || _fileList.SelectedItems[0].Tag is not VfsNode node
            || node.IsDirectory)
        {
            return;
        }

        if (!FilePreviewReader.CanPreview(node.Name))
        {
            MessageBox.Show(
                this,
                "別窓表示はテキスト、.docx、.xlsx、.xlsmに対応しています。.docと.xlsは対象外です。",
                "別窓表示",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        if (node.Size < 0 || node.Size > FilePreviewReader.MaximumFileSize)
        {
            MessageBox.Show(
                this,
                $"別窓表示できるファイルサイズは{FilePreviewReader.MaximumFileSize / 1024 / 1024:N0} MBまでです。",
                "別窓表示",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        try
        {
            _statusLabel.Text = $"{node.Name} を読み込み中...";
            var fileSystem = _currentFileSystem;
            var preview = await Task.Run(() =>
            {
                var data = fileSystem.ReadFile(node, 0, checked((int)node.Size));
                return FilePreviewReader.Read(node.Name, data);
            });
            if (IsDisposed)
            {
                return;
            }

            var window = new FilePreviewForm(node.Name, preview);
            window.Show(this);
            _statusLabel.Text = $"{node.Name}: 別窓表示";
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "別窓表示に失敗しました";
            MessageBox.Show(this, ex.Message, "別窓表示エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
