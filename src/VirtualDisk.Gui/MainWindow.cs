using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Qcow2Explorer.Core;
using Qcow2Explorer.Creation;
using Qcow2Explorer.FileSystems;
using Qcow2Explorer.Partitions;
using Qcow2Explorer.Previewing;

namespace VirtualDisk.Gui;

public sealed class MainWindow : Window
{
    private const int MaximumDisplayedDirectoryEntries = 250_000;
    private const int MaximumSearchResults = 5_000;
    private readonly TextBlock _imageSummary = new() { Text = "ディスクイメージを開いてください。" };
    private readonly TextBlock _pathLabel = new() { Text = "/" };
    private readonly TextBlock _status = new() { Text = "準備完了" };
    private readonly ListBox _partitions = new();
    private readonly ListBox _entries = new();
    private readonly Button _backButton = new() { Content = "上へ", IsEnabled = false };
    private readonly Button _previewButton = new() { Content = "プレビュー", IsEnabled = false };
    private readonly Button _extractButton = new() { Content = "抽出...", IsEnabled = false };
    private readonly Button _cancelButton = new() { Content = "キャンセル", IsEnabled = false };
    private readonly MenuItem _createMenuItem = new() { Header = "新規作成..." };
    private readonly MenuItem _openMenuItem = new() { Header = "開く..." };
    private readonly MenuItem _exitMenuItem = new() { Header = "終了" };
    private readonly MenuItem _previewMenuItem = new() { Header = "プレビュー", IsEnabled = false };
    private readonly MenuItem _extractMenuItem = new() { Header = "抽出...", IsEnabled = false };
    private readonly MenuItem _searchMenuItem = new() { Header = "検索...", IsEnabled = false };
    private readonly MenuItem _verifyMenuItem = new() { Header = "全読込検証", IsEnabled = false };
    private readonly MenuItem _cancelMenuItem = new() { Header = "キャンセル", IsEnabled = false };
    private readonly Stack<VfsNode> _directoryHistory = new();
    private IDiskImageReader? _reader;
    private IReadOnlyFileSystem? _fileSystem;
    private VfsNode? _currentDirectory;
    private CancellationTokenSource? _activeOperation;
    private bool _busy;

    public MainWindow(string? initialPath = null)
    {
        Title = "VirtualDisk Tools";
        Width = 1100;
        Height = 720;
        MinWidth = 760;
        MinHeight = 480;
        Content = BuildContent();
        Closed += (_, _) =>
        {
            _activeOperation?.Cancel();
            DisposeImage();
        };
        _partitions.SelectionChanged += async (_, _) => await OpenSelectedPartitionAsync();
        _entries.SelectionChanged += (_, _) => RefreshCommandState();
        _entries.DoubleTapped += async (_, _) => await ActivateSelectedEntryAsync();
        _backButton.Click += async (_, _) => await NavigateUpAsync();
        _previewButton.Click += async (_, _) => await PreviewSelectedAsync();
        _extractButton.Click += async (_, _) => await ExtractSelectedAsync();
        _cancelButton.Click += (_, _) => CancelActiveOperation();
        _createMenuItem.Click += async (_, _) => await CreateDiskAsync();
        _openMenuItem.Click += async (_, _) => await ChooseAndOpenImageAsync();
        _exitMenuItem.Click += (_, _) => Close();
        _previewMenuItem.Click += async (_, _) => await PreviewSelectedAsync();
        _extractMenuItem.Click += async (_, _) => await ExtractSelectedAsync();
        _searchMenuItem.Click += async (_, _) => await SearchFileSystemAsync();
        _verifyMenuItem.Click += async (_, _) => await VerifyFileSystemAsync();
        _cancelMenuItem.Click += (_, _) => CancelActiveOperation();

        if (!string.IsNullOrWhiteSpace(initialPath))
        {
            Opened += async (_, _) => await OpenImageAsync(initialPath);
        }
    }

