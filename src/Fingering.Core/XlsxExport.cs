// Ekspor rekap presensi + denda ke XLSX bergaya contoh kantor (Book1.xlsx): satu baris per hari per pegawai,
// subtotal per pegawai, total denda berupa angka jadi (tanpa rumus). Berkas dikunci:
//  - semua sheet diproteksi (tidak bisa diedit tanpa sandi), struktur buku dikunci;
//  - berkas dibuka sebagai "baca-saja" kecuali memasukkan sandi tulis (fileSharing/reservation password).
// CATATAN: ini perlindungan terhadap pengubahan, BUKAN enkripsi isi berkas (isi tetap bisa dibaca tanpa sandi).
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using ClosedXML.Excel;

namespace Fingering.Core;

public static class XlsxExport
{
    static readonly string[] Months = ["Januari", "Februari", "Maret", "April", "Mei", "Juni", "Juli", "Agustus", "September", "Oktober", "November", "Desember"];
    static readonly string[] MonthsShort = ["Jan", "Feb", "Mar", "Apr", "Mei", "Jun", "Jul", "Agu", "Sep", "Okt", "Nov", "Des"];

    /// <summary>"16 Oktober - 25 Oktober 2026" (tahun hanya di akhir bila sama).</summary>
    public static string PeriodText(DateTime from, DateTime to, bool shortMonths = false)
    {
        var m = shortMonths ? MonthsShort : Months;
        var a = from.Year == to.Year ? $"{from.Day} {m[from.Month - 1]}" : $"{from.Day} {m[from.Month - 1]} {from.Year}";
        return $"{a} - {to.Day} {m[to.Month - 1]} {to.Year}";
    }

    /// <summary>Nama berkas yang disarankan, mis. "Rekap Presensi 16 Oktober - 25 Oktober 2026.xlsx".</summary>
    public static string SuggestedFileName(DateTime from, DateTime to) => $"Rekap Presensi {PeriodText(from, to)}.xlsx";

    static readonly string[] Headers =
    [
        "PIN", "NIP", "Nama", "Tanggal", "Scan 1", "Scan 2", "Scan 3", "Scan 4", "Hadir", "Lupa absen",
        "Telat <10 mnt (a)", "Telat 10-20 mnt (b)", "Telat >20 mnt (c)", "Total denda (Rp)", "Shift",
    ];

    // warna header mengikuti contoh: Hadir biru, lupa absen merah tua, a kuning, b oranye, c merah, total merah muda
    static readonly string[] HeaderFill =
        ["#D9D9D9", "#D9D9D9", "#D9D9D9", "#D9D9D9", "#D9D9D9", "#D9D9D9", "#D9D9D9", "#D9D9D9", "#4A86E8", "#980000", "#FFD966", "#F6B26B", "#CC0000", "#EA9999", "#D9D9D9"];
    static readonly string[] HeaderFont =
        ["#000000", "#000000", "#000000", "#000000", "#000000", "#000000", "#000000", "#000000", "#FFFFFF", "#FFFFFF", "#000000", "#000000", "#FFFFFF", "#000000", "#000000"];

