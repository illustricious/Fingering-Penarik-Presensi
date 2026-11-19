// Migrasi database lama (SQLite polos: presensi.db + dump.db) ke database terenkripsi. Isi disalin utuh dengan sqlcipher_export,
// lalu jumlah baris SETIAP tabel dibandingkan; berkas lama tidak disentuh (kecuali dibaca) sampai pemanggil memintanya dihapus.
using Microsoft.Data.Sqlite;

namespace Fingering.Core;

public sealed record MigrationReport(int Tables, long Rows, int Punches, int Employees, int Devices);

public static class Migrator
{
    /// <summary>Cari DB lama yang masih polos di folder-folder kandidat; null bila tidak ada.</summary>
    public static string? FindLegacy(IEnumerable<string> dirs) =>
        dirs.Select(d => Path.Combine(d, "presensi.db")).FirstOrDefault(SecureDb.LooksPlaintext);

    static List<string> Tables(SqliteConnection c)
    {
        using var q = c.CreateCommand();
        q.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
        using var r = q.ExecuteReader();
        var l = new List<string>();
        while (r.Read()) l.Add(r.GetString(0));
        return l;
    }

    static long Count(SqliteConnection c, string table)
    {
        using var q = c.CreateCommand();
        q.CommandText = $"SELECT count(*) FROM \"{table.Replace("\"", "\"\"")}\"";
        return Convert.ToInt64(q.ExecuteScalar());
    }

    /// <summary>Salin satu DB polos ke berkas terenkripsi baru dan verifikasi jumlah baris tiap tabel.</summary>
    static (int tables, long rows) Copy(string plain, string dest, byte[] key)
    {
        if (File.Exists(dest)) File.Delete(dest);
        using (var src = SecureDb.Open(plain, (string?)null)) SecureDb.ExportTo(src, dest, SecureDb.RawKey(key));
        using var a = SecureDb.Open(plain, (string?)null);
        using var b = SecureDb.Open(dest, key);
        long rows = 0; var ta = Tables(a); var tb = Tables(b);
        if (!ta.SequenceEqual(tb)) throw new InvalidDataException("verifikasi migrasi gagal: daftar tabel berbeda");
        foreach (var t in ta)
        {
            long x = Count(a, t), y = Count(b, t);
            if (x != y) throw new InvalidDataException($"verifikasi migrasi gagal: tabel {t} berisi {x} baris, hasil enkripsi {y}");
            rows += x;
        }
        return (ta.Count, rows);
    }

    /// <summary>Migrasi presensi (wajib) dan dump (bila ada). Gagal di tengah jalan = berkas tujuan dihapus lagi, DB lama utuh.</summary>
    public static MigrationReport Migrate(string legacyDb, string? legacyDump, string destDb, string destDump, byte[] key)
    {
        try
        {
            var (t, rows) = Copy(legacyDb, destDb, key);
            if (legacyDump != null && File.Exists(legacyDump) && SecureDb.LooksPlaintext(legacyDump)) { var d = Copy(legacyDump, destDump, key); t += d.tables; rows += d.rows; }
            using var c = SecureDb.Open(destDb, key);
            long One(string sql) { using var q = c.CreateCommand(); q.CommandText = sql; return Convert.ToInt64(q.ExecuteScalar()); }
            return new MigrationReport(t, rows, (int)One("SELECT count(*) FROM punch"), (int)One("SELECT count(*) FROM employee"), (int)One("SELECT count(*) FROM device"));
        }
        catch
        {
            foreach (var f in new[] { destDb, destDump }) foreach (var s in new[] { "", "-wal", "-shm" }) try { File.Delete(f + s); } catch (IOException) { }
            throw;
        }
    }

    /// <summary>Hapus berkas DB lama: ditimpa nol lalu dihapus (upaya terbaik; pada SSD sisa data tidak bisa dijamin hilang).</summary>
    public static void WipeDelete(string path)
    {
        foreach (var f in new[] { path, path + "-wal", path + "-shm" })
        {
            if (!File.Exists(f)) continue;
            try
            {
                var len = new FileInfo(f).Length;
                using (var o = new FileStream(f, FileMode.Open, FileAccess.Write))
                {
                    var z = new byte[81920]; long left = len;
                    while (left > 0) { var n = (int)Math.Min(z.Length, left); o.Write(z, 0, n); left -= n; }
                    o.Flush(true);
                }
            }
            catch (IOException) { }
            File.Delete(f);
        }
    }
}
