using System.Text.Json;
using MMail.Core.Models;
using Microsoft.Data.Sqlite;

namespace MMail.Core.Storage;

/// Local cache: accounts, messages (with full-text search), mailbox names and
/// small preferences, in one SQLite file (WAL). Safe to call from any thread.
public sealed class MailStore : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly Lock _gate = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// The folder holding the database; attachments are cached next to it.
    public string Directory { get; }

    public MailStore(string path)
    {
        Directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _db.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;");
        Migrate();
    }

    public static MailStore OpenDefault() => new(AppPaths.Database);

    public void Dispose() => _db.Dispose();

    private void Migrate()
    {
        Exec("""
            CREATE TABLE IF NOT EXISTS accounts (
                id TEXT PRIMARY KEY, json TEXT NOT NULL, position INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS messages (
                id TEXT PRIMARY KEY,
                account TEXT NOT NULL, folder TEXT NOT NULL, uid INTEGER NOT NULL,
                date INTEGER NOT NULL,
                from_name TEXT NOT NULL, from_email TEXT NOT NULL,
                subject TEXT NOT NULL, preview TEXT NOT NULL, body TEXT NOT NULL, body_html TEXT,
                unread INTEGER NOT NULL, starred INTEGER NOT NULL, body_loaded INTEGER NOT NULL,
                extra TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS messages_folder ON messages(account, folder, uid DESC);
            CREATE VIRTUAL TABLE IF NOT EXISTS messages_fts USING fts5(
                subject, from_name, from_email, preview, body, content='messages', content_rowid='rowid',
                tokenize='unicode61 remove_diacritics 2');
            CREATE TRIGGER IF NOT EXISTS messages_ai AFTER INSERT ON messages BEGIN
                INSERT INTO messages_fts(rowid, subject, from_name, from_email, preview, body)
                VALUES (new.rowid, new.subject, new.from_name, new.from_email, new.preview, new.body);
            END;
            CREATE TRIGGER IF NOT EXISTS messages_ad AFTER DELETE ON messages BEGIN
                INSERT INTO messages_fts(messages_fts, rowid, subject, from_name, from_email, preview, body)
                VALUES ('delete', old.rowid, old.subject, old.from_name, old.from_email, old.preview, old.body);
            END;
            CREATE TRIGGER IF NOT EXISTS messages_au AFTER UPDATE ON messages BEGIN
                INSERT INTO messages_fts(messages_fts, rowid, subject, from_name, from_email, preview, body)
                VALUES ('delete', old.rowid, old.subject, old.from_name, old.from_email, old.preview, old.body);
                INSERT INTO messages_fts(rowid, subject, from_name, from_email, preview, body)
                VALUES (new.rowid, new.subject, new.from_name, new.from_email, new.preview, new.body);
            END;
            CREATE TABLE IF NOT EXISTS mailboxes (
                account TEXT NOT NULL, name TEXT NOT NULL, role TEXT,
                PRIMARY KEY (account, name));
            CREATE TABLE IF NOT EXISTS prefs (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            """);
    }

    // MARK: Accounts

    public List<MailAccountConfig> LoadAccounts()
    {
        lock (_gate)
        {
            using var cmd = Cmd("SELECT json FROM accounts ORDER BY position, rowid");
            using var r = cmd.ExecuteReader();
            var list = new List<MailAccountConfig>();
            while (r.Read())
                if (JsonSerializer.Deserialize<MailAccountConfig>(r.GetString(0), Json) is { } cfg) list.Add(cfg);
            return list;
        }
    }

    public void SaveAccount(MailAccountConfig cfg, int position)
    {
        lock (_gate)
        {
            using var cmd = Cmd("""
                INSERT INTO accounts(id, json, position) VALUES ($id, $json, $pos)
                ON CONFLICT(id) DO UPDATE SET json = excluded.json, position = excluded.position
                """, ("$id", cfg.Id), ("$json", JsonSerializer.Serialize(cfg, Json)), ("$pos", position));
            cmd.ExecuteNonQuery();
        }
    }

    public void DeleteAccount(string id)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            foreach (var sql in new[] { "DELETE FROM accounts WHERE id = $id", "DELETE FROM messages WHERE account = $id",
                                        "DELETE FROM mailboxes WHERE account = $id" })
            {
                using var cmd = Cmd(sql, ("$id", id));
                cmd.Transaction = tx;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    // MARK: Messages

    private sealed record Extra(List<string> To, List<string> Cc, List<string> Labels, List<AttachmentMeta> Attachments,
                                bool HasAttachment, string? MessageId, string? InReplyTo, string? References,
                                string? Unsubscribe, CalendarEvent? CalendarEvent);

    public void Upsert(IEnumerable<Email> emails)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            using var cmd = Cmd("""
                INSERT INTO messages(id, account, folder, uid, date, from_name, from_email, subject, preview, body,
                                     body_html, unread, starred, body_loaded, extra)
                VALUES ($id, $account, $folder, $uid, $date, $fn, $fe, $subject, $preview, $body, $html,
                        $unread, $starred, $loaded, $extra)
                ON CONFLICT(id) DO UPDATE SET
                    folder = excluded.folder, uid = excluded.uid, date = excluded.date,
                    from_name = excluded.from_name, from_email = excluded.from_email, subject = excluded.subject,
                    preview = excluded.preview, body = excluded.body, body_html = excluded.body_html,
                    unread = excluded.unread, starred = excluded.starred, body_loaded = excluded.body_loaded,
                    extra = excluded.extra
                """);
            cmd.Transaction = tx;
            foreach (var e in emails)
            {
                cmd.Parameters.Clear();
                var extra = new Extra(e.To, e.Cc, e.Labels, e.Attachments, e.HasAttachment, e.MessageId, e.InReplyTo,
                                      e.References, e.Unsubscribe, e.CalendarEvent);
                Add(cmd, ("$id", e.Id), ("$account", e.Account), ("$folder", e.Folder), ("$uid", (long)e.Uid),
                    ("$date", e.Date.ToUnixTimeSeconds()), ("$fn", e.FromName), ("$fe", e.FromEmail),
                    ("$subject", e.Subject), ("$preview", e.Preview), ("$body", e.Body), ("$html", e.BodyHtml),
                    ("$unread", e.Unread), ("$starred", e.Starred), ("$loaded", e.BodyLoaded),
                    ("$extra", JsonSerializer.Serialize(extra, Json)));
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public void Upsert(Email email) => Upsert([email]);

    public void Delete(IEnumerable<string> ids)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            using var cmd = Cmd("DELETE FROM messages WHERE id = $id");
            cmd.Transaction = tx;
            foreach (var id in ids)
            {
                cmd.Parameters.Clear();
                Add(cmd, ("$id", id));
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public List<Email> LoadFolder(string account, string folder, int limit = 500) =>
        Query("SELECT * FROM messages WHERE account = $a AND folder = $f ORDER BY date DESC LIMIT $n",
              ("$a", account), ("$f", folder), ("$n", limit));

    public Email? Get(string id) => Query("SELECT * FROM messages WHERE id = $id", ("$id", id)).FirstOrDefault();

    /// Full-text search over subject, sender, preview and body. Each word must match (prefix).
    public List<Email> Search(string query, IReadOnlyCollection<string>? accounts = null, int limit = 50)
    {
        var words = query.Split((char[])[' ', '\t', '\n', '"', '\'', '*', ':', '(', ')', '-', '^'],
                                StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return [];
        var match = string.Join(' ', words.Select(w => $"\"{w}\"*"));
        var hits = Query("""
            SELECT m.* FROM messages_fts f JOIN messages m ON m.rowid = f.rowid
            WHERE messages_fts MATCH $q ORDER BY m.date DESC LIMIT $n
            """, ("$q", match), ("$n", limit * 4));
        return hits.Where(e => accounts is null || accounts.Contains(e.Account)).Take(limit).ToList();
    }

    /// People the user receives mail from, most recent first.
    public List<Contact> Contacts(int limit)
    {
        lock (_gate)
        {
            using var cmd = Cmd("""
                SELECT from_name, from_email, MAX(date) d FROM messages
                WHERE from_email <> '' AND folder NOT IN ('sent', 'drafts', 'spam', 'trash')
                GROUP BY lower(from_email) ORDER BY d DESC LIMIT $n
                """, ("$n", limit));
            using var r = cmd.ExecuteReader();
            var list = new List<Contact>();
            while (r.Read()) list.Add(new Contact(r.GetString(0), r.GetString(1)));
            return list;
        }
    }

    private List<Email> Query(string sql, params (string, object?)[] args)
    {
        lock (_gate)
        {
            using var cmd = Cmd(sql, args);
            using var r = cmd.ExecuteReader();
            var list = new List<Email>();
            while (r.Read()) list.Add(Read(r));
            return list;
        }
    }

    private static Email Read(SqliteDataReader r)
    {
        var extra = JsonSerializer.Deserialize<Extra>(r.GetString(r.GetOrdinal("extra")), Json)!;
        return new Email
        {
            Id = r.GetString(r.GetOrdinal("id")),
            Account = r.GetString(r.GetOrdinal("account")),
            Folder = r.GetString(r.GetOrdinal("folder")),
            Uid = (uint)r.GetInt64(r.GetOrdinal("uid")),
            Date = DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(r.GetOrdinal("date"))).ToLocalTime(),
            FromName = r.GetString(r.GetOrdinal("from_name")),
            FromEmail = r.GetString(r.GetOrdinal("from_email")),
            Subject = r.GetString(r.GetOrdinal("subject")),
            Preview = r.GetString(r.GetOrdinal("preview")),
            Body = r.GetString(r.GetOrdinal("body")),
            BodyHtml = r.IsDBNull(r.GetOrdinal("body_html")) ? null : r.GetString(r.GetOrdinal("body_html")),
            Unread = r.GetBoolean(r.GetOrdinal("unread")),
            Starred = r.GetBoolean(r.GetOrdinal("starred")),
            BodyLoaded = r.GetBoolean(r.GetOrdinal("body_loaded")),
            To = extra.To, Cc = extra.Cc, Labels = extra.Labels, Attachments = extra.Attachments,
            HasAttachment = extra.HasAttachment, MessageId = extra.MessageId, InReplyTo = extra.InReplyTo,
            References = extra.References, Unsubscribe = extra.Unsubscribe, CalendarEvent = extra.CalendarEvent,
        };
    }

    // MARK: Mailboxes

    public void SaveMailboxes(string account, IEnumerable<(string Name, string? Role)> boxes)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            using (var del = Cmd("DELETE FROM mailboxes WHERE account = $a", ("$a", account))) { del.Transaction = tx; del.ExecuteNonQuery(); }
            using var cmd = Cmd("INSERT OR REPLACE INTO mailboxes(account, name, role) VALUES ($a, $n, $r)");
            cmd.Transaction = tx;
            foreach (var (name, role) in boxes)
            {
                cmd.Parameters.Clear();
                Add(cmd, ("$a", account), ("$n", name), ("$r", role));
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public List<(string Name, string? Role)> LoadMailboxes(string account)
    {
        lock (_gate)
        {
            using var cmd = Cmd("SELECT name, role FROM mailboxes WHERE account = $a ORDER BY name", ("$a", account));
            using var r = cmd.ExecuteReader();
            var list = new List<(string, string?)>();
            while (r.Read()) list.Add((r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1)));
            return list;
        }
    }

    // MARK: Preferences (small JSON values)

    public T? GetPref<T>(string key)
    {
        lock (_gate)
        {
            using var cmd = Cmd("SELECT value FROM prefs WHERE key = $k", ("$k", key));
            return cmd.ExecuteScalar() is string s ? JsonSerializer.Deserialize<T>(s, Json) : default;
        }
    }

    public void SetPref<T>(string key, T value)
    {
        lock (_gate)
        {
            using var cmd = Cmd("INSERT OR REPLACE INTO prefs(key, value) VALUES ($k, $v)",
                                ("$k", key), ("$v", JsonSerializer.Serialize(value, Json)));
            cmd.ExecuteNonQuery();
        }
    }

    // MARK: Helpers

    private void Exec(string sql)
    {
        lock (_gate)
        {
            using var cmd = Cmd(sql);
            cmd.ExecuteNonQuery();
        }
    }

    private SqliteCommand Cmd(string sql, params (string, object?)[] args)
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        Add(cmd, args);
        return cmd;
    }

    private static void Add(SqliteCommand cmd, params (string Name, object? Value)[] args)
    {
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }
}
