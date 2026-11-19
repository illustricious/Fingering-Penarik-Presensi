// Klien protokol "FK" (mesin Revo dkk) lewat TCP. Port dari presensi-hub/internal/fk/client.go.
// Format paket: ../PROTOKOL-FK.md. Terbukti pada Revo W-230N: login, info, hitung, baca semua log.
using System.Buffers.Binary;
using System.Net.Sockets;

namespace Fingering.Core;

public static class Cmd
{
    public const byte Info = 0x13, Login = 0x80, ReadNew = 0xA1, Mark = 0xA2, ReadAll = 0xA4, Status = 0xB4;
    public const byte Enroll = 0x91, UserList = 0x97, RangeSet = 0xAE, RangeRead = 0xAF, TimeGet = 0xB2, TimeSet = 0xB3;
    public const byte Product = 0xC3, UserInfo = 0xE8;
    /// <summary>Hapus SEMUA log di mesin (FK_EmptyGeneralLogData, slot 0x64 CIF13_1; param 0, timeout 26 dtk). Belum diuji di mesin nyata.</summary>
    public const byte ClearLogs = 0xA8;
    /// <summary>Tulis/hapus data user (hasil bongkar CIF13_1; belum diuji di mesin nyata): 0x93 hapus satu data enroll, 0x96 hak akses,
    /// 0x98 tulis data enroll (84 byte untuk sandi/kartu), 0xE9 tulis info user (nama UTF-16).</summary>
    public const byte DeleteEnroll = 0x93, SetPrivilege = 0x96, PutEnroll = 0x98, SetUserInfo = 0xE9;
    /// <summary>Kosongkan SEMUA data enroll (user + sidik jari + sandi + kartu) di mesin: FK_EmptyEnrollData = 0xC1, param 1, timeout 300 dtk
    /// (hasil bongkar FKAttend.dll, slot 0xA0 CIF13_1, cara sama dengan 0xA8; TAFSIRAN dari DLL, belum diuji di mesin nyata). 0xC1 dengan param 0 = FK_ClearKeeperData (hapus semua); TIDAK dipakai.</summary>
    public const byte EmptyEnroll = 0xC1;
}

public class FkException(string message) : Exception(message);

public sealed class FkConfig
{
    public string Host = "";
    public int Port = 5005;
    public int Machine = 1;
    public uint Key;
    public TimeSpan Timeout = TimeSpan.FromSeconds(8);
    public Action<string>? Trace;
    /// <summary>Dipanggil (0..1) saat membaca data besar (>= 16 potongan), mis. seluruh log; dipakai untuk bar progres.</summary>
    public Action<double>? Progress;
}

public sealed class FkClient : IDisposable
{
    const int Header = 16, RespHeader = 10, MaxFrame = 64000;
    public const int Block = 1024;

    public readonly FkConfig Cfg;
    readonly TcpClient tcp;
    readonly NetworkStream ns;
    readonly CancellationToken ct;
    ushort seq;
    public uint LastVal;
    byte[] buf = new byte[0];

    FkClient(FkConfig cfg, TcpClient tcp, CancellationToken ct)
    {
        Cfg = cfg; this.tcp = tcp; ns = tcp.GetStream(); this.ct = ct;
    }

