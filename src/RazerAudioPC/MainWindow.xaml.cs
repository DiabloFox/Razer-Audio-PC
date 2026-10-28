using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using RazerAudioPC.Device;
using RazerAudioPC.Pages;

namespace RazerAudioPC;

/// <summary>A page shown in the main window. Title null = dashboard header (logo).</summary>
public interface IPage
{
    string? Title { get; }
    FrameworkElement? HeaderRight => null;
}

public partial class MainWindow : Window
{
    private readonly Stack<FrameworkElement> _history = new();

    public HammerheadDevice Device { get; }

    /// <summary>When false the close button exits; when true it hides to the tray.</summary>
    public bool CloseToTray { get; set; } = true;

    public MainWindow(HammerheadDevice device)
    {
        Device = device;
        InitializeComponent();
        Device.Error += ShowToast;
        Navigate(new DashboardPage(this));
    }

    // ---------------------------------------------------------------- navigation

    public void Navigate(FrameworkElement page)
    {
        if (PageHost.Content is FrameworkElement current) _history.Push(current);
        Show(page);
    }

    public void GoBack()
    {
        if (_history.Count == 0) return;
        Show(_history.Pop());
    }

    private void Show(FrameworkElement page)
    {
        page.DataContext ??= Device;
        PageHost.Content = page;
        var p = page as IPage;
        bool isHome = p?.Title == null;
        Logo.Visibility = isHome ? Visibility.Visible : Visibility.Collapsed;
        PageTitle.Visibility = isHome ? Visibility.Collapsed : Visibility.Visible;
        PageTitle.Text = p?.Title ?? "";
        BackButton.Visibility = _history.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        HeaderRight.Content = p?.HeaderRight;

        page.Opacity = 0;
        page.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)));
    }

    private void Back_Click(object sender, RoutedEventArgs e) => GoBack();

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseDown(e);
        if (e.ChangedButton == MouseButton.XButton1 && SheetOverlay.Visibility != Visibility.Visible) GoBack();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == Key.Escape)
        {
            if (SheetOverlay.Visibility == Visibility.Visible) CloseSheet();
            else GoBack();
            e.Handled = true;
        }
    }

    // ---------------------------------------------------------------- bottom sheet

    public void OpenSheet(FrameworkElement content)
    {
        SheetContent.Content = content;
        SheetOverlay.Visibility = Visibility.Visible;
        SheetOffset.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
            new DoubleAnimation(400, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    public void CloseSheet()
    {
        SheetOverlay.Visibility = Visibility.Collapsed;
        SheetContent.Content = null;
    }

    private void SheetBackdrop_Click(object sender, MouseButtonEventArgs e) => CloseSheet();
    private void SheetClose_Click(object sender, RoutedEventArgs e) => CloseSheet();

    // ---------------------------------------------------------------- toast

    public void ShowToast(string message)
    {
        ToastText.Text = message;
        var anim = new DoubleAnimationUsingKeyFrames();
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(150))));
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(3000))));
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(3400))));
        Toast.BeginAnimation(OpacityProperty, anim);
    }

    // ---------------------------------------------------------------- window chrome

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        if (CloseToTray && !App.IsExiting)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
    }
}
