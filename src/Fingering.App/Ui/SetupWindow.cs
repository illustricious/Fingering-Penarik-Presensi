// Setup awal (sekali, saat vault belum ada): buat ID + sandi, buat database terenkripsi (atau migrasi otomatis DB lama), tampilkan kode pemulihan.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Fingering.Core;

namespace Fingering.Ui;

public sealed class SetupWindow : Window
{
    readonly TextBox id = Neo.Field("", "mis. admin");
    readonly TextBox pw = Neo.Field("", "min. 8 karakter", password: true);
    readonly TextBox pw2 = Neo.Field("", "ulangi sandi", password: true);
    readonly TextBlock err = Neo.Text("", 13, FontWeight.Black, "#C0143C", true);
    readonly Button go;
    readonly string? legacy;
    bool busy;

    public SetupWindow()
    {
        Title = "Fingering — setup awal";
        Icon = Neo.AppIcon();
        Width = 560; Height = 750;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        legacy = Migrator.FindLegacy(Program.LegacyDirs);

        var form = new StackPanel { Spacing = 8 };
        form.Children.Add(Neo.Text("SETUP AWAL", 12, FontWeight.Black));
        form.Children.Add(Neo.Text("Buat ID dan sandi login. Sandi ini juga yang membuka database terenkripsi (AES-256), jadi jangan sampai lupa.", 13, FontWeight.SemiBold, "#333333", true));
        if (legacy != null)
            form.Children.Add(Neo.Card(Neo.Text($"Ditemukan database lama di {Path.GetDirectoryName(legacy)}. Isinya akan dimigrasi otomatis ke database terenkripsi (diverifikasi baris per baris), tanpa ada yang hilang.", 13, FontWeight.Bold, Neo.Ink, true), Neo.Green, 4, 10, 8));
        form.Children.Add(Neo.Labeled("ID", id));
        form.Children.Add(Neo.Labeled("Sandi", pw));
        form.Children.Add(Neo.Labeled("Ulangi sandi", pw2));
        form.Children.Add(err);
        go = Neo.Btn(legacy != null ? "BUAT & MIGRASIKAN" : "BUAT & ENKRIPSI", () => _ = Create(), "pink");
        go.HorizontalAlignment = HorizontalAlignment.Stretch;
        form.Children.Add(go);

        var root = new StackPanel { Margin = new Thickness(32, 26, 32, 24) };
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        brand.Children.Add(Neo.IconImage(64));
        brand.Children.Add(Neo.Logo(46));
        root.Children.Add(brand);
        var tag = Neo.Tagline(14);
        tag.HorizontalAlignment = HorizontalAlignment.Left; tag.Margin = new Thickness(6, 0, 0, 16);
        root.Children.Add(tag);
        root.Children.Add(Neo.Card(form, Neo.White, 8, 20));
        Content = Neo.Frame(this, root, "Fingering — setup awal", resizable: false);
        Opened += (_, _) => id.Focus();
    }

    async Task Create()
    {
        if (busy) return;
        err.Text = "";
        var user = (id.Text ?? "").Trim(); var pass = pw.Text ?? "";
        try { Vault.CheckCredentials(user, pass); }
        catch (Exception e) { err.Text = e.Message; return; }
        if (pass != (pw2.Text ?? "")) { err.Text = "Ulangi sandi tidak sama."; return; }
        busy = true; go.IsEnabled = false; go.Content = "MEMBUAT… (tunggu)";
        try
        {
            var (key, code, rep) = await Task.Run(() => Build(user, pass));
            await Dialogs.ShowCode(this, "KODE PEMULIHAN", code,
                "Simpan kode ini di tempat aman (catat di kertas / password manager). Kode hanya muncul SEKALI. Kalau lupa sandi, kode inilah satu-satunya cara membuka data lagi." +
                (rep != null ? $"\n\nMigrasi selesai: {rep.Punches:N0} log, {rep.Employees:N0} pegawai, {rep.Devices:N0} mesin dipindah dan terverifikasi." : ""));
            if (legacy != null) await OfferWipe(legacy);
            App.Open(key, user, pass);
            var main = new MainWindow(user);
            if (Application.Current!.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d) d.MainWindow = main;
            main.Show();
            Close();
        }
        catch (Exception e)
        {
            err.Text = "Gagal: " + e.Message;
            busy = false; go.IsEnabled = true; go.Content = legacy != null ? "BUAT & MIGRASIKAN" : "BUAT & ENKRIPSI";
        }
    }

    (byte[] key, string code, MigrationReport? rep) Build(string user, string pass)
    {
        Directory.CreateDirectory(Program.DataDir);
        // sisa berkas tanpa vault (setup yang terputus) tidak dihapus, hanya disingkirkan
        foreach (var f in new[] { Program.DbPath, Program.DumpPath })
            foreach (var s in new[] { "", "-wal", "-shm" })
                if (File.Exists(f + s)) File.Move(f + s, f + s + ".yatim-" + DateTime.Now.ToString("yyyyMMddHHmmss"));
        var key = Vault.NewKey();
        MigrationReport? rep = null;
        if (legacy != null)
            rep = Migrator.Migrate(legacy, Path.Combine(Path.GetDirectoryName(legacy)!, "dump.db"), Program.DbPath, Program.DumpPath, key);
        else
        {
            using (new Store(Program.DbPath, key)) { }
            using (new DumpStore(Program.DumpPath, key)) { }
        }
        var (_, code) = Vault.Create(Program.DataDir, user, pass, key);   // vault ditulis TERAKHIR: setup yang gagal tidak meninggalkan setengah jadi
        return (key, code, rep);
    }

    /// <summary>Tawarkan menghapus DB lama yang polos (beserta cadangan impor lama di foldernya) setelah migrasi terverifikasi.</summary>
    async Task OfferWipe(string legacyDb)
    {
        var dir = Path.GetDirectoryName(legacyDb)!;
        var extra = Directory.Exists(Path.Combine(dir, "cadangan")) ? Directory.GetFiles(Path.Combine(dir, "cadangan"), "presensi-sebelum-impor-*.db") : [];
        var r = await Dialogs.Ask(this, "Hapus database lama?",
            $"Database lama di {dir} masih tersimpan tanpa enkripsi (siapa pun yang membuka folder itu bisa membacanya). Karena data sudah pindah dan terverifikasi, sebaiknya dihapus. " +
            $"Yang dihapus: presensi.db, dump.db{(extra.Length > 0 ? $" dan {extra.Length} berkas cadangan lama" : "")}. Ketik HAPUS untuk menghapus, atau batal untuk menyimpannya dulu.",
            [("Ketik HAPUS", false, "HAPUS")], "HAPUS DB LAMA", v => v[0].Trim() == "HAPUS" ? null : "Ketik HAPUS (huruf besar) untuk melanjutkan.");
        if (r == null) return;
        Migrator.WipeDelete(legacyDb);
        Migrator.WipeDelete(Path.Combine(dir, "dump.db"));
        foreach (var f in extra) Migrator.WipeDelete(f);
    }
}
