// Pembantu gaya neobrutalism: garis hitam tebal, bayangan keras tanpa blur, warna jenuh.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Fingering.Ui;

public static class Neo
{
    static readonly Uri IconUri = new("avares://Fingering/Assets/icon.png");

    public static WindowIcon AppIcon() => new(Avalonia.Platform.AssetLoader.Open(IconUri));

    /// <summary>Ikon aplikasi (jari tengah) sebagai gambar.</summary>
    public static Image IconImage(double size) => new()
    {
        Source = new Avalonia.Media.Imaging.Bitmap(Avalonia.Platform.AssetLoader.Open(IconUri)),
        Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center,
    };

    public const string Ink = "#111111", Cream = "#FFF4D6", Yellow = "#FFD93D", Pink = "#FF6B9D", Green = "#5BE584", Blue = "#7B9BFF", Orange = "#FF9F43", White = "#FFFFFF";

    public static IBrush B(string hex) => new SolidColorBrush(Color.Parse(hex));

    public static TextBlock Text(string s, double size = 14, FontWeight? w = null, string color = Ink, bool wrap = false) => new()
    {
        Text = s, FontSize = size, FontWeight = w ?? FontWeight.SemiBold, Foreground = B(color),
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
    };

    public static Border Card(Control child, string bg = White, double shadow = 6, double pad = 16, double radius = 12) => new()
    {
        Background = B(bg), BorderBrush = B(Ink), BorderThickness = new Thickness(3), CornerRadius = new CornerRadius(radius),
        BoxShadow = BoxShadows.Parse($"{shadow} {shadow} 0 0 {Ink}"), Padding = new Thickness(pad), Child = child,
        Margin = new Thickness(0, 0, shadow, shadow + 6),
    };

    public static Border Chip(string s, string bg, double size = 12, double rotate = 0) => new()
    {
        Background = B(bg), BorderBrush = B(Ink), BorderThickness = new Thickness(2.5), CornerRadius = new CornerRadius(6),
        Padding = new Thickness(8, 3), Child = Text(s, size, FontWeight.Black), RenderTransform = new RotateTransform(rotate),
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
    };

    public static Button Btn(string label, Action onClick, string cls = "", bool enabled = true)
    {
        var b = new Button { Content = label, IsEnabled = enabled };
        b.Classes.Add("neo");
        if (cls != "") b.Classes.Add(cls);
        b.Click += (_, _) => onClick();
        return b;
    }

    /// <summary>Ikon pensil (vektor, tidak bergantung font).</summary>
    public static Control PencilIcon(double size = 16) => new Avalonia.Controls.Shapes.Path
    {
        Data = Geometry.Parse("M20.71 7.04c.39-.39.39-1.04 0-1.41l-2.34-2.34c-.39-.39-1.02-.39-1.41 0l-1.83 1.83 3.75 3.75 1.83-1.83zM3 17.25V21h3.75L17.81 9.94l-3.75-3.75L3 17.25z"),
        Fill = B(Ink), Stretch = Stretch.Uniform, Width = size, Height = size,
    };

    /// <summary>Tombol kecil persegi berisi ikon, bergaya sama dengan tombol silang di kartu.</summary>
    public static Button IconBtn(Control icon, Action onClick, string cls = "white")
    {
        var b = Btn("", onClick, cls);
        b.Content = icon;
        b.Classes.Add("cap");
        b.Margin = new Thickness(0); b.Padding = new Thickness(0); b.Width = 36; b.Height = 32;
        return b;
    }

    public static TextBox Field(string text = "", string watermark = "", double width = double.NaN, bool password = false)
    {
        var t = new TextBox { Text = text, Watermark = watermark, Width = width };
        t.Classes.Add("neo");
        if (password) t.PasswordChar = '•';
        return t;
    }

    public static CheckBox Check(string label, bool on)
    {
        var c = new CheckBox { Content = label, IsChecked = on };
        c.Classes.Add("neo");
        return c;
    }