    /// <param name="includeDenda">false = kolom Total denda tetap ada tetapi tidak dihitung (diisi "-"); kolom lupa/telat tetap terisi.</param>
    public static void Write(string path, DateTime from, DateTime to, List<EmpRekap> data, DendaRules rules, string password, bool includeDenda = true)
    {
        using var wb = new XLWorkbook();
        wb.Style.Font.FontName = "Calibri";
        wb.Style.Font.FontSize = 11;
        var sheetName = PeriodText(from, to, shortMonths: true);
        if (sheetName.Length > 31) sheetName = sheetName[..31];
        var ws = wb.Worksheets.Add(sheetName);
        var sum = wb.Worksheets.Add("Ringkasan");
        var rul = wb.Worksheets.Add("Aturan");

        WriteRules(rul, rules, from, to, includeDenda);

        // ---- sheet utama ----
        var title = ws.Range(1, 1, 1, Headers.Length).Merge();
        title.Value = $"REKAP PRESENSI PERIODE {PeriodText(from, to).ToUpperInvariant()}";
        title.Style.Font.Bold = true; title.Style.Font.FontSize = 13;
        title.Style.Fill.BackgroundColor = XLColor.FromHtml("#F4CCCC");
        title.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        ws.Row(1).Height = 24;

        var maxLen = Headers.Select(h => h.Split(' ').Max(w => w.Length)).ToArray(); // minimal selebar kata terpanjang header (header dibungkus)
        for (int i = 0; i < Headers.Length; i++)
        {
            var c = ws.Cell(2, i + 1);
            c.Value = Headers[i];
            c.Style.Fill.BackgroundColor = XLColor.FromHtml(HeaderFill[i]);
            c.Style.Font.FontColor = XLColor.FromHtml(HeaderFont[i]);
            c.Style.Font.Bold = true;
            c.Style.Alignment.WrapText = true;
            c.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            c.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        }

        void Track(int col, string shown) { if (shown.Length > maxLen[col - 1]) maxLen[col - 1] = shown.Length; }

        int r = 3;
        var subtotalRows = new List<(EmpRekap e, int row)>();
        foreach (var e in data)
        {
            int first = r;
            foreach (var d in e.Days)
            {
                ws.Cell(r, 1).Value = long.TryParse(e.Pin, out var pin) ? pin : e.Pin; Track(1, e.Pin);
                ws.Cell(r, 2).Value = e.Nip; Track(2, e.Nip);
                ws.Cell(r, 3).Value = e.Name; Track(3, e.Name);
                ws.Cell(r, 4).Value = d.Date; ws.Cell(r, 4).Style.DateFormat.Format = "dd-mm-yyyy"; Track(4, "00-00-0000");
                for (int k = 0; k < Math.Min(4, d.Scans.Count); k++)
                {
                    var cc = ws.Cell(r, 5 + k);
                    cc.Value = d.Scans[k].TimeOfDay; cc.Style.DateFormat.Format = "hh:mm:ss";
                    Track(5 + k, "00:00:00");
                }
                ws.Cell(r, 9).Value = d.Hadir; ws.Cell(r, 10).Value = d.Lupa;
                ws.Cell(r, 11).Value = d.A; ws.Cell(r, 12).Value = d.B; ws.Cell(r, 13).Value = d.C;
                ws.Cell(r, 15).Value = d.Shift; Track(15, d.Shift);
                for (int col = 9; col <= 13; col++) Track(col, ws.Cell(r, col).GetString());
                r++;
            }
            // baris subtotal per pegawai: angka jadi (tanpa rumus Excel)
            ws.Cell(r, 1).Value = long.TryParse(e.Pin, out var pn) ? pn : e.Pin;
            ws.Cell(r, 3).Value = e.Name;
            ws.Cell(r, 4).Value = "TOTAL";
            int[] totals = [e.Hadir, e.Lupa, e.A, e.B, e.C];
            for (int i = 0; i < totals.Length; i++) { ws.Cell(r, 9 + i).Value = totals[i]; Track(9 + i, totals[i].ToString()); }
            if (includeDenda)
            {
                ws.Cell(r, 14).Value = rules.TotalDenda(e.Lupa, e.A, e.B, e.C);
                ws.Cell(r, 14).Style.NumberFormat.Format = "[$Rp-421] #,##0";
                Track(14, "Rp " + rules.TotalDenda(e.Lupa, e.A, e.B, e.C).ToString("N0"));
            }
            else ws.Cell(r, 14).Value = "-";
            var row = ws.Range(r, 1, r, Headers.Length);
            row.Style.Font.Bold = true;
            row.Style.Fill.BackgroundColor = XLColor.FromHtml("#FFF2CC");
            row.Style.Border.TopBorder = XLBorderStyleValues.Thin;
            subtotalRows.Add((e, r));
            r++;
        }
        int lastRow = Math.Max(r - 1, 2);

        // alignment: angka & jam di tengah, nama kiri
        ws.Range(3, 1, lastRow, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        ws.Range(3, 4, lastRow, 13).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        ws.Range(3, 15, lastRow, 15).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        ws.Range(3, 14, lastRow, 14).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
        ws.Range(2, 1, lastRow, Headers.Length).Style.Border.InsideBorder = XLBorderStyleValues.Hair;
        ws.Range(2, 1, lastRow, Headers.Length).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        ws.SheetView.FreezeRows(2);

        // lebar kolom pas dengan isi: angka mepet, nama lebar
        double[] widths = new double[Headers.Length];
        for (int i = 0; i < Headers.Length; i++) { widths[i] = Math.Max(maxLen[i], 3) + 2.5; ws.Column(i + 1).Width = widths[i]; }
        int maxLines = 1;
        for (int i = 0; i < Headers.Length; i++) maxLines = Math.Max(maxLines, WrapLines(Headers[i], widths[i] - 1));
        ws.Row(2).Height = 16 * maxLines + 6;

        // ---- ringkasan: satu baris per pegawai (angka jadi, tanpa rumus) ----
        var sh = new[] { "PIN", "Nama", "Hadir", "Lupa absen", "Telat <10 (a)", "Telat 10-20 (b)", "Telat >20 (c)", "Total denda (Rp)" };
        var sw = sh.Select(h => h.Split(' ').Max(w => w.Length)).ToArray();
        var st = sum.Range(1, 1, 1, sh.Length).Merge();
        st.Value = $"RINGKASAN DENDA PERIODE {PeriodText(from, to).ToUpperInvariant()}";
        st.Style.Font.Bold = true; st.Style.Font.FontSize = 13; st.Style.Fill.BackgroundColor = XLColor.FromHtml("#F4CCCC");
        st.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center; sum.Row(1).Height = 24;
        for (int i = 0; i < sh.Length; i++)
        {
            var c = sum.Cell(2, i + 1); c.Value = sh[i];
            c.Style.Font.Bold = true; c.Style.Fill.BackgroundColor = XLColor.FromHtml("#D9D9D9");
            c.Style.Alignment.WrapText = true; c.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center; c.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        }
        int sr = 3;
        foreach (var (e, row) in subtotalRows)
        {
            sum.Cell(sr, 1).Value = long.TryParse(e.Pin, out var pin) ? pin : e.Pin; sw[0] = Math.Max(sw[0], e.Pin.Length);
            sum.Cell(sr, 2).Value = e.Name; sw[1] = Math.Max(sw[1], e.Name.Length);
            int[] vals = [e.Hadir, e.Lupa, e.A, e.B, e.C];
            for (int i = 0; i < vals.Length; i++) sum.Cell(sr, 3 + i).Value = vals[i];
            sw[2] = Math.Max(sw[2], 3);
            if (includeDenda)
            {
                var tot = rules.TotalDenda(e.Lupa, e.A, e.B, e.C);
                sum.Cell(sr, 8).Value = tot;
                sw[7] = Math.Max(sw[7], ("Rp " + tot.ToString("N0")).Length);
            }
            else sum.Cell(sr, 8).Value = "-";
            sum.Cell(sr, 8).Style.NumberFormat.Format = "[$Rp-421] #,##0";
            sr++;
        }
        if (sr > 3)
        {
            sum.Cell(sr, 2).Value = "TOTAL";
            var rows = subtotalRows.Select(x => x.e).ToList();
            int[] grand = [rows.Sum(x => x.Hadir), rows.Sum(x => x.Lupa), rows.Sum(x => x.A), rows.Sum(x => x.B), rows.Sum(x => x.C)];
            for (int i = 0; i < grand.Length; i++) sum.Cell(sr, 3 + i).Value = grand[i];
            if (includeDenda) { sum.Cell(sr, 8).Value = rows.Sum(x => rules.TotalDenda(x.Lupa, x.A, x.B, x.C)); sum.Cell(sr, 8).Style.NumberFormat.Format = "[$Rp-421] #,##0"; }
            else sum.Cell(sr, 8).Value = "-";
            var tot = sum.Range(sr, 1, sr, sh.Length);
            tot.Style.Font.Bold = true; tot.Style.Fill.BackgroundColor = XLColor.FromHtml("#FFF2CC"); tot.Style.Border.TopBorder = XLBorderStyleValues.Medium;
            sw[7] = Math.Max(sw[7], includeDenda ? 14 : 4);
        }
        for (int i = 0; i < sh.Length; i++) sum.Column(i + 1).Width = sw[i] + 2.5;
        sum.Range(3, 3, Math.Max(sr, 3), 7).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        sum.Range(3, 1, Math.Max(sr, 3), 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        sum.Range(3, 8, Math.Max(sr, 3), 8).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
        sum.Range(2, 1, Math.Max(sr, 3), sh.Length).Style.Border.InsideBorder = XLBorderStyleValues.Hair;
        sum.Range(2, 1, Math.Max(sr, 3), sh.Length).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        sum.SheetView.FreezeRows(2);
        sum.Row(2).Height = 16 * sh.Select((h, i) => WrapLines(h, sw[i] + 1.5)).Max() + 6;

        // ---- proteksi: sheet read-only, struktur buku dikunci ----
        foreach (var s in new[] { ws, sum, rul })
            s.Protect(password, XLProtectionAlgorithm.Algorithm.SHA512).AllowElement(XLSheetProtectionElements.SelectEverything);
        wb.Protect(password, XLProtectionAlgorithm.Algorithm.SHA512); // struktur buku terkunci
        wb.Properties.Title = $"Rekap Presensi {PeriodText(from, to)}";
        wb.Properties.Author = "Fingering";
        wb.Properties.Comments = "Dibuat oleh Fingering. Berkas dilindungi dari pengubahan.";
        wb.SaveAs(path);

        AddWriteReservation(path, password);
    }

    static int WrapLines(string text, double width)
    {
        int lines = 1, cur = 0;
        foreach (var w in text.Split(' '))
        {
            if (cur == 0) cur = w.Length;
            else if (cur + 1 + w.Length <= width) cur += 1 + w.Length;
            else { lines++; cur = w.Length; }
        }
        return lines;
    }

    static void WriteRules(IXLWorksheet w, DendaRules r, DateTime from, DateTime to, bool includeDenda)
    {
        var t = w.Range(1, 1, 1, 3).Merge();
        t.Value = "ATURAN DENDA";
        t.Style.Font.Bold = true; t.Style.Fill.BackgroundColor = XLColor.FromHtml("#F4CCCC");
        w.Cell(2, 2).Value = "Kategori"; w.Cell(2, 3).Value = "Nominal (Rp)";
        w.Range(2, 2, 2, 3).Style.Font.Bold = true; w.Range(2, 2, 2, 3).Style.Fill.BackgroundColor = XLColor.FromHtml("#D9D9D9");
        (string, int)[] rows =
        [
            ($"a. Telat < {r.BatasA} menit", r.NominalA), ($"b. Telat {r.BatasA}-{r.BatasB} menit", r.NominalB), ($"c. Telat > {r.BatasB} menit", r.NominalC),
            ("d. Lupa absen 1 kali (per periode)", r.LupaSatu), ("d. Lupa absen 2 kali atau lebih (per periode)", r.LupaDua),
        ];
        for (int i = 0; i < rows.Length; i++)
        {
            w.Cell(3 + i, 2).Value = rows[i].Item1;
            w.Cell(3 + i, 3).Value = rows[i].Item2;
            w.Cell(3 + i, 3).Style.NumberFormat.Format = "[$Rp-421] #,##0";
        }
        string[] notes =
        [
            "Total denda per pegawai = jumlah denda telat (a, b, c) + denda lupa absen (0 / nominal d-1 / nominal d-2 sesuai jumlah hari lupa absen). Semua angka sudah jadi (bukan rumus).",
            "Hadir: 1 = ada scan pada hari itu. Lupa absen: 1 = hanya ada 1 kali scan pada hari itu (masuk atau pulang tidak tercatat); hari terakhir data yang baru 1 scan belum dianggap lupa absen.",
            "Telat dihitung dari scan PERTAMA terhadap jam mulai shift; detik dipotong (07:04:59 dihitung 4 menit). Sehari paling banyak satu kategori denda.",
            $"Shift dikenali otomatis dari jam mulai terbesar yang <= scan pertama + {r.GraceMinutes} menit. Jam mulai yang dikenali: {r.ShiftStarts}. Pegawai dapat punya daftar shift sendiri.",
            $"Scan ganda dalam {r.DedupeSeconds} detik dianggap satu. Shift malam: scan masuk malam di tanggal masuk, scan pulang pagi ditulis apa adanya (tanggal kalender sesuai mesin) di baris tanggal berikutnya dengan Hadir 0 dan keterangan \"pulang malam\"; pasangan ini tidak dihitung lupa absen.",
            "Hanya hari yang punya scan yang ditampilkan; hari tanpa scan tidak dihitung denda (tidak ada data libur/izin/cuti di aplikasi).",
            $"Periode: {PeriodText(from, to)}. Dibuat oleh Fingering pada {DateTime.Now:dd-MM-yyyy HH:mm}.",
            "Berkas ini dikunci dari pengubahan (read-only); isi masih dapat dibaca tanpa sandi.",
            .. includeDenda ? Array.Empty<string>() : new[] { "EKSPOR TANPA DENDA: kolom lupa absen dan telat tetap terisi, tetapi Total denda tidak dihitung (ditulis \"-\")." },
        ];
        for (int i = 0; i < notes.Length; i++) w.Cell(9 + i, 2).Value = notes[i];
        w.Column(1).Width = 3; w.Column(2).Width = 48; w.Column(3).Width = 16;
    }

    // ---- sandi tulis (Excel membuka read-only kecuali sandi dimasukkan) ----
    static byte[] SpinHash(string password, byte[] salt, int spin)
    {
        var h = SHA512.HashData([.. salt, .. Encoding.Unicode.GetBytes(password)]);
        for (uint i = 0; i < spin; i++) h = SHA512.HashData([.. h, .. BitConverter.GetBytes(i)]);
        return h;
    }

    static void AddWriteReservation(string path, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        const int spin = 100_000;
        var hash = SpinHash(password, salt, spin);
        var el = $"<fileSharing readOnlyRecommended=\"1\" userName=\"Fingering\" algorithmName=\"SHA-512\" hashValue=\"{Convert.ToBase64String(hash)}\" saltValue=\"{Convert.ToBase64String(salt)}\" spinCount=\"{spin}\"/>";
        using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
        var entry = zip.GetEntry("xl/workbook.xml") ?? throw new InvalidOperationException("workbook.xml tidak ditemukan");
        string xml;
        using (var sr = new StreamReader(entry.Open(), Encoding.UTF8)) xml = sr.ReadToEnd();
        // urutan skema: fileVersion?, fileSharing?, workbookPr?, ...  (elemen boleh berawalan namespace, mis. <x:workbook>)
        var m = System.Text.RegularExpressions.Regex.Match(xml, @"<(?<p>[A-Za-z0-9_]+:)?fileVersion(?=[\s/>])[^>]*?/>");
        int at;
        if (m.Success) at = m.Index + m.Length;
        else
        {
            var root = System.Text.RegularExpressions.Regex.Match(xml, @"<(?<p>[A-Za-z0-9_]+:)?workbook(?=[\s>])[^>]*>");
            if (!root.Success) throw new InvalidOperationException("elemen workbook tidak ditemukan di workbook.xml: " + xml[..Math.Min(300, xml.Length)]);
            at = root.Index + root.Length;
        }
        var prefix = (m.Success ? m : System.Text.RegularExpressions.Regex.Match(xml, @"<(?<p>[A-Za-z0-9_]+:)?workbook(?=[\s>])")).Groups["p"].Value;
        xml = xml.Insert(at, el.Replace("<fileSharing", "<" + prefix + "fileSharing"));
        entry.Delete();
        var ne = zip.CreateEntry("xl/workbook.xml", CompressionLevel.Optimal);
        using var w = new StreamWriter(ne.Open(), new UTF8Encoding(false));
        w.Write(xml);
    }
}
