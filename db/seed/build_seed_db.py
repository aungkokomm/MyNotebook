#!/usr/bin/env python3
"""
Build a sample My Notebook database: 10 notes + 10 screenshot images.

- Applies migrations/001_initial_schema.sql
- Generates real (tiny, valid) PNG files under data/attachments/screenshots/
- Inserts seed rows via normal INSERTs so the FTS triggers populate NotesFTS
- Verifies: integrity, FTS row count, an OCR-in-image search, ranked search,
  Unicode/diacritic-folded search, and soft-delete un-indexing.

No third-party deps (PNG is written by hand). Run:
    python build_seed_db.py
Outputs:
    ../notebook-seed.db
    ../../data/attachments/screenshots/{noteId}/*.png   (relative to app data root)
"""
import os
import shutil
import sqlite3
import struct
import sys
import uuid
import zlib
from datetime import datetime, timezone, timedelta

# Windows consoles default to cp1252; force UTF-8 so Unicode prints cleanly.
try:
    sys.stdout.reconfigure(encoding="utf-8")
except Exception:
    pass

HERE = os.path.dirname(os.path.abspath(__file__))
DB_PATH = os.path.normpath(os.path.join(HERE, "..", "notebook-seed.db"))
MIGRATION = os.path.normpath(os.path.join(HERE, "..", "migrations", "001_initial_schema.sql"))
# Data root mirrors the app's portable "./Data" folder so rel_path resolves.
DATA_ROOT = os.path.normpath(os.path.join(HERE, "..", "..", "data"))


def new_guid():
    return str(uuid.uuid4())


def short_guid():
    return uuid.uuid4().hex[:8]


def ms(dt):
    return int(dt.replace(tzinfo=timezone.utc).timestamp() * 1000)


def make_png(path, w, h, rgb):
    """Write a minimal valid solid-color PNG (no external libs)."""
    def chunk(tag, data):
        return (struct.pack(">I", len(data)) + tag + data
                + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF))
    sig = b"\x89PNG\r\n\x1a\n"
    ihdr = struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0)  # 8-bit RGB
    row = b"\x00" + bytes(rgb) * w
    raw = row * h
    idat = zlib.compress(raw, 9)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as f:
        f.write(sig + chunk(b"IHDR", ihdr) + chunk(b"IDAT", idat) + chunk(b"IEND", b""))


