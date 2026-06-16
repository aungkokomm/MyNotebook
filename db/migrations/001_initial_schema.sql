-- =====================================================================
--  My Notebook (WinUI 3) — Migration 001: initial schema
--  SQLite 3.45+ with FTS5 (unicode61). Idempotent where practical.
--
--  Design notes:
--   * Every content row carries guid / created_at / updated_at / deleted
--     so the (stubbed) ISyncService can be built later WITHOUT a migration.
--   * Soft-delete (deleted=1) is the default delete path. PNG files on disk
--     are retained; a separate "empty trash" purge removes rows + files.
--   * Timestamps are INTEGER Unix epoch milliseconds (UTC).
--   * NotesFTS is a *standalone* FTS5 table keyed by rowid = Notes.id.
--     It aggregates title + body_plain + OCR text (from Images) + tag names,
--     so a single MATCH query searches note bodies AND text inside images.
--     Triggers keep it in sync on every CRUD path (incl. soft-delete).
-- =====================================================================

PRAGMA foreign_keys = ON;
PRAGMA journal_mode = WAL;

-- ---------------------------------------------------------------------
-- Migration bookkeeping
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS SchemaMigrations (
    version     INTEGER PRIMARY KEY,
    applied_at  INTEGER NOT NULL,
    description TEXT
);

