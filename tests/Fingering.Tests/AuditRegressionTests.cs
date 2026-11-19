using System.Buffers.Binary;
using Fingering.Core;
using Xunit;

namespace Fingering.Tests;

public class AuditRegressionTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "fing-audit-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(dir)) Directory.Delete(dir, true); }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void Vault_RejectsUnboundedKdfBeforeDeriving(int iterations)
    {
        Vault.Create(dir, "admin", "password-test");
        var bytes = File.ReadAllBytes(Vault.PathIn(dir));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(5), iterations);
        File.WriteAllBytes(Vault.PathIn(dir), bytes);
        Assert.Throws<InvalidDataException>(() => Vault.Unlock(dir, "admin", "password-test"));
    }

    [Fact]
    public void Vault_RejectsTruncatedAndTrailingData()
    {
        Vault.Create(dir, "admin", "password-test");
        var bytes = File.ReadAllBytes(Vault.PathIn(dir));
        File.WriteAllBytes(Vault.PathIn(dir), bytes[..^1]);
        Assert.Throws<InvalidDataException>(() => Vault.Unlock(dir, "admin", "password-test"));
        File.WriteAllBytes(Vault.PathIn(dir), [.. bytes, 0]);
        Assert.Throws<InvalidDataException>(() => Vault.Unlock(dir, "admin", "password-test"));
    }

    [Fact]
    public async Task LoginTrace_DoesNotExposeCommunicationKey_AndReadRejectsOversizedData()
    {
        const uint key = 0x12345678;
        using var sim = new Simulator { CommunicationKey = key }.Start();
        var trace = new List<string>();
        using var client = await FkClient.DialAsync(new FkConfig { Host = "127.0.0.1", Port = sim.Port, Key = key, Trace = trace.Add });
        await client.Login();
        Assert.DoesNotContain(trace, line => line.Contains("78 56 34 12"));
        await Assert.ThrowsAsync<FkException>(() => client.ReadData(Cmd.ReadAll, 0, int.MaxValue));
        await Assert.ThrowsAsync<FkException>(() => client.ReadData(Cmd.ReadAll, 0, -1));
    }
    [Fact]
    public void Templates_PreserveSamePinFromDifferentDevices_AndMigrateLegacySchema()
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "templates.db");
        long first;
        using (var store = new Store(path))
        {
            first = store.SaveDevice(new Device { Host = "127.0.0.1" });
            store.SaveTemplate("7", first, 0, [1, 2]);
        }
        using (var db = SecureDb.Open(path, (byte[]?)null))
        using (var command = db.CreateCommand())
        {
            command.CommandText = @"ALTER TABLE employee_template RENAME TO newer;
                CREATE TABLE employee_template (
                    id INTEGER PRIMARY KEY, pin TEXT NOT NULL, device_id INTEGER NOT NULL REFERENCES device(id),
                    backup_no INTEGER NOT NULL, data BLOB NOT NULL, fetched_at TEXT NOT NULL DEFAULT (datetime('now')),
                    UNIQUE (pin, backup_no));
                INSERT INTO employee_template SELECT * FROM newer;
                DROP TABLE newer;";
            command.ExecuteNonQuery();
        }
        using (var store = new Store(path))
        {
            var second = store.SaveDevice(new Device { Host = "127.0.0.2" });
            store.SaveTemplate("7", second, 0, [9, 8]);
            Assert.Equal(new byte[] { 1, 2 }, Assert.Single(store.Templates("7", first)).data);
            Assert.Equal(new byte[] { 9, 8 }, Assert.Single(store.Templates("7", second)).data);
            Assert.Equal(2, store.Templates("7").Count);
        }
        using var reopened = new Store(path);
        Assert.Equal(2, reopened.Templates("7").Count);
    }
}
