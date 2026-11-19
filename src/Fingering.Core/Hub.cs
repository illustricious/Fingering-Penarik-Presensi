// Perangkai: pekerjaan (tarik log, pegawai, jam, sidik jari), kunci per mesin, dan penjadwal.
// Port dari presensi-hub/internal/app.
using System.Globalization;
using System.Text;

namespace Fingering.Core;

public sealed class Job
{
    public int Id;
    public string Kind = "", Device = "", Started = "", Error = "", Summary = "";
    public bool Done;
    /// <summary>Kemajuan 0..1; null = tidak diketahui (bar tak-tentu). Diisi pekerjaan yang tahu jumlah langkahnya.</summary>
    public double? Progress { get; private set; }
    readonly StringBuilder log = new();
    readonly object gate = new();
    public event Action? Changed;

    public string Log { get { lock (gate) return log.ToString(); } }

    public void Line(string s)
    {
        lock (gate) { if (log.Length < 4 << 20) log.AppendLine(s); }
        Changed?.Invoke();
    }

    public void SetProgress(double? p) { Progress = p is null ? null : Math.Clamp(p.Value, 0, 1); Changed?.Invoke(); }
    public void SetProgress(int done, int total) => SetProgress(total <= 0 ? null : (double)done / total);

    public void Finish(string summary, string? error)
    {
        Done = true; Summary = summary; Error = error ?? "";
        if (error == null) Progress = 1;
        Changed?.Invoke();
    }
}

public sealed class Hub
{
    public readonly Store Store;
    public readonly DumpStore Dumps;
    readonly List<Job> jobs = [];
    readonly Dictionary<long, SemaphoreSlim> locks = [];
    int nextId;
    public event Action<Job>? JobStarted;
    public event Action? DataChanged;

    public Hub(Store store, DumpStore dumps)
    {
        Store = store; Dumps = dumps;
    }

    SemaphoreSlim LockOf(long id)
    {
        lock (locks) return locks.TryGetValue(id, out var l) ? l : locks[id] = new SemaphoreSlim(1, 1);
    }

    public IReadOnlyList<Job> Jobs { get { lock (jobs) return [.. jobs]; } }

    Job NewJob(string kind, string device)
    {
        Job j;
        lock (jobs)
        {
            j = new Job { Id = ++nextId, Kind = kind, Device = device, Started = DateTime.Now.ToString(Store.TimeFmt) };
            jobs.Add(j);
            if (jobs.Count > 60) jobs.RemoveAt(0);
        }
        JobStarted?.Invoke(j);
        return j;
    }

    static FkConfig CfgOf(Device d, Job j)
    {
        uint.TryParse(d.CommKey.Trim(), CultureInfo.InvariantCulture, out var k);
        return new FkConfig { Host = d.Host, Port = d.Port, Machine = d.MachineNo, Key = k, Timeout = TimeSpan.FromSeconds(8), Trace = j.Line, Progress = p => j.SetProgress(p) };
    }

    /// <summary>Menyimpan data mentah ke dump.db; mengembalikan penanda "#id" (atau "" bila gagal).</summary>
    string Dump(Device d, string name, byte[] data)
    {
        try { return "#" + Dumps.Add(d.Name, name, data); } catch (Exception) { return ""; }
    }

    Job Start(long deviceId, string kind, Func<Device, Job, Task<string>> body)
    {
        var d = Store.GetDevice(deviceId) ?? throw new InvalidOperationException("mesin tidak ditemukan");
        var j = NewJob(kind, d.Name);
        _ = Task.Run(async () =>
        {
            var l = LockOf(d.Id);
            await l.WaitAsync();
            try { j.Finish(await body(d, j), null); }
            catch (Exception e) { j.Line($"GAGAL: {e.Message}"); j.Finish("", e.Message); }
            finally { l.Release(); DataChanged?.Invoke(); }
        });
        return j;
    }

    /// <summary>Membuka sesi (login + info) ke mesin lalu memanggil fn.</summary>
    Job Session(long deviceId, string kind, Func<Device, FkClient, DeviceInfo, Job, Task<string>> fn) =>
        Start(deviceId, kind, async (d, j) =>
        {
            var cfg = CfgOf(d, j);
            j.Line($"### {kind.ToUpperInvariant()} {d.Name} — tcp {d.Host}:{d.Port} mesin#{d.MachineNo}");
            using var c = await FkClient.DialAsync(cfg);
            await c.Login();
            var info = await c.Info();
            return await fn(d, c, info, j);
        });

