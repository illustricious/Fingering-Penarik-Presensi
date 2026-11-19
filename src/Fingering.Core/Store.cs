// Penyimpanan SQLite (skema sama dengan presensi-hub, jadi database hasil migrasi_backup.py bisa dipakai langsung).
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace Fingering.Core;

public sealed class Device
{
    public long Id { get; set; }
    public string Serial { get; set; } = "";
    public string Name { get; set; } = "";
    public string Model { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 5005;
    public string CommKey { get; set; } = "";
    public int MachineNo { get; set; } = 1;
    public bool Enabled { get; set; } = true;
    public string Layout { get; set; } = "auto";
    public int PullIntervalMin { get; set; }
    public string LastPullAt { get; set; } = "";
    public string LastError { get; set; } = "";
    public int PunchCount { get; set; }
}

public sealed record PunchRow(long Id, string Device, string Pin, string Name, string At, int Verify, int InOut, int WorkCode);
public sealed record Employee(long Id, string Pin, string Nip, string Name, int Status, string Shifts = "", string Group = "");
public sealed record RunRow(long Id, string Device, string Started, string Finished, string Status, int Fetched, int Inserted, string Error);
public sealed record Stats(int Devices, int Employees, int Punches, string First, string Last, int Today, int UnknownPins);

public sealed class PunchFilter
{
    public string From = "", To = "", Pin = "";
    public long DeviceId;
    public int Limit = 200, Offset;
}

public sealed class Store : IDisposable
{
    public const string TimeFmt = "yyyy-MM-dd HH:mm:ss";
    readonly SqliteConnection db;
    readonly object gate = new(); // satu koneksi dipakai bersama; serialkan akses

    readonly byte[]? key;

    /// <param name="key">kunci data 256-bit (dari Vault) = DB terenkripsi SQLCipher; null = SQLite biasa (tes / DB lama).</param>
    public Store(string path, byte[]? key = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        this.key = key;
        db = SecureDb.Open(path, key);
        Exec("PRAGMA busy_timeout=8000; PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA synchronous=NORMAL;");
        if (Scalar<long>("SELECT count(*) FROM sqlite_master WHERE type='table' AND name='schema_version'") == 0)
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("schema.sql")!;
            Exec(new StreamReader(s).ReadToEnd());
        }
        Exec(@"CREATE TABLE IF NOT EXISTS employee_template (
            id INTEGER PRIMARY KEY, pin TEXT NOT NULL, device_id INTEGER NOT NULL REFERENCES device(id),
            backup_no INTEGER NOT NULL, data BLOB NOT NULL, fetched_at TEXT NOT NULL DEFAULT (datetime('now')),
            UNIQUE (pin, device_id, backup_no));");
        // Format lama menimpa cadangan PIN yang sama dari mesin lain.
        if (Scalar<string>("SELECT sql FROM sqlite_master WHERE name='employee_template'").Contains("UNIQUE (pin, backup_no)", StringComparison.OrdinalIgnoreCase))
            Exec(@"BEGIN IMMEDIATE;
                ALTER TABLE employee_template RENAME TO employee_template_v1;
                CREATE TABLE employee_template (
                    id INTEGER PRIMARY KEY, pin TEXT NOT NULL, device_id INTEGER NOT NULL REFERENCES device(id),
                    backup_no INTEGER NOT NULL, data BLOB NOT NULL, fetched_at TEXT NOT NULL DEFAULT (datetime('now')),
                    UNIQUE (pin, device_id, backup_no));
                INSERT INTO employee_template SELECT * FROM employee_template_v1;
                DROP TABLE employee_template_v1;
                COMMIT;");
        if (Scalar<long>("SELECT count(*) FROM pragma_table_info('employee') WHERE name='shifts'") == 0)
            Exec("ALTER TABLE employee ADD COLUMN shifts TEXT NOT NULL DEFAULT ''");
        if (Scalar<long>("SELECT count(*) FROM pragma_table_info('employee') WHERE name='grup'") == 0)
            Exec("ALTER TABLE employee ADD COLUMN grup TEXT NOT NULL DEFAULT ''");
    }

    public void Dispose() => db.Dispose();

    /// <summary>Salinan utuh database ke berkas baru dengan kunci yang sama (terenkripsi bila Store terenkripsi); dipakai sebelum operasi tulis besar.</summary>
    public void BackupTo(string file) { lock (gate) SecureDb.ExportTo(db, file, key == null ? null : SecureDb.RawKey(key)); }

