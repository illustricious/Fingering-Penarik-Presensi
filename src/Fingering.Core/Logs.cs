// Tata letak rekaman log, deteksi otomatis, dan penarikan log. Port dari presensi-hub/internal/fk/logs.go.
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Fingering.Core;

/// <summary>Satu rekaman presensi dari mesin. At = jam dinding mesin (tanpa zona).</summary>
public sealed record Punch(string Pin, DateTime At, int Verify, int InOut, int WorkCode);

public sealed class DeviceInfo
{
    public byte[] Raw = [];
    public byte[] Ext = [];
    public ushort Version, Kind, ExtLen;
    public string Dialect = "";
}

public delegate Punch? ParseFn(ReadOnlySpan<byte> b);

public sealed class Layout(string name, int size, string note, ParseFn parse)
{
    public string Name { get; } = name;
    public int Size { get; } = size;
    public string Note { get; } = note;
    public Punch? Parse(ReadOnlySpan<byte> b) => parse(b);
}

public sealed class PullOptions
{
    public bool NewOnly;
    public string Layout = "";          // "" atau "auto" = deteksi otomatis
    public DateTime? From, To;          // hanya tanggal; To inklusif
    public Action<string, byte[]>? Dump;
}

public sealed class PullResult
{
    public int Count;
    public string Layout = "";
    public double Score;
    public List<Punch> Punches = [];
    public int Skipped;
}

