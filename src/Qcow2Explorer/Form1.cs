using System.Globalization;
using System.Text;
using Qcow2Explorer.Core;
using Qcow2Explorer.FileSystems;
using Qcow2Explorer.Mounting;
using Qcow2Explorer.Partitions;

namespace Qcow2Explorer;

public partial class Form1 : Form
{
    private readonly ToolStripTextBox _pathBox = new() { AutoSize = false, Width = 480, ReadOnly = true };
    private readonly ToolStripLabel _statusLabel = new("qcow2 を開いてください");
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

    private Qcow2Reader? _reader;
    private readonly List<PartitionInfo> _partitions = new();
    private readonly Dictionary<int, IReadOnlyFileSystem> _fileSystems = new();
    private readonly List<ProjectedFileSystemMount> _mounts = new();
    private IReadOnlyFileSystem? _currentFileSystem;
    private VfsNode? _currentDirectory;

    public Form1()
    {
        InitializeComponent();
        BuildUi();
        FormClosing += Form1FormClosing;
        FormClosed += (_, _) =>
        {
            DisposeMounts();
            DisposeFileSystems();
            _reader?.Dispose();
        };
    }

    private void BuildUi()
    {
        Text = "Qcow2 Explorer";
        MinimumSize = new Size(980, 640);
        Width = 1180;
        Height = 760;

        var toolStrip = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden };
        var openButton = new ToolStripButton("開く");
        openButton.Click += (_, _) => OpenImageDialog();
        toolStrip.Items.Add(openButton);
        toolStrip.Items.Add(new ToolStripSeparator());
        toolStrip.Items.Add(new ToolStripLabel("ファイル"));
        toolStrip.Items.Add(_pathBox);
        toolStrip.Items.Add(new ToolStripSeparator());
        toolStrip.Items.Add(_statusLabel);

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
        _fileList.Columns.Add("種別", 120);
        _fileList.DoubleClick += (_, _) => OpenSelectedListItem();