    public static async Task<FkClient> DialAsync(FkConfig cfg, CancellationToken ct = default)
    {
        var tcp = new TcpClient { NoDelay = true };
        tcp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        var addr = $"{cfg.Host}:{cfg.Port}";
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(cfg.Timeout.TotalSeconds, 2)));
        try { await tcp.ConnectAsync(cfg.Host, cfg.Port, cts.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { tcp.Dispose(); throw new FkException($"sambung tcp {addr}: timeout"); }
        catch (Exception e) when (e is SocketException or IOException) { tcp.Dispose(); throw new FkException($"sambung tcp {addr}: {e.Message}"); }
        catch { tcp.Dispose(); throw; }
        var c = new FkClient(cfg, tcp, ct);
        c.Trace($"== terhubung tcp {addr} (mesin #{cfg.Machine})");
        return c;
    }

    public void Dispose() => tcp.Dispose();

    public void Trace(string line) => Cfg.Trace?.Invoke(line);

    void Hex(string dir, ReadOnlySpan<byte> b)
    {
        if (Cfg.Trace == null) return;
        var show = b.Length > 48 ? b[..48] : b;
        var more = b.Length > 48 ? $" ...(+{b.Length - 48} byte)" : "";
        Trace($"{dir} {b.Length,5}  {Convert.ToHexString(show).Chunk(2).Select(c => new string(c)).Aggregate((x, y) => x + " " + y)}{more}");
    }

    // (code, val, payload, used) atau null bila belum lengkap
    (byte code, uint val, byte[]? payload, int used)? Extract(int dataLen)
    {
        var b = buf;
        for (int i = 0; i + RespHeader <= b.Length; i++)
        {
            if (b[i] != 0xAA || b[i + 1] != 0x55) continue;
            var s = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(i + 8));
            if (s != 0 && s != seq) continue; // balasan untuk permintaan lain
            var code = b[i + 3];
            var val = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(i + 4));
            if (code == 1 && dataLen > 0)
            {
                int need = i + 12 + dataLen;
                if (b.Length < need) return null;
                if (b[i + 10] != 0x55 || b[i + 11] != 0xAA)
                    throw new FkException($"fk: penanda data 55 AA tidak ada (dapat {b[i + 10]:X2} {b[i + 11]:X2})");
                return (code, val, b[(i + 12)..need], need);
            }
            return (code, val, null, i + RespHeader);
        }
        return null;
    }

    async Task<(byte code, uint val, byte[]? payload)> ReadFrame(int dataLen, TimeSpan timeout, bool sensitive)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var tmp = new byte[70000];
        while (true)
        {
            var r = Extract(dataLen);
            if (r is { } f)
            {
                buf = buf[f.used..];
                return (f.code, f.val, f.payload);
            }
            int n;
            try { n = await ns.ReadAsync(tmp, cts.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("fk: timeout menunggu mesin"); }
            catch (IOException e) { throw new FkException($"terima: {e.Message}"); }
            if (n == 0) throw new FkException("fk: koneksi ditutup mesin");
            if (!sensitive) Hex("RX", tmp.AsSpan(0, n));
            buf = [.. buf, .. tmp.AsSpan(0, n)];
            if (buf.Length > 4 * MaxFrame) buf = buf[^MaxFrame..];
        }
    }

    /// <summary>Satu paket: kirim lalu tunggu respons. dataLen = jumlah byte data yang diharapkan di balasan.</summary>
    public async Task<(byte code, uint val, byte[]? payload)> Request(byte cmd, uint param, ushort a, ushort b, ushort length, byte[]? data, int dataLen, TimeSpan timeout)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            seq++;
            if (seq == 0) seq = 1;
            var frame = new byte[Header + (data is { Length: > 0 } ? 4 + data.Length : 0)];
            frame[0] = 0x55; frame[1] = 0xAA; frame[2] = (byte)Cfg.Machine; frame[3] = cmd;
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4), param);
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(8), a);
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(10), b);
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(12), length);
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(14), seq);
            if (data is { Length: > 0 })
            {
                frame[16] = 0x55; frame[17] = 0xAA;
                data.CopyTo(frame, 18);
            }
            bool sensitive = cmd is Cmd.Login or Cmd.PutEnroll or Cmd.Enroll;
            if (sensitive) Trace($"TX cmd={cmd:X2} (data rahasia disembunyikan)");
            else Hex("TX", frame);
            try { await ns.WriteAsync(frame, ct); }
            catch (IOException e) { throw new FkException($"kirim: {e.Message}"); }
            var r = await ReadFrame(dataLen, timeout, sensitive);
            if (r.code == 0xFD)
            {
                Trace($"   (mesin sibuk, ulang {attempt + 1}/3)");
                await Task.Delay(300, ct);
                continue;
            }
            LastVal = r.val;
            return r;
        }
        throw new FkException("fk: mesin sibuk (FD) setelah 3 percobaan");
    }

    public async Task<(byte code, uint val)> SendCommand(byte cmd, uint param)
    {
        var r = await Request(cmd, param, (ushort)LastVal, 0xFFFF, 0, null, 0, Cfg.Timeout);
        return (r.code, r.val);
    }

    TimeSpan ReadTimeout => Cfg.Timeout * (Cfg.Port / 5001 + 1) * 2; // kelipatan timeout seperti pustaka asli

    /// <summary>Baca size byte dengan perintah cmd, per potongan 1024 byte.</summary>
    public async Task<byte[]> ReadData(byte cmd, uint param, int size)
    {
        if (size < 0 || size > Block * 65536) throw new FkException("ukuran data mesin di luar batas protokol (maksimal 64 MiB)");
        using var ms = new MemoryStream(size);
        for (int idx = 0, off = 0; off < size; idx++)
        {
            int n = Math.Min(size - off, Block);
            (byte code, uint val, byte[]? payload) r;
            try { r = await Request(cmd, param, (ushort)LastVal, (ushort)idx, (ushort)n, null, n, ReadTimeout); }
            catch (Exception e) when (e is FkException or TimeoutException)
            { throw new FkException($"baca potongan {idx} (offset {off}/{size}): {e.Message}"); }
            if (r.code != 1) throw new FkException($"baca potongan {idx}: mesin menolak (kode {r.code:X2})");
            ms.Write(r.payload!);
            off += n;
            if (size >= 16 * Block) Cfg.Progress?.Invoke((double)off / size);
        }
        return ms.ToArray();
    }

    /// <summary>Kirim data per potongan 1024 byte. Mengembalikan kode dan nilai balikan terakhir.</summary>
    public async Task<(byte code, uint val)> WriteData(byte cmd, uint param, byte[] data)
    {
        byte code = 0; uint val = 0;
        for (int idx = 0, off = 0; ; idx++)
        {
            int n = Math.Min(data.Length - off, Block);
            var r = await Request(cmd, param, (ushort)LastVal, (ushort)idx, (ushort)n, data[off..(off + n)], 0, ReadTimeout);
            (code, val) = (r.code, r.val);
            if (code != 1) return (code, val);
            off += n;
            if (off >= data.Length) break;
        }
        return (code, val);
    }

    /// <summary>Tulis data (param 0) lalu baca readSize byte (param 1); tag dibawa di u16@8 kedua tahap.</summary>
    public async Task<byte[]> WriteRead(byte cmd, ushort tag, byte[] data, int readSize)
    {
        string last = "tidak diketahui";
        for (int attempt = 0; attempt < 3; attempt++)
        {
            LastVal = tag;
            var (code, _) = await WriteData(cmd, 0, data);
            if (code != 1) { last = $"mesin menolak tahap tulis (kode {code:X2})"; continue; }
            LastVal = tag;
            return await ReadData(cmd, 1, readSize);
        }
        throw new FkException(last);
    }

    public async Task Login()
    {
        var (code, val) = await SendCommand(Cmd.Login, Cfg.Key);
        Trace($"== login: kode={code:X2} nilai={val:X8}");
        if (code != 1) throw new FkException($"login ditolak (kode {code:X2}) — periksa nomor mesin dan kata sandi komunikasi");
    }
}
