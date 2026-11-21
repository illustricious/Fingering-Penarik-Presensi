using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Fingering.Core;

namespace Fingering.Ui;

public sealed partial class MainWindow
{
    long passwordDeviceId;
    string passwordSearch = "";
    int passwordOffset;
    const int PasswordPageSize = 50;

    Control PasswordsPage()
    {
        var root = new StackPanel { Spacing = 8 };
        root.Children.Add(Heading("Password user", "Tarik data password user dari mesin ke database terenkripsi. Penarikan hanya membaca data mesin."));
        root.Children.Add(Neo.Card(Neo.Text("Angka yang dikenali ditandai KANDIDAT dan belum diverifikasi terhadap password asli di mesin. Format lain tetap disimpan sebagai data mentah; tidak ditebak menjadi password.",
            13, FontWeight.SemiBold, Neo.Ink, true), Neo.Yellow, 4, 12, 6));
        var devices = App.Store.Devices();
        if (devices.Count == 0)
        {
            root.Children.Add(Neo.Text("Belum ada mesin. Tambahkan mesin sebelum menarik password.", 14));
            root.Children.Add(Neo.Btn("+ TAMBAH MESIN", () => Go("mesin"), "green"));
            return root;
        }
        int selected = devices.FindIndex(d => d.Id == passwordDeviceId);
        if (selected < 0) selected = 0;
        passwordDeviceId = devices[selected].Id;
        var device = devices[selected];
        var machine = Neo.Combo(devices.Select(d => $"{d.Name} · {d.Host}"), selected, 300);
        machine.SelectionChanged += (_, _) =>
        {
            if (machine.SelectedIndex < 0) return;
            passwordDeviceId = devices[machine.SelectedIndex].Id; passwordOffset = 0; passwordSearch = "";
            Go("password");
        };
        var query = Neo.Field(passwordSearch, "Cari PIN atau nama", 235);
        void Search() { passwordSearch = query.Text?.Trim() ?? ""; passwordOffset = 0; Go("password"); }
        query.KeyDown += (_, e) => { if (e.Key == Key.Enter) Search(); };
        var controls = new WrapPanel();
        controls.Children.Add(Neo.Labeled("Mesin", machine));
        controls.Children.Add(Neo.Labeled("Cari", query));
        var search = Neo.Btn("CARI", Search, "white");
        search.VerticalAlignment = VerticalAlignment.Bottom; search.Margin = new Thickness(0, 0, 8, 10);
        controls.Children.Add(search);
        bool pulling = App.Hub.Jobs.Any(j => j.Kind == "tarik-password" && j.Device == device.Name && !j.Done);
        var pull = Neo.Btn(pulling ? "SEDANG MENARIK…" : "TARIK PASSWORD", () => Run(() =>
        {
            var job = App.Hub.Passwords(device.Id); Go("password", keepScroll: true); return job;
        }), "pink", !pulling);
        pull.VerticalAlignment = VerticalAlignment.Bottom; pull.Margin = new Thickness(0, 0, 0, 10);
        controls.Children.Add(pull); root.Children.Add(controls);

        var (rows, total) = App.Store.DevicePasswords(passwordDeviceId, passwordSearch, PasswordPageSize, passwordOffset);
        if (rows.Count == 0 && passwordOffset > 0)
        {
            passwordOffset = Math.Max(0, (total - 1) / PasswordPageSize * PasswordPageSize);
            (rows, total) = App.Store.DevicePasswords(passwordDeviceId, passwordSearch, PasswordPageSize, passwordOffset);
        }
        root.Children.Add(Neo.Text($"{total:N0} user tersimpan untuk {device.Name}. Password disamarkan; tekan LIHAT untuk membuka kandidat per baris.", 12, FontWeight.SemiBold, "#444444", true));
        const string columns = "90,180,90,140,230,160,185";
        var table = new StackPanel { MinWidth = 1075 };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions(columns) };
        string[] headings = ["PIN", "NAMA DI MESIN", "HAK", "KANDIDAT PASSWORD", "STATUS", "TERAKHIR DIBACA", "AKSI"];
        for (int i = 0; i < headings.Length; i++)
        {
            var label = Neo.Text(headings[i], 11, FontWeight.Black, Neo.Ink, true); label.Margin = new Thickness(10, 10);
            Grid.SetColumn(label, i); header.Children.Add(label);
        }
        table.Children.Add(new Border { Child = header, Background = Neo.B(Neo.Yellow), BorderBrush = Neo.B(Neo.Ink), BorderThickness = new Thickness(0, 0, 0, 3) });
        foreach (var row in rows)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(columns) };
            void Cell(int column, string text, string color = Neo.Ink)
            {
                var label = Neo.Text(text, 12, FontWeight.SemiBold, color, true);
                label.Margin = new Thickness(10, 9); Grid.SetColumn(label, column); grid.Children.Add(label);
            }
            Cell(0, row.Pin); Cell(1, row.Name == "" ? "(nama belum terbaca)" : row.Name);
            Cell(2, row.Privilege switch { 0 => "User", 1 => "Operator", 2 => "Subadmin", 3 => "Admin", _ => row.Privilege.ToString() });
            var candidate = row.Analysis.Candidate;
            var value = new SelectableTextBlock { Text = candidate == null ? "—" : "••••••••", FontSize = 13, FontWeight = FontWeight.Bold,
                Margin = new Thickness(10, 9), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(value, 3); grid.Children.Add(value);
            Cell(4, row.Status, row.Error != "" ? "#C0143C" : row.HasPassword ? "#8B4B00" : "#444444");
            Cell(5, row.FetchedAt == "" ? "—" : row.FetchedAt);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            bool revealed = false;
            Button? reveal = null;
            reveal = Neo.Btn("LIHAT", () =>
            {
                revealed = !revealed; value.Text = revealed ? candidate : "••••••••";
                reveal!.Content = revealed ? "TUTUP" : "LIHAT";
            }, "yellow", candidate != null);
            reveal.FontSize = 11; reveal.Margin = new Thickness(0, 4, 5, 4); reveal.Padding = new Thickness(8, 4);
            var detail = Neo.Btn("DETAIL", () => _ = PasswordDetails(row), "white");
            detail.FontSize = 11; detail.Margin = new Thickness(0, 4); detail.Padding = new Thickness(8, 4);
            actions.Children.Add(reveal); actions.Children.Add(detail);
            grid.DetachedFromVisualTree += (_, _) => { value.Text = candidate == null ? "—" : "••••••••"; revealed = false; };
            Grid.SetColumn(actions, 6); grid.Children.Add(actions);
            table.Children.Add(new Border { Child = grid, BorderBrush = Neo.B(Neo.Ink), BorderThickness = new Thickness(0, 0, 0, 1),
                Background = Neo.B(row.Error == "" ? Neo.White : "#FFF1F1") });
        }
        if (rows.Count == 0) table.Children.Add(new Border { Padding = new Thickness(16), Child = Neo.Text(passwordSearch == "" ? "Belum ada hasil tarikan. Pilih mesin lalu tekan TARIK PASSWORD." : "PIN / nama tidak ditemukan.", 14) });
        root.Children.Add(Neo.Card(new ScrollViewer { Content = table,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled }, Neo.White, 6, 0, 10));
        var pager = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        pager.Children.Add(Neo.Btn("← SEBELUMNYA", () => { passwordOffset -= PasswordPageSize; Go("password"); }, "white", passwordOffset > 0));
        pager.Children.Add(Neo.Text($"Halaman {passwordOffset / PasswordPageSize + 1} / {Math.Max(1, (total + PasswordPageSize - 1) / PasswordPageSize)}", 13));
        pager.Children.Add(Neo.Btn("BERIKUTNYA →", () => { passwordOffset += PasswordPageSize; Go("password"); }, "white", passwordOffset + rows.Count < total));
        root.Children.Add(pager);
        return root;
    }

    async Task PasswordDetails(DevicePasswordRow row)
    {
        var window = SmallDialog($"Password user · PIN {row.Pin}", out var body);
        window.Width = 590;
        body.Children.Add(Neo.Text($"{row.Device} · {row.Name}\n{row.Status}\nData terakhir: {row.FetchedAt}\nPercobaan terakhir: {row.AttemptedAt}", 13, FontWeight.SemiBold, Neo.Ink, true));
        if (row.Error != "") body.Children.Add(Neo.Text(row.Error, 13, FontWeight.Bold, "#C0143C", true));
        body.Children.Add(Neo.Text("Kandidat angka belum diverifikasi terhadap password asli. Data mentah disediakan untuk pemeriksaan format, bukan password yang sudah dipastikan benar.", 13, FontWeight.SemiBold, "#444444", true));
        var raw = new TextBox { IsReadOnly = true, Text = "Data mentah disamarkan", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            MaxHeight = 180, FontFamily = new FontFamily("Consolas, Menlo, monospace"), FontSize = 12 };
        body.Children.Add(raw);
        bool visible = false;
        Button? toggle = null;
        toggle = Neo.Btn("TAMPILKAN DATA MENTAH", () =>
        {
            visible = !visible;
            raw.Text = visible ? Convert.ToHexString(row.Data ?? []) : "Data mentah disamarkan";
            toggle!.Content = visible ? "SEMBUNYIKAN DATA" : "TAMPILKAN DATA MENTAH";
        }, "yellow", row.Data != null);
        body.Children.Add(toggle);
        body.Children.Add(Neo.Btn("TUTUP", () => window.Close(), "white"));
        window.Closed += (_, _) => raw.Text = "";
        await window.ShowDialog(this);
    }
}
