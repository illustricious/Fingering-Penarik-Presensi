using System.Text;
using Fingering.Core;
using Xunit;

namespace Fingering.Tests;

public class FkTests
{
    static FkConfig Cfg(Simulator s) => new() { Host = "127.0.0.1", Port = s.Port, Machine = s.Machine, Timeout = TimeSpan.FromSeconds(5) };

    static async Task<(FkClient c, DeviceInfo info)> Open(Simulator s)
    {
        var c = await FkClient.DialAsync(Cfg(s));
        await c.Login();
        return (c, await c.Info());
    }

    [Fact]
    public void RealTraceRecordC48()
    {
        // byte asli dari trace Revo W-230N, 31 Okt 2026
        var rec = new byte[48];
        Encoding.ASCII.GetBytes("173").CopyTo(rec, 0);
        new byte[] { 0x01, 0x00, 0x00, 0x1B, 0xFA, 0xA1, 0xF0, 0x4D, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x10 }.CopyTo(rec, 32);
        var p = Layouts.ByName("C48")!.Parse(rec);
        Assert.NotNull(p);
        Assert.Equal("173", p!.Pin);
        Assert.Equal(new DateTime(2026, 10, 16, 15, 19, 27), p.At);
        Assert.Equal(2, p.Verify);
        new byte[] { 0x01, 0x00, 0x00, 0x12, 0xFA, 0xA1, 0xB0, 0x2A, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x10 }.CopyTo(rec, 32);
        Assert.Equal(new DateTime(2026, 10, 16, 21, 10, 18), Layouts.ByName("C48")!.Parse(rec)!.At);
    }

    [Theory]
    [InlineData("Rofiq Yusnur")]
    [InlineData("A")]
    [InlineData("Éric")]
    public void DecodeName_Utf16AndUtf8(string name)
    {
        var u16 = new byte[128]; Encoding.Unicode.GetBytes(name).CopyTo(u16, 0);
        var u8 = new byte[128]; Encoding.UTF8.GetBytes(name).CopyTo(u8, 0);
        Assert.Equal(name, Features.DecodeName(u16));
        Assert.Equal(name, Features.DecodeName(u8));
    }

    [Fact]
    public void DecodeName_LatinAndEmpty()
    {
        var b = new byte[128]; b[0] = 0xC9; "ric"u8.CopyTo(b.AsSpan(1));
        Assert.Equal("Éric", Features.DecodeName(b));
        Assert.Equal("", Features.DecodeName(new byte[128]));
    }

    [Theory]
    [InlineData("C48")]
    [InlineData("C32")]
    [InlineData("B12")]
    public async Task PullAll_DetectsLayout(string layout)
    {
        using var sim = new Simulator { Layout = layout }.Start();
        var (c, info) = await Open(sim);
        using var _ = c;
        var r = await c.PullLogs(info, new PullOptions());
        Assert.Equal(layout, r.Layout);
        Assert.Equal(sim.Records, r.Punches.Count);
        Assert.Equal(sim.Times[0], r.Punches[0].At);
        Assert.Equal(sim.Times[^1], r.Punches[^1].At);
    }

    [Fact]
    public async Task Range_UsesMachineRange_WhenSupported()
    {
        using var sim = new Simulator().Start();
        var (c, info) = await Open(sim);
        using var _ = c;
        var day = new DateTime(2026, 9, 3);
        var r = await c.PullLogs(info, new PullOptions { From = day, To = day });
        Assert.NotEmpty(r.Punches);
        Assert.All(r.Punches, p => Assert.Equal(day, p.At.Date));
        Assert.Equal(sim.Times.Count(t => t.Date == day), r.Punches.Count);
    }

    [Fact]
    public async Task Range_FallsBackToFullPull_WhenMachineRejects()
    {
        using var sim = new Simulator { RejectRange = true }.Start();
        var (c, info) = await Open(sim);
        using var _ = c;
        var day = new DateTime(2026, 9, 3);
        var trace = new List<string>();
        c.Cfg.Trace = trace.Add;
        var r = await c.PullLogs(info, new PullOptions { From = day, To = day });
        Assert.Equal(sim.Times.Count(t => t.Date == day), r.Punches.Count);
        Assert.All(r.Punches, p => Assert.Equal(day, p.At.Date));
        Assert.Contains(trace, l => l.Contains("tarik semua log lalu saring"));
    }

    [Fact]
    public async Task Range_WithNoData_ReturnsEmpty()
    {
        using var sim = new Simulator().Start();
        var (c, info) = await Open(sim);
        using var _ = c;
        var r = await c.PullLogs(info, new PullOptions { From = new DateTime(2026, 1, 5), To = new DateTime(2026, 1, 5) });
        Assert.Empty(r.Punches);
    }

