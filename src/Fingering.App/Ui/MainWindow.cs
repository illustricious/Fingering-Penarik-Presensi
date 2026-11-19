using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Fingering.Core;

namespace Fingering.Ui;

public sealed class MainWindow : Window
{
    readonly string user;
    readonly Border content = new() { Padding = new Thickness(0) };
    ScrollViewer scroller = null!;
    readonly StackPanel nav = new() { Orientation = Orientation.Horizontal };
    readonly TextBlock conHead = Neo.Text("Konsol — sepi bro, belum ada kerjaan", 13, FontWeight.Black);
    readonly TextBox con = new() { IsReadOnly = true, FontFamily = new FontFamily("Cascadia Mono,Consolas,Menlo,DejaVu Sans Mono,monospace"), FontSize = 12,
        Background = Neo.B("#111111"), Foreground = Neo.B("#A6FFB8"), BorderThickness = new Thickness(0), AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
        // seleksi biru tua + teks putih: terbaca walau kerangka tidak menerapkan warna teks seleksi (hijau muda di atas kuning dulu sulit dibaca)
        SelectionBrush = Neo.B("#2F4FD8"), SelectionForegroundBrush = Neo.B("#FFFFFF"), CaretBrush = Neo.B("#A6FFB8") };
    readonly Border barWrap = new() { Height = 26, Margin = new Thickness(0, 0, 0, 8), IsVisible = false, ClipToBounds = true, Background = Neo.B(Neo.White),
        BorderBrush = Neo.B(Neo.Ink), BorderThickness = new Thickness(3), CornerRadius = new CornerRadius(8) };
    readonly Grid barGrid = new();
    readonly Border barFill = new();
    readonly TextBlock barText = Neo.Text("", 12, FontWeight.Black);
    Job? watched;
    bool dirty;
    string page = "dasbor";
    int punchOffset;
    readonly PunchFilter filter = new();

    static readonly (string key, string label)[] Pages =
    [("dasbor", "DASBOR"), ("mesin", "MESIN"), ("presensi", "PRESENSI"), ("pegawai", "PEGAWAI"), ("riwayat", "RIWAYAT"), ("setelan", "SETELAN")];

    public MainWindow(string user)
    {
        this.user = user;
        Title = "Fingering — Cuma butuh satu jari buat kamu puas";
        Icon = Neo.AppIcon();
        Width = 1180; Height = 820; MinWidth = 900; MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"), Margin = new Thickness(24, 14, 24, 6) };
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        brand.Children.Add(Neo.IconImage(56));
        brand.Children.Add(Neo.Logo(44));
        head.Children.Add(brand);
        var tag = Neo.Tagline(13);
        tag.Margin = new Thickness(10, 0, 0, 6);
        tag.VerticalAlignment = VerticalAlignment.Bottom;
        Grid.SetColumn(tag, 1); head.Children.Add(tag);
        var who = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Spacing = 8 };
        who.Children.Add(Neo.Chip(user, Neo.Yellow));
        who.Children.Add(Neo.Btn("KELUAR", Logout, "white"));
        who.Children[1].Margin = new Thickness(0);
        Grid.SetColumn(who, 3); head.Children.Add(who);

