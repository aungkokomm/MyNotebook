-- =====================================================================
--  Migration 003 — hybrid search: add a TRIGRAM index alongside the
--  unicode61 NotesFTS. Trigram matches any 3+ character substring in
--  ANY script, ignoring word boundaries — essential for Myanmar text
--  (written without spaces), partial words, and OCR fragments.
--  Both indexes are kept in sync by the recreated triggers below.
-- =====================================================================
CREATE VIRTUAL TABLE IF NOT EXISTS NotesTrigram USING fts5(
    title, body, ocr, tags,
    tokenize = 'trigram'
);

-- Backfill from existing (non-deleted) notes.
INSERT INTO NotesTrigram(rowid, title, body, ocr, tags)
SELECT n.id, n.title, n.body_plain,
    (SELECT COALESCE(group_concat(ocr_text, ' '), '') FROM Images WHERE note_id = n.id AND deleted = 0),
    (SELECT COALESCE(group_concat(t.name, ' '), '') FROM NoteTags nt JOIN Tags t ON t.id = nt.tag_id WHERE nt.note_id = n.id)
FROM Notes n WHERE n.deleted = 0;

-- Recreate every maintenance trigger so it writes to BOTH indexes.
DROP TRIGGER IF EXISTS trg_notes_ai;
DROP TRIGGER IF EXISTS trg_notes_au_index;
DROP TRIGGER IF EXISTS trg_notes_au_unindex;
DROP TRIGGER IF EXISTS trg_notes_ad;
DROP TRIGGER IF EXISTS trg_images_ai;
DROP TRIGGER IF EXISTS trg_images_au;
DROP TRIGGER IF EXISTS trg_images_ad;
DROP TRIGGER IF EXISTS trg_notetags_ai;
DROP TRIGGER IF EXISTS trg_notetags_ad;

CREATE TRIGGER trg_notes_ai AFTER INSERT ON Notes WHEN NEW.deleted = 0
BEGIN
    DELETE FROM NotesFTS WHERE rowid = NEW.id;
    DELETE FROM NotesTrigram WHERE rowid = NEW.id;
    INSERT INTO NotesFTS(rowid, title, body, ocr, tags) VALUES (NEW.id, NEW.title, NEW.body_plain,
        (SELECT COALESCE(group_concat(ocr_text,' '),'') FROM Images WHERE note_id=NEW.id AND deleted=0),
        (SELECT COALESCE(group_concat(t.name,' '),'') FROM NoteTags nt JOIN Tags t ON t.id=nt.tag_id WHERE nt.note_id=NEW.id));
    INSERT INTO NotesTrigram(rowid, title, body, ocr, tags) VALUES (NEW.id, NEW.title, NEW.body_plain,
        (SELECT COALESCE(group_concat(ocr_text,' '),'') FROM Images WHERE note_id=NEW.id AND deleted=0),
        (SELECT COALESCE(group_concat(t.name,' '),'') FROM NoteTags nt JOIN Tags t ON t.id=nt.tag_id WHERE nt.note_id=NEW.id));
END;

CREATE TRIGGER trg_notes_au_index AFTER UPDATE ON Notes WHEN NEW.deleted = 0
BEGIN
    DELETE FROM NotesFTS WHERE rowid = NEW.id;
    DELETE FROM NotesTrigram WHERE rowid = NEW.id;
    INSERT INTO NotesFTS(rowid, title, body, ocr, tags) VALUES (NEW.id, NEW.title, NEW.body_plain,
        (SELECT COALESCE(group_concat(ocr_text,' '),'') FROM Images WHERE note_id=NEW.id AND deleted=0),
        (SELECT COALESCE(group_concat(t.name,' '),'') FROM NoteTags nt JOIN Tags t ON t.id=nt.tag_id WHERE nt.note_id=NEW.id));
    INSERT INTO NotesTrigram(rowid, title, body, ocr, tags) VALUES (NEW.id, NEW.title, NEW.body_plain,
        (SELECT COALESCE(group_concat(ocr_text,' '),'') FROM Images WHERE note_id=NEW.id AND deleted=0),
        (SELECT COALESCE(group_concat(t.name,' '),'') FROM NoteTags nt JOIN Tags t ON t.id=nt.tag_id WHERE nt.note_id=NEW.id));