    [Fact]
    public async Task TinyResult_StillDetectsLayout()
    {
        using var sim = new Simulator { Records = 3 }.Start();
        var (c, info) = await Open(sim);
        using var _ = c;
        var r = await c.PullLogs(info, new PullOptions());
        Assert.Equal(3, r.Punches.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UserNames_FullNameNotFirstLetter(bool utf16)
    {
        using var sim = new Simulator { Utf16Names = utf16 }.Start();
        var (c, _) = await Open(sim);
        using var _ = c;
        var users = await c.ReadUsers();
        Assert.Equal(sim.Users, users.Count);
        var (name, _) = await c.UserInfo("7", DateTime.Now);
        Assert.Equal(Simulator.NameOf(7), name);
        Assert.Equal(2, users[0].Fingers);
    }

    [Fact]
    public async Task Clock_ReadAndSet()
    {
        using var sim = new Simulator().Start();
        var (c, _) = await Open(sim);
        using var _ = c;
        Assert.Equal(new DateTime(2026, 10, 31, 8, 30, 15), await c.DeviceTime());
        await c.SetDeviceTime(new DateTime(2026, 11, 1, 9, 0, 0));
        Assert.Equal(new DateTime(2026, 11, 1, 9, 0, 0), await c.DeviceTime());
        Assert.Equal(1, sim.SetClockCalls);
    }

    [Fact]
    public async Task Hub_PullUsersAndDedupe()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fingering-test-" + Guid.NewGuid().ToString("N"));
        using var sim = new Simulator { Records = 120 }.Start();
        using var store = new Store(Path.Combine(dir, "t.db"));
        using var dumps = new DumpStore(Path.Combine(dir, "dump.db"));
        var hub = new Hub(store, dumps);
        var id = store.SaveDevice(new Device { Name = "Sim", Host = "127.0.0.1", Port = sim.Port, MachineNo = sim.Machine });

        async Task<Job> Wait(Job j) { for (int i = 0; i < 200 && !j.Done; i++) await Task.Delay(50); Assert.True(j.Done); return j; }

        var j1 = await Wait(hub.Pull(id));
        Assert.Equal("", j1.Error);
        Assert.Equal(120, store.Stats().Punches);
        var j2 = await Wait(hub.Pull(id)); // tarik ulang tidak menggandakan
        Assert.Equal("", j2.Error);
        Assert.Equal(120, store.Stats().Punches);

        var ju = await Wait(hub.Users(id, "semua"));
        Assert.Equal("", ju.Error);
        var emp = store.Employees("").First(e => e.Pin == "5");
        Assert.Equal(Simulator.NameOf(5), emp.Name);

        var jl = await Wait(hub.Users(id, "lihat"));
        Assert.Contains("Pegawai Contoh 12", jl.Log);
        Assert.Equal(12, store.Employees("").Count); // "lihat" tidak menulis

        var jr = await Wait(hub.Pull(id, "pull", new DateTime(2026, 9, 2), new DateTime(2026, 9, 2)));
        Assert.Equal("", jr.Error);
        Assert.Equal(120, store.Stats().Punches);
        Assert.Equal(3, store.Runs(10).Count);
    }

    [Fact]
    public void Store_PasswordHashAndLogin()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fingering-test-" + Guid.NewGuid().ToString("N"));
        using var store = new Store(Path.Combine(dir, "t.db"));
        store.EnsureUser("illustricious", "snogard");
        Assert.True(store.CheckLogin("illustricious", "snogard"));
        Assert.False(store.CheckLogin("illustricious", "salah"));
        Assert.False(store.CheckLogin("orang", "snogard"));
        Assert.DoesNotContain("snogard", store.Setting("x") + Store.HashPassword("snogard"));
    }

    static async Task<Job> WaitJob(Job j) { for (int i = 0; i < 300 && !j.Done; i++) await Task.Delay(50); Assert.True(j.Done); return j; }

    [Fact]
    public async Task ClearLogs_RefusedUnlessAllowed_AndBacksUpFirst()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fingering-test-" + Guid.NewGuid().ToString("N"));
        using var sim = new Simulator { Records = 80 }.Start();
        using var store = new Store(Path.Combine(dir, "t.db"));
        using var dumps = new DumpStore(Path.Combine(dir, "dump.db"));
        var hub = new Hub(store, dumps);
        var id = store.SaveDevice(new Device { Name = "Sim", Host = "127.0.0.1", Port = sim.Port, MachineNo = sim.Machine });

        var refused = await WaitJob(hub.ClearLogs(id));   // default: tidak diizinkan
        Assert.NotEqual("", refused.Error);
        Assert.Equal(0, sim.ClearCalls);