    private Control BuildContent()
    {
        var menu = new Menu
        {
            ItemsSource = new MenuItem[]
            {
                new MenuItem
                {
                    Header = "ファイル",
                    ItemsSource = new Control[]
                    {
                        _createMenuItem,
                        _openMenuItem,
                        new Separator(),
                        _exitMenuItem,
                    },
                },
                new MenuItem
                {
                    Header = "操作",
                    ItemsSource = new Control[]
                    {
                        _searchMenuItem,
                        _previewMenuItem,
                        _extractMenuItem,
                        _verifyMenuItem,
                        new Separator(),
                        _cancelMenuItem,
                    },
                },
            },
        };
        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(12),
            Children =
            {
                _backButton,
                _previewButton,
                _extractButton,
                _cancelButton,
            },
        };
        var header = new StackPanel
        {
            Spacing = 4,
            Margin = new Thickness(12, 0, 12, 8),
            Children = { _imageSummary, _pathLabel },
        };
        var contentGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("300,*"),
            RowDefinitions = new RowDefinitions("*"),
            Margin = new Thickness(12, 0, 12, 8),
        };
        var partitionPanel = new DockPanel { Margin = new Thickness(0, 0, 8, 0) };
        var partitionTitle = new TextBlock { Text = "パーティション", FontWeight = Avalonia.Media.FontWeight.SemiBold };
        DockPanel.SetDock(partitionTitle, Dock.Top);
        partitionPanel.Children.Add(partitionTitle);
        partitionPanel.Children.Add(_partitions);
        var entryPanel = new DockPanel { Margin = new Thickness(8, 0, 0, 0) };
        var entryTitle = new TextBlock { Text = "ファイル", FontWeight = Avalonia.Media.FontWeight.SemiBold };
        DockPanel.SetDock(entryTitle, Dock.Top);
        entryPanel.Children.Add(entryTitle);
        entryPanel.Children.Add(_entries);
        Grid.SetColumn(partitionPanel, 0);
        Grid.SetColumn(entryPanel, 1);
        contentGrid.Children.Add(partitionPanel);
        contentGrid.Children.Add(entryPanel);

        var statusBorder = new Border
        {
            Padding = new Thickness(12, 8),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = _status,
        };
        var root = new DockPanel();
        DockPanel.SetDock(menu, Dock.Top);
        DockPanel.SetDock(toolbar, Dock.Top);
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(statusBorder, Dock.Bottom);
        root.Children.Add(menu);
        root.Children.Add(toolbar);
        root.Children.Add(header);
        root.Children.Add(statusBorder);
        root.Children.Add(contentGrid);
        return root;
    }

    private async Task ChooseAndOpenImageAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "ディスクイメージを開く",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("対応ディスクイメージ")
                {
                    Patterns =
                    [
                        "*.qcow2", "*.qcow", "*.vhd", "*.vhdx", "*.avhdx", "*.vmdk", "*.vdi",
                        "*.ova", "*.hdd", "*.hds", "*.vma", "*.dd", "*.img", "*.raw", "*.lzo", "*.E01",
                    ],
                },
                FilePickerFileTypes.All,
            ],
        });
        var path = files.SingleOrDefault()?.TryGetLocalPath();
        if (path is not null)
        {
            await OpenImageAsync(path);
        }
    }

    private async Task CreateDiskAsync()
    {
        var dialog = new VirtualDiskCreationDialog();
        var accepted = await dialog.ShowDialog<bool>(this);
        if (!accepted || dialog.Options is null)
        {
            return;
        }

        var options = dialog.Options;
        var destination = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "新規仮想ディスクの保存先",
            SuggestedFileName = options.ContainerFormat == VirtualDiskContainerFormat.Qcow2
                ? "disk.qcow2"
                : "disk.raw",
        });
        var path = destination?.TryGetLocalPath();
        if (path is null)
        {
            return;
        }

        path = EnsureContainerExtension(path, options.ContainerFormat);
        if (File.Exists(path) || Directory.Exists(path))
        {
            await ShowErrorAsync("仮想ディスクを作成できません", $"出力先は既に存在します: {path}");
            return;
        }

        var request = new VirtualDiskCreationRequest(
            path,
            options.CapacityBytes,
            options.ContainerFormat,
            options.PartitionTable,
            options.Partitions,
            options.InitialFiles);
        var operation = BeginOperation("仮想ディスクを作成しています...");
        string? createdPath = null;
        try
        {
            string? lastMessage = null;
            int? lastPercentage = null;
            var progress = new CallbackProgress<DiskImageProgress>(update =>
            {
                if (string.Equals(lastMessage, update.Message, StringComparison.Ordinal)
                    && lastPercentage == update.Percentage)
                {
                    return;
                }

                lastMessage = update.Message;
                lastPercentage = update.Percentage;
                Dispatcher.UIThread.Post(() =>
                {
                    if (ReferenceEquals(_activeOperation, operation))
                    {
                        var suffix = update.Percentage is int percentage ? $" ({percentage}%)" : string.Empty;
                        _status.Text = update.Message + suffix;
                    }
                });
            });
            var result = await Task.Run(
                () => VirtualDiskCreationService.CreateAsync(request, progress, operation.Token),
                operation.Token);
            createdPath = result.DestinationPath;
            _status.Text = $"仮想ディスクを作成しました: {createdPath}";
        }
        catch (OperationCanceledException)
        {
            _status.Text = "作成をキャンセルしました。途中ファイルは削除されています。";
        }
        catch (Exception exception)
        {
            _status.Text = $"作成に失敗しました: {exception.Message}";
            await ShowErrorAsync("仮想ディスクを作成できません", exception.Message);
        }
        finally
        {
            EndOperation(operation);
        }

        if (createdPath is not null)
        {
            await OpenImageAsync(createdPath);
        }
    }

    private static string EnsureContainerExtension(string path, VirtualDiskContainerFormat format)
    {
        if (!string.IsNullOrEmpty(Path.GetExtension(path)))
        {
            return path;
        }

        return path + (format == VirtualDiskContainerFormat.Qcow2 ? ".qcow2" : ".raw");
    }

    private async Task OpenImageAsync(string path)
    {
        var operation = BeginOperation("ディスクイメージを解析しています...");
        var selectFirstPartition = false;
        try
        {
            var opened = await Task.Run(() => OpenImage(path, operation.Token), operation.Token);
            DisposeImage();
            _reader = opened.Reader;
            _partitions.ItemsSource = opened.Partitions;
            _imageSummary.Text = $"{opened.Reader.FormatName} — {opened.Reader.Length:N0} bytes — {opened.Partitions.Count:N0} partition(s)";
            _status.Text = $"開きました: {opened.Reader.Path}";
            if (opened.Partitions.Count > 0)
            {
                selectFirstPartition = true;
            }
        }
        catch (OperationCanceledException)
        {
            _status.Text = "ディスクイメージの解析をキャンセルしました。";
        }
        catch (Exception ex)
        {
            _status.Text = $"開けませんでした: {ex.Message}";
            await ShowErrorAsync("ディスクイメージを開けません", ex.Message);
        }
        finally
        {
            EndOperation(operation);
        }

        if (selectFirstPartition)
        {
            _partitions.SelectedIndex = 0;
        }
    }

    private static OpenedImage OpenImage(string path, CancellationToken cancellationToken)
    {
        var reader = DiskImageReaderFactory.Open(path, cancellationToken: cancellationToken);
        try
        {
            var partitions = PartitionTableReader.ReadPartitionsWithWholeDiskFallback(reader, cancellationToken);
            foreach (var partition in partitions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                partition.FileSystem = FileSystemDetector.Detect(reader, partition, cancellationToken);
            }

            return new OpenedImage(reader, partitions.Select(partition => new PartitionItem(partition)).ToArray());
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    private async Task OpenSelectedPartitionAsync()
    {
        if (_busy)
        {
            return;
        }

        DisposeFileSystem();
        if (_reader is null || _partitions.SelectedItem is not PartitionItem selected)
        {
            RefreshCommandState();
            return;
        }

        var reader = _reader;
        var operation = BeginOperation($"パーティション#{selected.Partition.Number}を開いています...");
        try
        {
            var opened = await Task.Run(() =>
            {
                operation.Token.ThrowIfCancellationRequested();
                var fileSystem = FileSystemDetector.TryOpen(
                    reader,
                    selected.Partition,
                    out var error,
                    operation.Token);
                if (fileSystem is null)
                {
                    return new OpenedFileSystem(null, error, []);
                }

                try
                {
                    var entries = ReadDirectoryEntries(fileSystem, fileSystem.Root, operation.Token);
                    return new OpenedFileSystem(fileSystem, error, entries);
                }
                catch
                {
                    (fileSystem as IDisposable)?.Dispose();
                    throw;
                }
            }, operation.Token);
            if (!ReferenceEquals(_reader, reader)
                || !ReferenceEquals(_partitions.SelectedItem, selected))
            {
                (opened.FileSystem as IDisposable)?.Dispose();
                return;
            }

            if (opened.FileSystem is null)
            {
                _status.Text = opened.Error;
                return;
            }

            _fileSystem = opened.FileSystem;
            _directoryHistory.Clear();
            ApplyDirectory(opened.FileSystem.Root, opened.Entries);
            _status.Text = $"{opened.FileSystem.Name}を開きました。";
        }
        catch (OperationCanceledException)
        {
            _status.Text = "パーティションの読込をキャンセルしました。";
        }
        catch (Exception exception)
        {
            _status.Text = $"パーティションを開けませんでした: {exception.Message}";
            await ShowErrorAsync("パーティションを開けません", exception.Message);
        }
        finally
        {
            EndOperation(operation);
        }
    }

    private async Task ActivateSelectedEntryAsync()
    {
        if (_busy || _entries.SelectedItem is not EntryItem selected)
        {
            return;
        }

        if (!selected.Node.IsDirectory)
        {
            await PreviewSelectedAsync();
            return;
        }

        if (_currentDirectory is not null)
        {
            var previous = _currentDirectory;
            if (await LoadDirectoryAsync(selected.Node))
            {
                _directoryHistory.Push(previous);
                RefreshCommandState();
            }
        }
    }

    private async Task NavigateUpAsync()
    {
        if (_busy || !_directoryHistory.TryPeek(out var directory))
        {
            return;
        }

        if (await LoadDirectoryAsync(directory))
        {
            _directoryHistory.Pop();
            RefreshCommandState();
        }
    }

    private async Task<bool> LoadDirectoryAsync(VfsNode directory)
    {
        if (_fileSystem is null)
        {
            return false;
        }

        var fileSystem = _fileSystem;
        var operation = BeginOperation($"{directory.DisplayName}を読み込んでいます...");
        try
        {
            var entries = await Task.Run(
                () => ReadDirectoryEntries(fileSystem, directory, operation.Token),
                operation.Token);
            if (!ReferenceEquals(_fileSystem, fileSystem))
            {
                return false;
            }

            ApplyDirectory(directory, entries);
            _status.Text = $"{entries.Count:N0}項目を表示しています。";
            return true;
        }
        catch (OperationCanceledException)
        {
            _status.Text = "ディレクトリの読込をキャンセルしました。";
            return false;
        }
        catch (Exception exception)
        {
            _status.Text = $"ディレクトリを開けませんでした: {exception.Message}";
            await ShowErrorAsync("ディレクトリを開けません", exception.Message);
            return false;
        }
        finally
        {
            EndOperation(operation);
        }
    }

    private static IReadOnlyList<EntryItem> ReadDirectoryEntries(
        IReadOnlyFileSystem fileSystem,
        VfsNode directory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var nodes = fileSystem.ListDirectory(directory)
            ?? throw new InvalidDataException("ファイルシステムがnullの一覧を返しました。");
        if (nodes.Count > MaximumDisplayedDirectoryEntries)
        {
            throw new NotSupportedException(
                $"ディレクトリ項目数が画面表示上限 ({MaximumDisplayedDirectoryEntries:N0}) を超えています。"
                + " CLIのextractまたはverifyを使用してください。");
        }

        var entries = new EntryItem[nodes.Count];
        for (var index = 0; index < nodes.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            entries[index] = new EntryItem(
                nodes[index] ?? throw new InvalidDataException("ファイルシステムがnullノードを返しました。"));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return entries;
    }

    private void ApplyDirectory(VfsNode directory, IReadOnlyList<EntryItem> entries)
    {
        _currentDirectory = directory;
        _pathLabel.Text = string.IsNullOrWhiteSpace(directory.VirtualPath) ? "/" : directory.VirtualPath;
        _entries.ItemsSource = entries;
        RefreshCommandState();
    }

    private async Task SearchFileSystemAsync()
    {
        if (_busy || _fileSystem is null)
        {
            return;
        }

        var query = await PromptSearchQueryAsync();
        if (query is null || _fileSystem is null)
        {
            return;
        }

        var fileSystem = _fileSystem;
        var operation = BeginOperation($"「{query}」を検索しています...");
        IReadOnlyList<SearchMatch>? matches = null;
        try
        {
            var lastReportedDirectories = 0;
            var progress = new CallbackProgress<int>(directories =>
            {
                if (directories - lastReportedDirectories < 500)
                {
                    return;
                }

                lastReportedDirectories = directories;
                Dispatcher.UIThread.Post(() =>
                {
                    if (ReferenceEquals(_activeOperation, operation))
                    {
                        _status.Text = $"「{query}」を検索しています... {directories:N0}ディレクトリ";
                    }
                });
            });
            matches = await Task.Run(
                () => FileSystemSearch.Search(
                    fileSystem,
                    query,
                    progress,
                    operation.Token,
                    MaximumSearchResults),
                operation.Token);
            _status.Text = $"「{query}」: {matches.Count:N0}件見つかりました。";
        }
        catch (OperationCanceledException)
        {
            _status.Text = "検索をキャンセルしました。";
        }
        catch (Exception exception)
        {
            _status.Text = $"検索に失敗しました: {exception.Message}";
            await ShowErrorAsync("検索できません", exception.Message);
        }
        finally
        {
            EndOperation(operation);
        }

        if (matches is null)
        {
            return;
        }

        if (matches.Count == 0)
        {
            await ShowErrorAsync("検索結果", $"「{query}」に一致する項目はありません。");
            return;
        }

        var selected = await ShowSearchResultsAsync(query, matches);
        if (selected is null || !ReferenceEquals(_fileSystem, fileSystem))
        {
            return;
        }

        if (!selected.Node.IsDirectory)
        {
            await PreviewFileAsync(selected.Node);
            return;
        }

        if (await LoadDirectoryAsync(selected.Node))
        {
            _directoryHistory.Clear();
            if (!ReferenceEquals(selected.Node, fileSystem.Root))
            {
                _directoryHistory.Push(fileSystem.Root);
            }

            RefreshCommandState();
        }
    }

    private async Task<string?> PromptSearchQueryAsync()
    {
        var query = new TextBox { PlaceholderText = "ファイル名またはディレクトリ名" };
        var search = new Button
        {
            Content = "検索",
            IsEnabled = false,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var cancel = new Button { Content = "キャンセル" };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { cancel, search },
        };
        var dialog = new Window
        {
            Title = "ファイルシステムを検索",
            Width = 520,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = "名前に含まれる文字列を入力してください。" },
                    query,
                    buttons,
                },
            },
        };
        query.TextChanged += (_, _) => search.IsEnabled = !string.IsNullOrWhiteSpace(query.Text);
        search.Click += (_, _) => dialog.Close(query.Text?.Trim());
        cancel.Click += (_, _) => dialog.Close();
        dialog.Opened += (_, _) => query.Focus();
        return await dialog.ShowDialog<string?>(this);
    }

    private async Task<SearchMatch?> ShowSearchResultsAsync(
        string query,
        IReadOnlyList<SearchMatch> matches)
    {
        var items = matches.Select(match => new SearchResultItem(match)).ToArray();
        var results = new ListBox { ItemsSource = items };
        var open = new Button
        {
            Content = "開く",
            IsEnabled = false,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var close = new Button { Content = "閉じる" };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { close, open },
        };
        var message = matches.Count == MaximumSearchResults
            ? $"先頭{MaximumSearchResults:N0}件を表示しています。条件を絞ると残りも検索できます。"
            : $"{matches.Count:N0}件見つかりました。";
        var content = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            RowSpacing = 8,
            Margin = new Thickness(12),
        };
        AddPreviewControl(content, new TextBlock { Text = message }, 0);
        AddPreviewControl(content, results, 1);
        AddPreviewControl(content, buttons, 2);
        var dialog = new Window
        {
            Title = $"検索結果 — {query}",
            Width = 760,
            Height = 520,
            MinWidth = 520,
            MinHeight = 320,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = content,
        };

        void OpenSelected()
        {
            if (results.SelectedItem is SearchResultItem selected)
            {
                dialog.Close(selected.Match);
            }
        }

        results.SelectionChanged += (_, _) =>
        {
            open.IsEnabled = results.SelectedItem is SearchResultItem;
            open.Content = results.SelectedItem is SearchResultItem { Match.Node.IsDirectory: true }
                ? "ディレクトリを開く"
                : "プレビュー";
        };
        results.DoubleTapped += (_, _) => OpenSelected();
        open.Click += (_, _) => OpenSelected();
        close.Click += (_, _) => dialog.Close();
        return await dialog.ShowDialog<SearchMatch?>(this);
    }

    private async Task PreviewSelectedAsync()
    {
        if (_fileSystem is null
            || _entries.SelectedItem is not EntryItem { Node.IsDirectory: false } selected)
        {
            return;
        }

        await PreviewFileAsync(selected.Node);
    }

    private async Task PreviewFileAsync(VfsNode file)
    {
        if (_fileSystem is null || file.IsDirectory)
        {
            return;
        }

        var fileSystem = _fileSystem;
        var operation = BeginOperation($"{file.Name}をプレビュー用に読み込んでいます...");
        FilePreviewContent? preview = null;
        try
        {
            var progress = new CallbackProgress<DiskImageProgress>(update =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (ReferenceEquals(_activeOperation, operation))
                    {
                        var suffix = update.Percentage is int percentage ? $" {percentage}%" : string.Empty;
                        _status.Text = update.Message + suffix;
                    }
                });
            });
            preview = await Task.Run(
                () => ReadPreview(fileSystem, file, progress, operation.Token),
                operation.Token);
            _status.Text = $"プレビューを読み込みました: {file.Name}";
        }
        catch (OperationCanceledException)
        {
            _status.Text = "プレビューの読込をキャンセルしました。";
        }
        catch (Exception exception)
        {
            _status.Text = $"プレビューできませんでした: {exception.Message}";
            await ShowErrorAsync("ファイルをプレビューできません", exception.Message);
        }
        finally
        {
            EndOperation(operation);
        }

        if (preview is not null)
        {
            await ShowPreviewAsync(file.Name, preview);
        }
    }

    private static FilePreviewContent ReadPreview(
        IReadOnlyFileSystem fileSystem,
        VfsNode file,
        IProgress<DiskImageProgress> progress,
        CancellationToken cancellationToken)
    {
        if (file.Size < 0 || file.Size > FilePreviewReader.MaximumFileSize)
        {
            throw new NotSupportedException(
                $"プレビューできるファイルは{FilePreviewReader.MaximumFileSize / 1024 / 1024:N0} MiB以下です。");
        }

        var data = new byte[checked((int)file.Size)];
        const int chunkSize = 1024 * 1024;
        var offset = 0;
        while (offset < data.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(chunkSize, data.Length - offset);
            var chunk = fileSystem.ReadFile(file, offset, count)
                ?? throw new InvalidDataException("ファイルシステムがnullデータを返しました。");
            if (chunk.Length != count)
            {
                throw new EndOfStreamException(
                    $"ファイルが途中で終了しました: offset={offset:N0}, requested={count:N0}, actual={chunk.Length:N0}");
            }

            Buffer.BlockCopy(chunk, 0, data, offset, count);
            offset += count;
            progress.Report(new DiskImageProgress("プレビュー用データを読み込んでいます...", offset, data.Length));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return FilePreviewReader.Read(file.Name, data, cancellationToken);
    }

    private async Task ShowPreviewAsync(string fileName, FilePreviewContent preview)
    {
        var close = new Button { Content = "閉じる", HorizontalAlignment = HorizontalAlignment.Right };
        var text = new TextBox
        {
            Text = FormatPreview(preview),
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = Avalonia.Media.TextWrapping.NoWrap,
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(
            text,
            Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(
            text,
            Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        var content = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            RowSpacing = 8,
            Margin = new Thickness(12),
        };
        AddPreviewControl(content, new TextBlock
        {
            Text = preview.Description,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        }, 0);
        AddPreviewControl(content, text, 1);
        AddPreviewControl(content, close, 2);
        var dialog = new Window
        {
            Title = $"プレビュー — {fileName}",
            Width = 900,
            Height = 700,
            MinWidth = 600,
            MinHeight = 400,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = content,
        };
        close.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(this);
    }

    private static void AddPreviewControl(Grid grid, Control control, int row)
    {
        Grid.SetRow(control, row);
        grid.Children.Add(control);
    }

    private static string FormatPreview(FilePreviewContent preview)
    {
        if (preview.Text is not null)
        {
            return preview.Text;
        }

        var output = new StringBuilder();
        foreach (var sheet in preview.Sheets)
        {
            output.AppendLine($"===== {sheet.Name} =====");
            foreach (var row in sheet.Rows)
            {
                output.AppendLine(string.Join('\t', row));
            }

            if (sheet.IsTruncated)
            {
                output.AppendLine("... 表示上限により以降の行を省略しました ...");
            }

            output.AppendLine();
        }

        return output.ToString();
    }

    private async Task ExtractSelectedAsync()
    {
        if (_fileSystem is null || _entries.SelectedItem is not EntryItem selected)
        {
            return;
        }

        string? path;
        if (selected.Node.IsDirectory)
        {
            var destinations = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "抽出先フォルダーを選択",
                AllowMultiple = false,
            });
            path = destinations.SingleOrDefault()?.TryGetLocalPath();
        }
        else
        {
            var destination = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "ファイルを抽出",
                SuggestedFileName = selected.Node.Name,
            });
            path = destination?.TryGetLocalPath();
        }

        if (path is null)
        {
            return;
        }

        var fileSystem = _fileSystem;
        var file = selected.Node;
        var operation = BeginOperation($"{file.Name}を抽出しています...");
        try
        {
            if (!file.IsDirectory && _reader is not null && PathsEqual(path, _reader.Path))
            {
                throw new IOException("開いているディスクイメージ自身には抽出できません。");
            }

            var lastPercentage = -1;
            var progress = new CallbackProgress<CopyProgress>(update =>
            {
                var percentage = update.TotalBytes == 0
                    ? 100
                    : (int)Math.Min(100, update.BytesCopied * 100d / update.TotalBytes);
                if (percentage == lastPercentage)
                {
                    return;
                }

                lastPercentage = percentage;
                Dispatcher.UIThread.Post(() =>
                {
                    if (ReferenceEquals(_activeOperation, operation))
                    {
                        _status.Text = $"{file.Name}を抽出しています... {percentage}%";
                    }
                });
            });
            if (file.IsDirectory)
            {
                var result = await Task.Run(
                    () => FileSystemExporter.CopyNode(
                        fileSystem,
                        file,
                        path,
                        progress,
                        operation.Token),
                    operation.Token);
                _status.Text = $"抽出しました: {result.FilesCopied:N0}ファイル / {result.BytesCopied:N0} bytes";
                if (result.Errors.Count > 0)
                {
                    var details = string.Join(
                        Environment.NewLine,
                        result.Errors.Take(50).Select(error =>
                            $"{error.SourceName}: {error.Message}"));
                    if (result.Errors.Count > 50)
                    {
                        details += $"{Environment.NewLine}... 他{result.Errors.Count - 50:N0}件";
                    }

                    await ShowErrorAsync(
                        $"{result.Errors.Count:N0}件を抽出できませんでした",
                        details);
                }
            }
            else
            {
                await Task.Run(
                    () => FileSystemExporter.ExtractFileAsync(
                        fileSystem,
                        file,
                        path,
                        overwrite: true,
                        progress: progress,
                        cancellationToken: operation.Token),
                    operation.Token);
                _status.Text = $"抽出しました: {path}";
            }
        }
        catch (OperationCanceledException)
        {
            _status.Text = file.IsDirectory
                ? "抽出をキャンセルしました。抽出済みの項目は抽出先に残っています。"
                : "抽出をキャンセルしました。抽出先は変更されていません。";
        }
        catch (Exception ex)
        {
            _status.Text = $"抽出に失敗しました: {ex.Message}";
            await ShowErrorAsync("ファイルを抽出できません", ex.Message);
        }
        finally
        {
            EndOperation(operation);
        }
    }

    private async Task VerifyFileSystemAsync()
    {
        if (_fileSystem is null)
        {
            return;
        }

        var fileSystem = _fileSystem;
        var operation = BeginOperation($"{fileSystem.Name}を全読込検証しています...");
        try
        {
            long lastReportedBytes = 0;
            var lastReportedEntries = 0;
            var progress = new CallbackProgress<FileSystemVerificationProgress>(update =>
            {
                if (update.BytesRead - lastReportedBytes < 256L * 1024 * 1024
                    && update.EntriesChecked - lastReportedEntries < 1_000)
                {
                    return;
                }

                lastReportedBytes = update.BytesRead;
                lastReportedEntries = update.EntriesChecked;
                Dispatcher.UIThread.Post(() =>
                {
                    if (ReferenceEquals(_activeOperation, operation))
                    {
                        _status.Text =
                            $"全読込検証: {update.EntriesChecked:N0}項目 / "
                            + $"{update.BytesRead:N0} bytes — {update.CurrentPath}";
                    }
                });
            });
            var result = await Task.Run(
                () => FileSystemVerifier.Verify(
                    fileSystem,
                    fileSystem.Root,
                    progress,
                    operation.Token),
                operation.Token);
            _status.Text = result.IsValid
                ? $"検証成功: {result.FilesChecked:N0}ファイル / {result.BytesRead:N0} bytes"
                : $"検証で{result.Issues.Count:N0}件の問題を検出しました。";
            if (!result.IsValid)
            {
                var details = string.Join(
                    Environment.NewLine,
                    result.Issues.Take(50).Select(issue =>
                        $"{issue.Path}: {issue.ErrorType}: {issue.Message}"));
                if (result.Issues.Count > 50)
                {
                    details += $"{Environment.NewLine}... 他{result.Issues.Count - 50:N0}件";
                }

                await ShowErrorAsync("全読込検証で問題を検出", details);
            }
        }
        catch (OperationCanceledException)
        {
            _status.Text = "全読込検証をキャンセルしました。";
        }
        catch (Exception ex)
        {
            _status.Text = $"全読込検証に失敗しました: {ex.Message}";
            await ShowErrorAsync("全読込検証を実行できません", ex.Message);
        }
        finally
        {
            EndOperation(operation);
        }
    }

    private static bool PathsEqual(string first, string second)
    {
        try
        {
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            return string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), comparison);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private async Task ShowErrorAsync(string title, string message)
    {
        var close = new Button { Content = "閉じる", HorizontalAlignment = HorizontalAlignment.Right };
        var dialog = new Window
        {
            Title = title,
            Width = 520,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 16,
                Children = { new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, close },
            },
        };
        close.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(this);
    }

    private void SetBusy(bool busy, string? status = null)
    {
        _busy = busy;
        Cursor = busy ? new Cursor(StandardCursorType.Wait) : Cursor.Default;
        _partitions.IsEnabled = !busy;
        _entries.IsEnabled = !busy;
        _createMenuItem.IsEnabled = !busy;
        _openMenuItem.IsEnabled = !busy;
        _exitMenuItem.IsEnabled = !busy;
        _cancelButton.IsEnabled = busy;
        _cancelMenuItem.IsEnabled = busy;
        RefreshCommandState();
        if (status is not null)
        {
            _status.Text = status;
        }
    }

    private CancellationTokenSource BeginOperation(string status)
    {
        if (_activeOperation is not null)
        {
            throw new InvalidOperationException("別の操作が実行中です。");
        }

        _activeOperation = new CancellationTokenSource();
        SetBusy(true, status);
        return _activeOperation;
    }

    private void EndOperation(CancellationTokenSource operation)
    {
        if (ReferenceEquals(_activeOperation, operation))
        {
            _activeOperation = null;
        }

        operation.Dispose();
        SetBusy(false);
    }

    private void CancelActiveOperation()
    {
        if (_activeOperation is null || _activeOperation.IsCancellationRequested)
        {
            return;
        }

        _activeOperation.Cancel();
        _cancelButton.IsEnabled = false;
        _cancelMenuItem.IsEnabled = false;
        _status.Text = "キャンセルを要求しました。安全に中断するまでお待ちください...";
    }

    private void RefreshCommandState()
    {
        _backButton.IsEnabled = !_busy && _directoryHistory.Count > 0;
        _previewButton.IsEnabled = !_busy
            && _entries.SelectedItem is EntryItem { Node.IsDirectory: false };
        _extractButton.IsEnabled = !_busy && _entries.SelectedItem is EntryItem;
        _previewMenuItem.IsEnabled = _previewButton.IsEnabled;
        _extractMenuItem.IsEnabled = _extractButton.IsEnabled;
        _searchMenuItem.IsEnabled = !_busy && _fileSystem is not null;
        _verifyMenuItem.IsEnabled = !_busy && _fileSystem is not null;
    }

    private void DisposeImage()
    {
        DisposeFileSystem();
        _reader?.Dispose();
        _reader = null;
        _partitions.ItemsSource = null;
        _entries.ItemsSource = null;
    }

    private void DisposeFileSystem()
    {
        (_fileSystem as IDisposable)?.Dispose();
        _fileSystem = null;
        _currentDirectory = null;
        _directoryHistory.Clear();
        _entries.ItemsSource = null;
    }

    private sealed record OpenedImage(IDiskImageReader Reader, IReadOnlyList<PartitionItem> Partitions);

    private sealed record OpenedFileSystem(
        IReadOnlyFileSystem? FileSystem,
        string Error,
        IReadOnlyList<EntryItem> Entries);

    private sealed record PartitionItem(PartitionInfo Partition)
    {
        public override string ToString() =>
            $"#{Partition.Number}  {Partition.FileSystem}  {Partition.LengthBytes / (1024d * 1024):N1} MiB";
    }

    private sealed record EntryItem(VfsNode Node)
    {
        public override string ToString() =>
            $"{(Node.IsDirectory ? "[DIR]" : "     ")} {Node.Name}  {(Node.IsDirectory ? string.Empty : $"{Node.Size:N0} bytes")}";
    }

    private sealed record SearchResultItem(SearchMatch Match)
    {
        public override string ToString() =>
            $"{(Match.Node.IsDirectory ? "[DIR]" : "     ")} {Match.Path}";
    }

    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