def main():
    if os.path.exists(DB_PATH):
        os.remove(DB_PATH)
    # Clean previously-generated screenshots so reruns don't leave orphan PNGs
    # (each run's filenames carry a fresh short-guid).
    shots = os.path.join(DATA_ROOT, "attachments", "screenshots")
    if os.path.isdir(shots):
        shutil.rmtree(shots)

    con = sqlite3.connect(DB_PATH)
    con.execute("PRAGMA foreign_keys = ON")
    with open(MIGRATION, "r", encoding="utf-8") as f:
        con.executescript(f.read())

    base = datetime(2026, 6, 1, 9, 0, 0)

    # ---- Folders ----------------------------------------------------
    folders = [
        ("Work",      None),
        ("Personal",  None),
        ("Projects",  None),
        ("Receipts",  2),   # child of Personal (id=2)
    ]
    folder_ids = {}
    for i, (name, parent_idx) in enumerate(folders, start=1):
        t = ms(base + timedelta(days=i))
        con.execute(
            "INSERT INTO Folders(id,guid,name,parent_id,sort_order,created_at,updated_at) "
            "VALUES(?,?,?,?,?,?,?)",
            (i, new_guid(), name, parent_idx, i, t, t))
        folder_ids[name] = i

    # ---- Tags -------------------------------------------------------
    tags = [("idea", "#FFB900"), ("todo", "#E81123"), ("meeting", "#0078D4"),
            ("résumé", "#107C10"), ("café", "#5C2D91")]  # diacritics on purpose
    tag_ids = {}
    for i, (name, color) in enumerate(tags, start=1):
        con.execute("INSERT INTO Tags(id,guid,name,color,created_at) VALUES(?,?,?,?,?)",
                    (i, new_guid(), name, color, ms(base)))
        tag_ids[name] = i

    # ---- Notes (8 docs + 2 screenshot threads = 10) -----------------
    # (title, body_plain, folder, type, pinned, [tag names])
    notes = [
        ("Welcome to My Notebook",
         "This is your local-first notebook. Everything is stored next to the app in a portable Data folder. Search is instant and Unicode-aware.",
         "Work", "note", 1, ["idea"]),
        ("Q3 Planning Meeting",
         "Discussed roadmap for the screenshot thread feature and OCR search. Action items assigned to the team.",
         "Work", "note", 1, ["meeting", "todo"]),
        ("Grocery list",
         "Milk, eggs, bread, café beans, dark chocolate. Pick up résumé prints on the way home.",
         "Personal", "note", 0, ["todo", "café", "résumé"]),
        ("Book notes — Deep Work",
         "Concentration is a skill. Schedule blocks of focused, distraction-free time. Shallow work expands to fill available hours.",
         "Personal", "note", 0, ["idea"]),
        ("WinUI 3 tips",
         "Use Mica backdrop on Windows 11. RichEditBox stores RTF; extract plain text on save for full-text search.",
         "Projects", "note", 0, ["idea"]),
        ("Café reservation",
         "Table for four at the corner café, Saturday 7pm. Ask about the résumé workshop afterwards.",
         "Personal", "note", 0, ["café"]),
        ("Recipe — Pad Thai",
         "Rice noodles, tamarind, fish sauce, peanuts, lime. Soak noodles, stir-fry quickly over high heat.",
         "Personal", "note", 0, []),
        ("Archived idea (deleted)",
         "An old discarded idea that should NOT appear in search results because it is soft-deleted.",
         "Work", "note", 0, ["idea"]),
        # --- two screenshot threads ---
        ("Bug screenshots — login flow",
         "Collected screenshots of the failing login flow for triage.",
         "Projects", "thread", 0, ["todo"]),
        ("Receipts — June",
         "Scanned receipts captured via paste. OCR makes the amounts searchable.",
         "Receipts", "thread", 0, []),
    ]

    note_ids = {}
    for i, (title, body, folder, ntype, pinned, tnames) in enumerate(notes, start=1):
        t = ms(base + timedelta(hours=i))
        rtf = ("{\\rtf1\\ansi " + body.replace("\\", "\\\\").replace("{", "\\{").replace("}", "\\}") + "}")
        deleted = 1 if title.startswith("Archived") else 0
        con.execute(
            "INSERT INTO Notes(id,guid,folder_id,title,body_rtf,body_plain,note_type,"
            "pinned,sort_order,created_at,updated_at,deleted) VALUES(?,?,?,?,?,?,?,?,?,?,?,?)",
            (i, new_guid(), folder_ids[folder], title, rtf, body, ntype,
             pinned, i, t, t, deleted))
        note_ids[title] = i
        for tn in tnames:
            con.execute("INSERT INTO NoteTags(note_id,tag_id) VALUES(?,?)", (i, tag_ids[tn]))

    # ---- Images: 10 screenshots across the two thread notes ---------
    thread1 = note_ids["Bug screenshots — login flow"]
    thread2 = note_ids["Receipts — June"]
    # (note_id, w, h, rgb, ocr_text, caption)
    images = [
        (thread1, 320, 240, (220, 53, 69),  "ERROR 500 Internal Server Error at /api/login", "Login 500 error"),
        (thread1, 320, 240, (255, 193, 7),  "Warning: token expired, redirecting to sign-in", "Token expired"),
        (thread1, 320, 240, (0, 120, 212),  "Stack trace NullReferenceException AuthService.cs line 42", "Null ref in AuthService"),
        (thread1, 400, 300, (40, 167, 69),  "Login succeeded after retry — session id 8f3a", "Success after retry"),
        (thread1, 320, 240, (108, 117, 125),"Network tab shows 401 Unauthorized on first call", "401 unauthorized"),
        (thread2, 360, 480, (33, 37, 41),   "Café Luna receipt total $42.50 tax included", "Café Luna $42.50"),
        (thread2, 360, 480, (52, 58, 64),   "Office Depot résumé prints $12.99 thank you", "Office Depot $12.99"),
        (thread2, 360, 480, (73, 80, 87),   "Grocery store total $87.65 paid by card", "Grocery $87.65"),
        (thread2, 360, 480, (90, 98, 104),  "Taxi fare receipt $23.10 tip included", "Taxi $23.10"),
        (thread2, 360, 480, (108, 117, 125),"Hardware store invoice $156.00 net 30", "Hardware $156.00"),
    ]
    for k, (nid, w, h, rgb, ocr, caption) in enumerate(images, start=1):
        ts = (base + timedelta(hours=nid, minutes=k)).strftime("%Y%m%d_%H%M%S")
        fname = f"{ts}_{short_guid()}.png"
        rel = f"attachments/screenshots/{nid}/{fname}"
        abs_path = os.path.join(DATA_ROOT, rel.replace("/", os.sep))
        make_png(abs_path, w, h, rgb)
        t = ms(base + timedelta(hours=nid, minutes=k))
        con.execute(
            "INSERT INTO Images(id,guid,note_id,rel_path,width,height,ocr_text,caption,"
            "sort_order,created_at,updated_at) VALUES(?,?,?,?,?,?,?,?,?,?,?)",
            (k, new_guid(), nid, rel, w, h, ocr, caption, k, t, t))

    # ---- Smart Folders (saved searches) -----------------------------
    smart = [
        ("Ideas",     "fts", "tags:idea",   ""),
        ("To-do",     "fts", "tags:todo",   ""),
        ("Receipts",  "fts", "receipt OR $",""),
        ("Errors",    "fts", "error OR 401 OR 500", ""),
    ]
    for i, (name, kind, q, icon) in enumerate(smart, start=1):
        t = ms(base)
        con.execute(
            "INSERT INTO SavedSearches(id,guid,name,kind,query,icon,sort_order,created_at,updated_at) "
            "VALUES(?,?,?,?,?,?,?,?,?)",
            (i, new_guid(), name, kind, q, icon, i, t, t))

    con.commit()

    # ================= VERIFICATION =================================
    print(f"DB written: {DB_PATH}")
    print(f"Data root : {DATA_ROOT}")
    ok = True

    integrity = con.execute("PRAGMA integrity_check").fetchone()[0]
    print(f"integrity_check         : {integrity}")
    ok &= integrity == "ok"

    counts = {
        "Folders": con.execute("SELECT COUNT(*) FROM Folders").fetchone()[0],
        "Notes":   con.execute("SELECT COUNT(*) FROM Notes").fetchone()[0],
        "Images":  con.execute("SELECT COUNT(*) FROM Images").fetchone()[0],
        "Tags":    con.execute("SELECT COUNT(*) FROM Tags").fetchone()[0],
        "SavedSearches": con.execute("SELECT COUNT(*) FROM SavedSearches").fetchone()[0],
        "NotesFTS": con.execute("SELECT COUNT(*) FROM NotesFTS").fetchone()[0],
    }
    print(f"row counts              : {counts}")
    ok &= counts["Notes"] == 10 and counts["Images"] == 10
    # 9 indexed (10 notes minus 1 soft-deleted)
    ok &= counts["NotesFTS"] == 9

    def search(q):
        return con.execute(
            "SELECT n.title FROM NotesFTS f JOIN Notes n ON n.id=f.rowid "
            "WHERE NotesFTS MATCH ? ORDER BY bm25(NotesFTS) LIMIT 5", (q,)).fetchall()

    # OCR-inside-image search: '401' only exists in an image's ocr_text.
    r = search("401")
    print(f"search '401' (OCR only) : {[x[0] for x in r]}")
    ok &= any("login flow" in x[0] for x in r)

    # Ranked search across body + ocr.
    r = search("receipt OR résumé")
    print(f"search 'receipt OR résumé': {[x[0] for x in r]}")
    ok &= len(r) > 0

    # Unicode diacritic folding: 'cafe' should match 'café'.
    r = search("cafe")
    print(f"search 'cafe'->'café'    : {[x[0] for x in r]}")
    ok &= any("afé" in x[0] or "Café" in x[0] for x in r)

    # Prefix / as-you-type: 'log' should hit 'login'.
    r = search("log*")
    print(f"search 'log*' (prefix)   : {[x[0] for x in r]}")
    ok &= len(r) > 0

    # Soft-deleted note must NOT appear.
    r = search("discarded")
    print(f"search 'discarded' (del) : {[x[0] for x in r]} (expect empty)")
    ok &= len(r) == 0

    # Verify every image file exists on disk.
    missing = []
    for (rel,) in con.execute("SELECT rel_path FROM Images").fetchall():
        if not os.path.exists(os.path.join(DATA_ROOT, rel.replace("/", os.sep))):
            missing.append(rel)
    print(f"missing image files     : {missing if missing else 'none'}")
    ok &= not missing

    con.close()
    print("=" * 40)
    print("RESULT:", "ALL CHECKS PASSED ✅" if ok else "CHECKS FAILED ❌")
    return 0 if ok else 1


if __name__ == "__main__":
    raise SystemExit(main())