public static partial class Layouts
{
    static uint U32(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b[o..]);
    static ushort U16(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt16LittleEndian(b[o..]);

    public static DateTime? MkTime(int y, int mo, int d, int h, int mi, int s)
    {
        if (y < 2000 || y > DateTime.Now.Year + 1 || mo < 1 || mo > 12 || d < 1 || d > 31 || h > 23 || mi > 59 || s > 59) return null;
        if (d > DateTime.DaysInMonth(y, mo)) return null;
        return new DateTime(y, mo, d, h, mi, s, DateTimeKind.Unspecified);
    }

    // tanggal-jam 32 bit gaya CIF11/12/13/14
    public static DateTime? PackedTime(uint v, int sec) =>
        MkTime((int)(v >> 2 & 0x3ff) + 1900, (int)(v >> 12 & 0xf), (int)(v >> 16 & 0x1f), (int)(v >> 21 & 0x1f), (int)(v >> 26), sec);

    public static uint PackTime(DateTime t) =>
        (uint)(t.Year - 1900) << 2 | (uint)t.Month << 12 | (uint)t.Day << 16 | (uint)t.Hour << 21 | (uint)t.Minute << 26;

    static int IoMap(int v) => v switch { 0 => 1, 1 => 4, 2 => 2, 3 => 5, 4 => 3, 5 => 6, 8 => 0, _ => -1 };

    /// <summary>ID teks bergaris-nol; bukan ASCII tercetak -> "" (rekaman tidak sah).</summary>
    public static string IdText(ReadOnlySpan<byte> b)
    {
        var s = Encoding.Latin1.GetString(b).TrimEnd('\0', ' ');
        foreach (var r in s) if (r < 0x20 || r > 0x7e) return "";
        return s;
    }

    public static readonly Layout[] All =
    [
        new("B12", 12, "CIF11/12/14", b =>
        {
            var t = PackedTime(U32(b, 8), b[7]); var pin = U32(b, 0);
            return t is { } at && pin != 0 ? new Punch(pin.ToString(), at, b[6], b[5], 0) : null;
        }),
        new("A12", 12, "CIF10 / Gen tipe 2", b =>
        {
            int y = U16(b, 8), d = U16(b, 10); var pin = U32(b, 4);
            var t = MkTime(y & 0xfff, y >> 12, d & 0x1f, d >> 5 & 0x1f, d >> 10, b[3]);
            return t is { } at && pin != 0 ? new Punch(pin.ToString(), at, b[1] & 0x3f, IoMap(b[1] >> 6), 0) : null;
        }),
        new("G12", 12, "Gen tipe 4", b =>
        {
            int y = U16(b, 8), d = U16(b, 10); var pin = U32(b, 4); var v0 = U32(b, 0);
            var t = MkTime(y & 0xfff, y >> 12, d & 0x1f, d >> 5 & 0x1f, d >> 10, b[3]);
            return t is { } at && pin != 0 ? new Punch(pin.ToString(), at, (int)(v0 >> 8 & 0x3f), IoMap((int)(v0 >> 14 & 0x1f)), 0) : null;
        }),
        new("B20", 20, "CIF11_2/12_1/12_2/14_1", b =>
        {
            var t = PackedTime(U32(b, 8), b[7]); var pin = U32(b, 0);
            return t is { } at && pin != 0 ? new Punch(pin.ToString(), at, (int)U32(b, 16), b[15], (int)(U32(b, 12) & 0xffffff)) : null;
        }),
        new("C32", 32, "CIF13 (ID teks 16 byte)", b =>
        {
            var t = PackedTime(U32(b, 20), b[19]);
            var id = Encoding.Latin1.GetString(b[..16]).TrimEnd('\0', ' ');
            foreach (var r in id) if (r < 0x20 || r > 0x7e) { id = U32(b, 0).ToString(); break; }
            return t is { } at && id != "" && id != "0" ? new Punch(id, at, (int)((U32(b, 24) << 8) | b[27]), b[18], 0) : null;
        }),
        new("C48", 48, "CIF13_1 (ID teks 32 byte) — terbukti dari trace Revo W-230N", b =>
        {
            var t = PackedTime(U32(b, 36), b[35]); var id = IdText(b[..32]);
            return t is { } at && id != "" && id != "0" ? new Punch(id, at, b[43], b[47], (int)(U32(b, 40) & 0xffffff)) : null;
        }),
        new("G8", 8, "Gen tipe 1", b =>
        {
            var v0 = U32(b, 0); int a = U16(b, 4), d = U16(b, 6);
            var t = MkTime((a >> 5 & 0x7f) + 1900, a >> 12, d & 0x1f, d >> 5 & 0x1f, d >> 10, 0);
            return t is { } at && v0 >> 4 != 0 ? new Punch((v0 >> 4).ToString(), at, a & 0x1f, IoMap((int)(v0 >> 2 & 3)), 0) : null;
        }),
    ];

    public static Layout? ByName(string n) => All.FirstOrDefault(l => l.Name.Equals(n, StringComparison.OrdinalIgnoreCase));

    /// <summary>Dialek -> tata letak yang diduga (petunjuk; deteksi tetap memvalidasi).</summary>
    public static readonly Dictionary<string, string> DialectLayout = new()
    {
        ["10"] = "A12", ["10_1"] = "B20", ["11"] = "B12", ["11_1"] = "B12", ["11_2"] = "B20", ["12"] = "B12", ["12_1"] = "B20",
        ["12_2"] = "B20", ["13"] = "C32", ["13_1"] = "C48", ["14"] = "B12", ["14_1"] = "B20",
    };

    public static (List<Punch> punches, int valid, int total) ParseAll(byte[] data, Layout l)
    {
        var ps = new List<Punch>();
        int n = data.Length / l.Size, valid = 0;
        for (int i = 0; i < n; i++)
            if (l.Parse(data.AsSpan(i * l.Size, l.Size)) is { } p) { valid++; ps.Add(p); }
        return (ps, valid, n);
    }

    /// <summary>Memeriksa sampel awal dan akhir saja.</summary>
    public static double Score(byte[] data, Layout l)
    {
        int n = data.Length / l.Size;
        if (n == 0) return 0;
        var idx = new HashSet<int>();
        for (int i = 0; i < n && i < 150; i++) idx.Add(i);
        for (int i = n - 1; i >= 0 && i >= n - 150; i--) idx.Add(i);
        int valid = idx.Count(i => l.Parse(data.AsSpan(i * l.Size, l.Size)) != null);
        return (double)valid / idx.Count;
    }
}

public static partial class LogPuller
{
    static readonly Regex DialectRe = new(@"CommunicationInterfaceId\s*[:=]\s*""?([0-9]+(?:_[0-9]+)?)", RegexOptions.Compiled);

