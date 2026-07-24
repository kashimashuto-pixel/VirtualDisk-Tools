using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Qcow2Explorer.Previewing;

public static class FilePreviewReader
{
    public const int MaximumFileSize = 128 * 1024 * 1024;
    private const long MaximumXmlEntrySize = 32 * 1024 * 1024;
    private const long MaximumTotalXmlSize = 128 * 1024 * 1024;
    private const int MaximumWorksheetRows = 10_000;
    private const int MaximumWorksheetColumns = 256;
    private const int MaximumWorksheetCells = 500_000;

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".log", ".csv", ".tsv", ".json", ".xml", ".yaml", ".yml", ".md",
        ".ini", ".cfg", ".conf", ".cs", ".vb", ".ps1", ".bat", ".cmd", ".sql",
        ".html", ".htm", ".css", ".js", ".ts"
    };

    public static bool CanPreview(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return TextExtensions.Contains(extension)
            || extension.Equals(".docx", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".xlsm", StringComparison.OrdinalIgnoreCase);
    }

    public static FilePreviewContent Read(string fileName, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length > MaximumFileSize)
        {
            throw new InvalidDataException($"別窓表示の上限は{MaximumFileSize / 1024 / 1024:N0} MBです。");
        }

        if (TryRead(fileName, data, out var content))
        {
            return content;
        }

        throw new NotSupportedException(
            "対応する文書形式ではなく、内容もテキストとして安全に判定できませんでした。");
    }

    public static bool TryRead(string fileName, byte[] data, out FilePreviewContent content)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length > MaximumFileSize)
        {
            content = null!;
            return false;
        }

        var extension = Path.GetExtension(fileName);
        if (TextExtensions.Contains(extension))
        {
            content = new FilePreviewContent(
                "テキスト（読み取り専用）",
                DecodeText(data),
                []);
            return true;
        }

        if (extension.Equals(".docx", StringComparison.OrdinalIgnoreCase))
        {
            content = ReadDocx(data);
            return true;
        }

        if (extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".xlsm", StringComparison.OrdinalIgnoreCase))
        {
            content = ReadWorkbook(data);
            return true;
        }

        if (TryDecodeProbableText(data, out var text))
        {
            content = new FilePreviewContent(
                "内容からテキストと判定（読み取り専用）",
                text,
                []);
            return true;
        }

        content = null!;
        return false;
    }

    private static FilePreviewContent ReadDocx(byte[] data)
    {
        using var archive = OpenArchive(data);
        long totalXmlSize = 0;
        var document = LoadXmlEntry(archive, "word/document.xml", ref totalXmlSize);
        XNamespace word = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var output = new StringBuilder();

        foreach (var element in document.Descendants(word + "body").Elements())
        {
            if (element.Name == word + "p")
            {
                AppendParagraph(output, element, word);
            }
            else if (element.Name == word + "tbl")
            {
                foreach (var row in element.Elements(word + "tr"))
                {
                    var cells = row.Elements(word + "tc")
                        .Select(cell => string.Join(
                            " ",
                            cell.Descendants(word + "p").Select(paragraph => ReadParagraph(paragraph, word))))
                        .ToArray();
                    output.AppendLine(string.Join('\t', cells));
                }

                output.AppendLine();
            }
        }

        return new FilePreviewContent(
            "Word Open XML（本文・表の読み取り専用表示。画像・レイアウト・変更履歴は簡略化されます）",
            output.ToString(),
            []);
    }

    private static FilePreviewContent ReadWorkbook(byte[] data)
    {
        using var archive = OpenArchive(data);
        long totalXmlSize = 0;
        var workbook = LoadXmlEntry(archive, "xl/workbook.xml", ref totalXmlSize);
        var relationships = LoadXmlEntry(archive, "xl/_rels/workbook.xml.rels", ref totalXmlSize);
        XNamespace spreadsheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        XNamespace officeRelationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        XNamespace packageRelationships = "http://schemas.openxmlformats.org/package/2006/relationships";

        var relationshipTargets = relationships.Root?
            .Elements(packageRelationships + "Relationship")
            .Where(item => item.Attribute("Id") is not null && item.Attribute("Target") is not null)
            .ToDictionary(
                item => (string)item.Attribute("Id")!,
                item => NormalizeWorkbookTarget((string)item.Attribute("Target")!),
                StringComparer.Ordinal)
            ?? new Dictionary<string, string>(StringComparer.Ordinal);

        var sharedStrings = ReadSharedStrings(archive, spreadsheet, ref totalXmlSize);
        var sheets = new List<SpreadsheetPreviewSheet>();
        foreach (var sheet in workbook.Descendants(spreadsheet + "sheet"))
        {
            var name = (string?)sheet.Attribute("name") ?? $"Sheet{sheets.Count + 1}";
            var relationshipId = (string?)sheet.Attribute(officeRelationships + "id");
            if (relationshipId is null || !relationshipTargets.TryGetValue(relationshipId, out var target))
            {
                continue;
            }

            var worksheet = LoadXmlEntry(archive, target, ref totalXmlSize);
            sheets.Add(ReadWorksheet(name, worksheet, spreadsheet, sharedStrings));
        }

        if (sheets.Count == 0)
        {
            sheets.Add(new SpreadsheetPreviewSheet("(シートなし)", [], false));
        }

        return new FilePreviewContent(
            "Excel Open XML（数式は式を表示、書式・グラフ・マクロは実行しません）",
            null,
            sheets);
    }

    private static SpreadsheetPreviewSheet ReadWorksheet(
        string name,
        XDocument worksheet,
        XNamespace spreadsheet,
        IReadOnlyList<string> sharedStrings)
    {
        var values = new Dictionary<(int Row, int Column), string>();
        var maxRow = -1;
        var maxColumn = -1;
        foreach (var cell in worksheet.Descendants(spreadsheet + "c"))
        {
            var reference = (string?)cell.Attribute("r");
            if (!TryParseCellReference(reference, out var row, out var column)
                || row >= MaximumWorksheetRows
                || column >= MaximumWorksheetColumns)
            {
                continue;
            }

            var type = (string?)cell.Attribute("t");
            var formula = cell.Element(spreadsheet + "f")?.Value;
            var value = ReadCellValue(cell, type, spreadsheet, sharedStrings);
            if (!string.IsNullOrWhiteSpace(formula))
            {
                value = $"={formula}";
            }

            values[(row, column)] = value;
            maxRow = Math.Max(maxRow, row);
            maxColumn = Math.Max(maxColumn, column);
        }

        if (maxRow < 0 || maxColumn < 0)
        {
            return new SpreadsheetPreviewSheet(name, [], false);
        }

        var columnCount = maxColumn + 1;
        var rowCount = Math.Min(maxRow + 1, Math.Max(1, MaximumWorksheetCells / columnCount));
        var truncated = rowCount < maxRow + 1;
        var rows = new List<IReadOnlyList<string>>(rowCount);
        for (var row = 0; row < rowCount; row++)
        {
            var cells = new string[columnCount];
            for (var column = 0; column < columnCount; column++)
            {
                cells[column] = values.GetValueOrDefault((row, column), "");
            }

            rows.Add(cells);
        }

        return new SpreadsheetPreviewSheet(name, rows, truncated);
    }

    private static string ReadCellValue(
        XElement cell,
        string? type,
        XNamespace spreadsheet,
        IReadOnlyList<string> sharedStrings)
    {
        if (type == "inlineStr")
        {
            return string.Concat(cell.Descendants(spreadsheet + "t").Select(item => item.Value));
        }

        var value = cell.Element(spreadsheet + "v")?.Value ?? "";
        if (type == "s" && int.TryParse(value, out var sharedIndex)
            && sharedIndex >= 0 && sharedIndex < sharedStrings.Count)
        {
            return sharedStrings[sharedIndex];
        }

        return type == "b"
            ? value == "1" ? "TRUE" : "FALSE"
            : value;
    }

    private static IReadOnlyList<string> ReadSharedStrings(
        ZipArchive archive,
        XNamespace spreadsheet,
        ref long totalXmlSize)
    {
        var entry = archive.GetEntry("xl/sharedStrings.xml");
        if (entry is null)
        {
            return [];
        }

        var document = LoadXmlEntry(entry, ref totalXmlSize);
        return document.Descendants(spreadsheet + "si")
            .Select(item => string.Concat(item.Descendants(spreadsheet + "t").Select(text => text.Value)))
            .ToList();
    }

    private static string NormalizeWorkbookTarget(string target)
    {
        var normalized = target.Replace('\\', '/').TrimStart('/');
        while (normalized.StartsWith("../", StringComparison.Ordinal))
        {
            normalized = normalized[3..];
        }

        return normalized.StartsWith("xl/", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : $"xl/{normalized}";
    }

    private static void AppendParagraph(StringBuilder output, XElement paragraph, XNamespace word)
    {
        output.AppendLine(ReadParagraph(paragraph, word));
    }

    private static string ReadParagraph(XElement paragraph, XNamespace word)
    {
        var output = new StringBuilder();
        foreach (var element in paragraph.Descendants())
        {
            if (element.Name == word + "t")
            {
                output.Append(element.Value);
            }
            else if (element.Name == word + "tab")
            {
                output.Append('\t');
            }
            else if (element.Name == word + "br" || element.Name == word + "cr")
            {
                output.AppendLine();
            }
        }

        return output.ToString();
    }

    private static string DecodeText(byte[] data)
    {
        if (data.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }))
        {
            return Encoding.UTF8.GetString(data, 3, data.Length - 3);
        }

        if (data.AsSpan().StartsWith(new byte[] { 0xff, 0xfe, 0x00, 0x00 }))
        {
            return Encoding.UTF32.GetString(data, 4, data.Length - 4);
        }

        if (data.AsSpan().StartsWith(new byte[] { 0x00, 0x00, 0xfe, 0xff }))
        {
            return new UTF32Encoding(true, true).GetString(data, 4, data.Length - 4);
        }

        if (data.AsSpan().StartsWith(new byte[] { 0xff, 0xfe }))
        {
            return Encoding.Unicode.GetString(data, 2, data.Length - 2);
        }

        if (data.AsSpan().StartsWith(new byte[] { 0xfe, 0xff }))
        {
            return Encoding.BigEndianUnicode.GetString(data, 2, data.Length - 2);
        }

        try
        {
            return new UTF8Encoding(false, true).GetString(data);
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            try
            {
                return Encoding.GetEncoding(
                    932,
                    EncoderFallback.ExceptionFallback,
                    DecoderFallback.ExceptionFallback).GetString(data);
            }
            catch (DecoderFallbackException)
            {
                return Encoding.Latin1.GetString(data);
            }
        }
    }

    private static bool TryDecodeProbableText(byte[] data, out string text)
    {
        if (data.Length == 0)
        {
            text = "";
            return true;
        }

        if (HasTextBom(data))
        {
            text = DecodeText(data);
            return IsPlausibleText(text);
        }

        var sampleLength = Math.Min(data.Length, 64 * 1024);
        var sample = data.AsSpan(0, sampleLength).ToArray();
        var strictUtf8 = new UTF8Encoding(false, true);
        if (TryDecodeCandidate(strictUtf8, sample, data, out text))
        {
            return true;
        }

        if (LooksLikeUtf16(sample, littleEndian: true)
            && TryDecodeCandidate(new UnicodeEncoding(false, false, true), sample, data, out text))
        {
            return true;
        }

        if (LooksLikeUtf16(sample, littleEndian: false)
            && TryDecodeCandidate(new UnicodeEncoding(true, false, true), sample, data, out text))
        {
            return true;
        }

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var shiftJis = Encoding.GetEncoding(
            932,
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback);
        if (TryDecodeCandidate(shiftJis, sample, data, out text))
        {
            return true;
        }

        if (TryDecodeCandidate(Encoding.Latin1, sample, data, out text))
        {
            return true;
        }

        text = "";
        return false;
    }

    private static bool TryDecodeCandidate(
        Encoding encoding,
        byte[] sample,
        byte[] data,
        out string text)
    {
        string? sampleText = null;
        var maximumTrim = sample.Length == data.Length ? 0 : Math.Min(4, sample.Length - 1);
        for (var trim = 0; trim <= maximumTrim; trim++)
        {
            try
            {
                sampleText = encoding.GetString(sample, 0, sample.Length - trim);
                break;
            }
            catch (DecoderFallbackException)
            {
                // A bounded sample can end in the middle of a multibyte character.
            }
        }

        if (sampleText is null || !IsPlausibleText(sampleText))
        {
            text = "";
            return false;
        }

        try
        {
            text = sample.Length == data.Length
                ? sampleText
                : encoding.GetString(data);
            return IsPlausibleText(text);
        }
        catch (DecoderFallbackException)
        {
            text = "";
            return false;
        }
    }

    private static bool IsPlausibleText(string text)
    {
        if (text.Length == 0)
        {
            return true;
        }

        var controls = 0;
        var visible = 0;
        foreach (var character in text)
        {
            if (character == '\0')
            {
                return false;
            }

            if (char.IsControl(character)
                && character is not '\r' and not '\n' and not '\t' and not '\f')
            {
                controls++;
            }
            else if (!char.IsWhiteSpace(character))
            {
                visible++;
            }
        }

        var maximumControls = Math.Max(1, text.Length / 100);
        return controls <= maximumControls && (visible > 0 || text.All(char.IsWhiteSpace));
    }

    private static bool LooksLikeUtf16(byte[] sample, bool littleEndian)
    {
        if (sample.Length < 4)
        {
            return false;
        }

        var pairs = sample.Length / 2;
        var expectedNulls = 0;
        var otherNulls = 0;
        for (var index = 0; index < pairs * 2; index += 2)
        {
            var expectedIndex = littleEndian ? index + 1 : index;
            var otherIndex = littleEndian ? index : index + 1;
            if (sample[expectedIndex] == 0)
            {
                expectedNulls++;
            }

            if (sample[otherIndex] == 0)
            {
                otherNulls++;
            }
        }

        return expectedNulls >= Math.Max(2, pairs / 5)
            && otherNulls <= Math.Max(1, pairs / 20);
    }

    private static bool HasTextBom(byte[] data)
    {
        var span = data.AsSpan();
        return span.StartsWith(new byte[] { 0xef, 0xbb, 0xbf })
            || span.StartsWith(new byte[] { 0xff, 0xfe })
            || span.StartsWith(new byte[] { 0xfe, 0xff })
            || span.StartsWith(new byte[] { 0x00, 0x00, 0xfe, 0xff });
    }

    private static bool TryParseCellReference(string? reference, out int row, out int column)
    {
        row = -1;
        column = -1;
        if (string.IsNullOrWhiteSpace(reference))
        {
            return false;
        }

        var index = 0;
        var columnNumber = 0;
        while (index < reference.Length && char.IsLetter(reference[index]))
        {
            columnNumber = checked(columnNumber * 26 + char.ToUpperInvariant(reference[index]) - 'A' + 1);
            index++;
        }

        if (index == 0 || !int.TryParse(reference[index..], out var rowNumber) || rowNumber <= 0)
        {
            return false;
        }

        row = rowNumber - 1;
        column = columnNumber - 1;
        return true;
    }

    private static ZipArchive OpenArchive(byte[] data)
    {
        try
        {
            return new ZipArchive(new MemoryStream(data, writable: false), ZipArchiveMode.Read);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException("Office Open XMLファイルをZIPとして読み取れませんでした。", ex);
        }
    }

    private static XDocument LoadXmlEntry(ZipArchive archive, string path, ref long totalXmlSize)
    {
        var entry = archive.GetEntry(path)
            ?? throw new InvalidDataException($"Officeファイル内に必要な項目がありません: {path}");
        return LoadXmlEntry(entry, ref totalXmlSize);
    }

    private static XDocument LoadXmlEntry(ZipArchiveEntry entry, ref long totalXmlSize)
    {
        if (entry.Length > MaximumXmlEntrySize
            || totalXmlSize > MaximumTotalXmlSize - entry.Length)
        {
            throw new InvalidDataException("Officeファイルの展開後XMLサイズが表示上限を超えています。");
        }

        totalXmlSize += entry.Length;
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumXmlEntrySize
        });
        return XDocument.Load(reader, LoadOptions.None);
    }
}

public sealed record FilePreviewContent(
    string Description,
    string? Text,
    IReadOnlyList<SpreadsheetPreviewSheet> Sheets);

public sealed record SpreadsheetPreviewSheet(
    string Name,
    IReadOnlyList<IReadOnlyList<string>> Rows,
    bool IsTruncated);
