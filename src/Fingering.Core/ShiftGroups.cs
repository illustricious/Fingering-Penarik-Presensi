// Kelompok pegawai dan aturan jam mulai shift-nya (rumah sakit: pagi/siang/malam, manajemen per hari, jaga malam fleksibel).
// Urutan prioritas di Rekap: daftar shift milik pegawai > aturan kelompok > daftar global di Setelan.
namespace Fingering.Core;

public enum ShiftKind
{
    /// <summary>Daftar jam mulai tetap; shift dipilih dari scan pertama (aturan lama).</summary>
    Fixed,
    /// <summary>Jam mulai tunggal yang bergantung pada hari dalam minggu; hari tanpa isian = tidak dinilai telat.</summary>
    ByWeekday,
    /// <summary>Jam mulai = scan pertama dibulatkan ke jam terdekat (menit 30 ke atas naik): 22:14 -> 22:00, 22:40 -> 23:00.</summary>
    NearestHour,
}

public sealed class ShiftGroup(string name, ShiftKind kind, string starts = "", Func<DayOfWeek, string>? byDay = null, string note = "")
{
    public string Name { get; } = name;
    public ShiftKind Kind { get; } = kind;
    public string Note { get; } = note;

    /// <summary>Jam mulai yang berlaku pada tanggal tertentu (kosong bila tidak ada yang bisa dinilai).</summary>
    public List<TimeSpan> StartsOn(DateTime date) => Kind switch
    {
        ShiftKind.Fixed => DendaRules.ParseShifts(starts),
        ShiftKind.ByWeekday => DendaRules.ParseShifts(byDay?.Invoke(date.DayOfWeek) ?? ""),
        _ => [],
    };

    /// <summary>Jam mulai untuk kelompok NearestHour; bisa 24:00 bila scan lewat 23:30.</summary>
    public static TimeSpan RoundToHour(TimeSpan scan) => TimeSpan.FromHours(Math.Floor((scan.TotalMinutes + 30) / 60));
}

public static class ShiftGroups
{
    public const string NearestHourName = "Jaga Malam Backup";

    public static readonly IReadOnlyList<ShiftGroup> All =
    [
        new("Perawat", ShiftKind.Fixed, "07:00,10:00,14:00,21:00", note: "pagi 07, mid ranap 10, siang 14, malam 21"),
        new("Farmasi", ShiftKind.Fixed, "07:00,10:00,14:00,21:00", note: "disamakan dengan perawat (belum dikonfirmasi)"),
        new("Lab", ShiftKind.Fixed, "07:00,14:00,21:00"),
        new("Radiologi", ShiftKind.Fixed, "06:00,13:00"),
        new("Rekam Medis", ShiftKind.Fixed, "07:00,13:00", note: "pagi 07-15, siang 13-21"),
        new("Gizi", ShiftKind.Fixed, "05:30,13:00,21:00"),
        new("Pramusaji", ShiftKind.Fixed, "05:30,13:00,21:00"),
        new("OB", ShiftKind.Fixed, "07:00,14:00"),
        new("Security", ShiftKind.Fixed, "07:00,15:00,22:00"),
        new("Manajemen", ShiftKind.ByWeekday, byDay: d => d switch
        {
            DayOfWeek.Monday or DayOfWeek.Tuesday or DayOfWeek.Wednesday or DayOfWeek.Thursday => "08:00",
            DayOfWeek.Friday or DayOfWeek.Saturday => "07:00",
            _ => "",   // Minggu: libur, tidak dinilai telat (belum dikonfirmasi)
        }, note: "Senin-Kamis 08-16, Jumat-Sabtu 07-15"),
        new(NearestHourName, ShiftKind.NearestHour, note: "jam masuk = scan dibulatkan ke jam terdekat"),
    ];

    public static ShiftGroup? Find(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : All.FirstOrDefault(g => string.Equals(g.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    public static IEnumerable<string> Names => All.Select(g => g.Name);
}
