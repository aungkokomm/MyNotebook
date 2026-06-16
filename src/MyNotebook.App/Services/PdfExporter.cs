using QuestPDF.Drawing;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace MyNotebook.App.Services;

/// <summary>
/// Clean text PDF export. Uses QuestPDF (Skia/HarfBuzz under the hood) so complex
/// scripts like Myanmar shape correctly; the Myanmar Text font is embedded.
/// </summary>
public static class PdfExporter
{
    public sealed record NoteDoc(string Title, string DateLine, string Body);

    private static bool _init;

    private static void EnsureInit()
    {
        if (_init) return;
        QuestPDF.Settings.License = LicenseType.Community;
        Register(@"C:\Windows\Fonts\calibri.ttf");
        Register(@"C:\Windows\Fonts\calibrib.ttf");
        Register(@"C:\Windows\Fonts\calibrii.ttf");
        Register(@"C:\Windows\Fonts\mmrtext.ttf");   // Myanmar Text
        Register(@"C:\Windows\Fonts\mmrtextb.ttf");
        Register(@"C:\Windows\Fonts\seguiemj.ttf");  // emoji (best effort)
        _init = true;
    }

    private static void Register(string path)
    {
        try { if (File.Exists(path)) { using var s = File.OpenRead(path); FontManager.RegisterFont(s); } }
        catch { /* skip missing/locked fonts */ }
    }

    /// <summary>
    /// Render each note to its own PDF, then merge into one document with a PDF
    /// outline (bookmark) entry per note so a folder export is navigable.
    /// </summary>
    public static void Export(IReadOnlyList<NoteDoc> notes, string outPath)
    {
        EnsureInit();
        if (notes.Count == 0) return;

        using var outDoc = new PdfSharp.Pdf.PdfDocument();
        foreach (var n in notes)
        {
            var bytes = RenderNote(n);
            using var ms = new MemoryStream(bytes);
            using var src = PdfSharp.Pdf.IO.PdfReader.Open(ms, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);

            int start = outDoc.PageCount;
            for (int i = 0; i < src.PageCount; i++)
                outDoc.AddPage(src.Pages[i]);

            if (outDoc.PageCount > start)
                outDoc.Outlines.Add(
                    string.IsNullOrWhiteSpace(n.Title) ? "(untitled)" : n.Title,
                    outDoc.Pages[start], false);
        }
        outDoc.Save(outPath);
    }

    private static byte[] RenderNote(NoteDoc n) =>
        Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(50);
                // Font fallback chain: Latin -> Myanmar -> emoji.
                page.DefaultTextStyle(t => t.FontSize(11).FontFamily("Calibri", "Myanmar Text", "Segoe UI Emoji"));

                page.Content().Column(col =>
                {
                    col.Spacing(2);
                    col.Item().Text(string.IsNullOrWhiteSpace(n.Title) ? "(untitled)" : n.Title)
                       .FontSize(20).SemiBold();
                    if (!string.IsNullOrWhiteSpace(n.DateLine))
                        col.Item().Text(n.DateLine).FontSize(9).FontColor(Colors.Grey.Medium);
                    col.Item().PaddingTop(12).Text(n.Body ?? "").LineHeight(1.35f);
                });

                page.Footer().AlignRight().Text(t =>
                {
                    t.CurrentPageNumber();
                    t.Span(" / ");
                    t.TotalPages();
                });
            });
        }).GeneratePdf();
}
