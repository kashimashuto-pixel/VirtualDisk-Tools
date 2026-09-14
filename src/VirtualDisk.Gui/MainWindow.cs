using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Qcow2Explorer.Core;
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
    private readonly Button _backButton = new() { Content = "上へ", IsEnabled = false };
    private readonly Button _extractButton = new() { Content = "抽出...", IsEnabled = false };
    private readonly Stack<VfsNode> _directoryHistory = new();
    private IDiskImageReader? _reader;
    private IReadOnlyFileSystem? _fileSystem;
    private VfsNode? _currentDirectory;

    public MainWindow(string? initialPath = null)
    {
        Title = "VirtualDisk Tools";
        Width = 1100;
        Height = 720;
        MinWidth = 760;
        MinHeight = 480;
        Content = BuildContent();
        Closed += (_, _) => DisposeImage();
        _partitions.SelectionChanged += (_, _) => OpenSelectedPartition();
        _entries.SelectionChanged += (_, _) =>
            _extractButton.IsEnabled = _entries.SelectedItem is EntryItem { Node.IsDirectory: false };
        _entries.DoubleTapped += (_, _) => NavigateSelectedEntry();
        _backButton.Click += (_, _) => NavigateUp();
        _extractButton.Click += async (_, _) => await ExtractSelectedAsync();

        if (!string.IsNullOrWhiteSpace(initialPath))
        {
            Opened += async (_, _) => await OpenImageAsync(initialPath);
        }
    }

    private Control BuildContent()
    {
        var openButton = new Button { Content = "開く..." };
        openButton.Click += async (_, _) => await ChooseAndOpenImageAsync();
        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(12),
            Children = { openButton, _backButton, _extractButton },
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

    private async Task OpenImageAsync(string path)
    {
        try
        {
            SetBusy(true, "ディスクイメージを解析しています...");
            var opened = await Task.Run(() => OpenImage(path));
            DisposeImage();
            _reader = opened.Reader;
            _partitions.ItemsSource = opened.Partitions;
            _imageSummary.Text = $"{opened.Reader.FormatName} — {opened.Reader.Length:N0} bytes — {opened.Partitions.Count:N0} partition(s)";
            _status.Text = $"開きました: {opened.Reader.Path}";
            if (opened.Partitions.Count > 0)
            {
                _partitions.SelectedIndex = 0;
            }
        }
        catch (Exception ex)
        {
            _status.Text = $"開けませんでした: {ex.Message}";
            await ShowErrorAsync("ディスクイメージを開けません", ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static OpenedImage OpenImage(string path)
    {
        var reader = DiskImageReaderFactory.Open(path);
        try
        {
            var partitions = PartitionTableReader.ReadPartitionsWithWholeDiskFallback(reader);
            foreach (var partition in partitions)
            {
                partition.FileSystem = FileSystemDetector.Detect(reader, partition);
            }

            return new OpenedImage(reader, partitions.Select(partition => new PartitionItem(partition)).ToArray());
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    private void OpenSelectedPartition()
    {
        DisposeFileSystem();
        if (_reader is null || _partitions.SelectedItem is not PartitionItem selected)
        {
            return;
        }

        var fileSystem = FileSystemDetector.TryOpen(_reader, selected.Partition, out var error);
        if (fileSystem is null)
        {
            _status.Text = error;
            return;
        }

        _fileSystem = fileSystem;
        _directoryHistory.Clear();
        ShowDirectory(fileSystem.Root);
        _status.Text = $"{fileSystem.Name}を開きました。";
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
        _backButton.IsEnabled = _directoryHistory.Count > 0;
        _extractButton.IsEnabled = false;
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

        try
        {
            if (_reader is not null && PathsEqual(path, _reader.Path))
            {
                throw new IOException("開いているディスクイメージ自身には抽出できません。");
            }

            SetBusy(true, $"{selected.Node.Name}を抽出しています...");
            var progress = new Progress<CopyProgress>(update =>
            {
                var percentage = update.TotalBytes == 0
                    ? 100
                    : (int)Math.Min(100, update.BytesCopied * 100d / update.TotalBytes);
                _status.Text = $"{selected.Node.Name}を抽出しています... {percentage}%";
            });
            await FileSystemExporter.ExtractFileAsync(
                _fileSystem,
                selected.Node,
                path,
                overwrite: true,
                progress: progress);

            _status.Text = $"抽出しました: {path}";
        }
        catch (Exception ex)
        {
            _status.Text = $"抽出に失敗しました: {ex.Message}";
            await ShowErrorAsync("ファイルを抽出できません", ex.Message);
        }
        finally
        {
            SetBusy(false);
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
        Cursor = busy ? new Cursor(StandardCursorType.Wait) : Cursor.Default;
        _partitions.IsEnabled = !busy;
        _entries.IsEnabled = !busy;
        if (status is not null)
        {
            _status.Text = status;
        }
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
}
