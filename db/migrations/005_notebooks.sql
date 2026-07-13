-- =====================================================================
--  My Notebook (WinUI 3) — Migration 005: notebooks
--
--  Adds a top-level "notebook" above folders and notes (OneNote model:
--  Notebook > Section(folder) > Page(note)). Existing folders and notes
--  are moved into a single seeded default notebook so nothing is orphaned.
--
--  Search stays global (FTS is untouched) — a note carries a notebook_id
--  but the index does not, so cross-notebook search keeps working.
-- =====================================================================

-- ---------------------------------------------------------------------
-- Notebooks — the top of the hierarchy.
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS Notebooks (
    id          INTEGER PRIMARY KEY,
    guid        TEXT    NOT NULL UNIQUE,
    name        TEXT    NOT NULL,
    color       TEXT    NOT NULL DEFAULT '',
    sort_order  INTEGER NOT NULL DEFAULT 0,
    created_at  INTEGER NOT NULL,
    updated_at  INTEGER NOT NULL,
    deleted     INTEGER NOT NULL DEFAULT 0
);

-- Add the notebook foreign key to Folders and Notes (nullable; backfilled below).
ALTER TABLE Folders ADD COLUMN notebook_id INTEGER REFERENCES Notebooks(id) ON DELETE CASCADE;
ALTER TABLE Notes   ADD COLUMN notebook_id INTEGER REFERENCES Notebooks(id) ON DELETE CASCADE;

-- Seed the default notebook that inherits everything created before this migration.
INSERT INTO Notebooks(guid, name, color, sort_order, created_at, updated_at, deleted)
VALUES ('nb-default-00000000', 'My Notebook', '', 0,
        CAST(strftime('%s','now') AS INTEGER) * 1000,
        CAST(strftime('%s','now') AS INTEGER) * 1000, 0);

UPDATE Folders SET notebook_id = (SELECT id FROM Notebooks ORDER BY id LIMIT 1) WHERE notebook_id IS NULL;
UPDATE Notes   SET notebook_id = (SELECT id FROM Notebooks ORDER BY id LIMIT 1) WHERE notebook_id IS NULL;

CREATE INDEX IF NOT EXISTS ix_folders_notebook ON Folders(notebook_id);
CREATE INDEX IF NOT EXISTS ix_notes_notebook   ON Notes(notebook_id);

-- ---------------------------------------------------------------------
INSERT OR IGNORE INTO SchemaMigrations(version, applied_at, description)
VALUES (5, CAST(strftime('%s','now') AS INTEGER) * 1000, 'notebooks');

PRAGMA user_version = 5;
