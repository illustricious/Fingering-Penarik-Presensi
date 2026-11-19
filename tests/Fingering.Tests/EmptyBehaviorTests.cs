using Fingering.Core;
using Xunit;

namespace Fingering.Tests;

// Perilaku "kosongkan" pada mesin tiruan (SIMULATOR/TAFSIRAN; bukan bukti perilaku mesin asli).
public class EmptyBehaviorTests
{
    static async Task<Job> WaitJob(Job j) { for (int i = 0; i < 400 && !j.Done; i++) await Task.Delay(50); Assert.True(j.Done); return j; }

    static (Store store, DumpStore dumps, Hub hub, long id, string dir) Setup(Simulator sim)
    {
        var dir = Path.Combine(Path.GetTempPath(), "fingering-test-" + Guid.NewGuid().ToString("N"));
        var store = new Store(Path.Combine(dir, "t.db"));
        var dumps = new DumpStore(Path.Combine(dir, "dump.db"));
        var hub = new Hub(store, dumps);
        var id = store.SaveDevice(new Device { Name = "Sim", Host = "127.0.0.1", Port = sim.Port, MachineNo = sim.Machine });
        return (store, dumps, hub, id, dir);
    }

    [Fact]
    public async Task ClearLogs_KeepsUsers_AndUsersCanStillPunch()
    {
        using var sim = new Simulator { Records = 30, Users = 5 }.Start();
        var (store, dumps, hub, id, _) = Setup(sim);
        using var _s = store; using var _d = dumps;
        store.SetSetting("allow_clear", "1");
        Assert.Equal("", (await WaitJob(hub.ClearLogs(id))).Error);
        Assert.Equal(0, sim.Records);          // log kosong
        Assert.Equal(5, sim.Roster.Count);     // user + sidik jari tetap
        Assert.True(sim.TryPunch(3, new DateTime(2026, 11, 1, 7, 0, 0)));   // masih bisa absen
        Assert.Equal(1, sim.Records);
    }

    [Fact]
    public async Task EmptyEnroll_RefusedByDefault_NothingIsSent()
    {
        using var sim = new Simulator { Records = 10, Users = 4 }.Start();
        var (store, dumps, hub, id, _) = Setup(sim);
        using var _s = store; using var _d = dumps;
        Assert.NotEqual("", (await WaitJob(hub.EmptyEnroll(id))).Error);
        Assert.Equal(0, sim.EmptyCalls);
        Assert.Equal(4, sim.Roster.Count);
    }

    [Fact]
    public async Task EmptyEnroll_BacksUpEverythingFirst_ThenEmptiesOnlyUsers_LogsStay_PunchRejected()
    {
        using var sim = new Simulator { Records = 25, Users = 6 }.Start();
        var (store, dumps, hub, id, _) = Setup(sim);
        using var _s = store; using var _d = dumps;
        store.SetSetting("allow_empty_enroll", "1");
        var job = await WaitJob(hub.EmptyEnroll(id));
        Assert.Equal("", job.Error);
        Assert.Equal(1, sim.EmptyCalls);
        Assert.Empty(sim.Roster);                                   // user dan sidik jari hilang dari mesin
        Assert.Equal(25, sim.Records);                              // log presensi TIDAK ikut terhapus
        for (int pin = 1; pin <= 6; pin++) Assert.Equal(2, store.Templates(pin.ToString()).Count);   // 2 jari per user tercadangkan di PC
        Assert.Equal(6, store.Employees("").Count);                 // nama tetap di database PC
        Assert.False(sim.TryPunch(2, new DateTime(2026, 11, 1, 7, 0, 0)));  // tidak terdaftar -> tidak ada log
        Assert.Equal(25, sim.Records);
    }

    [Fact]
    public async Task EmptyEnroll_BackupFailure_DeletesNothing()
    {
        using var sim = new Simulator { Records = 10, Users = 3, RefuseEnrollRead = true }.Start();
        var (store, dumps, hub, id, _) = Setup(sim);
        using var _s = store; using var _d = dumps;
        store.SetSetting("allow_empty_enroll", "1");
        Assert.NotEqual("", (await WaitJob(hub.EmptyEnroll(id))).Error);
        Assert.Equal(0, sim.EmptyCalls);                            // perintah kosongkan tidak pernah dikirim
        Assert.Equal(3, sim.Roster.Count);
    }

    [Fact]
    public async Task EmptyEnroll_OnEmptyMachine_IsHarmless()
    {
        using var sim = new Simulator { Records = 5, Users = 1 }.Start();
        var (store, dumps, hub, id, _) = Setup(sim);
        using var _s = store; using var _d = dumps;
        store.SetSetting("allow_empty_enroll", "1");
        Assert.Equal("", (await WaitJob(hub.EmptyEnroll(id))).Error);
        Assert.Empty(sim.Roster);
        var again = await WaitJob(hub.EmptyEnroll(id));              // mesin sudah kosong
        Assert.Equal("", again.Error);
        Assert.Contains("tidak ada user", again.Summary);
        Assert.Equal(1, sim.EmptyCalls);                            // tidak mengirim perintah lagi
    }
}
