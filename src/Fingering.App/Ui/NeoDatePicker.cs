// Pemilih tanggal bergaya neobrutalism (pengganti CalendarDatePicker bawaan): kotak tanggal, popup kalender
// dengan navigasi bulan/tahun, hari mulai Senin, nama bulan/hari Indonesia (tanpa bergantung pada budaya sistem).
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Fingering.Ui;

public sealed class NeoDatePicker : Grid
{
    static readonly string[] Months = ["Januari", "Februari", "Maret", "April", "Mei", "Juni", "Juli", "Agustus", "September", "Oktober", "November", "Desember"];
    static readonly string[] MonthsShort = ["Jan", "Feb", "Mar", "Apr", "Mei", "Jun", "Jul", "Agu", "Sep", "Okt", "Nov", "Des"];
    static readonly string[] Days = ["SEN", "SEL", "RAB", "KAM", "JUM", "SAB", "MIN"];

    readonly Border face;
    readonly TextBlock label;
    readonly Popup popup;
    readonly Border panelHost = new();
    DateTime? selected;
    DateTime shown;

    public event Action? SelectedDateChanged;

    public DateTime? SelectedDate
    {
        get => selected;
        set { selected = value?.Date; UpdateLabel(); SelectedDateChanged?.Invoke(); }
    }

