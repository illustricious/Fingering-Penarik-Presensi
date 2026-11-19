// Fitur tambahan klien FK: jam, produk, daftar user, nama, sidik jari, rentang tanggal.
// Port dari presensi-hub/internal/fk/features.go. Belum terbukti di mesin nyata kecuali dinyatakan di PROTOKOL-FK.md.
using System.Buffers.Binary;
using System.Text;

namespace Fingering.Core;

public sealed record UserEntry(string Id, int Privilege, int Enabled, ushort Mask)
{
    /// <summary>Jumlah sidik jari terdaftar menurut mask (bit 3..6).</summary>
    public int Fingers => Mask >> 3 & 0xf;
}

public sealed class ProductData
{
    public byte[] Raw = [];
    public Dictionary<string, string> Strings = [];
    public int Number;
}

public static class Features
{
    static string CStr(ReadOnlySpan<byte> b)
    {
        int i = b.IndexOf((byte)0);
        if (i >= 0) b = b[..i];
        var sb = new StringBuilder();
        foreach (var x in b) sb.Append(x is >= 0x20 and < 0x7f ? (char)x : ' ');
        return sb.ToString().Trim();
    }

    static byte[] IdBuf(string id)
    {
        var b = new byte[0x30];
        Encoding.Latin1.GetBytes(id, 0, Math.Min(id.Length, 0x2f), b, 0);
        return b;
    }

    /// <summary>MENGHAPUS semua log di mesin, tidak bisa dibatalkan. Panggil hanya lewat Hub.ClearLogs (yang memverifikasi cadangan).</summary>
    public static async Task ClearLogs(this FkClient c)
    {
        var r = await c.Request(Cmd.ClearLogs, 0, (ushort)c.LastVal, 0xFFFF, 0, null, 0, TimeSpan.FromSeconds(26));
        if (r.code != 1) throw new FkException($"hapus log ditolak mesin (kode {r.code:X2})");
    }

    /// <summary>
    /// MENGOSONGKAN semua data enroll di mesin (semua user, sidik jari, sandi, kartu); log presensi TIDAK ikut terhapus. Tidak bisa dibatalkan.
    /// Panggil hanya lewat Hub.EmptyEnroll (yang mencadangkan semua data enroll ke PC dan memverifikasinya lebih dulu).
    /// </summary>
    public static async Task EmptyEnrollData(this FkClient c)
    {
        var r = await c.Request(Cmd.EmptyEnroll, 1, (ushort)c.LastVal, 0xFFFF, 0, null, 0, TimeSpan.FromSeconds(300));
        if (r.code != 1) throw new FkException($"kosongkan data user ditolak mesin (kode {r.code:X2})");
    }

    // ---- jam mesin ----

    public static async Task<DateTime> DeviceTime(this FkClient c)
    {
        var b = await c.ReadData(Cmd.TimeGet, 0, 8);
        return Layouts.MkTime(BinaryPrimitives.ReadUInt16LittleEndian(b), b[2], b[3], b[5], b[6], b[7])
            ?? throw new FkException($"jam mesin tidak masuk akal: {Convert.ToHexString(b)}");
    }

