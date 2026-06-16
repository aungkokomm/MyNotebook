using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace MyNotebook.App.Services;

/// <summary>
/// Writes a real .docx by converting the note's HTML body (with data-URI images)
/// into OpenXML via HtmlToOpenXml. Pure-managed, so it bundles in the self-contained app.
/// </summary>
public static class WordExporter
{
    public static async Task ExportAsync(string contentHtml, string path)
    {
        using var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var mainPart = doc.AddMainDocumentPart();
        mainPart.Document = new Document(new Body());

        var converter = new HtmlToOpenXml.HtmlConverter(mainPart);
        await converter.ParseHtml(contentHtml);   // parses + appends to the body, embeds images

        mainPart.Document.Save();
    }
}
