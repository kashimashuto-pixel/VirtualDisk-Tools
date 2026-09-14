using System.IO.Compression;
using System.Text;
using System.Xml;
using Qcow2Explorer.Previewing;

internal static class FilePreviewRobustnessTests
{
    public static void Run()
    {
        TestTextAndOfficePreviews();
        TestUnsafeXmlRejected();
        TestWorkbookRelationshipPaths();
        TestCancellation();
    }

    private static void TestTextAndOfficePreviews()
    {
        var text = FilePreviewReader.Read("notes.txt", Encoding.UTF8.GetBytes("cross-platform text"));
        Assert(text.Text == "cross-platform text", "UTF-8 preview");

        var docx = CreateZip(("word/document.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
              <w:body><w:p><w:r><w:t>portable document</w:t></w:r></w:p></w:body>
            </w:document>
            """));
        Assert(
            FilePreviewReader.Read("report.docx", docx).Text?.Contains("portable document", StringComparison.Ordinal) == true,
            "DOCX preview");

        var xlsx = CreateZip(
            ("xl/workbook.xml", """
                <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"
                          xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                  <sheets><sheet name="Data" sheetId="1" r:id="rId1"/></sheets>
                </workbook>
                """),
            ("xl/_rels/workbook.xml.rels", """
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Target="worksheets/sheet1.xml"/>
                </Relationships>
                """),
            ("xl/worksheets/sheet1.xml", """
                <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
                  <sheetData><row r="1"><c r="A1"><v>42</v></c></row></sheetData>
                </worksheet>
                """));
        var workbook = FilePreviewReader.Read("book.xlsx", xlsx);
        Assert(
            workbook.Sheets.Count == 1 && workbook.Sheets[0].Rows[0][0] == "42",
            "XLSX preview");
    }

    private static void TestUnsafeXmlRejected()
    {
        var docx = CreateZip(("word/document.xml", """
            <!DOCTYPE document [<!ENTITY external SYSTEM "file:///etc/passwd">]>
            <document>&external;</document>
            """));
        AssertThrows<XmlException>(
            () => FilePreviewReader.Read("unsafe.docx", docx),
            "Office DTD rejected");
    }

    private static void TestWorkbookRelationshipPaths()
    {
        var parentTargetWorkbook = CreateZip(
            ("xl/workbook.xml", """
                <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"
                          xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                  <sheets><sheet name="Parent" sheetId="1" r:id="rId1"/></sheets>
                </workbook>
                """),
            ("xl/_rels/workbook.xml.rels", """
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Target="../worksheets/root-sheet.xml"/>
                </Relationships>
                """),
            ("worksheets/root-sheet.xml", """
                <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
                  <sheetData><row r="1"><c r="A1"><v>7</v></c></row></sheetData>
                </worksheet>
                """));
        var preview = FilePreviewReader.Read("parent-target.xlsx", parentTargetWorkbook);
        Assert(preview.Sheets[0].Rows[0][0] == "7", "workbook parent relationship resolved from xl directory");

        var escapingWorkbook = CreateZip(
            ("xl/workbook.xml", """
                <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"
                          xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                  <sheets><sheet name="Unsafe" sheetId="1" r:id="rId1"/></sheets>
                </workbook>
                """),
            ("xl/_rels/workbook.xml.rels", """
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Target="../../outside.xml"/>
                </Relationships>
                """));
        AssertThrows<InvalidDataException>(
            () => FilePreviewReader.Read("escaping.xlsx", escapingWorkbook),
            "workbook relationship cannot escape archive root");
    }

    private static void TestCancellation()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        AssertThrows<OperationCanceledException>(
            () => FilePreviewReader.Read("notes.txt", Encoding.UTF8.GetBytes("cancel"), source.Token),
            "preview cancellation propagated");
    }

    private static byte[] CreateZip(params (string Name, string Content)[] entries)
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name, CompressionLevel.SmallestSize);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
        }

        return memory.ToArray();
    }

    private static void AssertThrows<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Assertion failed: {message}");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Assertion failed: {message}");
        }
    }
}
