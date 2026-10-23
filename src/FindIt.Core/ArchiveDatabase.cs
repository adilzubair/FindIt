using Microsoft.Data.Sqlite;

namespace FindIt.Core;

internal static class ArchiveDatabase
{
    public static SqliteConnection Open(string path, bool readOnly = false)
    {
        // The standard Windows VFS can reject an otherwise valid catalog path when
        // its rollback-journal suffix crosses MAX_PATH. Keep SQLite's file locking
        // while using its built-in long-path implementation, including ATTACHes.
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            Vfs = OperatingSystem.IsWindows() ? "win32-longpath" : null
        }.ToString());
        try
        {
            connection.Open();
            Execute(connection, "PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON; PRAGMA cache_size=-32768;");
            if (!readOnly) Execute(connection, "PRAGMA journal_mode=DELETE; PRAGMA synchronous=FULL;");
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }
    public static void Execute(SqliteConnection db, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = db.CreateCommand(); command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }
    public static bool InTransaction(SqliteConnection db) => SQLitePCL.raw.sqlite3_get_autocommit(db.Handle) == 0;
    public static object? Scalar(SqliteConnection db, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = db.CreateCommand(); command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command.ExecuteScalar();
    }
    public static void InitializeCatalog(SqliteConnection db)
    {
        Execute(db, """
        CREATE TABLE IF NOT EXISTS files(id TEXT PRIMARY KEY, path TEXT NOT NULL UNIQUE COLLATE NOCASE, kind INTEGER NOT NULL, bytes INTEGER NOT NULL, modified TEXT NOT NULL, hash TEXT NOT NULL DEFAULT '', seen INTEGER NOT NULL DEFAULT 0,processor_fingerprint TEXT NOT NULL DEFAULT '');
        CREATE INDEX IF NOT EXISTS files_modified_path ON files(modified DESC,path);
        CREATE INDEX IF NOT EXISTS files_kind_modified ON files(kind,modified DESC,path);
        CREATE TABLE IF NOT EXISTS segments(id INTEGER PRIMARY KEY AUTOINCREMENT,file_id TEXT NOT NULL REFERENCES files(id) ON DELETE CASCADE,kind INTEGER NOT NULL,text TEXT NOT NULL DEFAULT '',start_ms INTEGER,end_ms INTEGER,page INTEGER,thumbnail TEXT,vector_path TEXT,vector_offset INTEGER,vector_length INTEGER);
        CREATE INDEX IF NOT EXISTS segments_file ON segments(file_id,start_ms);
        CREATE VIRTUAL TABLE IF NOT EXISTS search_fts USING fts5(file_id UNINDEXED,segment_id UNINDEXED,name,path,text,tokenize='unicode61 remove_diacritics 2');
        CREATE TABLE IF NOT EXISTS observations(id INTEGER PRIMARY KEY AUTOINCREMENT,segment_id INTEGER NOT NULL REFERENCES segments(id) ON DELETE CASCADE,embedding BLOB NOT NULL,x REAL NOT NULL,y REAL NOT NULL,width REAL NOT NULL,height REAL NOT NULL,quality REAL NOT NULL,portrait TEXT);
        CREATE INDEX IF NOT EXISTS observations_segment ON observations(segment_id);
        CREATE TABLE IF NOT EXISTS completed(path TEXT PRIMARY KEY COLLATE NOCASE);
        CREATE TABLE IF NOT EXISTS issues(path TEXT NOT NULL,stage TEXT NOT NULL,message TEXT NOT NULL);
        """);
        using var command = db.CreateCommand(); command.CommandText = "PRAGMA table_info(files);";
        using var reader = command.ExecuteReader(); bool found = false;
        while (reader.Read()) if (reader.GetString(1) == "processor_fingerprint") found = true;
        reader.Close();
        if (!found) Execute(db, "ALTER TABLE files ADD COLUMN processor_fingerprint TEXT NOT NULL DEFAULT ''; ");
        using var segmentInfo = db.CreateCommand(); segmentInfo.CommandText = "PRAGMA table_info(segments);";
        using var segmentReader = segmentInfo.ExecuteReader(); bool hasEmbeddingPolicy = false;
        while (segmentReader.Read()) if (segmentReader.GetString(1) == "skip_embedding") hasEmbeddingPolicy = true;
        segmentReader.Close();
        if (!hasEmbeddingPolicy) Execute(db, "ALTER TABLE segments ADD COLUMN skip_embedding INTEGER NOT NULL DEFAULT 0;");
    }
    public static void InitializeOverlay(SqliteConnection db) => Execute(db, """
        CREATE TABLE IF NOT EXISTS people(id TEXT PRIMARY KEY,name TEXT NOT NULL,aliases TEXT NOT NULL DEFAULT '[]',portrait TEXT,confirmed INTEGER NOT NULL DEFAULT 1);
        CREATE TABLE IF NOT EXISTS person_references(id INTEGER PRIMARY KEY AUTOINCREMENT,person_id TEXT NOT NULL REFERENCES people(id) ON DELETE CASCADE,embedding BLOB NOT NULL,fingerprint TEXT NOT NULL DEFAULT '');
        CREATE TABLE IF NOT EXISTS assignments(observation_id INTEGER PRIMARY KEY,person_id TEXT NOT NULL REFERENCES people(id) ON DELETE CASCADE,confirmed INTEGER NOT NULL DEFAULT 0,rejected INTEGER NOT NULL DEFAULT 0,score REAL NOT NULL DEFAULT 1);
        CREATE TABLE IF NOT EXISTS rejections(observation_id INTEGER NOT NULL,person_id TEXT NOT NULL REFERENCES people(id) ON DELETE CASCADE,PRIMARY KEY(observation_id,person_id));
        CREATE INDEX IF NOT EXISTS assignments_person ON assignments(person_id);
        CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY,value TEXT NOT NULL);
        INSERT OR IGNORE INTO settings(key,value) VALUES('revision','0');
        INSERT OR IGNORE INTO settings(key,value) VALUES('observation_sequence','0');
        """);
}
