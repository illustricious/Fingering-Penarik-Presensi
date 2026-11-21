using System.Text;
using Fingering.Core;
using Xunit;

namespace Fingering.Tests;

public class PasswordTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "fing-password-" + Guid.NewGuid().ToString("N"));
    public PasswordTests() => Directory.CreateDirectory(dir);
    public void Dispose() => Directory.Delete(dir, true);
    static byte[] Record(string pin, byte[] bytes)
    {
        var raw = new byte[84]; Encoding.ASCII.GetBytes(pin).CopyTo(raw, 0);
        raw[32] = 10; bytes.CopyTo(raw, 52); return raw;
    }
    static async Task<Job> Wait(Job job)
    {
        for (int i = 0; i < 400 && !job.Done; i++) await Task.Delay(50);
        Assert.True(job.Done); return job;
    }
    static void AssertReadOnly(Simulator sim)
    {
        foreach (var command in new[] { Cmd.ClearLogs, Cmd.DeleteEnroll, Cmd.EmptyEnroll, Cmd.PutEnroll, Cmd.SetUserInfo, Cmd.SetPrivilege, Cmd.TimeSet, Cmd.Mark })
            Assert.False(sim.CommandCounts.ContainsKey(command), $"Mutation command {command:X2} was sent");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("001234")]
    [InlineData("12345678")]
    public void NumericAscii_IsOnlyACandidate_LeadingZerosPreserved(string text)
    {
        var parsed = MachinePassword.Analyze("7", Record("7", Encoding.ASCII.GetBytes(text)));
        Assert.Equal(PasswordDataFormat.NumericTextCandidate, parsed.Format);
        Assert.Equal(text, parsed.Candidate);
    }

    [Fact]
    public void UnknownBinaryMalformedHeadersAndTrailingData_AreNeverGuessed()
    {
        var binary = Record("7", [0xD2, 0x04]);
        Assert.Null(MachinePassword.Analyze("7", binary).Candidate);
        var ascii = Record("7", "001234"u8.ToArray());
        Assert.Equal(PasswordDataFormat.Unknown, MachinePassword.Analyze("8", ascii).Format);
        Assert.Equal(PasswordDataFormat.Unknown, MachinePassword.Analyze("7", ascii[..^1]).Format);
        ascii[32] = 11;
        Assert.Null(MachinePassword.Analyze("7", ascii).Candidate);
        ascii[32] = 10; ascii[70] = 1;
        Assert.Null(MachinePassword.Analyze("7", ascii).Candidate);
        Assert.Null(MachinePassword.Analyze("7", Record("7", "123456789"u8.ToArray())).Candidate);
        Assert.Equal(PasswordDataFormat.Empty, MachinePassword.Analyze("7", Record("7", [])).Format);
    }

    [Fact]
    public async Task Pull_PersistsMixedResults_UsesCommunicationKey_DoesNotWriteOrLogSecrets()
    {
        using var sim = new Simulator { Users = 4, Records = 10, CommunicationKey = 314159 }.Start();
        sim.Roster[0].Mask = 1; sim.Roster[0].PasswordBytes = "001234"u8.ToArray();
        sim.Roster[1].Mask = 1; sim.Roster[1].PasswordBytes = [0xD2, 0x04];
        sim.Roster[2].Mask = 1; sim.Roster[2].RefusePassword = true;
        using var store = new Store(Path.Combine(dir, "main.fdb"), Vault.NewKey());
        using var dump = new DumpStore(Path.Combine(dir, "dump.fdb"), Vault.NewKey());
        var id = store.SaveDevice(new Device { Host = "127.0.0.1", Port = sim.Port, CommKey = "314159", Name = "Sim" });
        var job = await Wait(new Hub(store, dump).Passwords(id));
        Assert.Equal("", job.Error);
        var (rows, total) = store.DevicePasswords(id);
        Assert.Equal(4, total);
        Assert.Equal("001234", rows[0].Analysis.Candidate);
        Assert.Contains("belum diverifikasi", rows[0].Status);
        Assert.Equal(Simulator.NameOf(1), rows[0].Name);
        Assert.Equal(PasswordDataFormat.Unknown, rows[1].Analysis.Format);
        Assert.NotEmpty(rows[2].Error); Assert.Null(rows[2].Data);
        Assert.False(rows[3].HasPassword); Assert.Null(rows[3].Data);
        Assert.Equal(3, sim.PasswordReadCalls);
        Assert.DoesNotContain("001234", job.Log + job.Summary);
        Assert.DoesNotContain("30 30 31 32 33 34", job.Log);
        Assert.DoesNotContain(Convert.ToHexString(rows[0].Data!), job.Log);
        AssertReadOnly(sim);
    }

    [Fact]
    public async Task RepeatedPull_FailureMarksOldData_RemovedPasswordAndRemovedUsersAreReflected()
    {
        using var sim = new Simulator { Users = 1, Records = 0 }.Start();
        var user = sim.Roster[0]; user.Mask = 1; user.PasswordBytes = "556677"u8.ToArray();
        using var store = new Store(Path.Combine(dir, "main.db"));
        using var dump = new DumpStore(Path.Combine(dir, "dump.db"));
        var id = store.SaveDevice(new Device { Host = "127.0.0.1", Port = sim.Port });
        var hub = new Hub(store, dump);
        Assert.Equal("", (await Wait(hub.Passwords(id))).Error);
        var first = Assert.Single(store.DevicePasswords(id).rows);
        user.RefusePassword = true;
        Assert.Equal("", (await Wait(hub.Passwords(id))).Error);
        var stale = Assert.Single(store.DevicePasswords(id).rows);
        Assert.Equal(first.Data, stale.Data); Assert.Equal(first.FetchedAt, stale.FetchedAt);
        Assert.Contains("data lama", stale.Status); Assert.NotEmpty(stale.Error);
        user.Mask = 0;
        await Wait(hub.Passwords(id));
        var absent = Assert.Single(store.DevicePasswords(id).rows);
        Assert.False(absent.HasPassword); Assert.Null(absent.Data); Assert.Empty(absent.Error);
        sim.Roster.Clear();
        await Wait(hub.Passwords(id));
        Assert.Empty(store.DevicePasswords(id).rows);
        AssertReadOnly(sim);
    }

    [Fact]
    public async Task UnsupportedDialect_PreservesPreviousSnapshot_WithoutReadingEnroll()
    {
        using var sim = new Simulator { Layout = "B12", Users = 1, Records = 0 }.Start();
        using var store = new Store(Path.Combine(dir, "main.db"));
        using var dump = new DumpStore(Path.Combine(dir, "dump.db"));
        var id = store.SaveDevice(new Device { Host = "127.0.0.1", Port = sim.Port });
        store.SavePasswordSnapshot(id, [new("1", "Existing", 0, true, Record("1", "1234"u8.ToArray()))]);
        Assert.NotEmpty((await Wait(new Hub(store, dump).Passwords(id))).Error);
        Assert.Equal("1234", Assert.Single(store.DevicePasswords(id).rows).Analysis.Candidate);
        Assert.Equal(0, sim.PasswordReadCalls); AssertReadOnly(sim);
    }

    [Fact]
    public void Snapshots_AreIsolatedByDevice_ValidateBeforeReplacing_AndPaginate()
    {
        using var store = new Store(Path.Combine(dir, "main.db"));
        var a = store.SaveDevice(new Device { Host = "127.0.0.1" });
        var b = store.SaveDevice(new Device { Host = "127.0.0.2" });
        store.SavePasswordSnapshot(a, [new("1", "Alpha", 0, true, Record("1", "1234"u8.ToArray())), new("2", "Beta", 0, false, null)]);
        store.SavePasswordSnapshot(b, [new("1", "Other", 0, true, Record("1", "5678"u8.ToArray()))]);
        Assert.Equal("1234", store.DevicePasswords(a).rows[0].Analysis.Candidate);
        Assert.Equal("5678", Assert.Single(store.DevicePasswords(b).rows).Analysis.Candidate);
        Assert.Equal(2, store.DevicePasswords(a, limit: 1).total);
        Assert.Equal("2", Assert.Single(store.DevicePasswords(a, limit: 1, offset: 1).rows).Pin);
        Assert.Equal("Alpha", Assert.Single(store.DevicePasswords(a, "Alpha").rows).Name);
        Assert.Empty(store.DevicePasswords(a, "' OR 1=1 --").rows);
        Assert.Throws<InvalidDataException>(() => store.SavePasswordSnapshot(a, [new("1", "X", 0, true, [1])]));
        Assert.Throws<InvalidDataException>(() => store.SavePasswordSnapshot(a, [new("1", "X", 0, false, null), new("1", "Y", 0, false, null)]));
        Assert.Equal(2, store.DevicePasswords(a).total);
    }

    [Fact]
    public void ExistingDatabase_AddsPasswordTableWithoutLosingEmployeeData()
    {
        var path = Path.Combine(dir, "legacy.db");
        long id;
        using (var store = new Store(path))
        {
            id = store.SaveDevice(new Device { Host = "127.0.0.1" });
            store.UpsertEmployee("1", "", "Existing Employee", 1);
        }
        using (var db = SecureDb.Open(path, (byte[]?)null))
        using (var command = db.CreateCommand())
        {
            command.CommandText = "DROP TABLE device_password"; command.ExecuteNonQuery();
        }
        using var upgraded = new Store(path);
        Assert.Equal("Existing Employee", Assert.Single(upgraded.Employees("")).Name);
        upgraded.SavePasswordSnapshot(id, [new("1", "Existing Employee", 0, true, Record("1", "1234"u8.ToArray()))]);
        Assert.Equal("1234", Assert.Single(upgraded.DevicePasswords(id).rows).Analysis.Candidate);
    }

    [Fact]
    public void PasswordData_IsEncrypted_AndBackupRestorePreservesIt()
    {
        var key = Vault.NewKey(); var file = Path.Combine(dir, "password.fingbak");
        var dbPath = Path.Combine(dir, "main.fdb");
        long device;
        using (var store = new Store(dbPath, key))
        using (var dump = new DumpStore(Path.Combine(dir, "dump.fdb"), key))
        {
            device = store.SaveDevice(new Device { Host = "127.0.0.1" });
            store.SavePasswordSnapshot(device, [new("1", "Password Test", 0, true, Record("1", "001234"u8.ToArray()))]);
            BackupService.Create(store, dump, file, "backup-password-test");
        }
        Assert.False(SecureDb.LooksPlaintext(dbPath));
        Assert.True(File.ReadAllBytes(dbPath).AsSpan().IndexOf("001234"u8) < 0);
        Assert.True(File.ReadAllBytes(file).AsSpan().IndexOf("001234"u8) < 0);
        var newKey = Vault.NewKey(); var restored = Path.Combine(dir, "restored.fdb");
        BackupService.Stage(file, "backup-password-test", newKey, restored, Path.Combine(dir, "restored-dump.fdb"));
        using var result = new Store(restored, newKey);
        Assert.Equal("001234", Assert.Single(result.DevicePasswords(device).rows).Analysis.Candidate);
    }
}