    static string Printable(byte[] b) => new(b.Select(x => x is >= 0x20 and < 0x7f ? (char)x : '.').ToArray());

    static TimeSpan WallDiff(DateTime dev) => dev - DateTime.Now;

    // ---- uji koneksi (baca-saja) ----
    public Job Probe(long deviceId) => Session(deviceId, "probe", async (d, c, info, j) =>
    {
        j.Line($"dialek=\"{info.Dialect}\" versi=0x{info.Version:X4} jenis={info.Kind}");
        if (info.Ext.Length > 0) j.Line($"teks info mesin: {Printable(info.Ext)}");
        Dump(d, "info48", info.Raw);
        int all = await c.Count(true), nw = await c.Count(false);
        j.Line($"jumlah log: semua={all} baru={nw}");
        if (all <= 0) return $"login ok; versi 0x{info.Version:X4}; log=0";
        // contoh baca terbatas (≤16 KB): deteksi tata letak tanpa menarik semua log
        var (sample, scores) = await c.ProbeLayouts(info, all);
        Dump(d, "probe-sample", sample);
        foreach (var s in scores) j.Line($"tata letak {s.name,-4} ({s.note}): skor {s.score:0.00}");
        var best = scores.Count > 0 && scores[0].score >= 0.9 ? scores[0].name : "tidak ada yang cocok";
        return $"login ok; versi 0x{info.Version:X4}; dialek {info.Dialect}; log={all}; tata letak terbaik {best}";
    });

    // ---- info & jam ----
    public Job InfoJob(long deviceId) => Session(deviceId, "info", async (d, c, info, j) =>
    {
        j.Line($"dialek=\"{info.Dialect}\" versi=0x{info.Version:X4}");
        if (info.Ext.Length > 0) j.Line($"teks info mesin: {Printable(info.Ext)}");
        var sum = new List<string>();
        try
        {
            var p = await c.Product();
            Dump(d, "product", p.Raw);
            foreach (var k in new[] { "p1", "p3", "p4", "p5", "p6", "p7" })
                if (p.Strings[k] != "") j.Line($"produk {k} = \"{p.Strings[k]}\"");
            j.Line($"produk p2 = {p.Number}");
            if (p.Strings["p1"] != "") sum.Add("p1=" + p.Strings["p1"]);
        }
        catch (Exception e) when (e is FkException or TimeoutException) { j.Line($"data produk gagal: {e.Message}"); }
        try
        {
            var t = await c.DeviceTime();
            var diff = WallDiff(t);
            j.Line($"jam mesin {t:yyyy-MM-dd HH:mm:ss} (selisih {Math.Round(diff.TotalSeconds)} dtk dari komputer ini)");
            sum.Add($"jam {t:HH:mm:ss} (selisih {Math.Round(diff.TotalSeconds)} dtk)");
        }
        catch (Exception e) when (e is FkException or TimeoutException) { j.Line($"jam mesin gagal: {e.Message}"); }
        try { var n = await c.Count(true); j.Line($"jumlah log di mesin: {n}"); sum.Add($"{n} log"); } catch (Exception e) when (e is FkException or TimeoutException) { }
        return string.Join("; ", sum);
    });

    /// <summary>Baca jam mesin; set=true MENGATUR jam mesin sama dengan jam komputer ini.</summary>
    public Job Clock(long deviceId, bool set) => Session(deviceId, set ? "sinkron-jam" : "jam", async (d, c, info, j) =>
    {
        var t = await c.DeviceTime();
        var diff = WallDiff(t);
        j.Line($"jam mesin {t:yyyy-MM-dd HH:mm:ss}; selisih {Math.Round(diff.TotalSeconds)} dtk");
        if (!set) return $"jam mesin {t:HH:mm:ss} (selisih {Math.Round(diff.TotalSeconds)} dtk)";
        await c.SetDeviceTime(DateTime.Now);
        var t2 = await c.DeviceTime();
        j.Line($"jam mesin sesudah diatur: {t2:yyyy-MM-dd HH:mm:ss}");
        return $"jam mesin diatur ke {t2:HH:mm:ss} (sebelumnya selisih {Math.Round(diff.TotalSeconds)} dtk)";
    });

