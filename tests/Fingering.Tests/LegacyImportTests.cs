using Fingering.Core;
using Xunit;

namespace Fingering.Tests;

public class LegacyImportTests
{
    const string Dump = @"DROP TABLE IF EXISTS att_log;
LOCK TABLES att_log WRITE;
INSERT INTO att_log VALUES
  ('SN1', '2024-05-27 09:42:10', '2', 1, 2, 0, 0, '0'),
  ('SN1', '2024-05-27 09:42:10', '2', 1, 2, 0, 0, '0'),
  ('SN1', '2026-10-20 07:00:00', '7', 1, 1, 0, 0, '0'),
  ('SN9', '2024-05-28 08:00:00', '2', 1, 1, 0, 0, '0'),
  ('SN1', '0000-00-00 00:00:00', '2', 1, 1, 0, 0, '0');
UNLOCK TABLES;
INSERT INTO device VALUES
  ('SN1', 'x', NULL, 'Mesin O''Neil', 1, 0, '192.168.1.253', 25, 3, '', '', '', '5005', 0, 10, 0, 0, '2026-11-03', '0', '0', '0', '', NULL, -1),
  ('SN9', 'y', NULL, 'Mesin Hilang', 3, 0, '10.9.9.9', 25, 3, '', '', '', '5005', 0, 10, 0, 0, '2026-11-03', '0', '0', '0', '', NULL, -1);
INSERT INTO pegawai VALUES
  (1, '2', NULL, 'budi', 'budi', '0', '', '0', NULL, 1, NULL, NULL, 0, 0, 0, '2026-01-01', NULL, 1, '2026-01-01', '', NULL, '', '', '', '000000', 0, NULL),
  (2, '7', NULL, 'sari (lama)', 'sari', '0', '', '0', NULL, 1, NULL, NULL, 0, 0, 0, '2026-01-01', NULL, 1, '2026-01-01', '', NULL, '', '', '', '000000', 0, NULL);
";

    static (Store s, string sql, string dir) Setup()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fing-leg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var s = new Store(Path.Combine(dir, "p.db"));
        s.SaveDevice(new Device { Name = "IGD", Host = "192.168.1.253" });
        s.UpsertEmployee("2", "", "Budi Baru", 1);   // sudah ada: namanya tidak boleh ditimpa
        var sql = Path.Combine(dir, "backup.sql");
        File.WriteAllText(sql, Dump);
        return (s, sql, dir);
    }

    [Fact]
    public void Tuples_HandlesQuotesAndNull()
    {
        var t = LegacyImport.Tuples("  ('a''b', NULL, 5, 'x\\'y'),").Single();
        Assert.Equal(["a'b", null, "5", "x'y"], t);
    }

    [Fact]
    public void DryRun_ChangesNothing_ThenApplyIsIdempotent()
    {
        var (s, sql, dir) = Setup();
        using (s)
        {
            var dry = LegacyImport.Run(s, sql, apply: false);
            Assert.Equal(0, s.Stats().Punches);
            Assert.Equal(1, s.Stats().Employees);
            Assert.Equal(2, dry.PunchAdded);       // 2 unik dari SN1 (duplikat dihitung sekali)
            Assert.Equal(1, dry.PunchBadRows);     // tanggal 0000
            Assert.Equal(1, dry.PunchUnmatchedRows);   // SN9 tidak ada di Fingering
            Assert.Equal(1, dry.EmployeesAdded);   // hanya PIN 7

            var run = LegacyImport.Run(s, sql, apply: true, backupDir: Path.Combine(dir, "bak"));
            Assert.True(File.Exists(run.BackupFile));
            Assert.Equal(2, s.Stats().Punches);
            Assert.Equal("Budi Baru", s.Employees("").First(e => e.Pin == "2").Name);
            Assert.Equal("sari (lama)", s.Employees("").First(e => e.Pin == "7").Name);

            var again = LegacyImport.Run(s, sql, apply: true);
            Assert.Equal(0, again.PunchAdded);
            Assert.Equal(0, again.EmployeesAdded);
            Assert.Equal(2, s.Stats().Punches);
        }
    }
}
