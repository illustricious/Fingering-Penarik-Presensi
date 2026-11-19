using Fingering.Core;
using Xunit;

namespace Fingering.Tests;

public class ProgressTests
{
    static async Task<Job> WaitJob(Job j) { for (int i = 0; i < 400 && !j.Done; i++) await Task.Delay(50); Assert.True(j.Done); return j; }

    [Fact]
    public async Task PullAll_ReportsMonotonicProgress_AndEndsAtOne()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fingering-test-" + Guid.NewGuid().ToString("N"));
        using var sim = new Simulator { Records = 900 }.Start();      // 900 x 48 byte > 16 potongan
        using var store = new Store(Path.Combine(dir, "t.db"));
        using var dumps = new DumpStore(Path.Combine(dir, "dump.db"));
        var hub = new Hub(store, dumps);
        var id = store.SaveDevice(new Device { Name = "Sim", Host = "127.0.0.1", Port = sim.Port, MachineNo = sim.Machine });

        var seen = new List<double>();
        hub.JobStarted += j => j.Changed += () => { if (j.Progress is { } p) lock (seen) seen.Add(p); };
        var job = await WaitJob(hub.Pull(id));
        Assert.Equal("", job.Error);
        Assert.Equal(1.0, job.Progress);
        lock (seen)
        {
            Assert.Contains(seen, p => p > 0 && p < 1);                              // ada kemajuan antara 0 dan 1
            for (int i = 1; i < seen.Count; i++) Assert.True(seen[i] >= seen[i - 1]); // tidak pernah mundur
        }
    }

    [Fact]
    public async Task EmptyEnroll_ReportsStepProgress()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fingering-test-" + Guid.NewGuid().ToString("N"));
        using var sim = new Simulator { Records = 10, Users = 8 }.Start();
        using var store = new Store(Path.Combine(dir, "t.db"));
        using var dumps = new DumpStore(Path.Combine(dir, "dump.db"));
        var hub = new Hub(store, dumps);
        var id = store.SaveDevice(new Device { Name = "Sim", Host = "127.0.0.1", Port = sim.Port, MachineNo = sim.Machine });
        store.SetSetting("allow_empty_enroll", "1");
        var seen = new List<double>();
        hub.JobStarted += j => j.Changed += () => { if (j.Progress is { } p) lock (seen) seen.Add(p); };
        var job = await WaitJob(hub.EmptyEnroll(id));
        Assert.Equal("", job.Error);
        Assert.Equal(1.0, job.Progress);
        lock (seen) Assert.True(seen.Count(p => p > 0 && p < 1) >= 3);
    }

    [Fact]
    public async Task FailedJob_DoesNotJumpToFull()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fingering-test-" + Guid.NewGuid().ToString("N"));
        using var sim = new Simulator { Records = 10, Users = 3 }.Start();
        using var store = new Store(Path.Combine(dir, "t.db"));
        using var dumps = new DumpStore(Path.Combine(dir, "dump.db"));
        var hub = new Hub(store, dumps);
        var id = store.SaveDevice(new Device { Name = "Sim", Host = "127.0.0.1", Port = sim.Port, MachineNo = sim.Machine });
        var job = await WaitJob(hub.EmptyEnroll(id));            // belum diizinkan -> gagal
        Assert.NotEqual("", job.Error);
        Assert.NotEqual(1.0, job.Progress);
    }
}
