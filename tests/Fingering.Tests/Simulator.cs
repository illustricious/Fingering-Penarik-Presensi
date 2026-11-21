// Mesin tiruan menurut TAFSIRAN protokol (PROTOKOL-FK.md). Membuktikan konsistensi kode, BUKAN perilaku mesin asli.
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Fingering.Core;

namespace Fingering.Tests;

public sealed class Simulator : IDisposable
{
    public string Layout = "C48";
    public int Records = 500, Users = 12, Machine = 1;
    public bool Utf16Names = true;
    public bool RejectRange;              // meniru 0xAE ditolak mesin
    public uint CommunicationKey;
    public int SetClockCalls, ClearCalls, DeleteCalls, EmptyCalls;
    public bool RefuseEnrollRead;         // meniru mesin menolak membaca data enroll (cadangan harus gagal)
    public sealed class SimUser { public string Id = "", Name = ""; public int Priv; public ushort Mask; public byte[] PasswordBytes = []; public bool RefusePassword; }
    public int PasswordReadCalls;
    public readonly System.Collections.Concurrent.ConcurrentDictionary<byte, int> CommandCounts = new();
    public readonly List<SimUser> Roster = [];
    public readonly List<DateTime> Times = [];

    TcpListener? ln;
    byte[] logs = [];
    int size;
    DateTime devTime = new(2026, 10, 31, 8, 30, 15);
    static readonly Dictionary<string, string> Dialect = new() { ["B12"] = "11", ["A12"] = "10", ["B20"] = "12_1", ["C32"] = "13", ["C48"] = "13_1" };

    public int Port => ((IPEndPoint)ln!.LocalEndpoint).Port;
    public static string NameOf(int i) => $"Pegawai Contoh {i}";

    public Simulator Start()
    {
        var l = Layouts.ByName(Layout)!;
        size = l.Size;
        var ms = new MemoryStream();
        var baseT = new DateTime(2026, 9, 1, 7, 0, 0);
        for (int i = 0; i < Records; i++)
        {
            var t = baseT.AddMinutes(i * 41).AddSeconds(i % 53);
            ms.Write(Encode(Layout, (uint)(i % Users + 1), t, i % 2));
            Times.Add(t);
        }
        logs = ms.ToArray();
        for (int i = 1; i <= Users; i++) Roster.Add(new SimUser { Id = i.ToString(), Name = NameOf(i), Priv = i % 2, Mask = 2 << 3 });
        ln = new TcpListener(IPAddress.Loopback, 0);
        ln.Start();
        _ = Task.Run(async () => { while (true) { var c = await ln.AcceptTcpClientAsync(); _ = Task.Run(() => Serve(c)); } });
        return this;
    }

    /// <summary>Menambah satu scan baru di akhir log (meniru pegawai menempel jari).</summary>
    public void AddRecord(uint pin, DateTime t)
    {
        logs = [.. logs, .. Encode(Layout, pin, t, 0)];
        Times.Add(t);
        Records++;
    }

    public void Dispose() => ln?.Stop();

    /// <summary>
    /// Meniru pegawai menempel jari. TAFSIRAN: mesin mencocokkan jari terhadap template di dalam dirinya; bila PIN tidak terdaftar
    /// (mis. setelah data enroll dikosongkan) tidak ada log yang tercatat. Mengembalikan true bila scan tercatat.
    /// </summary>
    public bool TryPunch(uint pin, DateTime t)
    {
        if (Roster.All(r => r.Id != pin.ToString())) return false;
        AddRecord(pin, t);
        return true;
    }

