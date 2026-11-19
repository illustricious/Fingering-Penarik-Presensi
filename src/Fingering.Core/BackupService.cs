// Backup & restore terenkripsi. Berkas backup (.fingbak) = kontainer buatan sendiri (magic "FINGBAK1") berisi dua database
// SQLCipher (presensi + data mentah mesin) yang masing-masing dienkripsi AES-256 dengan frasa sandi backup (PBKDF2-HMAC-SHA512,
// 256.000 putaran, salt acak per berkas). Frasa sandi backup terpisah dari sandi login, jadi backup bisa dipulihkan di PC lain
// (kunci data PC lain berbeda; saat restore isinya dienkripsi ulang dengan kunci data PC tujuan). Tidak ada data polos yang menyentuh disk.
using System.Text;
using System.Text.Json;

namespace Fingering.Core;

public sealed record BackupInfo(string CreatedAt, string AppVersion, int Devices, int Employees, int Punches, string First, string Last, long Bytes);

public static class BackupService
{
    public const string Extension = ".fingbak";
    static readonly byte[] Magic = "FINGBAK1"u8.ToArray();
    const string EntryPresensi = "presensi.fdb", EntryDump = "dump.fdb", EntryInfo = "info.json";

    public static string SuggestedName(DateTime at) => $"Fingering-backup-{at:yyyyMMdd-HHmm}{Extension}";

    public static void CheckPassphrase(string pass)
    {
        if (pass.Length < 8) throw new InvalidOperationException("frasa sandi backup minimal 8 karakter");
    }

    /// <summary>Buat backup terenkripsi dari DB yang sedang dipakai. Tidak mengubah DB asli.</summary>
    public static BackupInfo Create(Store store, DumpStore dump, string file, string passphrase)
    {
        CheckPassphrase(passphrase);
        if (File.Exists(file)) throw new IOException("berkas backup sudah ada: " + file);
        var tmp = Path.Combine(Path.GetTempPath(), "fingering-bak-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var p = Path.Combine(tmp, EntryPresensi); var d = Path.Combine(tmp, EntryDump);
            store.ExportWithPassphrase(p, passphrase);
            dump.ExportWithPassphrase(d, passphrase);
            var s = store.Stats();
            var info = new BackupInfo(DateTime.Now.ToString(Store.TimeFmt), typeof(BackupService).Assembly.GetName().Version?.ToString(3) ?? "",
                s.Devices, s.Employees, s.Punches, s.First, s.Last, 0);
            var infoBytes = JsonSerializer.SerializeToUtf8Bytes(info);
            var part = file + ".tmp";
            using (var o = File.Create(part))
            using (var w = new BinaryWriter(o, Encoding.UTF8, true))
            {
                w.Write(Magic);
                w.Write(3);
                WriteEntry(w, EntryInfo, infoBytes);
                WriteEntry(w, EntryPresensi, File.OpenRead(p));
                WriteEntry(w, EntryDump, File.OpenRead(d));
            }
            File.Move(part, file);
            return info with { Bytes = new FileInfo(file).Length };
        }
        finally { try { Directory.Delete(tmp, true); } catch (IOException) { } }
    }

    static void WriteEntry(BinaryWriter w, string name, byte[] data)
    {
        w.Write(name); w.Write((long)data.Length); w.Write(data);
    }

    static void WriteEntry(BinaryWriter w, string name, Stream s)
    {
        using (s) { w.Write(name); w.Write(s.Length); w.Flush(); s.CopyTo(w.BaseStream); }
    }

