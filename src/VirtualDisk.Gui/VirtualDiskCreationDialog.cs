using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Qcow2Explorer.Creation;

namespace VirtualDisk.Gui;

internal sealed class VirtualDiskCreationDialog : Window
{
    private const long MiB = 1024L * 1024;
    private readonly TextBox _capacityBox = new() { Text = "2048", Width = 140 };
    private readonly ComboBox _containerBox = new()
    {
        ItemsSource = new[] { "QCOW2", "RAW" },
        SelectedIndex = 0,
        Width = 130,
    };
    private readonly ComboBox _tableBox = new()
    {
        ItemsSource = new[] { "GPT", "MBR" },
        SelectedIndex = 0,
        Width = 130,
    };
    private readonly StackPanel _partitionRows = new() { Spacing = 6 };
    private readonly TextBlock _message = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly List<PartitionRow> _rows = new();

    public VirtualDiskCreationDialog()
    {
        Title = "新規仮想ディスク";
        Width = 900;
        Height = 620;
        MinWidth = 720;
        MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = BuildContent();
        AddPartition();
    }

    public VirtualDiskCreationOptions? Options { get; private set; }

    private Control BuildContent()
    {
        var settings = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children =
            {
                Field("形式", _containerBox),
                Field("パーティション表", _tableBox),
                Field("ディスク容量 (MiB)", _capacityBox),
            },
        };