        foreach (var (k, l) in Pages)
        {
            var key = k;
            var b = Neo.Btn(l, () => Go(key), "white");
            b.Classes.Add("nav");
            b.Tag = key;
            nav.Children.Add(b);
        }
        var navWrap = new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Content = nav, Margin = new Thickness(24, 6, 24, 0) };

        var scroll = new ScrollViewer { Content = new Border { Padding = new Thickness(24, 10, 24, 16), Child = content } };
        scroller = scroll;

        con.Classes.Add("console");
        ScrollViewer.SetAllowAutoHide(con, false);   // scrollbar tidak mengambang di atas teks (baris terakhir tidak tertutup)
        con.Padding = new Thickness(10, 8, 10, 8);
        var conBar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Thickness(0, 0, 0, 6) };
        conBar.Children.Add(conHead);
        conHead.VerticalAlignment = VerticalAlignment.Center;
        var conView = new Border { Height = 170, BorderBrush = Neo.B(Neo.Ink), BorderThickness = new Thickness(3), CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = con };
        var clr = Neo.Btn("BERSIHKAN", () => { con.Text = ""; }, "white");
        clr.Margin = new Thickness(0, 0, 8, 0); clr.Padding = new Thickness(8, 2);
        Grid.SetColumn(clr, 1); conBar.Children.Add(clr);
        var conPanel = new Border { Background = Neo.B(Neo.Pink), BorderBrush = Neo.B(Neo.Ink), BorderThickness = new Thickness(0, 3, 0, 0), Padding = new Thickness(24, 10, 24, 14) };
        Button? toggle = null;
        toggle = Neo.Btn("SEMBUNYIKAN ▼", () =>
        {
            bool hide = conView.IsVisible;   // sedang tampil -> sembunyikan; isi dan status di judul tetap diperbarui
            conView.IsVisible = !hide;
            toggle!.Content = hide ? "TAMPILKAN ▲" : "SEMBUNYIKAN ▼";
            conBar.Margin = new Thickness(0, 0, 0, hide ? 0 : 6);
            conPanel.Padding = new Thickness(24, 10, 24, hide ? 10 : 14);
        }, "yellow");
        toggle.Margin = new Thickness(0); toggle.Padding = new Thickness(8, 2);
        Grid.SetColumn(toggle, 2); conBar.Children.Add(toggle);
        // bar progres menyatu dengan panel konsol, di atas kotak log; tetap tampak saat log disembunyikan
        barGrid.ColumnDefinitions = new ColumnDefinitions("0*,0*,1*");
        barFill.BorderBrush = Neo.B(Neo.Ink); barFill.BorderThickness = new Thickness(0, 0, 3, 0);
        Grid.SetColumn(barFill, 1); barGrid.Children.Add(barFill);
        barText.HorizontalAlignment = HorizontalAlignment.Center; barText.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumnSpan(barText, 3); barGrid.Children.Add(barText);
        barWrap.Child = barGrid;
        var conBox = new StackPanel();
        conBox.Children.Add(conBar);
        conBox.Children.Add(barWrap);
        conBox.Children.Add(conView);
        conPanel.Child = conBox;

        var root = new DockPanel();
        var top = new StackPanel(); top.Children.Add(head); top.Children.Add(navWrap);
        DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        DockPanel.SetDock(conPanel, Dock.Bottom); root.Children.Add(conPanel);
        root.Children.Add(scroll);
        Content = Neo.Frame(this, root, "Fingering — Cuma butuh satu jari buat kamu puas");

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) => { if (dirty) { dirty = false; RefreshConsole(); } };
        timer.Start();
        var barTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        barTimer.Tick += (_, _) => UpdateBar();
        barTimer.Start();
        App.Hub.JobStarted += j => Dispatcher.UIThread.Post(() => Watch(j));
        App.Hub.DataChanged += () => Dispatcher.UIThread.Post(() => { if (page is "dasbor" or "mesin" or "riwayat" or "pegawai") Go(page, keepScroll: true); });
        Go("dasbor");
    }

    void Logout()
    {
        var w = new LoginWindow();
        if (Application.Current!.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d) d.MainWindow = w;
        w.Show();
        Close();
    }

    // ---- konsol pekerjaan ----
    void Watch(Job j)
    {
        if (watched != null) watched.Changed -= OnJobChanged;
        watched = j;
        j.Changed += OnJobChanged;
        con.Text = "";
        RefreshConsole();
    }

    void OnJobChanged() => dirty = true;

    /// <summary>
    /// Bar progres pekerjaan yang sedang dipantau: kemajuan nyata bila pekerjaan melaporkannya (baca log besar, daftar user),
    /// kalau tidak, penanda bergerak bolak-balik; selesai = penuh hijau, gagal = merah.
    /// </summary>
    void UpdateBar()
    {
        if (watched is not { } j) { barWrap.IsVisible = false; return; }
        barWrap.IsVisible = true;
        double left = 0, fill, right;
        string text, color;
        if (!j.Done)
        {
            color = Neo.Yellow;
            if (j.Progress is { } p) { fill = p; text = $"{j.Kind.ToUpperInvariant()} · {p:P0}"; }
            else
            {
                var phase = Environment.TickCount64 % 1400 / 1400.0;
                var tri = phase < 0.5 ? phase * 2 : (1 - phase) * 2;
                fill = 0.3; left = tri * 0.7;
                text = $"{j.Kind.ToUpperInvariant()} · berjalan…";
            }
        }
        else if (j.Error != "") { color = "#FF4D4D"; fill = j.Progress ?? 1; text = $"{j.Kind.ToUpperInvariant()} · GAGAL"; }
        else { color = Neo.Green; fill = 1; text = $"{j.Kind.ToUpperInvariant()} · SELESAI"; }
        right = Math.Max(0, 1 - left - fill);
        var cols = barGrid.ColumnDefinitions;
        cols[0].Width = new GridLength(left, GridUnitType.Star);
        cols[1].Width = new GridLength(fill, GridUnitType.Star);
        cols[2].Width = new GridLength(right, GridUnitType.Star);
        barFill.Background = Neo.B(color);
        barFill.BorderThickness = new Thickness(fill > 0 && right > 0 ? 3 : 0, 0, fill > 0 && right > 0 ? 3 : 0, 0);
        barText.Text = text;
    }

    void RefreshConsole()
    {
        if (watched is not { } j) return;
        var state = j.Done ? (j.Error != "" ? "GAGAL" : "SELESAI") : "BERJALAN…";
        conHead.Text = $"Konsol — {j.Kind} {j.Device}  [{state}]" + (j.Done ? $"  {(j.Error != "" ? j.Error : j.Summary)}" : "");
        con.Text = j.Log;
        con.CaretIndex = con.Text.Length;
    }

    // ---- navigasi ----
    void Go(string key, bool keepScroll = false)
    {
        var keep = keepScroll && page == key ? scroller.Offset : default;
        page = key;
        foreach (var c in nav.Children.OfType<Button>())
        {
            if ((string?)c.Tag == key) c.Classes.Add("active"); else c.Classes.Remove("active");
        }
        content.Child = key switch
        {
            "mesin" => DevicesPage(),
            "presensi" => PunchesPage(),
            "pegawai" => EmployeesPage(),
            "riwayat" => RunsPage(),
            "setelan" => SettingsPage(),
            _ => DashboardPage(),
        };
        scroller.Offset = keep; // halaman baru mulai dari atas; segar-ulang halaman yang sama mempertahankan posisi
    }

    static StackPanel Heading(string t, string sub = "")
    {
        var p = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        p.Children.Add(Neo.Text(t, 26, FontWeight.Black));
        if (sub != "") p.Children.Add(Neo.Text(sub, 13, FontWeight.SemiBold, "#444444", true));
        return p;
    }

    // ---- dasbor ----
    Control DashboardPage()
    {
        var s = App.Store.Stats();
        var root = new StackPanel();
        root.Children.Add(Heading("Dasbor", s.First == "" ? "Belum ada log presensi. Tambah mesin, lalu tarik presensi dulu bro." : $"Data presensi {s.First[..10]} s/d {s.Last[..10]}"));
        var wrap = new WrapPanel();
        void Stat(string label, string val, string color)
        {
            var p = new StackPanel { Width = 170 };
            p.Children.Add(Neo.Text(label.ToUpperInvariant(), 11, FontWeight.Black));
            p.Children.Add(Neo.Text(val, 34, FontWeight.Black));
            wrap.Children.Add(Neo.Card(p, color, 6, 14));
        }
        Stat("Mesin", s.Devices.ToString("N0"), Neo.Yellow);
        Stat("Pegawai", s.Employees.ToString("N0"), Neo.Green);
        Stat("Log presensi", s.Punches.ToString("N0"), Neo.Blue);
        Stat("Log hari ini", s.Today.ToString("N0"), Neo.Pink);
        Stat("PIN tak dikenal", s.UnknownPins.ToString("N0"), Neo.Orange);
        root.Children.Add(wrap);
        if (s.UnknownPins > 0)
            root.Children.Add(Neo.Card(Neo.Text($"{s.UnknownPins} PIN punya log tetapi belum ada di daftar pegawai. Buka MESIN → \"Tarik pegawai\" untuk melengkapi nama.", 13, FontWeight.Bold, Neo.Ink, true), Neo.White, 5, 14));
        return root;
    }

    // ---- mesin ----
    Control DevicesPage()
    {
        var root = new StackPanel();
        root.Children.Add(Heading("Mesin", "Satu mesin hanya melayani satu sesi pada satu waktu; jangan menarik dari dua tempat sekaligus."));
        var add = Neo.Btn("+ TAMBAH MESIN", () => EditDevice(new Device()), "green");
        root.Children.Add(add);
        foreach (var d in App.Store.Devices()) root.Children.Add(DeviceCard(d));
        if (root.Children.Count == 2) root.Children.Add(Neo.Text("Belum ada mesin. Mau narik apa kalau mesinnya aja belum ada bro?", 14));
        return root;
    }

    // Kartu mesin: aksi harian di depan, sisanya dilipat di "OPSI LANJUTAN". Pensil = ubah mesin, silang = hapus dari daftar.
    Control DeviceCard(Device d)
    {
        var p = new StackPanel { Spacing = 6 };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        titleRow.Children.Add(Neo.Text(d.Name, 20, FontWeight.Black));
        titleRow.Children.Add(Neo.Chip($"#{d.MachineNo}", Neo.Yellow));
        if (!d.Enabled) titleRow.Children.Add(Neo.Chip("NONAKTIF", Neo.Orange));
        titleRow.Margin = new Thickness(0, 0, 100, 0);   // sisakan ruang untuk tombol pensil + silang
        p.Children.Add(titleRow);
        p.Children.Add(Neo.Text($"{d.Host}:{d.Port}" + (d.PullIntervalMin > 0 ? $"  ·  otomatis tiap {d.PullIntervalMin} mnt" : "") + (d.Model != "" ? $"  ·  {d.Model}" : ""), 12, FontWeight.SemiBold, "#444444"));
        p.Children.Add(Neo.Text($"{d.PunchCount:N0} log tersimpan  ·  tarik terakhir: {(d.LastPullAt == "" ? "-" : d.LastPullAt)}", 13, FontWeight.Bold));
        if (App.Hub.LiveState(d.Id) != "") p.Children.Add(Neo.Text("Pantau langsung: " + App.Hub.LiveState(d.Id), 12, FontWeight.Bold, "#0B7A3B"));
        if (d.LastError != "") p.Children.Add(Neo.Text("Galat terakhir: " + d.LastError, 12, FontWeight.Bold, "#C0143C", true));

        // aksi utama
        var main = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        main.Children.Add(Neo.Btn("TARIK DATA BARU", () => Run(() => App.Hub.PullNew(d.Id)), "green"));
        main.Children.Add(Neo.Btn("TARIK SEMUA LOG", () => Run(() => App.Hub.Pull(d.Id)), "yellow"));
        main.Children.Add(Neo.Btn("UJI KONEKSI", () => Run(() => App.Hub.Probe(d.Id)), "blue"));
        p.Children.Add(main);

        // opsi lanjutan (dilipat)
        var adv = new WrapPanel { IsVisible = false };
        adv.Children.Add(Neo.Btn("INFO & JAM MESIN", () => Run(() => App.Hub.InfoJob(d.Id)), "white"));
        adv.Children.Add(Neo.Btn("LIHAT USER DI MESIN", () => Run(() => App.Hub.Users(d.Id, "lihat")), "white"));
        adv.Children.Add(Neo.Btn("TARIK PEGAWAI (BARU)", () => Run(() => App.Hub.Users(d.Id, "baru")), "white"));
        adv.Children.Add(Neo.Btn("TARIK PEGAWAI + PERBARUI NAMA", async () =>
        {
            if (await Confirm("Nama pegawai di database akan ditimpa dengan nama dari mesin. Lanjut?")) Run(() => App.Hub.Users(d.Id, "semua"));
        }, "white"));
        adv.Children.Add(Neo.Btn("UNDUH SIDIK JARI", async () =>
        {
            if (await Confirm("Mengunduh data sidik jari (biometrik) ke database lokal. Lanjut?")) Run(() => App.Hub.Fingerprints(d.Id));
        }, "white"));
        adv.Children.Add(Neo.Btn("SINKRON JAM MESIN", async () =>
        {
            if (await Confirm("Ini MENGUBAH jam di mesin menjadi jam komputer ini. Lanjut?")) Run(() => App.Hub.Clock(d.Id, true));
        }, "yellow"));
        if (App.Store.Setting("allow_users", "0") == "1")
        {
            adv.Children.Add(Neo.Btn("+ USER DI MESIN", () => AddUserDialog(d), "yellow"));
            adv.Children.Add(Neo.Btn("UBAH NAMA USER", () => RenameUserDialog(d), "yellow"));
            adv.Children.Add(Neo.Btn("HAPUS USER DI MESIN", () => DeleteUserDialog(d), "red"));
        }
        if (App.Store.Setting("allow_clear", "0") == "1")
            adv.Children.Add(Neo.Btn("HAPUS LOG DI MESIN", async () =>
            {
                if (await ConfirmDanger($"Semua log di mesin \"{d.Name}\" akan DIHAPUS PERMANEN (setelah disalin ke database PC dan diverifikasi).", d.Name, "nama mesin"))
                    Run(() => App.Hub.ClearLogs(d.Id));
            }, "red"));
        if (App.Store.Setting("allow_empty_enroll", "0") == "1")
            adv.Children.Add(Neo.Btn("KOSONGKAN SEMUA USER DI MESIN", async () =>
            {
                if (await ConfirmDanger($"SEMUA user, sidik jari, sandi dan kartu di mesin \"{d.Name}\" akan DIKOSONGKAN (log presensi tidak ikut terhapus). " +
                        "Data enroll dicadangkan ke database PC lebih dulu, tetapi aplikasi ini BELUM bisa menulis balik sidik jari ke mesin; " +
                        "setelah ini orang harus didaftarkan ulang di mesin.", d.Name, "nama mesin"))
                    Run(() => App.Hub.EmptyEnroll(d.Id));
            }, "red"));
        var more = Neo.Btn("OPSI LANJUTAN ▼", () => { }, "white");
        more.Padding = new Thickness(10, 4); more.FontSize = 12;
        more.Click += (_, _) => { adv.IsVisible = !adv.IsVisible; more.Content = adv.IsVisible ? "OPSI LANJUTAN ▲" : "OPSI LANJUTAN ▼"; };
        p.Children.Add(more);
        p.Children.Add(adv);

        // pojok kanan atas: pensil (ubah) dan silang (hapus dari daftar)
        var pencil = Neo.IconBtn(Neo.PencilIcon(), () => EditDevice(d), "white");
        ToolTip.SetTip(pencil, "Ubah mesin");
        var x = Neo.Btn("✕", async () =>
        {
            if (!await Confirm($"Hapus mesin \"{d.Name}\" dari daftar?")) return;
            try { App.Store.DeleteDevice(d.Id); Go("mesin"); } catch (Exception e) { Toast(e.Message); }
        }, "red");
        x.Classes.Add("cap");
        x.Margin = new Thickness(0); x.Padding = new Thickness(0); x.Width = 36; x.Height = 32; x.FontSize = 15;
        ToolTip.SetTip(x, "Hapus mesin dari daftar");
        var corner = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Right };
        corner.Children.Add(pencil); corner.Children.Add(x);
        var cardBody = new Grid();
        cardBody.Children.Add(p);
        cardBody.Children.Add(corner);
        return Neo.Card(cardBody, Neo.White, 7, 18);
    }

    void Run(Func<Job> start)
    {
        try { start(); }
        catch (Exception e) { Toast(e.Message); }
    }

    // ---- presensi ----
    Control PunchesPage()
    {
        var root = new StackPanel();
        root.Children.Add(Heading("Presensi", "Log mentah dari mesin, terbaru di atas. Waktu = jam dinding mesin."));
        var devs = App.Store.Devices();
        var from = Neo.DatePicker(filter.From == "" ? null : DateTime.Parse(filter.From, CultureInfo.InvariantCulture));
        var to = Neo.DatePicker(filter.To == "" ? null : DateTime.Parse(filter.To, CultureInfo.InvariantCulture));
        var pin = Neo.Field(filter.Pin, "PIN atau nama", 200);
        var dev = Neo.Combo(new[] { "Semua mesin" }.Concat(devs.Select(d => d.Name)), devs.FindIndex(d => d.Id == filter.DeviceId) + 1, 180);

        void Apply(bool reset = true)
        {
            filter.From = from.SelectedDate?.ToString("yyyy-MM-dd") ?? "";
            filter.To = to.SelectedDate?.ToString("yyyy-MM-dd") ?? "";
            filter.Pin = pin.Text?.Trim() ?? "";
            filter.DeviceId = dev.SelectedIndex > 0 ? devs[dev.SelectedIndex - 1].Id : 0;
            if (reset) punchOffset = 0;
            Go("presensi");
        }

        var bar = new WrapPanel();
        bar.Children.Add(Neo.Labeled("Dari", from));
        bar.Children.Add(Neo.Labeled("Sampai", to));
        bar.Children.Add(Neo.Labeled("PIN / nama", pin));
        bar.Children.Add(Neo.Labeled("Mesin", dev));
        var find = Neo.Btn("CARI", () => Apply(), "yellow"); find.VerticalAlignment = VerticalAlignment.Bottom; find.Margin = new Thickness(0, 0, 8, 10);
        var csv = Neo.Btn("EKSPOR CSV", () => _ = ExportCsv(), "green"); csv.VerticalAlignment = VerticalAlignment.Bottom; csv.Margin = new Thickness(0, 0, 8, 10);
        var noDenda = Neo.Check("Tanpa denda (kolom tetap ada)", false); noDenda.VerticalAlignment = VerticalAlignment.Bottom; noDenda.Margin = new Thickness(0, 0, 12, 18);
        var xl = Neo.Btn("EKSPOR REKAP (XLSX)", () => { Apply(false); _ = ExportXlsx(noDenda.IsChecked != true); }, "blue"); xl.VerticalAlignment = VerticalAlignment.Bottom; xl.Margin = new Thickness(0, 0, 8, 10);
        bar.Children.Add(find); bar.Children.Add(csv); bar.Children.Add(noDenda); bar.Children.Add(xl);
        root.Children.Add(bar);

        filter.Offset = punchOffset; filter.Limit = 200;
        var (rows, total) = App.Store.Punches(filter);
        var grid = new StackPanel();
        grid.Children.Add(PunchRowUi("WAKTU", "PIN", "NAMA", "MESIN", "VERIF", "IO", true));
        foreach (var r in rows) grid.Children.Add(PunchRowUi(r.At, r.Pin, r.Name == "" ? "—" : r.Name, r.Device, r.Verify.ToString(), r.InOut.ToString(), false));
        if (rows.Count == 0) grid.Children.Add(new Border { Padding = new Thickness(14), Child = Neo.Text("Sepi bro, belum ada yang absen. Tarik presensi dulu!", 14) });
        root.Children.Add(Neo.Card(grid, Neo.White, 6, 0, 10));

        const int PageSize = 200;
        int pages = Math.Max(1, (total + PageSize - 1) / PageSize), cur = Math.Min(punchOffset / PageSize + 1, pages);
        void GoPage(int p) { punchOffset = (Math.Clamp(p, 1, pages) - 1) * PageSize; Apply(false); }
        var pager = new WrapPanel();
        void Add(Control c) { c.VerticalAlignment = VerticalAlignment.Center; pager.Children.Add(c); }
        Add(Neo.Btn("« AWAL", () => GoPage(1), "white", cur > 1));
        Add(Neo.Btn("◀ SEBELUMNYA", () => GoPage(cur - 1), "white", cur > 1));
        var pg = Neo.Field(cur.ToString(), "", 80); pg.TextAlignment = TextAlignment.Center;
        void Jump() { if (int.TryParse((pg.Text ?? "").Trim().Replace(".", ""), out var p)) GoPage(p); else pg.Text = cur.ToString(); }
        pg.KeyDown += (_, e) => { if (e.Key == Key.Enter) Jump(); };
        var jump = Neo.Btn("LOMPAT", Jump, "yellow");
        var pgBox = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(4, 0, 12, 8) };
        pgBox.Children.Add(Neo.Text("Hal.", 13, FontWeight.Black)); pgBox.Children.Add(pg);
        pgBox.Children.Add(Neo.Text($"dari {pages:N0}", 13, FontWeight.Black)); pgBox.Children.Add(jump);
        foreach (var c in pgBox.Children) c.VerticalAlignment = VerticalAlignment.Center;
        jump.Margin = new Thickness(0);
        pager.Children.Add(pgBox);
        Add(Neo.Btn("BERIKUTNYA ▶", () => GoPage(cur + 1), "white", cur < pages));
        Add(Neo.Btn("AKHIR »", () => GoPage(pages), "white", cur < pages));
        root.Children.Add(pager);
        root.Children.Add(Neo.Text($"{(total == 0 ? 0 : punchOffset + 1):N0}–{Math.Min(punchOffset + PageSize, total):N0} dari {total:N0} log", 13, FontWeight.Bold));
        return root;
    }

    static Control PunchRowUi(string at, string pin, string name, string dev, string verify, string io, bool header)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("170,90,*,140,60,50"), Margin = new Thickness(0) };
        var cells = new[] { at, pin, name, dev, verify, io };
        for (int i = 0; i < cells.Length; i++)
        {
            var t = Neo.Text(cells[i], header ? 11 : 13, header ? FontWeight.Black : FontWeight.SemiBold);
            t.Margin = new Thickness(12, 7);
            t.TextTrimming = TextTrimming.CharacterEllipsis;
            Grid.SetColumn(t, i); g.Children.Add(t);
        }
        return new Border { Child = g, Background = Neo.B(header ? Neo.Yellow : Neo.White), BorderBrush = Neo.B(Neo.Ink), BorderThickness = new Thickness(0, 0, 0, header ? 3 : 1.5) };
    }

    async Task ExportCsv()
    {
        var f = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = CsvName(), DefaultExtension = "csv",
            FileTypeChoices = [new FilePickerFileType("CSV") { Patterns = ["*.csv"] }],
        });
        if (f == null) return;
        var q = new PunchFilter { From = filter.From, To = filter.To, Pin = filter.Pin, DeviceId = filter.DeviceId, Limit = 1_000_000 };
        var (rows, _) = App.Store.Punches(q);
        var sb = new StringBuilder("waktu,pin,nama,mesin,verifikasi,inout,workcode\n");
        foreach (var r in rows) sb.Append($"{r.At},{Csv(r.Pin)},{Csv(r.Name)},{Csv(r.Device)},{r.Verify},{r.InOut},{r.WorkCode}\n");
        await using var s = await f.OpenWriteAsync();
        s.SetLength(0);
        await s.WriteAsync(new UTF8Encoding(true).GetPreamble());
        await s.WriteAsync(Encoding.UTF8.GetBytes(sb.ToString()));
        Toast($"{rows.Count:N0} baris diekspor");
    }

    string CsvName() =>
        filter.From != "" && filter.To != "" ? $"presensi {filter.From} sd {filter.To}.csv"
        : filter.From != "" ? $"presensi sejak {filter.From}.csv" : $"presensi {DateTime.Now:yyyy-MM-dd}.csv";

    /// <summary>Rekap per pegawai per hari + denda; file xlsx dikunci (read-only). Nama berkas dan judul mengikuti tanggal.</summary>
    async Task ExportXlsx(bool includeDenda = true)
    {
        if (!DateTime.TryParseExact(filter.From, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var from)
            || !DateTime.TryParseExact(filter.To, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var to))
        { Toast("Isi tanggal Dari dan Sampai dulu bro, baru ekspor rekap"); return; }
        if (to < from) { Toast("Tanggal Sampai lebih awal dari Dari bro"); return; }
        var f = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = includeDenda ? XlsxExport.SuggestedFileName(from, to) : XlsxExport.SuggestedFileName(from, to).Replace(".xlsx", " (tanpa denda).xlsx"), DefaultExtension = "xlsx",
            FileTypeChoices = [new FilePickerFileType("Excel") { Patterns = ["*.xlsx"] }],
        });
        if (f == null) return;
        var tmp = Path.Combine(Path.GetTempPath(), "fingering-" + Guid.NewGuid().ToString("N") + ".xlsx");
        try
        {
            var rules = DendaRules.Load(App.Store);
            var emps = App.Store.Employees("").ToDictionary(e => e.Pin, e => new EmpInfo(e.Name, e.Nip, e.Shifts, e.Group));
            var punches = App.Store.PunchTimes(from.AddDays(-1), to.AddDays(1).AddDays(1).AddSeconds(-1));
            var q = filter.Pin;
            if (q != "") punches = punches.Where(p => p.pin == q || (emps.TryGetValue(p.pin, out var ei) && ei.Name.Contains(q, StringComparison.OrdinalIgnoreCase))).ToList();
            var data = Rekap.Build(punches, pin => emps.GetValueOrDefault(pin), rules, from, to, App.Store.LastPunchDate());
            if (data.Count == 0) { Toast("Tidak ada scan pada rentang itu bro, belum ada yang diekspor"); return; }
            await Task.Run(() => XlsxExport.Write(tmp, from, to, data, rules, App.Store.Setting("xlsx_password", "fingering"), includeDenda));
            await using (var s = await f.OpenWriteAsync())
            {
                s.SetLength(0);
                await using var src = File.OpenRead(tmp);
                await src.CopyToAsync(s);
            }
            Toast($"Rekap {data.Count} pegawai diekspor{(includeDenda ? "" : " TANPA denda")}: {XlsxExport.SuggestedFileName(from, to)} (dikunci read-only)");
        }
        catch (Exception e) { Toast("Ekspor gagal: " + e.Message); }
        finally { try { File.Delete(tmp); } catch (IOException) { } }
    }

    // Sel berawalan = + - @ diberi tanda kutip tunggal supaya tidak jadi rumus saat dibuka di Excel.
    static string Csv(string s)
    {
        if (s.Length > 0 && "=+-@\t\r".Contains(s[0])) s = "'" + s;
        return s.IndexOfAny([',', '"', '\n']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    // ---- pegawai ----
    Control EmployeesPage()
    {
        var root = new StackPanel();
        root.Children.Add(Heading("Pegawai", "Daftar PIN → nama. Isi lewat MESIN → \"Tarik pegawai\" atau tambah manual."));
        var q = Neo.Field("", "Cari PIN / nama / NIP", 260);
        var list = new StackPanel();
        void Fill()
        {
            list.Children.Clear();
            var emps = App.Store.Employees(q.Text?.Trim() ?? "");
            var head = new Grid { ColumnDefinitions = new ColumnDefinitions("90,*,150,160,90,90") };
            string[] hs = ["PIN", "NAMA", "KELOMPOK", "NIP", "STATUS", ""];
            for (int i = 0; i < hs.Length; i++) { var t = Neo.Text(hs[i], 11, FontWeight.Black); t.Margin = new Thickness(12, 7); Grid.SetColumn(t, i); head.Children.Add(t); }
            list.Children.Add(new Border { Child = head, Background = Neo.B(Neo.Yellow), BorderBrush = Neo.B(Neo.Ink), BorderThickness = new Thickness(0, 0, 0, 3) });
            foreach (var e in emps)
            {
                var g = new Grid { ColumnDefinitions = new ColumnDefinitions("90,*,150,160,90,90") };
                string[] cs = [e.Pin, e.Name, e.Group == "" ? "—" : e.Group, e.Nip == "" ? "—" : e.Nip, e.Status switch { 1 => "aktif", 0 => "nonaktif", _ => "berhenti" }];
                for (int i = 0; i < cs.Length; i++) { var t = Neo.Text(cs[i], 13); t.Margin = new Thickness(12, 7); Grid.SetColumn(t, i); g.Children.Add(t); }
                var ed = Neo.Btn("UBAH", () => EditEmployee(e), "white"); ed.Margin = new Thickness(6, 3); ed.Padding = new Thickness(8, 2);
                Grid.SetColumn(ed, 5); g.Children.Add(ed);
                list.Children.Add(new Border { Child = g, BorderBrush = Neo.B(Neo.Ink), BorderThickness = new Thickness(0, 0, 0, 1.5) });
            }
            if (emps.Count == 0) list.Children.Add(new Border { Padding = new Thickness(14), Child = Neo.Text("Pegawainya mana bro? Tarik dari mesin dulu.", 14) });
        }
        q.TextChanged += (_, _) => Fill();
        var bar = new WrapPanel();
        bar.Children.Add(Neo.Labeled("Cari", q));
        var add = Neo.Btn("+ TAMBAH", () => EditEmployee(null), "green"); add.VerticalAlignment = VerticalAlignment.Bottom; add.Margin = new Thickness(0, 0, 8, 10);
        bar.Children.Add(add);
        root.Children.Add(bar);
        Fill();
        root.Children.Add(Neo.Card(list, Neo.White, 6, 0, 10));
        return root;
    }

    // ---- riwayat ----
    Control RunsPage()
    {
        var root = new StackPanel();
        root.Children.Add(Heading("Riwayat tarik", "50 penarikan terakhir (jumlah dibaca vs baru setelah penghapusan duplikat)."));
        var list = new StackPanel();
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("170,140,90,90,90,*") };
        string[] hs = ["WAKTU", "MESIN", "STATUS", "DIBACA", "BARU", "CATATAN"];
        for (int i = 0; i < hs.Length; i++) { var t = Neo.Text(hs[i], 11, FontWeight.Black); t.Margin = new Thickness(12, 7); Grid.SetColumn(t, i); head.Children.Add(t); }
        list.Children.Add(new Border { Child = head, Background = Neo.B(Neo.Yellow), BorderBrush = Neo.B(Neo.Ink), BorderThickness = new Thickness(0, 0, 0, 3) });
        foreach (var r in App.Store.Runs(50))
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("170,140,90,90,90,*") };
            string[] cs = [r.Started, r.Device, r.Status, r.Fetched.ToString("N0"), r.Inserted.ToString("N0"), r.Error];
            for (int i = 0; i < cs.Length; i++)
            {
                var color = i == 2 ? (r.Status == "ok" ? "#0B7A3B" : r.Status == "error" ? "#C0143C" : Neo.Ink) : Neo.Ink;
                var t = Neo.Text(cs[i], 13, i == 2 ? FontWeight.Black : FontWeight.SemiBold, color);
                t.Margin = new Thickness(12, 7); t.TextTrimming = TextTrimming.CharacterEllipsis; Grid.SetColumn(t, i); g.Children.Add(t);
            }
            list.Children.Add(new Border { Child = g, BorderBrush = Neo.B(Neo.Ink), BorderThickness = new Thickness(0, 0, 0, 1.5) });
        }
        root.Children.Add(Neo.Card(list, Neo.White, 6, 0, 10));
        return root;
    }

    // ---- setelan ----
    Control SettingsPage()
    {
        var root = new StackPanel();
        root.Children.Add(Heading("Setelan"));
        var dump = Neo.Check("Simpan data mentah mesin ke dump.db untuk analisis ulang", App.Store.Setting("dump_logs", "1") == "1");
        var sync = Neo.Check("Sinkron jam mesin otomatis setelah tarik (MENGUBAH jam mesin bila selisih > 2 menit)", App.Store.Setting("sync_clock", "0") == "1");
        var clear = Neo.Check("Izinkan HAPUS LOG DI MESIN (tombol muncul di halaman Mesin; selalu simpan + verifikasi ke PC dulu)", App.Store.Setting("allow_clear", "0") == "1");
        clear.IsCheckedChanged += (_, _) => { App.Store.SetSetting("allow_clear", clear.IsChecked == true ? "1" : "0"); };
        var users = Neo.Check("Izinkan TAMBAH / UBAH NAMA / HAPUS USER DI MESIN (data enroll dicadangkan ke PC sebelum dihapus)", App.Store.Setting("allow_users", "0") == "1");
        users.IsCheckedChanged += (_, _) => { App.Store.SetSetting("allow_users", users.IsChecked == true ? "1" : "0"); };
        var empty = Neo.Check("Izinkan KOSONGKAN SEMUA USER + SIDIK JARI DI MESIN (bahaya; bawaan mati; log presensi tidak ikut terhapus; data enroll dicadangkan ke PC dulu)", App.Store.Setting("allow_empty_enroll", "0") == "1");
        empty.IsCheckedChanged += (_, _) => { App.Store.SetSetting("allow_empty_enroll", empty.IsChecked == true ? "1" : "0"); };
        dump.IsCheckedChanged += (_, _) => App.Store.SetSetting("dump_logs", dump.IsChecked == true ? "1" : "0");
        sync.IsCheckedChanged += (_, _) => App.Store.SetSetting("sync_clock", sync.IsChecked == true ? "1" : "0");
        var live = Neo.Field(App.Store.Setting("live_sec", "0"), "0", 90);
        live.TextChanged += (_, _) =>
        {
            if (int.TryParse(live.Text, out var v) && v >= 0) App.Store.SetSetting("live_sec", v.ToString());
        };
        var liveRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        liveRow.Children.Add(live);
        liveRow.Children.Add(new TextBlock { Text = "detik · PANTAU LANGSUNG (0 = mati). Menanyakan jumlah log tiap N detik; bila bertambah, tarik data baru. Minimal 5 detik.", FontWeight = FontWeight.SemiBold, FontSize = 13, TextWrapping = TextWrapping.Wrap, MaxWidth = 700, VerticalAlignment = VerticalAlignment.Center });
        var opts = new StackPanel { Spacing = 12 };
        opts.Children.Add(Neo.Text("OPSI TARIK", 12, FontWeight.Black));
        opts.Children.Add(dump); opts.Children.Add(sync); opts.Children.Add(clear); opts.Children.Add(users); opts.Children.Add(empty); opts.Children.Add(liveRow);
        root.Children.Add(Neo.Card(opts, Neo.White, 6, 18));

        var id = Neo.Field(user, "ID baru", 240);
        var pw = Neo.Field("", "Sandi baru (min. 8 karakter)", 280, true);
        var f = new WrapPanel();
        f.Children.Add(Neo.Labeled("ID", id));
        f.Children.Add(Neo.Labeled("Sandi baru", pw));
        var save = Neo.Btn("GANTI LOGIN", () =>
        {
            try
            {
                // vault dulu (kunci data dibungkus ulang dengan sandi baru; DB tidak dienkripsi ulang), baru tabel app_user
                Vault.ChangeLogin(Program.DataDir, App.Key!, id.Text ?? "", pw.Text ?? "");
                App.Store.ChangeLogin(id.Text ?? "", pw.Text ?? "");
                pw.Text = ""; Toast("Login diganti. Berlaku pada masuk berikutnya.");
            }
            catch (Exception e) { Toast(e.Message); }
        }, "pink");
        save.VerticalAlignment = VerticalAlignment.Bottom; save.Margin = new Thickness(0, 0, 8, 10);
        f.Children.Add(save);
        var lg = new StackPanel { Spacing = 6 };
        lg.Children.Add(Neo.Text("GANTI ID / SANDI LOGIN", 12, FontWeight.Black));
        lg.Children.Add(f);
        root.Children.Add(Neo.Card(lg, Neo.White, 6, 18));

        var sec = new StackPanel { Spacing = 6 };
        sec.Children.Add(Neo.Text("KEAMANAN DATA & BACKUP TERENKRIPSI", 12, FontWeight.Black));
        sec.Children.Add(Neo.Text($"Database dienkripsi AES-256 (SQLCipher) di {Program.DataDir}. Kuncinya acak dan hanya tersimpan terbungkus sandi login. " +
            "Backup (.fingbak) dikunci dengan frasa sandi sendiri sehingga bisa dipulihkan di PC lain; restore mengganti seluruh data dan menyimpan salinan data sekarang di folder cadangan.", 12, FontWeight.SemiBold, "#444444", true));
        var secRow = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        secRow.Children.Add(Neo.Btn("BACKUP SEKARANG…", () => _ = BackupNow(), "green"));
        secRow.Children.Add(Neo.Btn("RESTORE DARI BACKUP…", () => _ = RestoreBackup(), "yellow"));
        secRow.Children.Add(Neo.Btn("KODE PEMULIHAN BARU", () => _ = NewRecoveryCode(), "white"));
        sec.Children.Add(secRow);
        root.Children.Add(Neo.Card(sec, Neo.White, 6, 18));

        var dr = DendaRules.Load(App.Store);
        var fA = Neo.Field(dr.NominalA.ToString(), "", 110); var fB = Neo.Field(dr.NominalB.ToString(), "", 110); var fC = Neo.Field(dr.NominalC.ToString(), "", 110);
        var fL1 = Neo.Field(dr.LupaSatu.ToString(), "", 110); var fL2 = Neo.Field(dr.LupaDua.ToString(), "", 110);
        var fBa = Neo.Field(dr.BatasA.ToString(), "", 90); var fBb = Neo.Field(dr.BatasB.ToString(), "", 90);
        var fG = Neo.Field(dr.GraceMinutes.ToString(), "", 90); var fDd = Neo.Field(dr.DedupeSeconds.ToString(), "", 90);
        var fSh = Neo.Field(dr.ShiftStarts, "07:00,14:00,21:00", 520); var fPw = Neo.Field(App.Store.Setting("xlsx_password", "fingering"), "", 220);
        var dErr = Neo.Text("", 13, FontWeight.Black, "#C0143C", true);
        var dw = new WrapPanel();
        dw.Children.Add(Neo.Labeled("Denda a (Rp)", fA)); dw.Children.Add(Neo.Labeled("Denda b (Rp)", fB)); dw.Children.Add(Neo.Labeled("Denda c (Rp)", fC));
        dw.Children.Add(Neo.Labeled("Lupa absen 1x (Rp)", fL1)); dw.Children.Add(Neo.Labeled("Lupa absen 2x+ (Rp)", fL2));
        dw.Children.Add(Neo.Labeled("a: telat < (menit)", fBa)); dw.Children.Add(Neo.Labeled("b: sampai (menit)", fBb));
        dw.Children.Add(Neo.Labeled("Datang lebih awal maks (menit)", fG)); dw.Children.Add(Neo.Labeled("Scan ganda dalam (detik)", fDd));
        dw.Children.Add(Neo.Labeled("Jam mulai shift yang dikenali (pisah koma)", fSh)); dw.Children.Add(Neo.Labeled("Sandi tulis Excel", fPw));
        var dsave = Neo.Btn("SIMPAN ATURAN DENDA", () =>
        {
            try
            {
                var r = new DendaRules
                {
                    NominalA = int.Parse(fA.Text!), NominalB = int.Parse(fB.Text!), NominalC = int.Parse(fC.Text!), LupaSatu = int.Parse(fL1.Text!), LupaDua = int.Parse(fL2.Text!),
                    BatasA = int.Parse(fBa.Text!), BatasB = int.Parse(fBb.Text!), GraceMinutes = int.Parse(fG.Text!), DedupeSeconds = int.Parse(fDd.Text!), ShiftStarts = fSh.Text ?? "",
                };
                if (r.BatasA < 1 || r.BatasB < r.BatasA || DendaRules.ParseShifts(r.ShiftStarts).Count == 0 || (fPw.Text ?? "").Length < 4) throw new FormatException();
                r.Save(App.Store); App.Store.SetSetting("xlsx_password", fPw.Text!);
                dErr.Text = "Tersimpan.";
            }
            catch (Exception) { dErr.Text = "Cek isian: angka bulat, batas b >= batas a, minimal satu jam shift (07:00), sandi minimal 4 karakter."; }
        }, "green");
        var dp = new StackPanel { Spacing = 6 };
        dp.Children.Add(Neo.Text("ATURAN DENDA & REKAP (untuk ekspor XLSX)", 12, FontWeight.Black));
        dp.Children.Add(dw); dp.Children.Add(dErr); dp.Children.Add(dsave);
        root.Children.Add(Neo.Card(dp, Neo.White, 6, 18));

        var info = new StackPanel { Spacing = 4 };
        info.Children.Add(Neo.Text("DATA", 12, FontWeight.Black));
        info.Children.Add(Neo.Text($"Folder data: {Program.DataDir}", 13, FontWeight.SemiBold, Neo.Ink, true));
        info.Children.Add(Neo.Text("dump.fdb memuat data mentah mesin (satu berkas, terenkripsi); aman dihapus bila tidak diperlukan. \"Tarik data baru\" hanya mengambil log sejak log terakhir di database ini dan tidak mengubah mesin.", 12, FontWeight.SemiBold, "#444444", true));
        root.Children.Add(Neo.Card(info, Neo.Yellow, 6, 18));

        var imp = new StackPanel { Spacing = 6 };
        imp.Children.Add(Neo.Text("IMPOR BACKUP FINGERSPOT PERSONNEL (.sql)", 12, FontWeight.Black));
        imp.Children.Add(Neo.Text("Mengambil log presensi dari mesin yang IP-nya sama dengan mesin di sini, dan pegawai yang PIN-nya belum ada. Nama yang sudah ada tidak ditimpa, log ganda diabaikan, dan database dicadangkan dulu ke folder \"cadangan\".", 12, FontWeight.SemiBold, "#444444", true));
        imp.Children.Add(Neo.Btn("PILIH BACKUP.SQL…", () => _ = ImportLegacy(), "blue"));
        root.Children.Add(Neo.Card(imp, Neo.White, 6, 18));
        return root;
    }

    static string? PassCheck(string[] v) => v[0].Length < 8 ? "Frasa sandi minimal 8 karakter." : v.Length > 1 && v[0] != v[1] ? "Ulangi frasa sandi tidak sama." : null;

    async Task BackupNow()
    {
        var r = await Dialogs.Ask(this, "Backup terenkripsi",
            "Backup dikunci dengan frasa sandi khusus (terpisah dari sandi login) supaya bisa dipulihkan di PC lain. Catat frasa ini: tanpa frasa, backup tidak bisa dibuka siapa pun.",
            [("Frasa sandi backup (min. 8)", true, ""), ("Ulangi frasa sandi", true, "")], "PILIH LOKASI…", PassCheck);
        if (r == null) return;
        var f = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Simpan backup terenkripsi", SuggestedFileName = BackupService.SuggestedName(DateTime.Now), DefaultExtension = "fingbak",
            FileTypeChoices = [new FilePickerFileType("Backup Fingering") { Patterns = ["*" + BackupService.Extension] }],
        });
        if (f?.TryGetLocalPath() is not { } path) return;
        try
        {
            Toast("Membuat backup terenkripsi, tunggu sebentar bro…");
            var info = await Task.Run(() =>
            {
                if (File.Exists(path)) File.Delete(path);   // pengguna sudah mengonfirmasi timpa di dialog simpan
                return BackupService.Create(App.Store, App.Hub.Dumps, path, r[0]);
            });
            Toast($"Backup selesai: {info.Punches:N0} log, {info.Employees:N0} pegawai, {info.Bytes / 1024.0 / 1024.0:N1} MB, terenkripsi. {path}");
        }
        catch (Exception e) { Toast("Backup gagal: " + e.Message); }
    }

    async Task RestoreBackup()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Pilih berkas backup Fingering", AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Backup Fingering") { Patterns = ["*" + BackupService.Extension] }],
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
        var r = await Dialogs.Ask(this, "Buka backup", "Masukkan frasa sandi yang dipakai saat backup dibuat.", [("Frasa sandi backup", true, "")], "PERIKSA", v => v[0] == "" ? "Isi frasa sandi." : null);
        if (r == null) return;
        try
        {
            Toast("Memeriksa backup…");
            var info = await Task.Run(() => BackupService.Inspect(path, r[0]));
            if (!await ConfirmDanger($"Backup berisi {info.Devices} mesin, {info.Employees:N0} pegawai, {info.Punches:N0} log ({(info.First == "" ? "-" : info.First[..10])} s/d {(info.Last == "" ? "-" : info.Last[..10])}). " +
                    "SEMUA data saat ini akan DIGANTI dengan isi backup ini; salinan data sekarang disimpan otomatis di folder cadangan\\sebelum-restore-*. Aplikasi akan dimulai ulang.", "RESTORE", "kata"))
                return;
            Toast("Menyiapkan restore (mengenkripsi ulang dengan kunci PC ini)…");
            await Task.Run(() => BackupService.Stage(path, r[0], App.Key!, Program.DbPath + ".restore", Program.DumpPath + ".restore"));
            Toast("Restore siap. Memulai ulang aplikasi…");
            App.Restart();
        }
        catch (WrongKeyException) { Toast("Frasa sandi backup salah."); }
        catch (Exception e) { Toast("Restore gagal: " + e.Message); }
    }

    async Task NewRecoveryCode()
    {
        if (!await Confirm("Kode pemulihan yang lama tidak akan berlaku lagi. Buat kode baru?")) return;
        try
        {
            var code = await Task.Run(() => Vault.NewRecoveryCode(Program.DataDir, App.Key!));
            await Dialogs.ShowCode(this, "KODE PEMULIHAN BARU", code, "Simpan kode ini di tempat aman. Kode ini satu-satunya cara membuka data kalau sandi login lupa.");
        }
        catch (Exception e) { Toast("Gagal: " + e.Message); }
    }

    async Task ImportLegacy()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Pilih backup.sql dari Fingerspot Personnel", AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Dump SQL") { Patterns = ["*.sql"] }],
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
        try
        {
            Toast("Membaca backup, tunggu sebentar bro…");
            var preview = await Task.Run(() => LegacyImport.Run(App.Store, path, apply: false));
            if (!await Confirm("Hasil pemeriksaan (belum ada yang ditulis):\n\n" + LegacyImport.Describe(preview, false) + "\n\nLanjut impor?")) { Toast("Impor dibatalkan"); return; }
            var done = await Task.Run(() => LegacyImport.Run(App.Store, path, apply: true, backupDir: Path.Combine(Program.DataDir, "cadangan")));
            Toast($"Impor selesai: {done.PunchAdded:N0} log baru, {done.EmployeesAdded:N0} pegawai baru. Cadangan: {done.BackupFile}");
            Go("setelan");
        }
        catch (Exception e) { Toast("Impor gagal: " + e.Message); }
    }

    // ---- dialog ----
    void Toast(string msg) => conHead.Text = msg;

    async Task<bool> Confirm(string msg)
    {
        var tcs = new TaskCompletionSource<bool>();
        var w = new Window { Title = "Konfirmasi", Width = 460, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false };
        var p = new StackPanel { Margin = new Thickness(24), Spacing = 16 };
        p.Children.Add(Neo.Text(msg, 15, FontWeight.Bold, Neo.Ink, true));
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(Neo.Btn("LANJUT", () => { tcs.TrySetResult(true); w.Close(); }, "green"));
        row.Children.Add(Neo.Btn("BATAL", () => { tcs.TrySetResult(false); w.Close(); }, "white"));
        p.Children.Add(row);
        w.Content = Neo.Frame(w, p, w.Title ?? "", false, true);
        w.Closed += (_, _) => tcs.TrySetResult(false);
        await w.ShowDialog(this);
        return await tcs.Task;
    }

    /// <summary>Konfirmasi ganda: (1) dialog Ya/Batal, (2) ketik teks yang diminta DAN sandi login.</summary>
    async Task<bool> ConfirmDanger(string msg, string expected, string what)
    {
        if (!await Confirm(msg + "\n\nGa bisa di-undo ya bro. Yakin lanjut ke konfirmasi akhir?")) return false;
        var tcs = new TaskCompletionSource<bool>();
        var w = new Window { Title = "Konfirmasi akhir", Width = 520, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false };
        var box = Neo.Field("", expected);
        var pw = Neo.Field("", "sandi login", password: true);
        var err = Neo.Text("", 13, FontWeight.Black, "#C0143C", true);
        var p = new StackPanel { Margin = new Thickness(24), Spacing = 8 };
        p.Children.Add(Neo.Text("KONFIRMASI AKHIR (jangan asal pencet bro)", 20, FontWeight.Black));
        p.Children.Add(Neo.Text(msg, 14, FontWeight.Bold, Neo.Ink, true));
        p.Children.Add(Neo.Labeled($"Ketik {what}: {expected}", box));
        p.Children.Add(Neo.Labeled("Sandi login Fingering", pw));
        p.Children.Add(err);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var go = Neo.Btn("HAPUS / LANJUTKAN", () =>
        {
            if (App.Store.CheckLogin(user, pw.Text ?? "")) { tcs.TrySetResult(true); w.Close(); }
            else { err.Text = "Sandi salah."; pw.Text = ""; }
        }, "pink", false);
        box.TextChanged += (_, _) => go.IsEnabled = box.Text?.Trim() == expected;
        row.Children.Add(go);
        row.Children.Add(Neo.Btn("BATAL", () => { tcs.TrySetResult(false); w.Close(); }, "white"));
        p.Children.Add(row);
        w.Content = Neo.Frame(w, p, w.Title ?? "", false, true);
        w.Closed += (_, _) => tcs.TrySetResult(false);
        await w.ShowDialog(this);
        return await tcs.Task;
    }

    Window SmallDialog(string title, out StackPanel body)
    {
        var w = new Window { Title = title, Width = 480, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false };
        body = new StackPanel { Margin = new Thickness(24), Spacing = 6 };
        body.Children.Add(Neo.Text(title, 22, FontWeight.Black));
        w.Content = Neo.Frame(w, body, w.Title ?? "", false, true);
        return w;
    }

    void AddUserDialog(Device d)
    {
        var w = SmallDialog("Tambah user di mesin " + d.Name, out var p);
        var pin = Neo.Field("", "mis. 174"); var name = Neo.Field(""); var pw = Neo.Field("", "1-8 digit angka");
        var priv = Neo.Combo(["user biasa", "admin"], 0, 160);
        var err = Neo.Text("", 13, FontWeight.Black, "#C0143C", true);
        p.Children.Add(Neo.Text("User dibuat dengan sandi angka di layar mesin; sidik jari didaftarkan langsung di mesin. Format ini belum diuji di mesin asli — hasilnya diverifikasi otomatis.", 12, FontWeight.SemiBold, "#444444", true));
        p.Children.Add(Neo.Labeled("PIN", pin)); p.Children.Add(Neo.Labeled("Nama", name));
        p.Children.Add(Neo.Labeled("Hak akses", priv)); p.Children.Add(Neo.Labeled("Sandi di mesin (angka)", pw)); p.Children.Add(err);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(Neo.Btn("TAMBAH", () =>
        {
            if ((pin.Text ?? "").Trim() == "" || (name.Text ?? "").Trim() == "" || (pw.Text ?? "") == "") { err.Text = "PIN, nama, dan sandi wajib diisi."; return; }
            w.Close();
            Run(() => App.Hub.AddUser(d.Id, pin.Text!, name.Text!, priv.SelectedIndex == 1 ? 1 : 0, pw.Text!));
        }, "green"));
        row.Children.Add(Neo.Btn("BATAL", w.Close, "white"));
        p.Children.Add(row);
        w.ShowDialog(this);
    }

    void RenameUserDialog(Device d)
    {
        var w = SmallDialog("Ubah nama user di mesin", out var p);
        var pin = Neo.Field("", "PIN"); var name = Neo.Field("", "nama baru");
        p.Children.Add(Neo.Labeled("PIN", pin)); p.Children.Add(Neo.Labeled("Nama baru", name));
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(Neo.Btn("UBAH", () =>
        {
            if ((pin.Text ?? "").Trim() == "" || (name.Text ?? "").Trim() == "") return;
            w.Close(); Run(() => App.Hub.RenameUser(d.Id, pin.Text!.Trim(), name.Text!));
        }, "green"));
        row.Children.Add(Neo.Btn("BATAL", w.Close, "white"));
        p.Children.Add(row);
        w.ShowDialog(this);
    }

    void DeleteUserDialog(Device d)
    {
        var w = SmallDialog("Hapus user di mesin " + d.Name, out var p);
        var pin = Neo.Field("", "PIN yang dihapus");
        p.Children.Add(Neo.Text("Data enroll (sidik jari, sandi, kartu) diunduh dan disimpan ke database PC dulu, baru dihapus dari mesin.", 12, FontWeight.SemiBold, "#444444", true));
        p.Children.Add(Neo.Labeled("PIN", pin));
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(Neo.Btn("LANJUT", async () =>
        {
            var id = (pin.Text ?? "").Trim();
            if (id == "") return;
            w.Close();
            if (await ConfirmDanger($"User PIN {id} akan DIHAPUS dari mesin \"{d.Name}\".", id, "PIN"))
                Run(() => App.Hub.DeleteUser(d.Id, id));
        }, "pink"));
        row.Children.Add(Neo.Btn("BATAL", w.Close, "white"));
        p.Children.Add(row);
        w.ShowDialog(this);
    }

    void EditDevice(Device d)
    {
        var isNew = d.Id == 0;
        var w = new Window { Title = isNew ? "Tambah mesin" : "Ubah mesin", Width = 560, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false };
        var name = Neo.Field(d.Name); var host = Neo.Field(d.Host, "192.168.1.253");
        var port = Neo.Field(d.Port.ToString()); var no = Neo.Field(d.MachineNo.ToString());
        var key = Neo.Field(d.CommKey, "kosong = 0"); var model = Neo.Field(d.Model);
        var layout = Neo.Combo(new[] { "auto" }.Concat(Layouts.All.Select(l => l.Name)), Math.Max(0, new[] { "auto" }.Concat(Layouts.All.Select(l => l.Name)).ToList().FindIndex(x => x.Equals(d.Layout, StringComparison.OrdinalIgnoreCase))), 150);
        var interval = Neo.Field(d.PullIntervalMin.ToString());
        var enabled = Neo.Check("Aktif", d.Enabled || isNew);
        var err = Neo.Text("", 13, FontWeight.Black, "#C0143C", true);
        var wp = new WrapPanel { Width = 500 };
        wp.Children.Add(Neo.Labeled("Nama", name, 240)); wp.Children.Add(Neo.Labeled("Alamat IP / host", host, 220));
        wp.Children.Add(Neo.Labeled("Port", port, 100)); wp.Children.Add(Neo.Labeled("Nomor mesin", no, 100)); wp.Children.Add(Neo.Labeled("Sandi komunikasi", key, 160));
        wp.Children.Add(Neo.Labeled("Model", model, 200)); wp.Children.Add(Neo.Labeled("Tata letak", layout)); wp.Children.Add(Neo.Labeled("Tarik otomatis (menit, 0 = manual)", interval, 200));
        var p = new StackPanel { Margin = new Thickness(24), Spacing = 6 };
        p.Children.Add(Neo.Text(isNew ? "Tambah mesin" : "Ubah mesin", 22, FontWeight.Black));
        p.Children.Add(wp); p.Children.Add(enabled); p.Children.Add(err);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        row.Children.Add(Neo.Btn("SIMPAN", () =>
        {
            try
            {
                d.Name = name.Text ?? ""; d.Host = host.Text ?? ""; d.Model = model.Text ?? ""; d.CommKey = key.Text ?? "";
                d.Port = int.Parse(port.Text ?? "5005"); d.MachineNo = int.Parse(no.Text ?? "1"); d.PullIntervalMin = int.Parse(interval.Text ?? "0");
                d.Layout = (string?)layout.SelectedItem ?? "auto"; d.Enabled = enabled.IsChecked == true;
                if (d.Port is < 1 or > 65535 || d.MachineNo is < 1 or > 255 || d.PullIntervalMin < 0) throw new FormatException();
                App.Store.SaveDevice(d);
                w.Close(); Go("mesin");
            }
            catch (FormatException) { err.Text = "Port (1-65535), nomor mesin (1-255) dan interval harus berupa angka yang valid."; }
            catch (Exception e) { err.Text = e.Message.Contains("UNIQUE") ? "Mesin dengan alamat/nomor yang sama sudah terdaftar." : e.Message; }
        }, "green"));
        row.Children.Add(Neo.Btn("BATAL", w.Close, "white"));
        p.Children.Add(row);
        w.Content = Neo.Frame(w, new ScrollViewer { Content = p }, w.Title ?? "", false, true);
        w.ShowDialog(this);
    }

    void EditEmployee(Employee? e)
    {
        var w = new Window { Title = e == null ? "Tambah pegawai" : "Ubah pegawai", Width = 480, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false };
        var pin = Neo.Field(e?.Pin ?? ""); pin.IsEnabled = e == null;
        var name = Neo.Field(e?.Name ?? ""); var nip = Neo.Field(e?.Nip ?? ""); var shifts = Neo.Field(e?.Shifts ?? "", "kosong = otomatis, mis. 07:00,14:00");
        var status = Neo.Combo(["nonaktif", "aktif", "berhenti"], e?.Status ?? 1, 160);
        var groupNames = new List<string> { "(tanpa kelompok)" }; groupNames.AddRange(ShiftGroups.Names);
        var grp = Neo.Combo(groupNames, Math.Max(0, groupNames.FindIndex(x => string.Equals(x, e?.Group, StringComparison.OrdinalIgnoreCase))), 260);
        var err = Neo.Text("", 13, FontWeight.Black, "#C0143C", true);
        var p = new StackPanel { Margin = new Thickness(24), Spacing = 4 };
        p.Children.Add(Neo.Text(e == null ? "Tambah pegawai" : "Ubah pegawai", 22, FontWeight.Black));
        p.Children.Add(Neo.Labeled("PIN (ID di mesin)", pin)); p.Children.Add(Neo.Labeled("Nama", name));
        p.Children.Add(Neo.Labeled("NIP", nip)); p.Children.Add(Neo.Labeled("Kelompok (aturan jam shift)", grp)); p.Children.Add(Neo.Labeled("Jam masuk shift pegawai ini (menimpa kelompok)", shifts)); p.Children.Add(Neo.Labeled("Status", status)); p.Children.Add(err);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(Neo.Btn("SIMPAN", () =>
        {
            try { {
                if ((shifts.Text ?? "").Trim() != "" && DendaRules.ParseShifts(shifts.Text!).Count == 0) { err.Text = "Format shift: 07:00,14:00"; return; }
                App.Store.UpsertEmployee(pin.Text ?? "", nip.Text ?? "", name.Text ?? "", status.SelectedIndex, shifts.Text ?? "", grp.SelectedIndex <= 0 ? "" : groupNames[grp.SelectedIndex]);
            } w.Close(); Go("pegawai"); }
            catch (Exception ex) { err.Text = ex.Message; }
        }, "green"));
        row.Children.Add(Neo.Btn("BATAL", w.Close, "white"));
        p.Children.Add(row);
        w.Content = Neo.Frame(w, p, w.Title ?? "", false, true);
        w.ShowDialog(this);
    }
}