-- ---------------------------------------------------------------------
-- Folders — OneNote-style hierarchy (self-referencing).
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS Folders (
    id          INTEGER PRIMARY KEY,
    guid        TEXT    NOT NULL UNIQUE,
    name        TEXT    NOT NULL,
    parent_id   INTEGER REFERENCES Folders(id) ON DELETE SET NULL,
    sort_order  INTEGER NOT NULL DEFAULT 0,
    created_at  INTEGER NOT NULL,
    updated_at  INTEGER NOT NULL,
    deleted     INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS ix_folders_parent ON Folders(parent_id);

-- ---------------------------------------------------------------------
-- Notes — note_type 'note' (RichEditBox doc) or 'thread' (screenshot thread).
--   body_rtf   : RichEditBox RTF, source of truth for formatting.
--   body_plain : derived plain text on save -> feeds FTS (never edited directly).
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS Notes (
    id          INTEGER PRIMARY KEY,
    guid        TEXT    NOT NULL UNIQUE,
    folder_id   INTEGER REFERENCES Folders(id) ON DELETE SET NULL,
    title       TEXT    NOT NULL DEFAULT '',
    body_rtf    TEXT    NOT NULL DEFAULT '',
    body_plain  TEXT    NOT NULL DEFAULT '',
    note_type   TEXT    NOT NULL DEFAULT 'note'
                        CHECK (note_type IN ('note','thread')),
    pinned      INTEGER NOT NULL DEFAULT 0,
    sort_order  INTEGER NOT NULL DEFAULT 0,
    created_at  INTEGER NOT NULL,
    updated_at  INTEGER NOT NULL,
    deleted     INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS ix_notes_folder  ON Notes(folder_id);
CREATE INDEX IF NOT EXISTS ix_notes_list    ON Notes(deleted, pinned DESC, sort_order, updated_at DESC);
CREATE INDEX IF NOT EXISTS ix_notes_updated ON Notes(updated_at);

-- ---------------------------------------------------------------------
-- Images — screenshot/attachment cards in a thread.
--   rel_path : path RELATIVE to the data root, e.g.
--              attachments/screenshots/{noteId}/{ts}_{shortguid}.png
--   ocr_text : OCR output (Windows.Media.Ocr) -> indexed into NotesFTS.
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS Images (
    id          INTEGER PRIMARY KEY,
    guid        TEXT    NOT NULL UNIQUE,
    note_id     INTEGER NOT NULL REFERENCES Notes(id) ON DELETE CASCADE,
    rel_path    TEXT    NOT NULL,
    width       INTEGER,
    height      INTEGER,
    ocr_text    TEXT    NOT NULL DEFAULT '',
    caption     TEXT    NOT NULL DEFAULT '',
    sort_order  INTEGER NOT NULL DEFAULT 0,
    created_at  INTEGER NOT NULL,
    updated_at  INTEGER NOT NULL,
    deleted     INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS ix_images_note ON Images(note_id, deleted, sort_order);

-- ---------------------------------------------------------------------
-- Tags + NoteTags (many-to-many)
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS Tags (
    id          INTEGER PRIMARY KEY,
    guid        TEXT    NOT NULL UNIQUE,
    name        TEXT    NOT NULL UNIQUE,
    color       TEXT    NOT NULL DEFAULT '',
    created_at  INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS NoteTags (
    note_id     INTEGER NOT NULL REFERENCES Notes(id) ON DELETE CASCADE,
    tag_id      INTEGER NOT NULL REFERENCES Tags(id)  ON DELETE CASCADE,
    PRIMARY KEY (note_id, tag_id)
);
CREATE INDEX IF NOT EXISTS ix_notetags_tag ON NoteTags(tag_id);

-- ---------------------------------------------------------------------
-- SavedSearches — Apple-Notes-style Smart Folders.
--   query : FTS5 MATCH expression (MVP). 'kind' lets us add structured
--           (tag/date) filters later without a migration.
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS SavedSearches (
    id          INTEGER PRIMARY KEY,
    guid        TEXT    NOT NULL UNIQUE,
    name        TEXT    NOT NULL,
    kind        TEXT    NOT NULL DEFAULT 'fts' CHECK (kind IN ('fts','tag','date')),
    query       TEXT    NOT NULL DEFAULT '',
    icon        TEXT    NOT NULL DEFAULT '',
    sort_order  INTEGER NOT NULL DEFAULT 0,
    created_at  INTEGER NOT NULL,
    updated_at  INTEGER NOT NULL
);

-- =====================================================================
--  Full-text search (FTS5)
--   * unicode61 + remove_diacritics 2  -> Unicode-aware, accent-folding.
--   * prefix '2 3'                      -> fast "as-you-type" prefix search.
--   * Standalone (not external-content) so we can aggregate text from
--     multiple source tables (Notes + Images + Tags) into one row.
-- =====================================================================
CREATE VIRTUAL TABLE IF NOT EXISTS NotesFTS USING fts5(
    title,
    body,
    ocr,
    tags,
    prefix = '2 3',
    tokenize = "unicode61 remove_diacritics 2"
);

-- ---------------------------------------------------------------------
-- Trigger helpers
--   SQLite has no stored procs, so the "reindex one note" body is repeated.
--   Each upsert = DELETE the rowid then INSERT the freshly-aggregated row.
--   Soft-deleted notes (deleted=1) are removed from the index.
-- ---------------------------------------------------------------------

-- ---- Notes -----------------------------------------------------------
CREATE TRIGGER IF NOT EXISTS trg_notes_ai AFTER INSERT ON Notes
WHEN NEW.deleted = 0
BEGIN
    DELETE FROM NotesFTS WHERE rowid = NEW.id;
    INSERT INTO NotesFTS(rowid, title, body, ocr, tags)
    VALUES (
        NEW.id, NEW.title, NEW.body_plain,
        (SELECT COALESCE(group_concat(ocr_text, ' '), '')
           FROM Images WHERE note_id = NEW.id AND deleted = 0),
        (SELECT COALESCE(group_concat(t.name, ' '), '')
           FROM NoteTags nt JOIN Tags t ON t.id = nt.tag_id
          WHERE nt.note_id = NEW.id)
    );
END;

CREATE TRIGGER IF NOT EXISTS trg_notes_au_index AFTER UPDATE ON Notes
WHEN NEW.deleted = 0
BEGIN
    DELETE FROM NotesFTS WHERE rowid = NEW.id;
    INSERT INTO NotesFTS(rowid, title, body, ocr, tags)
    VALUES (
        NEW.id, NEW.title, NEW.body_plain,
        (SELECT COALESCE(group_concat(ocr_text, ' '), '')
           FROM Images WHERE note_id = NEW.id AND deleted = 0),
        (SELECT COALESCE(group_concat(t.name, ' '), '')
           FROM NoteTags nt JOIN Tags t ON t.id = nt.tag_id
          WHERE nt.note_id = NEW.id)
    );
END;

CREATE TRIGGER IF NOT EXISTS trg_notes_au_unindex AFTER UPDATE ON Notes
WHEN NEW.deleted = 1
BEGIN
    DELETE FROM NotesFTS WHERE rowid = NEW.id;
END;

CREATE TRIGGER IF NOT EXISTS trg_notes_ad AFTER DELETE ON Notes
BEGIN
    DELETE FROM NotesFTS WHERE rowid = OLD.id;
END;

-- ---- Images (re-aggregate OCR for the parent note) -------------------
CREATE TRIGGER IF NOT EXISTS trg_images_ai AFTER INSERT ON Images
BEGIN
    DELETE FROM NotesFTS WHERE rowid = NEW.note_id;
    INSERT INTO NotesFTS(rowid, title, body, ocr, tags)
    SELECT n.id, n.title, n.body_plain,
        (SELECT COALESCE(group_concat(ocr_text, ' '), '')
           FROM Images WHERE note_id = n.id AND deleted = 0),
        (SELECT COALESCE(group_concat(t.name, ' '), '')
           FROM NoteTags nt JOIN Tags t ON t.id = nt.tag_id
          WHERE nt.note_id = n.id)
    FROM Notes n WHERE n.id = NEW.note_id AND n.deleted = 0;
END;

CREATE TRIGGER IF NOT EXISTS trg_images_au AFTER UPDATE ON Images
BEGIN
    DELETE FROM NotesFTS WHERE rowid = NEW.note_id;
    INSERT INTO NotesFTS(rowid, title, body, ocr, tags)
    SELECT n.id, n.title, n.body_plain,
        (SELECT COALESCE(group_concat(ocr_text, ' '), '')
           FROM Images WHERE note_id = n.id AND deleted = 0),
        (SELECT COALESCE(group_concat(t.name, ' '), '')
           FROM NoteTags nt JOIN Tags t ON t.id = nt.tag_id
          WHERE nt.note_id = n.id)
    FROM Notes n WHERE n.id = NEW.note_id AND n.deleted = 0;
END;

CREATE TRIGGER IF NOT EXISTS trg_images_ad AFTER DELETE ON Images
BEGIN
    DELETE FROM NotesFTS WHERE rowid = OLD.note_id;
    INSERT INTO NotesFTS(rowid, title, body, ocr, tags)
    SELECT n.id, n.title, n.body_plain,
        (SELECT COALESCE(group_concat(ocr_text, ' '), '')
           FROM Images WHERE note_id = n.id AND deleted = 0),
        (SELECT COALESCE(group_concat(t.name, ' '), '')
           FROM NoteTags nt JOIN Tags t ON t.id = nt.tag_id
          WHERE nt.note_id = n.id)
    FROM Notes n WHERE n.id = OLD.note_id AND n.deleted = 0;
END;

-- ---- NoteTags (re-aggregate tag names for the note) ------------------
CREATE TRIGGER IF NOT EXISTS trg_notetags_ai AFTER INSERT ON NoteTags
BEGIN
    DELETE FROM NotesFTS WHERE rowid = NEW.note_id;
    INSERT INTO NotesFTS(rowid, title, body, ocr, tags)
    SELECT n.id, n.title, n.body_plain,
        (SELECT COALESCE(group_concat(ocr_text, ' '), '')
           FROM Images WHERE note_id = n.id AND deleted = 0),
        (SELECT COALESCE(group_concat(t.name, ' '), '')
           FROM NoteTags nt JOIN Tags t ON t.id = nt.tag_id
          WHERE nt.note_id = n.id)
    FROM Notes n WHERE n.id = NEW.note_id AND n.deleted = 0;
END;

CREATE TRIGGER IF NOT EXISTS trg_notetags_ad AFTER DELETE ON NoteTags
BEGIN
    DELETE FROM NotesFTS WHERE rowid = OLD.note_id;
    INSERT INTO NotesFTS(rowid, title, body, ocr, tags)
    SELECT n.id, n.title, n.body_plain,
        (SELECT COALESCE(group_concat(ocr_text, ' '), '')
           FROM Images WHERE note_id = n.id AND deleted = 0),
        (SELECT COALESCE(group_concat(t.name, ' '), '')
           FROM NoteTags nt JOIN Tags t ON t.id = nt.tag_id
          WHERE nt.note_id = n.id)
    FROM Notes n WHERE n.id = OLD.note_id AND n.deleted = 0;
END;

-- ---------------------------------------------------------------------
INSERT OR IGNORE INTO SchemaMigrations(version, applied_at, description)
VALUES (1, CAST(strftime('%s','now') AS INTEGER) * 1000, 'initial schema');

PRAGMA user_version = 1;
