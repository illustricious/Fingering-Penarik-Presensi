using Fingering.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fingering.Tests;

public class VaultTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "fing-vault-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(dir, true); } catch (IOException) { } }

    [Fact]
    public void Create_Unlock_WrongPasswordOrUser_Fails()
    {
        var (key, code) = Vault.Create(dir, "illustricious", "sandi-rahasia");
        Assert.Equal(32, key.Length);
        Assert.Matches("^([A-Z2-7]{4}-){7}[A-Z2-7]{4}$", code);
        Assert.Equal(key, Vault.Unlock(dir, "illustricious", "sandi-rahasia"));
        Assert.Equal(key, Vault.Unlock(dir, "  illustricious ", "sandi-rahasia"));   // spasi di ID diabaikan
        Assert.Null(Vault.Unlock(dir, "illustricious", "sandi-salah"));
        Assert.Null(Vault.Unlock(dir, "orang-lain", "sandi-rahasia"));
    }

    [Fact]
    public void VaultFile_DoesNotContainKeyOrUserInPlain()
    {
        var (key, _) = Vault.Create(dir, "illustricious", "sandi-rahasia");
        var bytes = File.ReadAllBytes(Vault.PathIn(dir));
        Assert.Equal("FKV1", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.True(bytes.AsSpan().IndexOf(key) < 0);
        Assert.True(bytes.AsSpan().IndexOf("illustricious"u8) < 0);
    }

    [Fact]
    public void Recovery_Works_ChangeLoginKeepsKey_NewRecoveryInvalidatesOld()
    {
        var (key, code) = Vault.Create(dir, "admin", "sandi-pertama");
        Assert.Equal(key, Vault.UnlockRecovery(dir, code.ToLowerInvariant().Replace("-", " ")));   // huruf kecil/spasi ditoleransi
        Assert.Null(Vault.UnlockRecovery(dir, "AAAA-AAAA-AAAA-AAAA-AAAA-AAAA-AAAA-AAAA"));

        Vault.ChangeLogin(dir, key, "admin2", "sandi-kedua-baru");
        Assert.Null(Vault.Unlock(dir, "admin", "sandi-pertama"));
        Assert.Equal(key, Vault.Unlock(dir, "admin2", "sandi-kedua-baru"));
        Assert.Equal(key, Vault.UnlockRecovery(dir, code));   // kode pemulihan tetap berlaku

        var code2 = Vault.NewRecoveryCode(dir, key);
        Assert.NotEqual(code, code2);
        Assert.Null(Vault.UnlockRecovery(dir, code));
        Assert.Equal(key, Vault.UnlockRecovery(dir, code2));
        Assert.Equal(key, Vault.Unlock(dir, "admin2", "sandi-kedua-baru"));
    }

    [Fact]
    public void WeakCredentials_AreRejected()
    {
        Assert.Throws<InvalidOperationException>(() => Vault.Create(dir, "", "sandi-rahasia"));
        Assert.Throws<InvalidOperationException>(() => Vault.Create(dir, "a", "pendek"));
    }

    [Fact]
    public void TamperedVault_IsRejected()
    {
        Vault.Create(dir, "a", "sandi-rahasia");
        var b = File.ReadAllBytes(Vault.PathIn(dir)); b[^3] ^= 0xFF;
        File.WriteAllBytes(Vault.PathIn(dir), b);
        Assert.Null(Vault.UnlockRecovery(dir, "AAAA-AAAA-AAAA-AAAA-AAAA-AAAA-AAAA-AAAA"));
        File.WriteAllBytes(Vault.PathIn(dir), [1, 2, 3]);
        Assert.Throws<InvalidDataException>(() => Vault.Unlock(dir, "a", "sandi-rahasia"));
    }
}

public class EncryptedStoreTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "fing-enc-" + Guid.NewGuid().ToString("N"));
    public EncryptedStoreTests() => Directory.CreateDirectory(dir);
    public void Dispose() { SqliteConnection.ClearAllPools(); try { Directory.Delete(dir, true); } catch (IOException) { } }

    Store NewStore(string name, byte[]? key)
    {
        var s = new Store(Path.Combine(dir, name), key);
        s.SaveDevice(new Device { Name = "IGD", Host = "192.168.1.253", CommKey = "rahasia-comm-key" });
        s.UpsertEmployee("7", "", "Nama Rahasia Pegawai", 1);
        s.SaveTemplate("7", s.Devices()[0].Id, 0, [1, 2, 3, 4]);
        return s;
    }

    [Fact]
    public void EncryptedFile_HasNoSqliteHeaderAndNoPlainText_AndWrongKeyFails()
    {
        var key = Vault.NewKey();
        var path = Path.Combine(dir, "e.fdb");
        using (var s = NewStore("e.fdb", key)) Assert.Equal(1, s.Stats().Employees);
        Assert.False(SecureDb.LooksPlaintext(path));
        var bytes = File.ReadAllBytes(path);
        Assert.True(bytes.AsSpan().IndexOf("Nama Rahasia Pegawai"u8) < 0);
        Assert.True(bytes.AsSpan().IndexOf("rahasia-comm-key"u8) < 0);
        Assert.True(bytes.AsSpan().IndexOf("SQLite format"u8) < 0);

        using (var again = new Store(path, key)) Assert.Equal("Nama Rahasia Pegawai", again.Employees("").Single().Name);
        Assert.Throws<WrongKeyException>(() => new Store(path, Vault.NewKey()));
        Assert.Throws<WrongKeyException>(() => new Store(path, null));
    }

    [Fact]
    public void PlainStore_IsReadableWithoutKey_ControlCase()
    {
        var path = Path.Combine(dir, "p.db");
        using (NewStore("p.db", null)) { }
        Assert.True(SecureDb.LooksPlaintext(path));
        Assert.True(File.ReadAllBytes(path).AsSpan().IndexOf("Nama Rahasia Pegawai"u8) >= 0);
    }

    [Fact]
    public void Migration_CopiesEverythingEncryptedAndVerifies()
    {
        var plain = Path.Combine(dir, "presensi.db");
        using (var s = NewStore("presensi.db", null))
            s.InsertPunches(s.Devices()[0].Id, s.StartRun(s.Devices()[0].Id), "pull", Enumerable.Range(0, 500).Select(i => new Punch("7", new DateTime(2026, 10, 1).AddMinutes(i * 7), 1, 0, 0)));
        using (var d = new DumpStore(Path.Combine(dir, "dump.db"))) d.Add("IGD", "info", [9, 9, 9]);

        var key = Vault.NewKey();
        var rep = Migrator.Migrate(plain, Path.Combine(dir, "dump.db"), Path.Combine(dir, "fingering.fdb"), Path.Combine(dir, "dump.fdb"), key);
        Assert.Equal((500, 1, 1), (rep.Punches, rep.Employees, rep.Devices));
        Assert.False(SecureDb.LooksPlaintext(Path.Combine(dir, "fingering.fdb")));
        Assert.False(SecureDb.LooksPlaintext(Path.Combine(dir, "dump.fdb")));
        using var enc = new Store(Path.Combine(dir, "fingering.fdb"), key);
        Assert.Equal(500, enc.Stats().Punches);
        Assert.Equal([1, 2, 3, 4], enc.Templates("7").Single().data);
        using var dump = new DumpStore(Path.Combine(dir, "dump.fdb"), key);
        Assert.Single(dump.List());
        Assert.True(File.Exists(plain));   // DB lama belum dihapus oleh Migrate
    }

    [Fact]
    public void WipeDelete_RemovesFiles()
    {
        var f = Path.Combine(dir, "x.db"); File.WriteAllBytes(f, new byte[5000]);
        Migrator.WipeDelete(f);
        Assert.False(File.Exists(f));
    }
}

