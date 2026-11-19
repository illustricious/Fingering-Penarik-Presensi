using Fingering.Core;
using Xunit;

namespace Fingering.Tests;

public class GroupTests
{
    static DayRow One(string group, DateTime at, params DateTime[] more)
    {
        var p = new List<(string, DateTime)> { ("1", at) };
        p.AddRange(more.Select(m => ("1", m)));
        var d = at.Date;
        return Rekap.Build(p, _ => new EmpInfo("X", "", "", group), new DendaRules(), d, d)[0].Days.Single();
    }

    // 2026-10-19 = Senin, 20 = Selasa, 23 = Jumat, 24 = Sabtu, 25 = Minggu
    static DateTime T(int day, string hm) => new DateTime(2026, 10, day) + TimeSpan.Parse(hm);

    [Theory]
    [InlineData(19, "08:12", "08:00", 12, 0, 1)]   // Senin: mulai 08:00
    [InlineData(22, "08:12", "08:00", 12, 0, 1)]   // Kamis
    [InlineData(23, "07:12", "07:00", 12, 0, 1)]   // Jumat: mulai 07:00
    [InlineData(24, "07:03", "07:00", 3, 1, 0)]    // Sabtu: mulai 07:00
    public void Manajemen_StartDependsOnWeekday(int day, string scan, string shift, int late, int a, int b)
    {
        var r = One("Manajemen", T(day, scan), T(day, "16:00"));
        Assert.Equal(shift, r.Shift);
        Assert.Equal((late, a, b), (r.LateMinutes, r.A, r.B));
    }

    [Fact]
    public void Manajemen_SundayIsNotJudged()
    {
        var r = One("Manajemen", T(25, "09:30"), T(25, "16:00"));
        Assert.Equal(("", 0), (r.Shift, r.LateMinutes));
    }

    [Theory]
    [InlineData("22:40", "23:00", 0, 0, 0)]   // dianggap masuk 23:00 -> tidak telat
    [InlineData("22:14", "22:00", 14, 0, 1)]  // dianggap masuk 22:00 -> telat 14 mnt (b)
    [InlineData("22:30", "23:00", 0, 0, 0)]
    [InlineData("22:29", "22:00", 29, 0, 0)]  // 29 mnt -> c
    [InlineData("23:40", "00:00", 0, 0, 0)]   // melewati tengah malam
    public void JagaMalamBackup_RoundsToNearestHour(string scan, string shift, int late, int a, int b)
    {
        var d = T(19, scan);
        var r = One(ShiftGroups.NearestHourName, d, d.Date.AddDays(1).AddHours(7));
        Assert.Equal(shift, r.Shift);
        Assert.Equal(late, r.LateMinutes);
        Assert.Equal((a, b), (r.A, r.B));
        if (scan == "22:29") Assert.Equal(1, r.C);
    }

    [Theory]
    [InlineData("Gizi", "05:40", "05:30", 10)]
    [InlineData("Pramusaji", "05:35", "05:30", 5)]
    [InlineData("Rekam Medis", "07:05", "07:00", 5)]
    [InlineData("Rekam Medis", "13:20", "13:00", 20)]
    [InlineData("Radiologi", "06:08", "06:00", 8)]
    [InlineData("Security", "22:04", "22:00", 4)]
    public void FixedGroups(string group, string scan, string shift, int late)
    {
        var r = One(group, T(19, scan), T(19, "23:30"));
        Assert.Equal((shift, late), (r.Shift, r.LateMinutes));
    }

    [Fact]
    public void OwnShiftList_OverridesGroup_AndNoGroupUsesGlobal()
    {
        var p = new[] { ("1", T(19, "09:20")), ("1", T(19, "17:00")) };
        var own = Rekap.Build(p, _ => new EmpInfo("X", "", "09:00", "Manajemen"), new DendaRules(), T(19, "00:00"), T(19, "00:00"))[0].Days.Single();
        Assert.Equal(("09:00", 20), (own.Shift, own.LateMinutes));
        var none = Rekap.Build(p, _ => new EmpInfo("X", "", ""), new DendaRules(), T(19, "00:00"), T(19, "00:00"))[0].Days.Single();
        Assert.Equal("08:00", none.Shift);
    }

