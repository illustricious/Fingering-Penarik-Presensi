// Membuka database SQLite/SQLCipher: tanpa kunci = SQLite biasa (hanya untuk tes dan migrasi dari DB lama),
// dengan kunci 256-bit (x'hex') atau frasa sandi (pencadangan) = terenkripsi AES-256 (SQLCipher 4).
using Microsoft.Data.Sqlite;

namespace Fingering.Core;

public sealed class WrongKeyException(string message) : Exception(message);

public static class SecureDb
{
    static SecureDb() => SQLitePCL.Batteries_V2.Init();

    /// <summary>Literal kunci mentah SQLCipher: "x'64 digit hex'" (tanpa PBKDF2 tambahan; kunci sudah acak 256 bit).</summary>
    public static string RawKey(byte[] key) => "\"x'" + Convert.ToHexString(key) + "'\"";

    /// <summary>Literal frasa sandi: SQLCipher menurunkan kunci dengan PBKDF2-HMAC-SHA512 (256.000 putaran, salt per berkas).</summary>
    public static string Passphrase(string pass) => "'" + pass.Replace("'", "''") + "'";

    static string Quote(string s) => "'" + s.Replace("'", "''") + "'";

    /// <param name="keyLiteral">hasil RawKey/Passphrase, atau null = tanpa enkripsi.</param>
    public static SqliteConnection Open(string path, string? keyLiteral)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        c.Open();
        try
        {
            if (keyLiteral != null) Run(c, "PRAGMA key = " + keyLiteral);
            Run(c, "SELECT count(*) FROM sqlite_master");   // uji kunci: gagal bila salah
        }
        catch (SqliteException e) when (e.SqliteErrorCode == 26)
        {
            c.Dispose();
            throw new WrongKeyException("kunci/sandi salah, atau berkas bukan database Fingering");
        }
        catch { c.Dispose(); throw; }
        return c;
    }

    public static SqliteConnection Open(string path, byte[]? key) => Open(path, key == null ? null : RawKey(key));

    static void Run(SqliteConnection c, string sql) { using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }

    /// <summary>
    /// Salin SELURUH isi database (skema + data) ke berkas baru yang dienkripsi dengan destKeyLiteral (null = tanpa enkripsi),
    /// lewat sqlcipher_export. Berkas tujuan tidak boleh sudah ada. Sumber tidak diubah.
    /// </summary>
    public static void ExportTo(SqliteConnection src, string destPath, string? destKeyLiteral)
    {
        if (File.Exists(destPath)) throw new IOException("berkas tujuan sudah ada: " + destPath);
        Run(src, $"ATTACH DATABASE {Quote(destPath)} AS tgt KEY {destKeyLiteral ?? "''"}");
        try
        {
            using var c = src.CreateCommand();
            c.CommandText = "SELECT sqlcipher_export('tgt')";
            c.ExecuteScalar();
        }
        finally { Run(src, "DETACH DATABASE tgt"); }
    }

    /// <summary>Apakah berkas diawali tanda tangan "SQLite format 3" (= TIDAK terenkripsi).</summary>
    public static bool LooksPlaintext(string path)
    {
        if (!File.Exists(path)) return false;
        using var f = File.OpenRead(path);
        var b = new byte[16];
        return f.Read(b, 0, 16) == 16 && System.Text.Encoding.ASCII.GetString(b).StartsWith("SQLite format 3");
    }
}