END;

CREATE TRIGGER trg_notes_au_unindex AFTER UPDATE ON Notes WHEN NEW.deleted = 1
BEGIN
    DELETE FROM NotesFTS WHERE rowid = NEW.id;
    DELETE FROM NotesTrigram WHERE rowid = NEW.id;
END;

CREATE TRIGGER trg_notes_ad AFTER DELETE ON Notes
BEGIN
    DELETE FROM NotesFTS WHERE rowid = OLD.id;
    DELETE FROM NotesTrigram WHERE rowid = OLD.id;
END;

CREATE TRIGGER trg_images_ai AFTER INSERT ON Images
BEGIN
    DELETE FROM NotesFTS WHERE rowid = NEW.note_id;
    DELETE FROM NotesTrigram WHERE rowid = NEW.note_id;
    INSERT INTO NotesFTS(rowid, title, body, ocr, tags) SELECT n.id, n.title, n.body_plain,
        (SELECT COALESCE(group_concat(ocr_text,' '),'') FROM Images WHERE note_id=n.id AND deleted=0),
        (SELECT COALESCE(group_concat(t.name,' '),'') FROM NoteTags nt JOIN Tags t ON t.id=nt.tag_id WHERE nt.note_id=n.id)
        FROM Notes n WHERE n.id=NEW.note_id AND n.deleted=0;
    INSERT INTO NotesTrigram(rowid, title, body, ocr, tags) SELECT n.id, n.title, n.body_plain,
        (SELECT COALESCE(group_concat(ocr_text,' '),'') FROM Images WHERE note_id=n.id AND deleted=0),
        (SELECT COALESCE(group_concat(t.name,' '),'') FROM NoteTags nt JOIN Tags t ON t.id=nt.tag_id WHERE nt.note_id=n.id)
        FROM Notes n WHERE n.id=NEW.note_id AND n.deleted=0;
END;

CREATE TRIGGER trg_images_au AFTER UPDATE ON Images
BEGIN
    DELETE FROM NotesFTS WHERE rowid = NEW.note_id;
    DELETE FROM NotesTrigram WHERE rowid = NEW.note_id;
    INSERT INTO NotesFTS(rowid, title, body, ocr, tags) SELECT n.id, n.title, n.body_plain,
        (SELECT COALESCE(group_concat(ocr_text,' '),'') FROM Images WHERE note_id=n.id AND deleted=0),
        (SELECT COALESCE(group_concat(t.name,' '),'') FROM NoteTags nt JOIN Tags t ON t.id=nt.tag_id WHERE nt.note_id=n.id)
        FROM Notes n WHERE n.id=NEW.note_id AND n.deleted=0;
    INSERT INTO NotesTrigram(rowid, title, body, ocr, tags) SELECT n.id, n.title, n.body_plain,
        (SELECT COALESCE(group_concat(ocr_text,' '),'') FROM Images WHERE note_id=n.id AND deleted=0),
        (SELECT COALESCE(group_concat(t.name,' '),'') FROM NoteTags nt JOIN Tags t ON t.id=nt.tag_id WHERE nt.note_id=n.id)
        FROM Notes n WHERE n.id=NEW.note_id AND n.deleted=0;
END;

