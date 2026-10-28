using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RazerAudioPC.Pages;

namespace RazerAudioPC;

/// <summary>`--screenshot &lt;folder&gt;`: renders each page of the main window to PNG (development aid).</summary>
public static class Screenshots
{
    public static async Task CaptureAllAsync(MainWindow window, string folder)
    {
        Directory.CreateDirectory(folder);
        var pages = new (string Name, Func<FrameworkElement> Create)[]
        {
            ("dashboard", () => new DashboardPage(window)),
            ("equalizer", () => new EqualizerPage(window)),
            ("anc", () => new AncPage(window)),
            ("remap", () => new RemapPage(window)),
            ("chroma", () => new ChromaPage(window)),
            ("quickconnect", () => new QuickConnectPage(window)),
            ("settings", () => new SettingsPage(window)),
        };

        foreach (var (name, create) in pages)
        {
            window.Navigate(create());
            await Settle();
            Save(window, Path.Combine(folder, $"{name}.png"));
        }

        // Remap picker sheet
        window.Navigate(new RemapPage(window));
        await Settle();
        if (window.PageHost.Content is RemapPage remap) remap.OpenPickerForScreenshot();
        await Settle();
        Save(window, Path.Combine(folder, "remap_sheet.png"));
    }

    private static async Task Settle()
    {
        await Task.Delay(450);
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static void Save(Window window, string path)
    {
        var root = (FrameworkElement)window.Content;
        var dpi = VisualTreeHelper.GetDpi(root);
        var bmp = new RenderTargetBitmap(
            (int)(root.ActualWidth * dpi.DpiScaleX), (int)(root.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bmp.Render(root);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
