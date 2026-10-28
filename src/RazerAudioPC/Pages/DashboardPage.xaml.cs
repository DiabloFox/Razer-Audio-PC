using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using RazerAudioPC.Bluetooth;
using RazerAudioPC.Device;

namespace RazerAudioPC.Pages;

public partial class DashboardPage : UserControl, IPage
{
    private readonly MainWindow _window;
    private HammerheadDevice Device => _window.Device;

    public string? Title => null;

    public FrameworkElement HeaderRight { get; }

    public DashboardPage(MainWindow window)
    {
        _window = window;
        InitializeComponent();

        var settings = new Button { Style = (Style)FindResource("IconButton"), ToolTip = "Settings" };
        settings.Content = new TextBlock { Style = (Style)FindResource("Icon"), Text = "", FontSize = 18 };
        settings.Click += (_, _) => _window.Navigate(new SettingsPage(_window));
        HeaderRight = settings;
    }

    private void Retry_Click(object sender, RoutedEventArgs e) => Device.Reconnect();

    private void Equalizer_Click(object sender, RoutedEventArgs e) => _window.Navigate(new EqualizerPage(_window));
    private void Anc_Click(object sender, RoutedEventArgs e) => _window.Navigate(new AncPage(_window));
    private void Remap_Click(object sender, RoutedEventArgs e) => _window.Navigate(new RemapPage(_window));
    private void Chroma_Click(object sender, RoutedEventArgs e) => _window.Navigate(new ChromaPage(_window));

    private void QuickConnect_Click(object sender, RoutedEventArgs e)
    {
        if (Device.GameMode)
        {
            _window.ShowToast("Quick Connect isn't available while Game Mode is on.");
            return;
        }
        _window.Navigate(new QuickConnectPage(_window));
    }

    // Toggle switches revert to the device state through the OneWay binding if a command fails.
    private static bool Checked(object sender) => ((ToggleButton)sender).IsChecked == true;

    private async void ChromaToggle_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        bool on = Checked(sender);
        await Device.SetChromaAsync(on ? ChromaEffect.Restore : ChromaEffect.Off, Device.ChromaColor);
        Sync(sender, Device.ChromaOn);
    }

    private async void GameMode_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        await Device.SetGameModeAsync(Checked(sender));
        Sync(sender, Device.GameMode);
    }

    private async void Dnd_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        await Device.SetDoNotDisturbAsync(Checked(sender));
        Sync(sender, Device.DoNotDisturb);
    }

    private async void AutoPause_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        await Device.SetAutoPauseAsync(Checked(sender));
        Sync(sender, Device.AutoPause);
    }

    private async void LowPower_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (Checked(sender) && !(Device.WornLeft && Device.WornRight))
        {
            _window.ShowToast("Put on both earbuds to turn on Low Power Mode.");
            Sync(sender, false);
            return;
        }
        await Device.SetLowPowerAsync(Checked(sender));
        Sync(sender, Device.LowPower);
    }

    // SetCurrentValue keeps the OneWay binding alive so later device changes still show.
    private static void Sync(object sender, bool value) =>
        ((ToggleButton)sender).SetCurrentValue(ToggleButton.IsCheckedProperty, value);

    // Clicking anywhere on a toggle card flips its switch.
    private void GameModeCard_Click(object sender, RoutedEventArgs e) => Flip(GameModeToggle, GameMode_Click);
    private void DndCard_Click(object sender, RoutedEventArgs e) => Flip(DndToggle, Dnd_Click);
    private void AutoPauseCard_Click(object sender, RoutedEventArgs e) => Flip(AutoPauseToggle, AutoPause_Click);
    private void LowPowerCard_Click(object sender, RoutedEventArgs e) => Flip(LowPowerToggle, LowPower_Click);

    private static void Flip(ToggleButton toggle, RoutedEventHandler handler)
    {
        toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, toggle.IsChecked != true);
        handler(toggle, new RoutedEventArgs());
    }
}