    async Task AutoSyncClock(FkClient c, Job j)
    {
        try
        {
            var t = await c.DeviceTime();
            var diff = WallDiff(t);
            if (Math.Abs(diff.TotalMinutes) < 2) { j.Line($"sinkron jam: selisih {Math.Round(diff.TotalSeconds)} dtk, tidak perlu diatur"); return; }
            await c.SetDeviceTime(DateTime.Now);
            j.Line($"sinkron jam: jam mesin {t:HH:mm:ss} diatur ke {DateTime.Now:HH:mm:ss}");
        }
        catch (Exception e) when (e is FkException or TimeoutException) { j.Line($"sinkron jam gagal: {e.Message}"); }
    }

    // ---- tarik log ----
    public Job Pull(long deviceId, string kind = "pull", DateTime? from = null, DateTime? to = null) =>
        Start(deviceId, kind, async (d, j) =>
        {
            var runId = Store.StartRun(d.Id);
            int fetched = 0, inserted = 0;
            string sum = "";
            string? err = null;
            try
            {
                j.Line($"### TARIK {d.Name} — tcp {d.Host}:{d.Port} mesin#{d.MachineNo}");
                using var c = await FkClient.DialAsync(CfgOf(d, j));
                await c.Login();
                var info = await c.Info();
                j.Line($"info: versi=0x{info.Version:X4} jenis={info.Kind}");
                Dump(d, "info48", info.Raw);
                var o = new PullOptions
                {
                    Layout = d.Layout, From = from, To = to,
                    Dump = (n, b) =>
                    {
                        if (Store.Setting("dump_logs", "1") != "1" || b.Length == 0) return;
                        var fn = Dump(d, n, b);
                        if (fn != "") j.Line($"data mentah disimpan di dump.db {fn} ({b.Length} byte)");
                    },
                };
                var res = await c.PullLogs(info, o);
                fetched = res.Punches.Count;
                inserted = Store.InsertPunches(d.Id, runId, "pull", res.Punches);
                j.Line($"== tersimpan: {inserted} rekaman baru dari {fetched} (sisanya sudah ada)");
                if (Store.Setting("sync_clock", "0") == "1") await AutoSyncClock(c, j);
                sum = $"tata letak {res.Layout}; {fetched} dibaca, {inserted} baru, {res.Skipped} dilewati";
                return sum;
            }
            catch (Exception e) { err = e.Message; throw; }
            finally { Store.FinishRun(runId, d.Id, fetched, inserted, err); }
        });

    // ---- pegawai ----
    /// <summary>mode: "lihat" (hanya tampil), "baru" (PIN yang belum ada), "semua" (juga perbarui nama).</summary>
    public Job Users(long deviceId, string mode) => Session(deviceId, mode == "lihat" ? "lihat-user" : "pegawai", async (d, c, info, j) =>
    {
        var users = await c.ReadUsers();
        j.Line($"== {users.Count} user di mesin");
        if (users.Count > 0)
            Dump(d, "users", Encoding.UTF8.GetBytes(string.Concat(users.Select(u => $"{u.Id}\t{u.Privilege}\t{u.Enabled}\t0x{u.Mask:X4}\n"))));
        if (mode == "lihat") return await ListUsers(c, j, users);
        int added = 0, renamed = 0, same = 0, noName = 0, fails = 0;
        bool dumped = false;
        var now = DateTime.Now;
        int pdone = 0;
        foreach (var u in users)
        {
            j.SetProgress(pdone++, users.Count);
            if (mode == "baru" && Store.HasEmployee(u.Id)) { same++; continue; }
            string name = "";
            try
            {
                var (n, raw) = await c.UserInfo(u.Id, now);
                name = n;
                if (!dumped) { dumped = true; Dump(d, "userinfo", raw); j.Line($"   contoh info mentah PIN {u.Id}: {Convert.ToHexString(raw.AsSpan(36, 40))}"); }
            }
            catch (Exception e) when (e is FkException or TimeoutException)
            {
                fails++;
                j.Line($"   PIN {u.Id}: nama gagal dibaca: {e.Message}");
                if (fails >= 5 && added + renamed == 0) throw new FkException("5 kali gagal membaca nama berturut-turut; hentikan (periksa format info user)");
            }
            if (name == "") noName++;
            switch (Store.MergeDeviceUser(u.Id, name, u.Privilege, mode == "semua"))
            {
                case "baru": added++; j.Line($"   + PIN {u.Id}  {name}"); break;
                case "nama": renamed++; j.Line($"   ~ PIN {u.Id}  {name}"); break;
                default: same++; break;
            }
        }
        return $"{users.Count} user di mesin: {added} baru, {renamed} nama diperbarui, {same} tidak berubah, {noName} tanpa nama";
    });

