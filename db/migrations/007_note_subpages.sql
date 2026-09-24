-- =====================================================================
--  My Notebook (WinUI 3) — Migration 007: note subpages (one level)
--
--  A note may be a subpage of another note (OneNote-style), one level deep.
--  A subpage shares its parent's folder and notebook. ON DELETE SET NULL so
--  that permanently purging a parent simply promotes any leftover child to a
--  top-level page rather than cascading a hard delete.
-- =====================================================================

ALTER TABLE Notes ADD COLUMN parent_note_id INTEGER REFERENCES Notes(id) ON DELETE SET NULL;

CREATE INDEX IF NOT EXISTS ix_notes_parent ON Notes(parent_note_id);

INSERT OR IGNORE INTO SchemaMigrations(version, applied_at, description)
VALUES (7, CAST(strftime('%s','now') AS INTEGER) * 1000, 'note subpages');

PRAGMA user_version = 7;
