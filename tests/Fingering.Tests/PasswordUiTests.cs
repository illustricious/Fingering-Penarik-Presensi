using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Fingering;
using Fingering.Core;
using Fingering.Ui;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(Fingering.Tests.PasswordTestAppBuilder))]

namespace Fingering.Tests;

public class PasswordTestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().WithInterFont().UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public class PasswordUiTests
{
    [AvaloniaFact]
    public void PasswordTab_MasksRevealsRemasks_FiltersAndPaginatesActualControls()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fing-password-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        typeof(App).Assembly.GetType("Fingering.Program")!.GetProperty("DataDir")!.SetValue(null, dir);
        App.Open(Vault.NewKey(), "ui-test", "ui-fixture-password");
        var device = App.Store.SaveDevice(new Device { Host = "127.0.0.1", Name = "UI simulator", Enabled = false });
        byte[] Record(string pin, byte[] bytes)
        {
            var raw = new byte[84]; Encoding.ASCII.GetBytes(pin).CopyTo(raw, 0); raw[32] = 10; bytes.CopyTo(raw, 52); return raw;
        }
        var reads = new List<PasswordRead>
        {
            new("1", "User Kandidat", 0, true, Record("1", "001234"u8.ToArray())),
            new("2", "User Gagal", 0, true, null, "Mesin menolak"),
            new("3", "User Format Biner", 0, true, Record("3", [0xD2, 0x04])),
            new("4", "User Data Kosong", 0, true, Record("4", [])),
        };
        reads.AddRange(Enumerable.Range(5, 51).Select(i => new PasswordRead(i.ToString(), $"User {i:00}", 0, false, null)));
        App.Store.SavePasswordSnapshot(device, reads);
        var window = new MainWindow("ui-test");
        try
        {
            window.Show(); window.UpdateLayout();
            void Click(string label)
            {
                var button = window.GetVisualDescendants().OfType<Button>().First(b => b.Content?.ToString() == label && b.IsEnabled);
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout();
            }
            string[] Values() => window.GetVisualDescendants().OfType<SelectableTextBlock>().Select(t => t.Text ?? "").ToArray();
            string[] Labels() => window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToArray();
            Click("PASSWORD USER");
            Assert.Contains("User Kandidat", Labels()); Assert.Contains("Gagal dibaca", Labels());
            Assert.Contains("Format belum dikenali", Labels()); Assert.Contains("Data password kosong", Labels());
            Assert.DoesNotContain("001234", Values()); Assert.Contains("••••••••", Values());
            var screenshots = Environment.GetEnvironmentVariable("FINGERING_TEST_SCREENSHOTS");
            if (screenshots != null)
            {
                Directory.CreateDirectory(screenshots);
                using var frame = window.CaptureRenderedFrame();
                frame?.Save(Path.Combine(screenshots, "password-user-table.png"));
            }
            Click("LIHAT"); Assert.Contains("001234", Values());
            Click("MESIN"); Click("PASSWORD USER");
            Assert.DoesNotContain("001234", Values());
            Click("BERIKUTNYA →");
            Assert.Contains("User 55", Labels()); Assert.DoesNotContain("User Kandidat", Labels());
            var search = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Watermark?.ToString() == "Cari PIN atau nama");
            search.Text = "User Gagal"; Click("CARI");
            Assert.Contains("User Gagal", Labels()); Assert.DoesNotContain("User 55", Labels());
            Assert.Contains("Gagal dibaca", Labels());
        }
        finally
        {
            window.Close(); App.Store.Dispose(); App.Hub.Dumps.Dispose();
            Directory.Delete(dir, true);
        }
    }
}
