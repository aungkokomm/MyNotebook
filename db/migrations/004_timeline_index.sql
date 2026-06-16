-- =====================================================================
--  Migration 004 — indexes for the notebook-wide Timeline view.
--  The Timeline lists every non-deleted note ordered by edit or creation
--  time. These partial-friendly composite indexes let SQLite satisfy that
--  ordered scan directly instead of sorting the whole table each open.
-- =====================================================================
CREATE INDEX IF NOT EXISTS idx_notes_updated ON Notes(deleted, updated_at DESC);
CREATE INDEX IF NOT EXISTS idx_notes_created ON Notes(deleted, created_at DESC);

INSERT OR IGNORE INTO SchemaMigrations(version, applied_at, description)
VALUES (4, CAST(strftime('%s','now') AS INTEGER) * 1000, 'timeline ordering indexes');

PRAGMA user_version = 4;