    /// <summary>Salinan utuh ke berkas baru yang dienkripsi dengan frasa sandi (untuk berkas backup yang bisa dipindah antar PC).</summary>
    public void ExportWithPassphrase(string file, string passphrase) { lock (gate) SecureDb.ExportTo(db, file, SecureDb.Passphrase(passphrase)); }

    // ---- pembantu ----
    int Exec(string sql, params (string, object?)[] p)
    {
        lock (gate) { using var c = Cmd(sql, p); return c.ExecuteNonQuery(); }
    }

    SqliteCommand Cmd(string sql, (string, object?)[] p)
    {
        var c = db.CreateCommand();
        c.CommandText = sql;
        foreach (var (k, v) in p) c.Parameters.AddWithValue(k, v ?? DBNull.Value);
        return c;
    }

    T Scalar<T>(string sql, params (string, object?)[] p)
    {
        lock (gate)
        {
            using var c = Cmd(sql, p);
            var v = c.ExecuteScalar();
            return v is null or DBNull ? default! : (T)Convert.ChangeType(v, typeof(T), CultureInfo.InvariantCulture);
        }
    }

    List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string, object?)[] p)
    {
        lock (gate)
        {
            using var c = Cmd(sql, p);
            using var r = c.ExecuteReader();
            var l = new List<T>();
            while (r.Read()) l.Add(map(r));
            return l;
        }
    }

    long InsertId(string sql, params (string, object?)[] p)
    {
        lock (gate) { using var c = Cmd(sql + "; SELECT last_insert_rowid();", p); return Convert.ToInt64(c.ExecuteScalar()); }
    }

    // ---- mesin ----
    const string DevCols = @"d.id,d.serial,d.name,d.model,d.host,d.port,d.comm_key,d.machine_no,d.enabled,d.layout,d.pull_interval_min,
        COALESCE(d.last_pull_at,''),d.last_error,(SELECT count(*) FROM punch p WHERE p.device_id=d.id)";

    static Device ReadDevice(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0), Serial = r.GetString(1), Name = r.GetString(2), Model = r.GetString(3), Host = r.GetString(4), Port = r.GetInt32(5),
        CommKey = r.GetString(6), MachineNo = r.GetInt32(7), Enabled = r.GetInt32(8) != 0, Layout = r.GetString(9), PullIntervalMin = r.GetInt32(10),
        LastPullAt = r.GetString(11), LastError = r.GetString(12), PunchCount = r.GetInt32(13),
    };

    public List<Device> Devices() => Query($"SELECT {DevCols} FROM device d ORDER BY d.enabled DESC, d.name, d.id", ReadDevice);

    public Device? GetDevice(long id) => Query($"SELECT {DevCols} FROM device d WHERE d.id=@id", ReadDevice, ("@id", id)).FirstOrDefault();

    public long SaveDevice(Device d)
    {
        d.Host = d.Host.Trim();
        if (d.Host == "") throw new InvalidOperationException("alamat IP / host wajib diisi");
        if (d.Serial.Trim() == "") d.Serial = $"{d.Host}:{d.Port}#{d.MachineNo}";
        if (d.Name.Trim() == "") d.Name = d.Host;
        if (d.Layout == "") d.Layout = "auto";
        (string, object?)[] p =
        [
            ("@s", d.Serial), ("@n", d.Name), ("@m", d.Model), ("@h", d.Host), ("@p", d.Port), ("@k", d.CommKey), ("@no", d.MachineNo),
            ("@e", d.Enabled ? 1 : 0), ("@l", d.Layout), ("@i", d.PullIntervalMin), ("@id", d.Id),
        ];
        if (d.Id == 0)
            return InsertId(@"INSERT INTO device(serial,name,protocol,model,host,port,comm_key,machine_no,enabled,transport,layout,pull_interval_min)
                VALUES (@s,@n,'fk',@m,@h,@p,@k,@no,@e,'tcp',@l,@i)", p);
        Exec("UPDATE device SET serial=@s,name=@n,model=@m,host=@h,port=@p,comm_key=@k,machine_no=@no,enabled=@e,layout=@l,pull_interval_min=@i WHERE id=@id", p);
        return d.Id;
    }

    public void DeleteDevice(long id)
    {
        var n = Scalar<long>("SELECT count(*) FROM punch WHERE device_id=@id", ("@id", id));
        if (n > 0) throw new InvalidOperationException($"mesin punya {n} log presensi; nonaktifkan saja, jangan dihapus");
        Exec("DELETE FROM device WHERE id=@id", ("@id", id));
    }

    // ---- tarik & log ----
    public long StartRun(long deviceId) =>
        InsertId("INSERT INTO sync_run(device_id,started_at) VALUES (@d,@t)", ("@d", deviceId), ("@t", DateTime.Now.ToString(TimeFmt)));

    public void FinishRun(long runId, long deviceId, int fetched, int inserted, string? error)
    {
        var now = DateTime.Now.ToString(TimeFmt);
        Exec("UPDATE sync_run SET finished_at=@t,status=@s,fetched=@f,inserted=@i,error=@e WHERE id=@id",
            ("@t", now), ("@s", error == null ? "ok" : "error"), ("@f", fetched), ("@i", inserted), ("@e", error ?? ""), ("@id", runId));
        if (error == null) Exec("UPDATE device SET last_pull_at=@t,last_error='' WHERE id=@d", ("@t", now), ("@d", deviceId));
        else Exec("UPDATE device SET last_error=@e WHERE id=@d", ("@e", error), ("@d", deviceId));
    }

    /// <summary>Menyimpan log; baris ganda diabaikan. Mengembalikan jumlah baris baru.</summary>
    public int InsertPunches(long deviceId, long runId, string source, IEnumerable<Punch> ps)
    {
        lock (gate)
        {
            using var tx = db.BeginTransaction();
            using var c = db.CreateCommand();
            c.Transaction = tx;
            c.CommandText = @"INSERT OR IGNORE INTO punch(device_id,pin,punched_at,verify_mode,inout_mode,work_code,source,sync_run_id)
                VALUES (@d,@p,@t,@v,@io,@w,@s,@r)";
            var names = new[] { "@d", "@p", "@t", "@v", "@io", "@w", "@s", "@r" };
            foreach (var n in names) c.Parameters.Add(new SqliteParameter(n, null));
            int added = 0;
            foreach (var p in ps)
            {
                c.Parameters["@d"].Value = deviceId; c.Parameters["@p"].Value = p.Pin;
                c.Parameters["@t"].Value = p.At.ToString(TimeFmt, CultureInfo.InvariantCulture);
                c.Parameters["@v"].Value = p.Verify; c.Parameters["@io"].Value = p.InOut; c.Parameters["@w"].Value = p.WorkCode;
                c.Parameters["@s"].Value = source; c.Parameters["@r"].Value = runId;
                added += c.ExecuteNonQuery();
            }
            tx.Commit();
            return added;
        }
    }

    /// <summary>Jumlah punch dalam daftar yang BELUM ada di database untuk mesin ini (0 = semuanya sudah tersimpan).</summary>
    public int CountMissing(long deviceId, IEnumerable<Punch> ps)
    {
        lock (gate)
        {
            using var c = db.CreateCommand();
            c.CommandText = "SELECT 1 FROM punch WHERE device_id=@d AND pin=@p AND punched_at=@t";
            var d = c.Parameters.AddWithValue("@d", deviceId);
            var pin = c.Parameters.AddWithValue("@p", "");
            var t = c.Parameters.AddWithValue("@t", "");
            int missing = 0;
            foreach (var p in ps)
            {
                pin.Value = p.Pin; t.Value = p.At.ToString(TimeFmt, CultureInfo.InvariantCulture);
                if (c.ExecuteScalar() == null) missing++;
            }
            return missing;
        }
    }

    static (string where, (string, object?)[] p) Where(PunchFilter f)
    {
        var w = new List<string> { "1=1" };
        var p = new List<(string, object?)>();
        if (f.From != "") { w.Add("p.punched_at >= @from"); p.Add(("@from", f.From + " 00:00:00")); }
        if (f.To != "") { w.Add("p.punched_at <= @to"); p.Add(("@to", f.To + " 23:59:59")); }
        if (f.Pin != "") { w.Add("(p.pin = @pin OR e.name LIKE @like)"); p.Add(("@pin", f.Pin)); p.Add(("@like", "%" + f.Pin + "%")); }
        if (f.DeviceId > 0) { w.Add("p.device_id = @dev"); p.Add(("@dev", f.DeviceId)); }
        return (string.Join(" AND ", w), [.. p]);
    }

    public (List<PunchRow> rows, int total) Punches(PunchFilter f)
    {
        var (w, p) = Where(f);
        int total = (int)Scalar<long>($"SELECT count(*) FROM punch p LEFT JOIN employee e ON e.pin=p.pin WHERE {w}", p);
        var limit = f.Limit <= 0 || f.Limit > 1_000_000 ? 200 : f.Limit;
        var rows = Query($@"SELECT p.id,d.name,p.pin,COALESCE(e.name,''),p.punched_at,p.verify_mode,p.inout_mode,p.work_code
            FROM punch p JOIN device d ON d.id=p.device_id LEFT JOIN employee e ON e.pin=p.pin
            WHERE {w} ORDER BY p.punched_at DESC, p.id DESC LIMIT {limit} OFFSET {Math.Max(f.Offset, 0)}",
            r => new PunchRow(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt32(5), r.GetInt32(6), r.GetInt32(7)), p);
        return (rows, total);
    }

    /// <summary>Waktu log terakhir milik satu mesin di database ini (null bila belum ada).</summary>
    public DateTime? LastPunchAt(long deviceId)
    {
        var v = Query("SELECT max(punched_at) FROM punch WHERE device_id=@d", r => r.IsDBNull(0) ? null : r.GetString(0), ("@d", deviceId)).FirstOrDefault();
        return v != null && DateTime.TryParseExact(v, TimeFmt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;
    }

    /// <summary>Tanggal log terakhir di seluruh database (hari berjalan data); null bila kosong.</summary>
    public DateTime? LastPunchDate()
    {
        var v = Query("SELECT max(punched_at) FROM punch", r => r.IsDBNull(0) ? null : r.GetString(0)).FirstOrDefault();
        return v != null && DateTime.TryParseExact(v, TimeFmt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t.Date : null;
    }

    // ---- pegawai ----
    public List<Employee> Employees(string q) => Query(
        @"SELECT id,pin,COALESCE(nip,''),name,status,shifts,grup FROM employee
          WHERE (@q='' OR pin=@q OR name LIKE '%'||@q||'%' OR nip LIKE '%'||@q||'%') ORDER BY CAST(pin AS INTEGER), pin LIMIT 2000",
        r => new Employee(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4), r.GetString(5), r.GetString(6)), ("@q", q));

    public void UpsertEmployee(string pin, string nip, string name, int status, string shifts = "", string group = "")
    {
        if (pin.Trim() == "" || name.Trim() == "") throw new InvalidOperationException("PIN dan nama wajib diisi");
        Exec(@"INSERT INTO employee(pin,nip,name,status,shifts,grup) VALUES (@p,@nip,@n,@s,@sh,@g)
               ON CONFLICT(pin) DO UPDATE SET nip=excluded.nip,name=excluded.name,status=excluded.status,shifts=excluded.shifts,grup=excluded.grup",
            ("@p", pin.Trim()), ("@nip", nip), ("@n", name.Trim()), ("@s", status), ("@sh", shifts.Trim()), ("@g", group.Trim()));
    }

    public bool HasEmployee(string pin) => Scalar<long>("SELECT count(*) FROM employee WHERE pin=@p", ("@p", pin)) > 0;

    public List<(int backup, byte[] data)> Templates(string pin, long? deviceId = null) => Query(
        "SELECT backup_no,data FROM employee_template WHERE pin=@p AND (@d IS NULL OR device_id=@d) ORDER BY device_id,backup_no",
        r => (r.GetInt32(0), (byte[])r["data"]), ("@p", pin), ("@d", deviceId));

    /// <summary>Menambahkan pegawai dari mesin. updateName=true juga menimpa nama yang sudah ada. Hasil: "baru", "nama" atau "".</summary>
    public string MergeDeviceUser(string pin, string name, int privilege, bool updateName)
    {
        var cur = Query("SELECT name FROM employee WHERE pin=@p", r => r.GetString(0), ("@p", pin)).FirstOrDefault();
        if (cur == null)
        {
            Exec("INSERT INTO employee(pin,name,privilege,status) VALUES (@p,@n,@pr,1)",
                ("@p", pin), ("@n", name == "" ? "(nama belum diketahui)" : name), ("@pr", privilege));
            return "baru";
        }
        if (updateName && name != "" && name != cur)
        {
            Exec("UPDATE employee SET name=@n WHERE pin=@p", ("@n", name), ("@p", pin));
            return "nama";
        }
        return "";
    }

    public void SaveTemplate(string pin, long deviceId, int backup, byte[] data) =>
        Exec(@"INSERT INTO employee_template(pin,device_id,backup_no,data) VALUES (@p,@d,@b,@x)
               ON CONFLICT(pin,device_id,backup_no) DO UPDATE SET data=excluded.data,fetched_at=datetime('now')",
            ("@p", pin), ("@d", deviceId), ("@b", backup), ("@x", data));

    /// <summary>Semua scan (semua mesin) pada rentang waktu [from, to], untuk rekap.</summary>
    public List<(string pin, DateTime at)> PunchTimes(DateTime from, DateTime to) => Query(
        "SELECT pin,punched_at FROM punch WHERE punched_at >= @a AND punched_at <= @b",
        r => (r.GetString(0), DateTime.ParseExact(r.GetString(1), TimeFmt, CultureInfo.InvariantCulture)),
        ("@a", from.ToString(TimeFmt, CultureInfo.InvariantCulture)), ("@b", to.ToString(TimeFmt, CultureInfo.InvariantCulture)));

    // ---- ringkasan ----
    public Stats Stats()
    {
        lock (gate)
        {
            using var c = Cmd(@"SELECT (SELECT count(*) FROM device),(SELECT count(*) FROM employee),(SELECT count(*) FROM punch),
                COALESCE((SELECT min(punched_at) FROM punch),''),COALESCE((SELECT max(punched_at) FROM punch),''),
                (SELECT count(*) FROM punch WHERE punched_at >= date('now','localtime')),
                (SELECT count(DISTINCT pin) FROM punch WHERE pin NOT IN (SELECT pin FROM employee))", []);
            using var r = c.ExecuteReader();
            r.Read();
            return new Stats(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetString(3), r.GetString(4), r.GetInt32(5), r.GetInt32(6));
        }
    }

    public List<RunRow> Runs(int limit) => Query(
        $@"SELECT r.id,d.name,r.started_at,COALESCE(r.finished_at,''),r.status,r.fetched,r.inserted,COALESCE(r.error,'')
           FROM sync_run r JOIN device d ON d.id=r.device_id ORDER BY r.id DESC LIMIT {limit}",
        r => new RunRow(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt32(5), r.GetInt32(6), r.GetString(7)));

    // ---- pengaturan & login ----
    public string Setting(string key, string def = "") =>
        Query("SELECT value FROM setting WHERE key=@k", r => r.GetString(0), ("@k", key)).FirstOrDefault() ?? def;

    public void SetSetting(string key, string value) =>
        Exec("INSERT INTO setting(key,value) VALUES (@k,@v) ON CONFLICT(key) DO UPDATE SET value=excluded.value", ("@k", key), ("@v", value));

    // PBKDF2-SHA256; format "pbkdf2$iter$salt$hash"
    public static string HashPassword(string pw)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var h = Rfc2898DeriveBytes.Pbkdf2(pw, salt, 120_000, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2$120000${Convert.ToBase64String(salt)}${Convert.ToBase64String(h)}";
    }

    public static bool VerifyPassword(string pw, string stored)
    {
        var p = stored.Split('$');
        if (p.Length != 4 || p[0] != "pbkdf2") return false;
        var h = Rfc2898DeriveBytes.Pbkdf2(pw, Convert.FromBase64String(p[2]), int.Parse(p[1]), HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(h, Convert.FromBase64String(p[3]));
    }

    /// <summary>Membuat akun awal bila tabel app_user masih kosong.</summary>
    public void EnsureUser(string username, string password)
    {
        if (Scalar<long>("SELECT count(*) FROM app_user") > 0) return;
        Exec("INSERT INTO app_user(username,password_hash,role) VALUES (@u,@h,'admin')", ("@u", username), ("@h", HashPassword(password)));
    }

    /// <summary>Mengganti nama pengguna dan sandi akun pertama (admin).</summary>
    public void ChangeLogin(string username, string password)
    {
        if (username.Trim() == "" || password.Length < 6) throw new InvalidOperationException("ID wajib diisi dan sandi minimal 6 karakter");
        Exec("UPDATE app_user SET username=@u,password_hash=@h WHERE id=(SELECT min(id) FROM app_user)", ("@u", username.Trim()), ("@h", HashPassword(password)));
    }

    public bool CheckLogin(string username, string password)
    {
        var h = Query("SELECT password_hash FROM app_user WHERE username=@u", r => r.GetString(0), ("@u", username)).FirstOrDefault();
        // verifikasi tetap dijalankan walau pengguna tak ada, supaya waktu tanggap seragam
        return VerifyPassword(password, h ?? HashPassword("x")) && h != null;
    }
}
