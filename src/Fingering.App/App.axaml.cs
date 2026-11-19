using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Fingering.Core;
using Fingering.Ui;

namespace Fingering;

public partial class App : Application
{
    public static Hub Hub { get; private set; } = null!;
    public static Store Store => Hub.Store;
    /// <summary>Kunci data (hanya di memori; di disk hanya ada dalam vault.fkv yang dibungkus sandi). null = belum login.</summary>
    public static byte[]? Key { get; private set; }

    static readonly CancellationTokenSource Cts = new();

    /// <summary>Buka database terenkripsi dengan kunci dari vault lalu jalankan penjadwal. Aman dipanggil berulang (mis. login ulang setelah keluar).</summary>
    public static void Open(byte[] key, string user, string password)
    {
        if (Hub != null) return;
        var store = new Store(Program.DbPath, key);
        var dump = new DumpStore(Program.DumpPath, key);
        // tabel app_user (dipakai konfirmasi sandi untuk aksi berbahaya) disamakan dengan login di vault
        store.EnsureUser(user, password);
        if (!store.CheckLogin(user, password)) store.ChangeLogin(user, password);
        Hub = new Hub(store, dump);
        Key = key;
        _ = Hub.RunScheduler(Cts.Token).ContinueWith(_ => { }, TaskScheduler.Default);
        _ = Hub.RunLive(Cts.Token).ContinueWith(_ => { }, TaskScheduler.Default);
    }

    /// <summary>Restore yang disiapkan sesi sebelumnya (berkas *.restore): pasang sebelum DB dibuka. DB lama dipindah ke cadangan\sebelum-restore-*.</summary>
    static void ApplyPendingRestore()
    {
        var rp = Program.DbPath + ".restore"; var rd = Program.DumpPath + ".restore";
        if (!File.Exists(rp) || !File.Exists(rd) || !Vault.Exists(Program.DataDir)) return;
        var bak = Path.Combine(Program.DataDir, "cadangan", "sebelum-restore-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(bak);
        foreach (var f in new[] { Program.DbPath, Program.DumpPath })
            foreach (var s in new[] { "", "-wal", "-shm" })
                if (File.Exists(f + s)) File.Move(f + s, Path.Combine(bak, Path.GetFileName(f) + s));
        File.Move(rp, Program.DbPath);
        File.Move(rd, Program.DumpPath);
    }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Exit += (_, _) => Cts.Cancel();
            try
            {
                for (int i = 0; ; i++)
                    try { ApplyPendingRestore(); break; }
                    catch (IOException) when (i < 10) { Thread.Sleep(500); }   // proses lama mungkin belum selesai menutup berkas
                desktop.MainWindow = Vault.Exists(Program.DataDir) ? new LoginWindow() : new SetupWindow();
            }
            catch (Exception e)
            {
                desktop.MainWindow = Neo.ErrorWindow("Gagal membuka aplikasi", e.Message);
            }
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Tutup aplikasi lalu jalankan lagi (mis. setelah restore), dengan jeda supaya berkas DB sudah dilepas.</summary>
    public static void Restart()
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("lokasi aplikasi tidak ditemukan");
        var psi = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false };
        psi.ArgumentList.Add("--data");
        psi.ArgumentList.Add(Program.DataDir);
        System.Diagnostics.Process.Start(psi);
        (Current!.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }
}