    async Task<string> ListUsers(FkClient c, Job j, List<UserEntry> users)
    {
        j.Line($"{"PIN",-12} {"NAMA (mesin)",-30} {"HAK",-5} {"JARI",-5} DATABASE");
        int inDb = 0, fails = 0;
        int pdone = 0;
        foreach (var u in users)
        {
            j.SetProgress(pdone++, users.Count);
            string name;
            try { name = (await c.UserInfo(u.Id, DateTime.Now)).name; }
            catch (Exception e) when (e is FkException or TimeoutException) { fails++; name = "(gagal baca nama)"; }
            var has = Store.HasEmployee(u.Id);
            if (has) inDb++;
            j.Line($"{u.Id,-12} {name,-30} {u.Privilege,-5} {u.Fingers,-5} {(has ? "ada" : "belum ada")}");
        }
        return $"{users.Count} user di mesin, {inDb} sudah di database, {fails} nama gagal dibaca";
    }

    /// <summary>Mengunduh data sidik jari mentah. pins kosong = semua user yang punya sidik jari.</summary>
    public Job Fingerprints(long deviceId, IReadOnlyCollection<string>? pins = null) => Session(deviceId, "sidik-jari", async (d, c, info, j) =>
    {
        var users = await c.ReadUsers();
        var want = (pins ?? []).Select(p => p.Trim()).ToHashSet();
        int saved = 0, skipped = 0, fails = 0;
        int pdone = 0;
        foreach (var u in users)
        {
            j.SetProgress(pdone++, users.Count);
            if (want.Count > 0 && !want.Contains(u.Id)) continue;
            if (u.Fingers == 0) { skipped++; continue; }
            for (int b = 0; b < u.Fingers; b++)
            {
                try { Store.SaveTemplate(u.Id, d.Id, b, await c.EnrollData(u.Id, b)); saved++; }
                catch (Exception e) when (e is FkException or TimeoutException) { fails++; j.Line($"   PIN {u.Id} jari {b}: gagal: {e.Message}"); }
            }
            j.Line($"   PIN {u.Id}: {u.Fingers} sidik jari");
        }
        return $"{saved} template tersimpan, {skipped} user tanpa sidik jari, {fails} gagal";
    });

    /// <summary>
    /// Tarik data BARU: sinkron terhadap data di PC (bukan penanda baca di mesin). Mengambil log sejak tanggal log terakhir
    /// milik mesin ini di database (mundur 1 hari sebagai pengaman); duplikat dibuang oleh kunci unik. Belum ada data = semua.
    /// Tidak mengubah apa pun di mesin.
    /// </summary>
    public Job PullNew(long deviceId, string kind = "pull-baru")
    {
        var last = Store.LastPunchAt(deviceId);
        return last is { } l ? Pull(deviceId, kind, l.Date.AddDays(-1), null) : Pull(deviceId, kind);
    }