public class BackupRestoreTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "fing-bak-" + Guid.NewGuid().ToString("N"));
    public BackupRestoreTests() => Directory.CreateDirectory(dir);
    public void Dispose() { SqliteConnection.ClearAllPools(); try { Directory.Delete(dir, true); } catch (IOException) { } }

    [Fact]
    public void Backup_IsOpaque_NeedsPassphrase_AndRestoresRekeyedForAnotherMachine()
    {
        var keyA = Vault.NewKey();
        var file = Path.Combine(dir, "b" + BackupService.Extension);
        using (var s = new Store(Path.Combine(dir, "a.fdb"), keyA))
        using (var d = new DumpStore(Path.Combine(dir, "ad.fdb"), keyA))
        {
            var dev = s.SaveDevice(new Device { Name = "POLI", Host = "192.168.100.12" });
            s.UpsertEmployee("1", "", "Rahasia Banget", 1);
            s.InsertPunches(dev, s.StartRun(dev), "pull", Enumerable.Range(0, 300).Select(i => new Punch("1", new DateTime(2026, 10, 1).AddMinutes(i * 9), 1, 0, 0)));
            d.Add("POLI", "info", [1, 2]);
            var info = BackupService.Create(s, d, file, "frasa-backup-1");
            Assert.Equal(300, info.Punches);
        }
        var bytes = File.ReadAllBytes(file);
        Assert.Equal("FINGBAK1", System.Text.Encoding.ASCII.GetString(bytes, 0, 8));
        Assert.True(bytes.AsSpan().IndexOf("Rahasia Banget"u8) < 0);
        Assert.True(bytes.AsSpan().IndexOf("SQLite format"u8) < 0);

        Assert.Throws<WrongKeyException>(() => BackupService.Inspect(file, "frasa-salah-banget"));
        var seen = BackupService.Inspect(file, "frasa-backup-1");
        Assert.Equal((300, 1, 1), (seen.Punches, seen.Employees, seen.Devices));

        // PC lain: kunci data berbeda -> hasil restore dienkripsi ulang dengan kunci PC itu
        var keyB = Vault.NewKey();
        var np = Path.Combine(dir, "new.fdb"); var nd = Path.Combine(dir, "newd.fdb");
        BackupService.Stage(file, "frasa-backup-1", keyB, np, nd);
        using (var s2 = new Store(np, keyB)) Assert.Equal("Rahasia Banget", s2.Employees("").Single().Name);
        Assert.Throws<WrongKeyException>(() => new Store(np, keyA));
        using (var d2 = new DumpStore(nd, keyB)) Assert.Single(d2.List());
    }

    [Fact]
    public void Stage_WithWrongPassphrase_LeavesNothingBehind()
    {
        var key = Vault.NewKey();
        var file = Path.Combine(dir, "c" + BackupService.Extension);
        using (var s = new Store(Path.Combine(dir, "a.fdb"), key))
        using (var d = new DumpStore(Path.Combine(dir, "ad.fdb"), key)) BackupService.Create(s, d, file, "frasa-backup-1");
        var np = Path.Combine(dir, "n.fdb"); var nd = Path.Combine(dir, "nd.fdb");
        Assert.Throws<WrongKeyException>(() => BackupService.Stage(file, "salah-salah-salah", key, np, nd));
        Assert.False(File.Exists(np)); Assert.False(File.Exists(nd));
        Assert.Throws<InvalidOperationException>(() => BackupService.CheckPassphrase("pendek"));
        File.WriteAllBytes(Path.Combine(dir, "bukan.fingbak"), [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13]);
        Assert.Throws<InvalidDataException>(() => BackupService.Inspect(Path.Combine(dir, "bukan.fingbak"), "apa-saja-boleh"));
    }
}