        store.SetSetting("allow_clear", "1");
        var ok = await WaitJob(hub.ClearLogs(id));
        Assert.Equal("", ok.Error);
        Assert.Equal(1, sim.ClearCalls);
        Assert.Equal(0, sim.Records);
        Assert.Equal(80, store.Stats().Punches);          // cadangan di PC utuh
    }

    [Fact]
    public async Task PullNew_OnlyFetchesSinceLastStoredPunch()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fingering-test-" + Guid.NewGuid().ToString("N"));
        using var sim = new Simulator { Records = 200 }.Start();
        using var store = new Store(Path.Combine(dir, "t.db"));
        using var dumps = new DumpStore(Path.Combine(dir, "dump.db"));
        var hub = new Hub(store, dumps);
        var id = store.SaveDevice(new Device { Name = "Sim", Host = "127.0.0.1", Port = sim.Port, MachineNo = sim.Machine });

        var first = await WaitJob(hub.PullNew(id));        // belum ada data -> semua
        Assert.Equal("", first.Error);
        Assert.Equal(200, store.Stats().Punches);
        var again = await WaitJob(hub.PullNew(id));        // tidak ada yang baru
        Assert.Equal("", again.Error);
        Assert.Equal(200, store.Stats().Punches);
        Assert.Contains("0 baru", again.Summary);
        Assert.NotNull(store.LastPunchAt(id));
    }

    [Fact]
    public async Task Live_PullsOnlyWhenCountGrows()
    {
        Hub.LiveMinSeconds = 1;
        var dir = Path.Combine(Path.GetTempPath(), "fingering-test-" + Guid.NewGuid().ToString("N"));
        using var sim = new Simulator { Records = 50, CommunicationKey = 918273 }.Start();
        using var store = new Store(Path.Combine(dir, "t.db"));
        using var dumps = new DumpStore(Path.Combine(dir, "dump.db"));
        var hub = new Hub(store, dumps);
        var id = store.SaveDevice(new Device { Name = "Sim", Host = "127.0.0.1", Port = sim.Port, MachineNo = sim.Machine, CommKey = "918273" });
        await WaitJob(hub.PullNew(id));
        Assert.Equal(50, store.Stats().Punches);

        store.SetSetting("live_sec", "1");
        using var cts = new CancellationTokenSource();
        var live = hub.RunLive(cts.Token);
        await Task.Delay(2500);                            // baseline terbentuk, tidak ada tarikan
        Assert.Equal(50, store.Stats().Punches);
        Assert.Single(store.Runs(10));

        sim.AddRecord(3, new DateTime(2026, 10, 31, 8, 0, 0));
        for (int i = 0; i < 80 && store.Stats().Punches < 51; i++) await Task.Delay(100);
        Assert.Equal(51, store.Stats().Punches);
        Assert.Contains("log di mesin", hub.LiveState(id));
        cts.Cancel();
        try { await live; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task Users_AddRenameDelete_WithGuardsAndBackup()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fingering-test-" + Guid.NewGuid().ToString("N"));
        using var sim = new Simulator { Records = 20, Users = 5 }.Start();
        using var store = new Store(Path.Combine(dir, "t.db"));
        using var dumps = new DumpStore(Path.Combine(dir, "dump.db"));
        var hub = new Hub(store, dumps);
        var id = store.SaveDevice(new Device { Name = "Sim", Host = "127.0.0.1", Port = sim.Port, MachineNo = sim.Machine });

        Assert.NotEqual("", (await WaitJob(hub.AddUser(id, "99", "Budi Santoso", 0, "1234"))).Error); // belum diizinkan
        Assert.DoesNotContain(sim.Roster, r => r.Id == "99");

        store.SetSetting("allow_users", "1");
        var add = await WaitJob(hub.AddUser(id, "99", "Budi Santoso", 0, "1234"));
        Assert.Equal("", add.Error);
        Assert.DoesNotContain("1234", add.Summary);
        Assert.DoesNotContain("31 32 33 34", add.Log);
        Assert.Equal("Budi Santoso", sim.Roster.First(r => r.Id == "99").Name);
        Assert.NotEqual("", (await WaitJob(hub.AddUser(id, "99", "Lagi", 0, "1234"))).Error);        // PIN dobel ditolak
        Assert.NotEqual("", (await WaitJob(hub.AddUser(id, "98", "X", 0, "12ab"))).Error);            // sandi non-angka ditolak

        var ren = await WaitJob(hub.RenameUser(id, "99", "Budi S."));
        Assert.Equal("", ren.Error);
        Assert.Equal("Budi S.", sim.Roster.First(r => r.Id == "99").Name);

        var del = await WaitJob(hub.DeleteUser(id, "3"));
        Assert.Equal("", del.Error);
        Assert.DoesNotContain(sim.Roster, r => r.Id == "3");
        Assert.Equal(2, store.Templates("3").Count);          // 2 sidik jari dicadangkan sebelum dihapus
        Assert.NotEqual("", (await WaitJob(hub.DeleteUser(id, "3"))).Error); // sudah tidak ada
    }
}