    public static async Task<DeviceInfo> Info(this FkClient c)
    {
        var raw = await c.ReadData(Cmd.Info, 0, 0x30);
        var di = new DeviceInfo { Raw = raw };
        if (raw.Length >= 0x30)
        {
            di.Version = BinaryPrimitives.ReadUInt16LittleEndian(raw);
            di.Kind = (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(4)) & 0xF);
            di.ExtLen = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(0x2e));
        }
        if (di.Version >= 0x104 && di.ExtLen > 0 && di.ExtLen <= 0x800)
        {
            try
            {
                di.Ext = await c.ReadData(Cmd.Info, 1, di.ExtLen);
                var m = DialectRe.Match(Encoding.Latin1.GetString(di.Ext));
                if (m.Success) di.Dialect = m.Groups[1].Value;
            }
            catch (Exception e) when (e is FkException or TimeoutException) { c.Trace($"   (blok info tambahan gagal dibaca: {e.Message})"); }
        }
        return di;
    }

    /// <summary>Jumlah log: semua (param 8) atau hanya yang baru (param 6).</summary>
    public static async Task<int> Count(this FkClient c, bool all)
    {
        var (code, val) = await c.SendCommand(Cmd.Status, all ? 8u : 6u);
        if (code != 1) throw new FkException($"hitung log ditolak (kode {code:X2})");
        if (val > 1_000_000) throw new FkException("jumlah log mesin di luar batas (maksimal 1.000.000)");
        return (int)val;
    }

    /// <summary>Menandai log baru sebagai terbaca di mesin (MENGUBAH mesin; hanya setelah data aman tersimpan).</summary>
    public static async Task MarkRead(this FkClient c)
    {
        var (code, _) = await c.SendCommand(Cmd.Mark, 2);
        if (code != 1) throw new FkException($"tandai-terbaca ditolak (kode {code:X2})");
    }

    sealed record Scored(Layout L, double S);

    static List<Layout> Candidates(FkClient c, DeviceInfo? info, PullOptions o)
    {
        if (o.Layout != "" && !o.Layout.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return [Layouts.ByName(o.Layout) ?? throw new FkException($"tata letak \"{o.Layout}\" tidak dikenal")];
        var cands = Layouts.All.ToList();
        if (info is { Dialect: not "" } && Layouts.DialectLayout.TryGetValue(info.Dialect, out var hint))
        {
            c.Trace($"== dialek mesin {info.Dialect} -> tata letak yang diduga {hint}");
            cands = [.. cands.Where(l => l.Name == hint), .. cands.Where(l => l.Name != hint)];
        }
        return cands;
    }

    // Membaca count rekaman: sampel awal, pilih tata letak, baca penuh. locked != null = tanpa deteksi ulang.
    static async Task<(List<Punch> ps, Scored best, int skipped)> PullStream(FkClient c, byte cmd, int count, List<Layout> cands, PullOptions o, Scored? locked)
    {
        // Sampel minimal cukup untuk beberapa rekaman tata letak yang diduga (hasil kecil, mis. 3 log, tetap terdeteksi).
        int sampleLen = Math.Min(Math.Max(count * 8, cands[0].Size * Math.Min(count, 4)), 16384);
        byte[] sample = [];
        try { sample = await c.ReadData(cmd, 0, sampleLen); }
        catch (Exception e) when (e is FkException or TimeoutException) { throw new FkException($"baca sampel: {e.Message}"); }
        o.Dump?.Invoke("sample", sample);
        var best = locked;
        if (best == null)
        {
            var scores = cands.Where(l => sample.Length / l.Size >= 1).Select(l => new Scored(l, Layouts.Score(sample, l)))
                .OrderByDescending(s => s.S).ToList(); // OrderBy stabil
            foreach (var s in scores) c.Trace($"   tata letak {s.L.Name,-4} ({s.L.Note}): skor {s.S.ToString("0.00", CultureInfo.InvariantCulture)}");
            if (scores.Count == 0 || scores[0].S < 0.9)
                throw new FkException($"tidak ada tata letak yang cocok (skor terbaik {(scores.Count > 0 ? scores[0].S : 0):0.00}) — kirim berkas sample.bin untuk dianalisis");
            best = scores[0];
        }
        int size = best.L.Size;
        c.Trace($"== dipilih {best.L.Name} ({size} byte/rekaman); membaca {count} rekaman");
        byte[] data;
        if (size * count <= sample.Length) data = sample[..(size * count)];
        else
        {
            try { data = await c.ReadData(cmd, 0, size * count); }
            catch (Exception e) when (e is FkException or TimeoutException) { throw new FkException($"baca log penuh: {e.Message}"); }
            o.Dump?.Invoke("log-" + best.L.Name, data);
        }
        var (ps, valid, total) = Layouts.ParseAll(data, best.L);
        c.Trace($"== {valid} rekaman sah, {total - valid} dilewati");
        return (ps, best, total - valid);
    }

    /// <summary>
    /// Menarik log. Mesin mengirim aliran byte yang sama berapa pun ukuran yang diminta, jadi: baca sampel awal,
    /// pilih tata letak yang menghasilkan tanggal masuk akal, baca seluruh log. Dengan From/To hanya rentang itu.
    /// </summary>
    public static async Task<PullResult> PullLogs(this FkClient c, DeviceInfo? info, PullOptions o)
    {
        var cands = Candidates(c, info, o);
        if (o.From is { } from0)
        {
            try { return await PullRange(c, o, cands); }
            catch (Exception e) when (e is FkException or TimeoutException)
            {
                // 0xAE/0xAF belum terbukti di mesin nyata (DLL asli memakai stride 32 tetapi mem-parsing 48 byte).
                // Jatuh ke tarik penuh (terbukti) lalu saring tanggal di PC.
                c.Trace($"== rentang di mesin gagal ({e.Message}); tarik semua log lalu saring di PC");
            }
        }
        var res = new PullResult();
        int count = await c.Count(!o.NewOnly);
        c.Trace($"== jumlah log menurut mesin: {count} ({(o.NewOnly ? "baru" : "semua")})");
        res.Count = count;
        if (count == 0) return res;
        var (ps, best, skipped) = await PullStream(c, o.NewOnly ? Cmd.ReadNew : Cmd.ReadAll, count, cands, o, null);
        (res.Layout, res.Score, res.Punches, res.Skipped) = (best.L.Name, best.S, ps, skipped);
        if (o.From != null) res.Punches = FilterDays(res.Punches, o.From.Value, o.To);
        return res;
    }

    /// <summary>Baca sampel awal log dan beri skor tiap tata letak (baca-saja; untuk uji koneksi).</summary>
    public static async Task<(byte[] sample, List<(string name, string note, double score)> scores)> ProbeLayouts(this FkClient c, DeviceInfo info, int count)
    {
        var cands = Candidates(c, info, new PullOptions());
        int len = Math.Min(Math.Max(count * 8, cands[0].Size * Math.Min(count, 4)), 16384);
        var sample = await c.ReadData(Cmd.ReadAll, 0, len);
        var scores = cands.Where(l => sample.Length / l.Size >= 1).Select(l => (l.Name, l.Note, Layouts.Score(sample, l)))
            .OrderByDescending(s => s.Item3).ToList();
        return (sample, scores);
    }

    /// <summary>Sisakan punch pada [from 00:00:00, to 23:59:59]; to null = hari ini.</summary>
    public static List<Punch> FilterDays(List<Punch> ps, DateTime from, DateTime? to)
    {
        var a = from.Date;
        var b = (to ?? DateTime.Now).Date.AddDays(1).AddSeconds(-1);
        return ps.Where(p => p.At >= a && p.At <= b).ToList();
    }

    static async Task<PullResult> PullRange(FkClient c, PullOptions o, List<Layout> cands)
    {
        var res = new PullResult();
        var from = o.From!.Value.Date;
        var to = (o.To ?? DateTime.Now).Date;
        if (to < from) throw new FkException("tanggal akhir sebelum tanggal awal");
        c.Trace($"== rentang {from:yyyy-MM-dd} s/d {to:yyyy-MM-dd}");
        Scored? locked = null;
        for (int y = from.Year; y <= to.Year; y++) // pustaka asli memakai tahun AWAL untuk kedua sisi: pecah per tahun
        {
            var a = y == from.Year ? from : new DateTime(y, 1, 1);
            var b = y == to.Year ? to : new DateTime(y, 12, 31);
            int count = await Features.RangeCount(c, a, b);
            c.Trace($"== {a:yyyy-MM-dd}..{b:yyyy-MM-dd}: mesin menjawab {count} log");
            res.Count += count;
            if (count == 0) continue;
            var (ps, best, skipped) = await PullStream(c, Cmd.RangeRead, count, cands, o, locked);
            locked = best;
            (res.Layout, res.Score) = (best.L.Name, best.S);
            res.Skipped += skipped;
            res.Punches.AddRange(ps);
        }
        res.Punches = FilterDays(res.Punches, from, to);
        return res;
    }
}
