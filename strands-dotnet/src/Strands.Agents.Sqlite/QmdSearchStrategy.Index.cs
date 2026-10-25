using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Strands.Agents.Storage;

public sealed partial class QmdSearchStrategy
{
    private const string Format = "strands-qmd-lexical-unicode17-v1";
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.Ordinal) { "node_modules", "vendor", "dist", "build" };
    private const string JsWhitespace = @"[\u0009-\u000d\u0020\u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000\ufeff]";
    private const string LineStart = @"(?:\A|(?<=[\r\n\u2028\u2029]))";
    private const string TitleText = @"([^\r\n\u2028\u2029]+)(?=\z|[\r\n\u2028\u2029])";
    private static readonly Regex MarkdownTitle = new(LineStart + "##?" + JsWhitespace + "+" + TitleText, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex MarkdownSecondTitle = new(LineStart + "##" + JsWhitespace + "+" + TitleText, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex OrgTitle = new(LineStart + @"#\+[Tt][Ii][Tt][Ll][Ee]:" + JsWhitespace + "*" + TitleText, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex OrgHeading = new(LineStart + @"\*+" + JsWhitespace + "+" + TitleText, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex Extension = new(@"\.[^.]+$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private SqliteConnection Connection(string root)
    {
        if (_connection is not null && _storageRoot == root) return _connection;
        CloseConnection();
        CheckLink(root);
        Directory.CreateDirectory(root);
        CheckLink(root);
        var path = DatabasePath(root);
        if (path != ":memory:") CheckLink(path);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, DefaultTimeout = 5 }.ToString());
        try
        {
            connection.Open();
            using (var tables = connection.CreateCommand())
            {
                tables.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
                using var reader = tables.ExecuteReader();
                var names = new List<string>();
                while (reader.Read()) names.Add(reader.GetString(0));
                if (names.Count != 0 && !names.Contains("qmd_format", StringComparer.Ordinal))
                    throw new InvalidDataException("The selected database is not a native Strands QMD index.");
            }
            Transaction(connection, transaction =>
            {
                Execute(connection, transaction, "CREATE TABLE IF NOT EXISTS qmd_format (value TEXT NOT NULL)");
                using var version = connection.CreateCommand();
                version.Transaction = transaction;
                version.CommandText = "SELECT value FROM qmd_format";
                var values = new List<string>();
                using (var reader = version.ExecuteReader()) while (reader.Read()) values.Add(reader.GetString(0));
                if (values.Count == 0) Execute(connection, transaction, "INSERT INTO qmd_format(value) VALUES($value)", ("$value", Format));
                else if (values.Count != 1 || values[0] != Format) throw new InvalidDataException("The selected native QMD index version is unsupported.");
                Execute(connection, transaction, "CREATE TABLE IF NOT EXISTS qmd_documents (id INTEGER PRIMARY KEY, key TEXT UNIQUE NOT NULL, hash TEXT NOT NULL, active INTEGER NOT NULL)");
                Execute(connection, transaction, "CREATE VIRTUAL TABLE IF NOT EXISTS qmd_fts USING fts5(filepath,title,body,tokenize='porter unicode61')");
            });
        }
        catch (Exception error)
        {
            try { connection.Dispose(); }
            catch (Exception cleanup) { AttachCleanup(error, cleanup); }
            throw;
        }
        _connection = connection;
        _storageRoot = root;
        return connection;
    }

    private string DatabasePath(string root)
    {
        if (_databasePath is { } configured)
        {
            if (configured.Length == 0) throw new ArgumentException("The QMD database path cannot be empty.", nameof(QmdSearchStrategyOptions.DatabasePath));
            return configured == ":memory:" ? configured : Path.GetFullPath(configured);
        }
        var trimmed = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var isRoot = trimmed.Length < (Path.GetPathRoot(root)?.Length ?? 0);
        var basename = isRoot ? "" : Path.GetFileName(trimmed);
        var parent = isRoot ? root : Path.GetDirectoryName(trimmed) ?? root;
        return Path.Combine(parent, "." + basename + "-qmd.sqlite");
    }

    private void CloseConnection()
    {
        var connection = _connection;
        _connection = null;
        _storageRoot = null;
        connection?.Dispose();
    }

    private static void Refresh(SqliteConnection connection, string root, CancellationToken cancellationToken)
    {
        CheckLink(root);
        var files = new List<(string Path, string Key)>();
        Walk(root, "", files, cancellationToken);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        Transaction(connection, transaction =>
        {
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CheckLink(file.Path);
                seen.Add(file.Key);
                string content;
                try { content = Encoding.UTF8.GetString(File.ReadAllBytes(file.Path)); }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                if (QmdUnicode17.Trim(content).Length == 0) continue;
                cancellationToken.ThrowIfCancellationRequested();
                Upsert(connection, transaction, file.Key, content);
            }
            using var lookup = connection.CreateCommand();
            lookup.Transaction = transaction;
            lookup.CommandText = "SELECT id,key FROM qmd_documents WHERE active=1";
            var removed = new List<long>();
            using (var reader = lookup.ExecuteReader()) while (reader.Read()) if (!seen.Contains(reader.GetString(1))) removed.Add(reader.GetInt64(0));
            foreach (var id in removed)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Execute(connection, transaction, "DELETE FROM qmd_fts WHERE rowid=$id", ("$id", id));
                Execute(connection, transaction, "UPDATE qmd_documents SET active=0 WHERE id=$id", ("$id", id));
            }
            cancellationToken.ThrowIfCancellationRequested();
        });
    }

    private static void Walk(string directory, string prefix, List<(string Path, string Key)> files, CancellationToken cancellationToken)
    {
        CheckLink(directory);
        foreach (var path in Directory.GetFileSystemEntries(directory).OrderBy(value => value, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(path);
            if (name.StartsWith(".", StringComparison.Ordinal)) continue;
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new StorageException("QMD does not follow symbolic links or reparse points within its selected root.");
            if ((attributes & FileAttributes.Directory) != 0)
            {
                if (!ExcludedDirectories.Contains(name)) Walk(path, prefix + name + "/", files, cancellationToken);
            }
            else files.Add((path, prefix + name));
        }
    }

    private static void CheckLink(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new StorageException("QMD does not follow symbolic links or reparse points within its selected root.");
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    private static void Upsert(SqliteConnection connection, SqliteTransaction transaction, string key, string content)
    {
        using var fingerprint = SHA256.Create();
        var hash = BitConverter.ToString(fingerprint.ComputeHash(Encoding.UTF8.GetBytes(content))).Replace("-", "");
        long id = 0;
        using (var lookup = connection.CreateCommand())
        {
            lookup.Transaction = transaction;
            lookup.CommandText = "SELECT id,hash,active FROM qmd_documents WHERE key=$key";
            lookup.Parameters.AddWithValue("$key", key);
            using var reader = lookup.ExecuteReader();
            if (reader.Read())
            {
                id = reader.GetInt64(0);
                if (reader.GetString(1) == hash && reader.GetInt32(2) == 1) return;
            }
        }
        if (id == 0)
        {
            Execute(connection, transaction, "INSERT INTO qmd_documents(key,hash,active) VALUES($key,$hash,1)", ("$key", key), ("$hash", hash));
            using var identity = connection.CreateCommand();
            identity.Transaction = transaction;
            identity.CommandText = "SELECT last_insert_rowid()";
            id = (long)identity.ExecuteScalar()!;
        }
        else Execute(connection, transaction, "UPDATE qmd_documents SET hash=$hash,active=1 WHERE id=$id", ("$hash", hash), ("$id", id));
        Execute(connection, transaction, "DELETE FROM qmd_fts WHERE rowid=$id", ("$id", id));
        Execute(connection, transaction, "INSERT INTO qmd_fts(rowid,filepath,title,body) VALUES($id,$path,$title,$body)",
            ("$id", id), ("$path", QmdUnicode17.NormalizeCjk("storage/" + key)),
            ("$title", QmdUnicode17.NormalizeCjk(Title(content, key))), ("$body", QmdUnicode17.NormalizeCjk(content)));
    }

    internal static string Title(string content, string key)
    {
        var extension = QmdUnicode17.Lower(Path.GetExtension(key));
        if (extension == ".md")
        {
            var match = MarkdownTitle.Match(content);
            if (match.Success)
            {
                var title = QmdUnicode17.Trim(match.Groups[1].Value);
                if (title is "Notes" or "\U0001f4dd Notes")
                {
                    var second = MarkdownSecondTitle.Match(content);
                    if (second.Success) title = QmdUnicode17.Trim(second.Groups[1].Value);
                }
                if (title.Length != 0) return title;
            }
        }
        else if (extension == ".org")
        {
            var match = OrgTitle.Match(content);
            if (!match.Success) match = OrgHeading.Match(content);
            if (match.Success)
            {
                var title = QmdUnicode17.Trim(match.Groups[1].Value);
                if (title.Length != 0) return title;
            }
        }
        var pieces = Extension.Replace(key, "").Split('/');
        return pieces[pieces.Length - 1] is { Length: > 0 } name ? name : key;
    }

    private static string? BuildMatch(string query)
    {
        var terms = QmdUnicode17.Tokens(query);
        if (terms.Count == 0) return null;
        return string.Join(" AND ", terms.Select(term =>
        {
            var normalized = QmdUnicode17.NormalizeCjk(term);
            return normalized == term ? "\"" + term + "\"*" : "\"" + QmdUnicode17.Trim(normalized) + "\"";
        }));
    }

    private static IReadOnlyList<StorageSearchResult> Query(SqliteConnection connection, string match)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT d.key,bm25(qmd_fts,1.5,4.0,1.0) AS rank FROM qmd_fts JOIN qmd_documents d ON d.id=qmd_fts.rowid WHERE qmd_fts MATCH $match AND d.active=1 ORDER BY rank ASC LIMIT 20";
        command.Parameters.AddWithValue("$match", match);
        using var reader = command.ExecuteReader();
        var results = new List<StorageSearchResult>();
        while (reader.Read())
        {
            var raw = Math.Abs(reader.GetDouble(1));
            var engine = raw / (1 + raw);
            results.Add(new(reader.GetString(0), engine / (1 + engine)));
        }
        return results.ToArray();
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string text, params (string Key, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = text;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Key, parameter.Value);
        command.ExecuteNonQuery();
    }

    private static void Transaction(SqliteConnection connection, Action<SqliteTransaction> action)
    {
        var transaction = connection.BeginTransaction();
        Exception? primary = null;
        try { action(transaction); transaction.Commit(); }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            try { transaction.Dispose(); }
            catch (Exception cleanup) when (primary is not null) { AttachCleanup(primary, cleanup); }
        }
    }

    private static void AttachCleanup(Exception primary, Exception cleanup)
    {
        if (ReferenceEquals(primary, cleanup)) return;
        if (primary.Data["Strands.CleanupErrors"] is not List<Exception> errors) primary.Data["Strands.CleanupErrors"] = errors = [];
        if (!errors.Any(error => ReferenceEquals(error, cleanup))) errors.Add(cleanup);
    }
}
