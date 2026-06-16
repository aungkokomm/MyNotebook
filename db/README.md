# My Notebook — Database (deliverable C)

Local-first SQLite schema with FTS5 full-text search, OCR-searchable images,
folders, tags, pinning, and Smart Folders (saved searches).

## Files

| Path | Purpose |
|---|---|
| `migrations/001_initial_schema.sql` | Full schema, FTS5 table, and CRUD triggers. Idempotent (`IF NOT EXISTS`). |
| `seed/build_seed_db.py` | Generates `notebook-seed.db` + real PNG files, then self-verifies. No third-party deps. |
| `notebook-seed.db` | Sample DB: 10 notes (incl. 2 screenshot threads) + 10 images. *(generated)* |
| `../data/attachments/screenshots/{noteId}/*.png` | Sample image files, paths match `Images.rel_path`. *(generated)* |

## Regenerate

```powershell
cd MyNotebook/db/seed
python build_seed_db.py      # rebuilds DB + PNGs, prints verification, exits non-zero on failure
```

## Schema at a glance

```
Folders ──< Notes ──< Images          NotesFTS (FTS5, standalone, rowid = Notes.id)
              │  └──< NoteTags >── Tags   columns: title, body, ocr, tags
              │                            tokenizer: unicode61 remove_diacritics 2
SavedSearches (Smart Folders)             prefix: '2 3'  (as-you-type)
SchemaMigrations (version bookkeeping)
```

### Conventions
- **Timestamps**: INTEGER Unix epoch **milliseconds**, UTC.
- **Sync-ready**: every content row has `guid`, `created_at`, `updated_at`, `deleted`.
- **Soft-delete**: `deleted = 1` is the default delete path; PNGs stay on disk until a separate "empty trash" purge. Soft-deleted notes are removed from `NotesFTS` by triggers.
- **note_type**: `'note'` (RichEditBox doc) or `'thread'` (screenshot thread).
- **RTF vs plain**: `body_rtf` is the formatting source of truth; `body_plain` is derived on save and is the only thing fed to FTS.

### How search stays in sync
`NotesFTS` is a standalone FTS5 table (not external-content) so one row can
aggregate text from **three** source tables. Triggers on `Notes`, `Images`, and
`NoteTags` re-aggregate and re-index the affected note on every INSERT/UPDATE/DELETE,
so **text inside screenshots (OCR) is searchable alongside note bodies and tag names**.

## Verified behaviours (asserted by the build script)

- ✅ `PRAGMA integrity_check = ok`, 10 notes / 10 images, 9 FTS rows (1 soft-deleted excluded)
- ✅ **OCR-in-image search** — `401` (exists only in an image's OCR text) finds its thread
- ✅ **Ranked** results via `bm25(NotesFTS)`
- ✅ **Unicode diacritic folding** — `cafe` matches `café`
- ✅ **Prefix / as-you-type** — `log*` matches `login`
- ✅ **Soft-deleted notes excluded** from search
- ✅ Every `Images.rel_path` resolves to a file on disk

## Query cheatsheet

```sql
-- Forgiving, ranked search (use this for the title-bar box; append '*' to the
-- last token for as-you-type):
SELECT n.id, n.title, snippet(NotesFTS, 1, '[', ']', '…', 10) AS preview
FROM NotesFTS f JOIN Notes n ON n.id = f.rowid
WHERE NotesFTS MATCH :q
ORDER BY bm25(NotesFTS, 10.0, 5.0, 3.0, 2.0)   -- weight title>body>ocr>tags
LIMIT 50;

-- Notes list for a folder (pinned first, newest first), excluding deleted:
SELECT id, title, pinned, updated_at FROM Notes
WHERE deleted = 0 AND folder_id = :folderId
ORDER BY pinned DESC, sort_order, updated_at DESC;

-- A thread's image cards, top-to-bottom:
SELECT rel_path, caption, ocr_text, created_at FROM Images
WHERE note_id = :noteId AND deleted = 0
ORDER BY sort_order, created_at;
```
