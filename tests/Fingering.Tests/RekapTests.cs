using System.IO.Compression;
using ClosedXML.Excel;
using Fingering.Core;
using Xunit;

namespace Fingering.Tests;

public class RekapTests
{
    static readonly DateTime D = new(2026, 10, 19);

    static List<EmpRekap> Run(params (string pin, DateTime at)[] p) =>
        Rekap.Build(p, _ => new EmpInfo("Fitri", "", ""), new DendaRules(), new DateTime(2026, 10, 16), new DateTime(2026, 10, 25));

    [Theory]
    [InlineData("07:04:25", 4, 1, 0, 0)]   // contoh nyata di Book1.xlsx: telat 4 menit -> kategori a
    [InlineData("06:58:30", 0, 0, 0, 0)]
    [InlineData("07:00:59", 0, 0, 0, 0)]   // detik dipotong
    [InlineData("07:09:59", 9, 1, 0, 0)]
    [InlineData("07:10:00", 10, 0, 1, 0)]  // batas bawah b
    [InlineData("07:20:59", 20, 0, 1, 0)]  // batas atas b
    [InlineData("07:21:00", 21, 0, 0, 1)]  // c
    public void LateCategories(string scan, int minutes, int a, int b, int c)
    {
        var t = TimeSpan.Parse(scan);
        var r = Run(("8", D + t), ("8", D + new TimeSpan(14, 8, 0)))[0].Days.Single();
        Assert.Equal("07:00", r.Shift);
        Assert.Equal((minutes, a, b, c), (r.LateMinutes, r.A, r.B, r.C));
        Assert.Equal(0, r.Lupa);
    }

    [Fact]
    public void ForgotScan_SingleScanAndPenaltyTiers()
    {
        var rules = new DendaRules();
        Assert.Equal(0, rules.TotalDenda(0, 0, 0, 0));
        Assert.Equal(15_000, rules.TotalDenda(1, 0, 0, 0));
        Assert.Equal(30_000, rules.TotalDenda(2, 0, 0, 0));
        Assert.Equal(30_000, rules.TotalDenda(5, 0, 0, 0));
        Assert.Equal(15_000 + 5_000 + 20_000 + 30_000, rules.TotalDenda(1, 1, 1, 1));
        var r = Run(("11", D + new TimeSpan(20, 50, 40)))[0].Days.Single();
        Assert.Equal(1, r.Lupa);
        Assert.Equal(1, r.Hadir);
    }

    [Fact]
    public void DoubleTap_CountsAsOneScan()
    {
        var r = Run(("8", D + new TimeSpan(7, 0, 0)), ("8", D + new TimeSpan(7, 0, 20)))[0].Days.Single();
        Assert.Single(r.Scans);
        Assert.Equal(1, r.Lupa);
    }

    [Fact]
    public void NightShift_ScansStayOnTheirOwnCalendarDay()
    {
        var e = Run(("5", D + new TimeSpan(21, 2, 0)), ("5", D.AddDays(1) + new TimeSpan(7, 5, 0)))[0];
        Assert.Equal(2, e.Days.Count);
        var inRow = e.Days[0]; var outRow = e.Days[1];
        // malam: hanya scan masuk, tetap dinilai telat, TIDAK lupa karena pulangnya ada keesokan hari
        Assert.Equal(D, inRow.Date);
        Assert.Equal([D + new TimeSpan(21, 2, 0)], inRow.Scans);
        Assert.Equal(("21:00", 2, 0, 1), (inRow.Shift, inRow.LateMinutes, inRow.Lupa, inRow.Hadir));
        // pagi: scan pulang ditulis apa adanya di tanggalnya sendiri; tidak hadir ganda, tidak lupa, tidak telat
        Assert.Equal(D.AddDays(1), outRow.Date);
        Assert.Equal([D.AddDays(1) + new TimeSpan(7, 5, 0)], outRow.Scans);
        Assert.Equal((0, 0, 0, "pulang malam"), (outRow.Hadir, outRow.Lupa, outRow.A + outRow.B + outRow.C, outRow.Shift));
        Assert.Equal(1, e.Hadir);
        Assert.Equal(0, e.Lupa);
    }

