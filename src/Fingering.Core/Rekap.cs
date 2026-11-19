// Rekap presensi per pegawai per hari + perhitungan denda, mengikuti contoh Excel kantor (Book1.xlsx).
// Aturan (dari legenda contoh): telat <10 mnt = a, 10-20 mnt = b, >20 mnt = c; lupa absen (hanya 1x scan sehari).
// Detik scan dipotong sebelum menghitung menit (sama dengan aturan hitung aplikasi lama, RE-NOTES-hitung-absensi.md).
using System.Globalization;

namespace Fingering.Core;

/// <summary>Nominal denda dan batas kategori; disimpan di tabel setting agar bisa diubah tanpa membangun ulang.</summary>
public sealed class DendaRules
{
    public int NominalA = 5_000, NominalB = 20_000, NominalC = 30_000;
    public int LupaSatu = 15_000, LupaDua = 30_000;   // lupa absen: 1x = LupaSatu; 2x atau lebih = LupaDua (total per periode)
    public int BatasA = 10, BatasB = 20;               // a: 1..BatasA-1; b: BatasA..BatasB; c: > BatasB (menit)
    public int GraceMinutes = 15;                      // scan boleh lebih awal sekian menit dari jam mulai shift dan tetap dianggap shift itu
    public int DedupeSeconds = 60;                     // scan ganda dalam selang ini dianggap satu
    /// <summary>Jam mulai shift yang dikenali (dipakai bila pegawai tidak punya daftar shift sendiri).</summary>
    public string ShiftStarts = "05:30,06:00,07:00,08:00,10:00,11:30,12:30,13:00,14:00,15:00,21:00,22:00";

    static int I(Store s, string k, int d) => int.TryParse(s.Setting(k, ""), out var v) ? v : d;

    public static DendaRules Load(Store s) => new()
    {
        NominalA = I(s, "denda_a", 5_000), NominalB = I(s, "denda_b", 20_000), NominalC = I(s, "denda_c", 30_000),
        LupaSatu = I(s, "denda_lupa1", 15_000), LupaDua = I(s, "denda_lupa2", 30_000),
        BatasA = I(s, "batas_a", 10), BatasB = I(s, "batas_b", 20), GraceMinutes = I(s, "shift_grace", 15), DedupeSeconds = I(s, "dedupe_detik", 60),
        ShiftStarts = s.Setting("shift_starts", "05:30,06:00,07:00,08:00,10:00,11:30,12:30,13:00,14:00,15:00,21:00,22:00"),
    };

    public void Save(Store s)
    {
        s.SetSetting("denda_a", NominalA.ToString()); s.SetSetting("denda_b", NominalB.ToString()); s.SetSetting("denda_c", NominalC.ToString());
        s.SetSetting("denda_lupa1", LupaSatu.ToString()); s.SetSetting("denda_lupa2", LupaDua.ToString());
        s.SetSetting("batas_a", BatasA.ToString()); s.SetSetting("batas_b", BatasB.ToString());
        s.SetSetting("shift_grace", GraceMinutes.ToString()); s.SetSetting("dedupe_detik", DedupeSeconds.ToString());
        s.SetSetting("shift_starts", ShiftStarts);
    }

    /// <summary>Daftar jam mulai terurut dari teks "07:00,14:00" (entri tidak valid diabaikan).</summary>
    public static List<TimeSpan> ParseShifts(string text) =>
        text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(x => TimeSpan.TryParseExact(x.Trim().Replace('.', ':'), @"h\:mm", CultureInfo.InvariantCulture, out var t) ? (TimeSpan?)t : null)
            .Where(t => t != null).Select(t => t!.Value).Distinct().OrderBy(t => t).ToList();

    public int TotalDenda(int lupa, int a, int b, int c) =>
        (lupa == 0 ? 0 : lupa == 1 ? LupaSatu : LupaDua) + a * NominalA + b * NominalB + c * NominalC;
}

public sealed class DayRow
{
    public DateTime Date;
    public List<DateTime> Scans = [];
    public int Hadir = 1, Lupa, A, B, C, LateMinutes;
    public string Shift = "";
}

public sealed class EmpRekap
{
    public string Pin = "", Nip = "", Name = "";
    public List<DayRow> Days = [];
    public int Hadir => Days.Sum(d => d.Hadir);
    public int Lupa => Days.Sum(d => d.Lupa);
    public int A => Days.Sum(d => d.A);
    public int B => Days.Sum(d => d.B);
    public int C => Days.Sum(d => d.C);
    public int LateMinutes => Days.Sum(d => d.LateMinutes);
}

public sealed record EmpInfo(string Name, string Nip, string Shifts, string Group = "");

