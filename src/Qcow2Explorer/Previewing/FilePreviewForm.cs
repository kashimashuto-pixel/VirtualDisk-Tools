namespace Qcow2Explorer.Previewing;

public sealed class FilePreviewForm : Form
{
    public FilePreviewForm(string fileName, FilePreviewContent content)
    {
        Text = $"{fileName} - 読み取り専用プレビュー";
        Width = 1100;
        Height = 760;
        MinimumSize = new Size(700, 480);
        StartPosition = FormStartPosition.CenterParent;

        var status = new Label
        {
            AutoEllipsis = true,
            Dock = DockStyle.Bottom,
            Height = 30,
            Padding = new Padding(8, 7, 8, 0),
            Text = content.Description
        };

        Controls.Add(content.Text is not null
            ? CreateTextView(content.Text)
            : CreateWorkbookView(content.Sheets));
        Controls.Add(status);
    }

    private static Control CreateTextView(string text)
    {
        return new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            DetectUrls = false,
            WordWrap = false,
            Font = new Font("Consolas", 10),
            Text = text
        };
    }

    private static Control CreateWorkbookView(IReadOnlyList<SpreadsheetPreviewSheet> sheets)
    {
        var tabs = new TabControl { Dock = DockStyle.Fill, ShowToolTips = true };
        foreach (var sheet in sheets)
        {
            var page = new TabPage(sheet.IsTruncated ? $"{sheet.Name} (一部)" : sheet.Name);
            var grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToOrderColumns = false,
                ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithoutHeaderText,
                RowHeadersWidth = 70,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
                VirtualMode = true
            };

            var columnCount = sheet.Rows.Count == 0 ? 0 : sheet.Rows.Max(row => row.Count);
            for (var column = 0; column < columnCount; column++)
            {
                grid.Columns.Add($"Column{column + 1}", ToColumnName(column));
                grid.Columns[column].Width = 120;
                grid.Columns[column].SortMode = DataGridViewColumnSortMode.NotSortable;
            }

            if (sheet.Rows.Count > 0)
            {
                grid.RowCount = sheet.Rows.Count;
                grid.CellValueNeeded += (_, e) =>
                {
                    if (e.RowIndex >= 0 && e.RowIndex < sheet.Rows.Count
                        && e.ColumnIndex >= 0 && e.ColumnIndex < sheet.Rows[e.RowIndex].Count)
                    {
                        e.Value = sheet.Rows[e.RowIndex][e.ColumnIndex];
                    }
                };
                grid.RowPostPaint += (_, e) =>
                {
                    var bounds = new Rectangle(
                        e.RowBounds.Left,
                        e.RowBounds.Top,
                        grid.RowHeadersWidth - 6,
                        e.RowBounds.Height);
                    TextRenderer.DrawText(
                        e.Graphics,
                        (e.RowIndex + 1).ToString(),
                        grid.Font,
                        bounds,
                        grid.RowHeadersDefaultCellStyle.ForeColor,
                        TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
                };
            }

            page.Controls.Add(grid);
            if (sheet.IsTruncated)
            {
                page.ToolTipText = "表示負荷を抑えるため、末尾の行を省略しています。";
            }

            tabs.TabPages.Add(page);
        }

        return tabs;
    }

    private static string ToColumnName(int zeroBasedColumn)
    {
        var value = zeroBasedColumn + 1;
        var result = "";
        while (value > 0)
        {
            value--;
            result = (char)('A' + value % 26) + result;
            value /= 26;
        }

        return result;
    }
}