    [Fact]
    public void NightShift_DoubleEveningScan_StillPairsWithMorning()
    {
        // contoh nyata: malam scan 20:31 lalu 20:38, pulang 07:03 keesokan hari -> pulang bukan "lupa" di baris sendiri
        var e = Run(("5", D + new TimeSpan(20, 31, 0)), ("5", D + new TimeSpan(20, 38, 0)), ("5", D.AddDays(1) + new TimeSpan(7, 3, 0)))[0];
        Assert.Equal(2, e.Days.Count);
        Assert.Equal(2, e.Days[0].Scans.Count);
        Assert.Equal(0, e.Days[0].Lupa);
        Assert.Equal((0, 0), (e.Days[1].Hadir, e.Days[1].Lupa));
        Assert.Equal(0, e.Lupa);
    }

    [Fact]
    public void NightShift_NoMorningScan_IsLupa_ButNotOnLastDataDay()
    {
        var p = new[] { ("5", D + new TimeSpan(21, 0, 0)) };
        Assert.Equal(1, Rekap.Build(p, _ => new EmpInfo("X", "", ""), new DendaRules(), D, D)[0].Days.Single().Lupa);
        Assert.Equal(0, Rekap.Build(p, _ => new EmpInfo("X", "", ""), new DendaRules(), D, D, lastDay: D)[0].Days.Single().Lupa);
    }

    [Fact]
    public void MorningScan_AfterDayShift_IsNotATreatedAsNightCheckout()
    {
        // hari sebelumnya shift siang-sore lengkap (13:55 + 21:04): scan pagi besoknya = masuk shift pagi biasa
        var e = Run(("5", D + new TimeSpan(13, 55, 0)), ("5", D + new TimeSpan(21, 4, 0)), ("5", D.AddDays(1) + new TimeSpan(7, 5, 0)), ("5", D.AddDays(1) + new TimeSpan(15, 1, 0)))[0];
        Assert.Equal(2, e.Days.Count);
        Assert.All(e.Days, d => Assert.Equal(1, d.Hadir));
    }

    [Fact]
    public void OwnShiftList_IsUsed()
    {
        var p = new[] { ("9", D + new TimeSpan(8, 20, 0)), ("9", D + new TimeSpan(16, 5, 0)) };
        var r = Rekap.Build(p, _ => new EmpInfo("Budi", "", "08:00"), new DendaRules(), D, D)[0].Days.Single();
        Assert.Equal("08:00", r.Shift);
        Assert.Equal(20, r.LateMinutes);
        Assert.Equal(1, r.B);
    }

    [Fact]
    public void DaysOutsideRange_AreDropped()
    {
        var e = Run(("8", new DateTime(2026, 10, 15, 7, 0, 0)), ("8", new DateTime(2026, 10, 26, 7, 0, 0)));
        Assert.Empty(e);
    }

    [Fact]
    public void PeriodText_AndFileName()
    {
        Assert.Equal("16 Oktober - 25 Oktober 2026", XlsxExport.PeriodText(new DateTime(2026, 10, 16), new DateTime(2026, 10, 25)));
        Assert.Equal("26 Agustus 2025 - 25 September 2026", XlsxExport.PeriodText(new DateTime(2025, 8, 26), new DateTime(2026, 9, 25)));
        Assert.Equal("Rekap Presensi 16 Oktober - 25 Oktober 2026.xlsx", XlsxExport.SuggestedFileName(new DateTime(2026, 10, 16), new DateTime(2026, 10, 25)));
    }