    // Denda tidak diakumulasi dalam sehari: hari 1 scan tidak dinilai telat.
    [Fact]
    public void SingleScanDay_IsOnlyLupa_NeverAlsoLate()
    {
        var r = One("Perawat", T(19, "07:40"));
        Assert.Equal(1, r.Lupa);
        Assert.Equal((0, 0, 0, 0), (r.A, r.B, r.C, r.LateMinutes));
    }

    [Fact]
    public void NoDay_CanCarryMoreThanOneLateCategoryOrLupaPlusLate()
    {
        var rules = new DendaRules();
        foreach (var g in ShiftGroups.Names.Append(""))
            for (int day = 19; day <= 25; day++)
                for (int m = 0; m < 24 * 60; m += 7)
                    foreach (var two in new[] { false, true })
                    {
                        var t = T(day, "00:00").AddMinutes(m);
                        var r = two ? One(g, t, t.AddHours(8)) : One(g, t);
                        Assert.True(r.A + r.B + r.C <= 1);
                        Assert.False(r.Lupa == 1 && r.A + r.B + r.C > 0);
                        int fee = r.A * rules.NominalA + r.B * rules.NominalB + r.C * rules.NominalC;
                        Assert.True(fee <= 30_000);
                    }
    }
}

public class NoDendaExportTests
{
    [Fact]
    public void Xlsx_WithoutDenda_KeepsAllColumnsButNoAmounts()
    {
        var from = new DateTime(2026, 10, 19); var to = from;
        var p = new List<(string, DateTime)> { ("6", from.AddHours(7).AddMinutes(25)), ("6", from.AddHours(15)), ("7", from.AddHours(20).AddMinutes(50)) };
        var rules = new DendaRules();
        var data = Rekap.Build(p, _ => new EmpInfo("Asih", "", ""), rules, from, to);
        var path = Path.Combine(Path.GetTempPath(), "fingering-nd-" + Guid.NewGuid().ToString("N") + ".xlsx");
        XlsxExport.Write(path, from, to, data, rules, "rahasia", includeDenda: false);

        using var wb = new ClosedXML.Excel.XLWorkbook(path);
        var ws = wb.Worksheet(1);
        // semua kolom header tetap ada, termasuk Total denda
        Assert.Equal("Total denda (Rp)", ws.Cell(2, 14).GetString());
        Assert.Equal("Lupa absen", ws.Cell(2, 10).GetString());
        Assert.Equal("Telat >20 mnt (c)", ws.Cell(2, 13).GetString());
        // fakta tetap terisi: telat 25 menit -> c = 1, tetapi tidak ada nominal rupiah
        Assert.Equal(1, ws.Cell(3, 13).Value.GetNumber());
        Assert.Equal("TOTAL", ws.Cell(4, 4).GetString());
        Assert.Equal("-", ws.Cell(4, 14).GetString());
        Assert.False(ws.Cell(4, 14).HasFormula);
        var sum = wb.Worksheet("Ringkasan");
        Assert.Equal("Total denda (Rp)", sum.Cell(2, 8).GetString());
        File.Delete(path);
    }
}

public class LastDayTests
{
    static readonly DateTime D = new(2026, 10, 19);

    [Fact]
    public void LastDataDay_SingleScan_IsNotLupa()
    {
        var p = new List<(string, DateTime)> { ("6", D.AddHours(7).AddMinutes(25)) };
        var day = Rekap.Build(p, _ => new EmpInfo("X", "", ""), new DendaRules(), D, D, lastDay: D)[0].Days.Single();
        Assert.Equal(0, day.Lupa);    // belum absen pulang: wajar baru 1 scan
        Assert.Equal(1, day.Hadir);
        Assert.Equal(25, day.LateMinutes);   // telat masuk tetap dinilai dari scan pertama
    }

    [Fact]
    public void EarlierDay_SingleScan_StillLupa()
    {
        var p = new List<(string, DateTime)> { ("6", D.AddHours(7)), ("6", D.AddDays(1).AddHours(7)), ("6", D.AddDays(1).AddHours(15)) };
        var rows = Rekap.Build(p, _ => new EmpInfo("X", "", ""), new DendaRules(), D, D.AddDays(1), lastDay: D.AddDays(1))[0].Days;
        Assert.Equal(1, rows[0].Lupa);
        Assert.Equal(0, rows[1].Lupa);
    }
}