CREATE TRIGGER trg_images_ad AFTER DELETE ON Images
BEGIN
    DELETE FROM NotesFTS WHERE rowid = OLD.note_id;
    DELETE FROM NotesTrigram WHERE rowid = OLD.note_id;
    INSERT INTO NotesFTS(rowid, title, body, ocr, tags) SELECT n.id, n.title, n.body_plain,
        (SELECT COALESCE(group_concat(ocr_text,' '),'') FROM Images WHERE note_id=n.id AND deleted=0),
        (SELECT COALESCE(group_concat(t.name,' '),'') FROM NoteTags nt JOIN Tags t ON t.id=nt.tag_id WHERE nt.note_id=n.id)
        FROM Notes n WHERE n.id=OLD.note_id AND n.deleted=0;
    INSERT INTO NotesTrigram(rowid, title, body, ocr, tags) SELECT n.id, n.title, n.body_plain,
        (SELECT COALESCE(group_concat(ocr_text,' '),'') FROM Images WHERE note_id=n.id AND deleted=0),
        (SELECT COALESCE(group_concat(t.name,' '),'') FROM NoteTags nt JOIN Tags t ON t.id=nt.tag_id WHERE nt.note_id=n.id)
        FROM Notes n WHERE n.id=OLD.note_id AND n.deleted=0;
END;

CREATE TRIGGER trg_notetags_ai AFTER INSERT ON NoteTags
BEGIN
    DELETE FROM NotesFTS WHERE rowid = NEW.note_id;
    DELETE FROM NotesTrigram WHERE rowid = NEW.note_id;
    INSERT INTO NotesFTS(rowid, title, body, ocr, tags) SELECT n.id, n.title, n.body_plain,
        (SELECT COALESCE(group_concat(ocr_text,' '),'') FROM Images WHERE note_id=n.id AND deleted=0),
        (SELECT COALESCE(group_concat(t.name,' '),'') FROM NoteTags nt JOIN Tags t ON t.id=nt.tag_id WHERE nt.note_id=n.id)
        FROM Notes n WHERE n.id=NEW.note_id AND n.deleted=0;
    INSERT INTO NotesTrigram(rowid, title, body, ocr, tags) SELECT n.id, n.title, n.body_plain,
        (SELECT COALESCE(group_concat(ocr_text,' '),'') FROM Images WHERE note_id=n.id AND deleted=0),
        (SELECT COALESCE(group_concat(t.name,' '),'') FROM NoteTags nt JOIN Tags t ON t.id=nt.tag_id WHERE nt.note_id=n.id)
        FROM Notes n WHERE n.id=NEW.note_id AND n.deleted=0;
END;

CREATE TRIGGER trg_notetags_ad AFTER DELETE ON NoteTags
BEGIN
    DELETE FROM NotesFTS WHERE rowid = OLD.note_id;
    DELETE FROM NotesTrigram WHERE rowid = OLD.note_id;
    INSERT INTO NotesFTS(rowid, title, body, ocr, tags) SELECT n.id, n.title, n.body_plain,
        (SELECT COALESCE(group_concat(ocr_text,' '),'') FROM Images WHERE note_id=n.id AND deleted=0),
        (SELECT COALESCE(group_concat(t.name,' '),'') FROM NoteTags nt JOIN Tags t ON t.id=nt.tag_id WHERE nt.note_id=n.id)
        FROM Notes n WHERE n.id=OLD.note_id AND n.deleted=0;
    INSERT INTO NotesTrigram(rowid, title, body, ocr, tags) SELECT n.id, n.title, n.body_plain,
        (SELECT COALESCE(group_concat(ocr_text,' '),'') FROM Images WHERE note_id=n.id AND deleted=0),
        (SELECT COALESCE(group_concat(t.name,' '),'') FROM NoteTags nt JOIN Tags t ON t.id=nt.tag_id WHERE nt.note_id=n.id)
        FROM Notes n WHERE n.id=OLD.note_id AND n.deleted=0;
END;

INSERT OR IGNORE INTO SchemaMigrations(version, applied_at, description)
VALUES (3, CAST(strftime('%s','now') AS INTEGER) * 1000, 'hybrid trigram search index');

PRAGMA user_version = 3;
