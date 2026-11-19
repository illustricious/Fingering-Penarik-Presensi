// Impor dump MySQL aplikasi lama (Fingerspot Personnel, backup.sql) ke database Fingering.
// Hanya yang cocok yang diambil: log presensi (att_log) untuk mesin yang IP-nya sama dengan mesin di Fingering, dan pegawai (pegawai)
// yang PIN-nya belum ada. Nama pegawai yang sudah ada TIDAK ditimpa. Idempoten: baris yang sudah ada diabaikan (kunci mesin+PIN+waktu).
using System.Globalization;
using System.Text;

namespace Fingering.Core;

public sealed class LegacyReport
{
    public sealed record DeviceMatch(string LegacySerial, string LegacyName, string Host, long? DeviceId, string LocalName, int Rows, int Added);
    public List<DeviceMatch> Devices = [];
    public int PunchRows, PunchBadRows, PunchUnmatchedRows, PunchAdded;
    public int EmployeesInDump, EmployeesAdded;
    public string? First, Last;
    public string BackupFile = "";
}

public static class LegacyImport
{
    /// <summary>Baca semua tuple (...) pada satu baris INSERT; nilai string tanpa tanda kutip, NULL -> null.</summary>
    public static IEnumerable<List<string?>> Tuples(string line)
    {
        int i = 0;
        while (i < line.Length)
        {
            while (i < line.Length && line[i] != '(') { if (line[i] == ';') yield break; i++; }
            if (i >= line.Length) yield break;
            i++;
            var row = new List<string?>();
            while (i < line.Length)
            {
                while (i < line.Length && line[i] == ' ') i++;
                if (i < line.Length && line[i] == '\'')
                {
                    var sb = new StringBuilder();
                    i++;
                    while (i < line.Length)
                    {
                        var ch = line[i];
                        if (ch == '\\' && i + 1 < line.Length) { sb.Append(line[i + 1] switch { 'n' => '\n', 'r' => '\r', 't' => '\t', '0' => '\0', var o => o }); i += 2; }
                        else if (ch == '\'' && i + 1 < line.Length && line[i + 1] == '\'') { sb.Append('\''); i += 2; }
                        else if (ch == '\'') { i++; break; }
                        else { sb.Append(ch); i++; }
                    }
                    row.Add(sb.ToString());
                }
                else
                {
                    int s = i;
                    while (i < line.Length && line[i] != ',' && line[i] != ')') i++;
                    var tok = line[s..i].Trim();
                    row.Add(tok.Equals("NULL", StringComparison.OrdinalIgnoreCase) ? null : tok);
                }
                while (i < line.Length && line[i] == ' ') i++;
                if (i < line.Length && line[i] == ',') { i++; continue; }
                if (i < line.Length && line[i] == ')') { i++; break; }
            }
            yield return row;
        }
    }

    /// <summary>Isi tabel tertentu: setiap tuple dari pernyataan INSERT INTO table VALUES ...;</summary>
    static IEnumerable<List<string?>> Rows(string path, string table)
    {
        var head = "INSERT INTO " + table + " VALUES";
        bool inside = false;
        foreach (var line in File.ReadLines(path, Encoding.Latin1))
        {
            if (!inside)
            {
                if (!line.StartsWith(head, StringComparison.Ordinal)) continue;
                inside = true;
                var rest = line[head.Length..];
                foreach (var t in Tuples(rest)) yield return t;
                if (rest.TrimEnd().EndsWith(';')) inside = false;
                continue;
            }
            foreach (var t in Tuples(line)) yield return t;
            if (line.TrimEnd().EndsWith(';')) inside = false;
        }
    }

    static string Norm(string? s) => (s ?? "").Trim().ToLowerInvariant();