    /// <summary>
    /// MENGHAPUS semua log di mesin. Pengaman: (1) harus diizinkan di Setelan; (2) hanya dialek 13_1 (satu-satunya yang dibongkar);
    /// (3) semua log dibaca penuh tanpa rekaman yang dilewati; (4) disimpan ke database dan DIVERIFIKASI ada semua; baru (5) perintah 0xA8.
    /// </summary>
    public Job ClearLogs(long deviceId) => Session(deviceId, "hapus-log", async (d, c, info, j) =>
    {
        if (Store.Setting("allow_clear", "0") != "1") throw new InvalidOperationException("hapus log di mesin belum diizinkan (Setelan)");
        if (info.Dialect != "13_1") throw new FkException($"hapus log hanya dibongkar untuk dialek 13_1 (mesin ini \"{info.Dialect}\"); dibatalkan");
        int count = await c.Count(true);
        j.Line($"== log di mesin: {count}");
        if (count == 0) return "tidak ada log di mesin";
        var runId = Store.StartRun(d.Id);
        PullResult res;
        try
        {
            res = await c.PullLogs(info, new PullOptions { Layout = d.Layout });
            if (res.Skipped > 0 || res.Punches.Count != count)
                throw new FkException($"cadangan tidak lengkap ({res.Punches.Count} sah dari {count}, {res.Skipped} dilewati); TIDAK ada yang dihapus");
            var added = Store.InsertPunches(d.Id, runId, "pull", res.Punches);
            Store.FinishRun(runId, d.Id, res.Punches.Count, added, null);
            j.Line($"== {res.Punches.Count} log dibaca, {added} baru disimpan");
        }
        catch (Exception e) { Store.FinishRun(runId, d.Id, 0, 0, e.Message); throw; }
        var missing = Store.CountMissing(d.Id, res.Punches);
        if (missing != 0) throw new FkException($"verifikasi gagal: {missing} log belum ada di database; TIDAK ada yang dihapus");
        j.Line("== verifikasi: semua log sudah ada di database. Mengirim perintah hapus (0xA8)…");
        await c.ClearLogs();
        var after = await c.Count(true);
        j.Line($"== sisa log di mesin: {after}");
        return $"{count} log aman di database; mesin sekarang berisi {after} log";
    });

    // ---- tambah / hapus / ubah user di mesin (MENGUBAH mesin) ----
    void RequireUsersAllowed(DeviceInfo info)
    {
        if (Store.Setting("allow_users", "0") != "1") throw new InvalidOperationException("ubah user di mesin belum diizinkan (Setelan)");
        if (info.Dialect != "13_1") throw new FkException($"tulis user hanya dibongkar untuk dialek 13_1 (mesin ini \"{info.Dialect}\"); dibatalkan");
    }

    /// <summary>Tambah user di mesin dengan sandi angka (data enroll sandi), lalu isi nama. Hasil diverifikasi lewat daftar user.</summary>
    public Job AddUser(long deviceId, string pin, string name, int privilege, string password) => Session(deviceId, "tambah-user", async (d, c, info, j) =>
    {
        RequireUsersAllowed(info);
        pin = pin.Trim();
        if (pin == "" || pin.Length > 31 || pin.Any(ch => ch is < ' ' or > '~')) throw new FkException("PIN harus 1-31 karakter ASCII");
        if (name.Trim() == "") throw new FkException("nama wajib diisi");
        if (password == "" || password.Length > 8 || !password.All(char.IsAsciiDigit)) throw new FkException("sandi mesin harus 1-8 digit angka");
        if ((await c.ReadUsers()).Any(u => u.Id == pin)) throw new FkException($"PIN {pin} sudah ada di mesin");
        var pw = new byte[32];
        Encoding.ASCII.GetBytes(password).CopyTo(pw, 0);
        j.Line($"== menulis user PIN {pin} (sandi, hak {privilege})…");
        await c.PutSimpleEnroll(pin, 10, privilege, pw);
        await c.SetUserName(pin, name.Trim());
        var after = await c.ReadUsers();
        if (!after.Any(u => u.Id == pin)) throw new FkException("mesin menjawab sukses tetapi user tidak muncul di daftar; periksa layar mesin");
        var (nm, _) = await c.UserInfo(pin, DateTime.Now);
        Store.MergeDeviceUser(pin, name.Trim(), privilege, true);
        j.Line($"== terverifikasi: user {pin} ada di mesin dengan nama \"{nm}\"");
        return $"user {pin} ({nm}) ditambahkan. Daftarkan sidik jari langsung di mesin";
    });

    /// <summary>Ubah nama user di mesin (dan di database).</summary>
    public Job RenameUser(long deviceId, string pin, string name) => Session(deviceId, "ubah-nama", async (d, c, info, j) =>
    {
        RequireUsersAllowed(info);
        if (!(await c.ReadUsers()).Any(u => u.Id == pin)) throw new FkException($"PIN {pin} tidak ada di mesin");
        await c.SetUserName(pin, name.Trim());
        var (nm, _) = await c.UserInfo(pin, DateTime.Now);
        if (nm != name.Trim()) throw new FkException($"nama di mesin sekarang \"{nm}\", bukan \"{name.Trim()}\"");
        Store.MergeDeviceUser(pin, name.Trim(), 0, true);
        return $"nama user {pin} di mesin = \"{nm}\"";
    });

