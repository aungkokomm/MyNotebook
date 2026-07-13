-- =====================================================================
--  My Notebook (WinUI 3) — Migration 006: folder (section) color
--
--  OneNote-style per-folder accent color. Notebooks already carry a color
--  column (migration 005); this adds the same to folders. Empty = follow
--  the theme (no accent).
-- =====================================================================

ALTER TABLE Folders ADD COLUMN color TEXT NOT NULL DEFAULT '';

INSERT OR IGNORE INTO SchemaMigrations(version, applied_at, description)
VALUES (6, CAST(strftime('%s','now') AS INTEGER) * 1000, 'folder color');

PRAGMA user_version = 6;
