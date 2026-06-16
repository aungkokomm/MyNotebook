using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

QuestPDF.Settings.License = LicenseType.Community;
foreach (var f in new[] { @"C:\Windows\Fonts\calibri.ttf", @"C:\Windows\Fonts\mmrtext.ttf" })
    try { using var s = File.OpenRead(f); QuestPDF.Drawing.FontManager.RegisterFont(s); } catch { }

(string title, string date, string body)[] notes =
{
    ("First note", "Monday", "Hello world. This is the first note."),
    ("မြန်မာ မှတ်စု", "Tuesday", "ဒါက မြန်မာစာ စမ်းသပ်မှု ဖြစ်ပါတယ်။ Mixed English too."),
    ("Third note", "Wednesday", "Another note with several\nlines of\ntext content."),
};

byte[] RenderNote((string title, string date, string body) n) =>
    Document.Create(c => c.Page(p =>
    {
        p.Size(PageSizes.A4); p.Margin(50);
        p.DefaultTextStyle(t => t.FontSize(11).FontFamily("Calibri", "Myanmar Text"));
        p.Content().Column(col =>
        {
            col.Item().Text(n.title).FontSize(20).SemiBold();
            col.Item().Text(n.date).FontSize(9).FontColor(Colors.Grey.Medium);
            col.Item().PaddingTop(12).Text(n.body);
        });
    })).GeneratePdf();

var outPath = Path.Combine(Path.GetTempPath(), "pdfcheck_bookmarks.pdf");
using (var outDoc = new PdfDocument())
{
    foreach (var n in notes)
    {
        var bytes = RenderNote(n);
        using var ms = new MemoryStream(bytes);
        using var src = PdfReader.Open(ms, PdfDocumentOpenMode.Import);
        int start = outDoc.PageCount;
        for (int i = 0; i < src.PageCount; i++) outDoc.AddPage(src.Pages[i]);
        if (outDoc.PageCount > start)
            outDoc.Outlines.Add(n.title, outDoc.Pages[start], false);
    }
    outDoc.Save(outPath);
}

// Read it back and report the outline (bookmark) tree.
using var check = PdfReader.Open(outPath, PdfDocumentOpenMode.InformationOnly);
Console.WriteLine($"Pages: {check.PageCount}");
Console.WriteLine($"Bookmarks: {check.Outlines.Count}");
foreach (var o in check.Outlines)
    Console.WriteLine($"  • {o.Title}");
Console.WriteLine($"Saved: {outPath}");
