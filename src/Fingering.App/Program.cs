using Avalonia;

namespace Fingering;

static class Program
{
    /// <summary>Folder data terenkripsi: %APPDATA%\illustricious\Fingering (fingering.fdb, dump.fdb, vault.fkv, cadangan\). Bisa diganti dengan: Fingering.exe --data "D:\folder".</summary>
    public static string DataDir { get; private set; } = "";
    public static string DbPath => Path.Combine(DataDir, "fingering.fdb");
    public static string DumpPath => Path.Combine(DataDir, "dump.fdb");

    /// <summary>Folder tempat DB lama (SQLite polos, presensi.db) mungkin berada: folder data sendiri, Dokumen\Fingering, dan %APPDATA%\Fingering.</summary>
    public static IEnumerable<string> LegacyDirs =>
    [
        DataDir,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Fingering"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Fingering"),
    ];

    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Contains("--version"))
        {
            Console.WriteLine(typeof(Program).Assembly.GetName().Version?.ToString(3));
            return;
        }
        var i = Array.IndexOf(args, "--data");
        DataDir = i >= 0 && i + 1 < args.Length
            ? Path.GetFullPath(args[i + 1])
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "illustricious", "Fingering");
        var ex = Array.IndexOf(args, "--export");
        if (ex >= 0 && ex + 3 < args.Length) { Environment.ExitCode = HeadlessExport(args[ex + 1], args[ex + 2], args[ex + 3], args); return; }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    static string? Arg(string[] args, string name) { var k = Array.IndexOf(args, name); return k >= 0 && k + 1 < args.Length ? args[k + 1] : null; }

    /// <summary>Fingering.exe --user ID --password SANDI --export 2026-10-16 2026-10-25 out.xlsx [--no-denda] : ekspor rekap tanpa membuka jendela (perlu login karena DB terenkripsi).</summary>
    static int HeadlessExport(string fromText, string toText, string outPath, string[] args)
    {
        try
        {
            var from = DateTime.ParseExact(fromText, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            var to = DateTime.ParseExact(toText, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            byte[]? key = null;
            if (Fingering.Core.Vault.Exists(DataDir))
            {
                key = Fingering.Core.Vault.Unlock(DataDir, Arg(args, "--user") ?? "", Arg(args, "--password") ?? "")
                    ?? throw new InvalidOperationException("login salah (pakai --user ID --password SANDI)");
            }
            using var store = new Fingering.Core.Store(key != null ? DbPath : Path.Combine(DataDir, "presensi.db"), key);
            var rules = Fingering.Core.DendaRules.Load(store);
            var emps = store.Employees("").ToDictionary(e => e.Pin, e => new Fingering.Core.EmpInfo(e.Name, e.Nip, e.Shifts, e.Group));
            var data = Fingering.Core.Rekap.Build(store.PunchTimes(from.AddDays(-1), to.AddDays(2).AddSeconds(-1)), p => emps.GetValueOrDefault(p), rules, from, to, store.LastPunchDate());
            Fingering.Core.XlsxExport.Write(outPath, from, to, data, rules, store.Setting("xlsx_password", "fingering"), includeDenda: !args.Contains("--no-denda"));
            Console.WriteLine($"{data.Count} pegawai -> {outPath}");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e.Message); return 1; }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
