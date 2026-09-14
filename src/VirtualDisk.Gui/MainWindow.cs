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

namespace VirtualDisk.Gui;

public sealed class MainWindow : Window
{
    private readonly TextBlock _imageSummary = new() { Text = "ディスクイメージを開いてください。" };
    private readonly TextBlock _pathLabel = new() { Text = "/" };
    private readonly TextBlock _status = new() { Text = "準備完了" };
    private readonly ListBox _partitions = new();
    private readonly ListBox _entries = new();
    private readonly Button _createButton = new() { Content = "新規作成..." };
    private readonly Button _openButton = new() { Content = "開く..." };
    private readonly Button _backButton = new() { Content = "上へ", IsEnabled = false };
    private readonly Button _extractButton = new() { Content = "抽出...", IsEnabled = false };
    private readonly Button _verifyButton = new() { Content = "全読込検証", IsEnabled = false };
    private readonly Button _cancelButton = new() { Content = "キャンセル", IsEnabled = false };
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
        _entries.DoubleTapped += (_, _) => NavigateSelectedEntry();
        _backButton.Click += (_, _) => NavigateUp();
        _createButton.Click += async (_, _) => await CreateDiskAsync();
        _extractButton.Click += async (_, _) => await ExtractSelectedAsync();
        _verifyButton.Click += async (_, _) => await VerifyFileSystemAsync();
        _cancelButton.Click += (_, _) => CancelActiveOperation();

        if (!string.IsNullOrWhiteSpace(initialPath))
        {
            Opened += async (_, _) => await OpenImageAsync(initialPath);
        }
    }

    private Control BuildContent()
    {
        _openButton.Click += async (_, _) => await ChooseAndOpenImageAsync();
        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(12),
            Children = { _createButton, _openButton, _backButton, _extractButton, _verifyButton, _cancelButton },
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
        DockPanel.SetDock(toolbar, Dock.Top);
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(statusBorder, Dock.Bottom);
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
            options.Partitions);
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
                if (operation.Token.IsCancellationRequested)
                {
                    (fileSystem as IDisposable)?.Dispose();
                    operation.Token.ThrowIfCancellationRequested();
                }

                return new OpenedFileSystem(fileSystem, error);
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
            ShowDirectory(opened.FileSystem.Root);
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

    private void NavigateSelectedEntry()
    {
        if (_entries.SelectedItem is not EntryItem { Node.IsDirectory: true } selected || _currentDirectory is null)
        {
            return;
        }

        _directoryHistory.Push(_currentDirectory);
        ShowDirectory(selected.Node);
    }

    private void NavigateUp()
    {
        if (_directoryHistory.TryPop(out var directory))
        {
            ShowDirectory(directory);
        }
    }

    private void ShowDirectory(VfsNode directory)
    {
        if (_fileSystem is null)
        {
            return;
        }

        _currentDirectory = directory;
        _pathLabel.Text = string.IsNullOrWhiteSpace(directory.VirtualPath) ? "/" : directory.VirtualPath;
        _entries.ItemsSource = _fileSystem.ListDirectory(directory).Select(node => new EntryItem(node)).ToArray();
        RefreshCommandState();
    }

    private async Task ExtractSelectedAsync()
    {
        if (_fileSystem is null || _entries.SelectedItem is not EntryItem { Node.IsDirectory: false } selected)
        {
            return;
        }

        var destination = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "ファイルを抽出",
            SuggestedFileName = selected.Node.Name,
        });
        var path = destination?.TryGetLocalPath();
        if (path is null)
        {
            return;
        }

        var fileSystem = _fileSystem;
        var file = selected.Node;
        var operation = BeginOperation($"{file.Name}を抽出しています...");
        try
        {
            if (_reader is not null && PathsEqual(path, _reader.Path))
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
        catch (OperationCanceledException)
        {
            _status.Text = "抽出をキャンセルしました。抽出先は変更されていません。";
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
        _createButton.IsEnabled = !busy;
        _openButton.IsEnabled = !busy;
        _cancelButton.IsEnabled = busy;
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
        _status.Text = "キャンセルを要求しました。安全に中断するまでお待ちください...";
    }

    private void RefreshCommandState()
    {
        _backButton.IsEnabled = !_busy && _directoryHistory.Count > 0;
        _extractButton.IsEnabled = !_busy
            && _entries.SelectedItem is EntryItem { Node.IsDirectory: false };
        _verifyButton.IsEnabled = !_busy && _fileSystem is not null;
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

    private sealed record OpenedFileSystem(IReadOnlyFileSystem? FileSystem, string Error);

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

    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