    public NeoDatePicker(DateTime? d = null, double width = 190)
    {
        Width = width;
        selected = d?.Date;
        shown = Month(selected ?? DateTime.Today);

        label = Neo.Text("", 14, FontWeight.SemiBold);
        label.VerticalAlignment = VerticalAlignment.Center;
        var arrow = Neo.Text("▾", 14, FontWeight.Black);
        arrow.VerticalAlignment = VerticalAlignment.Center;
        var inner = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        inner.Children.Add(label);
        Grid.SetColumn(arrow, 1); inner.Children.Add(arrow);
        face = new Border
        {
            Background = Neo.B(Neo.White), BorderBrush = Neo.B(Neo.Ink), BorderThickness = new Thickness(3), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 0), MinHeight = 42, Cursor = new Cursor(StandardCursorType.Hand), Child = inner,
        };
        face.PointerEntered += (_, _) => face.Background = Neo.B(Neo.Yellow);
        face.PointerExited += (_, _) => face.Background = Neo.B(Neo.White);
        face.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(face).Properties.IsLeftButtonPressed) return;
            if (popup!.IsOpen) { popup.IsOpen = false; return; }
            shown = Month(selected ?? DateTime.Today);
            Render();
            popup.IsOpen = true;
        };

        popup = new Popup
        {
            PlacementTarget = face, Placement = PlacementMode.BottomEdgeAlignedLeft, IsLightDismissEnabled = true,
            Child = panelHost,
        };
        Children.Add(face);
        Children.Add(popup);
        UpdateLabel();
    }

    static DateTime Month(DateTime d) => new(d.Year, d.Month, 1);

    void UpdateLabel()
    {
        if (selected is { } s) { label.Text = $"{s.Day:00} {MonthsShort[s.Month - 1]} {s.Year}"; label.Foreground = Neo.B(Neo.Ink); }
        else { label.Text = "pilih tanggal"; label.Foreground = Neo.B("#777777"); }
    }

    void Pick(DateTime? d)
    {
        SelectedDate = d;
        popup.IsOpen = false;
    }

    static Border Mini(string text, string bg, Action click, double minWidth = 34)
    {
        var b = new Border
        {
            Background = Neo.B(bg), BorderBrush = Neo.B(Neo.Ink), BorderThickness = new Thickness(2.5), CornerRadius = new CornerRadius(6),
            BoxShadow = BoxShadows.Parse($"2 2 0 0 {Neo.Ink}"), MinWidth = minWidth, Padding = new Thickness(8, 3), Margin = new Thickness(0, 0, 3, 3),
            Cursor = new Cursor(StandardCursorType.Hand), Child = Neo.Text(text, 13, FontWeight.Black),
        };
        ((TextBlock)b.Child).HorizontalAlignment = HorizontalAlignment.Center;
        b.PointerEntered += (_, _) => b.Background = Neo.B(Neo.Yellow);
        b.PointerExited += (_, _) => b.Background = Neo.B(bg);
        b.PointerPressed += (_, e) => { if (e.GetCurrentPoint(b).Properties.IsLeftButtonPressed) click(); };
        return b;
    }

    void Render()
    {
        var root = new StackPanel { Spacing = 6, Width = 292 };

        var nav = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto,Auto") };
        void Add(Control c, int col) { Grid.SetColumn(c, col); nav.Children.Add(c); }
        Add(Mini("«", Neo.White, () => { shown = shown.AddYears(-1); Render(); }), 0);
        Add(Mini("‹", Neo.White, () => { shown = shown.AddMonths(-1); Render(); }), 1);
        var title = Neo.Text($"{Months[shown.Month - 1]} {shown.Year}".ToUpperInvariant(), 14, FontWeight.Black);
        title.HorizontalAlignment = HorizontalAlignment.Center; title.VerticalAlignment = VerticalAlignment.Center;
        Add(title, 2);
        Add(Mini("›", Neo.White, () => { shown = shown.AddMonths(1); Render(); }), 3);
        Add(Mini("»", Neo.White, () => { shown = shown.AddYears(1); Render(); }), 4);
        root.Children.Add(nav);

        var grid = new UniformGrid { Columns = 7 };
        foreach (var dn in Days)
        {
            var t = Neo.Text(dn, 11, FontWeight.Black, dn == "MIN" ? "#C0143C" : Neo.Ink);
            t.HorizontalAlignment = HorizontalAlignment.Center; t.Margin = new Thickness(0, 2, 0, 4);
            grid.Children.Add(t);
        }
        var start = shown.AddDays(-(((int)shown.DayOfWeek + 6) % 7));
        for (int i = 0; i < 42; i++)
        {
            var date = start.AddDays(i);
            bool inMonth = date.Month == shown.Month, isSel = selected == date, isToday = date == DateTime.Today;
            var bg = isSel ? Neo.Ink : Neo.White;
            var fg = isSel ? Neo.Yellow : !inMonth ? "#A8A8A8" : date.DayOfWeek == DayOfWeek.Sunday ? "#C0143C" : Neo.Ink;
            var tb = Neo.Text(date.Day.ToString(), 13, isSel || isToday ? FontWeight.Black : FontWeight.SemiBold, fg);
            tb.HorizontalAlignment = HorizontalAlignment.Center; tb.VerticalAlignment = VerticalAlignment.Center;
            var cell = new Border
            {
                Background = Neo.B(bg), Height = 34, Margin = new Thickness(1.5), CornerRadius = new CornerRadius(6),
                BorderBrush = Neo.B(isToday ? Neo.Pink : isSel ? Neo.Ink : "#E4E4E4"), BorderThickness = new Thickness(isToday ? 3 : 1.5),
                Cursor = new Cursor(StandardCursorType.Hand), Child = tb,
            };
            cell.PointerEntered += (_, _) => { if (!isSel) { cell.Background = Neo.B(Neo.Yellow); tb.Foreground = Neo.B(Neo.Ink); } };
            cell.PointerExited += (_, _) => { cell.Background = Neo.B(bg); tb.Foreground = Neo.B(fg); };
            cell.PointerPressed += (_, e) => { if (e.GetCurrentPoint(cell).Properties.IsLeftButtonPressed) Pick(date); };
            grid.Children.Add(cell);
        }
        root.Children.Add(grid);

        var foot = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(0, 4, 0, 0) };
        foot.Children.Add(Mini("HARI INI", Neo.Green, () => Pick(DateTime.Today), 70));
        foot.Children.Add(Mini("HAPUS", Neo.Pink, () => Pick(null), 60));
        foot.Children.Add(Mini("TUTUP", Neo.White, () => popup.IsOpen = false, 60));
        root.Children.Add(foot);

        panelHost.Child = new Border
        {
            Background = Neo.B(Neo.Cream), BorderBrush = Neo.B(Neo.Ink), BorderThickness = new Thickness(3), CornerRadius = new CornerRadius(12),
            BoxShadow = BoxShadows.Parse($"6 6 0 0 {Neo.Ink}"), Padding = new Thickness(12), Margin = new Thickness(0, 6, 8, 8), Child = root,
        };
    }
}