    /// <summary>MENGUBAH mesin; panggil hanya atas permintaan eksplisit.</summary>
    public static async Task SetDeviceTime(this FkClient c, DateTime t)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)t.Year);
        b[2] = (byte)t.Month; b[3] = (byte)t.Day; b[4] = (byte)t.DayOfWeek;
        b[5] = (byte)t.Hour; b[6] = (byte)t.Minute; b[7] = (byte)t.Second;
        var (code, _) = await c.WriteData(Cmd.TimeSet, 0, b);
        if (code != 1) throw new FkException($"atur jam ditolak (kode {code:X2})");
    }

    // ---- produk ----

    public static async Task<ProductData> Product(this FkClient c)
    {
        var b = await c.ReadData(Cmd.Product, 0, 0x100);
        return new ProductData
        {
            Raw = b,
            Number = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(0xE0)),
            Strings = new()
            {
                ["p3"] = CStr(b.AsSpan(0x00, 0x20)), ["p4"] = CStr(b.AsSpan(0x20, 0x20)), ["p5"] = CStr(b.AsSpan(0x40, 0x20)),
                ["p1"] = CStr(b.AsSpan(0x60, 0x20)), ["p6"] = CStr(b.AsSpan(0x80, 0x20)), ["p7"] = CStr(b.AsSpan(0xA0, 0x40)),
            },
        };
    }

    // ---- user ----

    /// <summary>Daftar seluruh user (tanpa nama).</summary>
    public static async Task<List<UserEntry>> ReadUsers(this FkClient c)
    {
        int total = 0;
        foreach (uint idx in new uint[] { 2, 1 })
        {
            var (code, val) = await c.SendCommand(Cmd.Status, idx);
            if (code != 1) throw new FkException($"status {idx} ditolak (kode {code:X2})");
            c.Trace($"== status[{idx}] = {val}");
            if (val > 100_000 || total + (long)val > 100_000) throw new FkException("jumlah user mesin di luar batas (maksimal 100.000)");
            total += (int)val;
        }
        var outp = new List<UserEntry>();
        if (total == 0) return outp;
        var data = await c.ReadData(Cmd.UserList, 0, total * 36);
        for (int i = 0; i + 36 <= data.Length; i += 36)
        {
            var id = Layouts.IdText(data.AsSpan(i, 32));
            if (id == "") continue;
            outp.Add(new UserEntry(id, data[i + 0x20], data[i + 0x21], BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(i + 0x22))));
        }
        return outp;
    }

    /// <summary>Nama user (dan byte info mentah).</summary>
    public static async Task<(string name, byte[] raw)> UserInfo(this FkClient c, string id, DateTime now)
    {
        var tag = (ushort)(((now.Year - 2000) & 0x7f) << 9 | (now.Month & 0xf) << 5 | 0x14);
        var raw = await c.WriteRead(Cmd.UserInfo, tag, IdBuf(id), 0xCC);
        if (raw.Length < 0xCC) throw new FkException($"info user terlalu pendek ({raw.Length} byte)");
        return (DecodeName(raw.AsSpan(36, 128)), raw);
    }

    /// <summary>
    /// Kolom nama bisa teks 8-bit (NUL-terminated) atau UTF-16LE ("A\0l\0i\0"); memotong di NUL pertama pada
    /// kasus kedua hanya menyisakan huruf depan.
    /// </summary>
    public static string DecodeName(ReadOnlySpan<byte> b)
    {
        if (LooksUtf16(b))
        {
            var sb = new StringBuilder();
            for (int i = 0; i + 1 < b.Length; i += 2)
            {
                var ch = BinaryPrimitives.ReadUInt16LittleEndian(b[i..]);
                if (ch == 0) break;
                sb.Append((char)ch);
            }
            return sb.ToString().Trim();
        }
        int z = b.IndexOf((byte)0);
        if (z >= 0) b = b[..z];
        try { return new UTF8Encoding(false, true).GetString(b).Trim(); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(b).Trim(); }
    }

    static bool LooksUtf16(ReadOnlySpan<byte> b) =>
        b.Length >= 2 && b[0] != 0 && b[1] == 0 && (b.Length < 4 || b[2] == 0 || b[3] == 0);

    // ---- sidik jari ----

    /// <summary>Ukuran respons data enroll menurut nomor backup (dari CIF13_1::GetEnrollData).</summary>
    public static int EnrollSize(int backup) => backup switch
    {
        10 or 11 => 0x54,
        < 10 => 0x674,
        12 or (>= 13 and <= 16) or 0x32 => 0x4e54,
        >= 20 and < 30 => 0xbec,
        _ => 0,
    };

    public static Task<byte[]> EnrollData(this FkClient c, string id, int backup)
    {
        int n = EnrollSize(backup);
        if (n == 0) throw new FkException($"nomor backup {backup} tidak dikenal");
        return c.WriteRead(Cmd.Enroll, (ushort)backup, IdBuf(id), n);
    }

    // ---- tulis user (MENGUBAH mesin) ----

    /// <summary>Nomor backup yang dicoba saat menghapus seluruh data seorang user: jari 0-9, sandi 10, kartu 11, wajah 12, lainnya.</summary>
    public static readonly int[] AllBackups = [.. Enumerable.Range(0, 13), .. Enumerable.Range(13, 4), .. Enumerable.Range(20, 10)];

    /// <summary>Hapus satu data enroll (nomor backup) seorang user. Mengembalikan kode balasan mesin (1 = berhasil).</summary>
    public static async Task<byte> DeleteEnrollData(this FkClient c, string id, int backup)
    {
        c.LastVal = (uint)backup;
        var (code, _) = await c.WriteData(Cmd.DeleteEnroll, 1, IdBuf(id));
        return code;
    }

    /// <summary>Ubah nama user di mesin. Nama ditulis UTF-16LE di offset 36 (maks 63 karakter), sama seperti DLL asli (ANSI -> UTF-16).</summary>
    public static async Task SetUserName(this FkClient c, string id, string name)
    {
        var b = new byte[0xCC];
        Encoding.Latin1.GetBytes(id, 0, Math.Min(id.Length, 32), b, 0);
        var u = Encoding.Unicode.GetBytes(name.Length > 63 ? name[..63] : name);
        u.CopyTo(b, 36);
        var (code, _) = await c.WriteData(Cmd.SetUserInfo, 0x15, b);
        if (code != 1) throw new FkException($"ubah nama ditolak mesin (kode {code:X2})");
    }

    /// <summary>Ubah hak akses user (0 = user biasa; nilai lain sesuai mesin).</summary>
    public static async Task SetPrivilege(this FkClient c, string id, int privilege)
    {
        if (privilege is < 0 or > 0x3c) throw new FkException("hak akses di luar rentang");
        c.LastVal = (uint)privilege;
        var (code, _) = await c.WriteData(Cmd.SetPrivilege, 1, IdBuf(id));
        if (code != 1) throw new FkException($"ubah hak akses ditolak mesin (kode {code:X2})");
    }

    /// <summary>
    /// Tulis data enroll sandi (backup 10) atau kartu (11): rekaman 84 byte = ID[32], backup@32, hak@50, data[32]@52.
    /// Format data sandi (teks atau biner) BELUM terbukti; hasil selalu diverifikasi dengan membaca ulang daftar user.
    /// </summary>
    public static async Task PutSimpleEnroll(this FkClient c, string id, int backup, int privilege, byte[] data32)
    {
        if (backup is not (10 or 11)) throw new FkException("hanya sandi (10) dan kartu (11) yang didukung");
        var b = new byte[0x54];
        Encoding.Latin1.GetBytes(id, 0, Math.Min(id.Length, 32), b, 0);
        b[32] = (byte)backup; b[50] = (byte)privilege;
        data32.AsSpan(0, Math.Min(32, data32.Length)).CopyTo(b.AsSpan(52));
        c.LastVal = (uint)privilege;
        var (code, _) = await c.WriteData(Cmd.PutEnroll, (uint)backup, b);
        if (code != 1) throw new FkException($"tulis data enroll ditolak mesin (kode {code:X2})");
    }

    // ---- log per rentang tanggal ----

    /// <summary>12 byte: tahun u16, bulan, hari (awal); tahun u16 (tahun AWAL), bulan, hari (akhir); 4 byte nol.</summary>
    public static byte[] RangeStruct(DateTime from, DateTime to)
    {
        var b = new byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)from.Year);
        b[2] = (byte)from.Month; b[3] = (byte)from.Day;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), (ushort)from.Year);
        b[6] = (byte)to.Month; b[7] = (byte)to.Day;
        return b;
    }

    public static async Task<int> RangeCount(FkClient c, DateTime from, DateTime to)
    {
        var (code, val) = await c.WriteData(Cmd.RangeSet, 0, RangeStruct(from, to));
        if (code != 1)
        {
            if (val == 10) return 0; // jawaban "tidak ada data"
            throw new FkException($"rentang ditolak (kode {code:X2}, nilai {val})");
        }
        if (val > 1_000_000) throw new FkException("jumlah log rentang di luar batas (maksimal 1.000.000)");
        return (int)val;
    }
}
