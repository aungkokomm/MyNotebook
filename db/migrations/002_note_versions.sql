-- =====================================================================
--  Migration 002 — per-note version history (auto-snapshots).
--  Snapshots of a note's title + body are stored as the user edits, so
--  an earlier draft can be browsed and restored. Pruned to the newest
--  ~50 per note by the app. Cascades away when a note is hard-deleted.
-- =====================================================================
CREATE TABLE IF NOT EXISTS NoteVersions (
    id         INTEGER PRIMARY KEY,
    note_id    INTEGER NOT NULL,
    title      TEXT    NOT NULL DEFAULT '',
    body_rtf   TEXT    NOT NULL DEFAULT '',
    body_plain TEXT    NOT NULL DEFAULT '',
    created_at INTEGER NOT NULL,
    FOREIGN KEY (note_id) REFERENCES Notes(id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS ix_noteversions ON NoteVersions(note_id, created_at);

INSERT OR IGNORE INTO SchemaMigrations(version, applied_at, description)
VALUES (2, CAST(strftime('%s','now') AS INTEGER) * 1000, 'note version history');

PRAGMA user_version = 2;
