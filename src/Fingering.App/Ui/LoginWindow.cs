using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Fingering.Core;

namespace Fingering.Ui;

public sealed class LoginWindow : Window
{
    readonly TextBox id = Neo.Field("", "ID");
    readonly TextBox pw = Neo.Field("", "Kata sandi", password: true);
    readonly TextBlock err = Neo.Text("", 13, FontWeight.Black, "#C0143C");
    int fails;
    bool busy;

    public LoginWindow()
    {
        Title = "Fingering — masuk";
        Icon = Neo.AppIcon();
        Width = 520; Height = 650;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var tag = Neo.Tagline(15);
        tag.HorizontalAlignment = HorizontalAlignment.Left;
        tag.Margin = new Thickness(6, 0, 0, 22);

        var form = new StackPanel { Spacing = 10 };
        form.Children.Add(Neo.Labeled("ID", id));
        form.Children.Add(Neo.Labeled("Kata sandi", pw));
        form.Children.Add(err);
        var go = Neo.Btn("MASUK  →", () => _ = Submit(), "pink");
        go.HorizontalAlignment = HorizontalAlignment.Stretch;
        go.Margin = new Thickness(0, 6, 0, 0);
        form.Children.Add(go);
        var forgot = Neo.Btn("LUPA SANDI? PAKAI KODE PEMULIHAN", () => _ = Recover(), "white");
        forgot.HorizontalAlignment = HorizontalAlignment.Stretch; forgot.FontSize = 11; forgot.Padding = new Thickness(8, 4);
        form.Children.Add(forgot);

        var root = new StackPanel { Margin = new Thickness(32, 36, 32, 24) };
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        brand.Children.Add(Neo.IconImage(72));
        brand.Children.Add(Neo.Logo(50));
        root.Children.Add(brand);
        root.Children.Add(tag);
        root.Children.Add(Neo.Card(form, Neo.White, 8, 20));
        Content = Neo.Frame(this, root, "Fingering — masuk", resizable: false);

        KeyDown += (_, e) => { if (e.Key == Key.Enter) _ = Submit(); };
        Opened += (_, _) => id.Focus();
    }

    void Enter(byte[] key, string user, string pass)
    {
        App.Open(key, user, pass);
        var main = new MainWindow(user);
        if (Application.Current!.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d) d.MainWindow = main;
        main.Show();
        Close();
    }

    async Task Submit()
    {
        if (busy) return;
        busy = true;
        err.Text = "";
        try
        {
            var u = id.Text?.Trim() ?? "";
            var p = pw.Text ?? "";
            // membuka vault = menurunkan kunci dari sandi (PBKDF2 600.000 putaran) lalu membuka brankas; sengaja tidak instan
            var key = await Task.Run(() => Vault.Unlock(Program.DataDir, u, p));
            if (key != null)
            {
                try { Enter(key, u, p); }
                catch (Exception e) { err.Text = "Gagal membuka database: " + e.Message; }
                return;
            }
            fails++;
            err.Text = "Salah bro. Jarinya kepleset ya?";
            pw.Text = "";
            if (fails >= 3) { err.Text += $" Tunggu {Math.Min(fails * 2, 30)} detik…"; await Task.Delay(Math.Min(fails * 2, 30) * 1000); err.Text = "Salah bro. Jarinya kepleset ya?"; }
        }
        catch (Exception e) { err.Text = e.Message; }
        finally { busy = false; }
    }

    /// <summary>Lupa sandi: kode pemulihan membuka brankas, lalu pengguna menetapkan ID + sandi baru dan menerima kode pemulihan baru.</summary>
    async Task Recover()
    {
        if (busy) return;
        var r = await Dialogs.Ask(this, "Pulihkan akses",
            "Masukkan kode pemulihan (32 karakter, dicatat saat setup awal) lalu tetapkan ID dan sandi baru. Data tidak berubah.",
            [("Kode pemulihan", false, "XXXX-XXXX-XXXX-…"), ("ID baru", false, ""), ("Sandi baru (min. 8)", true, ""), ("Ulangi sandi baru", true, "")],
            "PULIHKAN", v =>
            {
                try { Vault.CheckCredentials(v[1], v[2]); } catch (Exception e) { return e.Message; }
                return v[2] != v[3] ? "Ulangi sandi tidak sama." : null;
            });
        if (r == null) return;
        busy = true;
        try
        {
            var key = await Task.Run(() => Vault.UnlockRecovery(Program.DataDir, r[0]));
            if (key == null) { err.Text = "Kode pemulihan salah."; return; }
            await Task.Run(() => Vault.ChangeLogin(Program.DataDir, key, r[1], r[2]));
            var code = await Task.Run(() => Vault.NewRecoveryCode(Program.DataDir, key));   // kode lama sudah terpakai: terbitkan yang baru
            await Dialogs.ShowCode(this, "KODE PEMULIHAN BARU", code, "Akses dipulihkan. Kode pemulihan yang lama tidak berlaku lagi; simpan kode baru ini di tempat aman.");
            Enter(key, r[1].Trim(), r[2]);
        }
        catch (Exception e) { err.Text = "Gagal memulihkan: " + e.Message; }
        finally { busy = false; }
    }
}