    [Fact]
    public void Xlsx_LayoutWidthsFormulasAndProtection()
    {
        var from = new DateTime(2026, 10, 16); var to = new DateTime(2026, 10, 25);
        var p = new List<(string, DateTime)>();
        // pegawai A: 3 hari normal + 1 telat 4 mnt + 1 lupa absen; pegawai B: telat 25 mnt
        foreach (var d in new[] { 16, 17, 18 }) { p.Add(("6", new DateTime(2026, 10, d, 6, 58, 30))); p.Add(("6", new DateTime(2026, 10, d, 15, 7, 34))); }
        p.Add(("6", new DateTime(2026, 10, 19, 7, 4, 25))); p.Add(("6", new DateTime(2026, 10, 19, 14, 8, 0)));
        p.Add(("6", new DateTime(2026, 10, 20, 20, 50, 40)));
        p.Add(("11", new DateTime(2026, 10, 21, 7, 25, 0))); p.Add(("11", new DateTime(2026, 10, 21, 14, 3, 0)));
        var rules = new DendaRules();
        var data = Rekap.Build(p, pin => new EmpInfo(pin == "6" ? "asihbudilestari" : "desi", "", ""), rules, from, to);

        var path = Path.Combine(Path.GetTempPath(), "fingering-x-" + Guid.NewGuid().ToString("N") + ".xlsx");
        XlsxExport.Write(path, from, to, data, rules, "rahasia");

        // sandi tulis ada di workbook.xml, dan urutan elemen sesuai skema
        using (var zip = ZipFile.OpenRead(path))
        {
            var xml = new StreamReader(zip.GetEntry("xl/workbook.xml")!.Open()).ReadToEnd();
            var fs = System.Text.RegularExpressions.Regex.Match(xml, "<(?:[a-z]+:)?fileSharing readOnlyRecommended=\"1\"");
            Assert.True(fs.Success, xml[..Math.Min(400, xml.Length)]);
            Assert.True(fs.Index < xml.IndexOf("workbookPr", StringComparison.Ordinal)); // urutan skema: fileSharing sebelum workbookPr
            Assert.Contains("workbookProtection", xml);
        }

        using var wb = new XLWorkbook(path);
        Assert.Equal(["16 Okt - 25 Okt 2026", "Ringkasan", "Aturan"], wb.Worksheets.Select(w => w.Name).ToArray());
        var ws = wb.Worksheet(1);
        Assert.Equal("REKAP PRESENSI PERIODE 16 OKTOBER - 25 OKTOBER 2026", ws.Cell(1, 1).GetString());
        Assert.True(ws.IsProtected);
        Assert.Equal("Nama", ws.Cell(2, 3).GetString());

        // lebar kolom: PIN/Hadir/angka mepet, nama lebar
        double wPin = ws.Column(1).Width, wName = ws.Column(3).Width, wHadir = ws.Column(9).Width, wA = ws.Column(11).Width;
        Assert.True(wPin <= 7, $"PIN {wPin}");
        Assert.True(wHadir <= 8, $"Hadir {wHadir}");
        Assert.True(wA <= 10, $"a {wA}");
        Assert.True(wName >= "asihbudilestari".Length, $"nama {wName}");
        Assert.True(wName > wPin * 2);

        // subtotal asih: baris 3..7 hari (16,17,18,19,20) + subtotal baris 8
        Assert.Equal("TOTAL", ws.Cell(8, 4).GetString());
        Assert.Equal(5, ws.Cell(8, 9).Value.GetNumber());    // hadir
        Assert.Equal(1, ws.Cell(8, 10).Value.GetNumber());   // lupa absen
        Assert.Equal(1, ws.Cell(8, 11).Value.GetNumber());   // a
        Assert.Equal(15_000 + 5_000, ws.Cell(8, 14).Value.GetNumber());
        // subtotal desi: telat 25 menit -> c = 30.000
        Assert.Equal(30_000, ws.Cell(10, 14).Value.GetNumber());
        Assert.False(ws.Cell(8, 14).HasFormula);   // angka jadi, bukan rumus
        Assert.DoesNotContain("Akumulasi", string.Concat(Enumerable.Range(1, 16).Select(c => ws.Cell(2, c).GetString())));

        var sum = wb.Worksheet("Ringkasan");
        Assert.Equal(20_000 + 30_000, sum.Cell(5, 8).Value.GetNumber()); // baris TOTAL
        File.Delete(path);
    }
}

public class EarlyNightArrivalTests
{
    [Fact]
    public void EveningScanBeforeNightShift_IsNotLateFromDayShift()
    {
        var d = new DateTime(2026, 11, 6);
        var p = new[] { ("143", d.AddHours(19).AddMinutes(53)), ("143", d.AddDays(1).AddHours(7).AddMinutes(4)) };
        var row = Rekap.Build(p, _ => new EmpInfo("Kiki", "", "07:00,10:00,14:00,21:00"), new DendaRules(), d, d)[0].Days.Single();
        Assert.Equal("21:00", row.Shift);     // bukan 14:00 (yang dulu memberi telat 353 menit)
        Assert.Equal(0, row.LateMinutes);
        Assert.Equal(0, row.Lupa);
    }
}
