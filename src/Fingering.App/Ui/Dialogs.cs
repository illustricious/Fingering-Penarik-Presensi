// Dialog umum bergaya neobrutalism: isian bertanya (frasa sandi, ID, dll.) dan tampilan kode pemulihan.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Fingering.Ui;

public static class Dialogs
{
    static Window NewWindow(string title, double width) =>
        new() { Title = title, Width = width, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false, Icon = Neo.AppIcon() };

    /// <summary>Dialog isian. validate mengembalikan teks galat (dialog tetap terbuka) atau null bila nilai diterima. Hasil null = dibatalkan.</summary>
    public static Task<string[]?> Ask(Window owner, string title, string info, (string label, bool secret, string hint)[] fields,
        string ok = "LANJUT", Func<string[], string?>? validate = null)
    {
        var tcs = new TaskCompletionSource<string[]?>();
        var w = NewWindow(title, 520);
        var p = new StackPanel { Margin = new Thickness(24), Spacing = 6 };
        p.Children.Add(Neo.Text(title, 20, FontWeight.Black));
        if (info != "") p.Children.Add(Neo.Text(info, 13, FontWeight.SemiBold, "#333333", true));
        var boxes = fields.Select(f => Neo.Field("", f.hint, double.NaN, f.secret)).ToArray();
        for (int i = 0; i < fields.Length; i++) p.Children.Add(Neo.Labeled(fields[i].label, boxes[i]));
        var err = Neo.Text("", 13, FontWeight.Black, "#C0143C", true);
        p.Children.Add(err);
        void Submit()
        {
            var vals = boxes.Select(b => b.Text ?? "").ToArray();
            var e = validate?.Invoke(vals);
            if (e != null) { err.Text = e; return; }
            tcs.TrySetResult(vals); w.Close();
        }
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(Neo.Btn(ok, Submit, "green"));
        row.Children.Add(Neo.Btn("BATAL", () => { tcs.TrySetResult(null); w.Close(); }, "white"));
        p.Children.Add(row);
        w.Content = Neo.Frame(w, p, title, false, true);
        w.Closed += (_, _) => tcs.TrySetResult(null);
        w.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) Submit(); };
        w.Opened += (_, _) => boxes.FirstOrDefault()?.Focus();
        w.ShowDialog(owner);
        return tcs.Task;
    }

    /// <summary>Tampilkan kode pemulihan. Tombol tutup baru aktif setelah pengguna mencentang bahwa kode sudah dicatat.</summary>
    public static Task ShowCode(Window owner, string title, string code, string note)
    {
        var tcs = new TaskCompletionSource();
        var w = NewWindow(title, 560);
        var p = new StackPanel { Margin = new Thickness(24), Spacing = 8 };
        p.Children.Add(Neo.Text(title, 20, FontWeight.Black));
        p.Children.Add(Neo.Text(note, 13, FontWeight.SemiBold, "#333333", true));
        var box = new Border { Background = Neo.B(Neo.Yellow), BorderBrush = Neo.B(Neo.Ink), BorderThickness = new Thickness(3), CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 12),
            Child = new SelectableTextBlock { Text = code, FontSize = 20, FontWeight = FontWeight.Black, FontFamily = new FontFamily("Consolas, Cascadia Mono, monospace"), TextWrapping = TextWrapping.Wrap, Foreground = Neo.B(Neo.Ink) } };
        p.Children.Add(box);
        var copy = Neo.Btn("SALIN KE CLIPBOARD", async () => { if (w.Clipboard != null) await w.Clipboard.SetTextAsync(code); }, "blue");
        p.Children.Add(copy);
        var ack = Neo.Check("Sudah saya catat/simpan di tempat aman (tanpa kode ini dan sandi, data tidak bisa dibuka lagi)", false);
        p.Children.Add(ack);
        var done = Neo.Btn("SELESAI", () => { tcs.TrySetResult(); w.Close(); }, "green", false);
        ack.IsCheckedChanged += (_, _) => done.IsEnabled = ack.IsChecked == true;
        p.Children.Add(done);
        w.Content = Neo.Frame(w, p, title, false, closeOnly: true);
        w.Closing += (_, e) => { if (ack.IsChecked != true) e.Cancel = true; };   // tidak bisa ditutup sebelum dicentang
        w.Closed += (_, _) => tcs.TrySetResult();
        w.ShowDialog(owner);
        return tcs.Task;
    }
}