    /// <summary>
    /// Hapus user di mesin. Pengaman: diizinkan di Setelan; dialek 13_1; user harus ada; SEMUA data enroll (sidik jari, sandi, kartu)
    /// diunduh dan disimpan ke database lebih dulu; baru dihapus, lalu diverifikasi bahwa user hilang dari daftar.
    /// </summary>
    public Job DeleteUser(long deviceId, string pin) => Session(deviceId, "hapus-user", async (d, c, info, j) =>
    {
        RequireUsersAllowed(info);
        var u = (await c.ReadUsers()).FirstOrDefault(x => x.Id == pin) ?? throw new FkException($"PIN {pin} tidak ada di mesin");
        var backups = new List<int>();
        for (int b = 0; b < u.Fingers; b++) backups.Add(b);
        if ((u.Mask & 1) != 0) backups.Add(10);
        if ((u.Mask & 2) != 0) backups.Add(11);
        j.Line($"== cadangan data enroll PIN {pin}: nomor {string.Join(",", backups)}");
        foreach (var b in backups)
        {
            var blob = await c.EnrollData(pin, b);
            Store.SaveTemplate(pin, d.Id, b, blob);
        }
        var saved = Store.Templates(pin, d.Id).Select(t => t.backup).ToHashSet();
        if (backups.Any(b => !saved.Contains(b))) throw new FkException("cadangan data enroll tidak lengkap; TIDAK ada yang dihapus");
        j.Line("== cadangan tersimpan di database. Menghapus dari mesin…");
        foreach (var b in Features.AllBackups) await c.DeleteEnrollData(pin, b);
        var gone = !(await c.ReadUsers()).Any(x => x.Id == pin);
        if (!gone) throw new FkException($"PIN {pin} masih ada di mesin setelah perintah hapus; periksa layar mesin");
        return $"user {pin} dihapus dari mesin ({backups.Count} data enroll dicadangkan di database)";
    });

    /// <summary>
    /// MENGOSONGKAN semua user + sidik jari di mesin (log presensi tidak disentuh). Pengaman: (1) harus diizinkan di Setelan ("allow_empty_enroll", bawaan mati);
    /// (2) hanya dialek 13_1; (3) setiap user: nama dibaca dan SEMUA data enroll (jari, sandi, kartu) diunduh ke database dan diverifikasi ada;
    /// (4) bila ada satu pun yang gagal dibaca, TIDAK ada yang dihapus; baru (5) perintah 0xC1; (6) hasil diperiksa: daftar user mesin harus kosong.
    /// CATATAN: cadangan tersimpan di database tetapi aplikasi ini BELUM bisa menulis balik sidik jari ke mesin (hanya sandi/kartu).
    /// </summary>
    public Job EmptyEnroll(long deviceId) => Session(deviceId, "kosongkan-user", async (d, c, info, j) =>
    {
        if (Store.Setting("allow_empty_enroll", "0") != "1") throw new InvalidOperationException("kosongkan data user di mesin belum diizinkan (Setelan)");
        if (info.Dialect != "13_1") throw new FkException($"kosongkan data user hanya dibongkar untuk dialek 13_1 (mesin ini \"{info.Dialect}\"); dibatalkan");
        var users = await c.ReadUsers();
        j.Line($"== {users.Count} user di mesin");
        if (users.Count == 0) return "tidak ada user di mesin";
        var now = DateTime.Now;
        int blobs = 0;
        int pdone = 0;
        foreach (var u in users)
        {
            j.SetProgress(pdone++, users.Count);
            var (name, _) = await c.UserInfo(u.Id, now);
            Store.MergeDeviceUser(u.Id, name, u.Privilege, false);
            var backups = new List<int>();
            for (int b = 0; b < u.Fingers; b++) backups.Add(b);
            if ((u.Mask & 1) != 0) backups.Add(10);
            if ((u.Mask & 2) != 0) backups.Add(11);
            foreach (var b in backups)
            {
                Store.SaveTemplate(u.Id, d.Id, b, await c.EnrollData(u.Id, b));
                blobs++;
            }
            var saved = Store.Templates(u.Id, d.Id).Select(t => t.backup).ToHashSet();
            if (backups.Any(b => !saved.Contains(b))) throw new FkException($"cadangan data enroll PIN {u.Id} tidak lengkap; TIDAK ada yang dihapus");
        }
        j.Line($"== cadangan lengkap: {users.Count} user, {blobs} data enroll tersimpan di database. Mengirim perintah kosongkan (0xC1)…");
        await c.EmptyEnrollData();
        var after = (await c.ReadUsers()).Count;
        j.Line($"== sisa user di mesin: {after}");
        if (after != 0) throw new FkException($"mesin masih berisi {after} user setelah perintah kosongkan; periksa layar mesin");
        return $"{users.Count} user dikosongkan dari mesin ({blobs} data enroll dicadangkan di database; log presensi tidak disentuh)";
    });