    /// <summary>Cocokkan dengan database lalu (bila apply) tulis. backupDir berisi salinan database sebelum ditulis.</summary>
    public static LegacyReport Run(Store store, string sqlPath, bool apply, string? backupDir = null)
    {
        var rep = new LegacyReport();
        var locals = store.Devices();

        // mesin lama -> mesin Fingering (cocok bila IP sama, atau serial sama)
        var map = new Dictionary<string, Device?>();
        foreach (var r in Rows(sqlPath, "device"))
        {
            string sn = r[0] ?? "", name = r[3] ?? "", ip = (r[6] ?? "").Trim();
            var local = locals.FirstOrDefault(d => Norm(d.Host) == Norm(ip) && ip != "") ?? locals.FirstOrDefault(d => Norm(d.Serial) == Norm(sn) && sn != "");
            map[sn] = local;
        }
        var legacyInfo = Rows(sqlPath, "device").ToDictionary(r => r[0] ?? "", r => (name: r[3] ?? "", ip: (r[6] ?? "").Trim()));

        // log per mesin
        var perDevice = new Dictionary<string, List<Punch>>();
        foreach (var r in Rows(sqlPath, "att_log"))
        {
            rep.PunchRows++;
            if (r.Count < 7 || r[0] == null || r[2] == null || r[1] == null
                || !DateTime.TryParseExact(r[1], Store.TimeFmt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) || at.Year < 2000)
            { rep.PunchBadRows++; continue; }
            if (!map.TryGetValue(r[0]!, out var local) || local == null) { rep.PunchUnmatchedRows++; continue; }
            if (!perDevice.TryGetValue(r[0]!, out var list)) perDevice[r[0]!] = list = [];
            _ = int.TryParse(r[3], out var verify); _ = int.TryParse(r[4], out var inout); _ = int.TryParse(r[6], out var wc);
            list.Add(new Punch(r[2]!.Trim(), at, verify, inout, wc));
            if (rep.First == null || string.CompareOrdinal(r[1], rep.First) < 0) rep.First = r[1];
            if (rep.Last == null || string.CompareOrdinal(r[1], rep.Last) > 0) rep.Last = r[1];
        }

        // pegawai: hanya PIN yang belum ada
        var have = store.Employees("").Select(e => e.Pin).ToHashSet();
        var newEmps = new List<(string pin, string name)>();
        foreach (var r in Rows(sqlPath, "pegawai"))
        {
            rep.EmployeesInDump++;
            var pin = (r.Count > 1 ? r[1] : null)?.Trim() ?? "";
            var name = (r.Count > 3 ? r[3] : null)?.Trim() ?? "";
            if (pin == "" || have.Contains(pin)) continue;
            have.Add(pin);
            newEmps.Add((pin, name));
        }

        if (apply)
        {
            if (backupDir != null)
            {
                Directory.CreateDirectory(backupDir);
                rep.BackupFile = Path.Combine(backupDir, $"presensi-sebelum-impor-{DateTime.Now:yyyyMMdd-HHmmss}.db");
                store.BackupTo(rep.BackupFile);
            }
            foreach (var (pin, name) in newEmps) { store.MergeDeviceUser(pin, name, 0, false); rep.EmployeesAdded++; }
        }
        else rep.EmployeesAdded = newEmps.Count;

        foreach (var (sn, (name, ip)) in legacyInfo)
        {
            map.TryGetValue(sn, out var local);
            perDevice.TryGetValue(sn, out var list);
            list = list?.DistinctBy(p => (p.Pin, p.At)).ToList();   // baris ganda di dump dihitung sekali
            int added = 0, rows = list?.Count ?? 0;
            if (local != null && list != null)
            {
                if (apply)
                {
                    var run = store.StartRun(local.Id);
                    for (int i = 0; i < list.Count; i += 5000) added += store.InsertPunches(local.Id, run, "import", list.Skip(i).Take(5000));
                    store.FinishRun(run, local.Id, rows, added, null);
                }
                else added = store.CountMissing(local.Id, list);
            }
            rep.PunchAdded += added;
            rep.Devices.Add(new LegacyReport.DeviceMatch(sn, name, ip, local?.Id, local?.Name ?? "", rows, added));
        }
        return rep;
    }

    public static string Describe(LegacyReport r, bool applied)
    {
        var sb = new StringBuilder();
        foreach (var d in r.Devices)
            sb.AppendLine(d.DeviceId == null
                ? $"• Mesin lama \"{d.LegacyName}\" ({d.Host}) tidak ada di Fingering — dilewati."
                : $"• \"{d.LegacyName}\" ({d.Host}) → {d.LocalName}: {d.Rows:N0} log, {(applied ? "masuk" : "akan masuk")} {d.Added:N0} baru.");
        if (r.First != null) sb.AppendLine($"Rentang log: {r.First[..10]} s/d {r.Last![..10]}.");
        if (r.PunchBadRows + r.PunchUnmatchedRows > 0) sb.AppendLine($"Dilewati: {r.PunchBadRows:N0} baris rusak, {r.PunchUnmatchedRows:N0} dari mesin yang tidak cocok.");
        sb.AppendLine($"Pegawai: {r.EmployeesInDump:N0} di backup, {(applied ? "ditambah" : "akan ditambah")} {r.EmployeesAdded:N0} yang PIN-nya belum ada (nama yang sudah ada tidak ditimpa).");
        return sb.ToString().TrimEnd();
    }
}