public static class Rekap
{
    /// <summary>
    /// Susun rekap untuk tanggal [from, to]. punches harus mencakup 1 hari sebelum from dan 1 hari sesudah to
    /// (shift malam: scan masuk malam hari, scan pulang pagi berikutnya; tiap scan tetap di tanggal kalender aslinya). lastDay = tanggal log terakhir yang ada di database (hari berjalan): hari itu tidak dihitung lupa absen.
    /// </summary>
    public static List<EmpRekap> Build(IEnumerable<(string pin, DateTime at)> punches, Func<string, EmpInfo?> emp, DendaRules rules, DateTime from, DateTime to, DateTime? lastDay = null)
    {
        var result = new List<EmpRekap>();
        var dedupe = TimeSpan.FromSeconds(Math.Max(0, rules.DedupeSeconds));
        var defaultShifts = DendaRules.ParseShifts(rules.ShiftStarts);
        foreach (var g in punches.GroupBy(p => p.pin).OrderBy(g => long.TryParse(g.Key, out var n) ? n : long.MaxValue).ThenBy(g => g.Key))
        {
            // 1) urut + buang scan ganda
            var scans = new List<DateTime>();
            foreach (var t in g.Select(x => x.at).OrderBy(x => x))
                if (scans.Count == 0 || t - scans[^1] > dedupe) scans.Add(t);

            // 2) kelompok per tanggal kalender, PERSIS seperti dari mesin (tidak ada scan yang dipindah ke hari lain)
            var days = scans.GroupBy(s => s.Date).OrderBy(x => x.Key).ToList();
            var byDate = days.ToDictionary(x => x.Key, x => x.ToList());

            // 3) hitung per hari. Shift malam: scan malam di tanggal masuk, scan pulang pagi di tanggal berikutnya (baris sendiri, Hadir 0);
            //    scan pagi (<11:00) tepat setelah hari yang hanya berisi scan malam (>=17:00) adalah scan pulang shift itu: tidak dinilai
            //    telat/lupa dan tidak menghitung hadir lagi.
            var info = emp(g.Key);
            var own = info != null ? DendaRules.ParseShifts(info.Shifts) : [];
            var group = ShiftGroups.Find(info?.Group);
            var er = new EmpRekap { Pin = g.Key, Name = info?.Name is { Length: > 0 } n ? n : g.Key, Nip = info?.Nip ?? "" };
            bool nightOpen = false;
            DateTime prevDate = default;
            foreach (var dg in days)
            {
                var date = dg.Key;
                var all = byDate[date];
                int outCount = nightOpen && prevDate == date.AddDays(-1) ? all.TakeWhile(s => s.TimeOfDay < TimeSpan.FromHours(11)).Count() : 0;
                var list = all.Skip(outCount).ToList();   // scan yang dinilai (masuk/pulang shift hari ini)
                nightOpen = list.Count > 0 && list.All(s => s.TimeOfDay >= TimeSpan.FromHours(17));
                prevDate = date;
                if (date < from.Date || date > to.Date) continue;
                var row = new DayRow { Date = date, Scans = all, Hadir = list.Count > 0 ? 1 : 0 };
                if (list.Count == 0) { row.Shift = "pulang malam"; er.Days.Add(row); continue; }
                // malam yang scan pulang paginya ada di tanggal berikutnya tidak lupa; hari terakhir data (hari ini) yang baru 1 scan juga belum lupa
                bool paired = nightOpen && byDate.TryGetValue(date.AddDays(1), out var nx) && nx[0].TimeOfDay < TimeSpan.FromHours(11);
                row.Lupa = list.Count == 1 && !paired && date != lastDay?.Date ? 1 : 0;
                var first = list[0].TimeOfDay;
                // prioritas: daftar shift pegawai > aturan kelompok > daftar global
                TimeSpan? start;
                // hari yang hanya berisi scan malam: pilih dari shift malam (>=17:00) saja, supaya datang agak awal (19:53 untuk shift 21:00) tidak dikira telat dari shift siang
                TimeSpan Pick(List<TimeSpan> s) => nightOpen && s.Any(x => x >= TimeSpan.FromHours(17)) ? PickShift(s.Where(x => x >= TimeSpan.FromHours(17)).ToList(), first, rules.GraceMinutes) : PickShift(s, first, rules.GraceMinutes);
                if (own.Count > 0) start = Pick(own);
                else if (group is { Kind: ShiftKind.NearestHour }) start = ShiftGroup.RoundToHour(first);
                else if (group != null) start = group.StartsOn(date) is { Count: > 0 } gs ? Pick(gs) : null;
                else start = defaultShifts.Count == 0 ? null : Pick(defaultShifts);
                if (start is { } st)
                {
                    row.Shift = TimeSpan.FromTicks(st.Ticks % TimeSpan.TicksPerDay).ToString(@"hh\:mm");
                    var firstMin = new TimeSpan(first.Hours, first.Minutes, 0); // detik dipotong
                    var late = (int)Math.Floor((firstMin - st).TotalMinutes);
                    // Denda tidak diakumulasi dalam sehari: hari lupa absen (1 scan) tidak bisa dinilai telat, jadi hanya kena denda lupa.
                    if (late > 0 && row.Lupa == 0)
                    {
                        row.LateMinutes = late;
                        if (late < rules.BatasA) row.A = 1; else if (late <= rules.BatasB) row.B = 1; else row.C = 1;
                    }
                }
                er.Days.Add(row);
            }
            if (er.Days.Count > 0) result.Add(er);
        }
        return result;
    }

    // shift = jam mulai terbesar yang <= (scan + grace); sebelum semua jam mulai -> jam mulai paling awal (datang lebih pagi dari semua shift)
    static TimeSpan PickShift(List<TimeSpan> starts, TimeSpan scan, int graceMin)
    {
        var limit = scan + TimeSpan.FromMinutes(graceMin);
        TimeSpan? best = null;
        foreach (var s in starts) if (s <= limit) best = s;
        return best ?? starts[0];
    }
}
