using Microsoft.Data.Sqlite;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace LocalCpuEmbeddings;

internal sealed record SearchHit(long Id, double Score, string Content, string Topic);
internal sealed class MemoryStore : IDisposable
{
    private readonly SqliteConnection _connection;
    private static readonly HashSet<string> QueryStopWords = new("a an and are as at be because been but by can could did do does for from had has have how i if in into is it its me my not of on or our should so than that the their them then there these they this those to us was we were what when where which who why will with would you your know".Split(' '));
    public MemoryStore(string path)
    {
        _connection = new SqliteConnection($"Data Source={path};Pooling=False");
        _connection.Open();
        Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;");
        Execute("""
            CREATE TABLE IF NOT EXISTS memory_items (
                id INTEGER PRIMARY KEY, source_type TEXT NOT NULL, source_reference TEXT NOT NULL,
                content TEXT NOT NULL, created_at TEXT NOT NULL, importance INTEGER NOT NULL,
                embedding_status TEXT NOT NULL DEFAULT 'pending', embedding_model TEXT,
                embedding_blob BLOB, embedded_at TEXT, topic TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS pending_items ON memory_items(embedding_status, id);
            CREATE VIRTUAL TABLE IF NOT EXISTS memory_fts USING fts5(content, content='memory_items', content_rowid='id');
            CREATE TRIGGER IF NOT EXISTS memory_insert AFTER INSERT ON memory_items BEGIN
                INSERT INTO memory_fts(rowid,content) VALUES(new.id,new.content);
            END;
            CREATE TRIGGER IF NOT EXISTS memory_content_update AFTER UPDATE OF content ON memory_items BEGIN
                INSERT INTO memory_fts(memory_fts,rowid,content) VALUES('delete',old.id,old.content);
                INSERT INTO memory_fts(rowid,content) VALUES(new.id,new.content);
                UPDATE memory_items SET embedding_status='pending',embedding_model=NULL,embedding_blob=NULL,embedded_at=NULL WHERE id=new.id;
            END;
            CREATE TRIGGER IF NOT EXISTS memory_delete AFTER DELETE ON memory_items BEGIN
                INSERT INTO memory_fts(memory_fts,rowid,content) VALUES('delete',old.id,old.content);
            END;
            """);
    }
    public void Ingest(IEnumerable<MemoryItem> items)
    {
        using var tx = _connection.BeginTransaction();
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO memory_items(id,source_type,source_reference,content,created_at,importance,topic) VALUES($id,$source,$ref,$content,$created,$importance,$topic)";
        foreach (var name in new[] { "$id", "$source", "$ref", "$content", "$created", "$importance", "$topic" }) cmd.Parameters.AddWithValue(name, "");
        cmd.Prepare();
        foreach (var item in items)
        {
            cmd.Parameters["$id"].Value = item.Id; cmd.Parameters["$source"].Value = item.SourceType;
            cmd.Parameters["$ref"].Value = item.SourceReference; cmd.Parameters["$content"].Value = item.Content;
            cmd.Parameters["$created"].Value = item.CreatedAt.ToString("O"); cmd.Parameters["$importance"].Value = item.Importance;
            cmd.Parameters["$topic"].Value = item.Topic; cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }
    public List<MemoryItem> Pending(int count, bool lengthOrder = false)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id,source_type,source_reference,content,created_at,importance,topic FROM memory_items WHERE embedding_status='pending' ORDER BY " + (lengthOrder ? "length(content),id" : "id") + " LIMIT $count";
        cmd.Parameters.AddWithValue("$count", count);
        using var r = cmd.ExecuteReader();
        List<MemoryItem> items = [];
        while (r.Read()) items.Add(new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), DateTimeOffset.Parse(r.GetString(4)), r.GetInt32(5), r.GetString(6)));
        return items;
    }
    public void SkipNoise(IEnumerable<MemoryItem> items)
    {
        using var tx = _connection.BeginTransaction();
        using var cmd = _connection.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "UPDATE memory_items SET embedding_status='skipped_noise' WHERE id=$id";
        cmd.Parameters.Add("$id", SqliteType.Integer);
        foreach (var item in items) { cmd.Parameters["$id"].Value = item.Id; cmd.ExecuteNonQuery(); }
        tx.Commit();
    }
    public void Save(IReadOnlyList<MemoryItem> items, float[][] vectors, string model)
    {
        using var tx = _connection.BeginTransaction();
        using var cmd = _connection.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "UPDATE memory_items SET embedding_status='ready',embedding_model=$model,embedding_blob=$blob,embedded_at=$at WHERE id=$id";
        cmd.Parameters.AddWithValue("$model", model); cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.Add("$blob", SqliteType.Blob); cmd.Parameters.Add("$id", SqliteType.Integer);
        for (int i = 0; i < items.Count; i++)
        {
            cmd.Parameters["$id"].Value = items[i].Id;
            cmd.Parameters["$blob"].Value = MemoryMarshal.AsBytes(vectors[i].AsSpan()).ToArray();
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }
    public List<SearchHit> Lexical(string query, int limit = 50)
    {
        // Remove generic English question/function words, then OR quoted terms. No model or reranker.
        var terms = Regex.Matches(query, @"[\p{L}\p{N}_]+").Select(x => x.Value.ToLowerInvariant()).Where(x => !QueryStopWords.Contains(x)).Distinct().ToArray();
        if (terms.Length == 0) return [];
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT m.id,bm25(memory_fts),m.content,m.topic FROM memory_fts JOIN memory_items m ON m.id=memory_fts.rowid WHERE memory_fts MATCH $q ORDER BY bm25(memory_fts),m.id LIMIT $limit";
        cmd.Parameters.AddWithValue("$q", string.Join(" OR ", terms.Select(x => '"' + x + '"')));
        cmd.Parameters.AddWithValue("$limit", limit);
        using var r = cmd.ExecuteReader(); List<SearchHit> hits = [];
        while (r.Read()) hits.Add(new(r.GetInt64(0), r.GetDouble(1), r.GetString(2), r.GetString(3)));
        return hits;
    }
    public List<SearchHit> Vector(float[] query, int limit = 50)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id,content,topic,embedding_blob FROM memory_items WHERE embedding_status='ready'";
        using var r = cmd.ExecuteReader(); List<SearchHit> hits = [];
        while (r.Read())
        {
            var vector = MemoryMarshal.Cast<byte, float>((byte[])r[3]);
            if (vector.Length != query.Length) throw new InvalidDataException("Vector dimension mismatch.");
            double score = 0;
            for (int d = 0; d < vector.Length; d++) score += vector[d] * query[d];
            hits.Add(new(r.GetInt64(0), score, r.GetString(1), r.GetString(2)));
        }
        return hits.OrderByDescending(x => x.Score).ThenBy(x => x.Id).Take(limit).ToList();
    }
    public static List<SearchHit> Hybrid(List<SearchHit> lexical, List<SearchHit> vector)
    {
        Dictionary<long, SearchHit> merged = [];
        foreach (var list in new[] { lexical, vector })
            for (int i = 0; i < list.Count; i++)
            {
                var hit = list[i]; double score = 1.0 / (60 + i + 1);
                merged[hit.Id] = hit with { Score = score + (merged.TryGetValue(hit.Id, out var old) ? old.Score : 0) };
            }
        return merged.Values.OrderByDescending(x => x.Score).ThenBy(x => x.Id).ToList();
    }
    public long Count(string status) { using var cmd = _connection.CreateCommand(); cmd.CommandText = "SELECT count(*) FROM memory_items WHERE embedding_status=$s"; cmd.Parameters.AddWithValue("$s", status); return (long)cmd.ExecuteScalar()!; }
    public void Checkpoint() => Execute("PRAGMA wal_checkpoint(TRUNCATE);");
    public void Execute(string sql) { using var cmd = _connection.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
    public void Dispose() => _connection.Dispose();
}