    public static StackPanel Labeled(string label, Control c, double width = double.NaN)
    {
        var p = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 12, 10) };
        p.Children.Add(Text(label.ToUpperInvariant(), 11, FontWeight.Black));
        if (!double.IsNaN(width)) c.Width = width;
        p.Children.Add(c);
        return p;
    }

    public static NeoDatePicker DatePicker(DateTime? d = null) => new(d);

    public static ComboBox Combo(IEnumerable<string> items, int selected = 0, double width = 180)
    {
        var c = new ComboBox { ItemsSource = items.ToList(), SelectedIndex = selected, BorderBrush = B(Ink), BorderThickness = new Thickness(3),
            CornerRadius = new CornerRadius(8), Background = B(White), MinHeight = 42, Width = width, FontWeight = FontWeight.SemiBold };
        return c;
    }

    /// <summary>Judul besar "FINGERING": huruf pink berkontur hitam tebal dengan bayangan keras.</summary>
    public static Control Logo(double size = 56)
    {
        var g = new Grid { Margin = new Thickness(4, 4, size * 0.1 + 4, size * 0.1 + 4) };
        var d = size * 0.09;
        g.Children.Add(new TextBlock { Text = "FINGERING", FontSize = size, FontWeight = FontWeight.Black, Foreground = B(Ink), RenderTransform = new TranslateTransform(d, d) });
        foreach (var (dx, dy) in new[] { (-3, 0), (3, 0), (0, -3), (0, 3), (-2, -2), (2, 2), (-2, 2), (2, -2) })
            g.Children.Add(new TextBlock { Text = "FINGERING", FontSize = size, FontWeight = FontWeight.Black, Foreground = B(Ink), RenderTransform = new TranslateTransform(dx, dy) });
        g.Children.Add(new TextBlock { Text = "FINGERING", FontSize = size, FontWeight = FontWeight.Black, Foreground = B(Pink) });
        return g;
    }

    /// <summary>Title bar buatan sendiri (kuning, garis hitam tebal) pengganti title bar Windows; tombol kecil, seret untuk memindah, klik dua kali = maksimalkan.</summary>
    public static Control Frame(Window w, Control body, string title, bool resizable = true, bool closeOnly = false)
    {
        if (!OperatingSystem.IsWindows()) return body;   // Linux/macOS: pakai title bar sistem (tepi jendela untuk ubah ukuran tidak terjamin pada NoChrome)
        w.ExtendClientAreaToDecorationsHint = true;
        w.ExtendClientAreaChromeHints = Avalonia.Platform.ExtendClientAreaChromeHints.NoChrome;
        w.ExtendClientAreaTitleBarHeightHint = -1;
        w.SystemDecorations = SystemDecorations.Full;

        Button Cap(string glyph, Action a, string cls)
        {
            var b = Btn(glyph, a, cls);
            b.Classes.Add("cap");
            b.Margin = new Thickness(0, 0, 8, 0); b.Padding = new Thickness(0); b.Width = 34; b.Height = 26; b.FontSize = 13;
            return b;
        }
        var caps = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        if (!closeOnly) caps.Children.Add(Cap("—", () => w.WindowState = WindowState.Minimized, "white"));
        Button? max = null;
        if (resizable && !closeOnly)
        {
            max = Cap("□", () => w.WindowState = w.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized, "white");
            caps.Children.Add(max);
        }
        caps.Children.Add(Cap("✕", w.Close, "red"));

        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        left.Children.Add(new Image { Source = new Avalonia.Media.Imaging.Bitmap(Avalonia.Platform.AssetLoader.Open(IconUri)), Width = 20, Height = 20 });
        left.Children.Add(Text(title, 13, FontWeight.Black));
        var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Height = 38 };
        bar.Children.Add(left);
        Grid.SetColumn(caps, 1); bar.Children.Add(caps);
        var barBox = new Border { Background = B(Yellow), BorderBrush = B(Ink), BorderThickness = new Thickness(0, 0, 0, 3), Child = bar };
        barBox.PointerPressed += (_, e) =>
        {
            if (e.Source is Visual v && v.FindAncestorOfType<Button>() != null) return;
            if (!e.GetCurrentPoint(barBox).Properties.IsLeftButtonPressed) return;
            if (e.ClickCount == 2 && resizable) w.WindowState = w.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            else w.BeginMoveDrag(e);
        };
        if (max != null) w.PropertyChanged += (_, e) =>
        {
            if (e.Property == Window.WindowStateProperty) max.Content = w.WindowState == WindowState.Maximized ? "❐" : "□";
        };

        var dock = new DockPanel();
        DockPanel.SetDock(barBox, Dock.Top); dock.Children.Add(barBox);
        dock.Children.Add(body);
        // garis tepi hitam di sekeliling jendela (tanpa title bar sistem tidak ada batas jendela)
        return new Border { BorderBrush = B(Ink), BorderThickness = new Thickness(3), Child = dock };
    }

    public static Control Tagline(double size = 14) => Chip("Cuma butuh satu jari buat kamu puas", Green, size, -3);

    public static Window ErrorWindow(string title, string msg)
    {
        var w = new Window { Title = "Fingering", Width = 520, Height = 260, WindowStartupLocation = WindowStartupLocation.CenterScreen, Icon = AppIcon() };
        var p = new StackPanel { Margin = new Thickness(24), Spacing = 12 };
        p.Children.Add(Text(title, 22, FontWeight.Black));
        p.Children.Add(Text(msg, 14, FontWeight.SemiBold, Ink, true));
        p.Children.Add(Btn("Tutup", w.Close, "pink"));
        w.Content = Neo.Frame(w, p, w.Title ?? "", false, true);
        return w;
    }
}
