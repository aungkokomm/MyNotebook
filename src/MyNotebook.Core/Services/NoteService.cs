using System.Text;
using Microsoft.Data.Sqlite;
using MyNotebook.Core.Models;

namespace MyNotebook.Core.Services;

/// <summary>SQLite-backed implementation of the notebook data API.</summary>
public sealed class NoteService : INoteService
{
    private readonly IStorageService _storage;

    public NoteService(IStorageService storage) => _storage = storage;

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    // ---------------------------------------------------------------- Folders
    public Folder CreateFolder(string name, long? parentId = null)
    {
        var f = new Folder { Name = name, ParentId = parentId, CreatedAt = Now(), UpdatedAt = Now() };
        using var con = _storage.OpenConnection();
        using var cmd = con.CreateCommand();
        cmd.CommandText = @"INSERT INTO Folders(guid,name,parent_id,sort_order,created_at,updated_at,deleted)
                            VALUES($g,$n,$p,$s,$c,$u,0); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$g", f.Guid);
        cmd.Parameters.AddWithValue("$n", f.Name);
        cmd.Parameters.AddWithValue("$p", (object?)f.ParentId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$s", f.SortOrder);
        cmd.Parameters.AddWithValue("$c", f.CreatedAt);
        cmd.Parameters.AddWithValue("$u", f.UpdatedAt);
        f.Id = (long)cmd.ExecuteScalar()!;
        return f;
    }

    public void RenameFolder(long id, string name)
    {
        using var con = _storage.OpenConnection();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "UPDATE Folders SET name=$n, updated_at=$u WHERE id=$id";
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$u", Now());
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void DeleteFolder(long id)
    {
        using var con = _storage.OpenConnection();
        using var tx = con.BeginTransaction();
        // Soft-delete the folder + all descendants, and the notes inside any of them.
        using (var cmd = con.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = @"
                WITH RECURSIVE sub(id) AS (
                    SELECT $id
                    UNION ALL
                    SELECT f.id FROM Folders f JOIN sub ON f.parent_id = sub.id
                )
                UPDATE Notes SET deleted=1, updated_at=$u WHERE folder_id IN (SELECT id FROM sub);";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$u", Now());
            cmd.ExecuteNonQuery();
        }
        using (var cmd = con.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = @"
                WITH RECURSIVE sub(id) AS (
                    SELECT $id
                    UNION ALL
                    SELECT f.id FROM Folders f JOIN sub ON f.parent_id = sub.id
                )
                UPDATE Folders SET deleted=1, updated_at=$u WHERE id IN (SELECT id FROM sub);";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$u", Now());
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void MoveNoteToFolder(long noteId, long? folderId)
    {
        using var con = _storage.OpenConnection();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "UPDATE Notes SET folder_id=$f, updated_at=$u WHERE id=$id";
        cmd.Parameters.AddWithValue("$f", (object?)folderId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$u", Now());
        cmd.Parameters.AddWithValue("$id", noteId);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<Folder> ListFolders()
    {
        using var con = _storage.OpenConnection();
        using var cmd = con.CreateCommand();
        cmd.CommandText = @"SELECT id,guid,name,parent_id,sort_order,created_at,updated_at,deleted
                            FROM Folders WHERE deleted=0 ORDER BY sort_order, name";
        using var r = cmd.ExecuteReader();
        var list = new List<Folder>();
        while (r.Read())
        {
            list.Add(new Folder
            {
                Id = r.GetInt64(0),
                Guid = r.GetString(1),
                Name = r.GetString(2),
                ParentId = r.IsDBNull(3) ? null : r.GetInt64(3),
                SortOrder = r.GetInt32(4),
                CreatedAt = r.GetInt64(5),
                UpdatedAt = r.GetInt64(6),
                Deleted = r.GetInt64(7) != 0,
            });
        }
        return list;
    }

    // ------------------------------------------------------------------ Notes
    public Note CreateNote(string title, NoteType type = NoteType.Note, long? folderId = null)
    {
        var n = new Note
        {
            Title = title,
            Type = type,
            FolderId = folderId,
            CreatedAt = Now(),
            UpdatedAt = Now(),
        };
        using var con = _storage.OpenConnection();
        using var cmd = con.CreateCommand();
        cmd.CommandText = @"INSERT INTO Notes(guid,folder_id,title,body_rtf,body_plain,note_type,
                              pinned,sort_order,created_at,updated_at,deleted)
                            VALUES($g,$f,$t,'','',$ty,0,0,$c,$u,0); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$g", n.Guid);
        cmd.Parameters.AddWithValue("$f", (object?)n.FolderId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$t", n.Title);
        cmd.Parameters.AddWithValue("$ty", n.Type.ToDbValue());
        cmd.Parameters.AddWithValue("$c", n.CreatedAt);
        cmd.Parameters.AddWithValue("$u", n.UpdatedAt);
        n.Id = (long)cmd.ExecuteScalar()!;
        return n;
    }

    public Note? GetNote(long id)
    {
        using var con = _storage.OpenConnection();
        using var cmd = con.CreateCommand();
        cmd.CommandText = @"SELECT id,guid,folder_id,title,body_rtf,body_plain,note_type,
                                   pinned,sort_order,created_at,updated_at,deleted
                            FROM Notes WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadNote(r) : null;
    }

    public void UpdateNote(Note note)
    {
        note.UpdatedAt = Now();
        using var con = _storage.OpenConnection();
        using var cmd = con.CreateCommand();
        cmd.CommandText = @"UPDATE Notes SET folder_id=$f, title=$t, body_rtf=$rtf, body_plain=$plain,
                              note_type=$ty, pinned=$p, sort_order=$s, updated_at=$u, deleted=$d
                            WHERE id=$id";
        cmd.Parameters.AddWithValue("$f", (object?)note.FolderId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$t", note.Title);
        cmd.Parameters.AddWithValue("$rtf", note.BodyRtf);
        cmd.Parameters.AddWithValue("$plain", note.BodyPlain);
        cmd.Parameters.AddWithValue("$ty", note.Type.ToDbValue());
        cmd.Parameters.AddWithValue("$p", note.Pinned ? 1 : 0);
        cmd.Parameters.AddWithValue("$s", note.SortOrder);
        cmd.Parameters.AddWithValue("$u", note.UpdatedAt);
        cmd.Parameters.AddWithValue("$d", note.Deleted ? 1 : 0);
        cmd.Parameters.AddWithValue("$id", note.Id);
        cmd.ExecuteNonQuery();
    }

    public void SetPinned(long noteId, bool pinned)
    {
        using var con = _storage.OpenConnection();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "UPDATE Notes SET pinned=$p, updated_at=$u WHERE id=$id";
        cmd.Parameters.AddWithValue("$p", pinned ? 1 : 0);
        cmd.Parameters.AddWithValue("$u", Now());
        cmd.Parameters.AddWithValue("$id", noteId);
        cmd.ExecuteNonQuery();
    }

    public void SoftDeleteNote(long noteId)
    {
        using var con = _storage.OpenConnection();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "UPDATE Notes SET deleted=1, updated_at=$u WHERE id=$id";
        cmd.Parameters.AddWithValue("$u", Now());
        cmd.Parameters.AddWithValue("$id", noteId);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<Note> ListNotes(long? folderId = null, bool includeDeleted = false)
    {
        using var con = _storage.OpenConnection();
        using var cmd = con.CreateCommand();
        var sb = new StringBuilder(@"SELECT id,guid,folder_id,title,body_rtf,body_plain,note_type,
                                            pinned,sort_order,created_at,updated_at,deleted
                                     FROM Notes WHERE 1=1");
        if (!includeDeleted) sb.Append(" AND deleted=0");
        if (folderId.HasValue) { sb.Append(" AND folder_id=$f"); cmd.Parameters.AddWithValue("$f", folderId.Value); }
        sb.Append(" ORDER BY pinned DESC, sort_order, updated_at DESC");
        cmd.CommandText = sb.ToString();
        using var r = cmd.ExecuteReader();
        var list = new List<Note>();
        while (r.Read()) list.Add(ReadNote(r));
        return list;
    }

    // ----------------------------------------------------------------- Images
    public ImageItem AddImage(long noteId, string relPath, int width, int height,
                              string ocrText = "", string caption = "")
    {
        var img = new ImageItem
        {
            NoteId = noteId, RelPath = relPath, Width = width, Height = height,
            OcrText = ocrText, Caption = caption, CreatedAt = Now(), UpdatedAt = Now(),
        };
        using var con = _storage.OpenConnection();
        using var cmd = con.CreateCommand();
        // sort_order defaults to (max+1) so cards append to the bottom of the thread.
        cmd.CommandText = @"INSERT INTO Images(guid,note_id,rel_path,width,height,ocr_text,caption,
                              sort_order,created_at,updated_at,deleted)
                            VALUES($g,$n,$rp,$w,$h,$o,$cap,
                              COALESCE((SELECT MAX(sort_order)+1 FROM Images WHERE note_id=$n),0),
                              $c,$u,0);
                            SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$g", img.Guid);
        cmd.Parameters.AddWithValue("$n", img.NoteId);
        cmd.Parameters.AddWithValue("$rp", img.RelPath);
        cmd.Parameters.AddWithValue("$w", img.Width);
        cmd.Parameters.AddWithValue("$h", img.Height);
        cmd.Parameters.AddWithValue("$o", img.OcrText);
        cmd.Parameters.AddWithValue("$cap", img.Caption);
        cmd.Parameters.AddWithValue("$c", img.CreatedAt);
        cmd.Parameters.AddWithValue("$u", img.UpdatedAt);
        img.Id = (long)cmd.ExecuteScalar()!;
        return img;
    }

    public void UpdateImageOcr(long imageId, string ocrText)
    {
        using var con = _storage.OpenConnection();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "UPDATE Images SET ocr_text=$o, updated_at=$u WHERE id=$id";
        cmd.Parameters.AddWithValue("$o", ocrText);
        cmd.Parameters.AddWithValue("$u", Now());
        cmd.Parameters.AddWithValue("$id", imageId);
        cmd.ExecuteNonQuery();
    }

    public void SaveNoteVersion(long noteId, string title, string bodyRtf, string bodyPlain)
    {
        using var con = _storage.OpenConnection();
        using (var ins = con.CreateCommand())
        {
            ins.CommandText = @"INSERT INTO NoteVersions(note_id,title,body_rtf,body_plain,created_at)
                                VALUES($n,$t,$r,$p,$c)";
            ins.Parameters.AddWithValue("$n", noteId);
            ins.Parameters.AddWithValue("$t", title ?? "");
            ins.Parameters.AddWithValue("$r", bodyRtf ?? "");
            ins.Parameters.AddWithValue("$p", bodyPlain ?? "");
            ins.Parameters.AddWithValue("$c", Now());
            ins.ExecuteNonQuery();
        }
        using var prune = con.CreateCommand();
        prune.CommandText = @"DELETE FROM NoteVersions WHERE note_id=$n AND id NOT IN
                              (SELECT id FROM NoteVersions WHERE note_id=$n ORDER BY created_at DESC, id DESC LIMIT 50)";
        prune.Parameters.AddWithValue("$n", noteId);
        prune.ExecuteNonQuery();
    }

    public IReadOnlyList<NoteVersion> ListNoteVersions(long noteId)
    {
        using var con = _storage.OpenConnection();
        using var cmd = con.CreateCommand();
        cmd.CommandText = @"SELECT id,note_id,title,body_rtf,body_plain,created_at
                            FROM NoteVersions WHERE note_id=$n ORDER BY created_at DESC, id DESC";
        cmd.Parameters.AddWithValue("$n", noteId);
        using var r = cmd.ExecuteReader();
        var list = new List<NoteVersion>();
        while (r.Read())
            list.Add(ReadVersion(r));
        return list;
    }

    public NoteVersion? GetNoteVersion(long versionId)
    {
        using var con = _storage.OpenConnection();
        using var cmd = con.CreateCommand();
        cmd.CommandText = @"SELECT id,note_id,title,body_rtf,body_plain,created_at FROM NoteVersions WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", versionId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadVersion(r) : null;
    }

    private static NoteVersion ReadVersion(Microsoft.Data.Sqlite.SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0), NoteId = r.GetInt64(1), Title = r.GetString(2),
        BodyRtf = r.GetString(3), BodyPlain = r.GetString(4), CreatedAt = r.GetInt64(5),
    };

    public string DeleteImage(long imageId)
    {
        using var con = _storage.OpenConnection();
        string rel;
        using (var sel = con.CreateCommand())
        {
            sel.CommandText = "SELECT rel_path FROM Images WHERE id=$id";
            sel.Parameters.AddWithValue("$id", imageId);
            rel = sel.ExecuteScalar() as string ?? "";
        }
        // Hard delete: the AFTER DELETE trigger re-aggregates the note's OCR into FTS.
        using var del = con.CreateCommand();
        del.CommandText = "DELETE FROM Images WHERE id=$id";
        del.Parameters.AddWithValue("$id", imageId);
        del.ExecuteNonQuery();
        return rel;
    }

    public void ReorderImages(long noteId, IReadOnlyList<long> orderedImageIds)
    {
        using var con = _storage.OpenConnection();
        using var tx = con.BeginTransaction();
        using var cmd = con.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE Images SET sort_order=$s, updated_at=$u WHERE id=$id AND note_id=$n";
        var pS = cmd.CreateParameter(); pS.ParameterName = "$s"; cmd.Parameters.Add(pS);
        var pU = cmd.CreateParameter(); pU.ParameterName = "$u"; pU.Value = Now(); cmd.Parameters.Add(pU);
        var pId = cmd.CreateParameter(); pId.ParameterName = "$id"; cmd.Parameters.Add(pId);
        var pN = cmd.CreateParameter(); pN.ParameterName = "$n"; pN.Value = noteId; cmd.Parameters.Add(pN);
        for (int i = 0; i < orderedImageIds.Count; i++)
        {
            pS.Value = i;
            pId.Value = orderedImageIds[i];
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public IReadOnlyList<ImageItem> ListImages(long noteId)
    {
        using var con = _storage.OpenConnection();
        using var cmd = con.CreateCommand();
        cmd.CommandText = @"SELECT id,guid,note_id,rel_path,width,height,ocr_text,caption,
                                   sort_order,created_at,updated_at,deleted
                            FROM Images WHERE note_id=$n AND deleted=0
                            ORDER BY sort_order, created_at";
        cmd.Parameters.AddWithValue("$n", noteId);
        using var r = cmd.ExecuteReader();
        var list = new List<ImageItem>();
        while (r.Read())
        {
            list.Add(new ImageItem
            {
                Id = r.GetInt64(0), Guid = r.GetString(1), NoteId = r.GetInt64(2),
                RelPath = r.GetString(3), Width = r.GetInt32(4), Height = r.GetInt32(5),
                OcrText = r.GetString(6), Caption = r.GetString(7), SortOrder = r.GetInt32(8),
                CreatedAt = r.GetInt64(9), UpdatedAt = r.GetInt64(10), Deleted = r.GetInt64(11) != 0,
            });
        }
        return list;
    }

    // ------------------------------------------------------------------- Tags
    public Tag EnsureTag(string name, string color = "")
    {
        using var con = _storage.OpenConnection();
        using (var sel = con.CreateCommand())
        {
            sel.CommandText = "SELECT id,guid,name,color,created_at FROM Tags WHERE name=$n";
            sel.Parameters.AddWithValue("$n", name);
            using var r = sel.ExecuteReader();
            if (r.Read())
                return new Tag { Id = r.GetInt64(0), Guid = r.GetString(1), Name = r.GetString(2),
                                 Color = r.GetString(3), CreatedAt = r.GetInt64(4) };
        }
        var tag = new Tag { Name = name, Color = color, CreatedAt = Now() };
        using var ins = con.CreateCommand();
        ins.CommandText = @"INSERT INTO Tags(guid,name,color,created_at) VALUES($g,$n,$c,$ca);
                            SELECT last_insert_rowid();";
        ins.Parameters.AddWithValue("$g", tag.Guid);
        ins.Parameters.AddWithValue("$n", tag.Name);
        ins.Parameters.AddWithValue("$c", tag.Color);
        ins.Parameters.AddWithValue("$ca", tag.CreatedAt);
        tag.Id = (long)ins.ExecuteScalar()!;
        return tag;
    }

    public void AddTagToNote(long noteId, long tagId)
    {
        using var con = _storage.OpenConnection();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO NoteTags(note_id,tag_id) VALUES($n,$t)";
        cmd.Parameters.AddWithValue("$n", noteId);
        cmd.Parameters.AddWithValue("$t", tagId);
        cmd.ExecuteNonQuery();
    }

    public void RemoveTagFromNote(long noteId, long tagId)
    {
        using var con = _storage.OpenConnection();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "DELETE FROM NoteTags WHERE note_id=$n AND tag_id=$t";
        cmd.Parameters.AddWithValue("$n", noteId);
        cmd.Parameters.AddWithValue("$t", tagId);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<Tag> ListTags()
    {
        using var con = _storage.OpenConnection();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT id,guid,name,color,created_at FROM Tags ORDER BY name";
        return ReadTags(cmd);
    }

    public IReadOnlyList<Tag> ListTagsForNote(long noteId)
    {
        using var con = _storage.OpenConnection();
        using var cmd = con.CreateCommand();
        cmd.CommandText = @"SELECT t.id,t.guid,t.name,t.color,t.created_at
                            FROM Tags t JOIN NoteTags nt ON nt.tag_id=t.id
                            WHERE nt.note_id=$n ORDER BY t.name";
        cmd.Parameters.AddWithValue("$n", noteId);
        return ReadTags(cmd);
    }

    private static IReadOnlyList<Tag> ReadTags(SqliteCommand cmd)
    {
        using var r = cmd.ExecuteReader();
        var list = new List<Tag>();
        while (r.Read())
            list.Add(new Tag { Id = r.GetInt64(0), Guid = r.GetString(1), Name = r.GetString(2),
                               Color = r.GetString(3), CreatedAt = r.GetInt64(4) });
        return list;
    }

    public IReadOnlyList<Note> ListNotesWithTag(long tagId)
    {
        using var con = _storage.OpenConnection();
        using var cmd = con.CreateCommand();
        cmd.CommandText = @"SELECT n.id,n.guid,n.folder_id,n.title,n.body_rtf,n.body_plain,n.note_type,
                                   n.pinned,n.sort_order,n.created_at,n.updated_at,n.deleted
                            FROM Notes n JOIN NoteTags nt ON nt.note_id=n.id
                            WHERE nt.tag_id=$t AND n.deleted=0
                            ORDER BY n.pinned DESC, n.updated_at DESC";
        cmd.Parameters.AddWithValue("$t", tagId);
        using var r = cmd.ExecuteReader();
        var list = new List<Note>();
        while (r.Read()) list.Add(ReadNote(r));
        return list;
    }

    // ---------------------------------------------------------- Saved searches
    public IReadOnlyList<SavedSearch> ListSavedSearches()
    {
        using var con = _storage.OpenConnection();
        using var cmd = con.CreateCommand();
        cmd.CommandText = @"SELECT id,guid,name,kind,query,icon,sort_order
                            FROM SavedSearches ORDER BY sort_order, name";
        using var r = cmd.ExecuteReader();
        var list = new List<SavedSearch>();
        while (r.Read())
        {
            list.Add(new SavedSearch
            {
                Id = r.GetInt64(0), Guid = r.GetString(1), Name = r.GetString(2),
                Kind = r.GetString(3), Query = r.GetString(4), Icon = r.GetString(5),
                SortOrder = r.GetInt32(6),
            });
        }
        return list;
    }

    // ----------------------------------------------------------------- Search
    // Highlight sentinels wrapped around matches in the snippet (parsed by the UI into
    // styled runs). Private-use codepoints that won't occur in real note text.
    public const char HiOpen = '';
    public const char HiClose = '';

    public IReadOnlyList<SearchHit> Search(string query, int limit = 50)
    {
        string word, sub, typeFilter;
        if (HasOperators(query))
            (word, sub, typeFilter) = ParseQuery(query);   // -exclude "phrase" title: tag: type: OR
        else
        {
            word = BuildMatchQuery(query);                 // simple path: prefix as-you-type
            sub = BuildTrigramQuery(query);
            typeFilter = "";
        }
        if (word.Length == 0 && sub.Length == 0) return Array.Empty<SearchHit>();

        using var con = _storage.OpenConnection();
        var hits = new List<SearchHit>();
        var seen = new HashSet<long>();

        // Pass 1 — word index: precise ranking + as-you-type prefix.
        if (word.Length > 0)
            QueryInto(con, "NotesFTS", "bm25(NotesFTS, 10.0, 5.0, 3.0, 2.0)", word, query, typeFilter, limit, hits, seen);

        // Pass 2 — trigram index: substrings the word index can't reach (e.g. a word inside
        // a spaceless Myanmar run). Appended after the word matches.
        if (sub.Length > 0 && hits.Count < limit)
            QueryInto(con, "NotesTrigram", "bm25(NotesTrigram)", sub, query, typeFilter, limit, hits, seen);

        return hits;
    }

    private static void QueryInto(SqliteConnection con, string table, string rankExpr, string match,
                                  string raw, string typeFilter, int limit, List<SearchHit> hits, HashSet<long> seen)
    {
        using var cmd = con.CreateCommand();
        var typeClause = typeFilter.Length > 0 ? " AND n.note_type = $type" : "";
        // Ranking: bm25 (lower = better) minus boosts for pinned notes and exact-title hits,
        // with recency as the tie-breaker.
        cmd.CommandText = $@"SELECT n.id, n.title, n.note_type, n.updated_at,
                                    COALESCE(fo.name, 'Unfiled') AS folder,
                                    snippet({table}, 1, char(57344), char(57345), '…', 14) AS preview,
                                    ({rankExpr}
                                       - CASE WHEN n.pinned = 1 THEN 3.0 ELSE 0 END
                                       - CASE WHEN lower(n.title) = lower($raw) THEN 8.0 ELSE 0 END) AS rank
                             FROM {table} f
                             JOIN Notes n ON n.id = f.rowid
                             LEFT JOIN Folders fo ON fo.id = n.folder_id
                             WHERE {table} MATCH $q AND n.deleted = 0{typeClause}
                             ORDER BY rank, n.updated_at DESC
                             LIMIT $lim";
        cmd.Parameters.AddWithValue("$q", match);
        cmd.Parameters.AddWithValue("$raw", raw.Trim());
        cmd.Parameters.AddWithValue("$lim", limit);
        if (typeFilter.Length > 0) cmd.Parameters.AddWithValue("$type", typeFilter);
        using var r = cmd.ExecuteReader();
        while (r.Read() && hits.Count < limit)
        {
            var id = r.GetInt64(0);
            if (!seen.Add(id)) continue;   // de-dupe across passes
            hits.Add(new SearchHit
            {
                NoteId = id,
                Title = r.GetString(1),
                Type = NoteTypeExtensions.ParseNoteType(r.GetString(2)),
                Updated = r.GetInt64(3),
                Folder = r.GetString(4),
                Preview = r.IsDBNull(5) ? "" : r.GetString(5),
                Rank = r.GetDouble(6),
            });
        }
    }

    // Only switch to the operator parser for a GENUINE operator — not any stray '-' or ':'
    // in normal text (e.g. "co-op" or a time "3:55"), which would change results unexpectedly.
    private static bool HasOperators(string raw)
    {
        if (raw.Contains('"')) return true;
        foreach (var tok in raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (tok == "OR") return true;
            if (tok.Length > 1 && tok[0] == '-') return true;
            if (tok.StartsWith("title:", StringComparison.OrdinalIgnoreCase) ||
                tok.StartsWith("tag:", StringComparison.OrdinalIgnoreCase) ||
                tok.StartsWith("type:", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// Parse power-search operators into (word-index MATCH, trigram MATCH, type filter):
    /// <c>-exclude</c>, <c>"exact phrase"</c>, <c>title:</c> / <c>tag:</c> scopes,
    /// <c>type:note|thread</c>, and bare <c>OR</c> between terms.
    /// </summary>
    internal static (string word, string trig, string type) ParseQuery(string raw)
    {
        string type = "";
        var word = new StringBuilder();
        var trig = new StringBuilder();
        bool orPending = false;

        void Append(StringBuilder sb, string piece)
        {
            if (sb.Length > 0) sb.Append(orPending ? " OR " : " ");
            sb.Append(piece);
        }

        foreach (var token in Tokenize(raw))
        {
            if (token == "OR") { orPending = true; continue; }

            bool exclude = token.Length > 1 && token[0] == '-';
            var t = exclude ? token[1..] : token;

            string? col = null;
            if (t.StartsWith("title:", StringComparison.OrdinalIgnoreCase)) { col = "title"; t = t[6..]; }
            else if (t.StartsWith("tag:", StringComparison.OrdinalIgnoreCase)) { col = "tags"; t = t[4..]; }
            else if (t.StartsWith("type:", StringComparison.OrdinalIgnoreCase))
            {
                var v = t[5..].ToLowerInvariant();
                type = v.StartsWith("thread") ? "thread" : v.StartsWith("note") ? "note" : "";
                continue;
            }
            if (t.Length == 0) continue;

            var phrase = "\"" + t.Replace("\"", "\"\"") + "\"";
            var prefix = exclude ? "NOT " : "";
            var expr = col != null ? $"{prefix}{col} : {phrase}" : $"{prefix}{phrase}";
            Append(word, expr);
            if (t.Length >= 3) Append(trig, expr);   // same scope/exclusion applies to trigram
            orPending = false;
        }
        return (word.ToString(), trig.ToString(), type);
    }

    /// <summary>Split on whitespace but keep "quoted phrases" as single tokens.</summary>
    private static IEnumerable<string> Tokenize(string s)
    {
        int i = 0;
        while (i < s.Length)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            if (i >= s.Length) break;
            // Allow a leading '-' before a quote: -"phrase"
            int dash = (s[i] == '-' && i + 1 < s.Length && s[i + 1] == '"') ? 1 : 0;
            if (s[i + dash] == '"')
            {
                int close = s.IndexOf('"', i + dash + 1);
                if (close < 0) close = s.Length;
                var inner = s.Substring(i + dash + 1, close - (i + dash + 1));
                yield return (dash == 1 ? "-" : "") + inner;
                i = close + 1;
            }
            else
            {
                int j = i;
                while (j < s.Length && !char.IsWhiteSpace(s[j])) j++;
                yield return s.Substring(i, j - i);
                i = j;
            }
        }
    }

    /// <summary>
    /// Turn raw user input into a safe FTS5 MATCH expression. Each term is quoted
    /// (so punctuation can't break syntax) and the final term gets a '*' prefix
    /// wildcard for as-you-type matching. Empty input → empty (no search).
    /// </summary>
    internal static string BuildMatchQuery(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var terms = raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder();
        for (int i = 0; i < terms.Length; i++)
        {
            var term = terms[i].Replace("\"", "\"\""); // escape embedded quotes
            if (sb.Length > 0) sb.Append(' ');
            sb.Append('"').Append(term).Append('"');
            if (i == terms.Length - 1) sb.Append('*'); // prefix on the last token
        }
        return sb.ToString();
    }

    /// <summary>
    /// Build a trigram MATCH expression: each whitespace-separated term of 3+ characters
    /// becomes a quoted phrase (a contiguous-substring match). Terms shorter than 3 chars
    /// are skipped (trigram needs ≥3 chars). A spaceless Myanmar run is one long term, so
    /// it matches as a substring anywhere in a note.
    /// </summary>
    internal static string BuildTrigramQuery(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var terms = raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder();
        foreach (var term in terms)
        {
            if (term.Length < 3) continue;
            var t = term.Replace("\"", "\"\"");
            if (sb.Length > 0) sb.Append(' ');
            sb.Append('"').Append(t).Append('"');
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------ Maintenance
    public NotebookStats GetStats()
    {
        using var con = _storage.OpenConnection();
        using var cmd = con.CreateCommand();
        cmd.CommandText = @"
            SELECT
              (SELECT COUNT(*) FROM Notes  WHERE deleted=0 AND note_type='note'),
              (SELECT COUNT(*) FROM Notes  WHERE deleted=0 AND note_type='thread'),
              (SELECT COUNT(*) FROM Images WHERE deleted=0),
              (SELECT COUNT(*) FROM Notes  WHERE deleted=1)";
        using var r = cmd.ExecuteReader();
        r.Read();
        return new NotebookStats
        {
            Notes = r.GetInt32(0),
            Threads = r.GetInt32(1),
            Images = r.GetInt32(2),
            DeletedNotes = r.GetInt32(3),
        };
    }

    public IReadOnlyList<string> EmptyTrash()
    {
        using var con = _storage.OpenConnection();
        // Collect image files of soft-deleted notes before removing the rows.
        var paths = new List<string>();
        using (var sel = con.CreateCommand())
        {
            sel.CommandText = @"SELECT i.rel_path FROM Images i
                                JOIN Notes n ON n.id = i.note_id
                                WHERE n.deleted = 1";
            using var r = sel.ExecuteReader();
            while (r.Read()) paths.Add(r.GetString(0));
        }
        // Hard-delete the notes; Images rows cascade (FK ON DELETE CASCADE).
        using (var del = con.CreateCommand())
        {
            del.CommandText = "DELETE FROM Notes WHERE deleted = 1";
            del.ExecuteNonQuery();
        }
        return paths;
    }

    public IReadOnlyList<Note> ListTrash()
    {
        using var con = _storage.OpenConnection();
        using var cmd = con.CreateCommand();
        cmd.CommandText = @"SELECT id,guid,folder_id,title,body_rtf,body_plain,note_type,
                                   pinned,sort_order,created_at,updated_at,deleted
                            FROM Notes WHERE deleted=1 ORDER BY updated_at DESC";
        using var r = cmd.ExecuteReader();
        var list = new List<Note>();
        while (r.Read()) list.Add(ReadNote(r));
        return list;
    }

    public void RestoreNote(long noteId)
    {
        using var con = _storage.OpenConnection();
        using var cmd = con.CreateCommand();
        // deleted=0 fires the FTS re-index trigger automatically.
        cmd.CommandText = "UPDATE Notes SET deleted=0, updated_at=$u WHERE id=$id";
        cmd.Parameters.AddWithValue("$u", Now());
        cmd.Parameters.AddWithValue("$id", noteId);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<string> DeleteNoteForever(long noteId)
    {
        using var con = _storage.OpenConnection();
        var paths = new List<string>();
        using (var sel = con.CreateCommand())
        {
            sel.CommandText = "SELECT rel_path FROM Images WHERE note_id=$id";
            sel.Parameters.AddWithValue("$id", noteId);
            using var r = sel.ExecuteReader();
            while (r.Read()) paths.Add(r.GetString(0));
        }
        using (var del = con.CreateCommand())
        {
            // Images rows cascade; trg_notes_ad removes the FTS row.
            del.CommandText = "DELETE FROM Notes WHERE id=$id";
            del.Parameters.AddWithValue("$id", noteId);
            del.ExecuteNonQuery();
        }
        return paths;
    }

    public void RebuildSearchIndex()
    {
        using var con = _storage.OpenConnection();
        using var tx = con.BeginTransaction();
        foreach (var table in new[] { "NotesFTS", "NotesTrigram" })
        {
            using (var clear = con.CreateCommand())
            {
                clear.Transaction = tx;
                clear.CommandText = $"DELETE FROM {table}";
                clear.ExecuteNonQuery();
            }
            using var fill = con.CreateCommand();
            fill.Transaction = tx;
            fill.CommandText = $@"
                INSERT INTO {table}(rowid, title, body, ocr, tags)
                SELECT n.id, n.title, n.body_plain,
                    (SELECT COALESCE(group_concat(ocr_text, ' '), '')
                       FROM Images WHERE note_id = n.id AND deleted = 0),
                    (SELECT COALESCE(group_concat(t.name, ' '), '')
                       FROM NoteTags nt JOIN Tags t ON t.id = nt.tag_id
                      WHERE nt.note_id = n.id)
                FROM Notes n WHERE n.deleted = 0";
            fill.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public IReadOnlyList<ImageItem> AllImages()
    {
        using var con = _storage.OpenConnection();
        using var cmd = con.CreateCommand();
        cmd.CommandText = @"SELECT id,guid,note_id,rel_path,width,height,ocr_text,caption,
                                   sort_order,created_at,updated_at,deleted
                            FROM Images WHERE deleted=0 ORDER BY note_id, sort_order";
        using var r = cmd.ExecuteReader();
        var list = new List<ImageItem>();
        while (r.Read())
        {
            list.Add(new ImageItem
            {
                Id = r.GetInt64(0), Guid = r.GetString(1), NoteId = r.GetInt64(2),
                RelPath = r.GetString(3), Width = r.GetInt32(4), Height = r.GetInt32(5),
                OcrText = r.GetString(6), Caption = r.GetString(7), SortOrder = r.GetInt32(8),
                CreatedAt = r.GetInt64(9), UpdatedAt = r.GetInt64(10), Deleted = r.GetInt64(11) != 0,
            });
        }
        return list;
    }

    private static Note ReadNote(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        Guid = r.GetString(1),
        FolderId = r.IsDBNull(2) ? null : r.GetInt64(2),
        Title = r.GetString(3),
        BodyRtf = r.GetString(4),
        BodyPlain = r.GetString(5),
        Type = NoteTypeExtensions.ParseNoteType(r.GetString(6)),
        Pinned = r.GetInt64(7) != 0,
        SortOrder = r.GetInt32(8),
        CreatedAt = r.GetInt64(9),
        UpdatedAt = r.GetInt64(10),
        Deleted = r.GetInt64(11) != 0,
    };
}

/// <summary>No-op sync seam for the MVP.</summary>
public sealed class NullSyncService : ISyncService
{
    public bool IsEnabled => false;
    public Task SyncNowAsync(CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>OCR fallback used when no engine is available (and in Core tests).</summary>
public sealed class NullOcrService : IOcrService
{
    public bool IsAvailable => false;
    public string? EngineLanguage => null;
    public Task<string> RecognizeAsync(string imagePath, CancellationToken ct = default)
        => Task.FromResult("");
}