    public static byte[] Encode(string layout, uint pin, DateTime t, int inout)
    {
        byte[] b;
        switch (layout)
        {
            case "C32":
                b = new byte[32]; Encoding.ASCII.GetBytes(pin.ToString()).CopyTo(b, 0);
                b[18] = (byte)inout; b[19] = (byte)t.Second; BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(20), Layouts.PackTime(t)); return b;
            case "C48":
                b = new byte[48]; Encoding.ASCII.GetBytes(pin.ToString()).CopyTo(b, 0);
                b[35] = (byte)t.Second; BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(36), Layouts.PackTime(t));
                b[43] = (byte)(1 + inout); b[47] = 0x10; return b;
            default: // B12
                b = new byte[12]; BinaryPrimitives.WriteUInt32LittleEndian(b, pin);
                b[5] = (byte)inout; b[6] = 1; b[7] = (byte)t.Second; BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), Layouts.PackTime(t)); return b;
        }
    }

    static byte[] Slice(byte[] b, int idx, int ln)
    {
        var o = new byte[ln];
        int off = idx * FkClient.Block;
        if (off < b.Length) Array.Copy(b, off, o, 0, Math.Min(ln, b.Length - off));
        return o;
    }

    static bool IsWrite(byte cmd, uint param, int ln) =>
        ln > 0 && (cmd is Cmd.RangeSet or Cmd.TimeSet || (cmd is Cmd.UserInfo or Cmd.Enroll && param == 0) || cmd is Cmd.DeleteEnroll or Cmd.SetPrivilege or Cmd.PutEnroll or Cmd.SetUserInfo);

    async Task Serve(TcpClient c)
    {
        using var _ = c;
        var ns = c.GetStream();
        var buf = new List<byte>();
        var tmp = new byte[4096];
        byte[] rangeData = [];
        string pending = "";
        int pendingTag = 0;
        async Task<bool> Fill(int n)
        {
            while (buf.Count < n) { int k = await ns.ReadAsync(tmp); if (k == 0) return false; buf.AddRange(tmp.AsSpan(0, k).ToArray()); }
            return true;
        }
        try
        {
            while (true)
            {
                if (!await Fill(16)) return;
                if (buf[0] != 0x55 || buf[1] != 0xAA) { buf.RemoveAt(0); continue; }
                var h = buf.GetRange(0, 16).ToArray(); buf.RemoveRange(0, 16);
                byte cmd = h[3];
                CommandCounts.AddOrUpdate(cmd, 1, (_, count) => count + 1);
                uint param = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(4));
                int idx = BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(10)), ln = BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(12));
                ushort seq = BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(14));
                byte[] body = [];
                if (IsWrite(cmd, param, ln))
                {
                    if (!await Fill(2 + ln + 2)) return;
                    body = buf.GetRange(2, ln).ToArray(); buf.RemoveRange(0, 2 + ln + 2);
                }
                byte code = 1; uint val = 0xBEEF0000 | seq; byte[]? data = null;
                switch (cmd)
                {
                    case Cmd.Login: if (param != CommunicationKey) code = 0; break;
                    case Cmd.Status:
                        val = param switch { 8 => (uint)Records, 6 => (uint)(Records / 10), 2 => (uint)Roster.Count, 1 => 0, _ => val };
                        break;
                    case Cmd.Info:
                        if (param == 0)
                        {
                            data = new byte[0x30];
                            BinaryPrimitives.WriteUInt16LittleEndian(data, 0x104);
                            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x2e), 64);
                        }
                        else { data = new byte[64]; Encoding.ASCII.GetBytes("{CommunicationInterfaceId:" + Dialect[Layout] + ",Serial:SIM0001}").CopyTo(data, 0); }
                        data = Slice(data, idx, ln);
                        break;
                    case Cmd.ReadAll: data = Slice(logs, idx, ln); break;
                    case Cmd.RangeSet:
                        if (RejectRange) { code = 0; val = 3; break; }
                        {
                            var from = new DateTime(BinaryPrimitives.ReadUInt16LittleEndian(body), body[2], body[3]);
                            var to = new DateTime(from.Year, body[6], body[7], 23, 59, 59);
                            var ms = new MemoryStream(); uint n = 0;
                            for (int i = 0; i < Times.Count; i++)
                                if (Times[i] >= from && Times[i] <= to) { ms.Write(Encode(Layout, (uint)(i % Users + 1), Times[i], i % 2)); n++; }
                            rangeData = ms.ToArray();
                            if (n == 0) { code = 0; val = 10; } else val = n;
                        }
                        break;
                    case Cmd.ClearLogs: logs = []; Records = 0; ClearCalls++; break;   // hanya log; Roster (user + sidik jari) tetap
                    case Cmd.EmptyEnroll:                                              // hanya data enroll; log tetap. param 0 (ClearKeeperData) tidak ditiru
                        if (param == 1) { Roster.Clear(); EmptyCalls++; } else code = 0;
                        break;
                    case Cmd.Enroll:
                        if (param == 0) { pending = Encoding.ASCII.GetString(body).TrimEnd('\0'); pendingTag = BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(8)); }
                        else
                        {
                            if (RefuseEnrollRead) { code = 0; break; }
                            var d = new byte[Features.EnrollSize(pendingTag)];
                            if (pendingTag == 10)
                            {
                                Interlocked.Increment(ref PasswordReadCalls);
                                var user = Roster.FirstOrDefault(u => u.Id == pending);
                                if (user == null || (user.Mask & 1) == 0 || user.RefusePassword) { code = 0; break; }
                                Encoding.ASCII.GetBytes(user.Id).CopyTo(d, 0);
                                d[32] = 10; d[50] = (byte)user.Priv;
                                user.PasswordBytes.AsSpan(0, Math.Min(32, user.PasswordBytes.Length)).CopyTo(d.AsSpan(52));
                            }
                            else for (int i = 0; i < d.Length; i++) d[i] = (byte)(i + pendingTag);
                            data = Slice(d, idx, ln);
                        }
                        break;
                    case Cmd.DeleteEnroll:
                        {
                            var id = Encoding.ASCII.GetString(body).TrimEnd('\0');
                            if (BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(8)) == 10) Roster.RemoveAll(r => r.Id == id);
                            DeleteCalls++;
                        }
                        break;
                    case Cmd.PutEnroll:
                        {
                            var id = Encoding.ASCII.GetString(body, 0, 32).TrimEnd('\0');
                            if (Roster.All(r => r.Id != id)) Roster.Add(new SimUser { Id = id, Priv = body[50], Mask = 1 });
                            if (body[32] == 10)
                            {
                                var user = Roster.First(r => r.Id == id);
                                user.Mask |= 1; user.PasswordBytes = body[52..84];
                            }
                        }
                        break;
                    case Cmd.SetUserInfo:
                        {
                            var id = Encoding.ASCII.GetString(body, 0, 32).TrimEnd('\0');
                            var u = Roster.FirstOrDefault(r => r.Id == id);
                            if (u == null) code = 0; else u.Name = Encoding.Unicode.GetString(body, 36, 128).Split('\0')[0];
                        }
                        break;
                    case Cmd.SetPrivilege:
                        {
                            var id = Encoding.ASCII.GetString(body, 0, 32).TrimEnd('\0');
                            var u = Roster.FirstOrDefault(r => r.Id == id);
                            if (u != null) u.Priv = BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(8));
                        }
                        break;
                    case Cmd.RangeRead: data = Slice(rangeData, idx, ln); break;
                    case Cmd.TimeGet:
                        data = new byte[8];
                        BinaryPrimitives.WriteUInt16LittleEndian(data, (ushort)devTime.Year);
                        data[2] = (byte)devTime.Month; data[3] = (byte)devTime.Day; data[4] = (byte)devTime.DayOfWeek;
                        data[5] = (byte)devTime.Hour; data[6] = (byte)devTime.Minute; data[7] = (byte)devTime.Second;
                        data = Slice(data, idx, ln);
                        break;
                    case Cmd.TimeSet:
                        devTime = new DateTime(BinaryPrimitives.ReadUInt16LittleEndian(body), body[2], body[3], body[5], body[6], body[7]);
                        SetClockCalls++;
                        break;
                    case Cmd.UserList:
                        {
                            var ms = new MemoryStream();
                            foreach (var u in Roster.ToList())
                            {
                                var e = new byte[36]; Encoding.ASCII.GetBytes(u.Id).CopyTo(e, 0);
                                e[0x20] = (byte)u.Priv; e[0x21] = 1; BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(0x22), u.Mask);
                                ms.Write(e);
                            }
                            data = Slice(ms.ToArray(), idx, ln);
                        }
                        break;
                    case Cmd.UserInfo:
                        if (param == 0) pending = Encoding.ASCII.GetString(body).TrimEnd('\0');
                        else
                        {
                            var d = new byte[0xCC];
                            Encoding.ASCII.GetBytes(pending).CopyTo(d, 0);
                            var name = Roster.FirstOrDefault(r => r.Id == pending)?.Name ?? "?";
                            (Utf16Names ? Encoding.Unicode : Encoding.UTF8).GetBytes(name).CopyTo(d, 36);
                            data = Slice(d, idx, ln);
                        }
                        break;
                    default: code = 0; break;
                }
                var resp = new List<byte> { 0xAA, 0x55, (byte)Machine, code };
                resp.AddRange(BitConverter.GetBytes(val));
                resp.AddRange(BitConverter.GetBytes(seq));
                if (data != null && code == 1) { resp.Add(0x55); resp.Add(0xAA); resp.AddRange(data); }
                await ns.WriteAsync(resp.ToArray());
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or SocketException) { }
    }
}
