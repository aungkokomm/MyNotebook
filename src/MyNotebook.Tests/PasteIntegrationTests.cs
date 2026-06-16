using MyNotebook.Core.Models;
using MyNotebook.Core.Services;
using Xunit;

namespace MyNotebook.Tests;

/// <summary>
/// End-to-end of the screenshot-thread core path (no UI):
///   new thread → "paste" image (write PNG + Images row) → reopen → cards present.
/// Mirrors the Ctrl+T / Ctrl+V flow the WinUI layer will drive.
/// </summary>
public class PasteOpenSaveIntegrationTests
{
    // 1x1 PNG, base64 — stands in for a clipboard bitmap.
    private const string OnePxPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    [Fact]
    public void Paste_image_into_thread_persists_and_reopens()
    {
        var root = Path.Combine(Path.GetTempPath(), "MNB_" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new PathService(explicitRoot: root);
            var storage = new StorageService(paths);
            storage.Initialize();
            var notes = new NoteService(storage);

            // Ctrl+T: new screenshot thread.
            var thread = notes.CreateNote("Screenshots", NoteType.Thread);

            // Ctrl+V: save the bitmap to attachments and create the Images row.
            var rel = paths.NewScreenshotRelPath(thread.Id);
            var abs = paths.ToAbsolute(rel);
            File.WriteAllBytes(abs, Convert.FromBase64String(OnePxPngBase64));
            Assert.True(File.Exists(abs));                       // file saved

            var img = notes.AddImage(thread.Id, rel, 1, 1, ocrText: "invoice 9981");

            // Reopen the note in a brand-new service instance (fresh connection).
            var notes2 = new NoteService(new StorageService(paths));
            var reopened = notes2.GetNote(thread.Id)!;
            Assert.Equal(NoteType.Thread, reopened.Type);

            var cards = notes2.ListImages(thread.Id);
            Assert.Single(cards);
            Assert.Equal(img.Id, cards[0].Id);
            Assert.True(File.Exists(paths.ToAbsolute(cards[0].RelPath)));   // card resolves to a file

            // OCR text from the pasted image is searchable.
            Assert.Contains(notes2.Search("9981"), h => h.NoteId == thread.Id);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void Multiple_pastes_keep_card_order_and_unique_paths()
    {
        var root = Path.Combine(Path.GetTempPath(), "MNB_" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new PathService(explicitRoot: root);
            var storage = new StorageService(paths);
            storage.Initialize();
            var notes = new NoteService(storage);
            var thread = notes.CreateNote("Burst", NoteType.Thread);

            var bytes = Convert.FromBase64String(OnePxPngBase64);
            for (int i = 0; i < 3; i++)
            {
                var rel = paths.NewScreenshotRelPath(thread.Id);
                File.WriteAllBytes(paths.ToAbsolute(rel), bytes);
                notes.AddImage(thread.Id, rel, 1, 1, caption: $"shot {i}");
            }

            var cards = notes.ListImages(thread.Id);
            Assert.Equal(3, cards.Count);
            Assert.Equal(new[] { "shot 0", "shot 1", "shot 2" }, cards.Select(c => c.Caption));
            Assert.Equal(3, cards.Select(c => c.RelPath).Distinct().Count());   // no path collisions
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