        var addButton = new Button { Content = "パーティション追加" };
        addButton.Click += (_, _) => AddPartition();
        var partitionHeader = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("100,120,*,*,Auto"),
            ColumnSpacing = 8,
            Children =
            {
                Header("形式", 0),
                Header("容量 (MiB)", 1),
                Header("ボリュームラベル", 2),
                Header("パーティション名", 3),
            },
        };
        var partitionEditor = new StackPanel
        {
            Spacing = 6,
            Children = { partitionHeader, _partitionRows },
        };

        var createButton = new Button { Content = "保存先を選んで作成", IsDefault = true };
        createButton.Click += (_, _) => Accept();
        var cancelButton = new Button { Content = "キャンセル", IsCancel = true };
        cancelButton.Click += (_, _) => Close(false);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { cancelButton, createButton },
        };

        var content = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto,Auto"),
            RowSpacing = 12,
            Margin = new Thickness(18),
        };
        AddAt(content, new TextBlock
        {
            Text = "外部ツールやWSLを使わず、RAWまたは非圧縮sparse QCOW2を作成します。"
                + " NTFS／ext4は64 MiB以上、XFSは320 MiB以上を指定してください。",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        }, 0);
        AddAt(content, settings, 1);
        AddAt(content, addButton, 2);
        AddAt(content, new ScrollViewer { Content = partitionEditor }, 3);
        AddAt(content, _message, 4);
        AddAt(content, buttons, 5);
        return content;
    }

    private static StackPanel Field(string label, Control control) => new()
    {
        Spacing = 4,
        Children = { new TextBlock { Text = label }, control },
    };

    private static TextBlock Header(string text, int column)
    {
        var header = new TextBlock
        {
            Text = text,
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
        };
        Grid.SetColumn(header, column);
        return header;
    }

    private static void AddAt(Grid grid, Control control, int row)
    {
        Grid.SetRow(control, row);
        grid.Children.Add(control);
    }

    private void AddPartition()
    {
        var maximum = _tableBox.SelectedIndex == 1 ? 4 : 128;
        if (_rows.Count >= maximum)
        {
            _message.Text = $"選択したパーティション表では最大{maximum:N0}個です。";
            return;
        }

        var number = _rows.Count + 1;
        var row = new PartitionRow(number, RemovePartition);
        _rows.Add(row);
        _partitionRows.Children.Add(row.Control);
        _message.Text = string.Empty;
    }

    private void RemovePartition(PartitionRow row)
    {
        if (_rows.Count <= 1)
        {
            _message.Text = "少なくとも1つのパーティションが必要です。";
            return;
        }

        _rows.Remove(row);
        _partitionRows.Children.Remove(row.Control);
        _message.Text = string.Empty;
    }

    private void Accept()
    {
        try
        {
            Options = ReadOptions();
            Close(true);
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        {
            _message.Text = exception.Message;
        }
    }

    private VirtualDiskCreationOptions ReadOptions()
    {
        var capacityMiB = ParsePositiveInteger(_capacityBox.Text, "ディスク容量");
        if (capacityMiB < 512)
        {
            throw new ArgumentException("ディスク容量は512 MiB以上にしてください。");
        }

        var table = _tableBox.SelectedIndex == 1
            ? VirtualDiskPartitionTableKind.Mbr
            : VirtualDiskPartitionTableKind.Gpt;
        if (table == VirtualDiskPartitionTableKind.Mbr && _rows.Count > 4)
        {
            throw new ArgumentException("MBRでは最大4パーティションです。不要な行を削除してください。");
        }

        var partitions = _rows.Select((row, index) => row.ReadDefinition(index + 1)).ToArray();
        var capacityBytes = checked(capacityMiB * MiB);
        var partitionBytes = partitions.Aggregate(0L, (total, partition) => checked(total + partition.SizeBytes));
        var trailingMetadataBytes = table == VirtualDiskPartitionTableKind.Gpt ? 33L * 512 : 0;
        if (checked(MiB + partitionBytes + trailingMetadataBytes) > capacityBytes)
        {
            throw new ArgumentException("パーティションの合計容量とパーティション表がディスク容量に収まりません。");
        }

        return new VirtualDiskCreationOptions(
            capacityBytes,
            _containerBox.SelectedIndex == 1
                ? VirtualDiskContainerFormat.Raw
                : VirtualDiskContainerFormat.Qcow2,
            table,
            partitions);
    }

    private static long ParsePositiveInteger(string? text, string field)
    {
        if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            || value <= 0)
        {
            throw new ArgumentException($"{field}は正の整数で指定してください。");
        }

        return value;
    }

    private sealed class PartitionRow
    {
        private readonly ComboBox _fileSystemBox = new()
        {
            ItemsSource = new[] { "NTFS", "ext4", "XFS" },
            SelectedIndex = 0,
        };
        private readonly TextBox _sizeBox = new() { Text = "512" };
        private readonly TextBox _labelBox;
        private readonly TextBox _nameBox;

        public PartitionRow(int number, Action<PartitionRow> remove)
        {
            _labelBox = new TextBox { Text = $"VDT_NTFS_{number}" };
            _nameBox = new TextBox { Text = $"NTFS partition {number}" };
            var removeButton = new Button { Content = "削除" };
            removeButton.Click += (_, _) => remove(this);
            Control = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("100,120,*,*,Auto"),
                ColumnSpacing = 8,
                Children = { _fileSystemBox, _sizeBox, _labelBox, _nameBox, removeButton },
            };
            for (var column = 0; column < Control.Children.Count; column++)
            {
                Grid.SetColumn(Control.Children[column], column);
            }
        }

        public Grid Control { get; }

        public VirtualDiskPartitionDefinition ReadDefinition(int number)
        {
            var fileSystem = _fileSystemBox.SelectedIndex switch
            {
                0 => VirtualDiskFileSystemKind.Ntfs,
                1 => VirtualDiskFileSystemKind.Ext4,
                2 => VirtualDiskFileSystemKind.Xfs,
                _ => throw new ArgumentException($"パーティション#{number}のファイルシステムを選択してください。"),
            };
            var sizeMiB = ParsePositiveInteger(_sizeBox.Text, $"パーティション#{number}の容量");
            var minimumMiB = fileSystem == VirtualDiskFileSystemKind.Xfs ? 320 : 64;
            if (sizeMiB < minimumMiB)
            {
                throw new ArgumentException(
                    $"パーティション#{number}の{fileSystem}容量は{minimumMiB:N0} MiB以上にしてください。");
            }

            return new VirtualDiskPartitionDefinition(
                checked(sizeMiB * MiB),
                _nameBox.Text ?? string.Empty,
                _labelBox.Text ?? string.Empty,
                fileSystem);
        }
    }
}

internal sealed record VirtualDiskCreationOptions(
    long CapacityBytes,
    VirtualDiskContainerFormat ContainerFormat,
    VirtualDiskPartitionTableKind PartitionTable,
    IReadOnlyList<VirtualDiskPartitionDefinition> Partitions);
