// Data mentah dari mesin (info, sampel log, dll) disimpan dalam SATU berkas SQLite (dump.db), bukan ratusan berkas .bin.
using Microsoft.Data.Sqlite;

namespace Fingering.Core;

public sealed record DumpRow(long Id, string Device, string Name, string CreatedAt, int Size);

public sealed class DumpStore : IDisposable
{
    readonly SqliteConnection db;
    readonly object gate = new();

    public DumpStore(string path, byte[]? key = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        db = SecureDb.Open(path, key);
        Exec(@"PRAGMA journal_mode=WAL; PRAGMA busy_timeout=8000;
               CREATE TABLE IF NOT EXISTS dump (
                 id INTEGER PRIMARY KEY, device TEXT NOT NULL, name TEXT NOT NULL,
                 created_at TEXT NOT NULL, size INTEGER NOT NULL, data BLOB NOT NULL);");
    }

    void Exec(string sql) { lock (gate) { using var c = db.CreateCommand(); c.CommandText = sql; c.ExecuteNonQuery(); } }

    public long Add(string device, string name, byte[] data, DateTime? at = null)
    {
        lock (gate)
        {
            using var c = db.CreateCommand();
            c.CommandText = "INSERT INTO dump(device,name,created_at,size,data) VALUES (@d,@n,@t,@s,@x); SELECT last_insert_rowid();";
            c.Parameters.AddWithValue("@d", device);
            c.Parameters.AddWithValue("@n", name);
            c.Parameters.AddWithValue("@t", (at ?? DateTime.Now).ToString(Store.TimeFmt));
            c.Parameters.AddWithValue("@s", data.Length);
            c.Parameters.AddWithValue("@x", data);
            return Convert.ToInt64(c.ExecuteScalar());
        }
    }

    public List<DumpRow> List(int limit = 200)
    {
        lock (gate)
        {
            using var c = db.CreateCommand();
            c.CommandText = $"SELECT id,device,name,created_at,size FROM dump ORDER BY id DESC LIMIT {limit}";
            using var r = c.ExecuteReader();
            var l = new List<DumpRow>();
            while (r.Read()) l.Add(new DumpRow(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4)));
            return l;
        }
    }

    public byte[]? Get(long id)
    {
        lock (gate)
        {
            using var c = db.CreateCommand();
            c.CommandText = "SELECT data FROM dump WHERE id=@i";
            c.Parameters.AddWithValue("@i", id);
            return c.ExecuteScalar() as byte[];
        }
    }

    /// <summary>Memasukkan berkas .bin lama (format dumps/ versi awal) sekali saja; berkas asli tidak dihapus.</summary>
    public int ImportBinFolder(string dir)
    {
        if (!Directory.Exists(dir)) return 0;
        int n = 0;
        foreach (var f in Directory.GetFiles(dir, "*.bin"))
        {
            try
            {
                // pola: {mesin}_{yyyyMMdd-HHmmss}_{nama}.bin
                var parts = Path.GetFileNameWithoutExtension(f).Split('_');
                var dev = parts.Length >= 3 ? string.Join("_", parts[..^2]) : "?";
                var nm = parts.Length >= 3 ? parts[^1] : Path.GetFileNameWithoutExtension(f);
                Add(dev, nm, File.ReadAllBytes(f), File.GetLastWriteTime(f));
                n++;
            }
            catch (IOException) { }
        }
        return n;
    }

    public void ExportWithPassphrase(string file, string passphrase) { lock (gate) SecureDb.ExportTo(db, file, SecureDb.Passphrase(passphrase)); }

    public void Dispose() => db.Dispose();
}
