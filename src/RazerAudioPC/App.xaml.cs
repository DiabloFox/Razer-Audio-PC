using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using RazerAudioPC.Bluetooth;
using RazerAudioPC.Device;
using Forms = System.Windows.Forms;

namespace RazerAudioPC;

public partial class App : Application
{
    private static readonly StringBuilder PendingLog = new();
    private static Mutex? _instance;

    private HammerheadDevice? _device;
    private MainWindow? _window;
    private Forms.NotifyIcon? _tray;
    private bool _lowBatteryWarned;

    public static AppSettings Settings { get; private set; } = new();
    public static bool IsExiting { get; private set; }
    public static string LogPath => Path.Combine(AppSettings.Folder, "log.txt");

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Contains("--probe"))
        {
            await Probe.RunAsync();
            Shutdown();
            return;
        }

        _instance = new Mutex(true, "RazerAudioPC.SingleInstance", out bool first);
        if (!first)
        {
            MessageBox.Show("Razer Audio is already running (check the system tray).", "Razer Audio");
            Shutdown();
            return;
        }

        Settings = AppSettings.Load();
        StartLog();

        _device = new HammerheadDevice();
        _device.PropertyChanged += OnDeviceChanged;
        _window = new MainWindow(_device) { CloseToTray = Settings.CloseToTray };
        _window.Closed += (_, _) => ExitApp();
        CreateTray();

        if (!e.Args.Contains("--minimized")) _window.Show();

        if (e.Args.Contains("--demo"))
        {
            _device.LoadDemoState();
        }

        int shot = Array.IndexOf(e.Args, "--screenshot");
        if (shot >= 0 && shot + 1 < e.Args.Length)
        {
            // Dev aid: render every page to PNGs in the given folder, then exit.
            await Screenshots.CaptureAllAsync(_window, e.Args[shot + 1]);
            ExitApp();
            return;
        }
        if (!e.Args.Contains("--demo")) _device.Start();
    }

    // ---------------------------------------------------------------- log

    private static void StartLog()
    {
        Directory.CreateDirectory(AppSettings.Folder);
        File.WriteAllText(LogPath, $"Razer Audio PC log — {DateTime.Now}\r\n");
        BleLog.Sink += line =>
        {
            lock (PendingLog) PendingLog.AppendLine($"{DateTime.Now:HH:mm:ss.fff} {line}");
            if (PendingLog.Length > 4096) FlushLog();
        };
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        timer.Tick += (_, _) => FlushLog();
        timer.Start();
    }

    public static void FlushLog()
    {
        string text;
        lock (PendingLog)
        {
            if (PendingLog.Length == 0) return;
            text = PendingLog.ToString();
            PendingLog.Clear();
        }
        try { File.AppendAllText(LogPath, text); } catch { /* log is best-effort */ }
    }

    // ---------------------------------------------------------------- tray

    private void CreateTray()
    {
        var iconStream = GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"))!.Stream;
        _tray = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(iconStream),
            Text = "Razer Hammerhead Pro HyperSpeed",
            Visible = true,
            ContextMenuStrip = new Forms.ContextMenuStrip(),
        };
        _tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) ShowWindow(); };
        _tray.ContextMenuStrip.Opening += (_, _) => BuildTrayMenu();
    }

    private void BuildTrayMenu()
    {
        var menu = _tray!.ContextMenuStrip!;
        var d = _device!;
        menu.Items.Clear();

        string header = d.IsConnected
            ? $"L {Battery(d.BatteryLeft)}   R {Battery(d.BatteryRight)}"
            : "Not connected";
        menu.Items.Add(new Forms.ToolStripMenuItem(header) { Enabled = false });
        menu.Items.Add(new Forms.ToolStripSeparator());

        if (d.IsConnected)
        {
            var anc = new Forms.ToolStripMenuItem("Noise Cancellation");
            anc.DropDownItems.Add(Item("Off", d.Anc == 0, () => d.SetAncAsync(0)));
            anc.DropDownItems.Add(Item("On", d.Anc is > 0 and <= 10, () => d.SetAncAsync((byte)d.AncLevel)));
            anc.DropDownItems.Add(Item("Ambient", d.Anc == 0xFF, () => d.SetAncAsync(0xFF)));
            menu.Items.Add(anc);

            var eq = new Forms.ToolStripMenuItem("Equalizer");
            foreach (var p in new[] { EqPreset.DefaultThx, EqPreset.Amplified, EqPreset.Vocal, EqPreset.EnhancedBass, EqPreset.EnhancedClarity, EqPreset.Custom })
            {
                var preset = p;
                eq.DropDownItems.Add(Item(Presets.Name(p), d.EqPreset == p, () => d.SetEqPresetAsync(preset)));
            }
            menu.Items.Add(eq);

            menu.Items.Add(Item("Game Mode", d.GameMode, () => d.SetGameModeAsync(!d.GameMode)));
            menu.Items.Add(Item("Chroma Lighting", d.ChromaOn,
                () => d.SetChromaAsync(d.ChromaOn ? ChromaEffect.Off : ChromaEffect.Restore, d.ChromaColor)));
            menu.Items.Add(new Forms.ToolStripSeparator());
        }

        menu.Items.Add("Open Razer Audio", null, (_, _) => ShowWindow());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());
    }

    private static Forms.ToolStripMenuItem Item(string text, bool check, Func<Task> action) =>
        new(text, null, async (_, _) => await action()) { Checked = check };

    private static string Battery(int? v) => v is int b ? $"{b}%" : "--";

    private void OnDeviceChanged(object? sender, PropertyChangedEventArgs e)
    {
        var d = _device!;
        if (_tray == null) return;
        if (e.PropertyName is nameof(HammerheadDevice.BatteryLeft) or nameof(HammerheadDevice.BatteryRight) or nameof(HammerheadDevice.State))
        {
            string tip = d.IsConnected
                ? $"Hammerhead Pro HyperSpeed\nL {Battery(d.BatteryLeft)}  ·  R {Battery(d.BatteryRight)}"
                : "Hammerhead Pro HyperSpeed\nNot connected";
            _tray.Text = tip.Length > 63 ? tip[..63] : tip;

            int? low = new[] { d.BatteryLeft, d.BatteryRight }.Where(b => b.HasValue).Min();
            if (low is <= 15 && !_lowBatteryWarned && Settings.BatteryNotifications)
            {
                _lowBatteryWarned = true;
                _tray.ShowBalloonTip(5000, "Earbuds battery low", $"Battery at {low}%. Put your earbuds in the case to charge.", Forms.ToolTipIcon.Warning);
            }
            else if (low is > 20) _lowBatteryWarned = false;
        }
    }

    private void ShowWindow()
    {
        if (_window == null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void ExitApp()
    {
        if (IsExiting) return;
        IsExiting = true;
        FlushLog();
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        _device?.Dispose();
        _window?.Close();
        Shutdown();
    }
}
