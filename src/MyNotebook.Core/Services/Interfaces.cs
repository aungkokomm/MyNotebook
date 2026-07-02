using MyNotebook.Core.Models;

namespace MyNotebook.Core.Services;

/// <summary>
/// Resolves where the portable data lives. Tries the folder next to the exe;
/// if it is not writable (Program Files, read-only USB), falls back to
/// %LOCALAPPDATA%\MyNotebook. Everyone asks this service rather than guessing.
/// </summary>
public interface IPathService
{
    /// <summary>Absolute path to the data root (contains the DB and attachments).</summary>
    string DataRoot { get; }

    /// <summary>True when DataRoot is the portable location next to the exe.</summary>
    bool IsPortable { get; }

    /// <summary>Absolute path to the SQLite database file.</summary>
    string DbPath { get; }

    /// <summary>Absolute directory for a thread's screenshots; created on demand.</summary>
    string EnsureScreenshotDir(long noteId);

    /// <summary>A fresh, collision-proof relative PNG path for a pasted screenshot.</summary>
    string NewScreenshotRelPath(long noteId);

    /// <summary>Resolve a stored relative path to an absolute one.</summary>
    string ToAbsolute(string relPath);
}

/// <summary>Owns the SQLite connection lifecycle and schema migration.</summary>
public interface IStorageService
{
    /// <summary>Create the DB if missing and apply pending migrations (idempotent).</summary>
    void Initialize();

    /// <summary>Save a timestamped snapshot of the DB to a portable Backups\ folder (keeps the last few).</summary>
    void BackupOnLaunch();

    /// <summary>
    /// Write a clean, self-contained, restorable backup to <paramref name="destZipPath"/>: a
    /// consistent DB snapshot (WAL checkpointed in), the attachments folder, settings.json, and a
    /// manifest. Excludes the WebView2 cache and the Backups folder itself.
    /// </summary>
    void CreateBackupZip(string destZipPath);

    /// <summary>Peek at a backup (.zip or .db) without applying it. Returns whether it is a valid
    /// My Notebook backup and a human-readable description for a confirmation prompt.</summary>
    (bool ok, string message) InspectBackup(string path);

    /// <summary>Stage a backup file (.zip or .db) to be applied on the next launch. Does NOT touch
    /// the live data yet — the swap happens in <see cref="ApplyPendingRestoreIfAny"/> before the DB opens.</summary>
    void StageRestore(string sourcePath);

    /// <summary>If a restore was staged, swap it into place. MUST be called at startup BEFORE the DB
    /// is opened or migrated. Safe: leaves the staged file in place if the swap fails.</summary>
    void ApplyPendingRestoreIfAny();

    /// <summary>Release pooled SQLite handles so the DB file can be replaced (used around restore).</summary>
    void ReleaseConnections();

    /// <summary>Open a new connection with foreign keys enabled.</summary>
    Microsoft.Data.Sqlite.SqliteConnection OpenConnection();

    /// <summary>Current schema version (PRAGMA user_version).</summary>
    int SchemaVersion { get; }
}

/// <summary>Notes, folders, images, tags, and search. The app's main data API.</summary>
public interface INoteService
{
    // Folders
    Folder CreateFolder(string name, long? parentId = null);
    IReadOnlyList<Folder> ListFolders();
    void RenameFolder(long id, string name);
    /// <summary>Soft-delete a folder, its subfolders, and all their notes.</summary>
    void DeleteFolder(long id);

    // Move a note into a folder (null = Unfiled).
    void MoveNoteToFolder(long noteId, long? folderId);

    // Notes
    Note CreateNote(string title, NoteType type = NoteType.Note, long? folderId = null);
    Note? GetNote(long id);
    void UpdateNote(Note note);
    void SetPinned(long noteId, bool pinned);
    void SoftDeleteNote(long noteId);
    IReadOnlyList<Note> ListNotes(long? folderId = null, bool includeDeleted = false);

    /// <summary>All non-deleted notes ordered for the Timeline by the given axis, newest first.</summary>
    IReadOnlyList<Note> ListTimeline(TimelineAxis axis = TimelineAxis.Modified);

    // Images (screenshot thread cards)
    ImageItem AddImage(long noteId, string relPath, int width, int height,
                       string ocrText = "", string caption = "");
    void UpdateImageOcr(long imageId, string ocrText);
    IReadOnlyList<ImageItem> ListImages(long noteId);
    /// <summary>Permanently delete one image. Returns its rel_path for file cleanup.</summary>
    string DeleteImage(long imageId);
    /// <summary>Persist a new order for a thread's images (ids in the desired order).</summary>
    void ReorderImages(long noteId, IReadOnlyList<long> orderedImageIds);

    // Version history (auto-snapshots)
    /// <summary>Save a snapshot of a note's title + body; prunes to the newest ~50.</summary>
    void SaveNoteVersion(long noteId, string title, string bodyRtf, string bodyPlain);
    /// <summary>A note's snapshots, newest first.</summary>
    IReadOnlyList<NoteVersion> ListNoteVersions(long noteId);
    NoteVersion? GetNoteVersion(long versionId);

    // Tags
    Tag EnsureTag(string name, string color = "");
    void AddTagToNote(long noteId, long tagId);
    void RemoveTagFromNote(long noteId, long tagId);
    IReadOnlyList<Tag> ListTags();
    IReadOnlyList<Tag> ListTagsForNote(long noteId);
    IReadOnlyList<Note> ListNotesWithTag(long tagId);

    // Smart folders
    IReadOnlyList<SavedSearch> ListSavedSearches();

    // Search — forgiving, ranked, Unicode/diacritic-folded, prefix as-you-type.
    IReadOnlyList<SearchHit> Search(string query, int limit = 50);

    // Maintenance (Settings → Data / Search)
    /// <summary>Counts for the Settings page.</summary>
    NotebookStats GetStats();
    /// <summary>Permanently remove soft-deleted notes and their image rows.
    /// Returns the rel_paths of image files the caller should delete from disk.</summary>
    IReadOnlyList<string> EmptyTrash();
    /// <summary>Soft-deleted notes (the trash), newest first.</summary>
    IReadOnlyList<Note> ListTrash();
    /// <summary>Bring a note back from the trash.</summary>
    void RestoreNote(long noteId);
    /// <summary>Permanently delete one note. Returns its image rel_paths for file cleanup.</summary>
    IReadOnlyList<string> DeleteNoteForever(long noteId);
    /// <summary>Drop and rebuild the FTS index from the base tables.</summary>
    void RebuildSearchIndex();
    /// <summary>All non-deleted images (used to re-run OCR across the notebook).</summary>
    IReadOnlyList<ImageItem> AllImages();
}

/// <summary>
/// OCR abstraction. The Windows implementation (Windows.Media.Ocr) lives in the
/// App project so Core stays portable and unit-testable.
/// </summary>
public interface IOcrService
{
    /// <summary>True when an OCR engine is available (a language pack is installed).</summary>
    bool IsAvailable { get; }

    /// <summary>Display name of the active OCR language, or null when unavailable.</summary>
    string? EngineLanguage { get; }

    /// <summary>Recognize text from a PNG/image file. Returns "" if unavailable.</summary>
    Task<string> RecognizeAsync(string imagePath, CancellationToken ct = default);
}

/// <summary>Sync is out of scope for MVP; this stub keeps the seam open.</summary>
public interface ISyncService
{
    bool IsEnabled { get; }
    Task SyncNowAsync(CancellationToken ct = default);
}