        var previewButton = new ToolStripButton("プレビュー");
        previewButton.Click += (_, _) => PreviewSelectedFile();
        var extractButton = new ToolStripButton("抽出");
        extractButton.Click += (_, _) => ExtractSelectedFile();
        var copyButton = new ToolStripButton("選択コピー");
        copyButton.Click += async (_, _) => await CopySelectedItemsAsync();
        var copyFolderButton = new ToolStripButton("表示フォルダをコピー");
        copyFolderButton.Click += async (_, _) => await CopyCurrentDirectoryAsync();
        var mountButton = new ToolStripButton("マウント");
        mountButton.Click += (_, _) => MountSelectedPartition();
        var explorerStrip = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden };
        explorerStrip.Items.Add(previewButton);
        explorerStrip.Items.Add(extractButton);
        explorerStrip.Items.Add(copyButton);
        explorerStrip.Items.Add(copyFolderButton);
        explorerStrip.Items.Add(new ToolStripSeparator());
        explorerStrip.Items.Add(mountButton);

        var right = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 330 };
        right.Panel1.Controls.Add(_fileList);
        right.Panel2.Controls.Add(_previewText);

        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 320 };
        split.Panel1.Controls.Add(_tree);
        split.Panel2.Controls.Add(right);

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

    private void OpenImageDialog()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "qcow2 files (*.qcow2;*.qcow)|*.qcow2;*.qcow|All files (*.*)|*.*",
            Title = "qcow2 ファイルを開く"
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            LoadImage(dialog.FileName);
        }
    }

    private void LoadImage(string path)
    {
        if (!ConfirmAndDisposeMounts("新しい qcow2 を開く前に、現在のマウントを解除します。続行しますか？"))
        {
            return;
        }

        try
        {
            Cursor = Cursors.WaitCursor;
            DisposeFileSystems();
            _reader?.Dispose();
            _reader = new Qcow2Reader(path);
            _partitions.Clear();
            _pathBox.Text = path;

            FillHeader();
            AnalyzePartitions();
            ReadRawData();
            _statusLabel.Text = "読込完了";
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "読込失敗";
            MessageBox.Show(this, ex.Message, "qcow2 読込エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            Cursor = Cursors.Default;
        }
    }

    private void FillHeader()
    {
        _headerList.Items.Clear();
        _warningText.Clear();
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

        var warnings = _reader.GetWarnings();
        _warningText.Text = warnings.Count == 0
            ? "警告なし"
            : string.Join(Environment.NewLine, warnings);
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

        foreach (var partition in PartitionTableReader.ReadPartitions(_reader))
        {
            partition.FileSystem = FileSystemDetector.Detect(_reader, partition);
            _partitions.Add(partition);
            _partitionGrid.Rows.Add(
                partition.Number,
                partition.Scheme,
                partition.FileSystem,
                partition.Name,
                $"{partition.Type} ({partition.TypeId})",
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

        if (_partitions.Count == 0)
        {
            _statusLabel.Text = "パーティションなし";
        }
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
            var result = _reader.LookupCluster(offset);
            var host = result.HostClusterOffset.HasValue ? $"0x{result.HostClusterOffset.Value:X}" : "(未割当)";
            var compression = result.IsCompressed ? $", compressed={FormatBytes(result.CompressedLength)}" : "";
            _statusLabel.Text = $"virtual cluster {result.VirtualClusterIndex:N0} -> {host}";
            _hexText.Text = $"{_statusLabel.Text}{Environment.NewLine}zero={result.ReadsAsZero}{compression}{Environment.NewLine}{Environment.NewLine}{_hexText.Text}";
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

        if (e.Node.Nodes.Count != 1 || e.Node.Nodes[0].Tag is not DummyNodeTag)
        {
            return;
        }

        e.Node.Nodes.Clear();
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
            if (e.Node.Nodes.Count == 1 && e.Node.Nodes[0].Tag is DummyNodeTag)
            {
                e.Node.Nodes.Clear();
                AddDirectoryChildren(e.Node, fs, fs.Root);
            }
        }

        if (e.Node.Tag is DirectoryNodeTag directoryTag)
        {
            PopulateFileList(directoryTag.FileSystem, directoryTag.Node);
        }
    }

    private void AddDirectoryChildren(TreeNode treeNode, IReadOnlyFileSystem fileSystem, VfsNode directory)
    {
        foreach (var child in fileSystem.ListDirectory(directory).Where(n => n.IsDirectory))
        {
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
            item.SubItems.Add(node.IsDirectory ? "Folder" : "File");
            _fileList.Items.Add(item);
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

    private void OpenSelectedListItem()
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
            PreviewSelectedFile();
        }
    }

    private void OpenDirectoryFromList(VfsNode node)
    {
        if (_tree.SelectedNode is null || _currentFileSystem is null)
        {
            return;
        }

        foreach (TreeNode child in _tree.SelectedNode.Nodes)
        {
            if (child.Tag is DirectoryNodeTag tag && ReferenceEquals(tag.Node, node))
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
            Cursor = Cursors.WaitCursor;
            var result = await Task.Run(() => FileSystemExporter.CopyNodes(fileSystem, nodes, dialog.SelectedPath, progress));
            _statusLabel.Text = $"コピー完了: {result.FilesCopied:N0} files, {FormatBytes(result.BytesCopied)}";
            MessageBox.Show(
                this,
                $"コピーが完了しました。{Environment.NewLine}ファイル: {result.FilesCopied:N0}{Environment.NewLine}フォルダ: {result.DirectoriesCreated:N0}{Environment.NewLine}サイズ: {FormatBytes(result.BytesCopied)}",
                "コピー完了",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "コピーエラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _statusLabel.Text = "コピー失敗";
        }
        finally
        {
            Cursor = Cursors.Default;
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

    private sealed record PartitionNodeTag(PartitionInfo Partition);
    private sealed record DirectoryNodeTag(IReadOnlyFileSystem FileSystem, VfsNode Node);
    private sealed record DummyNodeTag;
}