    /// <summary>Baca isi kontainer ke folder kerja. Hanya tiga nama berkas tetap yang diterima (tidak ada path dari berkas backup).</summary>
    static Dictionary<string, string> Extract(string file, string workDir)
    {
        var map = new Dictionary<string, string>();
        using var f = File.OpenRead(file);
        using var r = new BinaryReader(f, Encoding.UTF8, true);
        if (f.Length < 12 || !r.ReadBytes(8).AsSpan().SequenceEqual(Magic)) throw new InvalidDataException("bukan berkas backup Fingering (.fingbak)");
        int n = r.ReadInt32();
        if (n is < 1 or > 8) throw new InvalidDataException("kontainer backup rusak");
        for (int i = 0; i < n; i++)
        {
            var name = r.ReadString();
            long len = r.ReadInt64();
            if (len < 0 || len > f.Length - f.Position) throw new InvalidDataException("kontainer backup terpotong");
            if (name is not (EntryPresensi or EntryDump or EntryInfo)) { f.Seek(len, SeekOrigin.Current); continue; }
            var path = Path.Combine(workDir, name);
            using (var o = File.Create(path))
            {
                var buf = new byte[81920]; long left = len;
                while (left > 0) { int k = f.Read(buf, 0, (int)Math.Min(buf.Length, left)); if (k <= 0) throw new InvalidDataException("kontainer backup terpotong"); o.Write(buf, 0, k); left -= k; }
            }
            map[name] = path;
        }
        if (!map.ContainsKey(EntryPresensi) || !map.ContainsKey(EntryDump)) throw new InvalidDataException("isi backup tidak lengkap");
        return map;
    }

    static string NewWork()
    {
        var d = Path.Combine(Path.GetTempPath(), "fingering-rst-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    static void Cleanup(string dir) { try { Directory.Delete(dir, true); } catch (IOException) { } }

    /// <summary>Periksa berkas backup + frasa sandi tanpa menulis apa pun ke DB. Melempar WrongKeyException bila frasa salah.</summary>
    public static BackupInfo Inspect(string file, string passphrase)
    {
        var work = NewWork();
        try
        {
            var m = Extract(file, work);
            using var c = SecureDb.Open(m[EntryPresensi], SecureDb.Passphrase(passphrase));
            long Count(string sql) { using var q = c.CreateCommand(); q.CommandText = sql; return Convert.ToInt64(q.ExecuteScalar()); }
            string Text(string sql) { using var q = c.CreateCommand(); q.CommandText = sql; return q.ExecuteScalar() as string ?? ""; }
            if (Count("SELECT count(*) FROM sqlite_master WHERE name='schema_version'") == 0) throw new InvalidDataException("backup bukan database Fingering");
            return new BackupInfo(
                m.TryGetValue(EntryInfo, out var ip) ? JsonSerializer.Deserialize<BackupInfo>(File.ReadAllBytes(ip))?.CreatedAt ?? "" : "",
                "", (int)Count("SELECT count(*) FROM device"), (int)Count("SELECT count(*) FROM employee"), (int)Count("SELECT count(*) FROM punch"),
                Text("SELECT min(punched_at) FROM punch"), Text("SELECT max(punched_at) FROM punch"), new FileInfo(file).Length);
        }
        finally { Cleanup(work); }
    }

    /// <summary>
    /// Siapkan restore: dekripsi backup dengan frasa sandi, enkripsi ulang dengan kunci data PC ini, simpan sebagai
    /// destPresensi/destDump (berkas baru, belum dipakai). Jumlah baris diverifikasi. Tidak menyentuh DB yang sedang berjalan.
    /// </summary>
    public static BackupInfo Stage(string file, string passphrase, byte[] dataKey, string destPresensi, string destDump)
    {
        var info = Inspect(file, passphrase);   // frasa salah / bukan backup -> gagal di sini
        var work = NewWork();
        try
        {
            var m = Extract(file, work);
            foreach (var (entry, dest) in new[] { (EntryPresensi, destPresensi), (EntryDump, destDump) })
            {
                if (File.Exists(dest)) File.Delete(dest);
                using var c = SecureDb.Open(m[entry], SecureDb.Passphrase(passphrase));
                SecureDb.ExportTo(c, dest, SecureDb.RawKey(dataKey));
            }
            using var v = SecureDb.Open(destPresensi, dataKey);
            using var q = v.CreateCommand();
            q.CommandText = "SELECT (SELECT count(*) FROM punch),(SELECT count(*) FROM employee)";
            using var rd = q.ExecuteReader(); rd.Read();
            if (rd.GetInt32(0) != info.Punches || rd.GetInt32(1) != info.Employees) throw new InvalidDataException("verifikasi restore gagal: jumlah baris tidak sama");
            return info;
        }
        catch { foreach (var d in new[] { destPresensi, destDump }) try { File.Delete(d); } catch (IOException) { } throw; }
        finally { Cleanup(work); }
    }
}