    // ---- pantau langsung (pengganti realtime) ----
    /// <summary>Batas bawah interval pantau (detik); tes boleh menurunkannya.</summary>
    public static int LiveMinSeconds = 5;
    readonly Dictionary<long, string> liveState = [];
    public string LiveState(long deviceId) { lock (liveState) return liveState.GetValueOrDefault(deviceId, ""); }
    void SetLive(long id, string s) { lock (liveState) liveState[id] = s; }

    /// <summary>
    /// Pantau langsung: tiap N detik (Setelan "live_sec", 0 = mati) menanyakan JUMLAH log di mesin (perintah hitung, baca-saja).
    /// Bila bertambah, jalankan "tarik data baru". Realtime asli (mesin mendorong scan ke PC) tidak dipakai kantor: use_realtime=0.
    /// </summary>
    public async Task RunLive(CancellationToken ct)
    {
        using var t = new PeriodicTimer(TimeSpan.FromSeconds(1));
        var next = new Dictionary<long, DateTime>();
        var counts = new System.Collections.Concurrent.ConcurrentDictionary<long, int>();
        while (await t.WaitForNextTickAsync(ct))
        {
            if (!int.TryParse(Store.Setting("live_sec", "0"), out var sec) || sec <= 0) { counts.Clear(); continue; }
            sec = Math.Max(sec, LiveMinSeconds);
            foreach (var d in Store.Devices())
            {
                if (!d.Enabled || next.GetValueOrDefault(d.Id) > DateTime.Now) continue;
                var l = LockOf(d.Id);
                if (!await l.WaitAsync(0, ct)) continue; // mesin sedang dipakai pekerjaan lain
                next[d.Id] = DateTime.Now.AddSeconds(sec);
                _ = Task.Run(async () =>
                {
                    bool pull = false;
                    try
                    {
                        var cfg = CfgOf(d, new Job());
                        cfg.Timeout = TimeSpan.FromSeconds(5);
                        cfg.Trace = null;
                        using var c = await FkClient.DialAsync(cfg, ct);
                        await c.Login();
                        var n = await c.Count(true);
                        var had = counts.TryGetValue(d.Id, out var prev);
                        counts[d.Id] = n;
                        pull = had && n > prev;
                        SetLive(d.Id, $"{DateTime.Now:HH:mm:ss} · {n:N0} log di mesin" + (pull ? $" · +{n - prev} baru" : ""));
                    }
                    catch (Exception e) when (e is FkException or TimeoutException or OperationCanceledException) { SetLive(d.Id, $"{DateTime.Now:HH:mm:ss} · gagal: {e.Message}"); }
                    finally { l.Release(); }
                    if (pull) PullNew(d.Id, "live");
                }); // Selalu jalankan finally setelah semaphore diperoleh, termasuk saat pembatalan.
            }
        }
    }

    /// <summary>Memeriksa tiap 30 detik mesin yang jatuh tempo.</summary>
    public async Task RunScheduler(CancellationToken ct)
    {
        using var t = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await t.WaitForNextTickAsync(ct))
        {
            foreach (var d in Store.Devices())
            {
                if (!d.Enabled || d.PullIntervalMin <= 0) continue;
                bool due = d.LastPullAt == "" || !DateTime.TryParseExact(d.LastPullAt, Store.TimeFmt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var lp)
                    || DateTime.Now - lp >= TimeSpan.FromMinutes(d.PullIntervalMin);
                if (due && LockOf(d.Id).CurrentCount > 0) PullNew(d.Id, "auto");
            }
        }
    }
}
